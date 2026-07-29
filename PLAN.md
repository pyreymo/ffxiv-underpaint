# Underpaint Native Transparency Sorting Plan

Last updated: 2026-07-29

## Goal

Find a legal engine-backed path that gives Underpaint rectangles and arbitrary three-point triangles native
camera-depth ordering for transparent rendering.

The current low-level pass-builder backend remains in place until a candidate proves visual behavior, primitive
semantics, lifecycle ownership, and acceptable cost. The current investigation uses normal AVFX only for its
game-owned identity, lifecycle, producer, and sorting path. Underpaint must provide the primitive geometry and semantic
inputs; an authored rectangle AVFX is not the implementation.

Detailed static traces, addresses, runtime-probe results, and investigation history are archived in
[`docs/AVFX_SORTING_RESEARCH.md`](docs/AVFX_SORTING_RESEARCH.md).

## Product Boundary

- Consumers work with semantic retained drawables, not native render commands or GPU resources.
- AVFX and `VfxObject` may be used as an internal host if their ownership remains private to Underpaint.
- The public API must not expose AVFX, Apricot, `DocumentInstance`, resource redirection, native handles, model indices,
  shaders, materials, vertex buffers, or index buffers.
- Rectangle remains a shared unit mesh plus per-drawable dimensions and transform.
- Triangle must ultimately support arbitrary three-point geometry. The unit equilateral triangle contract currently in
  `docs/PRIMITIVE_API.md` is superseded and requires a separate API redesign before implementation is complete.
- The host AVFX is not the geometry source. A design that requires one authored AVFX resource per shape, one generated
  AVFX resource per changing drawable, or per-frame mutation of a shared AVFX resource does not satisfy the primitive
  model.

## Required Results

The following are independent results and must be reported separately:

1. Correct back-to-front ordering among Underpaint primitives.
2. Correct depth testing against opaque scene geometry.
3. Correct ordering relative to native transparent objects in the same producer category and draw layer.
4. Correct ordering of transparent faces within one primitive where the chosen geometry contains overlapping faces.

An object-level native host is not assumed to solve face ordering. Evidence from one AVFX category must not be
generalized to all native transparent rendering.

## Hard Constraints

- Do not implement CPU distance sorting or manually encode camera depth into `Context.SortKey`.
- Do not append foreign records to native frame containers or retain frame/job pointers across frames.
- Do not forge `DocumentInstance`, `BgObject`, model, submesh, resource, or Apricot slot indices.
- Do not copy or hand-sort expanded native command packets.
- Do not call native render builders from arbitrary Dalamud callbacks.
- Do not turn the existing sorting probe into a production lifecycle component.
- Keep A/B tests single-variable and bounded.
- Prove producer, container, sorting unit, worker behavior, final consumption, allocator, thread, and lifetime ownership
  before modifying a native queue or implementing a persistent host.

## Established Decisions

### Current Backend

Evidence: current checkout code, prior static analysis, and user runtime confirmation.

- Underpaint injects one native pass-builder call per primitive at the existing `ModelRenderer` rendezvous.
- Different primitives currently enter the same `Context` with the same `SortKey`.
- Their transparency order follows publication/append order; there is no camera-depth stage.
- Existing opaque-scene depth behavior does not prove transparent sorting.
- Repeating caller-order swaps has no remaining diagnostic value.

### BgObject Rejected

Evidence: local FFCS plus static IDA trace of the verified global binary.

- The normal `BgObject` model path partitions visible objects by culling index.
- `GeometryInstancingRenderer` and `BGInstancingRenderer` regroup work by renderer slot and material/resource identity.
- No camera-depth key or comparator exists in the traced producer chain.
- Same-resource objects may be emitted as one instanced draw, so one `BgObject` per primitive does not create one
  camera-sorted unit.
- `BgObject` is rejected for transparency sorting. Do not implement or runtime-probe a `BgObject` host for this goal.

### Normal AVFX Passed The Ordering Bridge

Evidence: static IDA analysis followed by bounded runtime capture and user runtime confirmation.

- Normal `VfxObject.Create` instances receive real game-owned `VfxResourceInstance` and Apricot
  `DocumentInstance` identities.
