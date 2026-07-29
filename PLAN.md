# Underpaint Native Transparency Sorting Plan

Last updated: 2026-07-29

## Purpose

Find a legal engine-backed path that gives Underpaint primitives native camera-depth ordering for transparent rendering.
The investigation must identify the real owner, producer container, sorting granularity, worker partitioning, command
publication, and lifetime protocol before any native host or queue is modified.

`Client::Graphics::Scene::BgObject` was the first candidate, but the verified global-binary static trace rejected it as
a camera-depth sorting seam. Its normal model path feeds `BGInstancingRenderer`, which partitions visible objects by
culling index and batches them by renderer slot and material/resource identity without producing a camera-depth key.

The first-priority candidate is now a real AVFX host created through the game's normal `CreateVfx` lifecycle. This is
not the rejected approach of forging Apricot records or `DocumentInstance` indices. The investigation must prove that a
normally created instance reaches the known Apricot depth producer and preserves that order through final consumption
before custom AVFX geometry or an Underpaint-owned resource redirector is considered.

## Tracking Protocol

- Read this file before continuing the investigation.
- Update `Last updated`, `Current State`, `Next Action`, and `Session Log` at the start and end of every research round.
- Record conclusions by evidence source: current code, static IDA, runtime probe, or user runtime confirmation.
- Do not turn an established conclusion back into a proposed experiment unless the earlier evidence is explicitly
  invalidated.
- Record version-specific addresses only together with the binary identity from which they came.
- Keep abandoned paths and stop reasons here so they are not repeatedly proposed after context compaction.

## Success Criteria

- For each candidate, identify the native container that holds its transparent render work before
  `Context.PushBackCommand`.
- Identify whether the producer sorts by camera depth and whether the sort unit is object, model, submesh/material, or
  another record.
- Prove whether the producer order survives worker partitioning and final command merge.
- Identify a legitimate per-primitive or equivalent engine-backed host seam that does not require forged indices,
  frame-arena pointers, hand-authored SortKeys, or hand-sorted command packets.
- Prove create, load, update, cull, render, cleanup, and destruction ownership before implementing a persistent host.

The visual capability target is split into four independent results:

1. Correct back-to-front ordering among Underpaint primitives.
2. Correct depth testing against the opaque scene.
3. Correct ordering relative to native transparent objects in the same producer category and draw layer.
4. Correct ordering of transparent faces within one primitive.

An object-level native host is not assumed to solve item 4. Success on items 1-3 must be reported separately, and
same-category evidence must not be generalized to all native transparent rendering.

## Constraints

- Do not implement CPU distance sorting or manually encode camera depth into `Context.SortKey`.
- Do not append foreign records to native frame containers or retain frame/job pointers across frames.
- Do not forge `DocumentInstance`, `BgObject`, model, submesh, or resource indices.
- Do not copy expanded native command packets.
- Do not call native builders from arbitrary Dalamud callbacks.
- Use read-only native investigation until producer, container, allocator, consumer, thread, and lifetime are understood.
- Keep A/B tests single-variable and bounded.

## Current Rendering Path

Evidence: current checkout code.

- Underpaint does not create, register, update, or destroy a `BgObject`.
- `Renderer` publishes retained primitive commands to `NativeBackend`.
- `NativeBackend.BuildPassesDetour` waits for the natural `ModelRenderer` pass-builder rendezvous at view 30 / subview
  11.
- After the natural builder call, Underpaint borrows the current `ModelRenderer`, render-thread
  `GraphicsKernelContext`, shader-selection environment, command allocator, and active pass.
- Underpaint installs its own vertex/index buffers, declaration, world/instance/model/material constants, textures, and
  selected shaders into the current context.
- It iterates primitives in published array order and calls the original native pass builder once per primitive.
- It restores all modified context state afterward.
- This is a low-level native builder injection path. It is neither a `BgObject` path nor a fully hand-written D3D draw
  path.

Relevant code:

- `Renderer.cs`
- `Internal/NativeBackend.cs`
- `Internal/NativeContextState.cs`
- `Internal/NativeResources.cs`
- `Internal/MaterialHelper.cs`

## Established Sorting Conclusions

Evidence: prior IDA work plus user runtime confirmation.

- Different Underpaint primitives currently publish commands with the same `Context.SortKey`.
- Their visible transparency order is determined by the order in which their commands enter the same `Context`.
- For the tested path, the primitive drawn last appears in front.
- The current primitive loop preserves caller publication order; it has no camera-depth ordering stage.
- Swapping caller publication order would only reconfirm the already-established append-order behavior. It has no new
  value for finding a native sorting seam and must not be proposed as the next discriminator.
- Native depth testing and correct transparent blending order are separate properties. Existing native depth behavior
  does not prove transparent sorting.
- Moving the existing multi-primitive loop into one `BgObject` callback would not by itself solve sorting. If the engine
  sorts only whole `BgObject` instances, one native host containing many sequential primitive submissions remains one
  sorting unit.

## Apricot Investigation

Evidence: read-only static IDA analysis of the global binary identified below.

- Apricot categories 0-11 have a real camera-depth producer.
- Frame setup obtains view matrices for 11 render contexts.
- The producer computes an affine depth from the saved view-matrix Z column and `DocumentInstance` world position,
  adds resource depth bias, and adds an instance-ID fractional tie-breaker.
- It stores `{float depthKey, instanceIndex}` pairs and sorts them by the float key in ascending order.
- Under the verified negative-Z-forward camera convention, the intended order is far to near.
- Category 12 is not depth sorted. Its key is `priority * 100 + instanceId % 10000`.
- Sorted categories 0-11 are consumed by worker stride. Each worker preserves only its own ascending subsequence.
- Final `DeviceDX11.PostTick` merging uses the command's 32-bit SortKey. Equal SortKeys use fixed source-context
  priority and do not reconstruct the Apricot float-depth rank.
