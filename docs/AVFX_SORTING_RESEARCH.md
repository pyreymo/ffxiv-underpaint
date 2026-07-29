# AVFX Native Transparency Sorting Research Archive

This document preserves the evidence behind the decisions in the root `PLAN.md`. It is an archive, not an active task
list. Current goals, gates, and next actions belong in `PLAN.md`.

Last consolidated: 2026-07-29

## Evidence Boundary

The conclusions below distinguish current checkout code, local FFCS source, static IDA analysis, runtime probes, and
user runtime confirmation. Build success is not runtime evidence. Version-specific addresses are valid only for the
binary identity recorded with them.

## Current Underpaint Submission Path

Evidence: current checkout code.

- `Renderer` publishes retained primitive commands to `NativeBackend`.
- `NativeBackend.BuildPassesDetour` waits for the natural `ModelRenderer` pass-builder rendezvous at view 30 / subview
  11.
- After the natural builder call, Underpaint borrows the current `ModelRenderer`, render-thread
  `GraphicsKernelContext`, shader-selection environment, command allocator, and active pass.
- Underpaint installs its own geometry, declaration, transforms, instance/model/material constants, textures, and
  selected shaders into the current context.
- It calls the original native pass builder once per primitive in published array order and restores all modified
  context state afterward.
- This is low-level native builder injection. It is not a `BgObject`, AVFX, or hand-written D3D draw path.

## Established Append-Order Behavior

Evidence: static IDA analysis and user runtime confirmation.

- Different Underpaint primitives currently publish commands with the same `Context.SortKey`.
- Their visible transparency order follows command append order in the same `Context`; the last tested primitive appears
  in front.
- The current loop preserves caller publication order and contains no camera-depth ordering stage.
- Repeating publication-order swaps would only reconfirm this behavior and has no value for finding an engine sorting
  seam.
- Native depth testing against opaque geometry and correct transparent blending order are separate properties.

## Apricot Depth Producer

Evidence: read-only static IDA analysis of the verified global binary.

- Apricot categories 0-11 compute an affine camera-space depth from the saved view matrix and each real
  `DocumentInstance` world position.
- The producer adds resource `SoftKeyOffset` and an instance-generation fractional tie-breaker, stores
  `{float depthKey, instanceIndex}` pairs, and sorts them in ascending order. Under the verified negative-Z-forward
  convention this is far to near.
- Category 12 is not camera-depth sorted. It uses `priority * 100 + instanceId % 10000`.
- Categories 0-11 are consumed by worker stride. Each worker preserves its own ascending subsequence.
- Final `DeviceDX11.PostTick` merging uses the command's 32-bit `SortKey`; equal keys use fixed source-context priority
  and do not reconstruct the Apricot float rank.
- Apricot records carry real internal slot indices. Their consumers dereference `DocumentInstance`, resource,
  transform, child-list, and virtual-render state. Direct container injection, fake documents, foreign indices, and
  translating depth rank into hand-authored command keys were rejected.

Key addresses for the verified global binary:

- `0x140393060`: obtain render-context view matrices.
- `0x1403B81B0`: save matrix/context state.
- `0x1403B5AF0`: document prepass and category list construction.
- `0x1403B9390`: categories 0-11 depth-pair generation.
- `0x1403B9B20 -> 0x1404068C0`: float-key sorting.
- `0x1403B9650`: sorted-index consumer and `DocumentInstance` virtual render call.
- `0x14037A300`: category-12 priority-key writer.

## Normal AVFX Lifecycle Bridge

Evidence: local FFCS and public source, followed by read-only static IDA analysis of the verified global binary.

- `VfxObject.Create` at `0x140799220` allocates a real scene object, initializes its Apricot listener, adds it to the
  scene world, loads the AVFX resource, and creates a `VfxResourceInstance`.
- Actor VFX enters through `0x140856900 -> LoadCharacterVfx (0x140393560)` and reaches the same lower
  `VfxResourceInstance` lifecycle.
- The resource instance owns the real resource, Apricot resource handle, listener, transform, color/intensity, and one
  packed Apricot handle. It registers in a game-owned global set rather than synchronously creating a fake document.
- `Framework.TaskUpdateGraphicsScene (0x1400D41E0) -> 0x140392B00 -> 0x14038E5F0` processes that set. Once the resource
  is ready, `0x14038E5F0 -> 0x1403B5E30` creates and publishes a real `DocumentInstance` in the engine slot table.

Stable identity bridge:

- The Apricot slot record stores `DocumentInstance*` at `+0x30`, resource at `+0x38`, owner listener at `+0x40`, and
  originating `VfxResourceInstance*` at `+0x48`.
- Generation at `+0x60` and slot index at `+0x64` are copied as one packed handle to
  `VfxResourceInstance+0x60`.
