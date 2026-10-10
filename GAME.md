# The starter game

`src/Game` is the starter: a small game that runs every part of the engine, for a clone to grow into its game. This
file is the game's own; a clone rewrites it to describe its game. Replace this paragraph with what the game is.

- Name: "Itera Game" (`Engine:Application:Name`), data folder `IteraGame` (`~/.local/share/IteraGame/`), no Steam App ID
  (Steam stays off).
- The world (`WorldGeneration/`): `GameGenerator` (`game.world`, version 1) on the engine's `HeightfieldGenerator`,
  10 cm voxels: gentle grass hills of `FractalNoise`, about 6 m high and up to 10 m up or down, gravel where the ground
  lies below 2 m, 5 cm of surface over dirt down to 1 m, then stone, and stone boulders (2 to 6 m across) a third of
  their height buried, at most one a 16 m cell (`JitteredGrid`). The materials are `GameMaterials` (grass, dirt, stone,
  gravel), drawn by `GamePalette` from the texture sets `grass`, `dirt`, `stone`, and `gravel` in `Textures/`. The sky
  is one clear day (`GameAtmosphere`), drawn with the engine's look (`RenderLook.Default`) under the engine's sun, which
  stays where it is (the console's `sun` and `time` move it for a run). A change to what the generator makes, or to its
  grid's size, raises its `Version` (`tests/Game.Tests/WorldTests` pins a hash and the grid's bounds).
