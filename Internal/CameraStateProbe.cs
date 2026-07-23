using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Shader;

namespace Underpaint.Internal;

internal static unsafe class CameraStateProbe
{
    private const int ConstantOffset = 0x940;
    private const int CameraConstantId = 24;

    internal static string Describe(byte* context)
    {
        var buffer = *(ConstantBuffer**)(context + ConstantOffset + CameraConstantId * sizeof(nint));
        if (buffer == null)
            return "buffer=null";

        var data = buffer->TryGetSourcePointer();
        if (data == null)
            return $"buffer=0x{(nint)buffer:X}, bytes={buffer->ByteSize}, source=null";

        var camera = (CameraParameter*)data;
        return $"buffer=0x{(nint)buffer:X}, bytes={buffer->ByteSize}, "
            + $"viewX={camera->ViewMatrixX}, viewY={camera->ViewMatrixY}, viewZ={camera->ViewMatrixZ}, "
            + $"inverseX={camera->InverseViewMatrixX}, inverseY={camera->InverseViewMatrixY}, "
            + $"inverseZ={camera->InverseViewMatrixZ}, eye={camera->EyePosition}";
    }
}
