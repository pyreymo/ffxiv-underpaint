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

    private readonly Hook<BuildPassesDelegate> buildPassesHook;
    private readonly MaterialHelper materialHelper;
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
            resources.CreateConstants();
            var helperResult = materialHelper.Validate(
                (ModelRenderer*)modelRenderer,
                (byte*)context,
                resources.InstanceConstant,
                resources.ModelConstant
            );
            log.Information(
                "[Underpaint] Native constants and material helpers verified: OnRenderMaterial=0x{OnRenderMaterial:X}, "
                    + "Output40=0x{Output:X8}, Descriptor=0x{Descriptor:X}, "
                    + "InstanceConstantId={InstanceConstantId}, ModelConstantId={ModelConstantId}.",
                helperResult.OnRenderMaterial,
                helperResult.Output,
                helperResult.ShaderDescriptor,
                helperResult.InstanceConstantId,
                helperResult.ModelConstantId
            );
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Underpaint] Native initialization failed; submission is disabled.");
        }

        return result;
    }

    private delegate nint BuildPassesDelegate(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount);
}
