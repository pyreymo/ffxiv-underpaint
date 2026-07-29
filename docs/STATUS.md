# Underpaint Status

Updated: 2026-07-30

## Current Backend

Underpaint now has one retained AVFX backend. The former low-level `charactertransparency` pass-builder backend and all
AVFX geometry/performance probes have been removed.

For each drawable, the backend owns one persistent normal shell `VfxObject` and its real game-created
`DocumentInstance`. The shell supplies lifecycle, category-2 depth sorting, worker scheduling, and the synchronous model
builder. Underpaint supplies the model record, shared vertex/index wrappers, complete affine transform, color, alpha,
and retained drawable state.

The render detours read an immutable payload reference. They do not allocate, upload, release, or wait on the framework
publication lock. A drawable missing from a complete published frame keeps its native identity but suppresses its model
builder call. Disposing a drawable retires its host through the normal static-VFX cleanup entry.

An experimental native-particle branch now connects to the installed `VFXEditorCN 1.9.6.0` assembly through an
exact-version reflection adapter. It creates an editable document from VFXEditorCN's own default fragments, changes its
particle to `DecalRing`, installs a repeating `Color.Bri` curve `(0,1) -> (10,4) -> (20,1)`, and publishes the result
through VFXEditorCN's existing virtual replacement path. Decal-ring instances use normal `VfxObject` transforms and do
not enter Underpaint's model-builder substitution.

## Public Semantics

- `TriangleDrawable`: immutable unit equilateral triangle centered at local origin.
- `RectangleDrawable`: shared unit rectangle with retained positive width and height.
- `SphereDrawable`: shared unit-diameter, 1,024-face flat-shaded sphere with retained positive radius.
- `DecalRingDrawable`: shared VFXEditorCN-authored native particle definition with independent retained VFX hosts.
- `Matrix4x4 transform`: complete geometry transform, including affine shear.
- `Vector3 sortingCenter`: independent world-space point written only to `VfxObject.Position`.
- `Vector3 color` and `alpha`: mapped to `VfxObject.Color`.

Dither is not exposed. Its previous implementation belonged to the deleted shader-specific backend, and no equivalent
AVFX descriptor semantic has been established.

## Verified Evidence

- runtime-owned AVFX model records and wrappers render through the shell builder;
- color and smooth alpha update without host recreation;
- independent documents preserve independent payload identity;
- category-2 position swaps reverse visible transparent composition correctly;
- a shared runtime-owned 1,024-face faceted sphere rendered across hundreds of independent instances with encouraging
  performance;
- shell files remain immutable and contain no product geometry.

## Runtime Validation Pending

The production-shaped backend still needs explicit runtime confirmation for triangle, rectangle, and sphere transforms;
missing-frame hide/show; drawable retire/recreate; territory change; logout/title screen; plugin reload/unload; and
sorting against a native transparent category-2 control. Build success does not close those gates.

The decal-ring branch is build-verified only. Runtime still must confirm document creation, visible ring geometry,
brightness repetition, transform/color updates, missing-frame hide/show, editing plus manual VFXEditor update, and clean
removal across drawable dispose, renderer dispose, VFXEditor unload, and plugin reload.
