#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FfxivQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using FfxivVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using FfxivVector4 = FFXIVClientStructs.FFXIV.Common.Math.Vector4;

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
    private const string LoadVertexBufferSourceSignature =
        "48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 8B 49 38 41 8B F8 45 85 C0 75 ?? 8B F9 2B FA 8D 04 3A 3B C8 72 ?? 8B 4B 3C F6 C1 03 74 ?? 41 F6 C1 01 75 ?? 8B 05 ?? ?? ?? ?? 48 83 7C C3 40 00 74 ?? F6 C1 11 74 ?? 48 8B 43 60";
    private const int ExpectedDrawLayer = 2;
    private const int MaxHostCount = 2;
    private const float HostPhaseStep = 2.1f;

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
    private readonly LoadBufferSourceDelegate loadVertexBufferSource;
    private readonly nint loadVertexBufferSourceAddress;
    private readonly nint[] vfxAddresses = new nint[MaxHostCount];
    private readonly nint[] documentAddresses = new nint[MaxHostCount];
    private readonly nint[] ownedModels = new nint[MaxHostCount];
    private readonly Vector3[] hostPositions = new Vector3[MaxHostCount];
    private readonly Vector3[] transformOffsets = new Vector3[MaxHostCount];
    private readonly Vector3[] originalTranslations = new Vector3[MaxHostCount];
    private readonly int[] modelBuildCounts = new int[MaxHostCount];
    private readonly int[] colorUpdateCounts = new int[MaxHostCount];
    private readonly int[] vertexWriteCounts = new int[MaxHostCount];
    private readonly int[] vertexWriteMissCounts = new int[MaxHostCount];
    private readonly bool[] hasOriginalTranslations = new bool[MaxHostCount];
    private readonly bool[] releaseOwnedModelPending = new bool[MaxHostCount];
    private Hook<DocumentRenderDelegate>? documentRenderHook;
    private nint apricotCore;
    private int ownedModelCreateCount;
    private int ownedModelReleaseCount;
    private int hostCount;
    private string status = "Ready.";
    private long animationStartTimestamp;
    private bool animateColorAndAlpha;
    private bool animateVertices;
    private bool alphaOrderingTest;
    private bool swapPositionsPending;
    private int positionSwapCount;
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
        loadVertexBufferSourceAddress = sigScanner.ScanText(LoadVertexBufferSourceSignature);
        loadVertexBufferSource = Marshal.GetDelegateForFunctionPointer<LoadBufferSourceDelegate>(loadVertexBufferSourceAddress);

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
                var details = new StringBuilder(
                    $"{status} OwnedModels={Volatile.Read(ref ownedModelCreateCount)}/{Volatile.Read(ref ownedModelReleaseCount)} "
                        + $"vertexSource=0x{loadVertexBufferSourceAddress:X}."
                );
                for (var index = 0; index < hostCount; index++)
                {
                    var model = (OwnedModelRecord*)ownedModels[index];
                    details.Append(
                        $"\n[{index}] Vfx=0x{vfxAddresses[index]:X} Document=0x{documentAddresses[index]:X} "
                            + $"Model=0x{ownedModels[index]:X} VertexWrapper=0x{(model == null ? 0 : model->VertexWrapper):X} "
                            + $"Position={hostPositions[index]} "
                            + $"ModelCalls={modelBuildCounts[index]} ColorUpdates={colorUpdateCounts[index]} "
                            + $"VertexWrites={vertexWriteCounts[index]} VertexMisses={vertexWriteMissCounts[index]}."
                    );
                    if (hasOriginalTranslations[index])
                        details.Append($" OriginalTranslation={originalTranslations[index]}.");
                }
                return details.ToString();
            }
        }
    }

    internal void Start(
        string resourcePath,
        Vector3 position,
        Vector3 offset,
        bool animateColor,
        bool animateVertexPositions,
        bool testAlphaOrdering,
        int instanceCount,
        Vector3 instanceSpacing
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (!IsFinite(position) || !IsFinite(offset) || !IsFinite(instanceSpacing))
            throw new ArgumentOutOfRangeException(nameof(position), "Probe position, offset, and spacing must be finite.");
        if (instanceCount is < 1 or > MaxHostCount)
            throw new ArgumentOutOfRangeException(nameof(instanceCount), $"Probe instance count must be between 1 and {MaxHostCount}.");
        if (testAlphaOrdering && instanceCount != 2)
            throw new ArgumentException("The alpha-ordering test requires exactly two instances.", nameof(instanceCount));
        if (testAlphaOrdering && (animateColor || animateVertexPositions))
            throw new ArgumentException("The alpha-ordering test cannot be combined with color or vertex animation.");

        ReleasePendingOwnedModels();

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (hostCount != 0)
                throw new InvalidOperationException("Stop the active AVFX geometry probe hosts before starting another run.");
            hostCount = instanceCount;
            animateColorAndAlpha = animateColor;
            animateVertices = animateVertexPositions;
            alphaOrderingTest = testAlphaOrdering;
            swapPositionsPending = false;
            positionSwapCount = 0;
            animationStartTimestamp = Stopwatch.GetTimestamp();
            for (var index = 0; index < hostCount; index++)
            {
                transformOffsets[index] = offset;
                originalTranslations[index] = default;
                modelBuildCounts[index] = 0;
                colorUpdateCounts[index] = 0;
                vertexWriteCounts[index] = 0;
                vertexWriteMissCounts[index] = 0;
                hasOriginalTranslations[index] = false;
                releaseOwnedModelPending[index] = false;
            }
            status = $"Starting {hostCount} normal AVFX host(s).";
        }

        try
        {
            removeHook.Enable();
            depthProducerHook.Enable();
            modelBuilderHook.Enable();
            for (var index = 0; index < instanceCount; index++)
            {
                var model = CreateOwnedModel(animateVertexPositions);
                lock (sync)
                    ownedModels[index] = (nint)model;

                var vfx = VfxObject.Create(resourcePath, PoolName);
                if (vfx == null)
                    throw new InvalidOperationException($"VfxObject.Create returned null for host {index}.");

                lock (sync)
                    vfxAddresses[index] = (nint)vfx;
                run(vfx, 0f, uint.MaxValue);
                hostPositions[index] = position + instanceSpacing * index;
                SetPosition(vfx, hostPositions[index]);
                UpdateColor(index, vfx, index * HostPhaseStep);
            }
        }
        catch
        {
            Stop();
            throw;
        }
    }

    internal void Update()
    {
        ReleasePendingOwnedModels();

        lock (sync)
        {
            if (disposed || hostCount == 0)
                return;

            if (swapPositionsPending && hostCount == 2 && vfxAddresses[0] != 0 && vfxAddresses[1] != 0)
            {
                (hostPositions[0], hostPositions[1]) = (hostPositions[1], hostPositions[0]);
                SetPosition((VfxObject*)vfxAddresses[0], hostPositions[0]);
                SetPosition((VfxObject*)vfxAddresses[1], hostPositions[1]);
                swapPositionsPending = false;
                positionSwapCount++;
            }

            var elapsed = (float)Stopwatch.GetElapsedTime(animationStartTimestamp).TotalSeconds;
            for (var index = 0; index < hostCount; index++)
            {
                var vfx = (VfxObject*)vfxAddresses[index];
                if (vfx == null)
                    continue;
                if (animateColorAndAlpha || alphaOrderingTest)
                    UpdateColor(index, vfx, elapsed + index * HostPhaseStep);
                if (documentAddresses[index] == 0 && apricotCore != 0)
                    TryAttachDocument(index, vfx);
            }

            var mode =
                alphaOrderingTest ? "alpha-order"
                : animateColorAndAlpha ? (animateVertices ? "color+vertices" : "color")
                : (animateVertices ? "vertices" : "static");
            status = $"Active: mode={mode}, hosts={hostCount}, positionSwaps={positionSwapCount}.";
        }
    }

    internal void SwapPositions()
    {
        lock (sync)
        {
            if (!disposed && alphaOrderingTest && hostCount == 2)
                swapPositionsPending = true;
        }
    }

    internal void Stop()
    {
        var activeVfx = new nint[MaxHostCount];
        var models = new nint[MaxHostCount];
        int count;
        lock (sync)
        {
            if (disposed)
                return;
            count = hostCount;
            for (var index = 0; index < count; index++)
            {
                activeVfx[index] = vfxAddresses[index];
                models[index] = ownedModels[index];
                vfxAddresses[index] = 0;
                documentAddresses[index] = 0;
                ownedModels[index] = 0;
                releaseOwnedModelPending[index] = false;
            }
            hostCount = 0;
            status = "Stopped.";
        }

        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        for (var index = 0; index < count; index++)
        {
            if (activeVfx[index] != 0)
                removeHook.Original((VfxObject*)activeVfx[index]);
            ReleaseOwnedModel((OwnedModelRecord*)models[index]);
        }
    }

    public void Dispose()
    {
        var activeVfx = new nint[MaxHostCount];
        var models = new nint[MaxHostCount];
        int count;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            count = hostCount;
            for (var index = 0; index < count; index++)
            {
                activeVfx[index] = vfxAddresses[index];
                models[index] = ownedModels[index];
                vfxAddresses[index] = 0;
                documentAddresses[index] = 0;
                ownedModels[index] = 0;
                releaseOwnedModelPending[index] = false;
            }
            hostCount = 0;
        }

        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        for (var index = 0; index < count; index++)
        {
            if (activeVfx[index] != 0)
                removeHook.Original((VfxObject*)activeVfx[index]);
            ReleaseOwnedModel((OwnedModelRecord*)models[index]);
        }
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
            for (var index = 0; index < hostCount; index++)
            {
                if (document == documentAddresses[index])
                {
                    renderingDocument = document;
                    break;
                }
            }
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
            var hostIndex = -1;
            for (var index = 0; index < hostCount; index++)
            {
                if (documentAddresses[index] != 0 && renderingDocument == documentAddresses[index])
                {
                    hostIndex = index;
                    break;
                }
            }
            if (hostIndex < 0 || descriptorAddress == 0)
                return modelBuilderHook.Original(rendererState, useProjection, descriptorAddress);

            var sourceDescriptor = (nint*)descriptorAddress;
            if (sourceDescriptor[2] == 0)
                return modelBuilderHook.Original(rendererState, useProjection, descriptorAddress);

            var descriptor = stackalloc nint[11];
            Buffer.MemoryCopy(sourceDescriptor, descriptor, 11 * sizeof(nint), 11 * sizeof(nint));
            var sourceTransform = (float*)sourceDescriptor[2];
            var transform = stackalloc float[12];
            Buffer.MemoryCopy(sourceTransform, transform, 12 * sizeof(float), 12 * sizeof(float));

            if (!hasOriginalTranslations[hostIndex])
            {
                originalTranslations[hostIndex] = new Vector3(transform[9], transform[10], transform[11]);
                hasOriginalTranslations[hostIndex] = true;
            }
            modelBuildCounts[hostIndex]++;

            var transformOffset = transformOffsets[hostIndex];
            transform[9] += transformOffset.X;
            transform[10] += transformOffset.Y;
            transform[11] += transformOffset.Z;
            var model = (OwnedModelRecord*)ownedModels[hostIndex];
            var ownedModelReady =
                model != null
                && (!animateVertices || TryWriteAnimatedVertices(hostIndex, model, GetElapsedSeconds() + hostIndex * HostPhaseStep));
            if (ownedModelReady)
                descriptor[0] = (nint)model;
            descriptor[2] = (nint)transform;
            return modelBuilderHook.Original(rendererState, useProjection, (nint)descriptor);
        }
    }

    private nint RemoveDetour(VfxObject* vfx)
    {
        lock (sync)
        {
            for (var index = 0; index < hostCount; index++)
            {
                if ((nint)vfx != vfxAddresses[index])
                    continue;
                vfxAddresses[index] = 0;
                documentAddresses[index] = 0;
                releaseOwnedModelPending[index] = true;
                status = $"The game removed tracked VFX host {index}.";
                break;
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

    private void TryAttachDocument(int index, VfxObject* vfx)
    {
        var resourceInstance = vfx->VfxResourceInstance;
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
            status = $"Rejected host {index}: the game-owned Apricot slot identity did not match the VFX handle.";
            return;
        }

        var document = *(nint*)(slotRecord + 0x30);
        var resource = *(byte**)(slotRecord + 0x38);
        if (document == 0 || resource == null)
            return;

        var drawLayer = (*(uint*)(resource + 0x5C) >> 10) & 0x1F;
        if (drawLayer != ExpectedDrawLayer)
        {
            status = $"Rejected host {index}: DrawLayerType={drawLayer}, expected {ExpectedDrawLayer}.";
            return;
        }

        var renderTarget = *(nint*)(*(nint*)document + 0x128);
        if (renderTarget == 0)
            return;
        documentRenderHook ??= gameInteropProvider.HookFromAddress<DocumentRenderDelegate>(renderTarget, DocumentRenderDetour);
        documentRenderHook.Enable();
        documentAddresses[index] = document;
    }

    private void UpdateColor(int index, VfxObject* vfx, float elapsedSeconds)
    {
        if (alphaOrderingTest)
        {
            vfx->Color =
                index == 0
                    ? new FfxivVector4
                    {
                        X = 1f,
                        Y = 0f,
                        Z = 0f,
                        W = 0.5f,
                    }
                    : new FfxivVector4
                    {
                        X = 0f,
                        Y = 0f,
                        Z = 1f,
                        W = 0.5f,
                    };
            colorUpdateCounts[index]++;
            return;
        }

        if (!animateColorAndAlpha)
        {
            vfx->Color = new FfxivVector4
            {
                X = 1f,
                Y = 1f,
                Z = 1f,
                W = 1f,
            };
            return;
        }

        vfx->Color = new FfxivVector4
        {
            X = Wave(elapsedSeconds),
            Y = Wave(elapsedSeconds + 2f * MathF.PI / 3f),
            Z = Wave(elapsedSeconds + 4f * MathF.PI / 3f),
            W = 0.15f + 0.75f * Wave(elapsedSeconds * 0.7f),
        };
        colorUpdateCounts[index]++;
    }

    private bool TryWriteAnimatedVertices(int index, OwnedModelRecord* model, float elapsedSeconds)
    {
        var wrapper = model->VertexWrapper;
        var resource = wrapper == 0 ? 0 : *(nint*)(wrapper + 0x10);
        if (resource == 0)
        {
            vertexWriteMissCounts[index]++;
            return false;
        }

        var vertices = (AvfxVertex*)loadVertexBufferSource(resource, 0, (uint)(3 * sizeof(AvfxVertex)), 2);
        if (vertices == null)
        {
            vertexWriteMissCounts[index]++;
            return false;
        }

        var horizontal = 0.2f * MathF.Sin(elapsedSeconds * 1.3f);
        var height = 0.5f + 0.25f * MathF.Sin(elapsedSeconds * 1.7f);
        vertices[0] = new AvfxVertex(-0.5f, -0.5f, 0f);
        vertices[1] = new AvfxVertex(0.5f, -0.5f, 0f);
        vertices[2] = new AvfxVertex(horizontal, height, 0f);
        vertexWriteCounts[index]++;
        return true;
    }

    private float GetElapsedSeconds() => (float)Stopwatch.GetElapsedTime(animationStartTimestamp).TotalSeconds;

    private static float Wave(float radians) => 0.5f + 0.5f * MathF.Sin(radians);

    private OwnedModelRecord* CreateOwnedModel(bool dynamicVertices)
    {
        var model = (OwnedModelRecord*)NativeMemory.AllocZeroed((nuint)sizeof(OwnedModelRecord));
        Interlocked.Increment(ref ownedModelCreateCount);
        try
        {
            var vertices = stackalloc AvfxVertex[3] { new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(0f, 0.5f, 0f) };
            var indices = stackalloc ushort[3] { 0, 1, 2 };

            model->VertexWrapper = createVertexWrapper(0, (uint)(3 * sizeof(AvfxVertex)), dynamicVertices ? (byte)1 : (byte)0);
            if (
                model->VertexWrapper == 0
                || (!dynamicVertices && !InitializeWrapper(model->VertexWrapper, vertices, initializeVertexBuffer))
            )
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

    private void ReleasePendingOwnedModels()
    {
        Span<nint> models = stackalloc nint[MaxHostCount];
        int count;
        lock (sync)
        {
            count = hostCount;
            for (var index = 0; index < count; index++)
            {
                if (!releaseOwnedModelPending[index])
                    continue;
                releaseOwnedModelPending[index] = false;
                models[index] = ownedModels[index];
                ownedModels[index] = 0;
            }
        }
        for (var index = 0; index < count; index++)
        {
            if (models[index] != 0)
                ReleaseOwnedModel((OwnedModelRecord*)models[index]);
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

    private delegate nint LoadBufferSourceDelegate(nint resource, int byteOffset, uint byteSize, byte flags);

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
