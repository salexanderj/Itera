# Rendering: text moved out of the rules files

Recorded 2026-10-19, when `.claude/rules/rendering.md` was cleaned and split into `rendering.md`, `rendering-world.md`,
`rendering-light.md`, `rendering-look.md`, `rendering-materials.md`, `rendering-mesh-assets.md`, `rendering-passes.md`,
`rendering-views.md`, and `rendering-present.md`, and `mesh-assets.md` and `particles.md` were cleaned. The rules files
state what is true now. This file keeps what they held that is history, a measurement, a value of an option, or a
detail of the starter game. Each passage is quoted as it was.

## History

- Where the shelved raymarcher is (from the opening paragraph of `rendering.md`): "the shelved raymarcher is on the
  branch `raymarcher` and the tag `raymarcher-final` of the Zorf TD archive, `salexanderj/ZorfTD-archive`".
- The sun's image axis (from "The sun moves as a game says"): "a sun that never moved keeps the fit's own axis, as
  before".
- The section "Underground light, a first try" held the sky field and the point lights. Both are described as they
  work now in `.claude/rules/rendering-light.md`.
- Why the far cascade is not balanced: "the far one is chosen in a second pass one level coarser (`FarShadowCutShare`),
  which is not balanced: it draws plain meshes and needs no stitched seams, and balancing it split its columns back to
  the camera's".
- Sorting the camera's commands: "so the depth test rejects hidden leaves before they are shaded, and runs repeat (in
  load order they did not; the shadow lists stay unsorted)".
