using Dalamud.Plugin.Services;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Owns native G-buffer hooks and accepts independent opaque and semitransparent submissions.</summary>
public sealed class UnderpaintRenderer : IDisposable
{
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private readonly D3D11GBufferBackend backend;
    private NativeGeometrySubmissionBackend? nativeGeometryBackend;

    public UnderpaintDiagnostics Diagnostics { get; }

    public UnderpaintRenderer(IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;
        backend = new D3D11GBufferBackend(gameInteropProvider, log);
        Diagnostics = new UnderpaintDiagnostics(backend);
    }

    internal NativeGeometry CreateNativeGeometry(
        ReadOnlySpan<System.Numerics.Vector3> positions,
        ReadOnlySpan<ushort> indices
    ) => NativeGeometryBackend.CreateGeometry(positions, indices);

    internal NativeGeometrySubmissionResult SubmitNativeGeometry(
        nint modelRenderer,
        nint materialParameters,
        NativeGeometry geometry,
        NativePassBuilder submit
    ) => NativeGeometryBackend.Submit(modelRenderer, materialParameters, geometry, submit);

    internal NativeRigidInstance CreateNativeRigidInstance(
        NativeGeometry geometry,
        System.Numerics.Matrix4x4 currentWorldView
    ) => NativeGeometryBackend.CreateRigidInstance(geometry, currentWorldView);

    internal void ArmNativeGeometrySubmission(NativeGeometry geometry)
    {
        backend.BeginNativeGeometryDrawCapture(
            geometry.VertexBufferResource,
            geometry.IndexBufferResource
        );
        try
        {
            NativeGeometryBackend.ArmStandalone(geometry);
        }
        catch
        {
            backend.CompleteNativeGeometryDrawCapture("arm-failed");
            throw;
        }
    }

    internal void BeginNativeGeometryDrawCapture(NativeGeometry geometry) =>
        backend.BeginNativeGeometryDrawCapture(
            geometry.VertexBufferResource,
            geometry.IndexBufferResource
        );

    internal void CompleteNativeGeometryDrawCapture(string reason) =>
        backend.CompleteNativeGeometryDrawCapture(reason);

    internal void CancelNativeGeometrySubmission(string reason = "cancelled")
    {
        nativeGeometryBackend?.CancelStandalone();
        backend.CompleteNativeGeometryDrawCapture(reason);
    }

    internal bool TryTakeNativeGeometrySubmission(
        out NativeGeometryStandaloneSubmission submission
    ) => NativeGeometryBackend.TryTakeStandalone(out submission);

    internal bool TryTakeNativeGeometryDrawCapture(out NativeGeometryDrawCapture capture) =>
        backend.TryTakeNativeGeometryDrawCapture(out capture);

    private NativeGeometrySubmissionBackend NativeGeometryBackend =>
        nativeGeometryBackend ??= new NativeGeometrySubmissionBackend(gameInteropProvider, log);

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
        nativeGeometryBackend?.Dispose();
        backend.Dispose();
    }
}
