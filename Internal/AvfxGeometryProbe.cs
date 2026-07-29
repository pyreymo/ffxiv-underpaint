#if DEBUG
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FfxivQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using FfxivVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace Underpaint.Internal;

internal sealed unsafe class AvfxGeometryProbe : IDisposable
{
    private const string PoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const string RunCallSignature = "E8 ?? ?? ?? ?? B0 02 EB 02";
    private const string RemoveSignature =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private const string DepthProducerSignature = "48 89 4C 24 ?? 53 41 56 48 81 EC ?? ?? ?? ?? 48 8B 99 ?? ?? ?? ?? 45 8B D8 4C 63 F2";
    private const string ModelBuilderSignature = "4C 8B DC 55 53 56 57 49 8D 6B A1 48 81 EC E8 00 00 00 49 8B 00";
    private const int ExpectedDrawLayer = 2;

    private readonly object sync = new();
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly StaticVfxRunDelegate run;
    private readonly Hook<StaticVfxRemoveDelegate> removeHook;
    private readonly Hook<DepthProducerDelegate> depthProducerHook;
    private readonly Hook<ModelBuilderDelegate> modelBuilderHook;
    private Hook<DocumentRenderDelegate>? documentRenderHook;
    private nint apricotCore;
    private nint vfxAddress;
    private nint documentAddress;
    private Vector3 transformOffset;
    private Vector3 originalTranslation;
    private int modelBuildCount;
    private string status = "Ready.";
    private bool hasOriginalTranslation;
    private bool disposed;

    [ThreadStatic]
    private static nint renderingDocument;

    internal AvfxGeometryProbe(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner)
    {
        this.gameInteropProvider = gameInteropProvider;
        var runAddress = sigScanner.ScanText(RunCallSignature);
        run = Marshal.GetDelegateForFunctionPointer<StaticVfxRunDelegate>(runAddress);

        Hook<StaticVfxRemoveDelegate>? remove = null;
        Hook<DepthProducerDelegate>? depthProducer = null;
        Hook<ModelBuilderDelegate>? modelBuilder = null;
        try
        {
            remove = gameInteropProvider.HookFromSignature<StaticVfxRemoveDelegate>(RemoveSignature, RemoveDetour);
            depthProducer = gameInteropProvider.HookFromSignature<DepthProducerDelegate>(DepthProducerSignature, DepthProducerDetour);
            modelBuilder = gameInteropProvider.HookFromSignature<ModelBuilderDelegate>(ModelBuilderSignature, ModelBuilderDetour);
        }
        catch
        {
            modelBuilder?.Dispose();
            depthProducer?.Dispose();
            remove?.Dispose();
            throw;
        }

        removeHook = remove;
        depthProducerHook = depthProducer;
        modelBuilderHook = modelBuilder;
    }

    internal string Status
    {
        get
        {
            lock (sync)
            {
                if (!hasOriginalTranslation)
                    return status;
                return $"{status} ModelCalls={modelBuildCount} OriginalTranslation={originalTranslation}.";
            }
        }
    }

    internal void Start(string resourcePath, Vector3 position, Vector3 offset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (!IsFinite(position) || !IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(position), "Probe position and offset must be finite.");

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (vfxAddress != 0)
                throw new InvalidOperationException("Stop the active AVFX geometry probe before starting another one.");
            transformOffset = offset;
            originalTranslation = default;
            modelBuildCount = 0;
            hasOriginalTranslation = false;
            documentAddress = 0;
            status = "Starting a normal AVFX host.";
        }

        removeHook.Enable();
        depthProducerHook.Enable();
        modelBuilderHook.Enable();

