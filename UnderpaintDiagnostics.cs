using System.Numerics;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Runtime-only controls for isolating projection and native G-buffer state mismatches.</summary>
public sealed class UnderpaintDiagnostics
{
    private readonly D3D11GBufferBackend backend;

    internal UnderpaintDiagnostics(D3D11GBufferBackend backend)
    {
        this.backend = backend;
    }

    /// <summary>Additional opaque clip-space offset expressed in pixels of the injected viewport.</summary>
    public Vector2 OpaqueJitterPixels
    {
        get => backend.GetOpaqueJitterPixels();
        set => backend.SetOpaqueJitterPixels(value);
    }

    /// <summary>Forces opaque coverage to one so Bayer dithering cannot affect an isolation test.</summary>
    public bool ForceOpaqueAlpha
    {
        get => backend.GetForceOpaqueAlpha();
        set => backend.SetForceOpaqueAlpha(value);
    }

    /// <summary>
    /// Positive reverse-Z rasterizer depth bias, in R24 depth units, used to isolate equal-depth
    /// surface competition. Zero matches the native opaque state.
    /// </summary>
    public int OpaqueDepthBias
    {
        get => backend.GetOpaqueDepthBias();
        set => backend.SetOpaqueDepthBias(value);
    }

    /// <summary>Captures the next native draw issued while the opaque G-buffer is bound.</summary>
    public void RequestOpaqueDrawSnapshot() => backend.RequestOpaqueDrawSnapshot();

    /// <summary>Returns and consumes the latest completed one-shot snapshot.</summary>
    public bool TryTakeOpaqueDrawSnapshot(out NativeDrawSnapshot snapshot) =>
        backend.TryTakeOpaqueDrawSnapshot(out snapshot);

#if DEBUG
    /// <summary>Starts a bounded, read-only capture of native semitransparent Stage A/C draws.</summary>
    public void BeginTransparentDrawCapture(int maxDraws = 128, int maxStageAFrames = 4) =>
        backend.BeginTransparentDrawCapture(maxDraws, maxStageAFrames);

    public void CancelTransparentDrawCapture(string reason = "cancelled") =>
        backend.CancelTransparentDrawCapture(reason);

    public TransparentDrawCaptureStatus TransparentDrawCaptureStatus =>
        backend.GetTransparentDrawCaptureStatus();

    public bool TryTakeTransparentDrawCapture(out TransparentDrawCapture capture) =>
        backend.TryTakeTransparentDrawCapture(out capture);
#endif
}

public sealed record NativeDrawSnapshot(
    long Sequence,
    DateTimeOffset CapturedAt,
    IReadOnlyList<NativeViewportSnapshot> Viewports,
    IReadOnlyList<NativeScissorSnapshot> Scissors,
    NativeRasterizerSnapshot? Rasterizer,
    NativeDepthStencilSnapshot? DepthStencil,
    nint VertexShader,
    IReadOnlyList<NativeConstantBufferSnapshot> VertexConstantBuffers,
    Matrix4x4 ControlViewProjection,
    Matrix4x4? SceneViewProjection,
    NativeCameraParameterSnapshot? CameraParameter,
    IReadOnlyList<string> RenderTargets,
    string DepthTarget
);

public readonly record struct NativeViewportSnapshot(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth,
    float MaxDepth
);

public readonly record struct NativeScissorSnapshot(int Left, int Top, int Right, int Bottom);

public sealed record NativeRasterizerSnapshot(
    string FillMode,
    string CullMode,
    bool FrontCounterClockwise,
    int DepthBias,
    float DepthBiasClamp,
    float SlopeScaledDepthBias,
    bool DepthClip,
    bool Scissor
);

public sealed record NativeDepthStencilSnapshot(
    bool DepthEnabled,
    string DepthWriteMask,
    string DepthComparison,
    bool StencilEnabled,
    int StencilReference
);

public sealed record NativeConstantBufferSnapshot(
    int Slot,
    nint Pointer,
    int ByteWidth,
    ulong ContentHash
);

public sealed record NativeCameraParameterSnapshot(
    int Slot,
    int ByteOffset,
    float MatchError,
    bool Transposed,
    Matrix4x4 ViewProjection,
    Matrix4x4 Projection,
    Matrix4x4 MainViewToProjection
);