- Therefore the Apricot producer is proven, but a strict cross-worker final depth total order has not been proven.
- Apricot sorted records contain internal slot indices. Consumers dereference real `DocumentInstance`, resource,
  transform, child-list, and virtual-render state.
- Direct Apricot container injection is not a legal arbitrary-primitive seam. Foreign indices, fake documents, borrowed
  frame containers, and translating Apricot depth rank into hand-authored SortKeys are permanently rejected approaches.
- This rejection does not cover a real `DocumentInstance` created by the game's normal AVFX resource and `CreateVfx`
  lifecycle. Whether such an instance enters categories 0-11 and retains its order through final command consumption is
  the next investigation.

Version-specific Apricot function addresses from this binary:

- `0x140393060`: obtain render-context view matrices.
- `0x1403B81B0`: save matrix/context state.
- `0x1403B5AF0`: document prepass and category list construction.
- `0x1403B9390`: categories 0-11 depth-pair generation.
- `0x1403B9B20 -> 0x1404068C0`: float-key sorting.
- `0x1403B9650`: sorted-index consumer and `DocumentInstance` virtual render call.
- `0x14037A300`: category 12 priority-key writer.

## Real AVFX Host Leads

Evidence: user-provided review of public Pictomancy, Splatoon, VFXEditor, and FFCS implementations. These are candidate
leads and must be validated against current local source and the verified binary before they become runtime facts.

- Public implementations call the game's normal `CreateVfx`, retain a real `VfxResourceInstance`, update transform and
  color through game functions, and end the lifecycle through `DestroyVfx`.
- Public resource-replacement implementations redirect a normal ResourceManager request for a game path to a local
  unpacked AVFX resource, then continue through the original loader.
- AVFX format implementations expose top-level draw-order intent including `Default`, `Reverse`, and `Depth`, plus a
  `Sort Key Offset` field. These names do not yet prove a mapping to the known Apricot depth key or resource depth bias.
- AVFX particle types include model and basic geometric forms. Model particles can contain vertex/index data, making a
  custom low-poly resource plausible without injecting a foreign Apricot frame record.
- Particle-level blend mode, draw priority, depth test, depth write, and depth offset are resource-defined inputs that
  must be controlled independently during experiments.
- A shared static mesh plus per-instance transform may fit rectangles, regular polygons, polyhedra, and spheres. It is
  not yet proven sufficient for an arbitrary three-point Underpaint triangle; normal TRS may lack the required affine
  shape freedom, and rewriting a shared AVFX resource per instance is not an acceptable substitute.

## Normal AVFX Lifecycle Static Bridge

Evidence: current local FFCS and public source plus read-only static IDA analysis of the verified global binary.

Normal creation and ownership:

- FFCS `VfxObject.Create` resolves to `0x140799220`. It allocates a 0x390-byte scene object, initializes its embedded
  Apricot listener at `VfxObject+0x90`, adds the object to the real scene `World`, calls `OnAddedToWorld`, loads the AVFX
  resource, and creates a 0xD0-byte `VfxResourceInstance` at `VfxObject+0x2A0`.
- The actor-effect entry at `0x140856900 -> LoadCharacterVfx (0x140393560)` allocates a real 0x1E0-byte `VfxData` and
  reaches the same `VfxResourceInstance` constructor. Static `VfxObject` and actor `VfxData` are therefore two normal
  owners of the same lower lifecycle, not separate Apricot submission protocols.
- `VfxResourceInstance` retains the real `VfxResourceObject`, its `ApricotResourceHandle`, owner listener, transform,
  color/intensity, and one packed Apricot handle. It registers itself in a game-owned global set; it does not create a
  `DocumentInstance` synchronously in an arbitrary caller.
- `Framework.TaskUpdateGraphicsScene (0x1400D41E0) -> 0x140392B00 -> 0x14038E5F0` processes that set. Once the resource
  is ready, `0x14038E5F0 -> 0x1403B5E30` allocates and constructs a real Apricot `DocumentInstance` and publishes it in
  the engine's 136-byte slot table. Exact OS-thread identity remains a runtime observation, but the static owner and
  game task boundary are established.

Stable identity and container bridge:

- The Apricot slot record stores `DocumentInstance*` at `+0x30`, resource at `+0x38`, owner Apricot listener at `+0x40`,
  and the originating `VfxResourceInstance*` at `+0x48`.
- The record stores a generation at `+0x60` and slot index at `+0x64`. The same two values are copied as one packed
  64-bit handle to `VfxResourceInstance+0x60`.
- Category lists and sorted depth pairs carry the slot index. A bounded probe can therefore correlate
  `VfxObject/VfxData -> VfxResourceInstance -> {generation, slot} -> DocumentInstance -> sorted record` without forging
  an index or retaining a frame-arena pointer.
- The sorted consumer calls each `DocumentInstance` render virtual synchronously. A thread-local capture scope around
  that call can associate commands appended during the call with the persistent packed handle; final command execution
  still requires an explicit same-frame command identity capture.

AVFX field mapping and producer category:

- The AVFX parser at `0x1403ACE90` writes `DrawLayerType` (`DwLy`) to resource flags `+0x5C` bits 10-14.
- The category-list producer at `0x1403B5AF0` reads those exact bits as `(flags >> 10) & 0x1F`. Values 0-11 enter the
  established camera-depth producers; value 12 enters the established priority path.
- The parser writes `DrawOrderType` (`DwOT`) to bits 15-23. `DocumentInstance.ctor` uses that value to choose an internal
  document traversal callback. It does not select the producer category. Public names such as `DrawOrder = Depth`
  must not be used as evidence that a document enters categories 0-11.
- The parser writes `SoftKeyOffset` (`SKO`) to resource `+0x58`. The depth producer at `0x1403B9390` adds that exact
  float to camera-space depth before the instance-generation fractional tie-breaker.
