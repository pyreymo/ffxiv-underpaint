#if DEBUG
namespace Underpaint;

public enum TransparentDrawStage
{
    StageA,
    StageC,
}

public enum TransparentDrawCaptureState
{
    Idle,
    Capturing,
    Completed,
    Cancelled,
    Failed,
}

public sealed record TransparentDrawCaptureStatus(
    TransparentDrawCaptureState State,
    int CapturedDraws,
    int CapturedStageAFrames,
    string? Reason
);

public sealed record TransparentDrawCapture(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Reason,
    int CapturedStageAFrames,
    IReadOnlyList<TransparentNativeDrawSnapshot> Draws
);

public sealed record TransparentNativeDrawSnapshot(
    long Sequence,
    long Timestamp,
    TransparentDrawStage Stage,
    int ThreadId,
    string DrawType,
    uint ElementCount,
    uint InstanceCount,
    uint StartIndex,
    int BaseVertex,
    uint StartVertex,
    uint StartInstance,
    nint VertexShader,
    nint PixelShader,
    nint InputLayout,
    IReadOnlyList<TransparentVertexBufferSnapshot> VertexBuffers,
    TransparentIndexBufferSnapshot? IndexBuffer,
    IReadOnlyList<TransparentConstantBufferSnapshot> VertexConstantBuffers,
    IReadOnlyList<TransparentConstantBufferSnapshot> PixelConstantBuffers,
    IReadOnlyList<TransparentShaderResourceSnapshot> PixelShaderResources,
    IReadOnlyList<string> NativeStack
);

public readonly record struct TransparentVertexBufferSnapshot(
    int Slot,
    nint Buffer,
    int Stride,
    int Offset
);

public readonly record struct TransparentIndexBufferSnapshot(
    nint Buffer,
    string Format,
    int Offset
);

public readonly record struct TransparentConstantBufferSnapshot(
    int Slot,
    nint Buffer,
    int ByteWidth,
    ulong? ContentHash
);

public readonly record struct TransparentShaderResourceSnapshot(int Slot, nint View, nint Resource);
#endif
