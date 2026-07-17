using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>
/// Collects world geometry for the next selected G-buffer pass.
/// Opaque geometry uses dithered coverage; semitransparent geometry uses continuous alpha in the native composite.
/// </summary>
public sealed class GBufferDrawList : IDisposable
{
    public const uint MaxFanSegments = 128;
    public const uint MaxSphereLatitudeSegments = 64;
    public const uint MaxSphereLongitudeSegments = 128;

    private const uint DefaultSphereLatitudeSegments = 8;
    private const uint DefaultSphereLongitudeSegments = 16;

    private readonly D3D11GBufferBackend backend;
    private readonly GBufferTarget target;
    private readonly GBufferMaterial material;
    private readonly SemitransparentLighting lighting;
    private readonly List<GBufferDrawCommand> commands = [];
    private bool disposed;

    internal GBufferDrawList(
        D3D11GBufferBackend backend,
        GBufferTarget target,
        GBufferMaterial material,
        SemitransparentLighting lighting
    )
    {
        this.backend = backend;
        this.target = target;
        this.material = material;
        this.lighting = lighting;
    }

    public void AddTriangleFilled(Vector3 a, Vector3 b, Vector3 c, uint color)
    {
        AddTriangleFilled(a, b, c, color, color, color);
    }

    public void AddTriangleFilled(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        uint colorA,
        uint colorB,
        uint colorC
    )
    {
        ThrowIfDisposed();
        var normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() < 1e-8f)
        {
            return;
        }

