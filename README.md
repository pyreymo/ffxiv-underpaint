# Underpaint

Underpaint (`ffxiv-underpaint`) is a small Dalamud library for inserting plugin-owned world geometry into FFXIV's native opaque and semitransparent G-buffer passes.

It is independent from Pictomancy. A plugin may use both, but Underpaint does not initialize, call, or reference Pictomancy.

## Minimal use

```csharp
private readonly UnderpaintRenderer underpaint = new(gameInteropProvider, pluginLog);

using (var draw = underpaint.DrawOpaque())
{
    draw.AddSphere(center, radius, 0xFFFFFFFF);
}

using (var draw = underpaint.DrawSemitransparent(
    lighting: new SemitransparentLighting(1f, 2f, 1f)))
{
    draw.AddFanFilled(center, 0f, 5f, -1f, 1f, 0x80FF40FF);
}
```

Disposing a `GBufferDrawList` publishes an immutable snapshot. Each target is latest-wins and retains its last snapshot until replaced or explicitly cleared:

```csharp
underpaint.Clear(GBufferTarget.Opaque);
underpaint.Clear(GBufferTarget.Semitransparent);
```

Dispose `UnderpaintRenderer` before the plugin unloads. Do not retain a draw list after disposal.

## Projection diagnostics

`UnderpaintRenderer.Diagnostics` provides runtime-only isolation controls. `OpaqueJitterPixels` applies a signed pixel offset in clip space, while `ForceOpaqueAlpha` removes Bayer coverage from opaque tests. `RequestOpaqueDrawSnapshot()` captures the next native draw while the opaque G-buffer is bound; consume the result with `TryTakeOpaqueDrawSnapshot()`.

The one-shot snapshot records native viewport, scissor, rasterizer/depth state, render-target formats, VS constant-buffer hashes, and locates an aligned FFXIV `CameraParameter` block inside larger VS constant buffers when present. Constant-buffer readback intentionally stalls the GPU and must not be requested every frame.

## Current shape support

- filled triangle and quad;
- horizontal fan/ring sector;
- smooth mesh sphere;
- textured world quad from a native D3D11 shader-resource-view pointer.

The textured overload retains the COM resource until the published snapshot is replaced.

## Status

Underpaint currently owns the extracted, functional prototype. It writes/tests native scene depth and participates in native lighting, but it is not yet visually equivalent to native game geometry in every pass. In particular, motion vectors/TAA identity, shadow passes, and correct ordering against hair, beards, water, and other transparent surfaces remain open work. See [ARCHITECTURE.md](ARCHITECTURE.md) and [RENDER_PROTOCOL.md](RENDER_PROTOCOL.md).

An experimental route through the game's model and background-object material builders was retired after static and runtime analysis showed that neither is a generic primitive submission API: the model builder requires the ModelRenderer six-pass shader protocol, while the matching background builder traverses a complete BgParts resource graph. Underpaint does not fabricate those scene objects; plugin-owned basic geometry remains on the explicit G-buffer backend described above.
