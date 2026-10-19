# The starter game

`src/Game` is the starter: a small game that runs every part of the engine, for a clone to grow into its game. This
file is the game's own, and a clone rewrites it to describe its game. Replace this paragraph with what the game is.

- Name: "Itera Game" (`Engine:Application:Name`), with the data folder `IteraGame` (`~/.local/share/IteraGame/`). It has
  no Steam App ID, so Steam stays off.
- The game's options, the engine's options its settings change, its console commands, its systems in their run order,
  and its other lists are generated in `docs/reference/game-*.md` (the `docs` skill).

## The world

- `GameGenerator` (`game.world`) builds the world on the engine's `HeightfieldGenerator`: 10 cm voxels, 8 km across
  (`GameGenerator.WorldGrid`). A change to what it makes, to its grid's size, or to its objects raises its `Version`.
  `tests/Game.Tests/WorldTests` pins a hash, the grid's bounds, the placements, and the kinds' shapes.
- The ground is gentle grass hills of `FractalNoise`, with gravel in the hollows and dirt, then stone, below the surface.
  Past 1 km from the middle, broad relief (`Relief`) rises over the hills.
- Voxel objects of 5 cm voxels stand on it (`GameObjects`, registered at the run's start; `GameForests`, the generator's
  placer): boulders, forest in patches (broadleaves and young trees), bushes, and rocks. Each is placed in a cell of a
  `JitteredGrid` by its cell's hash. None stands within 15 m of the middle, which holds the start and the fox's circle
  (the fox walks on the voxels and meets no object). No tree, bush, or rock stands on a boulder, and no bush or rock
  against a trunk.
- The materials are `GameMaterials`, drawn by `GamePalette` from the texture sets in `Textures/`. Characters and
  navigation pass through leaves (`GamePassability`).
- The sky is one clear day with a light haze (`GameAtmosphere`), drawn with the engine's look (`RenderLook.Default`)
  under the engine's sun. The console's `sun` and `time` move the sun for a run.
- The map is seen to its end: the shipped settings draw an 8 km view over nine levels (`docs/reference/game-options.md`).

## The player

- The player starts at the world's middle, its eye a person's height over the ground there
  (`GameGenerator.SurfaceHeight`), looking along -z (`StartPose`). `Game:World:CameraPose` sets another start, which
  must lie in the play area.
- The match's first origin is the start eye's whole metres (`StartPose` is the game's `IOriginStart`), and
  `OriginShiftSystem` moves it with the camera from there.
- `PlayerSpawnSystem` makes the player with the engine's components and a `Climber`, and the engine's systems move it.
- Climbing is the engine's `ClimbingMode`, registered as the game's `IMovementMode` with the tag it climbs.
  `GameSurfaceTags` tags the boulders' stone, and the ground's stone where dug down to it, as climbable. Space with a
  boulder just ahead grabs it instead of jumping. On it W and S climb, A and D move along it, nothing held hangs, Space
  jumps off, and Ctrl lets go; climbing past its top pulls the player up onto it.

## The fox

- The fox (`GameModels.Fox`) is Khronos's Fox sample (`Models/Fox/`, credits in `Models/SOURCES.md`), with its clips
  Survey, Walk, and Run and its texture compressed in `Models/Fox/Fox.textures/`. `GameRunStartup` declares it.
- `FoxSystem` puts it on the ground in front of the start, then over and over it looks about, walks, and runs round a
  circle beyond where it started (`FoxRoute`), each clip fading into the next (`FoxSystem.FadeSeconds`).
- Its circle's centre moves with the match's origin (`FoxRouteShift`, the game's `IOriginShiftHandler`).

## The minimap

- `Minimap`, `MinimapSystem`, and `MinimapLayer` show the engine's image view `minimap`: an orthographic camera at the
  player's eye looking straight down, north (-z) up, `Game:Minimap:Metres` across.
- It shows in the HUD's top-right corner (below the stats line while that shows), with a thin dark frame and a white
  arrow turned to where the player looks. `MinimapLayer` is a scoped `IMatchUiLayer`.
- It is on from the start. M (the Keybindings tab's Minimap) hides and shows it, kept in `settings.json`. The benchmark
  hides it.

## Options and views

- The game's options are `Game:Player`, `Game:World`, and `Game:Minimap`, checked by `PlayerOptionsValidator`,
  `WorldOptionsValidator`, and `MinimapOptionsValidator`. A key under `Game` in no section of the game's stops the game
  (`GameServiceCollectionExtensions.ValidateOptions`).
- Every value in `appsettings.json` is the code's default, except the name, the data folder, the window's title, the view
  distance, and the levels (`ShippedSettingsTests`).
- The benchmark's views (`BenchmarkViews`) are also `tp`'s places: `start`, `start-low`, `down`, `up`, `high`, `far`,
  `distant` (in the broad relief, the origin moved there), `forest`, and `forest-high`.
- `GameForests.ForestSpot` finds the forest views' spot: the first one out from the middle where the forest is whole
  round it and no object stands within reach. `GameForests.ClearSpot` moves `far` and `distant` until no object stands
  within reach.

## Systems

The game registers its systems in `GameServiceCollectionExtensions` in run order, each registration's comment saying
why it sits there. `docs/reference/game-systems.md` lists the order, and `SystemOrderTests` pins it. Every system but
`PlayerSpawnSystem`, `FoxSystem`, and `MinimapSystem` is the engine's (`.claude/rules/systems.md`). A game's own system
goes where its comment says, with a behaviour test and a zero-allocation test in `tests/Game.Tests`.

The tests (each name says what it checks): `SystemOrderTests`, `ShippedSettingsTests`, `GameOptionsTests`,
`WorldTests`, `ForestTests`, `PlayerSpawnSystemTests`, `FoxSystemTests`, `MinimapSystemTests`, `MinimapLayerTests`,
`ChecklistTests`, `FoxModelTests`, `BenchmarkViewsTests`, `MovementAgreementTests`, `ScenarioTests`, `DocumentationTests`, and `ReferenceTests`.