        normal = Vector3.Normalize(normal);
        commands.Add(
            new GBufferDrawCommand([
                new GBufferVertex(a, normal, colorA.ToVector4(), Vector2.Zero),
                new GBufferVertex(b, normal, colorB.ToVector4(), Vector2.Zero),
                new GBufferVertex(c, normal, colorC.ToVector4(), Vector2.Zero),
            ])
        );
    }

    public void AddQuadFilled(Vector3 a, Vector3 b, Vector3 c, Vector3 d, uint color)
    {
        AddQuadFilled(a, b, c, d, color, color, color, color);
    }

    public void AddQuadFilled(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        uint colorA,
        uint colorB,
        uint colorC,
        uint colorD
    )
    {
        ThrowIfDisposed();
        var normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() < 1e-8f)
        {
            return;
        }

        normal = Vector3.Normalize(normal);
        commands.Add(
            new GBufferDrawCommand([
                new GBufferVertex(a, normal, colorA.ToVector4(), Vector2.Zero),
                new GBufferVertex(b, normal, colorB.ToVector4(), Vector2.Zero),
                new GBufferVertex(c, normal, colorC.ToVector4(), Vector2.Zero),
                new GBufferVertex(a, normal, colorA.ToVector4(), Vector2.Zero),
                new GBufferVertex(c, normal, colorC.ToVector4(), Vector2.Zero),
                new GBufferVertex(d, normal, colorD.ToVector4(), Vector2.Zero),
            ])
        );
    }

    /// <summary>
    /// Adds a horizontal opaque fan or ring sector. Angles use the same convention as
    /// A zero segment count selects eight segments per radian.
    /// </summary>
    public void AddFanFilled(
        Vector3 origin,
        float innerRadius,
        float outerRadius,
        float minAngle,
        float maxAngle,
        uint innerColor,
        uint? outerColor = null,
        uint numSegments = 0
    )
    {
        ThrowIfDisposed();
        if (
            !float.IsFinite(innerRadius)
            || !float.IsFinite(outerRadius)
            || innerRadius < 0f
            || outerRadius <= innerRadius
        )
        {
            return;
        }

        var totalAngle = maxAngle - minAngle;
        if (!float.IsFinite(totalAngle) || MathF.Abs(totalAngle) < 1e-6f)
        {
            return;
        }

        if (numSegments == 0)
        {
            numSegments = (uint)MathF.Ceiling(MathF.Abs(totalAngle) * 8f);
        }

        numSegments = Math.Clamp(numSegments, 1u, MaxFanSegments);
        var verticesPerSegment = innerRadius > 0f ? 6 : 3;
        var vertices = new GBufferVertex[numSegments * verticesPerSegment];
        var normal = Vector3.UnitY;
        var inner = innerColor.ToVector4();
        var outer = (outerColor ?? innerColor).ToVector4();
        var angleStep = totalAngle / numSegments;
        var vertexIndex = 0;

        for (var segment = 0u; segment < numSegments; segment++)
        {
            var u0 = segment / (float)numSegments;
            var u1 = (segment + 1) / (float)numSegments;
            var angle0 = MathF.PI / 2f + minAngle + segment * angleStep;
            var angle1 = angle0 + angleStep;
            var direction0 = new Vector3(MathF.Cos(angle0), 0f, MathF.Sin(angle0));
            var direction1 = new Vector3(MathF.Cos(angle1), 0f, MathF.Sin(angle1));
            var outer0 = origin + outerRadius * direction0;
            var outer1 = origin + outerRadius * direction1;

            if (innerRadius <= 0f)
            {
                WriteUpwardTriangle(
                    origin,
                    inner,
                    new Vector2((u0 + u1) * 0.5f, 0f),
                    outer1,
                    outer,
                    new Vector2(u1, 1f),
                    outer0,
                    outer,
                    new Vector2(u0, 1f)
                );
                continue;
            }

            var inner0 = origin + innerRadius * direction0;
            var inner1 = origin + innerRadius * direction1;
            WriteUpwardTriangle(
                inner0,
                inner,
                new Vector2(u0, 0f),
                inner1,
                inner,
                new Vector2(u1, 0f),
                outer1,
                outer,
                new Vector2(u1, 1f)
            );
            WriteUpwardTriangle(
                inner0,
                inner,
                new Vector2(u0, 0f),
                outer1,
                outer,
                new Vector2(u1, 1f),
                outer0,
                outer,
                new Vector2(u0, 1f)
            );
        }

        commands.Add(new GBufferDrawCommand(vertices));
        return;

        void WriteUpwardTriangle(
            Vector3 a,
            Vector4 colorA,
            Vector2 uvA,
            Vector3 b,
            Vector4 colorB,
            Vector2 uvB,
            Vector3 c,
            Vector4 colorC,
            Vector2 uvC
        )
        {
            if (Vector3.Cross(b - a, c - a).Y < 0f)
            {
                (b, c) = (c, b);
                (colorB, colorC) = (colorC, colorB);
                (uvB, uvC) = (uvC, uvB);
            }

            vertices[vertexIndex++] = new GBufferVertex(a, normal, colorA, uvA);
            vertices[vertexIndex++] = new GBufferVertex(b, normal, colorB, uvB);
            vertices[vertexIndex++] = new GBufferVertex(c, normal, colorC, uvC);
        }
    }

    /// <summary>
    /// Adds a smooth-shaded UV sphere to the selected G-buffer. Latitude and longitude segment counts
    /// default to 8 and 16 respectively and are clamped to the supported range.
    /// </summary>
    public void AddSphere(
        Vector3 origin,
        float radius,
        uint color,
        uint latitudeSegments = DefaultSphereLatitudeSegments,
        uint longitudeSegments = DefaultSphereLongitudeSegments
    )
    {
        ThrowIfDisposed();
        if (
            !float.IsFinite(origin.X)
            || !float.IsFinite(origin.Y)
            || !float.IsFinite(origin.Z)
            || !float.IsFinite(radius)
            || radius <= 0f
        )
        {
            return;
        }

        latitudeSegments = Math.Clamp(latitudeSegments, 3u, MaxSphereLatitudeSegments);
        longitudeSegments = Math.Clamp(longitudeSegments, 3u, MaxSphereLongitudeSegments);

        var sphereColor = color.ToVector4();
        var vertices = new List<GBufferVertex>(D3D11GBufferBackend.MaxVerticesPerCommand);
        var top = Vector3.UnitY;
        var bottom = -Vector3.UnitY;

        for (var longitude = 0u; longitude < longitudeSegments; longitude++)
        {
            var direction0 = SphereDirection(1, longitude);
            var direction1 = SphereDirection(1, longitude + 1);
            AddTriangle(top, direction1, direction0);
        }

        for (var latitude = 1u; latitude < latitudeSegments - 1; latitude++)
        {
            for (var longitude = 0u; longitude < longitudeSegments; longitude++)
            {
                var upper0 = SphereDirection(latitude, longitude);
                var upper1 = SphereDirection(latitude, longitude + 1);
                var lower0 = SphereDirection(latitude + 1, longitude);
                var lower1 = SphereDirection(latitude + 1, longitude + 1);
                AddTriangle(upper0, lower1, lower0);
                AddTriangle(upper0, upper1, lower1);
            }
        }

        for (var longitude = 0u; longitude < longitudeSegments; longitude++)
        {
            var direction0 = SphereDirection(latitudeSegments - 1, longitude);
            var direction1 = SphereDirection(latitudeSegments - 1, longitude + 1);
            AddTriangle(bottom, direction0, direction1);
        }

        FlushVertices();
        return;

        Vector3 SphereDirection(uint latitude, uint longitude)
        {
            var latitudeAngle = MathF.PI / 2f - latitude * MathF.PI / latitudeSegments;
            var longitudeAngle = longitude * 2f * MathF.PI / longitudeSegments;
            var horizontalScale = MathF.Cos(latitudeAngle);
            return new Vector3(
                horizontalScale * MathF.Cos(longitudeAngle),
                MathF.Sin(latitudeAngle),
                horizontalScale * MathF.Sin(longitudeAngle)
            );
        }

        void AddTriangle(Vector3 normalA, Vector3 normalB, Vector3 normalC)
        {
            if (vertices.Count + 3 > D3D11GBufferBackend.MaxVerticesPerCommand)
            {
                FlushVertices();
            }

            vertices.Add(
                new GBufferVertex(origin + radius * normalA, normalA, sphereColor, Vector2.Zero)
            );
            vertices.Add(
                new GBufferVertex(origin + radius * normalB, normalB, sphereColor, Vector2.Zero)
            );
            vertices.Add(
                new GBufferVertex(origin + radius * normalC, normalC, sphereColor, Vector2.Zero)
            );
        }

        void FlushVertices()
        {
            if (vertices.Count == 0)
            {
                return;
            }

            commands.Add(new GBufferDrawCommand([.. vertices]));
            vertices.Clear();
        }
    }

    /// <summary>
    /// Adds an opaque or cutout textured world quad. U follows +right and V follows +down.
    /// </summary>
    public void AddImage(nint nativePtr, Vector3 center, Vector3 right, Vector3 down)
    {
        ThrowIfDisposed();
        if (nativePtr == 0)
        {
            return;
        }

        var normal = Vector3.Cross(right, down);
        if (normal.LengthSquared() < 1e-8f)
        {
            return;
        }

        normal = Vector3.Normalize(normal);
        var a = center - right / 2f - down / 2f;
        var b = center + right / 2f - down / 2f;
        var c = center + right / 2f + down / 2f;
        var d = center - right / 2f + down / 2f;

        Marshal.AddRef(nativePtr);
        ShaderResourceView? retainedTexture = null;
        try
        {
            retainedTexture = new ShaderResourceView(nativePtr);
            commands.Add(
                new GBufferDrawCommand(
                    [
                        new GBufferVertex(a, normal, Vector4.One, new Vector2(0f, 0f)),
                        new GBufferVertex(b, normal, Vector4.One, new Vector2(1f, 0f)),
                        new GBufferVertex(c, normal, Vector4.One, new Vector2(1f, 1f)),
                        new GBufferVertex(a, normal, Vector4.One, new Vector2(0f, 0f)),
                        new GBufferVertex(c, normal, Vector4.One, new Vector2(1f, 1f)),
                        new GBufferVertex(d, normal, Vector4.One, new Vector2(0f, 1f)),
                    ],
                    retainedTexture
                )
            );
            retainedTexture = null;
        }
        catch
        {
            if (retainedTexture != null)
            {
                retainedTexture.Dispose();
            }
            else
            {
                Marshal.Release(nativePtr);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        backend.Publish(target, commands, material, lighting);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