- The materials' blend: "the border between materials wanders round the stones of their height maps instead of
  stepping at 10 cm (a hard edge, by choice; the models keep their vertices' material)".

## Measurements

- Reading the picture under a `Screen` pass: "A copy at 2560x1440 moves about 30 MB; a blur of 24 taps and a grey over
  it took about 0.12 ms at 849x1390 (`docs/rendering-extension.md`)."
- Stitched seams: "About nine in ten of a seam's open edges close; the levels' samples disagree in a few places."
- Compressed textures: "a quarter of the decoded size (the fox's 1024-texel colour: 1.3 MB against 5.3 MB)".
- Texture sets: "a 4-texel stand-in for a group with none, never written: every set is 1024 texels today, so the large group
  holds only that".
- Dynamic render scaling: "A step every 8 frames measured: down at once by the square root of the time's excess, up
  by at most 0.02."
- Image views and multisampling: "a view from above takes every block round it at the camera's finest levels, many
  triangles smaller than its texels, and four samples doubled an update's time".
- The offscreen frame cap: "so a low cap slows loading (ARCHITECTURE.md has the measurement)".

## Values of options and constants the rules files gave

The values are in the code and in `docs/reference/engine-options.md`. These were the values written in the rules
files.

- Levels and distance: the world was meshed "from its 10 cm voxels near the camera (to 16.8 m) through 20 and 40 cm to
  80 cm far (four levels stream by default, `Engine:Voxels:Levels`), out to `Engine:Renderer:ViewDistanceMetres` (256
  m)".
- Shadows: "two shadow cascades (a third for the distance under a low sun with `Engine:Renderer:DistantShadowMetres`)".
  The cascades were squares "round the bounding sphere of the view's slice from 0 to 30 m and from 25 m to 200 m or the
  view distance", reaching "100 m further towards the sun, and further as the sun sinks: `MeshView.CasterReach`, up to
  300 m". The distant cascade ran "from where the far one starts to fade (180 m) to that depth or the view's end", was
  drawn "only while the sun is under 30 degrees over the horizon (`MeshView.DistantSunDegrees`) and faded in down to
  25", had casters "reaching three times as far towards the sun (`DistantCasterShare`)", and had texels "about 2.4 m
  at 2 km". `Engine:Renderer:DistantShadowMetres` was "(0, none, by default; 400 to 8000)".
- Shadow maps: `Engine:Renderer:ShadowMapTexels` "2048 by default"; the sun shadow had "a 1.5-texel normal offset", "an
  8-tap blocker search", "16 comparison taps of a Poisson disc, 1 to 16 texels", and the near cascade blended "over
  the 5 m before its end"; "the far shadows fade over the last tenth of theirs".
- The mesh heap: `Engine:Renderer:MeshMegabytes` "512 by default"; an eviction freed "at least 16 MB more than the
  mesh needs and at most 128 MB a frame"; a pool of "64 stitch jobs".
- Levels: `Engine:Renderer:MeshReachShare` "0.5 by default"; `Engine:Renderer:MeshFinestLevel` "0 by default".
- Sky field: "a box of 200 x 100 x 200 cells round the view, a cell being a voxel of the level nearest 64 cm
  (`SkyField.LevelFor`: level 3, 80 cm, in the game, a box of 160 x 80 x 160 m)"; read "at most 1 ms a frame"; "air
  under open sky is 255, and a worker spreads it through air keeping 0.85 a cell"; built again "when the view is 32 m
  from its middle or 16 m above or below it, or 2 s after an edit or a column of the read level loaded (a failed build
  is tried again after 2 s)"; read "never below 0.02".
- Point lights: lights "within 48 m whose reach (power over 0.02, square-rooted, at most 16 m)"; the budget
  `Engine:Renderer:MostLights` "128 by default, at most `MeshPass.MostLights`, 256"; clusters "16 x 9 tiles of the
  drawn image by 24 slices of depth (the first to 1 m, the last from 48 m, between them a fixed ratio)", each keeping
  "up to 32" lights.
- Mesh assets: "at most 16,384 a frame" instances and "at most 32,768 a frame" palettes; `MeshAssetPass.MaxDraws`
  (16,384) and `MaxCommands` (4,096); `Engine:Renderer:SkinMegabytes` "16 by default"; `MostSkinnedVertices`
  "4,194,240"; "36 bytes" per `MeshAssetVertex`; the texture cap was "less the mesh pass's nine" images and "the 25 other
  descriptors and attachment a stage holds".
- Image views: "a size in texels, 16 to 4096, and how often it is drawn, once in 1 to 60 frames; at most
  `RenderViews.MostViews`, 4"; a view's records held "at most 8,192 blocks", the models' "16,384 draws, 4,096
  commands", and the mesh assets' seen list "4,096 draws, 1,024 commands".
- Game passes: "at most `GpuTimings.MostGamePasses` (8)"; the picture "freed once no pass has read it for 600 frames
  (`RenderPasses.PictureFramesBeforeFree`)".
- Render scale: dynamic scaling "on by default"; the scale ran "0.5 to 1"; `Engine:Renderer:DynamicScalingThreshold`
  "(120 by default; the menu offers 30, 60, 75, 90, 100, 120, 144, 165, 180, 240, 360)"; `Engine:Renderer:RenderScale`
  "(1 by default; the benchmark turns scaling off)"; `Engine:Window:OffscreenFps` "(240)"; `Engine:Renderer:MsaaSamples`
  "(4 by default)".
- The sun: "all from 2 degrees over the horizon, none from 2 degrees under, fading between"; the camera's near plane
  "`ViewConstants.Near`, 5 cm for the engine's cameras".
- Textures: filtered "16 times anisotropically"; sides "under 5 % of the blend not read"; 2048-texel sets in the large
  arrays and 1024-texel ones in the small; the bake tile "2.56 m"; images "1024-texel"; `materials ... all` used "the
  first small set, 3 m wide".
- Particles: the pool "(262,144 by default)"; the camera's near limit "within 0.6 m of the camera"; a bounce at "0.3 m
  and its step" behind the depth image.
- `SharpGLTF.Core` was pinned in the notes as "1.0.7 (MIT, no dependencies)".

## The starter game

- The packaging of a game's shaders: "(the starter has none until it adds a shader; the mesh sample's
  `Itera.MeshSample.csproj` has it ...)".
- The minimap as the example of an orthographic view: "the starter's minimap gives `new CameraView(eye, turn, 0,
  float.NaN, CameraProjection.Orthographic, metres, float.NaN)`".
- Texture sources: "`SOURCES.md` lists every source and licence" (the starter's folder `src/Game/Textures/`).
- The fox's textures: "the starter's `FoxModelTests` checks `Fox.textures` against the fox's image".
