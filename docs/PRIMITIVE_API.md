# Retained Primitive API

## Geometry

All primitives use immutable shared runtime-owned topology and fixed center UV `(0.5, 0.5)`.

- Triangle: unit equilateral triangle in local XY, centroid at origin, front normal `+Z`.
- Rectangle: unit square in local XY, scaled by retained width and height.
- Sphere: unit diameter, centered at origin, 1,024 triangles with independent face normals.

## API

```csharp
public sealed class Renderer : IDisposable
{
    public TriangleDrawable CreateTriangle();
    public RectangleDrawable CreateRectangle(float width, float height);
    public SphereDrawable CreateSphere(float radius);
    public DecalRingDrawable CreateAnimatedDecalRing();
    public PrimitiveFrame BeginFrame();
}

public sealed class PrimitiveFrame : IDisposable
{
    public void DrawTriangle(
        TriangleDrawable drawable,
        Matrix4x4 transform,
        Vector3 sortingCenter,
        Vector3 color,
        float alpha = 1f
    );

    public void DrawRectangle(
        RectangleDrawable drawable,
        Matrix4x4 transform,
        Vector3 sortingCenter,
        Vector3 color,
        float alpha = 1f
    );

    public void DrawSphere(
        SphereDrawable drawable,
        Matrix4x4 transform,
        Vector3 sortingCenter,
        Vector3 color,
        float alpha = 1f
    );

    public void DrawAnimatedDecalRing(
        DecalRingDrawable drawable,
        Matrix4x4 transform,
        Vector3 color,
        float alpha = 1f
    );

    public void Publish();
}
```

`DecalRingDrawable` is backed by the native AVFX `DecalRing` particle rather than a custom mesh. Creation requires the
enabled `VFXEditorCN` assembly version pinned by the repository submodule. Underpaint creates one editable VFXEditorCN
document with a repeating `Color.Bri` curve over frames 0, 10, and 20; instances share that definition and retain
independent native `VfxObject` identities. Its transform must decompose into scale, rotation, and translation.

`sortingCenter` is not composed with `transform`. It selects the point used by AVFX document ordering; `transform`
alone controls actual geometry placement, rotation, scale, and shear. For centered built-in primitives, callers normally
use the geometry's world-space center for both values.

Rectangle dimensions and sphere radius are retained drawable properties. `Resize` changes the transform composed into
the next frame without replacing drawable identity or shared topology.

## Frame Semantics

- A published frame is a complete latest-wins snapshot.
- A drawable may appear at most once per frame.
- Missing drawables keep their native host/document identity but emit no model draw.
- An empty published frame hides all retained drawables.
- Disposing a drawable retires its native host; disposing the renderer invalidates all drawables and releases all hosts
  and shared topology.
- Caller-owned mutable memory is never retained.

AVFX paths, documents, model records, wrappers, native handles, shaders, materials, textures, and dither are not public
API.
