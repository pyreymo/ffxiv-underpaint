# Underpaint AVFX Backend Plan

Last updated: 2026-07-30

## Current Decision

AVFX is the first production-shaped Underpaint backend.

The accepted chain is:

`retained drawable -> persistent VfxObject -> real DocumentInstance -> category-2 depth ordering -> scoped model builder -> Underpaint geometry`

The old low-level pass-builder backend and temporary AVFX probes have been removed. Detailed reverse engineering and
completed experiments remain archived in [`docs/AVFX_SORTING_RESEARCH.md`](docs/AVFX_SORTING_RESEARCH.md) and Git
history.

The current shell/model-substitution implementation is evidence for native lifecycle, document identity, scheduling,
sorting, and runtime-owned geometry. It is not the intended final effect-authoring abstraction. The target direction is
runtime AVFX composition using native particle types such as `DecalRing`, with the game retaining ownership of particle
geometry, animation, culling, and rendering wherever a suitable native primitive exists.

Underpaint will not maintain a private subset or forked copy of VFXEditor's AVFX format model. The preferred integration
is a versioned Dalamud IPC contract with VFXEditor. VFXEditor should own editable AVFX documents, import/export,
serialization, and publication of compiled revisions as loadable virtual resources. Underpaint should own semantic
effect definitions, retained instances, frame publication, `VfxObject` lifetime, and reaction to VFXEditor revision or
shutdown events. Direct references to VFXEditor plugin objects or editor-internal C# types are out of scope.

The IPC contract does not exist in the current VFXEditor checkout. Its minimum shape still needs agreement with
VFXEditor upstream: API version and initialized/disposed events; create/open/close editor sessions; import/export bytes;
publish a session revision to a stable or revisioned virtual AVFX path; and notify consumers when a revision becomes
ready or invalid. IPC payloads should use runtime-safe primitives such as IDs, strings, and byte arrays rather than
sharing editor object references across plugin load contexts.

2026-07-30 follow-up: adding an upstream VFXEditor IPC provider is not considered a realistic dependency. Underpaint
must not copy or maintain a private AVFX format subset. The leading unmodified-plugin candidate is therefore an
exact-version, fail-closed reflection adapter over VFXEditor's existing public manager/document/file surface. Current
source exposes `Plugin.AvfxManager`, manager/document collections, document creation, import/export, and `ToBytes()`;
cross-plugin AssemblyLoadContext visibility and unload safety are not yet runtime-verified. A small full VFXEditor fork
with an IPC bridge and a filesystem handoff remain fallback options, not the current decision.

## Implemented Model

- One persistent shell host and real document per retained drawable.
- One immutable shared runtime model per primitive kind.
- Triangle and rectangle use centered unit topology.
- Sphere is a unit-diameter, flat-shaded UV sphere with exactly 1,024 triangles.
- `sortingCenter` drives only `VfxObject.Position`.
- The complete `Matrix4x4` is packed into the copied native 3x4 descriptor and drives only geometry.
- RGB and smooth alpha use `VfxObject.Color`.
- Missing drawables publish a null payload and suppress the scoped builder call without destroying identity.
- `Drawable.Dispose` removes the host through the normal static-VFX cleanup path.
- Render callbacks read immutable payload and document-map snapshots without the framework publication lock.
- The shell asset remains immutable and contains no product geometry.

## Public Boundary

Public consumers see only `Renderer`, `PrimitiveFrame`, retained triangle/rectangle/sphere drawables, transform,
sorting center, color, and alpha. AVFX paths, documents, wrappers, buffers, native handles, model records, material
inputs, and dither remain private.

Dither was removed because its only verified meaning belonged to the deleted `charactertransparency` backend. No AVFX
equivalent has been established, so keeping the parameter would expose unsupported behavior.

## Evidence

Runtime testing has established:

- normal shell instances receive independent game-owned document identities;
- Underpaint-owned model records and vertex/index wrappers are accepted by the shell model builder;
- dynamic RGB and smooth alpha work without host recreation;
- two documents retain isolated payload identity;
- category-2 position swaps reverse final execution and visible alpha composition together;
- a shared runtime-owned 1,024-face faceted sphere scales to hundreds of independent instances with encouraging observed
  frame cost.

These results establish the rendering principle and justify the first backend implementation. They do not replace the
production-shaped lifecycle validation below.

## Next Validation

Run these gates in order on the current backend:

1. Triangle, rectangle, and sphere visibility, dimensions, color, alpha, and full transform.
2. Independent sorting center without duplicate translation.
3. Missing-frame hide/show while preserving document identity.
4. Drawable dispose, recreation, and repeated cycles.
5. Territory change, logout, title screen, plugin reload, and unload.
6. Sorting against a native transparent category-2 control.
7. Instance cardinality and performance with realistic mixed primitive scenes.

Any runtime failure must be recorded separately from build results. Do not restore the deleted low-level backend or
probe controls as compatibility paths.

## Next Architecture Investigation

Before expanding the current custom-model backend, first run a read-only reflection probe against an installed,
unmodified VFXEditor:

1. Locate the VFXEditor assembly without Dalamud internal plugin-manager APIs and verify its exact assembly version.
2. Resolve `Plugin.AvfxManager`, enumerate managers/documents, and read active document metadata without retaining any
   VFXEditor object, `Type`, delegate, or event subscription across callbacks.
3. Disable or unload VFXEditor and confirm the probe releases all references, detects loss, and does not prevent its
   AssemblyLoadContext from unloading.

Only if those lifecycle gates pass, validate one narrow runtime editing round trip:

1. Underpaint requests an editable runtime document containing a minimal scheduler, timeline, emitter, and
   `DecalRing` particle.
2. VFXEditor exposes that document in its normal runtime editor and can import/export it through its existing model.
3. VFXEditor publishes a compiled revision under a loadable virtual AVFX path without exposing its internal node types.
4. Underpaint creates and retires one normal `VfxObject` from that revision; no model-builder substitution is involved.
5. A document edit publishes a new revision and causes an explicit, bounded instance recreation rather than mutating
   unknown parsed Apricot structures in place.

Until that interaction contract is available, the in-memory virtual-resource transport remains unresolved. The
existing disk-backed reload and static shell redirect paths prove useful mechanisms but are not the final runtime
composition boundary.

## Session Continuity

At the end of each substantive discussion or implementation round, briefly update this plan and the Underpaint project
memory with the decision, new evidence, and next unresolved action. Keep those updates concise and do not treat memory as
a substitute for the current checkout or runtime evidence.
