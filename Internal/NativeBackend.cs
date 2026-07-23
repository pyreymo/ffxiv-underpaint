using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal sealed unsafe class NativeBackend : IDisposable
{
    private const string BuildPassesSignature = "44 89 4C 24 ?? 44 89 44 24 ?? 53 56 57 41 54 41 55";
    private const int ExpectedMainView = 30;
    private const int ExpectedMainSubView = 11;

    private readonly Hook<BuildPassesDelegate> buildPassesHook;
    private readonly MaterialHelper materialHelper;
    private readonly MaterialLoader material;
    private readonly NativeResources resources;
    private readonly IPluginLog log;
    private int loggedFirstCall;
    private int loggedMainRendezvous;
    private int loggedFirstSubmission;
    private int lastSubmittedFrame = -1;
    private int submissionDisabled;
    private Matrix4x4 fixedTriangleWorld;
    private bool hasFixedTriangleWorld;

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
                try
                {
                    helperResult = materialHelper.Apply((ModelRenderer*)modelRenderer, (byte*)context, ownedMaterialParameters, selection);
                    if (!MaterialHelper.TryResolveActiveShaders((byte*)context, helperResult.ShaderDescriptor, out shaders))
                        return result;
                    if (Interlocked.Exchange(ref lastSubmittedFrame, frame) == frame)
                        return result;

                    var cameraManager = CameraManager.Instance();
                    var camera = cameraManager == null ? null : cameraManager->CurrentCamera;
                    var renderCamera = camera == null ? null : camera->RenderCamera;
                    if (renderCamera == null)
                        throw new InvalidOperationException("The current render camera is not available.");

                    var view = (Matrix4x4)renderCamera->ViewMatrix;
                    if (!hasFixedTriangleWorld)
                    {
                        if (!Matrix4x4.Invert(view, out var inverseView))
                            throw new InvalidOperationException("The current render view matrix is not invertible.");
                        fixedTriangleWorld = Matrix4x4.CreateTranslation(0, 0, -5) * inverseView;
                        hasFixedTriangleWorld = true;
                    }

                    var currentWorldView = fixedTriangleWorld * view;
                    var previousWorldView = currentWorldView;
                    var triangleColor = new Vector4(1, 0, 0, 0.5f);
                    resources.WriteFixedTriangleConstants(material.ShaderPackage, currentWorldView, previousWorldView, triangleColor);
                    contextState.InstallShaders(shaders, helperResult.ShaderDescriptor);
                    contextState.Install(resources);
                    commandBaseBefore = (nint)context->CommandAllocationBase;
                    commandUsedBefore = context->CommandAllocationUsedSize;
                    buildPassesHook.Original(
                        modelRenderer,
                        (nint)ownedMaterialParameters,
                        NativeResources.VertexCount,
                        0,
                        NativeResources.IndexCount
                    );
                    commandBaseAfter = (nint)context->CommandAllocationBase;
                    commandUsedAfter = context->CommandAllocationUsedSize;
                    if (commandBaseAfter == commandBaseBefore && commandUsedAfter <= commandUsedBefore)
                        throw new InvalidOperationException("The native pass builder produced no command data.");
                }
                finally
                {
                    contextState.Restore();
                }

                if (Interlocked.CompareExchange(ref loggedFirstSubmission, 1, 0) == 0)
                {
                    log.Information(
                        "[Underpaint] Submitted one owned triangle every render frame: Frame={Frame}, "
                            + "CommandArena=0x{CommandBaseBefore:X}+{CommandUsedBefore}->0x{CommandBaseAfter:X}+{CommandUsedAfter}, "
                            + "ActivePass={ActivePass}, OnRenderMaterial=0x{OnRenderMaterial:X}, "
                            + "Output40=0x{Output:X8}, Descriptor=0x{Descriptor:X}, "
                            + "MaterialConstantId={MaterialConstantId}, InstanceConstantId={InstanceConstantId}, "
                            + "ModelConstantId={ModelConstantId}, WorldConstantId={WorldConstantId}, "
                            + "NormalSamplerId={NormalSamplerId}, IndexSamplerId={IndexSamplerId}, "
                            + "TableSamplerId={TableSamplerId}, WhiteTexture=ready.",
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

    private delegate nint BuildPassesDelegate(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount);
}
