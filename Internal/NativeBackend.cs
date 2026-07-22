using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal sealed unsafe class NativeBackend : IDisposable
{
    private const string BuildPassesSignature = "44 89 4C 24 ?? 44 89 44 24 ?? 53 56 57 41 54 41 55";
    private const int ExpectedMainView = 30;
    private const int ExpectedMainSubView = 11;
    private const int MainTransformSubView = 12;

    private readonly Hook<BuildPassesDelegate> buildPassesHook;
    private readonly MaterialHelper materialHelper;
    private readonly MaterialLoader material;
    private readonly NativeResources resources;
    private readonly IPluginLog log;
    private int loggedFirstCall;
    private int loggedMainRendezvous;
    private int nativeInitializationAttempted;

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

        if (Interlocked.CompareExchange(ref nativeInitializationAttempted, 1, 0) != 0)
            return result;

        try
        {
            resources.CreateConstants(material.ShaderPackage);
            resources.LoadWhiteTexture();
            var view = GetMainViewMatrix();
            resources.WriteInitialWorld(view);
            var worldConstantId = ((ModelRenderer*)modelRenderer)->ConstantSamplerIds[(int)ModelRenderer.WellKnownConstant.WorldViewMatrix];
            var helperResult = materialHelper.Validate(
                (ModelRenderer*)modelRenderer,
                (byte*)context,
                resources.InstanceConstant,
                resources.ModelConstant,
                resources.MaterialConstant,
                resources.WhiteTexture
            );
            var contextState = new NativeContextState((byte*)context, worldConstantId, helperResult);
            try
            {
                contextState.Install(resources);
                contextState.VerifyInstalled(resources);
            }
            finally
            {
                contextState.Restore();
            }
            contextState.VerifyRestored();
            log.Information(
                "[Underpaint] Native constants and material helpers verified: OnRenderMaterial=0x{OnRenderMaterial:X}, "
                    + "Output40=0x{Output:X8}, Descriptor=0x{Descriptor:X}, "
                    + "MaterialConstantId={MaterialConstantId}, InstanceConstantId={InstanceConstantId}, "
                    + "ModelConstantId={ModelConstantId}, WorldConstantId={WorldConstantId}, "
                    + "NormalSamplerId={NormalSamplerId}, IndexSamplerId={IndexSamplerId}, "
                    + "TableSamplerId={TableSamplerId}, WhiteTexture=ready, ContextRestore=verified.",
                helperResult.OnRenderMaterial,
                helperResult.Output,
                helperResult.ShaderDescriptor,
                helperResult.MaterialConstantId,
                helperResult.InstanceConstantId,
                helperResult.ModelConstantId,
                worldConstantId,
                helperResult.NormalSamplerId,
                helperResult.IndexSamplerId,
                helperResult.TableSamplerId
            );
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Underpaint] Native initialization failed; submission is disabled.");
        }

        return result;
    }

    private static Matrix4x4 GetMainViewMatrix()
    {
        var manager = Manager.Instance();
        var camera = manager == null ? null : manager->Views[ExpectedMainView].SubViews[MainTransformSubView].Camera;
        if (camera == null)
            throw new InvalidOperationException("The native main-view camera is not available.");

        var view = *(Matrix4x4*)&camera->ViewMatrix;
        view.M14 = 0;
        view.M24 = 0;
        view.M34 = 0;
        view.M44 = 1;
        return view;
    }

    private delegate nint BuildPassesDelegate(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount);
}
