#if DEBUG
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FfxivQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using FfxivVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace Underpaint.Internal;

internal sealed unsafe class AvfxSortProbe : IDisposable
{
    private const string PoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const string RunCallSignature = "E8 ?? ?? ?? ?? B0 02 EB 02";
    private const string RemoveSignature =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private const string TaskUpdateGraphicsSceneSignature =
        "48 83 EC 28 48 8B 0D ?? ?? ?? ?? 48 8B 01 FF 50 20 E8 ?? ?? ?? ?? 48 8B 0D";
    private const string DepthProducerSignature =
        "48 89 4C 24 ?? 53 41 56 48 81 EC ?? ?? ?? ?? 48 8B 99 ?? ?? ?? ?? 45 8B D8 4C 63 F2";
    private const string SortedConsumerSignature =
        "48 8B C4 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 44 8B BC 24 ?? ?? ?? ?? 45 8B E1 45 8B F0 4C 8B E9 45 85 FF";
    private const int CaptureFrameLimit = 120;
    private const int CaptureEventLimit = 1024;
    private const int CaptureCommandLimit = 512;

    private readonly object sync = new();
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private readonly StaticVfxRunDelegate run;
    private readonly Hook<StaticVfxRemoveDelegate> removeHook;
    private readonly Hook<TaskUpdateGraphicsSceneDelegate> taskUpdateGraphicsSceneHook;
    private readonly Hook<DepthProducerDelegate> depthProducerHook;
    private readonly Hook<SortedConsumerDelegate> sortedConsumerHook;
    private readonly Hook<PushBackCommandDelegate> pushBackCommandHook;
    private readonly Hook<ProcessCommandsDelegate> processCommandsHook;
    private readonly List<ProbeInstance> activeVfx = [];
    private Hook<DocumentRenderDelegate>? documentRenderHook;
    private ProbeRequest? pendingRequest;
    private CaptureSession? capture;
    private string status = "Ready.";
    private string? report;
    private nint apricotCore;
    private bool stopRequested;
    private bool disposed;

    [ThreadStatic]
    private static nint renderingDocument;

    [ThreadStatic]
    private static int documentPushSequence;

    internal AvfxSortProbe(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;
        var runAddress = sigScanner.ScanText(RunCallSignature);
        run = Marshal.GetDelegateForFunctionPointer<StaticVfxRunDelegate>(runAddress);
        Hook<StaticVfxRemoveDelegate>? remove = null;
        Hook<TaskUpdateGraphicsSceneDelegate>? taskUpdate = null;
        Hook<DepthProducerDelegate>? depthProducer = null;
        Hook<SortedConsumerDelegate>? sortedConsumer = null;
        Hook<PushBackCommandDelegate>? pushBack = null;
        Hook<ProcessCommandsDelegate>? process = null;
        try
        {
            remove = gameInteropProvider.HookFromSignature<StaticVfxRemoveDelegate>(RemoveSignature, RemoveDetour);
            taskUpdate = gameInteropProvider.HookFromSignature<TaskUpdateGraphicsSceneDelegate>(
                TaskUpdateGraphicsSceneSignature,
                TaskUpdateGraphicsSceneDetour
            );
            depthProducer = gameInteropProvider.HookFromSignature<DepthProducerDelegate>(DepthProducerSignature, DepthProducerDetour);
            sortedConsumer = gameInteropProvider.HookFromSignature<SortedConsumerDelegate>(SortedConsumerSignature, SortedConsumerDetour);
            pushBack = gameInteropProvider.HookFromAddress<PushBackCommandDelegate>(
                (nint)Context.MemberFunctionPointers.PushBackCommand,
                PushBackCommandDetour
            );
            process = gameInteropProvider.HookFromAddress<ProcessCommandsDelegate>(
                (nint)ImmediateContext.MemberFunctionPointers.ProcessCommands,
                ProcessCommandsDetour
            );
        }
        catch
        {
            process?.Dispose();
            pushBack?.Dispose();
            sortedConsumer?.Dispose();
            depthProducer?.Dispose();
            taskUpdate?.Dispose();
            remove?.Dispose();
            throw;
        }

        removeHook = remove;
        taskUpdateGraphicsSceneHook = taskUpdate;
        depthProducerHook = depthProducer;
        sortedConsumerHook = sortedConsumer;
        pushBackCommandHook = pushBack;
        processCommandsHook = process;
    }

