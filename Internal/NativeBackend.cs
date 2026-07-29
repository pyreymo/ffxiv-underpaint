using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal sealed unsafe class NativeBackend : IDisposable
{
    private const string BuildPassesSignature = "44 89 4C 24 ?? 44 89 44 24 ?? 53 56 57 41 54 41 55";
    private const int ExpectedMainView = 30;
    private const int ExpectedMainSubView = 11;
    private const int MainRenderCameraSubView = 12;

    private readonly Hook<BuildPassesDelegate> buildPassesHook;
    private readonly MaterialHelper materialHelper;
    private readonly MaterialLoader material;
    private readonly NativeResources resources;
    private readonly IPluginLog log;
    private readonly object submissionLock = new();
    private int loggedFirstCall;
    private int loggedMainRendezvous;
    private int loggedFirstSubmission;
    private int lastSubmittedFrame = -1;
    private int submissionDisabled;
    private int hasPendingFrame;
    private FrameCommand[] pendingPrimitives = [];
    private int pendingPrimitiveCount;
    private RenderCommand[] renderingPrimitives = [];
    private readonly Dictionary<ulong, Matrix4x4> consumedTransforms = [];
    private HashSet<ulong> lastPublishedDrawableIds = [];
    private readonly HashSet<ulong> retiredDrawableIds = [];
    private Matrix4x4 previousView;
    private bool hasPreviousView;

#if DEBUG
    private const int SortKeyCaptureFrameLimit = 2;
    private const int SortKeyCaptureCommandLimit = 64;

    private readonly Hook<PushBackCommandDelegate> pushBackCommandHook;
    private SortKeyCaptureSession? sortKeyCapture;
    private string? sortKeyCaptureStatus;
    private nint sortKeyCaptureContext;
    private int sortKeyCapturePushSequence;
    private int sortKeyCaptureActive;
#endif

    internal NativeBackend(
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        MaterialLoader material,
        NativeResources resources,
        IPluginLog log
    )
    {
        this.log = log;
        this.material = material;
        this.resources = resources;
        materialHelper = new MaterialHelper(sigScanner, material);
        buildPassesHook = gameInteropProvider.HookFromSignature<BuildPassesDelegate>(BuildPassesSignature, BuildPassesDetour);
#if DEBUG
        pushBackCommandHook = gameInteropProvider.HookFromAddress<PushBackCommandDelegate>(
            (nint)Context.MemberFunctionPointers.PushBackCommand,
            PushBackCommandDetour
        );
#endif
        buildPassesHook.Enable();
    }

    public void Dispose()
    {
        buildPassesHook.Dispose();
#if DEBUG
        pushBackCommandHook.Dispose();
#endif
    }

#if DEBUG
    internal string? SortKeyCaptureStatus => Volatile.Read(ref sortKeyCaptureStatus);

    internal void ArmSortKeyCapture()
    {
        if (Volatile.Read(ref sortKeyCaptureActive) != 0)
            return;

        sortKeyCapture = new SortKeyCaptureSession();
        Volatile.Write(ref sortKeyCaptureStatus, $"Armed for {SortKeyCaptureFrameLimit} Underpaint frames.");
        pushBackCommandHook.Enable();
        Volatile.Write(ref sortKeyCaptureActive, 1);
    }
#endif

    internal void SubmitFrame(ReadOnlySpan<FrameCommand> primitives)
    {
        lock (submissionLock)
        {
            var publishedDrawableIds = new HashSet<ulong>();
            foreach (var primitive in primitives)
                publishedDrawableIds.Add(primitive.DrawableId);

            foreach (var drawableId in lastPublishedDrawableIds)
            {
                if (!publishedDrawableIds.Contains(drawableId))
                    consumedTransforms.Remove(drawableId);
            }
            lastPublishedDrawableIds = publishedDrawableIds;

            if (pendingPrimitives.Length < primitives.Length)
                pendingPrimitives = new FrameCommand[primitives.Length];

            primitives.CopyTo(pendingPrimitives);
            pendingPrimitiveCount = primitives.Length;
            Volatile.Write(ref hasPendingFrame, primitives.Length == 0 ? 0 : 1);
        }
    }

    internal void RetireDrawable(ulong drawableId)
    {
        lock (submissionLock)
        {
            consumedTransforms.Remove(drawableId);
            lastPublishedDrawableIds.Remove(drawableId);
            retiredDrawableIds.Add(drawableId);

            var writeIndex = 0;
            for (var readIndex = 0; readIndex < pendingPrimitiveCount; readIndex++)
            {
                var primitive = pendingPrimitives[readIndex];
                if (primitive.DrawableId != drawableId)
                    pendingPrimitives[writeIndex++] = primitive;
            }

            pendingPrimitiveCount = writeIndex;
            Volatile.Write(ref hasPendingFrame, writeIndex == 0 ? 0 : 1);
        }
    }

    private nint BuildPassesDetour(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount)
    {
        var result = buildPassesHook.Original(modelRenderer, materialParameters, vertexCount, startIndex, indexCount);

        var threadLocals = ThreadLocals.ThreadLocalInstance();
        var context = threadLocals == null ? null : threadLocals->GraphicsKernelContext;
        if (context == null)
            return result;

        if (Volatile.Read(ref loggedFirstCall) == 0 && Interlocked.CompareExchange(ref loggedFirstCall, 1, 0) == 0)
        {
            log.Information(
                "[Underpaint] Native pass builder reached on view {View}, subview {SubView}.",
                context->ViewIndex,
                context->CurrentSubViewIndex
            );
        }

        var isMainRendezvous =
            context->ViewIndex == ExpectedMainView
            && context->CurrentSubViewIndex == ExpectedMainSubView
            && modelRenderer != 0
            && materialParameters != 0
            && *(nint*)materialParameters != 0;
        if (!isMainRendezvous)
            return result;

        if (Volatile.Read(ref loggedMainRendezvous) == 0 && Interlocked.CompareExchange(ref loggedMainRendezvous, 1, 0) == 0)
        {
            log.Information(
                "[Underpaint] Native main-view rendezvous verified at view {View}, subview {SubView}.",
                context->ViewIndex,
                context->CurrentSubViewIndex
            );
        }

        if (Volatile.Read(ref submissionDisabled) != 0)
            return result;
        if (Volatile.Read(ref hasPendingFrame) == 0)
            return result;

        var framework = Framework.Instance();
        if (framework == null)
            return result;

        var frame = unchecked((int)framework->FrameCounter);
        if (Volatile.Read(ref lastSubmittedFrame) == frame)
            return result;

        try
        {
            resources.CreateConstants();
            resources.MaterialTextures.EnsureCreated();
            resources.WriteSharedConstants(material.ShaderPackage);
            var worldConstantId = ((ModelRenderer*)modelRenderer)->ConstantSamplerIds[(int)ModelRenderer.WellKnownConstant.WorldViewMatrix];
            var bindings = materialHelper.ValidateResources(
                resources.InstanceConstant,
                resources.ModelConstant,
                resources.MaterialConstant,
                resources.MaterialTextures
            );
            var model = stackalloc Model[1];
            var modelParameters = stackalloc ModelRenderer.OnRenderModelParams[1];
            var ownedMaterialParameters = stackalloc ModelRenderer.OnRenderMaterialParams2[1];
            var selection = stackalloc MaterialHelper.ShaderSelection[1];
            materialHelper.Initialize(
                (ModelRenderer*)modelRenderer,
                model,
                modelParameters,
                ownedMaterialParameters,
                selection,
                resources.InstanceConstant
            );
            try
            {
                var contextState = new NativeContextState((byte*)context, worldConstantId, bindings);
                MaterialHelperResult helperResult;
                ShaderPair shaders;
                nint commandBaseBefore;
                ulong commandUsedBefore;
                nint commandBaseAfter;
                ulong commandUsedAfter;
                RenderCommand[] primitives;
                int primitiveCount;
                try
                {
                    helperResult = materialHelper.Apply((ModelRenderer*)modelRenderer, (byte*)context, ownedMaterialParameters, selection);
                    if (!MaterialHelper.TryResolveActiveShaders((byte*)context, helperResult.ShaderDescriptor, out shaders))
                        return result;

                    var renderManager = Manager.Instance();
                    if (renderManager == null)
                        return result;

                    var camera = renderManager->Views[ExpectedMainView].SubViews[MainRenderCameraSubView].Camera;
                    if (camera == null)
                        return result;

                    var view = (Matrix4x4)camera->ViewMatrix;
                    view.M44 = 1;
                    if (!IsFinite(view) || !Matrix4x4.Invert(view, out _))
                        return result;
                    if (Interlocked.Exchange(ref lastSubmittedFrame, frame) == frame)
                        return result;
                    ReleaseRetiredDrawableResources();
                    if (!TryTakeFrame(out primitives, out primitiveCount))
                        return result;

                    contextState.InstallShaders(shaders, helperResult.ShaderDescriptor);
                    commandBaseBefore = (nint)context->CommandAllocationBase;
                    commandUsedBefore = context->CommandAllocationUsedSize;
                    for (var index = 0; index < primitiveCount; index++)
                    {
                        var primitive = primitives[index];
                        var mesh = resources.GetMesh(primitive.Mesh);
                        var currentWorldView = primitive.CurrentTransform * view;
                        var previousWorldView =
                            primitive.PreviousTransform * (primitive.HasPrevious && hasPreviousView ? previousView : view);
                        var primitiveResources = resources.WritePrimitive(
                            primitive.Mesh,
                            primitive.DrawableId,
                            currentWorldView,
                            previousWorldView,
                            primitive.Color,
                            primitive.Alpha,
                            primitive.Dither
                        );
                        contextState.Install(resources, mesh, primitiveResources);

                        var drawCommandBaseBefore = (nint)context->CommandAllocationBase;
                        var drawCommandUsedBefore = context->CommandAllocationUsedSize;
#if DEBUG
                        if (Volatile.Read(ref sortKeyCaptureActive) != 0)
                        {
                            BeginSortKeyPrimitive(context, frame, index, primitive);
                            try
                            {
                                buildPassesHook.Original(
                                    modelRenderer,
                                    (nint)ownedMaterialParameters,
                                    mesh.VertexCount,
                                    0,
                                    mesh.IndexCount
                                );
                            }
                            finally
                            {
                                EndSortKeyPrimitive(context);
                            }
                        }
                        else
                            buildPassesHook.Original(modelRenderer, (nint)ownedMaterialParameters, mesh.VertexCount, 0, mesh.IndexCount);
#else
                        buildPassesHook.Original(modelRenderer, (nint)ownedMaterialParameters, mesh.VertexCount, 0, mesh.IndexCount);
#endif
                        if (
                            (nint)context->CommandAllocationBase == drawCommandBaseBefore
                            && context->CommandAllocationUsedSize <= drawCommandUsedBefore
                        )
                            throw new InvalidOperationException(
                                $"The native pass builder produced no command data for {primitive.Mesh} drawable {primitive.DrawableId}."
                            );
                    }
#if DEBUG
                    if (Volatile.Read(ref sortKeyCaptureActive) != 0)
                        FinishSortKeyCaptureFrame();
#endif
                    commandBaseAfter = (nint)context->CommandAllocationBase;
                    commandUsedAfter = context->CommandAllocationUsedSize;

                    previousView = view;
                    hasPreviousView = true;
                }
                finally
                {
                    contextState.Restore();
                }

                if (Interlocked.CompareExchange(ref loggedFirstSubmission, 1, 0) == 0)
                {
                    log.Information(
                        "[Underpaint] Submitted {PrimitiveCount} primitives through the native pass builder: Frame={Frame}, "
                            + "CommandArena=0x{CommandBaseBefore:X}+{CommandUsedBefore}->0x{CommandBaseAfter:X}+{CommandUsedAfter}, "
                            + "ActivePass={ActivePass}, OnRenderMaterial=0x{OnRenderMaterial:X}, "
                            + "Output40=0x{Output:X8}, Descriptor=0x{Descriptor:X}, "
                            + "MaterialConstantId={MaterialConstantId}, InstanceConstantId={InstanceConstantId}, "
                            + "ModelConstantId={ModelConstantId}, WorldConstantId={WorldConstantId}, "
                            + "NormalSamplerId={NormalSamplerId}, IndexSamplerId={IndexSamplerId}, "
                            + "TableSamplerId={TableSamplerId}, GeneratedMaterialTextures=ready.",
                        primitiveCount,
                        frame,
                        commandBaseBefore,
                        commandUsedBefore,
                        commandBaseAfter,
                        commandUsedAfter,
                        shaders.Pass,
                        helperResult.OnRenderMaterial,
                        helperResult.Output,
                        helperResult.ShaderDescriptor,
                        bindings.MaterialConstantId,
                        bindings.InstanceConstantId,
                        bindings.ModelConstantId,
                        worldConstantId,
                        bindings.NormalSamplerId,
                        bindings.IndexSamplerId,
                        bindings.TableSamplerId
                    );
                }
            }
            finally
            {
                materialHelper.Destroy(selection);
            }
        }
        catch (Exception exception)
        {
#if DEBUG
            AbortSortKeyCapture(exception.Message);
#endif
            Volatile.Write(ref submissionDisabled, 1);
            log.Error(exception, "[Underpaint] Native submission failed; later frames are disabled.");
        }

        return result;
    }

    private void ReleaseRetiredDrawableResources()
    {
        ulong[] retired;
        lock (submissionLock)
        {
            if (retiredDrawableIds.Count == 0)
                return;
            retired = [.. retiredDrawableIds];
            retiredDrawableIds.Clear();
        }

        foreach (var drawableId in retired)
            resources.ReleasePrimitive(drawableId);
    }

    private bool TryTakeFrame(out RenderCommand[] primitives, out int primitiveCount)
    {
        lock (submissionLock)
        {
            if (Volatile.Read(ref hasPendingFrame) == 0)
            {
                primitives = [];
                primitiveCount = 0;
                return false;
            }

            if (renderingPrimitives.Length < pendingPrimitiveCount)
                renderingPrimitives = new RenderCommand[pendingPrimitiveCount];

            for (var index = 0; index < pendingPrimitiveCount; index++)
            {
                var primitive = pendingPrimitives[index];
                var hasPrevious = consumedTransforms.TryGetValue(primitive.DrawableId, out var previousTransform);
                if (!hasPrevious)
                    previousTransform = primitive.CurrentTransform;
                consumedTransforms[primitive.DrawableId] = primitive.CurrentTransform;
                renderingPrimitives[index] = new RenderCommand(
                    primitive.DrawableId,
                    primitive.Mesh,
                    primitive.CurrentTransform,
                    primitive.SortingCenter,
                    previousTransform,
                    hasPrevious,
                    primitive.Color,
                    primitive.Alpha,
                    primitive.Dither
                );
            }

            primitives = renderingPrimitives;
            primitiveCount = pendingPrimitiveCount;
            pendingPrimitiveCount = 0;
            Volatile.Write(ref hasPendingFrame, 0);
            return true;
        }
    }

    private static bool IsFinite(Matrix4x4 matrix)
    {
        var values = new ReadOnlySpan<float>(&matrix, 16);
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
                return false;
        }

        return true;
    }

