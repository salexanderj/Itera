---
paths:
  - "src/Itera.Engine/Rendering/Grass/**"
  - "src/Itera.Engine/Rendering/GpuData/{GrassFrame,GrassBlock}.cs"
  - "src/Itera.Engine/Rendering/Shaders/{grass-data.glsl,grass-place.comp,grass-count.comp,grass.vert,grass.frag}"
  - "src/Itera.Engine/GrassServiceCollectionExtensions.cs"
  - "tests/Itera.Engine.Tests/Rendering/Grass/**"
---

## Grass

Blades of grass made each frame on the GPU from the voxel terrain the camera draws. Related files: `rendering-passes.md`
(the stages the two passes record at) and `rendering-world.md` (`WorldMeshes.CameraCommands`, the heap).

### A game opts in

- A game calls `AddIteraGrass` with its own `IGrassSource`. It registers the source, `GrassPlacePass`, and
  `GrassDrawPass`, in that order. The two passes count among the game's render passes (`GpuTimings.MostGamePasses`).
- The starter game does not call it, so Itera's own validation runs never draw grass.
- Each frame the place pass asks the source's `TryGetLook` for a `GrassLook`: the material blades stand on and the
  field's numbers. False, no density, or no radius places nothing, and nothing is drawn.

### Placing

- `GrassPlacePass` records at the `Compute` stage. Its `Write` lists the camera's blocks whose centres lie within the
  look's radius plus `GrassPlacePass.BlockReach` blocks of the finest level (`WorldMeshes.FinestBlockMetres`), nearest
  first, at most `GrassPlacePass.MostBlocks`. It writes them and the slot's `GrassFrame` (`GrassPlacePass.FrameOf`).
- `grass-place.comp` runs one invocation a triangle of those blocks. It reads the triangle from the mesh heap, and the
  block's draw record from the mesh pass's set.
- A triangle makes its area times the density in candidates. Each is seeded from the block's absolute corner and the
  triangle's corners, so the same ground grows the same blades in every frame and every game.
- A candidate stands where four things together pass its own threshold: the material's share of the ground (its
  corners' weights), the slope, the thinning from the look's full density out to its radius, and the hue cover.
- It grows from nothing as they pass it. So craters, paths, and the field's edge clear blades without popping.
- A first round counts the blades that stand and marks them, a bit each. The second writes only those, so the hue
  cover's reads are made again only for blades that stand.
- The hue cover compares the material's texture set's own colour, over the set's mean, with grey as unit directions
  (`groundCover`). A texel past the look's `HueTolerance` holds fewer blades, and past twice it none. So a patch of
  another hue in a texture (bare ash in a grass) stays bare.
- Each blade takes the ground's colour under it as `mesh.frag` draws it: the texture set read at a fixed footprint, the
  patches, the tint map, and the look's world values.
- Near blades have three segments, far ones one triangle. Each list has room for `GrassPlacePass.NearMost` or
  `FarMost`. A triangle reserves its blades with one atomic add a list.
- `grass-count.comp` then writes the two indexed indirect draws.
- The blades and the draws are one copy each, on the device. Each fresh frame waits for the last frame's draws before
  writing them. A frame presented again draws the last blades made.
- Nothing is placed under an orthographic lens or with no block near.

### Drawing

- `GrassDrawPass` records at the `Opaque` stage: one `CmdDrawIndexedIndirect` of the two draws, over an index pattern of
  `GrassPlacePass.BladesAnInstance` blades an instance. It owns no buffer: it takes the place pass's.
- `grass.vert` builds a blade from its record: a curve leaned along its facing and swayed by gusts that drift over the
  ground. It tapers to its tip, is at least the look's `LeastPixels` wide at its root, and turns its width towards the
  eye when seen edge on.
- Its colour runs from the look's `RootShade` of the ground's at its root to `TipLight` at its tip. Through the thinning
  band its shading fades into the ground's, so a far blade that comes and goes changes the picture little.
- `grass.frag` lights it cheaply: one filtered lookup of the shadow cascade that holds the pixel (`sunShadowOnce`), the
  sky field, and no point lights. A far blade, a few pixels, takes both from its corners (`grass.vert`), not its
  pixels. It then takes the fog, the tone curve, and the grade as `shadeSurface` in `mesh-shade.glsl` does.
- Blades are solid, drawn on both faces, and write depth. They cast no shadow and show in no image view.

### Sync

- The heap's upload barriers name the compute stage (`MeshPass.RecordUploads`), so the place pass reads this frame's
  meshes.
- The texture sets' first copy names only the fragment stage (`MeshPass.Textures.cs`). It happens under the loading
  screen, frames before any compute stage records.

### Tests

`GrassPassTests`.