- The game-owned `VfxResourceInstance` field at `+0xB0` selects a separate per-category render-state/TLS bank and can be
  overridden to 9 or 10 by resource state. It is not the `DrawLayerType` category consumed by `0x1403B5AF0`.

Destruction:

- `VfxObject` cleanup at `0x14045AA80` first detaches the resource-instance listener, destroys the owned
  `VfxResourceInstance`, unregisters culling state, and then continues scene cleanup.
- `VfxResourceInstance.vf0` at `0x14040B9F0` validates the packed generation and slot, clears the slot's originating
  instance pointer, marks the real Apricot record for retirement, notifies the listener, releases both resource
  references, removes itself from the global set, and frees its 0xD0 allocation.
- The normal lifecycle is symmetric enough to justify a bounded runtime control probe. Persistent-host integration is
  still deferred until final order, visual behavior, and reload/logout/plugin-unload cleanup are observed.

## IDA Identity And Reliability

Verified global IDB:

- Path: `C:\Users\Administrator\Documents\Repos\FFXIVQuickLauncher\.ffxiv-exe-probe-state\game\ffxiv_dx11.exe.i64`
- SHA-256: `4236e770e673150e85f8d10beab2fc4834c82f86aab8a555a9175439fc906a6d`
- MD5: `db16bf90d76be1a344a648abcc9465fe`

CN IDB warning:

- Path: `C:\Program Files (x86)\上海数龙科技有限公司\最终幻想XIV\game\ffxiv_dx11.exe.i64`
- Several imported Apricot data symbols do not match the bytes at their named addresses.
- Addresses labeled as Apricot vtables were observed to contain `chara/...` path strings.
- Apricot names and addresses derived from those mismatched symbols are invalid for CN and must not be reused.
- Global addresses must never be transferred directly to CN. CN anchors require independent byte/signature and
  semantic validation.

IDA operating rules:

- Open and close databases only through IDA MCP.
- Verify input identity with `ida_idb_meta` before using saved addresses.
- Check segment, bytes, executable targets, and xrefs before trusting a PDB/imported symbol.
- Keep Hex-Rays calls serial.
- Avoid `ida_pseudocode_at`; it previously caused a reproducible MCP `Connection closed` failure.
- Prefer serial `ida_decompile`, disassembly, xrefs, and bounded read-only `ida_run_script` queries.
- Always call `ida_close_idb` when a research round is complete.

## BgObject Facts Confirmed From Local FFCS

Evidence: current local FFXIVClientStructs source/index, not runtime validation.

- `BgObject` inherits `DrawObject`, which inherits scene `Object`.
- It has scene position, rotation, scale, visibility, transform-change state, and a `ModelResourceHandle*`.
- FFCS exposes `Create`, `SetModel`, `OnAddedToWorld`, `UpdateTransforms`, `UpdateCulling`, `UpdateRender`,
  `CleanupRender`, and `Dtor`-related lifecycle methods.
- A model must reach `ModelResourceHandle.LoadState >= 7` before it is treated as loaded.
- These facts make `BgObject` a plausible resource-backed scene host, but they do not prove its transparent sorting
  behavior, sorting granularity, registration protocol, or suitability for arbitrary Underpaint geometry.

## BgObject Static Trace

Evidence: current local FFCS plus read-only static IDA analysis of the verified global binary identified above.

Validated entry points and ownership:

- The validated `BgObject` vtable is at `0x142162AB8` in `.rdata`; its only constructor xref is
  `Client::Graphics::Scene::BGObject.ctor` at `0x14045133E`, and its entries resolve to executable code.
- FFCS virtual slots match the validated table: `CleanupRender` is slot 1 (`0x140451400`), `UpdateRender` is slot 4
  (`0x140452320`), `UpdateCulling` is slot 6 (`0x1404524F0`), and `UpdateTransforms` is slot 7 (`0x1404519F0`).
- `UpdateRender` manages model/animation load state. `UpdateTransforms` updates cached transforms. `UpdateCulling`
  updates the real CullingManager object at `BgObject+0x80`. None of these functions submits transparent work directly.
- `BgObject` render-resource setup at `0x140453DC0` creates real renderer-owned instances and a CullingManager object;
  cleanup at `0x140451400` releases those resources and unregisters culling state.

Producer and container chain:

1. CullingManager builds visible-object index arrays by scanning its bitsets in ascending culling index at
   `0x14024BED0`. Type-1 entries, which dispatch to `BgObject`, are copied to one array and split into contiguous jobs of
   at most 200 indices.
2. Render callback workers consume those index ranges at `0x14024E830`. The type-1 dispatch table entry is
   `0x140452A70`, the actual `BgObject` render callback.
3. `0x140452A70` selects renderer-owned instances from `BgObject+0x98` and calls their virtual submission method. The
   normal instance implementation reaches `0x140287BE0`.
4. `0x140287BE0` atomically reserves a 96-byte input record in `GeometryInstancingRenderer`. The record contains the
   renderer slot, model-resource/instance descriptors, transform pointer, material-related flags, and auxiliary state;
   it contains no camera-depth field or `BgObject*` identity used for sorting.
5. `0x140286F00` consumes those records and appends 88-byte records into one vector per renderer slot. It preserves the
   reservation sequence within each slot and performs no comparison or sort.
6. `BGInstancingRenderer` schedules non-empty slot vectors to worker callbacks. `0x14028AD60` processes each vector in
   contiguous chunks of up to 256 instances and sends it to the pass-specific builders.

Sorting and command publication:

- There is no camera-position or view-Z calculation in the CullingManager index scan, the `BgObject` callback,
  `0x140287BE0`, `0x140286F00`, or the `BGInstancingRenderer` worker path.
- Culling workers are partitioned by ascending object index, not depth. Multiple workers reserve renderer records
  atomically, so cross-worker completion can change the resulting append order; no later stage reconstructs culling
  index order or camera-depth order.
