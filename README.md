# Underpaint

Underpaint is a retained world-space primitive renderer for Dalamud plugins.

The first AVFX backend uses a minimal immutable lifecycle shell to obtain a real game-owned `VfxObject`,
`DocumentInstance`, worker scheduling, opaque-scene depth behavior, and category-2 camera-depth ordering. Underpaint
replaces only the synchronous model-builder descriptor with its own shared geometry and semantic payload.

## Public primitives

- unit equilateral triangle;
- unit rectangle with retained width and height;
- unit-diameter, 1,024-face faceted sphere with retained radius.

Each retained drawable receives an independent native sorting identity. Callers publish a complete frame containing the
geometry transform, independent world-space sorting center, RGB color, and smooth alpha. AVFX resources, native handles,
models, buffers, materials, textures, shaders, and dither controls remain private.

See [`docs/PRIMITIVE_API.md`](docs/PRIMITIVE_API.md) for the API contract, [`docs/STATUS.md`](docs/STATUS.md) for the
current implementation boundary, and [`docs/AVFX_SORTING_RESEARCH.md`](docs/AVFX_SORTING_RESEARCH.md) for archived
evidence.
