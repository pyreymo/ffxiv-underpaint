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

## Current shape support

- filled triangle and quad;
- horizontal fan/ring sector;
- smooth mesh sphere;
- textured world quad from a native D3D11 shader-resource-view pointer.

The textured overload retains the COM resource until the published snapshot is replaced.

## Status

Underpaint currently owns the extracted, functional prototype. It writes/tests native scene depth and participates in native lighting, but it is not yet visually equivalent to native game geometry in every pass. In particular, motion vectors/TAA identity, shadow passes, and correct ordering against hair, beards, water, and other transparent surfaces remain open work. See [ARCHITECTURE.md](ARCHITECTURE.md) and [RENDER_PROTOCOL.md](RENDER_PROTOCOL.md).