- This permits bounded correlation from a normal VFX owner to its persistent handle, document, sorted record, produced
  commands, and same-frame command execution without forging an index or retaining a frame pointer.

AVFX field mapping:

- Parser `0x1403ACE90` writes `DrawLayerType` (`DwLy`) to resource flags `+0x5C` bits 10-14.
- Category producer `0x1403B5AF0` reads those bits. Values 0-11 enter the camera-depth producer; value 12 enters the
  priority path.
- `DrawOrderType` (`DwOT`) occupies bits 15-23 and selects an internal document traversal callback. Its public `Depth`
  label does not select the document-level producer category.
- `SoftKeyOffset` (`SKO`) is stored at resource `+0x58` and is the float bias added by the depth producer.
- `VfxResourceInstance+0xB0` selects a separate render-state/TLS bank. It is not `DrawLayerType`.

Destruction bridge:

- `VfxObject` cleanup at `0x14045AA80` detaches the listener, destroys the resource instance, unregisters culling state,
  and continues scene cleanup.
- `VfxResourceInstance.vf0` at `0x14040B9F0` validates generation and slot, marks the real record for retirement,
  releases resources, removes the instance from the global set, and frees its allocation.
- Static ownership was symmetric enough for a bounded control probe, but the probe's custom transition scheduling was
  never accepted as production lifecycle evidence.

## AVFX Model-Particle Leads

Evidence: review of public Pictomancy, Splatoon, VFXEditor, Penumbra, and FFCS implementations. These are capability
leads, not runtime facts for Underpaint.

- Public implementations use normal `CreateVfx`, retain real `VfxResourceInstance` owners, update transform and color
  through game functions, and end the lifecycle through the scheduler-managed VFX cleanup path.
- Established resource-replacement implementations redirect a normal ResourceManager request to an external unpacked
  AVFX resource and continue through the original loader.
- AVFX supports model particles with embedded vertex/index data and basic geometric particle forms, making a minimal
  rectangle resource plausible without injecting a foreign Apricot record.
- Blend mode, draw layer, draw order, priority, depth test, depth write, and depth offset are independent resource
  inputs. Public field names alone do not prove their producer mapping.
- A shared mesh plus per-instance transform is a plausible rectangle path. Arbitrary three-point triangle support
  remains unproven because ordinary TRS does not provide general affine/shear freedom.

## BgObject Investigation

Evidence: local FFCS plus read-only static IDA analysis of the verified global binary.

Validated ownership:

- The validated `BgObject` vtable is `0x142162AB8`; its constructor xref is `0x14045133E`.
- `CleanupRender` is slot 1 (`0x140451400`), `UpdateRender` slot 4 (`0x140452320`), `UpdateCulling` slot 6
  (`0x1404524F0`), and `UpdateTransforms` slot 7 (`0x1404519F0`).
- Render-resource setup at `0x140453DC0` creates renderer-owned instances and culling state. None of the lifecycle
  methods directly produces transparent commands.

Producer trace:

1. CullingManager scans bitsets in ascending culling index at `0x14024BED0` and splits visible type-1 indices into jobs
   of at most 200 entries.
2. Worker callback `0x14024E830` dispatches type 1 to the `BgObject` callback at `0x140452A70`.
3. That callback selects renderer-owned instances and reaches `0x140287BE0`.
4. `0x140287BE0` atomically reserves a 96-byte `GeometryInstancingRenderer` input. It contains renderer/resource,
   transform, material flags, and auxiliary state, but no camera-depth key or sortable `BgObject` identity.
5. `0x140286F00` appends 88-byte records to vectors by renderer slot without comparing depth.
6. `BGInstancingRenderer` processes each vector in chunks of up to 256 instances at `0x14028AD60`.

Sorting decision:

- No camera-position or view-Z calculation exists in the culling scan, object callback, geometry-instancing producer,
  per-slot vector builder, or BG instancing worker path.
- Cross-worker atomic reservation can change append order, and no later stage restores culling or depth order.
- Builders `0x14028D580` and `0x14028D6A0` write fixed command SortKey classes.
- Branch `0x14028D430 -> 0x14028D990` groups and sorts by resource/buffer identity, not camera depth.
- `0x1402E1B10 -> 0x1402E1DF0` can emit one instanced draw for multiple same-resource objects.
- The useful sorting unit is therefore a renderer/material/resource batch, not an individual `BgObject`.
- `BgObject` was rejected as a transparency-sorting seam. A runtime sample could only observe the already-explained
  unsorted worker and batching behavior.

## Bounded AVFX Runtime Probe

Evidence: Debug-only Underpaint instrumentation and user runtime confirmation.

- The probe created normal `VfxObject` instances, validated packed handles against real Apricot slots, scoped the
  tracked document render virtual call, correlated synchronous `Context.PushBackCommand` calls with
  `ImmediateContext.ProcessCommands`, and recorded OS thread and worker start/stride data.
