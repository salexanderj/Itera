---
paths:
  - "src/Itera.Engine/**"
  - "tests/Itera.Engine.Tests/*.cs"
---

## The engine

- `src/Itera.Engine`: everything not game specific. Composition in `EngineServiceCollectionExtensions.AddIteraEngine`,
  and the voxel terrain's in `VoxelServiceCollectionExtensions.AddIteraVoxels`; the match's identity a joining game must
  share in `Matches/MatchIdentity.cs` (`IMatchIdentity`: the generator's with terrain, else the application's name);
  options checks in `EngineOptionsValidator`; the run's initialisation order (one step list), main loop, and shutdown
  in `GameLoop`; a match's start, join, and end in `Matches/MatchFlow.cs` (no window: `MatchFlowTests` drives it); a
  match without a window or GPU for tests and tools in `Matches/HeadlessMatch.cs` (`HeadlessRun`, `HeadlessMatch`, over
  `MatchFlow`); a match's services, step list, and fixed step (`RunFixedStep`) in `Matches/MatchServices.cs` (`MatchStart`,
  `MatchContext`, `MatchControl`, `IRunStartup` for the game's run-wide registries); the transports a session takes
  through `ISessionTransports`. Folders:
  - `Assets/`: mesh assets, models read from glTF files at the run's "mesh assets" step, with a skinned model's rig and
    clips (`.claude/rules/mesh-assets.md`).
  - `Saves/` (`SaveService`), `Modding/` (mod loading: `ModLoader`, `ModLoadContext`, `ModDispatcher`), `Diagnostics/`
    (Serilog setup `LoggingServiceCollectionExtensions`, the session header `SessionInfo`, loop stats `FrameStats`, GPU
    pass times `GpuTimings`, `ConsoleLogBuffer`, crash logging, `ControlLog`), `Time/` (`GameClock`,
    `FixedStepAccumulator`), `Loading/` (`LoadingScreen`, `ILoadingTask`, `LoadingProgress`,
    `EngineLoadingTasks.cs`), `Control/` (`ControlService`, `ControlRequest`, `InputScript`).
  - `Ecs/`: `EcsWorld` (times each system), `IFixedUpdateSystem` and `IFrameUpdateSystem` (`SystemInterfaces.cs`),
    `EcsChecks`, `CheckedWorldExtensions`, and `EcsInspector` (archetypes, entities, and components read by reflection
    for the console's `ecs` and the control port's `query`; it allocates, so commands only).
  - `Components/` and `Systems/`: the components and systems a game registers (`.claude/rules/systems.md`).
  - `Maths/` (`DeterministicMath`, `Rotation`, `RigidTransform`) and `Animation/` (skeletons, poses, clips, foot IK):
    `.claude/rules/animation.md`.

A game composes the engine in its `Program.cs`: `AddIteraEngine`, then its own (the starter's `AddGame`, which registers
its `IChunkGenerator` and calls `AddIteraVoxels()` before its own loading tasks), then the host with `ValidateScopes`
and `ValidateOnBuild`, then `GameLoop.Run()`. A game with voxel terrain registers an `IChunkGenerator` and calls
`AddIteraVoxels()` (the voxel models, the loose bodies, the character mover, and the play area are the core's, and read
the world when there is one); a game without it plays matches with no world (`MatchServices.Terrain` is null; its
`Voxels` and the other terrain members throw). `IVoxelPalette` (grey), `IVoxelTintMap` (none), `IVoxelPassability` (nothing passable), and
`MenuStyle` (plain) have engine defaults it may replace. Its own GPU passes are `IRenderPass` singletons it registers
(`.claude/rules/rendering.md`, "Passes a game adds"). `tests/Itera.Engine.Tests/Matches/EngineComposition.cs` is the
least a game adds (a floor generator with the terrain, which `terrain: false` leaves out, and one fixed and one frame
system), over which `MatchFlowTests`, `MatchLifetimeTests`, `CompositionTests`, and `HeadlessMatchTests` run, with and
without terrain.

The other folders are in their areas' files in `.claude/rules/`.
