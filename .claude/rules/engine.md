---
paths:
  - "src/Itera.Engine/*.cs"
  - "src/Itera.Engine/{Ecs,Time,Diagnostics,Modding,Control}/**"
  - "src/Itera.Engine/Matches/{HeadlessMatch,IRunStartup,MatchContext,MatchControl,MatchControlStates,MatchFlow,MatchIdentity,MatchServices,MatchStart,OriginShifts}.cs"
  - "tests/Itera.Engine.Tests/*.cs"
  - "tests/Itera.Engine.Tests/{Ecs,Time,Diagnostics,Modding,Control,Configuration}/**"
  - "tests/Itera.Engine.Tests/Matches/{EngineComposition,CompositionTests,MatchFlowTests,MatchLifetimeTests,HeadlessMatchTests,OriginShiftsTests}.cs"
---

## The engine

`src/Itera.Engine` holds everything that is not game specific. Each area's folder has its own rules file, listed in
`CLAUDE.md`. This file covers the composition, the run, the match, the origin, and the folders no other file covers.

### Composition and the run

- `EngineServiceCollectionExtensions.AddIteraEngine` composes the engine. `VoxelServiceCollectionExtensions.AddIteraVoxels`
  composes the voxel terrain.
- `EngineOptions` holds the options, and `EngineOptionsValidator` checks them. `UnknownOptionKeys` refuses keys no
  option has. A new option takes its range in the validator (`.claude/skills/scaffold`).