- The fox (`GameModels.Fox`): Khronos's Fox sample (`Models/Fox/`, credits in `Models/SOURCES.md`; 24 joints, clips
  Survey, Walk, and Run; its texture compressed in `Models/Fox/Fox.textures/`), declared at the run's start (`GameRunStartup`). `FoxSystem` puts it on the ground 4 m in front
  of the start pose, side on, then over and over: it looks about (Survey) for 6 s, walks (1.1 m/s) for 8 s and runs
  (4 m/s) for 6 s round a 3 m circle beyond where it started, on the voxel ground (`FoxRoute`), each clip fading into
  the next over 0.3 s (`FoxSystem.FadeSeconds`). Its circle's centre moves with the match's origin (`FoxRouteShift`,
  the game's `IOriginShiftHandler`).
- The player starts at the world's middle, its eye a person's height over the ground or a boulder there
  (`GameGenerator.SurfaceHeight`), looking along -z (`StartPose`; `Game:World:CameraPose` sets another, which must lie in
  the play area). `PlayerSpawnSystem` makes it with the engine's components and a `Climber`; the engine's systems
  move it (W A S D, Shift, Space, Ctrl to crouch, Tab for third person; the console's `fly` for the fly camera).
- Climbing (the engine's `ClimbingMode`, registered as the game's `IMovementMode` with the tag it climbs; stone, the
  boulders, tagged climbable by `GameSurfaceTags`):
  Space with a boulder within 0.3 m ahead grabs it instead of jumping; on it W and S climb at 1.5 m/s, A and D move
  along it at 2 m/s, nothing held hangs, Space jumps off it (4 m/s away, 5 m/s up), and Ctrl lets go; climbing past
  its top pulls the player up onto it where there is room to stand. Its state per player is `Climber`.
- The minimap (`Minimap`, `MinimapSystem`, `MinimapLayer`): the engine's image view `minimap` (512 texels square,
  drawn at most every 4 frames, one sample a pixel), an orthographic camera at the player's eye looking straight down,
  north (-z) up, `Game:Minimap:Metres` across (64), shown in the HUD's top-right corner a fifth of the screen's height
  square (below the stats line while that shows) with a thin dark frame and a white arrow at its middle turned to where
  the player looks (`MinimapLayer`, a scoped `IMatchUiLayer` registered in `AddGame`); on from the start, M (the
  Keybindings tab's Minimap) hides and shows it, kept in `settings.json` (its `Game` section's `Minimap`); the benchmark
  hides it.
- Options: `Game:Player` (`HeightMetres` 1.7, 1.2 to 2.5; `StartFlying`; `ThirdPerson`), `Game:World`
  (`CameraPose`), and `Game:Minimap` (`Metres` 64, 16 to 256), checked by `PlayerOptionsValidator`,
  `WorldOptionsValidator`, and `MinimapOptionsValidator`; a key under `Game` in no section of
  the game's stops the game (`GameServiceCollectionExtensions.ValidateOptions`). Every value in `appsettings.json` is
  the code's default except the name, data folder, and window title (`ShippedSettingsTests`).
- The benchmark's views (`BenchmarkViews`), also `tp`'s places (`tp far`; `game.py shots far`): `start`, `start-low`,
  `down`, `up`, `high` (40 m over the middle), and `far`
  (200 m out along +x, looking back).

## Systems

Registered in `GameServiceCollectionExtensions` in run order; `tests/Game.Tests/SystemOrderTests` checks the order.

- Fixed: `PlayerSpawnSystem` (makes the player at the start), `FoxSystem` (puts the fox at the start, then moves it and
  chooses its clip), `MapResetSystem` (`cleanup`, `reset`), `DebugBallSystem` (`balls`), `PlayerInputSystem`,
  `PhysicsReadSystem`, `PlayerMovementSystem`, `StandInSystem`, `LooseBodySystem`, `MeshAnimatorSystem` (the clips moved
  on, after every system that chooses one), `StreamingCentreSystem`.
- Frame: `PlayerCameraSystem`, `DebugFlyCameraSystem`, `BenchmarkSystem` (with `Engine:Benchmark:Enabled`),
  `DebugOutlineSystem`, `CameraExtractSystem`, `MinimapSystem` (the minimap round the player, after the camera's
  systems), `LooseBodyModelSystem`, `MeshDrawSystem`, `AtmosphereSystem`.

Every system but `PlayerSpawnSystem`, `FoxSystem`, and `MinimapSystem` is the engine's (`.claude/rules/systems.md`). A game's own system
goes in this order where its comments say why, with a behaviour test and a zero-allocation test in `tests/Game.Tests`.

## Files

- `src/Game`: `Program.cs` (the host, then `GameLoop.Run()`), `GameServiceCollectionExtensions` (`AddGame`: options,
  world: its generator and the engine's voxel terrain, `AddIteraVoxels`; systems), `GameOptions.cs`, `StartPose.cs`, `BenchmarkViews.cs`, `GameRunStartup.cs` (the movement keys first,
  then the game's, the models, and the minimap's view, key, and switch), `GameModels.cs`, `GameSurfaceTags.cs`,
  `Minimap.cs` (with its options), `MinimapLayer.cs`, `Systems/PlayerSpawnSystem.cs`, `Systems/MinimapSystem.cs`,
  `Systems/FoxSystem.cs`, `Systems/FoxRouteShift.cs`, `Components/FoxRoute.cs`, `WorldGeneration/`, `Textures/`,
  `Models/`, `appsettings.json`.
- `tests/Game.Tests`: `SystemOrderTests`, `ShippedSettingsTests`, `GameOptionsTests`, `WorldTests`,
  `PlayerSpawnSystemTests`, `FoxSystemTests`, `MinimapSystemTests` (the view asked round the eye, M, the benchmark, the
  HUD's square), `MinimapLayerTests` (inside its bounds, hidden while not shown or loading, below the stats line, the
  arrow's redraw, no allocation), `ChecklistTests` (every console command in the run-game skill's list, every numeric
  option validated, every game system with an allocation test, every vector of the game's components moved with the
  origin or listed as no position), `FoxModelTests` (the fox's rig and clips against
  SharpGLTF's own world matrices; its compressed textures made from its image), `BenchmarkViewsTests`, and
  `ScenarioTests` (the starter played without a window through `HeadlessMatch`; the fox read, drawn posed, its clip
  moving with the steps, walking its circle; the boulder nearest the start climbed onto; a walk with the origin
  following the player against one without).
