# Underpaint architecture

## Ownership boundary

The public layer owns submission semantics:

- `UnderpaintRenderer` owns one backend and its hooks/resources.
- `GBufferDrawList` collects CPU geometry for exactly one target and one material tuple.
- `GBufferMaterial` describes the raw native G-buffer constants.
- `SemitransparentLighting` describes the temporary lighting used by the semitransparent composite.
- `GBufferTarget` identifies independent opaque and semitransparent streams.

`Internal/D3D11GBufferBackend` owns all game-specific behavior: pass recognition, D3D11 objects, shaders, command recording, native injection, and snapshot lifetime. Consumers must not know hook addresses or render-target layouts.

Pictomancy remains an overlay/VFX dependency of EventHorizon, but no Pictomancy type crosses Underpaint's project boundary.

## State model

Opaque and semitransparent submissions are separate latest-wins slots. Publishing one never changes or clears the other. A draw-list disposal atomically replaces only its selected slot; an empty disposed list clears that slot.

A published frame owns its draw commands and retained texture resources. Frames are reference-counted because the semitransparent path spans two native stages. Replacing the published semitransparent frame cannot change the frame already selected for an in-progress Stage A/Stage C cycle.

## Thread and lifetime rules

- Submission may occur independently from the render hook; replacement is protected by the backend state lock.
- Hook callbacks never borrow caller-owned lists or mutable material state.
- `GBufferDrawList.Dispose` is the commit point.
- `UnderpaintRenderer.Dispose` disables hooks before releasing D3D resources and published frames.
- A plugin owns exactly one renderer instance. Multiple instances would install competing hooks on the same D3D11 context and are unsupported.

## Dependency direction

```text
EventHorizon / another plugin
    |-- Pictomancy (optional overlay and VFX)
    `-- Underpaint
          `-- Dalamud services + SharpDX + FFXIVClientStructs supplied by SDK
```

The demo window is an integration consumer of both libraries; it is not part of either backend's ownership model.

## Deliberate non-goals of this extraction

This change separates ownership and removes avoidable cross-backend state coupling. It does not guess at native motion-vector encoding, fabricate object history, or reorder the game's transparent renderer. Those require further runtime evidence and dedicated hook points.