- The normal pass builder at `0x14028D580` writes a fixed Context SortKey class (`0x01200000` in the affected low bits)
  before entering the generic model builder. The related pass-5 builder at `0x14028D6A0` does the same.
- The `0x03000000` branch enters `0x14028D430 -> 0x14028D990`. It groups and insertion-sorts records by a composite
  resource/buffer identity at record offset `+0xB0`; this is a batching key, not camera depth.
- `0x1402E1B10 -> 0x1402E1DF0` emits one instanced draw command when a resource group contains multiple instances.
  Command SortKey bits are derived from material/shader/resource state. No per-instance depth rank is encoded.
- Therefore the useful sorting unit is a renderer-slot/material-resource batch, not a `BgObject`. Two instances of the
  same model/material can become one instanced draw whose instance order is inherited from worker reservation order.
  Different batches are ordered by resource-derived command keys and the already-established final context merge, not
  by camera depth.

Static decision:

- The normal resource-backed transparent `BgObject` path has no camera-depth producer to preserve through worker or
  command merging.
- One-host-per-primitive does not change the sorting unit because `BGInstancingRenderer` re-batches hosts that share a
  renderer slot/material resource.
- A runtime control sample cannot promote this path into a legal sorting seam; it would only measure unsorted worker,
  batching, and final-merge behavior already explained by the static chain.
- `BgObject` is rejected for Underpaint native transparency sorting. This does not reject it as a general scene host for
  unrelated future requirements.

## Current State

- Implementation work will use the root `ffxiv-underpaint` checkout on branch `probe/avfx-native-sort`. The nested
  `event-horizon/libraries/Underpaint` checkout must not be edited directly.
- Current low-level Underpaint SortKey and append-order behavior is already understood; no further publication-order
  A/B is planned.
- Forged Apricot records and fake `DocumentInstance` indices remain permanently rejected.
- The normal `BgObject` model path has been traced through CullingManager, `GeometryInstancingRenderer`,
  `BGInstancingRenderer`, pass-specific batching, and `Context.PushBackCommand`.
- `BgObject` has been rejected as a native camera-depth sorting seam: it has no depth-key producer, partitions by
  culling index, and batches by renderer slot/material-resource identity.
- No bounded `BgObject` runtime sorting probe is justified because the static decision gate failed before runtime.
- Apricot proves that the game does perform camera-depth sorting in specific producers. The rejected `BgObject` result
  does not establish that native transparent rendering is globally unsorted.
- The normal `VfxObject.Create` and actor `VfxData` paths have been statically proven to converge on a real
  `VfxResourceInstance` and game-created Apricot `DocumentInstance` with a symmetric retire path.
- AVFX `DrawLayerType`, not `DrawOrderType`, selects the Apricot producer category. Values 0-11 reach the established
  camera-depth path; value 12 reaches the priority path. `SoftKeyOffset` is the established resource depth bias.
- A stable correlation chain exists through the persistent `VfxResourceInstance` packed generation/slot handle. This
  makes a bounded runtime probe possible without foreign indices or retained frame pointers.
- The category-2 two-instance A/B has passed runtime validation. Swapping only the two positions reversed the Apricot
  rank and final command execution order together. Across 50 comparable frames and 1,024 captured commands there were
  zero order failures and zero missing executions; both `Start=0/1, Stride=16` worker subsequences were observed.
- Visible overlap order and the larger scale cost case remain unproven. The normal category-2 AVFX route has passed the
  producer-to-final-execution part of the Phase 1 runtime gate, including observed cross-worker subsequences.
- A Debug-only bounded probe is now implemented in the root Underpaint checkout. It creates normal `VfxObject`
  instances, validates their packed handle against the real Apricot slot record, dynamically scopes the
  tracked document render virtual call, correlates synchronous `Context.PushBackCommand` calls with
  `ImmediateContext.ProcessCommands`, and reports OS thread IDs plus worker start/stride.
- The probe is default-off and bounded to 120 frames, 1,024 producer/consumer events, 512 commands, and 2-32 instances.
  Reaching a bound disables observation hooks but leaves the control VFX visible until Stop, re-arm, or Renderer
  disposal.
- Both Debug and Release Underpaint builds pass with zero warnings. Runtime validation is recorded separately from
  compile-time validation.
- Underpaint revision `4c1ce9e` retains the completed Debug probe as research history. EH no longer consumes it: the EH
  submodule has returned to pre-probe revision `2d0720d`, and its probe UI, priority asset, and extra VFX redirect were
  removed rather than promoted into production.
- The 2026-07-29 CN client update exposed one probe initialization bug: Dalamud `ScanText` already resolves a signature
  whose first opcode is CALL/JMP to the callee, but the probe attempted a second rel32 resolution. The duplicate
  resolution has been removed; all five probe signatures remain unique in the updated executable.
- Re-arming from the still-active category-2 capture to category 12 produced a native crash at
  `ffxiv_dx11.exe+0x3B83E2`. Static analysis of the exact crash binary and register state proves that the pre-category
  global slot pass observed slot 8 with flags `0x05` but a null `DocumentInstance*`, then dereferenced
  `document+0x228`. This did not execute in the category-12 sorted consumer and does not invalidate the control asset.
- Moving probe cleanup before the hooked graphics-scene task did not fix re-arm. A second user runtime crash at
  `ffxiv_dx11.exe+0x3B4D5C` showed that the original task worker immediately consumed an active slot whose document had
  just been cleared by the probe. Category 12 had not started, so neither crash is evidence against the control asset.
- The scanned function is FFCS `VfxObject.CleanupRender`, but EH's existing controller copied from VFXEditor has long
  used the same entry successfully as the scheduler-managed static-VFX stop path. The probe failures therefore do not
  justify adding `Dtor(1)` or reopening EH's established lifecycle implementation. They show that the probe's batch
  re-arm and graphics-task transition scheme is not a valid lifecycle seam.
