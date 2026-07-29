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

### Candidate Runtime Model

The candidate is not a runtime-mutated AVFX resource. It is an Underpaint drawable carried by an immutable, minimal AVFX
lifecycle shell:

- AVFX owns only the legal engine identity and execution envelope: `VfxObject`, `VfxResourceInstance`, real
  `DocumentInstance`, category-2 camera-depth rank, render-worker scheduling, and the synchronous model-builder call.
- Underpaint owns the dynamic draw payload: model record, vertex/index wrappers, topology, transform, dimensions, color,
  alpha, and the retained drawable lifetime that publishes those values.
- During the scoped model-builder call, Underpaint copies the transient descriptor and replaces its model record and
  semantic inputs. It does not mutate the shared AVFX resource, its model array, Apricot slots, or native queues.
- All drawables use the same immutable shell resource. Each drawable may own a normal shell instance so that the game
  assigns it an independent real `DocumentInstance`; there is no generated AVFX file per drawable or per shape.
- The shell's placeholder model exists only to reach the native model builder. Its authored geometry is never a product
  primitive and must not determine rectangle or triangle semantics.

This can reasonably be described as a runtime-modifiable primitive hosted by AVFX, but not as a modifiable AVFX asset.
The distinction is architectural: AVFX supplies lifecycle and sorting identity; Underpaint supplies the actual draw.

Reject this model if a neutral shell cannot produce one predictable model-builder call, or if Underpaint must keep
neutralizing arbitrary emitter, timeline, random, binder, particle, texture, or material behavior from a donor effect.
Cleaning `no-binder.avfx` one descriptor field at a time is explicitly not the implementation path.

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
- Do not convert donor-effect parameter suppression into a production payload pipeline.
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

### AVFX Model Builder Descriptor Seam Passed The First Runtime Gate

Evidence: current external asset parsing, static IDA analysis of the current global and CN binaries, and user runtime
confirmation in a closed test area.

- `no-binder.avfx` is useful only as a normal category-2 host sample. It contains ten particles and three model blocks;
  the only populated model has 55 vertices and 80 triangles. It is not a minimal rectangle resource and must not become
  Underpaint's geometry implementation.
- Both AVFX `Model` and `LightModel` particles resolve a resource model record and synchronously call the same native
  model builder from the real document render scope.
- The builder receives a stack descriptor containing the model record, a complete 3x4 transform, and the remaining
  particle/material inputs. It reads the descriptor synchronously, binds the model record's vertex/index resources,
  writes constants, and emits the native draw before returning.
- The Debug detour copied that stack descriptor and changed only its 3x4 transform translation. The original descriptor,
  shared AVFX resource, Apricot slots, model indices, and native queues were not modified.
- User runtime confirmation: only part of the visible `no-binder` effect moved. This is consistent with the asset
  structure: its `LightModel` particles pass through the hooked model builder, while its `Quad` and `Powder` particles
  do not. The visual observation alone does not identify each moved or stationary component by particle type.
- This proves that the current CN model builder consumes the copied transform and that the descriptor seam can alter
  model draws without rewriting the AVFX resource.
- The test area contained no second `no-binder` instance. Isolation from another instance using the same resource was
  therefore not tested and must not be claimed. That missing control does not block the next resource-ownership step;
  later multi-host ordering tests will exercise independent document identities directly.
- Static analysis of both current binaries has now closed the wrapper protocol: native constructors create 32-byte
  reference wrappers around uploaded kernel buffers, model cleanup releases them through virtual slot `+0x8`, and the
  builder does not retain the temporary model record or wrapper pointers.
- Wrapper creation/upload uses the global graphics device rather than a borrowed Apricot render context. Exact arbitrary
  thread legality is not proven, so the Debug gate confines creation/release to Start/Stop control paths and performs no
  resource operation inside the render detour.
- User runtime confirmation on CN: enabling the owned record changed the visible model-builder subset into triangles;
  no wrapper rejection or runtime failure was observed. The host effect has strong authored jitter, so this observation
  does not establish stable geometry or a controlled per-instance transform.
- After Stop, probe status reported `OwnedModels=1/1`. This confirms balanced model-owner accounting for the observed
  single Start/Stop run. Repeated recreation, territory transition, and unload remain separate lifecycle controls.

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
- A Debug-only owned-model probe creates a private 40-byte record and native vertex/index wrappers for one fixed
  three-vertex unit triangle per Start. During the scoped document render it always substitutes descriptor element 0
  without changing the AVFX resource or document identity.
- The owned record passed its first CN visual gate on the old donor: the model-builder subset became triangles, and the
  observed Stop returned model create/release accounting to `1/1`.
- No persistent Underpaint AVFX host, resource redirector, or public AVFX API has been implemented.
- Cross-instance isolation, repeated recreation, territory transition, and unload cleanup remain unverified.
- The controlled-transform experiment on `no-binder.avfx` confirmed that donor transform was one influence, but other
  authored inputs remained active. That control has been removed; further donor cleanup is stopped.
- The accepted architecture is now the immutable minimal lifecycle shell described above, not continued parasitism on
  `no-binder.avfx`.
