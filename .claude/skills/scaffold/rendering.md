# Adding to Itera: the look, lenses, passes, and image views

The `scaffold` skill's recipes for the look, lenses, passes, and image views. Each lists every file the addition touches, in order, and the test that fails when one is missed; finish with the skill's "Then".

## The game's look

1. The game's `IAtmosphereSource` (the starter's `GameAtmosphere`; `AtmosphereSystem` in the frame systems asks it at
   the camera's absolute place, a `WorldPoint`, each frame) gives the sky (`At`) and the look (`LookAt`): start from `RenderLook.Default` and change values
   with `with` (`RenderLook.Default with { Exposure = 0.9f, WorldSaturation = 1 }`). A place or a time of day may blend
   two looks (`RenderLook.Lerp`); it runs every frame, so it allocates nothing.
2. Try values live with the console's run-only `look NAME VALUE` (`look NAME R G B` for a colour; `look` lists them,
   `look reset` drops them), then copy the ones chosen into code.
3. A moving sun: the source's `At` gives the `Atmosphere` with its `SunDirection` (a day's course, `SunPath.AtHour` the
   test day's), and the sky's and the look's colours for the hour. From 2 degrees under the horizon the sun gives no
   light and no shadow maps are drawn; for a moon, point `SunDirection` at the moon, above the horizon, with a dim, cool
   `SunColour` (the look's disc sharpness and strength draw its disc).
4. Check it: `game.py shots ... --compare OLD` against a set taken before, and a look at the PNGs (`time HH:MM` and
   `sun AZIMUTH ELEVATION` show other hours).

## The main camera's lens

The active camera (`CameraRules.CreateCamera`: `ActiveCamera`, `CameraPose`, `CameraLens`) keeps its lens until a system
changes it; `CameraExtractSystem` hands both to the renderer (`.claude/rules/rendering.md`, the camera's lens).

1. Try it first with the console's run-only `lens ortho HEIGHT [NEAR FAR]` (`lens off` gives the camera's own back).
2. A frame system before `CameraExtractSystem` writes `CameraLens.Orthographic(heightMetres, near, far)` into the
   camera entity (`float.NaN` for near and far: the view distance each way) and puts its `CameraPose` at the middle of
   what it shows; the player's field of view does not apply to it. A game whose camera is not at the player's eye
   (a top-down or strategy view) registers its own camera system in place of `PlayerCameraSystem` and `DebugFlyCameraSystem`, which makes the camera entity with `CameraRules.CreateCamera` as `PlayerCameraSystem` does. It goes in `SystemOrderTests`, with a behaviour test and a zero-allocation test.
3. Check it: `game.py shots` of its views (`x,y,z,yaw,pitch`), and `validate.sh --offscreen`.

## A render pass of the game's own

GPU work the engine does not do (a highlight, an outline, a screen effect, a compute pass) is a render pass; the
mesh sample's `AimHighlightPass` (`samples/Itera.MeshSample/Rendering/`) is a short one. Load the `vulkan` and
`ecs-vulkan` skills first, and read `.claude/rules/rendering.md`, "Passes a game adds".

1. A class implementing `IRenderPass`: its `Name` and `Stage`. `Initialize(setup)`: a host-visible buffer with a region a
   frame slot (`setup.Allocator.CreateBuffer(..., MemoryUsage.Upload, ...)`, `RenderPassSetup.FramesInFlight`
   regions), a set of it (`setup.CreateBufferSet`), a layout with the view's push constants (`setup.CreateLayout`,
   `sizeof(ViewConstants)`), and a pipeline for the stage's targets (`setup.CreateTriangles`). `Write(slot, frame)`
   writes the slot's region, write only, from what a frame system left in a run-wide service; `Record(context)`
   flushes the slot's region, binds, pushes `context.View`, and draws; `OnTargetsChanged` makes the pipeline again when
   the colour format or samples changed; `CompileShaders` and `ReplacePipelines` for shader reloads; `Dispose` safe
   twice and when `Initialize` never ran (runs without graphics).
2. Register it with the run-wide service it reads: `services.AddSingleton<IRenderPass, MyPass>()`. The match's frame
   system that fills the service clears it in its `Dispose`, since the pass outlives the match.
3. Its shaders go in the game's `Shaders/` folder, copied to `Shaders/Game/` (the sample's csproj item), and include the
   engine's `../view.glsl` (`viewClip`, `view.slot`); the struct they read is `layout(scalar)`, with a `GpuLayout` test
   in the game's tests (the sample's `AimHighlightPassTests`).
4. Check it: the log's `Render pass of the game's: NAME at the STAGE stage`, `validate.sh` (with `--binary` for a game
   outside `src/Game`) windowed and `--offscreen`, a screenshot of it, and its time in `game.py gpu` (`Game NAME`) and
   the Debug line `GPU passes over ...`'s `game passes`.

## An image view (a minimap, a camera's picture)

A view of the world the engine draws into an image (`RenderViews`; `.claude/rules/rendering.md`, "Image views"); the
starter's minimap (`src/Game/Minimap.cs`, `MinimapLayer.cs`, `Systems/MinimapSystem.cs`) is the example.

1. Declare it in the game's `IRunStartup.Start`: `views.Declare(name, width, height, everyFrames)` (keep the index),
   and its key (`KeyBindings.Register`) and a saved on/off (`PlayerSettings.RegisterSwitch`) if the player turns it on
   and off ("A game's own on/off setting", below).
2. A frame system (after `CameraExtractSystem`) asks for it each frame it should be drawn: `views.Draw(index, camera)`
   with a `CameraView` (an orthographic one: `CameraProjection.Orthographic`, a height in metres, NaN near and far for
   the view distance each way round its position). It goes in `SystemOrderTests`, with a behaviour test
   (`RenderViews.Asked` gives what it asked) and a zero-allocation test.
3. To show it on the HUD, an `IMatchUiLayer` calls `views.Show(index, x, y, width, height)` in its `Bounds` (asked
   every frame it shows, at the screen's size) and draws its frame and marks round the rectangle, leaving the inside
   clear; register it `services.AddScoped<IMatchUiLayer, MyLayer>()`. The engine's stats line sits in the top-right
   corner too: keep clear of it (the starter's `MinimapLayer` takes it).
4. To show it on a mesh material (a screen in the world), call `views.ShowOnMaterial(index, assetName, materialName)`
   in the same startup: the material shows the picture for its colour, and for its emission when it glows, through its
   colour's texture coordinates (0 to 1 across the picture, v 0 at its top); the mesh sample's `YardCamera` and the
   yard's `yard-screen`.
5. Check it: the log's `Image view NAME: ...` line, `validate.sh` windowed and `--offscreen`, a screenshot with the UI
   (`screenshot 1 PATH`, not `noui`), and its cost in `game.py gpu` (`ImageViews`, a time per update).
