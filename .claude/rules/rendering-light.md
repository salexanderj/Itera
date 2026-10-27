---
paths:
  - "src/Itera.Engine/Rendering/{ShadowCascades,PointLights}.cs"
  - "src/Itera.Engine/Rendering/Meshes/{MeshPass,MeshView,ViewVolume,CascadeBounds,SkyField,WorldMeshes.Sky,WorldMeshes.Lights}.cs"
  - "src/Itera.Engine/Rendering/GpuData/MeshLighting.cs"
  - "src/Itera.Engine/Rendering/Shaders/{lighting.glsl,sky.glsl,air.glsl,light-halos.glsl,mesh.frag,mesh-light.glsl,mesh-shade.glsl,mesh-clusters.comp,mesh-shadow.vert,depth-only.frag,world-sky.frag}"
  - "tests/Itera.Engine.Tests/Rendering/{ShadowCascadesTests,PointLightsTests}.cs"
  - "tests/Itera.Engine.Tests/Rendering/Meshes/{MeshPassTests,MeshViewTests,SkyFieldTests}.cs"
---

## Shadows, the main pass, and lights

The main pass draws the world, the models, and the mesh assets with one lighting model. Related files: `rendering.md`
(the core), `rendering-world.md` (what is drawn), `rendering-look.md` (the values the shaders read), and
`rendering-mesh-assets.md`.

### The frame's view

`MeshView` is the frame's view. `GameLoop` starts it before `WorldMeshes.Upload` and ends it after. Its frustum,
cascades, and lights are shared by every list `MeshPass` draws. It writes the slot's `MeshLighting` record: the
cascades, the sky field's corner and cell, and the light count.

- `ViewVolume` holds the frustum, the fitted cascades, the pixels a metre covers (`PixelsPerMetre`), and the cascades'
  texel widths. `MeshView` sets its own at `Begin` and passes these on.
- `MeshView.Predict` sets another volume as `Begin` would, before the frame's `Begin`, with the image axis the view
  carries. `MeshPoseCulling` culls the posed meshes by it (`rendering-mesh-assets.md`). Change the fit in
  `ViewVolume` alone, so the two never differ.

### Shadow cascades

`ShadowCascades.Fit` fits the sun's cascades to the view.

- There are two cascades: a near one over the first slice of the view, and a far one over the rest.
- `Engine:Renderer:DistantShadowMetres` adds a third, the distant cascade, from where the far one starts to fade.
- The distant cascade is drawn only while the sun is low (`MeshView.DistantSunDegrees`) and fades in as it lowers
  (`MeshView.DistantStrength`, `MeshLighting.DistantStrength`). `MeshView.DistantShadows` says whether the frame draws
  it. Its casters reach further towards the sun (`DistantCasterShare`).
- `ViewVolume.Set` fits them. Each cascade is a square round the bounding sphere of its slice. Its centre snaps to whole
  texels. It reaches towards the sun as far as the casters need, and further as the sun sinks (`MeshView.CasterReach`).
- While the sun moves, `MeshView` carries the maps' image axis from frame to frame (`ShadowCascades.ImageAxis`: the
  least turn that keeps it square to the sun, also overhead). A sun that never moved keeps the fit's own axis. A
  match's end forgets the sun (`MeshView.ForgetSun`).
- With the view's shadows on, `WorldMeshes` lists the blocks that cast into each cascade (`CascadeBounds`): those
  whose bounding sphere meets the cascade's box and lies inside each plane of its slice that faces the sun.
