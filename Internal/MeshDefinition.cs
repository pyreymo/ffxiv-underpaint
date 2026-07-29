using System.Numerics;

namespace Underpaint.Internal;

internal enum MeshKind
{
    Triangle,
    Rectangle,
    Sphere,
}

internal readonly record struct MeshVertex(Vector3 Position, Vector3 Normal, Vector2 TextureCoordinate);

internal sealed class MeshDefinition
{
    private static readonly Vector2 FixedTextureCoordinate = new(0.5f, 0.5f);
    private static readonly MeshDefinition Triangle = CreateTriangle();
    private static readonly MeshDefinition Rectangle = CreateRectangle();
    private static readonly MeshDefinition Sphere = CreateSphere();
    private static readonly MeshDefinition[] Definitions = [Triangle, Rectangle, Sphere];

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

    private static MeshDefinition CreateSphere()
    {
        const int longitudeSegments = 32;
        const int latitudeSegments = 17;
        const int faceCount = longitudeSegments * (latitudeSegments - 1) * 2;
        var vertices = new MeshVertex[faceCount * 3];
        var indices = new ushort[faceCount * 3];
        var writeIndex = 0;
        var north = new Vector3(0f, 0f, 0.5f);
        var south = new Vector3(0f, 0f, -0.5f);

        for (var longitude = 0; longitude < longitudeSegments; longitude++)
        {
            var next = (longitude + 1) % longitudeSegments;
            WriteTriangle(north, Point(1, longitude), Point(1, next));
        }

        for (var latitude = 1; latitude < latitudeSegments - 1; latitude++)
        {
            for (var longitude = 0; longitude < longitudeSegments; longitude++)
            {
                var next = (longitude + 1) % longitudeSegments;
                var upperLeft = Point(latitude, longitude);
                var upperRight = Point(latitude, next);
                var lowerLeft = Point(latitude + 1, longitude);
                var lowerRight = Point(latitude + 1, next);
                WriteTriangle(upperLeft, lowerLeft, lowerRight);
                WriteTriangle(upperLeft, lowerRight, upperRight);
            }
        }

        for (var longitude = 0; longitude < longitudeSegments; longitude++)
        {
            var next = (longitude + 1) % longitudeSegments;
            WriteTriangle(south, Point(latitudeSegments - 1, next), Point(latitudeSegments - 1, longitude));
        }

        return new MeshDefinition(MeshKind.Sphere, vertices, indices);

        Vector3 Point(int latitude, int longitude)
        {
            var polar = MathF.PI * latitude / latitudeSegments;
            var azimuth = 2f * MathF.PI * longitude / longitudeSegments;
            var radial = 0.5f * MathF.Sin(polar);
            return new Vector3(radial * MathF.Cos(azimuth), radial * MathF.Sin(azimuth), 0.5f * MathF.Cos(polar));
        }

        void WriteTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (Vector3.Dot(normal, a + b + c) < 0f)
            {
                (b, c) = (c, b);
                normal = -normal;
            }
            vertices[writeIndex] = new MeshVertex(a, normal, FixedTextureCoordinate);
            indices[writeIndex] = (ushort)writeIndex++;
            vertices[writeIndex] = new MeshVertex(b, normal, FixedTextureCoordinate);
            indices[writeIndex] = (ushort)writeIndex++;
            vertices[writeIndex] = new MeshVertex(c, normal, FixedTextureCoordinate);
            indices[writeIndex] = (ushort)writeIndex++;
        }
    }
}