- AVFX `DrawLayerType`, not `DrawOrderType`, selects the producer category.
- Categories 0-11 use the established camera-depth producer. Category 12 uses a priority key.
- The existing category-2 sample has `DrawLayerType = 2`, `DrawOrderType = 0`, and `SoftKeyOffset = 0`.
- Swapping only two instance positions reversed Apricot rank and final command execution order together.
- Across 50 comparable frames and 1,024 captured commands, the probe observed zero order failures and zero missing
  executions, including both observed `Start=0/1, Stride=16` worker subsequences.
- This proves category-2 producer rank propagation to final execution for the observed case. It does not yet prove
  visible model-particle overlap, primitive semantics, production lifecycle, or scale cost.

### AVFX Model Builder Provides A Descriptor Seam

Evidence: current external asset parsing plus static IDA analysis of the current global and CN binaries.

- `no-binder.avfx` is useful only as a normal category-2 host sample. It contains ten particles and three model blocks;
  the only populated model has 55 vertices and 80 triangles. It is not a minimal rectangle resource and must not become
  Underpaint's geometry implementation.
- Both AVFX `Model` and `LightModel` particles resolve a resource model record and synchronously call the same native
  model builder from the real document render scope.
- The builder receives a stack descriptor containing the model record, a complete 3x4 transform, and the remaining
  particle/material inputs. It reads the descriptor synchronously, binds the model record's vertex/index resources,
  writes constants, and emits the native draw before returning.
- A scoped detour can copy that stack descriptor and replace semantic inputs for one real game-owned document without
  mutating the shared AVFX resource, changing Apricot slots or indices, retaining frame pointers, or writing a queue.
- Transform substitution requires no new native resource and is the first runtime gate. Geometry substitution remains
  blocked until the AVFX model wrapper's creation, upload, reference, and release protocol is proven.

### Temporary Sorting Probe Retired

- The old Debug-only sorting probe exists only in Git history and has been removed from the current source.
- EH no longer consumes the probe revision or exposes its sorting controls and category-12 asset.
- Re-arm and Stop experiments crashed because the probe cleared documents while active slot work could still consume
  them. This invalidates the probe's transition scheme, not the normal category-2 AVFX route.
- Static analysis already establishes category 12 as a priority path. Do not repair re-arm or repeat category-12 tests
  unless a future decision depends on evidence unavailable by a safer route.

## Current State

- Active Underpaint checkout: root `ffxiv-underpaint` repository, branch `probe/avfx-native-sort`.
- The nested `event-horizon/libraries/Underpaint` checkout is an EH consumer submodule and must not be edited directly.
- Phase 1, normal AVFX identity and producer-to-final-execution ordering, is complete for category 2.
- The external authored-rectangle experiment has been rejected because it would test an asset as the implementation,
  not Underpaint geometry carried by an AVFX lifecycle.
- A Debug-only transform descriptor probe now exists in Underpaint. It uses the real `VfxObject` and document, preserves
  the original model resource, and changes only the copied 3x4 transform during the scoped synchronous builder call.
- The transform probe has passed build validation but has not been run in game.
- No Underpaint-owned AVFX geometry resource has been created or substituted.
- No persistent Underpaint AVFX host, resource redirector, or public AVFX API has been implemented.
- The next question is whether the descriptor seam is correctly scoped to one real document and visibly consumes the
  substituted transform.

## Next Action: Scoped Transform Descriptor A/B

Event Horizon may supply its existing `no-binder.avfx` redirect and Debug control, but the descriptor hook is owned by
Underpaint. The asset is only a known category-2 host containing model draws; its authored geometry is not evidence for
Underpaint primitive capability.

### Experiment Setup

1. Start one normal category-2 `no-binder` host through the Debug probe at a visible world position.
2. Keep its AVFX resource, model record, particle state, color, alpha, depth settings, and creation order unchanged.
3. In only that game-owned document's model-builder scope, copy the stack descriptor and add a clearly visible offset to
   the descriptor's 3x4 transform translation.
4. Confirm the original descriptor is never modified and the copied transform lives only for the synchronous original
   builder call.
5. Stop through the normal static-VFX remove path. Do not use the retired sorting probe's graphics-task transition.

### First Visual Gate

Record these results separately:

1. The normal host is visible and stable before substitution.
2. Only model draws belonging to the tracked document report builder hits.
3. Enabling the copied transform offset moves only those model draws by the requested amount.
4. Stopping and recreating the host does not crash, leave a visible object, or affect normal hidden-player markers.
5. Plugin unload with the probe active removes the host through the normal lifecycle without a crash.

Stop after this gate if the scoped transform is not visibly consumed or lifecycle cleanup fails. Do not add custom
geometry to work around an unproven descriptor seam.