#if DEBUG
    private void BeginSortKeyPrimitive(Context* context, int frame, int sequence, RenderCommand primitive)
    {
        if (
            Volatile.Read(ref sortKeyCaptureActive) == 0
            || sortKeyCapture == null
            || sortKeyCapture.CommandCount >= SortKeyCaptureCommandLimit
        )
            return;

        var position = primitive.SortingCenter;
        sortKeyCapture.PrimitiveCount++;
        sortKeyCapture.EntryKeys.Add(context->SortKey);
        sortKeyCapture.Details.AppendLine(
            $"Primitive Frame={frame} Sequence={sequence} Drawable={primitive.DrawableId} "
                + $"Position=({position.X:F3},{position.Y:F3},{position.Z:F3}) Entry=0x{context->SortKey:X8}"
        );
        sortKeyCaptureContext = (nint)context;
        sortKeyCapturePushSequence = 0;
    }

    private void EndSortKeyPrimitive(Context* context)
    {
        if (sortKeyCapture == null || sortKeyCaptureContext != (nint)context)
            return;

        sortKeyCapture.Details.AppendLine($"  Exit=0x{context->SortKey:X8} Commands={sortKeyCapturePushSequence}");
        sortKeyCaptureContext = 0;
    }

    private void PushBackCommandDetour(Context* context, void* command)
    {
        var capture = sortKeyCapture;
        var shouldCapture =
            Volatile.Read(ref sortKeyCaptureActive) == 0
                ? false
                : capture != null && sortKeyCaptureContext == (nint)context && capture.CommandCount < SortKeyCaptureCommandLimit;
        if (!shouldCapture)
        {
            pushBackCommandHook.Original(context, command);
            return;
        }

        var type = command == null ? -1 : *(int*)command;
        var view = context->ViewIndex;
        var subView = context->CurrentSubViewIndex;
        var sortKey = context->SortKey;
        pushBackCommandHook.Original(context, command);

        capture!.CommandCount++;
        capture.CommandKeys.Add(sortKey);
        capture.Details.AppendLine($"  Push={sortKeyCapturePushSequence++} Type={type} View={view}.{subView} SortKey=0x{sortKey:X8}");
    }

    private void FinishSortKeyCaptureFrame()
    {
        var capture = sortKeyCapture;
        if (Volatile.Read(ref sortKeyCaptureActive) == 0 || capture == null)
            return;

        capture.FrameCount++;
        if (capture.FrameCount < SortKeyCaptureFrameLimit && capture.CommandCount < SortKeyCaptureCommandLimit)
            return;

        CompleteSortKeyCapture(capture);
    }

    private void CompleteSortKeyCapture(SortKeyCaptureSession capture)
    {
        Volatile.Write(ref sortKeyCaptureActive, 0);
        sortKeyCaptureContext = 0;
        sortKeyCapture = null;
        pushBackCommandHook.Disable();

        var status =
            $"Complete: {capture.FrameCount} frames, {capture.PrimitiveCount} primitives, "
            + $"{capture.CommandCount} commands, {capture.EntryKeys.Count} entry keys, {capture.CommandKeys.Count} command keys. "
            + "See the Dalamud log for details.";
        Volatile.Write(ref sortKeyCaptureStatus, status);
        log.Information(
            "[Underpaint] SortKey capture complete. Frames={Frames} Primitives={Primitives} Commands={Commands} "
                + "EntryKeys={EntryKeys} CommandKeys={CommandKeys}\n{Details}",
            capture.FrameCount,
            capture.PrimitiveCount,
            capture.CommandCount,
            capture.EntryKeys.Count,
            capture.CommandKeys.Count,
            capture.Details.ToString()
        );
    }

    private void AbortSortKeyCapture(string reason)
    {
        if (Volatile.Read(ref sortKeyCaptureActive) == 0)
            return;

        Volatile.Write(ref sortKeyCaptureActive, 0);
        sortKeyCaptureContext = 0;
        sortKeyCapture = null;
        pushBackCommandHook.Disable();
        Volatile.Write(ref sortKeyCaptureStatus, $"Capture stopped: {reason}");
    }
#endif

    private delegate nint BuildPassesDelegate(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount);

#if DEBUG
    private delegate void PushBackCommandDelegate(Context* context, void* command);

    private sealed class SortKeyCaptureSession
    {
        internal readonly HashSet<uint> EntryKeys = [];
        internal readonly HashSet<uint> CommandKeys = [];
        internal readonly System.Text.StringBuilder Details = new();
        internal int FrameCount;
        internal int PrimitiveCount;
        internal int CommandCount;
    }
#endif

    private readonly record struct RenderCommand(
        ulong DrawableId,
        MeshKind Mesh,
        Matrix4x4 CurrentTransform,
        Vector3 SortingCenter,
        Matrix4x4 PreviousTransform,
        bool HasPrevious,
        Vector3 Color,
        float Alpha,
        float Dither
    );
}

internal readonly record struct FrameCommand(
    ulong DrawableId,
    MeshKind Mesh,
    Matrix4x4 CurrentTransform,
    Vector3 SortingCenter,
    Vector3 Color,
    float Alpha,
    float Dither
);