    internal string Status
    {
        get
        {
            lock (sync)
                return status;
        }
    }

    internal void Arm(string resourcePath, IReadOnlyList<Vector3> positions, int expectedDrawLayerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count is < 2 or > 32)
            throw new ArgumentOutOfRangeException(nameof(positions), "The probe requires 2-32 VFX instances.");
        if (expectedDrawLayerType is < 0 or > 12)
            throw new ArgumentOutOfRangeException(nameof(expectedDrawLayerType));

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pendingRequest = new ProbeRequest(resourcePath, [.. positions], expectedDrawLayerType);
            stopRequested = false;
            report = null;
            status = $"Armed: {positions.Count} instances, expected category {expectedDrawLayerType}.";
        }
    }

    internal void Update()
    {
        ProbeRequest? request;
        bool shouldStop;
        lock (sync)
        {
            if (disposed)
                return;
            shouldStop = stopRequested;
            stopRequested = false;
            request = pendingRequest;
            pendingRequest = null;
        }

        if (shouldStop)
            StopActiveVfx();
        if (request != null)
        {
            StopActiveVfx();
            Start(request);
        }

        var completed = false;
        lock (sync)
        {
            if (capture == null)
                return;

            RefreshIdentities();
            var frame = CurrentFrame();
            if (!capture.CompletionPending && frame - capture.StartFrame >= CaptureFrameLimit)
                RequestCompletion($"Reached the {CaptureFrameLimit}-frame limit.");
            if (capture.CompletionPending)
            {
                CompleteCapture();
                completed = true;
            }
        }
        if (completed)
            DisableObservationHooks();
    }

    internal void Stop()
    {
        lock (sync)
        {
            if (disposed)
                return;
            pendingRequest = null;
            stopRequested = true;
            status = "Stop requested.";
        }
    }

    internal string? TakeReport()
    {
        lock (sync)
        {
            var value = report;
            report = null;
            return value;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            pendingRequest = null;
            stopRequested = false;
        }

        StopActiveVfx();
        documentRenderHook?.Dispose();
        processCommandsHook.Dispose();
        pushBackCommandHook.Dispose();
        sortedConsumerHook.Dispose();
        depthProducerHook.Dispose();
        taskUpdateGraphicsSceneHook.Dispose();
        removeHook.Dispose();
    }

    private void Start(ProbeRequest request)
    {
        try
        {
            removeHook.Enable();
            EnableObservationHooks();
            lock (sync)
                capture = new CaptureSession(request, CurrentFrame());

            for (var index = 0; index < request.Positions.Length; index++)
            {
                var position = request.Positions[index];
                var vfx = VfxObject.Create(request.ResourcePath, PoolName);
                if (vfx == null)
                    throw new InvalidOperationException("VfxObject.Create returned null.");

                lock (sync)
                    activeVfx.Add(new ProbeInstance(index, (nint)vfx, position));
                run(vfx, 0f, uint.MaxValue);
                SetPosition(vfx, position);
            }

            lock (sync)
                status = $"Capturing: {activeVfx.Count} instances, expected category {request.ExpectedDrawLayerType}.";
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                if (capture != null)
                    capture.Errors.Add(exception.ToString());
                RequestCompletion($"Start failed: {exception.Message}");
                CompleteCapture();
            }
            StopActiveVfx();
        }
    }

    private void StopActiveVfx()
    {
        DisableObservationHooks();
        removeHook.Disable();

        ProbeInstance[] vfxObjects;
        Hook<DocumentRenderDelegate>? activeDocumentHook;
        lock (sync)
        {
            if (capture != null && !capture.Completed)
            {
                RequestCompletion("Stopped by the caller.");
                CompleteCapture();
            }
            vfxObjects = [.. activeVfx];
            activeVfx.Clear();
            capture = null;
            apricotCore = 0;
            activeDocumentHook = documentRenderHook;
            documentRenderHook = null;
            if (!disposed)
                status = "Stopped.";
        }

        activeDocumentHook?.Dispose();
        foreach (var instance in vfxObjects)
            removeHook.Original((VfxObject*)instance.VfxAddress);
    }

    private nint RemoveDetour(VfxObject* vfx)
    {
        lock (sync)
        {
            activeVfx.RemoveAll(instance => instance.VfxAddress == (nint)vfx);
            if (capture != null && activeVfx.Count == 0)
                RequestCompletion("All tracked VFX instances were removed by the game.");
        }
        return removeHook.Original(vfx);
    }

    private void EnableObservationHooks()
    {
        taskUpdateGraphicsSceneHook.Enable();
        depthProducerHook.Enable();
        sortedConsumerHook.Enable();
        pushBackCommandHook.Enable();
        processCommandsHook.Enable();
    }

    private void DisableObservationHooks()
    {
        documentRenderHook?.Disable();
        processCommandsHook.Disable();
        pushBackCommandHook.Disable();
        sortedConsumerHook.Disable();
        depthProducerHook.Disable();
        taskUpdateGraphicsSceneHook.Disable();
    }

    private void RefreshIdentities()
    {
        var currentCapture = capture;
        if (currentCapture == null || currentCapture.Completed || apricotCore == 0)
            return;

        var state = *(byte**)(apricotCore + 0x1498);
        if (state == null)
            return;

        nint renderTarget = 0;
        foreach (var instance in activeVfx)
        {
            var resourceInstance = ((VfxObject*)instance.VfxAddress)->VfxResourceInstance;
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
            {
                RequestCompletion($"Packed handle validation failed for instance {instance.Index}.");
                return;
            }

            var document = *(nint*)(slotRecord + 0x30);
            var resource = *(nint*)(slotRecord + 0x38);
            if (document == 0 || resource == 0)
                continue;

            var flags = *(uint*)(resource + 0x5C);
            var drawLayerType = (int)((flags >> 10) & 0x1F);
            var drawOrderType = (int)((flags >> 15) & 0x1FF);
            var softKeyOffset = *(float*)(resource + 0x58);
            if (drawLayerType != currentCapture.Request.ExpectedDrawLayerType)
            {
                RequestCompletion(
                    $"Rejected: parsed DrawLayerType {drawLayerType}, expected {currentCapture.Request.ExpectedDrawLayerType}."
                );
                return;
            }

            instance.ResourceInstance = (nint)resourceInstance;
            instance.Handle = handle;
            instance.Generation = generation;
            instance.Slot = slot;
            instance.Document = document;
            instance.Resource = resource;
            instance.DrawLayerType = drawLayerType;
            instance.DrawOrderType = drawOrderType;
            instance.SoftKeyOffset = softKeyOffset;
            currentCapture.BySlot[slot] = instance;
            currentCapture.ByDocument[document] = instance;

            var target = *(nint*)(*(nint*)document + 0x128);
            if (renderTarget == 0)
                renderTarget = target;
            else if (renderTarget != target)
            {
                RequestCompletion("Tracked documents use different render virtual functions.");
                return;
            }
        }

        if (renderTarget == 0 || documentRenderHook != null)
            return;

        documentRenderHook = gameInteropProvider.HookFromAddress<DocumentRenderDelegate>(renderTarget, DocumentRenderDetour);
        documentRenderHook.Enable();
        status = $"Capturing: linked {currentCapture.ByDocument.Count}/{activeVfx.Count} real documents.";
    }

    private void TaskUpdateGraphicsSceneDetour()
    {
        CaptureThread(static session => session.TaskThreads);
        taskUpdateGraphicsSceneHook.Original();
    }

    private nint DepthProducerDetour(nint core, int category, uint workerIndex, uint workerCount, nint producedCount)
    {
        var result = depthProducerHook.Original(core, category, workerIndex, workerCount, producedCount);
        try
        {
            lock (sync)
            {
                apricotCore = core;
                if (capture == null || capture.Completed || category != capture.Request.ExpectedDrawLayerType)
                    return result;
                capture.ProducerThreads.Add(GetCurrentThreadId());
                RecordEvent(
                    $"Producer Frame={CurrentFrame()} Category={category} Worker={workerIndex}/{workerCount} Thread={GetCurrentThreadId()}"
                );
            }
        }
        catch (Exception exception)
        {
            FailCapture(exception);
        }
        return result;
    }

    private nint SortedConsumerDetour(nint core, int category, uint start, int stride, uint count)
    {
        try
        {
            lock (sync)
            {
                apricotCore = core;
                var currentCapture = capture;
                if (
                    currentCapture != null
                    && !currentCapture.Completed
                    && category == currentCapture.Request.ExpectedDrawLayerType
                    && stride > 0
                )
                {
                    currentCapture.ConsumerThreads.Add(GetCurrentThreadId());
                    var state = *(byte**)(core + 0x1498);
                    var pairs = state + 0x46000 + category * 0x4000;
                    for (var rank = start; rank < count && currentCapture.EventCount < CaptureEventLimit; rank += (uint)stride)
                    {
                        var slot = *(uint*)(pairs + rank * 8 + 4);
                        if (!currentCapture.BySlot.TryGetValue(slot, out var instance))
                            continue;
                        var depthKey = *(float*)(pairs + rank * 8);
                        RecordEvent(
                            $"Sorted Frame={CurrentFrame()} Instance={instance.Index} Slot={slot} Rank={rank} "
                                + $"DepthKey={depthKey:F6} Start={start} Stride={stride} Thread={GetCurrentThreadId()}"
                        );
                    }
                }
            }
        }
        catch (Exception exception)
        {
            FailCapture(exception);
        }
        return sortedConsumerHook.Original(core, category, start, stride, count);
    }

    private nint DocumentRenderDetour(nint document)
    {
        var hook = documentRenderHook!;
        var previousDocument = renderingDocument;
        var previousSequence = documentPushSequence;
        lock (sync)
        {
            if (capture != null && capture.ByDocument.ContainsKey(document))
            {
                renderingDocument = document;
                documentPushSequence = 0;
            }
        }

        try
        {
            return hook.Original(document);
        }
        finally
        {
            renderingDocument = previousDocument;
            documentPushSequence = previousSequence;
        }
    }

    private void PushBackCommandDetour(Context* context, void* command)
    {
        var document = renderingDocument;
        var sortKey = context->SortKey;
        var view = context->ViewIndex;
        var subView = context->CurrentSubViewIndex;
        pushBackCommandHook.Original(context, command);

        if (document == 0 || command == null)
            return;
        try
        {
            lock (sync)
            {
                var currentCapture = capture;
                if (
                    currentCapture == null
                    || currentCapture.Completed
                    || currentCapture.Commands.Count >= CaptureCommandLimit
                    || !currentCapture.ByDocument.TryGetValue(document, out var instance)
                )
                    return;

                var captured = new CapturedCommand(
                    currentCapture.Commands.Count,
                    CurrentFrame(),
                    instance.Index,
                    (nint)command,
                    *(int*)command,
                    sortKey,
                    (nint)context,
                    view,
                    subView,
                    documentPushSequence++,
                    GetCurrentThreadId()
                );
                currentCapture.Commands.Add(captured);
                currentCapture.CommandsByAddress[(nint)command] = captured;
                currentCapture.PushThreads.Add(captured.PushThread);
                if (currentCapture.Commands.Count >= CaptureCommandLimit)
                    RequestCompletion($"Reached the {CaptureCommandLimit}-command limit.");
            }
        }
        catch (Exception exception)
        {
            FailCapture(exception);
        }
    }

    private void ProcessCommandsDetour(ImmediateContext* context, RenderCommandBufferGroup* commands, uint commandCount)
    {
        try
        {
            lock (sync)
            {
                var currentCapture = capture;
                if (currentCapture != null && !currentCapture.Completed)
                {
                    currentCapture.ProcessThreads.Add(GetCurrentThreadId());
                    for (var index = 0u; index < commandCount; index++)
                    {
                        var address = (nint)commands[index].Command;
                        if (!currentCapture.CommandsByAddress.Remove(address, out var captured))
                            continue;
                        captured.ExecutionSequence = currentCapture.ExecutionCount++;
                        captured.ExecutionThread = GetCurrentThreadId();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            FailCapture(exception);
        }
        processCommandsHook.Original(context, commands, commandCount);
    }

    private void CaptureThread(Func<CaptureSession, HashSet<uint>> selector)
    {
        lock (sync)
        {
            if (capture is { Completed: false } currentCapture)
                selector(currentCapture).Add(GetCurrentThreadId());
        }
    }

    private void RecordEvent(string value)
    {
        if (capture == null || capture.Completed || capture.EventCount >= CaptureEventLimit)
            return;
        capture.Events.Add(value);
        capture.EventCount++;
        if (capture.EventCount >= CaptureEventLimit)
            RequestCompletion($"Reached the {CaptureEventLimit}-event limit.");
    }

    private void RequestCompletion(string reason)
    {
        if (capture == null || capture.Completed || capture.CompletionPending)
            return;
        capture.CompletionPending = true;
        capture.CompletionReason = reason;
    }

    private void CompleteCapture()
    {
        var currentCapture = capture;
        if (currentCapture == null || currentCapture.Completed)
            return;

        currentCapture.Completed = true;
        var builder = new StringBuilder();
        builder.AppendLine("Underpaint AVFX sorting probe");
        builder.AppendLine($"Reason={currentCapture.CompletionReason}");
        builder.AppendLine(
            $"Path={currentCapture.Request.ResourcePath} ExpectedCategory={currentCapture.Request.ExpectedDrawLayerType} "
                + $"Instances={activeVfx.Count} Frames={CurrentFrame() - currentCapture.StartFrame}"
        );
        builder.AppendLine($"TaskThreads={FormatThreads(currentCapture.TaskThreads)}");
        builder.AppendLine($"ProducerThreads={FormatThreads(currentCapture.ProducerThreads)}");
        builder.AppendLine($"ConsumerThreads={FormatThreads(currentCapture.ConsumerThreads)}");
        builder.AppendLine($"PushThreads={FormatThreads(currentCapture.PushThreads)}");
        builder.AppendLine($"ProcessThreads={FormatThreads(currentCapture.ProcessThreads)}");
        foreach (var instance in activeVfx)
        {
            builder.AppendLine(
                $"Instance={instance.Index} Position=({instance.Position.X:F3},{instance.Position.Y:F3},{instance.Position.Z:F3}) "
                    + $"Vfx=0x{instance.VfxAddress:X} ResourceInstance=0x{instance.ResourceInstance:X} "
                    + $"Handle=0x{instance.Handle:X16} Generation={instance.Generation} Slot={instance.Slot} "
                    + $"Document=0x{instance.Document:X} Resource=0x{instance.Resource:X} DrawLayer={instance.DrawLayerType} "
                    + $"DrawOrder={instance.DrawOrderType} SoftKeyOffset={instance.SoftKeyOffset:F6}"
            );
        }
        foreach (var value in currentCapture.Events)
            builder.AppendLine(value);
        foreach (var command in currentCapture.Commands)
        {
            builder.AppendLine(
                $"Command={command.Sequence} Frame={command.Frame} Instance={command.InstanceIndex} "
                    + $"DocumentPush={command.DocumentSequence} Type={command.Type} SortKey=0x{command.SortKey:X8} "
                    + $"Context=0x{command.Context:X} View={command.View}.{command.SubView} PushThread={command.PushThread} "
                    + $"Execution={command.ExecutionSequence?.ToString() ?? "missing"} ExecutionThread={command.ExecutionThread}"
            );
        }
        foreach (var error in currentCapture.Errors)
            builder.AppendLine($"Error={error}");

        report = builder.ToString();
        status = $"Complete: {activeVfx.Count} instances, {currentCapture.EventCount} events, "
            + $"{currentCapture.Commands.Count} commands. VFX remain active until Stop.";
        log.Information("[Underpaint] AVFX sorting probe complete.\n{Report}", report);
    }

    private void FailCapture(Exception exception)
    {
        lock (sync)
        {
            if (capture == null || capture.Completed)
                return;
            capture.Errors.Add(exception.ToString());
            RequestCompletion($"Capture failed: {exception.Message}");
        }
    }

    private static int CurrentFrame()
    {
        var framework = Framework.Instance();
        return framework == null ? 0 : unchecked((int)framework->FrameCounter);
    }

    private static string FormatThreads(HashSet<uint> threads) => threads.Count == 0 ? "none" : string.Join(',', threads.Order());

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static void SetPosition(VfxObject* vfx, Vector3 position)
    {
        vfx->Position = new FfxivVector3 { X = position.X, Y = position.Y, Z = position.Z };
        vfx->Rotation = FfxivQuaternion.Identity;
        vfx->UpdateTransforms(true);
    }

    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);
    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);
    private delegate void TaskUpdateGraphicsSceneDelegate();
    private delegate nint DepthProducerDelegate(nint core, int category, uint workerIndex, uint workerCount, nint producedCount);
    private delegate nint SortedConsumerDelegate(nint core, int category, uint start, int stride, uint count);
    private delegate nint DocumentRenderDelegate(nint document);
    private delegate void PushBackCommandDelegate(Context* context, void* command);
    private delegate void ProcessCommandsDelegate(ImmediateContext* context, RenderCommandBufferGroup* commands, uint commandCount);

    private sealed record ProbeRequest(string ResourcePath, Vector3[] Positions, int ExpectedDrawLayerType);

    private sealed class ProbeInstance(int index, nint vfxAddress, Vector3 position)
    {
        internal int Index { get; } = index;
        internal nint VfxAddress { get; } = vfxAddress;
        internal Vector3 Position { get; } = position;
        internal nint ResourceInstance;
        internal ulong Handle;
        internal uint Generation;
        internal uint Slot;
        internal nint Document;
        internal nint Resource;
        internal int DrawLayerType = -1;
        internal int DrawOrderType = -1;
        internal float SoftKeyOffset;
    }

    private sealed class CaptureSession(ProbeRequest request, int startFrame)
    {
        internal ProbeRequest Request { get; } = request;
        internal int StartFrame { get; } = startFrame;
        internal readonly Dictionary<uint, ProbeInstance> BySlot = [];
        internal readonly Dictionary<nint, ProbeInstance> ByDocument = [];
        internal readonly Dictionary<nint, CapturedCommand> CommandsByAddress = [];
        internal readonly List<CapturedCommand> Commands = [];
        internal readonly List<string> Events = [];
        internal readonly List<string> Errors = [];
        internal readonly HashSet<uint> TaskThreads = [];
        internal readonly HashSet<uint> ProducerThreads = [];
        internal readonly HashSet<uint> ConsumerThreads = [];
        internal readonly HashSet<uint> PushThreads = [];
        internal readonly HashSet<uint> ProcessThreads = [];
        internal int EventCount;
        internal int ExecutionCount;
        internal bool CompletionPending;
        internal string CompletionReason = "Capture completed.";
        internal bool Completed;
    }

    private sealed class CapturedCommand(
        int sequence,
        int frame,
        int instanceIndex,
        nint address,
        int type,
        uint sortKey,
        nint context,
        int view,
        int subView,
        int documentSequence,
        uint pushThread
    )
    {
        internal int Sequence { get; } = sequence;
        internal int Frame { get; } = frame;
        internal int InstanceIndex { get; } = instanceIndex;
        internal nint Address { get; } = address;
        internal int Type { get; } = type;
        internal uint SortKey { get; } = sortKey;
        internal nint Context { get; } = context;
        internal int View { get; } = view;
        internal int SubView { get; } = subView;
        internal int DocumentSequence { get; } = documentSequence;
        internal uint PushThread { get; } = pushThread;
        internal int? ExecutionSequence;
        internal uint ExecutionThread;
    }
}
#endif
