using System.Numerics;

namespace Underpaint;

/// <summary>
/// Raw native G-buffer material tuple. G0 RGB is replaced by the geometry normal and G2 RGB by
/// vertex or texture albedo.
/// </summary>
public readonly record struct GBufferMaterial(Vector4 G0, Vector4 G1, Vector4 G2, Vector4 G3, Vector4 G4, byte Stencil)
{
    public static GBufferMaterial Default =>
        new(
            new Vector4(0.5f, 1f, 0.5f, 128f / 255f),
            new Vector4(243f / 255f, 216f / 255f, 0f, 0f),
            new Vector4(52f / 255f, 42f / 255f, 35f / 255f, 1f),
            new Vector4(65504f, 0f, 0f, 1f),
            new Vector4(127f / 255f, 1f, 127f / 255f, 0f),
            0x10
        );
}
