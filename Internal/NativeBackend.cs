using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
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
    private Primitive[] pendingPrimitives = [];
    private int pendingPrimitiveCount;
    private Primitive[] renderingPrimitives = [];
    private Matrix4x4 previousView;
    private bool hasPreviousView;

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
        buildPassesHook.Enable();
    }

    public void Dispose() => buildPassesHook.Dispose();

    internal void SubmitFrame(ReadOnlySpan<Primitive> primitives)
    {
        lock (submissionLock)
        {
            if (pendingPrimitives.Length < primitives.Length)
                pendingPrimitives = new Primitive[primitives.Length];

            primitives.CopyTo(pendingPrimitives);
            pendingPrimitiveCount = primitives.Length;
            Volatile.Write(ref hasPendingFrame, primitives.Length == 0 ? 0 : 1);
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
            resources.LoadWhiteTexture();
            resources.WriteSharedConstants(material.ShaderPackage);
            var worldConstantId = ((ModelRenderer*)modelRenderer)->ConstantSamplerIds[(int)ModelRenderer.WellKnownConstant.WorldViewMatrix];
            var bindings = materialHelper.ValidateResources(
                resources.InstanceConstant,
                resources.ModelConstant,
                resources.MaterialConstant,
                resources.WhiteTexture
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
                Primitive[] primitives;
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
                    if (!TryTakeFrame(out primitives, out primitiveCount))
                        return result;

                    contextState.InstallShaders(shaders, helperResult.ShaderDescriptor);
                    commandBaseBefore = (nint)context->CommandAllocationBase;
                    commandUsedBefore = context->CommandAllocationUsedSize;
                    for (var index = 0; index < primitiveCount; index++)
                    {
                        var primitive = primitives[index];
                        var mesh = resources.GetMesh(primitive.Type);
                        var currentWorldView = primitive.CurrentTransform * view;
                        var previousWorldView = primitive.PreviousTransform * (hasPreviousView ? previousView : view);
                        var primitiveResources = resources.WritePrimitive(
                            primitive.Type,
                            primitive.Id,
                            currentWorldView,
                            previousWorldView,
                            primitive.Color,
                            primitive.Alpha,
                            primitive.DitherFade
                        );
                        contextState.Install(resources, mesh, primitiveResources);

                        var drawCommandBaseBefore = (nint)context->CommandAllocationBase;
                        var drawCommandUsedBefore = context->CommandAllocationUsedSize;
                        buildPassesHook.Original(modelRenderer, (nint)ownedMaterialParameters, mesh.VertexCount, 0, mesh.IndexCount);
                        if (
                            (nint)context->CommandAllocationBase == drawCommandBaseBefore
                            && context->CommandAllocationUsedSize <= drawCommandUsedBefore
                        )
                            throw new InvalidOperationException(
                                $"The native pass builder produced no command data for {primitive.Type} {primitive.Id}."
                            );
                    }
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
                            + "TableSamplerId={TableSamplerId}, WhiteTexture=ready.",
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
            Volatile.Write(ref submissionDisabled, 1);
            log.Error(exception, "[Underpaint] Native submission failed; later frames are disabled.");
        }

        return result;
    }

    private bool TryTakeFrame(out Primitive[] primitives, out int primitiveCount)
    {
        lock (submissionLock)
        {
            if (Volatile.Read(ref hasPendingFrame) == 0)
            {
                primitives = [];
                primitiveCount = 0;
                return false;
            }

            (renderingPrimitives, pendingPrimitives) = (pendingPrimitives, renderingPrimitives);
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

    private delegate nint BuildPassesDelegate(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount);
}