- Category 12 is no longer required for the AVFX route decision. Static analysis already establishes the category-12
  priority-key path, while category 2 has directly proven camera-depth rank propagation through final command execution.
  Do not spend further work fixing re-arm or Stop solely to complete that negative control.
- The non-instanced `CharacterBase -> Render::Model -> ModelRenderer` path remains a second-priority research candidate,
  not the next implementation target.
- No `BgObject` host implementation has been started.

## Next Action

Do not implement or runtime-probe a `BgObject` host for transparency sorting.

Proceed to a minimal external AVFX rectangle/model-particle capability experiment. Do not execute more category-12
re-arm tests and do not turn the temporary sorting probe into a production VFX lifecycle component.

### Repository And Delivery Workflow

1. Keep AVFX research and any later Underpaint implementation in the root `ffxiv-underpaint` repository. Do not repair
   the temporary probe unless a narrowly required observation cannot be obtained another way.
2. Do not edit files inside `event-horizon/libraries/Underpaint`. That checkout is only an EH consumer submodule.
3. Keep the existing AVFX probe Debug-only, default-off, bounded, and separate from the production primitive backend.
   Do not use category-2-to-category-12 re-arm or rely on its Stop path for further evidence.
4. Build Underpaint, commit only intended files, and push a completed research step before updating a consumer.
5. In the EH repository, fetch the pushed Underpaint revision and update only the submodule revision plus the minimal
   EH Debug UI/control integration. Do not copy or independently patch Underpaint source under EH.
6. Build EH against that exact Underpaint revision and report both the Underpaint commit and EH submodule revision in
   the runtime handoff.
7. Commit and push the EH parent integration branch as part of the same delivery. A local EH working tree or successful
   build is not a completed cross-repository handoff. Verify both remote branch hashes after pushing.

### Existing AVFX Evidence Resources

- The depth-sorted sample is the existing EH asset `EventHorizon/Assets/no-binder.avfx`, exposed through its existing
  resource redirect. Its top-level fields have been parsed directly from the file:
  `DrawLayerType = 2`, `DrawOrderType = 0`, and `SoftKeyOffset = 0`.
- Category 2 is one of the established camera-depth categories 0-11, so the user does not need to select another AVFX
  before implementation starts.
- The category-12 copy remains a valid byte-identical control except for `DrawLayerType` at file offset `0x10C`, but its
  runtime negative-control value is now lower than the cost and risk of repairing temporary probe re-arm. Do not use it
  as the next experiment.
- Existing resource redirection belongs to the EH test harness, not the Underpaint library. Do not add an
  Underpaint-owned redirector before the external minimal-model experiment passes.

### Phase 1: Prove The Normal AVFX-To-Apricot Path

The static bridge and the required runtime producer-to-final-execution evidence are complete for category 2.

Completed evidence:

1. Normal `VfxObject.Create` instances reached stable real `VfxResourceInstance`, packed slot, and
   `DocumentInstance` identities with parsed `DrawLayerType = 2`.
2. Swapping only camera-space positions reversed both Apricot depth rank and final command execution order.
3. Across 50 comparable frames and 1,024 captured commands, there were zero rank/execution order failures and zero
   missing executions.
4. The capture observed both `Start=0/1, Stride=16` worker subsequences and recorded the relevant task, producer,
   consumer, command-push, and execution threads.
5. Category 12 remains statically understood as a priority-key path, but its runtime negative control is not required to
   proceed to the model-primitive experiment.
6. No additional runtime group is required from this temporary probe. Its re-arm and Stop behavior are not production
   lifecycle evidence, and all instrumentation must remain disabled outside bounded research use.

### Phase 2: Validate A Minimal AVFX Model Primitive

Phase 1 has proved that category-2 producer depth order survives final command consumption across observed worker
subsequences. The next experiment must test useful drawable capability rather than another producer control.

1. Build a minimal external AVFX experiment with one persistent rectangle model particle and `DrawLayerType` explicitly
   set to a verified depth-sorted category 0-11. Use ordinary alpha blending, enable depth test, and disable depth write.
   Control `DrawOrderType` independently; do not assume its `Depth` label selects document-level depth sorting.
2. Embed only a minimal rectangle mesh. Use VFXEditor/Penumbra or equivalent established resource replacement and
   lifecycle code for this experiment; do not add an Underpaint resource redirector yet.
3. Give overlapping instances distinguishable per-instance tints while keeping one resource, fixed creation/update
   order, and one real AVFX instance per drawable. Change only camera-space depth in the ordering A/B.
4. Validate back-to-front overlap, order reversal when the camera crosses the depth relation, and depth testing against
   opaque scene geometry as separate visual results.
5. After visual sorting succeeds, validate transform, color, alpha, hide/show, resource reload, destroy, zone change,
   logout, and plugin reload independently, using the established scheduler-managed lifecycle rather than the probe's
   transition hooks.
6. Determine whether per-instance parameters can express current Underpaint rectangle semantics. Investigate arbitrary
   three-point triangles separately because ordinary TRS may not provide the required affine/shear freedom. Reject a
   design that requires per-frame mutation of a shared AVFX resource or one generated resource per changing drawable.
7. Only after the visual and semantic checks pass, measure CPU time, p95/p99/max frame cost, native allocations,
   resource residency, worker count, and command count at
   8, 32, 128, and 512 instances before considering integration.

### Phase 3: CharacterBase Fallback

Investigate the non-instanced `CharacterBase -> Render::Model -> ModelRenderer` path only if AVFX fails because normally
created instances do not reach depth-sorted categories, cross-worker merge loses final order, one-instance-per-primitive
cost is unacceptable, or AVFX model particles cannot express required geometry/color/transform semantics.

The CharacterBase trace must stop immediately if no record before `ModelRenderer.OnRenderMaterial` contains both stable
model identity and camera-depth/world-position input. Character glass/transparency branches may be documented, but their
specialized skeleton, model-slot, material callback, dither, or pass semantics must not be treated as a reusable host
without independent evidence.

