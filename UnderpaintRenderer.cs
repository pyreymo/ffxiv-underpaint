using Dalamud.Plugin.Services;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Owns native G-buffer hooks and accepts independent opaque and semitransparent submissions.</summary>
public sealed class UnderpaintRenderer : IDisposable
{
    private readonly D3D11GBufferBackend backend;

    public UnderpaintDiagnostics Diagnostics { get; }

    public UnderpaintRenderer(IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        backend = new D3D11GBufferBackend(gameInteropProvider, log);
        Diagnostics = new UnderpaintDiagnostics(backend);
    }

    public GBufferDrawList DrawOpaque(GBufferMaterial? material = null) =>
        new(
            backend,
            GBufferTarget.Opaque,
            material ?? GBufferMaterial.Default,
            SemitransparentLighting.Default
        );

    public GBufferDrawList DrawSemitransparent(
        GBufferMaterial? material = null,
        SemitransparentLighting? lighting = null
    ) =>
        new(
            backend,
            GBufferTarget.Semitransparent,
            material ?? GBufferMaterial.Default,
            (lighting ?? SemitransparentLighting.Default).Clamped()
        );

    public void Clear(GBufferTarget target) => backend.Clear(target);

    public void Dispose()
    {
        backend.Dispose();
    }
}
