---
paths:
  - "src/Itera.Engine/Rendering/{RenderViews,RenderViewPass,CameraView}.cs"
  - "src/Itera.Engine/Rendering/Meshes/{RenderViewTarget,MeshPass.Views,MeshAssetPass.Views}.cs"
  - "samples/Itera.MeshSample/{Yard,YardCamera}.cs"
  - "samples/Itera.MeshSample/Systems/YardCameraSystem.cs"
  - "src/Game/Minimap.cs"
  - "src/Game/Systems/MinimapSystem.cs"
  - "tests/Itera.Engine.Tests/Rendering/RenderViewsTests.cs"
  - "tests/Itera.Engine.Tests/Rendering/Meshes/FakeViewLists.cs"
---

## Image views

A game draws views of the world into images of its own (`RenderViews`, run-wide), such as a game's minimap (the
starter's `Minimap`) or a screen in the world. Related files: `rendering.md` (the core, with the camera's lens),
`rendering-passes.md` (a game's passes, which do not draw into views), `rendering-mesh-assets.md` (the mesh assets a
view draws), and `docs/rendering-extension.md`.

### Declaring and asking

- A game declares each view at its run's start (`IRunStartup.Start`): a name, a size in texels, and how often it is
  drawn, once in some frames. `RenderViews.MostViews` limits the count.
- The run's "image views" step, after "mesh assets on the gpu", makes the views and seals the declarations.
- Each frame it wants one drawn, a frame system asks for it with a camera (`RenderViews.Draw`, any lens; `Asked` reads
  the ask back). The view is drawn on such a frame once its frames since the last update reach its cadence.
- The camera is a `CameraView`. An orthographic view from above is `new CameraView(eye, turn, 0, float.NaN,
  CameraProjection.Orthographic, metres, float.NaN)`. A near and far that are not finite mean the view distance each
  way (`ViewConstants.Create`). A perspective camera works too.

### Showing a view

- A HUD layer shows a view in one rectangle of screen pixels each frame (`RenderViews.Show`, from its `Bounds`, which the
  UI surface asks every frame the layer shows, at the screen's size).
- A later rectangle for a view in a frame replaces its earlier one. A rectangle whose position or size is not finite,
  or whose width or height is not above 0, is ignored.
- The UI pass draws the view under the UI's pixels (`ui.frag`'s `views[4]`, binding 1, a 1x1 stand-in where there is
  none; the panels in `UiConstants`), dimmed with the picture. A view shows its last update until its next.
- A mesh material shows a view too: `RenderViews.ShowOnMaterial(view, asset, material)`, by the run's asset and
  material names, at the run's start. The mesh sample's screen does this.
  - Each declared view has a slot of the asset pass's texture array after the asset textures. It is a white stand-in
    until the views are made, then its shown image, in the pass's set and every view's (`MeshAssetPass.SetViewImages`).
  - The material reads the slot for its colour, and for its emission when it glows, clamped, through the colour's
    texture coordinates (`MeshAssetMaterial.ShowingView`).
  - A name no asset has is a Warning at "mesh assets on the gpu".
  - A view that sees a material showing itself shows its last update there.
  - The picture was tone-mapped and graded when it was drawn, and the material shades it again as any texture.

### What a view draws

- The world, the models, the mesh assets, the sky, and the point lights, without shadows (the sun lights it
  unshadowed) and without particles.
- One sample a pixel whatever the world's. `MeshPass` and `MeshAssetPass` make their camera pipelines again at one
  sample while the world is multisampled (`WantViewPipelines`).
- The local player's own body (`ModelShadow.Only`), and not what the camera alone shows (`ModelShadow.None`, the
  first-person arms).
- The terrain's levels and meshes stay the main camera's. `WorldMeshes` lists for each view due (`IRenderViewLists`)
  the camera's leaf columns' blocks that its frustum meets, nearest first by its depth, and offers it their lights.
  It uses the stitched meshes where the camera's are. A block only a view sees asks for no stitched mesh.
- The models and the mesh assets list for it as for the camera. A posed instance only a view sees is skinned too.

### How it is recorded

Each view (`RenderViewTarget`, `Rendering/Meshes/`) has its own frame records, a region a slot.

- The world's draws and commands, the models', its lighting record (no shadows, the sky field's place), its point
  lights and their clusters, and the mesh assets' seen list and blended list.
- Its own sets of the mesh pass's and the asset pass's layouts name them, so it draws with their pipelines and shaders
  unchanged.
- `RenderViewPass` starts the due views after the frame systems (`BeginViews`: their constants from the asked camera at
  the view's size, the frame's sun and sky, no shadows; their `MeshView`s). It ends them with the events' lights
  (`EndViews`; the HUD's panels go into the slot).
- It records each after the world's pass: its clusters, then the world, the models, the mesh assets, the sky, and the
  blended assets into its colour and depth. Then it copies the colour into the image the shaders and the HUD read
  (`Shown`, cleared in the first frame). That image is always in the shader-read layout except during that copy, so no
  rendering reads the image it draws into.
- Nothing is recorded under the loading screen.
- The pass is timed as `GpuPass.ImageViews`, not part of `Voxels`.
- The view's constants carry the match's origin, set by `RenderViewPass` for each view (`rendering.md`).

### Without graphics

With `Engine:Window:Mode=None` or `HeadlessMatch`, the "image views" and "render passes" steps do not run. The
declarations are not sealed, no view is drawn, and no pass is made. `Declare`, `Draw`, `Asked`, and `Show` still work,
for a game's tests. The loop with `Mode=None` and `HeadlessMatch.Frame` forget each frame's asks and panels before the
frame systems, as with graphics. No UI is drawn there to call `Show`.

### Tests

`RenderViewsTests`, with `FakeViewLists`, and `WorldMeshesTests` for an image view's lists.