## AVFX Decision Gate

Result for `BgObject`: failed statically. Do not proceed to a `BgObject` runtime control sample.

Proceed from Phase 1 to Phase 2 only if all of these are true:

- Instances are created and destroyed through the normal game-owned AVFX lifecycle.
- Each Underpaint candidate host has one real, stable Apricot sorting identity.
- The instance enters depth-sorted category 0-11, not only priority-sorted category 12.
- Camera-depth rank changes reach final execution order, including when instances occupy different worker subsequences.
- The route does not append foreign records, retain frame pointers, forge indices, or hand-author command SortKeys.

Reject or demote the AVFX route if any of these are true:

- It only sorts an entire host and there is no viable one-host-per-primitive lifecycle or cost model.
- It relies on opaque resource/model indices that cannot be supplied without forging native records.
- Its sorted order is lost during worker partitioning or equal-SortKey final merging.
- Downstream particle or draw batching destroys independent document order.
- Normal `CreateVfx` instances do not enter categories 0-11.
- AVFX model instances cannot express required primitive geometry without per-instance resource mutation.
- One-instance-per-primitive CPU, memory, worker, or command cost is unacceptable at target counts.
- Geometry substitution requires copying expanded commands or mutating frame-lifetime containers.
- Create, world registration, culling, render, cleanup, and destruction ownership cannot be made explicit and
  symmetric.

## Deferred Implementation Work

- Do not create a persistent `BgObject` controller yet.
- Do not expose a `BgObject` concept through the public API.
- Do not add an Underpaint-owned AVFX resource redirector before the external minimal-model experiment passes.
- Do not expose AVFX, Apricot, `DocumentInstance`, or resource-redirection details through the public primitive API.
- Do not replace the current backend until a native producer seam passes the decision gate.
- If a seam passes, first implement one resource-backed host with normal create/load/update/hide/show/cleanup/destroy
  behavior and no custom geometry.
- Geometry substitution and transparent-order validation must be separate later steps.

## Session Log

### 2026-07-29: EH AVFX sorting probe integration removed

- Removed the Debug AVFX sorting controls and report forwarding from the EH playground.
- Removed `no-binder-priority.avfx`, its project content item, and the category-12 branch from EH's static VFX resource
  redirector. The existing hidden-player marker path and `no-binder.avfx` behavior remain unchanged.
- Returned EH's Underpaint submodule from probe revision `4c1ce9e` to pre-probe revision `2d0720d`, so Debug startup no
  longer constructs the temporary `AvfxSortProbe` or installs its native hooks.
- Verified that the cleaned EH files and gitlink match the state before `a21a46e` semantically, and built EH Debug and
  Release with zero warnings and zero errors. No AVFX experiment was executed.

### 2026-07-29: Category-12 probe repair dropped; minimal rectangle promoted

- Re-centered the investigation on the product question: whether a normal AVFX host gives Underpaint useful native
  transparent ordering, not whether a temporary probe supports repeatable category switching.
- Category 2 already proved camera-depth rank propagation through final command execution across both observed worker
  subsequences. Static analysis independently establishes that category 12 uses a priority key, so the failed runtime
  negative control has little remaining decision value.
- Recorded the second re-arm crash at `ffxiv_dx11.exe+0x3B4D5C`: the probe cleared a document immediately before the
  original graphics task consumed its still-active slot. Category 12 had not started.
- Corrected the lifecycle interpretation. FFCS identifies the scanned function as `VfxObject.CleanupRender`, while EH's
  VFXEditor-derived static-VFX controller has used the same stop entry successfully. No Dtor addition or EH lifecycle
  audit is justified by the probe crash.
- Cancelled further category-12 re-arm/Stop repair and promoted the external minimal AVFX rectangle/model-particle
  experiment as the next action. This planning update changes no runtime code and does not execute that experiment.

### 2026-07-29: Transition control hook separated from bounded capture hooks

- User runtime evidence on `93fefea` showed that the first category-2 Arm created two VFX and completed its report, but
  later category-12 Arm and Stop had no effect. The latest EH file log contains only the category-2 report at 12:07:59;
  there is no category-12 report or managed exception.
- The shared hook lifecycle was wrong: capture completion called `DisableObservationHooks`, which also disabled the
  graphics-scene task hook responsible for consuming future `pendingRequest` and `stopRequested` transitions.
- Kept the graphics-scene task hook enabled after the first Arm. Bounded completion still disables document, depth,
  sorted-consumer, command-push, and command-process observation hooks, while the low-cost transition control hook stays
  available for re-arm and Stop. It is disposed normally with the probe.
- Built Underpaint Debug and Release with zero warnings and zero errors. Runtime category-2 -> category-12 -> Stop
  validation remains pending.

### 2026-07-29: Re-arm crash traced to slot retirement timing

- Analyzed crash log `dalamud_appcrash_20260729_110114_878_20368.log` and its exact CN executable in IDA. The crash is
  `C0000005` at `ffxiv_dx11.exe+0x3B83E2`, reading `0x228` through a null `DocumentInstance*` for slot 8.
- Proved that `0x1403B8300` is a global active-slot prepass, not the category-12 sorted consumer. At the fault the slot
  flags were `0x05`: active bit 0 was set, skip bit 1 was clear, and the previous prepass result bit 2 was set.
- Traced normal slot publication through `0x1403B5660`: it writes the document, sets active bit 0, and appends the slot
  to the `+0x7A030` list whose count is at `+0x7B030`.
- Traced retirement through `TaskLayoutWorld -> 0x140392470 -> 0x1403B4850`: state-3 slots have their document
  destroyed, are swap-removed from that same list, and decrement the list count before the later prepass.