- The near cascade takes the camera's levels. The distant cascade takes them too, from the terrain's blocks alone, in
  its own list (`MeshPass.DistantList`, of the world's four `WorldLists`). The models and the mesh assets cast nothing
  into it.
- The far cascade chooses its blocks in a second pass one level coarser (`FarShadowCutShare`). That pass draws plain
  meshes.
- A full shadow list warns once a match.
- At night (`rendering-look.md`) `ViewConstants.Create` leaves the shadows switch off, so no caster is listed and the
  maps are only cleared.
- The shadow maps are layers of a depth map array of `Engine:Renderer:ShadowMapTexels` texels a side. A frame that
  does not draw the distant cascade (`MeshFrame.Distant`) does not draw its layer.
- A cascade draws only faces turned away from the sun (`mesh-shadow.vert`, `depth-only.frag`). The pass is timed as
  `ShadowMaps`, part of `Voxels`.
- An origin move changes no texel: the cascades snap in absolute terms (`ShadowCascades.Fit`'s origin).

### The main pass

`MeshPass.Record` draws, in this order:

1. The cluster pass (`mesh-clusters.comp`), the skinning (`rendering-mesh-assets.md`), and the game's `Compute` passes.
2. The cascades into the depth map array.
3. The models shown whole, the world's blocks, the fading models, the mesh assets (`rendering-mesh-assets.md`), then
   the game's `Opaque` passes. The models go first so the ground behind them fails the depth test before it is
   shaded. They draw with `Engine:Renderer:MsaaSamples` samples a pixel, into images of their own. Rendering resolves
   into the target (the mean) and into the depth image (sample 0, which the particles and the lines test against).
   The depth image's frame-start barrier covers the resolve, which Vulkan counts as a colour attachment write.
   The multisampled depth image is sampled where the device allows that count, though no shader reads it: RADV then
   keeps it compressed for the resolve.
4. The sky where the depth is still 0 (`world-sky.frag`).
5. The blended mesh assets, then the game's `Transparent` passes (`rendering-passes.md`).

Every block in view is one `vkCmdDrawIndexedIndirect`. `mesh.vert` pulls the vertices from the heap and projects them
through `view.glsl`'s `viewClip`, as every pass that draws into the view's depth does.

`mesh.frag` shades a surface in this order:

- The material's colour and tint (`rendering-materials.md`).
- `shadeSurface` in `mesh-shade.glsl`, shared by every surface the pass draws: the sun through the cascade that covers
  the point by view depth, with a normal offset (`sunShadow` in `mesh-light.glsl`).
- Sky and ground light, dimmed by openness and scaled by the sky field.
- The point lights, the air (`air.glsl`, `rendering-look.md`) and the lights' halos in it (`light-halos.glsl`), the tone
  curve, the grade, and the glow.
- A dither of up to one step of the 8-bit image (`ditherColour` in `lighting.glsl`), so smooth fog and sky show no
  bands. The sky, the grass, and the particle cubes dither their colour too.

The near cascade has contact-hardening shadows: a blocker search over the maps read as depths (binding 9, no
comparison), then comparison taps of a Poisson disc. The far and distant cascades use a 3x3 filter of comparisons, read as 4 bilinear comparisons with the same weights
(`shadowGrid`). The near
cascade blends into the far one before its end (`MeshLighting.NearBlendStart`). The far cascade's slice starts at
`MeshLighting.NearBlendStart`, so the two overlap where the near one blends into the far one. The far shadows fade at
their end (`FarFadeStart`) or blend there into the distant cascade's, which fade at theirs (`DistantFadeStart`).

`mesh.frag` sets the world's own saturation, value, and side and top shade. `mesh-shade.glsl` sets the sunlight's
warmth and the sky light's coolness. The split tone fades as the fog thickens, so the far ground meets the sky, which
takes the grade but no split tone.

A glowing surface keeps its colour, takes no grade or split tone, and adds its glow's own brightness through the tone
curve after them. Where a texture set's emission mask says only a share of it glows (`rendering-materials.md`), that
share keeps its colour and the rest is shaded as a surface that does not glow.

- SMAA does not run on a multisampled world.
- The pass's GPU time is `Voxels` (`GpuPass`).
- The aim dot is the HUD's (`AimDotLayer`, `ui.md`).
- The particles move after the world's pass and meet it through the depth image (`particles.md`). `MeshPass`'s set
  layout serves the compute stage for that, and its barriers after the cascades and the sky field's copy name it.

### The sky field

`SkyField` says how much of the open sky each cell of a box round the view sees. A cell is a voxel of the level
nearest 64 cm (`SkyField.LevelFor`). When the world has no level near 64 cm, the cells are voxels of the coarsest level, read
every few.

- The main thread reads the cells from the resident columns, a share of a millisecond each frame, over as many frames
  as it takes (`VoxelWorld.TryReadOwnBlock`: each block's own voxels, no apron). A block whose cell is one material or
  air is one fill. An edit made meanwhile may show in part of the field until the next build.
- Air under open sky is full. A worker spreads light through air, losing a share a cell, brightest first. Solid cells
  take their brightest air neighbour's.
- The field is built again when the view has moved far from its middle, or a while after an edit or a loaded column of
  the read level inside its box (`SkyField.Overlaps`, in absolute centimetres). A failed build is tried again after a
  while.
- It is staged whole in one frame and copied by `MeshPass.RecordSkyField` into an R8 3D texture (binding 8, linear,
  white border for open sky) with the frame's other copies, under the loading screen too.
- `mesh.frag` scales sky and ground light by it, half a cell in front of the surface, never to nothing.
- The box is absolute (`SkyField.Corner` and `Centre`). Its corner is written local each frame.

### Point lights

- Each world block's glowing vertices (a material's glow) give one light at their glow-weighted middle when the mesh
  is uploaded (`WorldMeshes.Lights.cs`).
- Events give others through `PointLights`, a run-wide service the frame systems fill. `Add` is for a light that
  shines while something happens. `Flash` is for one that fades as the square of its time left. `GameLoop` starts it
  each frame before the frame group and clears it at a match's end. A game lights its flashes, fires, and glows there.
- Each frame the lights of the drawn blocks (offered by `WorldMeshes` as it lists them) and of events (at
  `MeshView.End`) whose reach meets the frustum are ranked by power over distance squared.
- The brightest go into the slot's list, brightest first (`Engine:Renderer:MostLights`, at most
  `MeshPass.MostLights`; binding 7, `MeshLight`).
- The cluster pass sorts them into the view's clusters: tiles of the drawn image by slices of depth
  (`mesh-data.glsl`'s `meshClusterSlice`). Each cluster keeps the lights that matter most to it (binding 10, one
  region a slot).
- `mesh-shade.glsl`'s `pointLight` loops over its pixel's cluster's lights.
- A frame with no light (`MeshFrame.Lights`) sorts no clusters, and `pointLight` reads none (the lighting record's
  count).
- The console's `lights test N` scatters test lights for the run.

### Tests

`ShadowCascadesTests`, `MeshViewTests`, `SkyFieldTests`, `PointLightsTests`, and `MeshPassTests`.
