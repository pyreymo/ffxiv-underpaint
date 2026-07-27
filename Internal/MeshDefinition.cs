using System.Numerics;

namespace Underpaint.Internal;

internal enum MeshKind
{
    Triangle,
    Rectangle,
}

internal readonly record struct MeshVertex(Vector3 Position, Vector3 Normal, Vector2 TextureCoordinate);

internal sealed class MeshDefinition
{
    private static readonly Vector2 FixedTextureCoordinate = new(0.5f, 0.5f);
    private static readonly MeshDefinition Triangle = CreateTriangle();
    private static readonly MeshDefinition Rectangle = CreateRectangle();
    private static readonly MeshDefinition[] Definitions = [Triangle, Rectangle];

    internal MeshKind Kind { get; }
    internal MeshVertex[] Vertices { get; }
    internal ushort[] Indices { get; }

    private MeshDefinition(MeshKind kind, MeshVertex[] vertices, ushort[] indices)
    {
        Kind = kind;
        Vertices = vertices;
        Indices = indices;
    }

    internal static ReadOnlySpan<MeshDefinition> All => Definitions;

    private static MeshDefinition CreateTriangle()
    {
        var height = MathF.Sqrt(3f) / 2f;
        return new MeshDefinition(
            MeshKind.Triangle,
            [
                CreateVertex(new Vector3(-0.5f, -height / 3f, 0)),
                CreateVertex(new Vector3(0.5f, -height / 3f, 0)),
                CreateVertex(new Vector3(0, 2f * height / 3f, 0)),
            ],
            [0, 1, 2]
        );
    }

    private static MeshDefinition CreateRectangle() =>
        new(
            MeshKind.Rectangle,
            [
                CreateVertex(new Vector3(-0.5f, -0.5f, 0)),
                CreateVertex(new Vector3(0.5f, -0.5f, 0)),
                CreateVertex(new Vector3(0.5f, 0.5f, 0)),
                CreateVertex(new Vector3(-0.5f, 0.5f, 0)),
            ],
            [0, 1, 2, 0, 2, 3]
        );

    private static MeshVertex CreateVertex(Vector3 position) => new(position, Vector3.UnitZ, FixedTextureCoordinate);
}
