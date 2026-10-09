# The starter game

`src/Game` is the starter: a small game that runs every part of the engine, for a clone to grow into its game. This
file is the game's own; a clone rewrites it to describe its game. Replace this paragraph with what the game is.

- Name: "Itera Game" (`Engine:Application:Name`), data folder `IteraGame` (`~/.local/share/IteraGame/`), no Steam App ID
  (Steam stays off).
- The world (`WorldGeneration/`): `GameGenerator` (`game.world`, version 1) on the engine's `HeightfieldGenerator`,
  10 cm voxels: gentle grass hills of `FractalNoise`, about 6 m high and up to 10 m up or down, gravel where the ground
  lies below 2 m, 5 cm of surface over dirt down to 1 m, then stone, and stone boulders (2 to 6 m across) a third of their
  height buried, at most one a 16 m cell (`JitteredGrid`). The materials are `GameMaterials` (grass, dirt, stone, gravel), drawn by
  `GamePalette` from the texture sets `grass`, `dirt`, `stone`, and `gravel` in `Textures/`. The sky is one clear day
  (`GameAtmosphere`). A change to what the generator makes raises its `Version` (`tests/Game.Tests/WorldTests` pins a
  hash).
- The player starts at the world's middle, its eye a person's height over the ground or a boulder there
  (`GameGenerator.SurfaceHeight`), looking along -z (`StartPose`; `Game:World:CameraPose` sets another, which must lie in
  the play area). `PlayerSpawnSystem` makes it with the engine's components; the engine's systems
  move it (W A S D, Shift, Space, Ctrl to crouch, Tab for third person; the console's `fly` for the fly camera).
- Options: `Game:Player` (`HeightMetres` 1.7, 1.2 to 2.5; `StartFlying`; `ThirdPerson`) and `Game:World`
  (`CameraPose`), checked by `PlayerOptionsValidator` and `WorldOptionsValidator`; a key under `Game` in no section of
  the game's stops the game (`GameServiceCollectionExtensions.ValidateOptions`). Every value in `appsettings.json` is
  the code's default except the name, data folder, and window title (`ShippedSettingsTests`).
- The benchmark's views (`BenchmarkViews`), also `tp`'s places (`tp far`; `game.py shots far`): `start`, `start-low`,
  `down`, `up`, `high` (40 m over the middle), and `far`
  (200 m out along +x, looking back).

## Systems

Registered in `GameServiceCollectionExtensions` in run order; `tests/Game.Tests/SystemOrderTests` checks the order.

- Fixed: `PlayerSpawnSystem` (makes the player at the start), `MapResetSystem` (`cleanup`, `reset`), `DebugBallSystem`
  (`balls`), `PlayerInputSystem`, `PhysicsReadSystem`, `PlayerMovementSystem`, `StandInSystem`, `LooseBodySystem`,
  `StreamingCentreSystem`.
- Frame: `PlayerCameraSystem`, `DebugFlyCameraSystem`, `BenchmarkSystem` (with `Engine:Benchmark:Enabled`),
  `DebugOutlineSystem`, `CameraExtractSystem`, `LooseBodyModelSystem`, `AtmosphereSystem`.

Every system but `PlayerSpawnSystem` is the engine's (`.claude/rules/systems.md`). A game's own system goes in this
order where its comments say why, with a behaviour test and a zero-allocation test in `tests/Game.Tests`.

## Files

- `src/Game`: `Program.cs` (the host, then `GameLoop.Run()`), `GameServiceCollectionExtensions` (`AddGame`: options,
  world, systems), `GameOptions.cs`, `StartPose.cs`, `BenchmarkViews.cs`, `GameRunStartup.cs` (the movement keys first,
  then the game's), `Systems/PlayerSpawnSystem.cs`, `WorldGeneration/`, `Textures/`, `appsettings.json`.
- `tests/Game.Tests`: `SystemOrderTests`, `ShippedSettingsTests`, `GameOptionsTests`, `WorldTests`, `PlayerSpawnSystemTests`,
  `BenchmarkViewsTests`, and `ScenarioTests` (the starter played without a window through `HeadlessMatch`).
