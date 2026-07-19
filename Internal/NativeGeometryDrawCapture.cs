namespace Underpaint.Internal;

internal readonly record struct NativeGeometryVertexBufferBinding(
    int Slot,
    nint Buffer,
    int Stride,
    int Offset
);

internal readonly record struct NativeGeometryConstantBufferBinding(
    int Slot,
    nint Buffer,
    int ByteWidth,
    ulong? ContentHash
);

internal readonly record struct NativeGeometryShaderResourceBinding(
    int Slot,
    nint View,
    nint Resource
);

internal readonly record struct NativeGeometryDrawMatch(
    long Sequence,
    long Timestamp,
    int ThreadId,
    string Pass,
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
    IReadOnlyList<NativeGeometryVertexBufferBinding> VertexBuffers,
    nint IndexBuffer,
    string IndexFormat,
    int IndexOffset,
    IReadOnlyList<NativeGeometryConstantBufferBinding> VertexConstantBuffers,
    IReadOnlyList<NativeGeometryConstantBufferBinding> PixelConstantBuffers,
    IReadOnlyList<NativeGeometryShaderResourceBinding> ShaderResources
);

internal sealed record NativeGeometryDrawCapture(
    nint TargetVertexBuffer,
    nint TargetIndexBuffer,
    string Reason,
    IReadOnlyList<NativeGeometryDrawMatch> Draws
);
