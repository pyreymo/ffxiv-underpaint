# Render protocol

This document describes the actual protocol implemented by the backend. Names such as “Stage A” and “Stage C” are local labels, not game API names.

## Submission

1. The consumer creates an opaque or semitransparent draw list.
2. Shape calls append CPU-side triangle commands and retain any texture SRVs.
3. Disposing the list publishes an immutable frame into that target's latest-wins slot.
4. Publication does not immediately issue GPU work. The snapshot waits for a matching native pass.

This retained-snapshot behavior is why a submission can appear one presentation later than an immediate overlay. Splitting the code out of Pictomancy removes unrelated initialization/state overhead, but cannot by itself remove that protocol latency.

## Hook points

The backend hooks D3D11 immediate-context methods obtained from the game's active device context:

- `OMSetRenderTargets` and `OMSetRenderTargetsAndUnorderedAccessViews` recognize complete native opaque (five MRT) and semitransparent (four MRT) bindings.
- draw calls (`Draw`, `DrawIndexed`, and instanced variants) trigger injection only after a candidate binding is complete.
- `PSSetShaderResources` recognizes the later native semitransparent composite inputs and arms Stage C.

Recognition validates native resource identity and dimensions against `RenderTargetManager`; MRT count alone is not treated as sufficient.

## Opaque path

At the first draw after the complete opaque MRT binding, Underpaint records and executes its commands into the native G-buffer/depth targets. Vertex alpha and texture alpha use a rotating 4x4 Bayer coverage phase. The current implementation therefore depends on temporal accumulation for stable fractional coverage.

## Semitransparent path

The semitransparent path has a paired cycle:

1. Stage A selects and retains the current semitransparent frame, then writes geometry into the native semitransparent G-buffer and depth/stencil targets.
2. Native lighting work runs.
3. Shader-resource bindings identify the game's composite inputs and arm Stage C.
4. Immediately before the matching native draw, Stage C replays the exact retained frame with the selected lighting constants into the scene target.
5. The cycle reference is released.

The retained cycle is important: a new submission between Stage A and Stage C becomes the next published frame and cannot mix geometry/material data into the current cycle.

## Known missing native contracts

### Temporal stability

Underpaint does not currently write the game's velocity/motion-vector target and has no persistent per-object previous transform. It also has not yet proven whether the injected view-projection matrix must include the game's per-frame jitter at the selected hook. As a result, camera or object motion can shimmer and reconstructed depth can appear offset.

Required exploration:

- identify the native velocity target and encoding;
- capture current and previous jittered view-projection matrices at the exact opaque/semitransparent pass;
- add stable submission identity plus previous transform/history;
- determine history reset rules for teleports, zone changes, camera cuts, and skipped submissions;
- verify depth convention and viewport/render-resolution scaling at the injection point.

Likely hook seams are the native constant-buffer bindings/updates adjacent to the matched pass, not merely another draw-call hook.

### Transparent occlusion

Hair, beards, water, and similar surfaces do not share one ordinary depth contract. Some are alpha-tested, some are depth-reading transparent passes, and some are composited after Underpaint's Stage C. Writing the opaque depth buffer cannot make a later transparent pass blend correctly if that pass reads another depth/scene copy or was already rendered.

Required exploration:

- classify failures by material/pass (hair/beard, glass, water) rather than applying one global fix;
- trace their render-target, depth-SRV, stencil, and scene-color bindings;
- determine whether Underpaint must participate before those passes, update a copied depth resource, or composite after them;
- preserve native transparency order and premultiplied/straight-alpha conventions.

### Other gaps

- no shadow-map participation;
- no native material/object identifiers beyond the exposed raw G-buffer tuple;
- command upload still maps a small dynamic vertex buffer per command and replays semitransparent geometry in both stages;
- no public diagnostics for pass matches, dropped cycles, or GPU timing.

The next performance step should batch each frame into a growable upload buffer while retaining draw ranges for texture changes. It should be measured only after correctness instrumentation exists, because Stage A/Stage C replay is intentional protocol work rather than accidental Pictomancy overhead.
