# Underpaint AVFX Backend Plan

Last updated: 2026-07-29

## Current Decision

AVFX is the first production-shaped Underpaint backend.

The accepted chain is:

`retained drawable -> persistent VfxObject -> real DocumentInstance -> category-2 depth ordering -> scoped model builder -> Underpaint geometry`

The old low-level pass-builder backend and temporary AVFX probes have been removed. Detailed reverse engineering and
completed experiments remain archived in [`docs/AVFX_SORTING_RESEARCH.md`](docs/AVFX_SORTING_RESEARCH.md) and Git
history.

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