- A user-authored one-node candidate was parsed as category 2 with exactly one scheduler, timeline, point emitter,
  `LightModel` particle, and model, plus zero binders. Its rotation came from three UV-scroll sets, not model transform.
- An offline TLV-preserving trim produced a 7,340-byte shell candidate with zero textures/UV sets, disabled texture and
  distortion inputs, fixed transform/color/alpha curves, ordinary blend mode, and a three-vertex placeholder model.
- The probe now always substitutes its Underpaint-owned model record. Donor geometry and controlled-transform A/B
  switches are no longer exposed.
- EH publishes the candidate through the dedicated virtual path `vfx/common/eff/underpaint_shell.avfx`; the original
  user asset remains untouched and the generated shell is now a project-owned asset.
- User runtime confirmation on CN: the game accepted the shell and displayed one static white Underpaint-owned triangle
  with stable opaque-scene depth handling, no animation, and no unrelated visible effect. This passes the shell's parser,
  visual-neutrality, and geometry-stability gate. It does not yet prove transparent alpha, two-host ordering, exact
  model-builder call cardinality, or transition lifecycle.
- A bounded Debug probe now has two independently selectable animation modes. Color mode updates only
  `VfxObject.Color` over time. Vertex mode creates one native dynamic AVFX vertex wrapper per Start and writes changing
  positions through its render-scope source pointer without recreating the host, document, model record, or wrapper.
  Neither animation mode has runtime confirmation yet.

## Next Action: Bounded Retained-Backend Experiment

The immutable shell gate has passed. The next work is an explicitly Debug-only backend experiment that consumes the
existing retained `FrameCommand` snapshots without changing the public API or replacing the current backend.

### Required Shell Properties

1. Category 2 with one scheduler, timeline, emitter, persistent `LightModel` particle, and model index 0.
2. One predictable model-builder call per active shell instance.
3. No binder, `Quad`, `Powder`, random position/rotation/scale, velocity, noise, or additional visible particle.
4. Neutral fixed color/alpha/material inputs and no unnecessary texture animation.
5. One placeholder model block sufficient to reach the builder; its geometry is not reused by Underpaint.
6. No authored behavior that changes topology, transform, tint, alpha, or visibility after startup.

### First Semantic Gate

Keep one shell host and owned triangle, then vary one native input at a time:

1. Set only `VfxObject.Color.xyz` and confirm RGB reaches the owned model draw.
2. Set only `VfxObject.Color.w` and confirm smooth transparency rather than dither or disappearance.
3. With color animation disabled, enable dynamic vertices and confirm the top vertex changes position continuously while
   status retains one stable VFX/document/model/wrapper identity and `VertexMisses` remains zero.
4. Stop each mode and confirm model-owner accounting returns to equality; no repeated creation/release may occur while
   either animation is running.
5. Replace only the copied 3x4 transform with one `FrameCommand.CurrentTransform`; keep `VfxObject.Position` equal to
   the primitive's world-space sorting center and confirm geometry and sorting identity coincide without double
   translation.
6. Add the owned rectangle record using the same wrapper protocol and confirm dimensions remain transform-driven.

If host color does not propagate, statically map the remaining descriptor semantic inputs before another bounded A/B.
Do not turn unknown descriptor fields into a trial-and-error payload pipeline.

### First Ordering Gate

After smooth alpha is proven, create exactly two persistent shell hosts with fixed creation/publication order and
different colors. Give each a real independent category-2 `DocumentInstance`, place their transparent owned triangles
so they overlap in screen space at different camera depths, then swap only their positions. Pass requires visible blend
order to reverse with camera depth while opaque-scene depth behavior remains correct. Capture document rank and final
execution order for the same frames so the visual result remains correlated with the already-established producer path.

### Experimental Backend Seam

- Keep `Renderer`, retained drawable types, `PrimitiveFrame`, and `FrameCommand` unchanged.
- Add a separate Debug-only AVFX backend owner; do not grow `AvfxGeometryProbe` into the lifecycle implementation.
- Reconcile the latest complete frame by `DrawableId`: one persistent shell instance and real document per drawable,
  shared immutable triangle/rectangle topology records where semantics allow, and no AVFX resource mutation.
- A missing drawable in a published frame must suppress that host's model-builder call without destroying its retained
  identity. `Drawable.Dispose` retires and destroys the host through one symmetric owner path.
- Publish immutable per-host payload snapshots. The render detour may only read the current payload and synchronously
  copy/replace the descriptor; it must not allocate, upload, release, or wait under a framework-thread lock.
- Route a test frame to either the current backend or the AVFX experiment, never both, so duplicate draws cannot mask the
  result.
- Keep host position synchronized to the payload's world-space sorting center independently from the full descriptor
  transform. This distinction is required for affine/sheared geometry and future arbitrary three-point triangles.

Only after the one-host semantic gate and two-host ordering gate pass should create/hide/show/retire and transition
lifecycle be generalized. Cost measurements remain last.

## Follow-Up Gates

These gates run only after the minimal shell carries the already-accepted owned mesh without donor behavior.

### Primitive Semantics

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
- One immutable shared lifecycle shell produces a predictable neutral model-builder call without donor-effect cleanup.
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