## Follow-Up Gates

These gates run only after the scoped transform gate passes.

### Primitive Semantics

- Prove the native AVFX model wrapper create/upload/reference/release protocol before substituting geometry.
- Substitute one Underpaint-owned fixed unit mesh while retaining the normal document, particle state, and sort path.
- Validate per-instance transform, tint, smooth alpha, and rectangle dimensions without mutating the shared AVFX
  resource.
- Determine how one persistent host can express arbitrary three-point triangle geometry, including affine/shear freedom
  unavailable through ordinary TRS.
- Reject the route if arbitrary triangles require per-frame shared-resource mutation or one generated resource per
  changing drawable.
- Test ordering against a native transparent object in the same AVFX category and draw layer.
- If a primitive resource contains overlapping transparent faces, test internal face order independently from
  document-level host order.

### Lifecycle

- Validate create, load, update, hide/show, resource reload, destroy, and repeated recreation through the established
  scheduler-managed lifecycle.
- Validate zone change, logout, title screen, plugin reload, and plugin unload separately.
- Require explicit and symmetric ownership for world registration, culling, render state, resource references, cleanup,
  and destruction.
- Do not use the temporary sorting probe's re-arm or Stop behavior as lifecycle evidence.

### Cost

Measure 8, 32, 128, and 512 simultaneous one-host-per-primitive instances only after visual, semantic, and lifecycle
gates pass. Record:

- CPU time and p95/p99/max frame cost.
- Native allocation count and resource residency.
- Worker participation and command count.
- Load/reload spikes and cleanup cost.

Reject or demote the route if one-host-per-primitive cost is unacceptable at target counts.

## AVFX Decision Gate

AVFX may proceed to an Underpaint integration design only if all of the following are true:

- Each drawable has one real, stable game-owned sorting identity in category 0-11.
- Camera-depth changes produce correct visible back-to-front ordering through final execution.
- Downstream particle or draw batching does not destroy independent document order.
- Rectangle and arbitrary three-point triangle semantics are expressible from Underpaint-owned geometry and descriptor
  inputs without authored per-shape AVFX, per-frame mutation of a shared AVFX resource, or one generated AVFX resource
  per changing drawable.
- Opaque depth testing and same-category native transparent ordering satisfy their separate visual gates.
- Create, update, hide/show, reload, cleanup, and destruction ownership are explicit and symmetric.
- Cost is acceptable at the measured instance counts.
- The route does not violate any hard constraint above.

Reject or demote AVFX if any required result depends on forged indices, borrowed frame storage, hand-authored command
keys, copied command packets, per-frame resource rewriting, or an unsafe lifecycle transition.

## Integration Sequence

If AVFX passes every gate:

1. Redesign the retained triangle API to represent arbitrary three-point geometry. Do not silently retain the currently
   documented unit-equilateral-only contract.
2. Implement one internal AVFX lifecycle host and Underpaint-owned geometry path in the root repository with normal
   create/load/update/hide/show/cleanup/destroy behavior.
3. Keep AVFX and resource-redirection details private. Preserve semantic drawable and latest-wins frame publication
   boundaries.
4. Add geometry substitution only after the plain host lifecycle is validated. Do not replace the current backend in
   the same step.
5. Build, runtime-validate, commit, and push the Underpaint revision. Verify the remote branch hash.
6. Only then update EH's Underpaint submodule, add the minimal consumer integration, build and runtime-validate EH,
   commit and push the parent repository, and verify its remote branch hash.

## Fallback

Investigate non-instanced `CharacterBase -> Render::Model -> ModelRenderer` only if AVFX fails a required geometry,
ordering, lifecycle, or cost gate.

Stop that trace immediately if no producer record before `ModelRenderer.OnRenderMaterial` contains both stable model
identity and camera-depth or world-position input. Character glass/transparency branches may be documented, but their
skeleton, model-slot, material callback, dither, and pass semantics are not reusable evidence without an independent
ownership and sorting trace.

## Update Protocol

- Keep this file limited to current goals, established decisions, active gates, and next actions.
- Put addresses, full traces, crash analysis, completed experiment detail, and superseded paths in
  `docs/AVFX_SORTING_RESEARCH.md`.
- Update `Last updated`, `Current State`, and `Next Action` when a decision gate changes.
- Never turn an established conclusion back into a proposed experiment unless its evidence is explicitly invalidated.