- Identified the probe bug: re-arm removed active category-2 VFX from the later Dalamud framework callback, after the
  lifecycle task but before the prepass, creating exactly the observed one-frame stale-list/null-document state.
- Changed Arm/Stop transitions to retire old VFX before the hooked native lifecycle task, let the original task consume
  retirement, and start a replacement request only from the subsequent framework update. Category 12 remains enabled.
- Corrected the updated-binary offline addresses: the previous scanner treated PE raw offsets as RVAs. The `.text`
  mapping is raw `0x400` to RVA `0x1000`, so the affected values require `+0xC00`. The corrected anchors are run call
  `0x1408CA40D` -> callee `0x14045C270`, remove `0x14045A100`, graphics-scene task `0x1400D4420`, depth producer
  `0x1403B8BC0`, and sorted consumer `0x1403B8E80`.
- Built Underpaint Debug and Release with zero warnings and zero errors. Runtime re-arm validation remains pending.

### 2026-07-29: Updated CN client run-address resolution fixed

- Reproduced the runtime initialization failure from the logged exception before any probe hook was enabled.
- Confirmed from the current local Dalamud XML that `ScanText` calls `ReadJmpCallSig` for an IDA signature beginning at
  CALL/JMP and returns the real target address, not the call instruction address.
- Removed Underpaint's duplicate `ResolveRelativeCall` step. The static VFX run delegate now uses the `ScanText` result
  directly, matching EH's existing `[Signature]` path and other current consumers.
- Recorded the updated CN executable identity: size `51,774,720`, SHA-256
  `6f64fd34ca45ed6ef0616f0aaf4d25a42c19104d550a34b26ae5ffc1e980d87d`, MD5
  `04f0e75c4e67aca6086e0dfa637f3bcc`.
- Offline-scanned the updated executable. All probe anchors remain unique. The addresses first recorded here were raw
  file offsets incorrectly presented as RVAs; the corrected values are recorded in the later crash-analysis entry.
- Built Underpaint Debug and Release with zero warnings and zero errors. Runtime initialization on the updated CN client
  remains pending user confirmation.
- Committed and pushed the Underpaint fix as `191db55` (`Fix static VFX run address resolution`).
- Updated EH to consume `191db55`, rebuilt Debug and Release with zero warnings and zero errors, and committed/pushed
  the gitlink update as `5f94b29` (`Update Underpaint AVFX probe fix`).

### 2026-07-29: Cross-repository delivery completed and protocol corrected

- The user completed publication of Underpaint branch `probe/avfx-native-sort`; its published revision is `dddd70d`.
- Updated EH to consume exact Underpaint revision `dddd70d`, then committed the parent integration as `a21a46e`
  (`Add AVFX sorting probe controls`) on `3d-playground`.
- Pushed EH `3d-playground` and verified remote branch hash
  `a21a46e65e0c393a5a763bc0d98a834843c58f14` with `ls-remote`.
- Rebuilt `EventHorizon.sln` in Debug and Release against `dddd70d`: zero warnings and zero errors in both
  configurations.
- Corrected the delivery protocol: when a planned experiment spans Underpaint and EH, completion requires commits and
  remote pushes in both repositories, not only an Underpaint commit plus an uncommitted EH working tree.
- Existing unrelated CRLF-only and user worktree changes in both repositories remain unstaged and uncommitted.

### 2026-07-29: EH control harness wired to pushed Underpaint revision

- Committed the root Underpaint implementation as `a19a19f` (`Add bounded AVFX sorting probe`); the published branch
  later advanced to documentation revision `dddd70d`.
- Updated the EH submodule checkout and parent gitlink from `2d0720d` to exact Underpaint revision `dddd70d`; no source
  under `event-horizon/libraries/Underpaint` was independently edited.
- Added `no-binder-priority.avfx` as the category-12 control. Binary comparison confirms exactly one changed byte:
  file offset `0x10C`, `0x02 -> 0x0C`; both Debug and Release outputs contain the copied asset.
- Extended the existing static VFX redirector with a distinct category-12 game path while retaining the existing
  category-2 path and asset.
- Added Debug-only Rendering Research controls for instance count, first offset, per-instance step, category-2 arm,
  category-12 arm, stop, status, and one-shot forwarding of the completed report to `DebugFileLog` source
  `Underpaint.AvfxSortProbe`.
- The EH framework update owns probe start/stop consumption. Territory changes, window disposal, Renderer disposal,
  re-arm, and explicit Stop all converge on the Underpaint cleanup path.
- Built `EventHorizon.sln` in Debug and Release against submodule revision `dddd70d`: zero warnings and zero errors in
  both configurations.
- The EH parent integration is committed and pushed as `a21a46e`. Runtime ordering and lifecycle results remain pending
  user execution.

### 2026-07-29: Bounded AVFX runtime probe implemented in root Underpaint

- Independently matched the depth producer, sorted consumer, and graphics-scene task anchors in both verified binaries.
  Global addresses are `0x1403B9390`, `0x1403B9650`, and `0x1400D41E0`; CN addresses are `0x1403B85D0`,
  `0x1403B8890`, and `0x1400D4120` respectively.
- Recorded the then-current CN binary identity: SHA-256
  `c3f209032fd9bb97a379c85adb7269f33e87e070b9094b71e3467f8be3b00cb9`, MD5
  `776dcff0e9b5b5474d71693e3fa7e616`.
- Added `Internal/AvfxSortProbe.cs` as a Debug-only owner for normal VFX create/run/update/remove, identity validation,
  bounded native observation, report generation, and symmetric cleanup.
- Added only semantic Debug controls to `Renderer`: arm, framework update, stop request, status, and report consumption.
  No native pointer, slot, document, command, or mutable resource is exposed as an API.
- The probe reads but does not mutate the real Apricot slot table. It scopes the tracked document's real render virtual
  call and correlates command pointers only within the bounded same-frame capture.
