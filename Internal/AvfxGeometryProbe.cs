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
    private const string CreateVertexWrapperSignature =
        "48 89 74 24 ?? 57 48 83 EC 30 48 8B 0D ?? ?? ?? ?? 41 0F B6 C0 BE 01 00 00 00 84 C0 41 B8 04 08 00 00";
    private const string CreateIndexWrapperSignature =
        "48 89 74 24 ?? 57 48 83 EC 30 48 8B 0D ?? ?? ?? ?? 45 84 C0 BE 01 00 00 00 C7 44 24 ?? 0A 00 00 00";
    private const string InitializeVertexBufferSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 50 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 44 8B 49";
    private const string InitializeIndexBufferSignature = "40 53 48 83 EC 20 F7 41 40 00 08 00 00 48 8B D9";
    private const int ExpectedDrawLayer = 2;

    private readonly object sync = new();
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly StaticVfxRunDelegate run;
    private readonly Hook<StaticVfxRemoveDelegate> removeHook;
    private readonly Hook<DepthProducerDelegate> depthProducerHook;
    private readonly Hook<ModelBuilderDelegate> modelBuilderHook;
    private readonly CreateBufferWrapperDelegate createVertexWrapper;
    private readonly CreateBufferWrapperDelegate createIndexWrapper;
    private readonly InitializeBufferDelegate initializeVertexBuffer;
    private readonly InitializeBufferDelegate initializeIndexBuffer;
    private Hook<DocumentRenderDelegate>? documentRenderHook;
    private nint apricotCore;
    private nint vfxAddress;
    private nint documentAddress;
    private Vector3 transformOffset;
    private Vector3 originalTranslation;
    private int modelBuildCount;
    private int ownedModelCreateCount;
    private int ownedModelReleaseCount;
    private string status = "Ready.";
    private OwnedModelRecord* ownedModel;
    private bool hasOriginalTranslation;
    private bool useOwnedModel = true;
    private bool releaseOwnedModelPending;
    private bool disposed;

    [ThreadStatic]
    private static nint renderingDocument;

    internal AvfxGeometryProbe(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner)
    {
        this.gameInteropProvider = gameInteropProvider;
        var runAddress = sigScanner.ScanText(RunCallSignature);
        run = Marshal.GetDelegateForFunctionPointer<StaticVfxRunDelegate>(runAddress);
        createVertexWrapper = Marshal.GetDelegateForFunctionPointer<CreateBufferWrapperDelegate>(
            sigScanner.ScanText(CreateVertexWrapperSignature)
        );
        createIndexWrapper = Marshal.GetDelegateForFunctionPointer<CreateBufferWrapperDelegate>(
            sigScanner.ScanText(CreateIndexWrapperSignature)
        );
        initializeVertexBuffer = Marshal.GetDelegateForFunctionPointer<InitializeBufferDelegate>(
            sigScanner.ScanText(InitializeVertexBufferSignature)
        );
        initializeIndexBuffer = Marshal.GetDelegateForFunctionPointer<InitializeBufferDelegate>(
            sigScanner.ScanText(InitializeIndexBufferSignature)
        );

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
                var details =
                    $"{status} OwnedMesh={(useOwnedModel ? "On" : "Off")} ModelCalls={modelBuildCount} OwnedModels={Volatile.Read(ref ownedModelCreateCount)}/{Volatile.Read(ref ownedModelReleaseCount)}.";
                return hasOriginalTranslation ? $"{details} OriginalTranslation={originalTranslation}." : details;
            }
        }
    }

    internal void SetOwnedMeshEnabled(bool enabled)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            useOwnedModel = enabled;
        }
    }

    internal void Start(string resourcePath, Vector3 position, Vector3 offset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (!IsFinite(position) || !IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(position), "Probe position and offset must be finite.");

        ReleasePendingOwnedModel();

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (vfxAddress != 0 || ownedModel != null)
                throw new InvalidOperationException("Stop the active AVFX geometry probe before starting another one.");
            transformOffset = offset;
            originalTranslation = default;
            modelBuildCount = 0;
            hasOriginalTranslation = false;
            documentAddress = 0;
            releaseOwnedModelPending = false;
            status = "Starting a normal AVFX host.";
        }

        var model = CreateOwnedModel();
        lock (sync)
            ownedModel = model;

        VfxObject* vfx = null;
        try
        {
            removeHook.Enable();
            depthProducerHook.Enable();
            modelBuilderHook.Enable();
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
            modelBuilderHook.Disable();
            depthProducerHook.Disable();
            removeHook.Disable();
            ReleaseOwnedModel(TakeOwnedModel());
            throw;
        }
    }

    internal void Update()
    {
        ReleasePendingOwnedModel();

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
        OwnedModelRecord* model;
        lock (sync)
        {
            if (disposed)
                return;
            activeVfx = vfxAddress;
            vfxAddress = 0;
            documentAddress = 0;
            releaseOwnedModelPending = false;
            model = ownedModel;
            ownedModel = null;
            status = "Stopped.";
        }

        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        if (activeVfx != 0)
            removeHook.Original((VfxObject*)activeVfx);
        ReleaseOwnedModel(model);
    }

    public void Dispose()
    {
        nint activeVfx;
        OwnedModelRecord* model;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            activeVfx = vfxAddress;
            vfxAddress = 0;
            documentAddress = 0;
            releaseOwnedModelPending = false;
            model = ownedModel;
            ownedModel = null;
        }

        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        if (activeVfx != 0)
            removeHook.Original((VfxObject*)activeVfx);
        ReleaseOwnedModel(model);
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
        lock (sync)
        {
            if (documentAddress == 0 || renderingDocument != documentAddress || descriptorAddress == 0)
                return modelBuilderHook.Original(rendererState, useProjection, descriptorAddress);

            var sourceDescriptor = (nint*)descriptorAddress;
            if (sourceDescriptor[2] == 0)
                return modelBuilderHook.Original(rendererState, useProjection, descriptorAddress);

            var descriptor = stackalloc nint[11];
            Buffer.MemoryCopy(sourceDescriptor, descriptor, 11 * sizeof(nint), 11 * sizeof(nint));
            var sourceTransform = (float*)sourceDescriptor[2];
            var transform = stackalloc float[12];
            Buffer.MemoryCopy(sourceTransform, transform, 12 * sizeof(float), 12 * sizeof(float));

            if (!hasOriginalTranslation)
            {
                originalTranslation = new Vector3(transform[9], transform[10], transform[11]);
                hasOriginalTranslation = true;
            }
            modelBuildCount++;

            transform[9] += transformOffset.X;
            transform[10] += transformOffset.Y;
            transform[11] += transformOffset.Z;
            if (useOwnedModel && ownedModel != null)
                descriptor[0] = (nint)ownedModel;
            descriptor[2] = (nint)transform;
            return modelBuilderHook.Original(rendererState, useProjection, (nint)descriptor);
        }
    }

    private nint RemoveDetour(VfxObject* vfx)
    {
        lock (sync)
        {
            if ((nint)vfx == vfxAddress)
            {
                vfxAddress = 0;
                documentAddress = 0;
                releaseOwnedModelPending = true;
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

    private OwnedModelRecord* CreateOwnedModel()
    {
        var model = (OwnedModelRecord*)NativeMemory.AllocZeroed((nuint)sizeof(OwnedModelRecord));
        Interlocked.Increment(ref ownedModelCreateCount);
        try
        {
            var vertices = stackalloc AvfxVertex[3] { new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(0f, 0.5f, 0f) };
            var indices = stackalloc ushort[3] { 0, 1, 2 };

            model->VertexWrapper = createVertexWrapper(0, (uint)(3 * sizeof(AvfxVertex)), 0);
            if (model->VertexWrapper == 0 || !InitializeWrapper(model->VertexWrapper, vertices, initializeVertexBuffer))
                throw new InvalidOperationException("The game rejected the owned AVFX vertex wrapper.");

            model->IndexWrapper = createIndexWrapper(0, 3 * sizeof(ushort), 0);
            if (model->IndexWrapper == 0 || !InitializeWrapper(model->IndexWrapper, indices, initializeIndexBuffer))
                throw new InvalidOperationException("The game rejected the owned AVFX index wrapper.");

            model->VertexCount = 3;
            model->IndexCount = 3;
            return model;
        }
        catch
        {
            ReleaseOwnedModel(model);
            throw;
        }
    }

    private static bool InitializeWrapper(nint wrapper, void* data, InitializeBufferDelegate initialize)
    {
        var resource = *(nint*)(wrapper + 0x10);
        return resource != 0 && initialize(resource, data) != 0;
    }

    private void ReleasePendingOwnedModel()
    {
        OwnedModelRecord* model = null;
        lock (sync)
        {
            if (releaseOwnedModelPending)
            {
                releaseOwnedModelPending = false;
                model = ownedModel;
                ownedModel = null;
            }
        }
        ReleaseOwnedModel(model);
    }

    private OwnedModelRecord* TakeOwnedModel()
    {
        lock (sync)
        {
            var model = ownedModel;
            ownedModel = null;
            releaseOwnedModelPending = false;
            return model;
        }
    }

    private void ReleaseOwnedModel(OwnedModelRecord* model)
    {
        if (model == null)
            return;

        ReleaseWrapper(ref model->VertexWrapper);
        ReleaseWrapper(ref model->IndexWrapper);
        NativeMemory.Free(model);
        Interlocked.Increment(ref ownedModelReleaseCount);
    }

    private static void ReleaseWrapper(ref nint wrapper)
    {
        var value = wrapper;
        wrapper = 0;
        if (value == 0)
            return;

        var release = (delegate* unmanaged<nint, uint>)(*(nint**)value)[1];
        release(value);
    }

    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);

    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    private delegate nint DepthProducerDelegate(nint core, int category, uint workerIndex, uint workerCount, nint producedCount);

    private delegate nint DocumentRenderDelegate(nint document);

    private delegate nint ModelBuilderDelegate(nint rendererState, byte useProjection, nint descriptor);

    private delegate nint CreateBufferWrapperDelegate(nint allocatorState, uint byteSize, byte dynamic);

    private delegate byte InitializeBufferDelegate(nint resource, void* data);

    [StructLayout(LayoutKind.Explicit, Size = 0x28)]
    private struct OwnedModelRecord
    {
        [FieldOffset(0x10)]
        internal nint VertexWrapper;

        [FieldOffset(0x18)]
        internal nint IndexWrapper;

        [FieldOffset(0x24)]
        internal ushort VertexCount;

        [FieldOffset(0x26)]
        internal ushort IndexCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct AvfxVertex
    {
        private const uint NormalPositiveZ = 0x7FFF8080;
        private const uint TangentPositiveX = 0x7F8080FF;

        private readonly Half positionX;
        private readonly Half positionY;
        private readonly Half positionZ;
        private readonly Half positionW;
        private readonly uint normal;
        private readonly uint tangent;
        private readonly uint color;
        private readonly Half uv1X;
        private readonly Half uv1Y;
        private readonly Half uv2X;
        private readonly Half uv2Y;
        private readonly Half uv3X;
        private readonly Half uv3Y;
        private readonly Half uv4X;
        private readonly Half uv4Y;

        internal AvfxVertex(float x, float y, float z)
        {
            positionX = (Half)x;
            positionY = (Half)y;
            positionZ = (Half)z;
            positionW = (Half)1f;
            normal = NormalPositiveZ;
            tangent = TangentPositiveX;
            color = uint.MaxValue;
            uv1X = uv1Y = uv2X = uv2Y = uv3X = uv3Y = uv4X = uv4Y = (Half)0.5f;
        }
    }
}
#endif