- It was default-off and bounded to 120 frames, 1,024 producer/consumer events, 512 commands, and 2-32 instances.
- The existing EH asset `EventHorizon/Assets/no-binder.avfx` parsed as `DrawLayerType = 2`, `DrawOrderType = 0`, and
  `SoftKeyOffset = 0`.
- Swapping only two positions reversed Apricot depth rank and final command execution order together.
- Across 50 comparable frames and 1,024 captured commands, there were zero order failures and zero missing executions.
- Both observed `Start=0/1, Stride=16` worker subsequences preserved the expected rank/execution relationship.
- This passed the producer-to-final-execution gate for category 2. It did not prove visible overlap, model-particle
  geometry, production lifecycle, or scale cost.

## Probe Transition Failures

Evidence: user crash logs and static analysis of the exact CN crash binary.

- The 2026-07-29 CN update exposed a scanner bug: Dalamud `ScanText` had already resolved a leading CALL/JMP signature
  to its callee, while the probe applied a second rel32 resolution. Revision `191db55` removed the duplicate step.
- Re-arming from an active category-2 capture toward category 12 crashed at `ffxiv_dx11.exe+0x3B83E2`. A global active
  slot prepass observed slot 8 with flags `0x05` and a null `DocumentInstance*`, then dereferenced `document+0x228`.
- Moving cleanup before the hooked graphics-scene task produced a second crash at `ffxiv_dx11.exe+0x3B4D5C`: the
  original task worker consumed a still-active slot whose document the probe had just cleared.
- Category 12 had not started in either crash. These failures did not invalidate the category-2 result or category-12
  static field mapping.
- The scanned stop function is FFCS `VfxObject.CleanupRender`. EH's established VFXEditor-derived controller uses the
  same scheduler-managed static-VFX stop entry successfully. The crashes therefore identify the probe's batch re-arm
  and graphics-task transition scheme as invalid; they do not justify adding `Dtor(1)` or changing EH's lifecycle.
- Category-12 runtime re-arm and Stop repair were abandoned because static analysis already establishes its priority
  path and the negative control no longer affects the AVFX decision.

Corrected anchors recorded for that updated CN binary included run target `0x14045C270`, remove `0x14045A100`,
graphics-scene task `0x1400D4420`, depth producer `0x1403B8BC0`, and sorted consumer `0x1403B8E80`. These addresses
must not be transferred to another binary.

## Binary Identity And IDA Reliability

Verified global IDB:

- Path: `C:\Users\Administrator\Documents\Repos\FFXIVQuickLauncher\.ffxiv-exe-probe-state\game\ffxiv_dx11.exe.i64`
- SHA-256: `4236e770e673150e85f8d10beab2fc4834c82f86aab8a555a9175439fc906a6d`
- MD5: `db16bf90d76be1a344a648abcc9465fe`

Updated CN executable recorded during probe repair:

- Size: `51,774,720` bytes.
- SHA-256: `6f64fd34ca45ed6ef0616f0aaf4d25a42c19104d550a34b26ae5ffc1e980d87d`.
- MD5: `04f0e75c4e67aca6086e0dfa637f3bcc`.

The CN IDB previously contained imported Apricot symbols whose names did not match bytes at their addresses. Global
addresses and imported names must never be transferred to CN without independent byte/signature and semantic
validation.

IDA operating rules retained from the investigation:

- Open and close databases through IDA MCP and verify identity before using saved addresses.
- Validate segment, bytes, executable targets, and xrefs before trusting imported symbols.
- Keep Hex-Rays calls serial. Prefer decompile, disassembly, xrefs, and bounded read-only scripts.
- Avoid `ida_pseudocode_at`; it previously caused a reproducible MCP connection failure.

## Condensed Timeline

- 2026-07-28: Recorded current append-order behavior and created the persistent sorting investigation.
- 2026-07-28: Traced and rejected `BgObject` before runtime because its producer has no camera-depth key.
- 2026-07-28: Proved the normal `CreateVfx -> VfxResourceInstance -> DocumentInstance` static bridge and AVFX field
  mapping.
- 2026-07-29: Added the bounded Debug probe in Underpaint revision `a19a19f`; later documentation revisions published
  the harness through `dddd70d`.
- 2026-07-29: Category-2 runtime capture passed producer-to-final-execution ordering across observed worker
  subsequences.
- 2026-07-29: Probe re-arm failures demonstrated that its transition hook was not a production lifecycle seam.
- 2026-07-29: EH removed the temporary sorting UI, category-12 asset, extra redirect, and probe submodule revision.
- 2026-07-29: Revision `ed08abb` refocused the next decision on external AVFX model-particle capability instead of
  further probe repair.

The complete pre-consolidation narrative remains available in Git history at revision `ed08abb` and its ancestors.