- Stop is consumed on the framework update path. Teardown disables and drains observation/removal hooks before claiming
  and removing tracked VFX, preventing UI-thread native destruction and concurrent double removal.
- Built `Underpaint.csproj` in Debug and Release with the local Dalamud API 15 SDK: zero warnings and zero errors in both
  configurations.
- Runtime identity stability, cross-worker final order, visible overlap order, zone/logout cleanup, and actual hook
  execution remain unverified until EH consumes the pushed revision and the user runs the control groups.

### 2026-07-28: AVFX probe repository and resource plan fixed

- Created root Underpaint branch `probe/avfx-native-sort` for the upcoming implementation.
- Fixed the repository boundary: edit and push the root Underpaint repository; never patch the nested EH Underpaint
  checkout directly. EH will consume the pushed revision through its submodule.
- Parsed the existing `no-binder.avfx` header and selected its `DrawLayerType = 2` resource as the depth-sorted sample.
- Selected a byte-identical copy with only `DrawLayerType` changed to 12 as the priority control, preserving a
  single-variable A/B.
- Split ownership so Underpaint owns the native probe and VFX lifecycle while EH owns resource redirection, Debug UI,
  and file-log handoff.
- No runtime hook, VFX host, backend, public primitive API, EH source, or nested submodule source was changed in this
  planning update.

### 2026-07-28: Normal AVFX lifecycle trace started

- Began Phase 1 as a read-only source and static-binary investigation.
- Limited this round to the legitimate `CreateVfx`, update, Apricot registration/category, and destruction bridge.
- Made no native hook, runtime probe, host, backend, or public API changes.

### 2026-07-28: Normal AVFX-to-Apricot static bridge completed

- Revalidated the global IDB SHA-256 and completed the trace using that same verified binary.
- Proved that normal static `VfxObject` and actor `VfxData` creation converge on the game-owned
  `VfxResourceInstance -> DocumentInstance` lifecycle.
- Recovered the persistent packed generation/slot handle and the slot record's document, resource, listener, and
  originating-instance fields, providing a legal bounded-correlation key.
- Mapped AVFX `DrawLayerType` to the exact resource bits consumed by the category producer, `DrawOrderType` to an
  independent internal document traversal selection, and `SoftKeyOffset` to the depth producer's resource bias.
- Verified the normal retire path through `VfxObject` cleanup and `VfxResourceInstance` destruction.
- Passed the static decision gate and selected a bounded, default-off existing-resource runtime capture as the next
  action. Cross-worker final order remains deliberately unclaimed.
- Made no native hook, runtime probe, host, backend, or public API changes.

### 2026-07-28: Persistent investigation plan created

- Added this root-level plan at the user's request to survive context compaction.
- Corrected the investigation state: equal Underpaint SortKeys and append-order transparency behavior are established,
  not pending experiments.
- Removed caller publication-order swapping from the next-step plan because it would duplicate established evidence.
- Preserved the completed Apricot findings, binary identity, CN symbol-trust warning, and IDA MCP operating rules.
- Set the next work item to a read-only `BgObject` producer/container/sorting-granularity trace.

### 2026-07-28: BgObject transparent producer trace completed

- Revalidated the global IDB identity before using saved addresses.
- Validated the `BgObject` vtable and FFCS lifecycle slots against executable targets.
- Traced the normal model path from CullingManager's ascending index scan through the type-1 `BgObject` callback,
  `GeometryInstancingRenderer`, `BGInstancingRenderer`, pass-specific batching, and command publication.
- Recovered the relevant container units: culling index arrays, worker ranges of at most 200 objects, 96-byte atomic
  renderer inputs, 88-byte per-slot instance records, and per-pass material/resource groups.
- Found no camera-depth field or comparator. The only observed sort in the `0x03000000` branch uses a composite
  resource/buffer key, and same-resource instances can be emitted as one instanced draw.
- Rejected `BgObject` as an Underpaint transparency-sorting seam and cancelled the planned runtime control sample.
- Made no native hook, host, backend, or public API changes.

### 2026-07-28: Initial CharacterBase fallback selected (superseded)

- Clarified the evidence boundary: Apricot has a camera-depth producer, so the `BgObject` rejection cannot be generalized
  into a claim that the game has no native transparent depth sorting.
- Selected the non-instanced `CharacterBase -> Render::Model -> ModelRenderer` path for the next read-only trace because
  it bypasses the rejected static-background instancing producer.
- Defined the next decision gate around the actual producer record, comparator, sorting unit, worker partition, command
  SortKey, and final merge rather than around visible behavior alone.
- Kept character glass/transparency renderers as branches to inspect, not assumed reusable seams.

This priority was superseded after public implementations exposed a normal, resource-backed `CreateVfx` lifecycle into
the AVFX rendering subsystem. CharacterBase remains a fallback and the trace criteria above remain applicable if AVFX
fails its earlier gates.

### 2026-07-28: Real AVFX host promoted to first candidate

- Split the Apricot conclusion into two routes: forged container/index injection remains permanently rejected, while a
  real `DocumentInstance` obtained through normal AVFX creation is a legitimate candidate pending proof.
- Recorded public implementation and AVFX-format findings as leads requiring validation against current local source and
  the verified binary; field names alone are not treated as a mapping to the known depth producer.
- Reordered the investigation into a static `CreateVfx -> DocumentInstance` bridge, a bounded existing-resource runtime
  probe, a separate minimal AVFX model experiment, and only then a CharacterBase fallback.
- Added explicit cross-worker coverage, final SortKey/execution capture, one-instance-per-primitive performance limits,
  and lifecycle checks.
- Split visual success into primitive-to-primitive ordering, opaque-scene depth testing, same-category native transparent
  ordering, and internal transparent-face ordering. Object-level AVFX sorting is not assumed to solve the last item.
- Deferred any Underpaint-owned AVFX resource redirector, host implementation, or public API change until the external
  experiment passes the decision gate.
