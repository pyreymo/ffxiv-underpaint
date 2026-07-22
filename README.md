# Underpaint

Underpaint is a lightweight world-space primitive rendering library for Dalamud plugins.

The first implementation targets one fixed native semitransparent material path using
`charactertransparency.shpk`. Underpaint owns its geometry, transforms, colors, constants,
textures, and GPU resources while using the game's native material helpers and pass builder.

## First-version scope

- fixed native semitransparent material path;
- per-primitive color and alpha;
- current and previous transforms;
- native main view and verified auxiliary views;
- triangles, quads, discs, rings, sectors, and spheres;
- reusable unit geometry for matching detail levels.

`alpha = 1` means fully opaque output within the semitransparent path. It is not a true opaque
material profile.

Underpaint does not load arbitrary models, layouts, shaders, materials, or textures. It does not
create game objects or provide a general model renderer.

## Status

The previous hand-written G-buffer and transparency prototypes have been archived in Git history.
The native implementation is being rebuilt in small, reviewable steps. No rendering API is
currently available on this branch.

See [docs/RESEARCH.md](docs/RESEARCH.md) for the verified findings retained from the prototypes.