        VfxObject* vfx = null;
        try
        {
            vfx = VfxObject.Create(resourcePath, PoolName);
            if (vfx == null)
                throw new InvalidOperationException("VfxObject.Create returned null.");

            lock (sync)
                vfxAddress = (nint)vfx;
            run(vfx, 0f, uint.MaxValue);
            SetPosition(vfx, position);
        }
        catch
        {
            lock (sync)
                vfxAddress = 0;
            if (vfx != null)
                removeHook.Original(vfx);
            throw;
        }
    }

    internal void Update()
    {
        lock (sync)
        {
            if (disposed || vfxAddress == 0 || documentAddress != 0 || apricotCore == 0)
                return;

            var resourceInstance = ((VfxObject*)vfxAddress)->VfxResourceInstance;
            if (resourceInstance == null)
                return;

            var state = *(byte**)(apricotCore + 0x1498);
            var handle = *(ulong*)((byte*)resourceInstance + 0x60);
            var generation = (uint)handle;
            var slot = (uint)(handle >> 32);
            if (state == null || handle == 0 || slot >= 2048)
                return;

            var slotRecord = state + 0x2000 + slot * 0x88;
            if (
                *(nint*)(slotRecord + 0x48) != (nint)resourceInstance
                || *(uint*)(slotRecord + 0x60) != generation
                || *(uint*)(slotRecord + 0x64) != slot
            )
            {
                status = "Rejected: the game-owned Apricot slot identity did not match the VFX handle.";
                return;
            }

            var document = *(nint*)(slotRecord + 0x30);
            var resource = *(byte**)(slotRecord + 0x38);
            if (document == 0 || resource == null)
                return;

            var drawLayer = (*(uint*)(resource + 0x5C) >> 10) & 0x1F;
            if (drawLayer != ExpectedDrawLayer)
            {
                status = $"Rejected: DrawLayerType={drawLayer}, expected {ExpectedDrawLayer}.";
                return;
            }

            var renderTarget = *(nint*)(*(nint*)document + 0x128);
            if (renderTarget == 0)
                return;
            documentRenderHook ??= gameInteropProvider.HookFromAddress<DocumentRenderDelegate>(renderTarget, DocumentRenderDetour);
            documentRenderHook.Enable();
            documentAddress = document;
            status = $"Active: document=0x{document:X}, offset={transformOffset}.";
        }
    }

    internal void Stop()
    {
        nint activeVfx;
        lock (sync)
        {
            if (disposed)
                return;
            activeVfx = vfxAddress;
            vfxAddress = 0;
            documentAddress = 0;
            status = "Stopped.";
        }

        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        if (activeVfx != 0)
            removeHook.Original((VfxObject*)activeVfx);
    }

    public void Dispose()
    {
        nint activeVfx;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            activeVfx = vfxAddress;
            vfxAddress = 0;
            documentAddress = 0;
        }

        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        if (activeVfx != 0)
            removeHook.Original((VfxObject*)activeVfx);
        documentRenderHook?.Dispose();
        modelBuilderHook.Dispose();
        depthProducerHook.Dispose();
        removeHook.Dispose();
    }

    private nint DepthProducerDetour(nint core, int category, uint workerIndex, uint workerCount, nint producedCount)
    {
        var result = depthProducerHook.Original(core, category, workerIndex, workerCount, producedCount);
        if (category == ExpectedDrawLayer)
        {
            lock (sync)
                apricotCore = core;
        }
        return result;
    }

    private nint DocumentRenderDetour(nint document)
    {
        var previousDocument = renderingDocument;
        lock (sync)
        {
            if (document == documentAddress)
                renderingDocument = document;
        }

        try
        {
            return documentRenderHook!.Original(document);
        }
        finally
        {
            renderingDocument = previousDocument;
        }
    }

    private nint ModelBuilderDetour(nint rendererState, byte useProjection, nint descriptorAddress)
    {
        nint targetDocument;
        Vector3 offset;
        lock (sync)
        {
            targetDocument = documentAddress;
            offset = transformOffset;
        }
        if (targetDocument == 0 || renderingDocument != targetDocument || descriptorAddress == 0)
            return modelBuilderHook.Original(rendererState, useProjection, descriptorAddress);

        var sourceDescriptor = (nint*)descriptorAddress;
        if (sourceDescriptor[2] == 0)
            return modelBuilderHook.Original(rendererState, useProjection, descriptorAddress);

        var descriptor = stackalloc nint[11];
        Buffer.MemoryCopy(sourceDescriptor, descriptor, 11 * sizeof(nint), 11 * sizeof(nint));
        var sourceTransform = (float*)sourceDescriptor[2];
        var transform = stackalloc float[12];
        Buffer.MemoryCopy(sourceTransform, transform, 12 * sizeof(float), 12 * sizeof(float));

        lock (sync)
        {
            if (!hasOriginalTranslation)
            {
                originalTranslation = new Vector3(transform[9], transform[10], transform[11]);
                hasOriginalTranslation = true;
            }
            modelBuildCount++;
        }

        transform[9] += offset.X;
        transform[10] += offset.Y;
        transform[11] += offset.Z;
        descriptor[2] = (nint)transform;
        return modelBuilderHook.Original(rendererState, useProjection, (nint)descriptor);
    }

    private nint RemoveDetour(VfxObject* vfx)
    {
        lock (sync)
        {
            if ((nint)vfx == vfxAddress)
            {
                vfxAddress = 0;
                documentAddress = 0;
                status = "The game removed the tracked VFX host.";
            }
        }
        return removeHook.Original(vfx);
    }

    private static void SetPosition(VfxObject* vfx, Vector3 position)
    {
        vfx->Position = new FfxivVector3
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };
        vfx->Rotation = FfxivQuaternion.Identity;
        vfx->UpdateTransforms(true);
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);

    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    private delegate nint DepthProducerDelegate(nint core, int category, uint workerIndex, uint workerCount, nint producedCount);

    private delegate nint DocumentRenderDelegate(nint document);

    private delegate nint ModelBuilderDelegate(nint rendererState, byte useProjection, nint descriptor);
}
#endif
