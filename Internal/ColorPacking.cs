using System.Numerics;
using System.Runtime.CompilerServices;

namespace Underpaint.Internal;

internal static class ColorPacking
{
    private const float ByteToFloat = 1f / 255f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 ToVector4(this uint color) =>
        new(
            (color & 0xFF) * ByteToFloat,
            ((color >> 8) & 0xFF) * ByteToFloat,
            ((color >> 16) & 0xFF) * ByteToFloat,
            ((color >> 24) & 0xFF) * ByteToFloat
        );
}
