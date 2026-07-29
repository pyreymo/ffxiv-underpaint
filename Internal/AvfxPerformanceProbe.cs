#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FfxivQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using FfxivVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using FfxivVector4 = FFXIVClientStructs.FFXIV.Common.Math.Vector4;

namespace Underpaint.Internal;

internal sealed unsafe class AvfxPerformanceProbe : IDisposable
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
    private const int MeshColumns = 20;
    private const int MeshRows = 25;
    private const int MeshFaceCount = MeshColumns * MeshRows * 2;
    private const int WarmupFrames = 180;
    private const int SampleFrames = 600;
    private const int CooldownFrames = 180;

    private readonly object sync = new();
    private readonly Process process = Process.GetCurrentProcess();
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private readonly StaticVfxRunDelegate run;
    private readonly Hook<StaticVfxRemoveDelegate> removeHook;
    private readonly Hook<DepthProducerDelegate> depthProducerHook;
    private readonly Hook<ModelBuilderDelegate> modelBuilderHook;
    private readonly CreateBufferWrapperDelegate createVertexWrapper;
    private readonly CreateBufferWrapperDelegate createIndexWrapper;
    private readonly InitializeBufferDelegate initializeVertexBuffer;
    private readonly InitializeBufferDelegate initializeIndexBuffer;
    private readonly double[] frameSamples = new double[SampleFrames];
    private readonly double[] cpuSamples = new double[SampleFrames];
    private Dictionary<nint, int> hostIndices = [];
    private HashSet<nint> attachedDocuments = [];
    private HashSet<nint> documentSnapshot = [];
    private nint[] hosts = [];
    private nint[] hostDocuments = [];
    private Hook<DocumentRenderDelegate>? documentRenderHook;
    private nint apricotCore;
    private nint ownedModel;
    private ProbePhase phase;
    private int warmupFrameCount;
    private int sampleFrameCount;
    private int cooldownFrameCount;
    private double warmupFrameMax;
    private double warmupCpuMax;
    private double previousCpuMilliseconds;
    private double createMilliseconds;
    private double removeMilliseconds;
    private double topologyCreateMilliseconds;
    private int benchmarkHostCount;
    private int modelBuildCalls;
    private int activeDocumentCount;
    private long privateBytesBefore;
    private long privateBytesActive;
    private long workingSetBefore;
    private long workingSetActive;
    private string status = "Ready.";
    private bool disposed;

    [ThreadStatic]
    private static bool renderingPerformanceDocument;

    internal AvfxPerformanceProbe(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;
        run = Marshal.GetDelegateForFunctionPointer<StaticVfxRunDelegate>(sigScanner.ScanText(RunCallSignature));
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
        removeHook = gameInteropProvider.HookFromSignature<StaticVfxRemoveDelegate>(RemoveSignature, RemoveDetour);
        depthProducerHook = gameInteropProvider.HookFromSignature<DepthProducerDelegate>(DepthProducerSignature, DepthProducerDetour);
        modelBuilderHook = gameInteropProvider.HookFromSignature<ModelBuilderDelegate>(ModelBuilderSignature, ModelBuilderDetour);
    }

    internal string Status
    {
        get
        {
            lock (sync)
                return status;
        }
    }

    internal void Start(string resourcePath, Vector3 sortingCenter, int hostCount, float spacing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (hostCount is not (0 or 1 or 4 or 16 or 64 or 256))
            throw new ArgumentOutOfRangeException(nameof(hostCount), "Host count must be 0, 1, 4, 16, 64, or 256.");
        if (!IsFinite(sortingCenter) || !float.IsFinite(spacing) || spacing < 0)
            throw new ArgumentOutOfRangeException(nameof(sortingCenter), "Sorting center and spacing must be finite.");

        if (hostCount != 0)
            EnsureOwnedModel();

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (phase is not ProbePhase.Idle and not ProbePhase.Complete)
                throw new InvalidOperationException("Stop the active AVFX performance test before starting another run.");
            if (hosts.Length != 0)
                throw new InvalidOperationException("Stop the completed AVFX performance test before starting another run.");

            ResetMeasurements();
            benchmarkHostCount = hostCount;
            SnapshotMemory(out privateBytesBefore, out workingSetBefore);
            hosts = new nint[hostCount];
            hostDocuments = new nint[hostCount];
            hostIndices = new Dictionary<nint, int>(hostCount);
            attachedDocuments = [];
            Volatile.Write(ref documentSnapshot, []);
            apricotCore = 0;
            modelBuildCalls = 0;
            removeHook.Enable();
            depthProducerHook.Enable();
            modelBuilderHook.Enable();
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var side = hostCount == 0 ? 0 : (int)Math.Ceiling(Math.Sqrt(hostCount));
            var center = (side - 1) * 0.5f;
            for (var index = 0; index < hostCount; index++)
            {
                var vfx = VfxObject.Create(resourcePath, PoolName);
                if (vfx == null)
                    throw new InvalidOperationException($"VfxObject.Create returned null for host {index}.");

                lock (sync)
                {
                    hosts[index] = (nint)vfx;
                    hostIndices.Add((nint)vfx, index);
                }
                run(vfx, 0f, uint.MaxValue);
                var row = index / side;
                var column = index % side;
                SetHost(
                    vfx,
                    sortingCenter + new Vector3((column - center) * spacing, 0f, (row - center) * spacing)
                );
            }

            stopwatch.Stop();
            lock (sync)
            {
                createMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                previousCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
                phase = ProbePhase.Warmup;
                status =
                    $"Warmup: hosts={hostCount}, faces/host={MeshFaceCount}, frame=0/{WarmupFrames}, "
                    + $"create={createMilliseconds:F2} ms.";
            }
            log.Information(
                "[Underpaint] AVFX performance started. Hosts={HostCount} FacesPerHost={FacesPerHost} "
                    + "SortingCenter={SortingCenter} Spacing={Spacing} CreateMs={CreateMs:F2} TopologyCreateMs={TopologyCreateMs:F2}.",
                hostCount,
                MeshFaceCount,
                sortingCenter,
                spacing,
                createMilliseconds,
                topologyCreateMilliseconds
            );
        }
        catch
        {
            Stop();
            throw;
        }
    }

    internal void Update(TimeSpan frameDelta)
    {
        var frameMilliseconds = frameDelta.TotalMilliseconds;
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds < 0)
            return;

        bool finishSamples = false;
        lock (sync)
        {
            if (disposed || phase is ProbePhase.Idle or ProbePhase.Complete)
                return;

            TryAttachDocuments();

            var currentCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
            var cpuMilliseconds = Math.Max(0, currentCpuMilliseconds - previousCpuMilliseconds);
            previousCpuMilliseconds = currentCpuMilliseconds;

            switch (phase)
            {
                case ProbePhase.Warmup:
                    warmupFrameMax = Math.Max(warmupFrameMax, frameMilliseconds);
                    warmupCpuMax = Math.Max(warmupCpuMax, cpuMilliseconds);
                    warmupFrameCount++;
                    if (warmupFrameCount >= WarmupFrames)
                    {
                        SnapshotMemory(out privateBytesActive, out workingSetActive);
                        activeDocumentCount = attachedDocuments.Count;
                        Interlocked.Exchange(ref modelBuildCalls, 0);
                        phase = ProbePhase.Sampling;
                        previousCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
                    }
                    status = $"Warmup: hosts={hosts.Length}, frame={warmupFrameCount}/{WarmupFrames}, create={createMilliseconds:F2} ms.";
                    break;
                case ProbePhase.Sampling:
                    frameSamples[sampleFrameCount] = frameMilliseconds;
                    cpuSamples[sampleFrameCount] = cpuMilliseconds;
                    sampleFrameCount++;
                    status =
                        $"Sampling: hosts={hosts.Length}, documents={activeDocumentCount}, faces/host={MeshFaceCount}, "
                        + $"frame={sampleFrameCount}/{SampleFrames}.";
                    finishSamples = sampleFrameCount >= SampleFrames;
                    break;
                case ProbePhase.Cooldown:
                    cooldownFrameCount++;
                    if (cooldownFrameCount >= CooldownFrames)
                        Complete();
                    else
                        status = $"Cooldown: frame={cooldownFrameCount}/{CooldownFrames}, remove={removeMilliseconds:F2} ms.";
                    break;
            }
        }

        if (finishSamples)
            FinishSamples();
    }

    internal void Stop()
    {
        nint[] activeHosts;
        lock (sync)
        {
            if (hosts.Length == 0)
            {
                phase = ProbePhase.Idle;
                Volatile.Write(ref documentSnapshot, []);
                documentRenderHook?.Disable();
                modelBuilderHook.Disable();
                depthProducerHook.Disable();
                removeHook.Disable();
                return;
            }

            activeHosts = hosts;
            hosts = [];
            hostDocuments = [];
            hostIndices = [];
            attachedDocuments = [];
            Volatile.Write(ref documentSnapshot, []);
            phase = ProbePhase.Idle;
        }

        var stopwatch = Stopwatch.StartNew();
        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        foreach (var address in activeHosts)
        {
            if (address != 0)
                removeHook.Original((VfxObject*)address);
        }
        stopwatch.Stop();

        lock (sync)
        {
            removeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            status = $"Stopped: hosts={activeHosts.Length}, remove={removeMilliseconds:F2} ms.";
        }
        log.Information(
            "[Underpaint] AVFX performance stopped. Hosts={HostCount} RemoveMs={RemoveMs:F2}.",
            benchmarkHostCount,
            removeMilliseconds
        );
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
        }

        Stop();
        lock (sync)
            disposed = true;
        documentRenderHook?.Dispose();
        modelBuilderHook.Dispose();
        depthProducerHook.Dispose();
        removeHook.Dispose();
        ReleaseOwnedModel();
        process.Dispose();
    }

    private void FinishSamples()
    {
        nint[] activeHosts;
        lock (sync)
        {
            if (phase != ProbePhase.Sampling)
                return;
            activeHosts = hosts;
            hosts = [];
            hostDocuments = [];
            hostIndices = [];
            attachedDocuments = [];
            Volatile.Write(ref documentSnapshot, []);
            phase = ProbePhase.Cooldown;
        }

        var stopwatch = Stopwatch.StartNew();
        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        foreach (var address in activeHosts)
        {
            if (address != 0)
                removeHook.Original((VfxObject*)address);
        }
        stopwatch.Stop();

        lock (sync)
        {
            removeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            status = $"Cooldown: frame=0/{CooldownFrames}, remove={removeMilliseconds:F2} ms.";
        }
    }

    private void Complete()
    {
        SnapshotMemory(out var privateBytesAfter, out var workingSetAfter);
        var frame = Summarize(frameSamples);
        var cpu = Summarize(cpuSamples);
        var calls = Volatile.Read(ref modelBuildCalls);
        status =
            $"Complete: hosts={benchmarkHostCount}, documents={activeDocumentCount}, faces/host={MeshFaceCount}, "
            + $"modelCalls={calls} ({calls / (double)SampleFrames:F2}/frame), "
            + $"create={createMilliseconds:F2} ms, remove={removeMilliseconds:F2} ms, "
            + $"loadMax={warmupFrameMax:F2} ms/{warmupCpuMax:F2} CPU-ms.\n"
            + $"Frame ms: avg={frame.Average:F2}, p95={frame.P95:F2}, p99={frame.P99:F2}, max={frame.Max:F2}.\n"
            + $"Process CPU ms/frame: avg={cpu.Average:F2}, p95={cpu.P95:F2}, p99={cpu.P99:F2}, max={cpu.Max:F2}.\n"
            + $"Private bytes: active={FormatBytes(privateBytesActive - privateBytesBefore)}, "
            + $"after={FormatBytes(privateBytesAfter - privateBytesBefore)}; "
            + $"working set: active={FormatBytes(workingSetActive - workingSetBefore)}, "
            + $"after={FormatBytes(workingSetAfter - workingSetBefore)}.";
        phase = ProbePhase.Complete;
        log.Information("[Underpaint] AVFX performance result. {Result}", status.Replace('\n', ' '));
    }

    private nint RemoveDetour(VfxObject* vfx)
    {
        lock (sync)
        {
            if (hostIndices.Remove((nint)vfx, out var index))
            {
                var document = hostDocuments[index];
                if (document != 0)
                {
                    attachedDocuments.Remove(document);
                    Volatile.Write(ref documentSnapshot, new HashSet<nint>(attachedDocuments));
                    hostDocuments[index] = 0;
                }
                hosts[index] = 0;
            }
        }
        return removeHook.Original(vfx);
    }

    private nint DepthProducerDetour(nint core, int category, uint workerIndex, uint workerCount, nint producedCount)
    {
        var result = depthProducerHook.Original(core, category, workerIndex, workerCount, producedCount);
        if (category == ExpectedDrawLayer)
            Volatile.Write(ref apricotCore, core);
        return result;
    }

    private nint DocumentRenderDetour(nint document)
    {
        var previous = renderingPerformanceDocument;
        renderingPerformanceDocument = Volatile.Read(ref documentSnapshot).Contains(document);
        try
        {
            return documentRenderHook!.Original(document);
        }
        finally
        {
            renderingPerformanceDocument = previous;
        }
    }

    private nint ModelBuilderDetour(nint rendererState, byte useProjection, nint descriptorAddress)
    {
        var model = Volatile.Read(ref ownedModel);
        if (!renderingPerformanceDocument || model == 0 || descriptorAddress == 0)
            return modelBuilderHook.Original(rendererState, useProjection, descriptorAddress);

        var sourceDescriptor = (nint*)descriptorAddress;
        var descriptor = stackalloc nint[11];
        Buffer.MemoryCopy(sourceDescriptor, descriptor, 11 * sizeof(nint), 11 * sizeof(nint));
        descriptor[0] = model;
        Interlocked.Increment(ref modelBuildCalls);
        return modelBuilderHook.Original(rendererState, useProjection, (nint)descriptor);
    }

    private void TryAttachDocuments()
    {
        var core = Volatile.Read(ref apricotCore);
        if (core == 0 || attachedDocuments.Count >= hosts.Length)
            return;

        var state = *(byte**)(core + 0x1498);
        if (state == null)
            return;

        var changed = false;
        for (var index = 0; index < hosts.Length; index++)
        {
            if (hostDocuments[index] != 0)
                continue;
            var vfx = (VfxObject*)hosts[index];
            var resourceInstance = vfx == null ? null : vfx->VfxResourceInstance;
            if (resourceInstance == null)
                continue;

            var handle = *(ulong*)((byte*)resourceInstance + 0x60);
            var generation = (uint)handle;
            var slot = (uint)(handle >> 32);
            if (handle == 0 || slot >= 2048)
                continue;

            var slotRecord = state + 0x2000 + slot * 0x88;
            if (
                *(nint*)(slotRecord + 0x48) != (nint)resourceInstance
                || *(uint*)(slotRecord + 0x60) != generation
                || *(uint*)(slotRecord + 0x64) != slot
            )
                continue;

            var document = *(nint*)(slotRecord + 0x30);
            var resource = *(byte**)(slotRecord + 0x38);
            if (document == 0 || resource == null || ((*(uint*)(resource + 0x5C) >> 10) & 0x1F) != ExpectedDrawLayer)
                continue;

            var renderTarget = *(nint*)(*(nint*)document + 0x128);
            if (renderTarget == 0)
                continue;
            documentRenderHook ??= gameInteropProvider.HookFromAddress<DocumentRenderDelegate>(renderTarget, DocumentRenderDetour);
            documentRenderHook.Enable();
            hostDocuments[index] = document;
            attachedDocuments.Add(document);
            changed = true;
        }

        if (changed)
            Volatile.Write(ref documentSnapshot, new HashSet<nint>(attachedDocuments));
    }

    private void EnsureOwnedModel()
    {
        if (Volatile.Read(ref ownedModel) != 0)
            return;

        var stopwatch = Stopwatch.StartNew();
        var model = CreateOwnedModel();
        stopwatch.Stop();
        topologyCreateMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        Volatile.Write(ref ownedModel, (nint)model);
        log.Information(
            "[Underpaint] AVFX performance topology created. Faces={Faces} Vertices={Vertices} Indices={Indices} CreateMs={CreateMs:F2}.",
            MeshFaceCount,
            (MeshColumns + 1) * (MeshRows + 1),
            MeshFaceCount * 3,
            topologyCreateMilliseconds
        );
    }

    private OwnedModelRecord* CreateOwnedModel()
    {
        var model = (OwnedModelRecord*)NativeMemory.AllocZeroed((nuint)sizeof(OwnedModelRecord));
        try
        {
            const int vertexCount = (MeshColumns + 1) * (MeshRows + 1);
            const int indexCount = MeshFaceCount * 3;
            var vertices = stackalloc AvfxVertex[vertexCount];
            var indices = stackalloc ushort[indexCount];

            for (var row = 0; row <= MeshRows; row++)
            {
                for (var column = 0; column <= MeshColumns; column++)
                {
                    vertices[row * (MeshColumns + 1) + column] = new AvfxVertex(
                        column / (float)MeshColumns - 0.5f,
                        row / (float)MeshRows - 0.5f,
                        0f
                    );
                }
            }

            var writeIndex = 0;
            for (var row = 0; row < MeshRows; row++)
            {
                for (var column = 0; column < MeshColumns; column++)
                {
                    var lowerLeft = (ushort)(row * (MeshColumns + 1) + column);
                    var lowerRight = (ushort)(lowerLeft + 1);
                    var upperLeft = (ushort)(lowerLeft + MeshColumns + 1);
                    var upperRight = (ushort)(upperLeft + 1);
                    indices[writeIndex++] = lowerLeft;
                    indices[writeIndex++] = lowerRight;
                    indices[writeIndex++] = upperRight;
                    indices[writeIndex++] = lowerLeft;
                    indices[writeIndex++] = upperRight;
                    indices[writeIndex++] = upperLeft;
                }
            }

            model->VertexWrapper = createVertexWrapper(0, (uint)(vertexCount * sizeof(AvfxVertex)), 0);
            if (model->VertexWrapper == 0 || !InitializeWrapper(model->VertexWrapper, vertices, initializeVertexBuffer))
                throw new InvalidOperationException("The game rejected the shared AVFX performance vertex wrapper.");

            model->IndexWrapper = createIndexWrapper(0, (uint)(indexCount * sizeof(ushort)), 0);
            if (model->IndexWrapper == 0 || !InitializeWrapper(model->IndexWrapper, indices, initializeIndexBuffer))
                throw new InvalidOperationException("The game rejected the shared AVFX performance index wrapper.");

            model->VertexCount = (ushort)vertexCount;
            model->IndexCount = (ushort)indexCount;
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

    private void ReleaseOwnedModel()
    {
        var model = (OwnedModelRecord*)Interlocked.Exchange(ref ownedModel, 0);
        ReleaseOwnedModel(model);
    }

    private static void ReleaseOwnedModel(OwnedModelRecord* model)
    {
        if (model == null)
            return;
        ReleaseWrapper(ref model->VertexWrapper);
        ReleaseWrapper(ref model->IndexWrapper);
        NativeMemory.Free(model);
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

    private void ResetMeasurements()
    {
        Array.Clear(frameSamples);
        Array.Clear(cpuSamples);
        warmupFrameCount = 0;
        sampleFrameCount = 0;
        cooldownFrameCount = 0;
        warmupFrameMax = 0;
        warmupCpuMax = 0;
        createMilliseconds = 0;
        removeMilliseconds = 0;
        activeDocumentCount = 0;
        modelBuildCalls = 0;
        privateBytesBefore = 0;
        privateBytesActive = 0;
        workingSetBefore = 0;
        workingSetActive = 0;
    }

    private void SnapshotMemory(out long privateBytes, out long workingSet)
    {
        process.Refresh();
        privateBytes = process.PrivateMemorySize64;
        workingSet = process.WorkingSet64;
    }

    private static SampleSummary Summarize(double[] samples)
    {
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return new SampleSummary(
            samples.Average(),
            sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
            sorted[(int)Math.Ceiling(sorted.Length * 0.99) - 1],
            sorted[^1]
        );
    }

    private static string FormatBytes(long bytes) => $"{bytes / (1024d * 1024d):+0.00;-0.00;0.00} MiB";

    private static void SetHost(VfxObject* vfx, Vector3 position)
    {
        vfx->Position = new FfxivVector3
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };
        vfx->Rotation = FfxivQuaternion.Identity;
        vfx->Color = new FfxivVector4
        {
            X = 1f,
            Y = 1f,
            Z = 1f,
            W = 0.5f,
        };
        vfx->UpdateTransforms(true);
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

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

    private enum ProbePhase
    {
        Idle,
        Warmup,
        Sampling,
        Cooldown,
        Complete,
    }

    private readonly record struct SampleSummary(double Average, double P95, double P99, double Max);
}
#endif
