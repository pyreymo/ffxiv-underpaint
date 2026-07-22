# Retained rendering findings

This file keeps only the conclusions needed to rebuild Underpaint. The archived source and Git
history remain the reference for experimental implementation details.

## Native submission boundary

- Native material expansion must run on the game's render thread with the current
  `ModelRenderer`, graphics context, view, subview, and frame allocator state.
- A fixed `.mtrl` can be loaded and retained directly through `ResourceManager`; scene objects are
  not required to own the material.
- The proven helper order is shader-selection initialization, `OnRenderMaterial`, `ApplyMaterial`,
  final selection resolution, resource installation, and the native pass-builder call.
- The pass builder can generate main-view and auxiliary-view commands from Underpaint-owned vertex
  and index buffers when the surrounding native state is valid.
- Every graphics-context field changed for submission must be restored after the synchronous
  builder call.

## Ownership target

The fixed donor material should ultimately contribute only its runtime `Material*`, its
`charactertransparency.shpk` package, and the behavior of the two material helpers. Underpaint must
provide geometry, vertex declaration, current and previous world transforms, color and alpha,
model and instance constants, material constants, neutral textures, and any required neutral color
table resources.

The archived prototype had begun replacing these inputs, but its material constant contents and
color table still originated from the donor, and not every helper-installed texture binding was
proven neutral. Those parts must be established again rather than copied as completed behavior.

## Constraints retained from runtime research

- Do not copy shader-selection output, pass flags, constant buffers, texture bindings, shader
  descriptors, expanded commands, or unknown callback results from a scene object.
- Do not invoke the pass builder from an arbitrary Dalamud callback or retain frame-arena pointers
  across frames.
- A `DrawIndexed` stack identifies the executor, not the command producer or its ownership rules.
- The old hand-written semitransparent G-buffer/composite implementation was an approximation and
  is not part of the new backend.
- Function signatures, native offsets, view indices, and shader layouts are game-version-specific
  and must fail clearly when their validated contract no longer holds.

## Fixed prototype inputs to revalidate

- Donor material:
  `chara/equipment/e0378/material/v0002/mt_c0101e0378_top_a.mtrl`
- Expected shader package: `charactertransparency.shpk`
- Neutral texture candidate: `chara/common/texture/white.tex`

The new implementation must explicitly verify the donor's shader package before submitting. The
archived prototype loaded the material but did not enforce that check.
