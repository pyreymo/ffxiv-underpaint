using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal sealed unsafe class NativeBackend : IDisposable
{
    private const string BuildPassesSignature = "44 89 4C 24 ?? 44 89 44 24 ?? 53 56 57 41 54 41 55";

    // Observed rendezvous used by the archived native prototype. This hook verifies it again at runtime
    // before later commits are allowed to submit Underpaint geometry there.
    private const int ExpectedMainView = 30;
    private const int ExpectedMainSubView = 11;

    private readonly Hook<BuildPassesDelegate> buildPassesHook;
    private readonly IPluginLog log;
    private int loggedFirstCall;
    private int loggedMainRendezvous;

    internal NativeBackend(IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.log = log;
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

        if (
            context->ViewIndex == ExpectedMainView
            && context->CurrentSubViewIndex == ExpectedMainSubView
            && modelRenderer != 0
            && materialParameters != 0
            && *(nint*)materialParameters != 0
            && Volatile.Read(ref loggedMainRendezvous) == 0
            && Interlocked.CompareExchange(ref loggedMainRendezvous, 1, 0) == 0
        )
        {
            log.Information(
                "[Underpaint] Native main-view rendezvous verified at view {View}, subview {SubView}.",
                context->ViewIndex,
                context->CurrentSubViewIndex
            );
        }

        return result;
    }

    private delegate nint BuildPassesDelegate(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount);
}