- `GameLoop` holds the run's initialisation order (one step list, `GameLoop.CreateSteps`), the main loop, and the shutdown. `EngineSteps` runs the steps.
- `Matches/MatchFlow.cs` holds a match's start, join, and end. It needs no window.
- `Matches/MatchServices.cs` holds a match's services, step list, and fixed step (`RunFixedStep`), with `MatchStart`,
  `MatchContext`, and `MatchControl` (with `MatchControlStates` for a game's tests of its menu).
- `IRunStartup` is where a game registers its run-wide registries.
- `Matches/MatchIdentity.cs` holds what a joining game must share (`IMatchIdentity`): the generator's and the object
  kinds' with terrain, else the application's name.
- A session takes its transports through `ISessionTransports`.
- `Matches/HeadlessMatch.cs` (`HeadlessRun`, `HeadlessMatch`, over `MatchFlow`) plays a match without a window or GPU,
  offline, hosting, or joining a host in the same process.

A game composes the engine in its `Program.cs`: `AddIteraEngine`, then its own composition, then the host with
`ValidateScopes` and `ValidateOnBuild`, then `GameLoop.Run()`.

- A game with voxel terrain registers an `IChunkGenerator` and calls `AddIteraVoxels()`. The voxel models, the loose
  bodies, the character mover, and the play area are the core's and read the world when there is one.
- A game without terrain plays matches with no world. `MatchServices.Terrain` is null, and its `Voxels` and the other
  terrain members throw.
- `IVoxelPalette`, `IVoxelTintMap`, `IVoxelPassability`, `MenuStyle`, and `IMenu` have engine defaults a game may
  replace (`.claude/rules/ui.md`).
- A game's own GPU passes are `IRenderPass` singletons it registers (`.claude/rules/rendering-passes.md`). Its image views are declared in its `IRunStartup` (`RenderViews`), as are its own on/off settings
  (`PlayerSettings.RegisterSwitch`, `.claude/rules/ui.md`).

### Folders

- `Ecs/`: `EcsWorld` (times each system), `IFixedUpdateSystem` and `IFrameUpdateSystem` (`SystemInterfaces.cs`),
  `EcsChecks`, `CheckedWorldExtensions`, and `EcsInspector`. The inspector reads archetypes, entities, and components by
  reflection for the console's `ecs` and the control port's `query`; it allocates, so commands only.
- `Components/` and `Systems/`: `.claude/rules/systems.md`.
- `Time/`: `GameClock`, `FixedStepAccumulator`.
- `Loading/`: `LoadingScreen`, `ILoadingTask`, `LoadingProgress`, `EngineLoadingTasks.cs` (`.claude/rules/ui.md`).
- `Diagnostics/`: Serilog setup (`LoggingServiceCollectionExtensions`), the session header (`SessionInfo`), loop stats
  (`FrameStats`), GPU pass times (`GpuTimings`), `ConsoleLogBuffer`, crash logging, and `ControlLog`.
- `Modding/`: mod loading (`ModLoader`, `ModLoadContext`, `ModDispatcher`).
- `Control/`: `ControlService`, `ControlRequest`, `InputScript`.
- `Maths/`: `WorldPoint` (an absolute point in double metres), `WorldOrigin` (the match's origin on whole metres, a
  scoped service), and `OriginShift`. The world, the mover, the bodies, and the mesh colliders share `WorldOrigin`, and
  every conversion between local floats and absolute points or centimetres goes through it (`CLAUDE.md`, Conventions).
  The rest of `Maths/` is in `.claude/rules/animation.md`.

Other folders: `Assets/` (`.claude/rules/mesh-assets.md`), `Replication/` (`.claude/rules/replication.md`), `Spatial/`
(`.claude/rules/spatial.md`), `Saves/`, `Networking/`, and `Matches/` (`.claude/rules/sessions.md`), `UI/`,
`Input/`, `Settings/`, and `Commands/` (`.claude/rules/ui.md`).

### The match's origin

The match's origin moves through `OriginShifts` (`Matches/`, scoped). `ShiftTo(point)` puts the origin on the whole
metres nearest the point, within the edit format's range, and moves everything kept in local positions the other way.
It moves:

- Bepu's bodies and statics (`PhysicsWorld.Shift`).
- The engine's components (`OriginShifts.ShiftedComponents`): the camera's, the characters' feet now and before, a
  climber's target, and the mesh placements and loose bodies' poses now and before.
- The world's streaming centres, the retired models, the point lights' flashes, the particles' queued bursts,
  attractors, and stress place, and the sounds queued, held, waiting, and playing.
- The frame view's camera (`RenderView.Camera`).
- The game's `IOriginShiftHandler`s, in their registration order.

Rules for a move:

- A component or service that keeps a local position across steps must be moved by the origin: add its vectors to
  `ShiftedComponents`, or give the game an `IOriginShiftHandler`. A component's vector field left out fails
  `OriginShiftsTests`.
- The origin moves only between steps and frames, or in the first fixed system's update (`EcsWorld.OriginMayMove`). A
  move asked anywhere else throws, and a move to the place the origin is at returns false wherever it is asked.
- A move allocates nothing but its Debug line. A position far from the new origin rounds to its floats there.
- Mods are not told.
- A match's first origin is where the game's match starts: its `IOriginStart`, read at the match's first step ("origin"),
  once the session has settled the seed. With none, it is the map's middle.
- `OriginShiftSystem` (`.claude/rules/systems.md`) follows the active camera with `OriginShifts.Follow`. A jump asks
  first with `RequestFollow`.
- The console's run-only `origin move DX DY DZ` (`EngineCommands`) moves the origin, and `origin` prints it and the
  camera's place.
- The renderer, the particles' GPU pool, and the sound follow the origin (`.claude/rules/rendering.md`, "Under a moved
  origin"). Positions in messages are absolute (`.claude/rules/sessions.md`).

### Tests

`tests/Itera.Engine.Tests/<Area>/` mirrors `src/Itera.Engine/<Area>/`. For this file's areas:

- `Matches/EngineComposition.cs` builds the engine's real services with the least a game adds: a floor generator with
  the terrain (which `terrain: false` leaves out), and one fixed and one frame system. Over it
  `CompositionTests`, `MatchFlowTests`, `MatchLifetimeTests`, and `HeadlessMatchTests` run, with and without terrain.
- `OriginShiftsTests` and `Systems/OriginShiftSystemTests` cover the origin.
- `Ecs/` (`EcsWorldTests`, `CheckedWorldExtensionsTests`, `EcsInspectorTests`), `Time/`, `Diagnostics/`, `Modding/`
  (`ModLoaderIntegrationTests`, `ModDispatcherTests`), `Control/` (`ControlServiceTests`), and `Configuration/`
  (`EngineOptionsValidatorTests`, `UnknownOptionKeysTests`).
- `EngineStepsTests`, `ExitSignalPolicyTests`, `GameLoopSaveTests`, and `ChecklistTests` are in the project's root.
