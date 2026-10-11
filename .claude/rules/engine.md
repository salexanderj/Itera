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
  `MatchContext`, `MatchControl` with `MatchControlStates` for a game's tests of its menu, `IRunStartup` for the game's
  run-wide registries); the transports a session takes
  through `ISessionTransports`. Folders:
  - `Assets/`: mesh assets, models read from glTF files at the run's "mesh assets" step, with a skinned model's rig and
    clips (`.claude/rules/mesh-assets.md`).
  - `Saves/` (`SaveService`, `SaveFiles`), `Modding/` (mod loading: `ModLoader`, `ModLoadContext`, `ModDispatcher`), `Diagnostics/`
    (Serilog setup `LoggingServiceCollectionExtensions`, the session header `SessionInfo`, loop stats `FrameStats`, GPU
    pass times `GpuTimings`, `ConsoleLogBuffer`, crash logging, `ControlLog`), `Time/` (`GameClock`,
    `FixedStepAccumulator`), `Loading/` (`LoadingScreen`, `ILoadingTask`, `LoadingProgress`,
    `EngineLoadingTasks.cs`), `Control/` (`ControlService`, `ControlRequest`, `InputScript`).
  - `Ecs/`: `EcsWorld` (times each system), `IFixedUpdateSystem` and `IFrameUpdateSystem` (`SystemInterfaces.cs`),
    `EcsChecks`, `CheckedWorldExtensions`, and `EcsInspector` (archetypes, entities, and components read by reflection
    for the console's `ecs` and the control port's `query`; it allocates, so commands only).
  - `Components/` and `Systems/`: the components and systems a game registers (`.claude/rules/systems.md`).
  - `Maths/`: the two frames of positions (`WorldPoint`, an absolute point in double metres; `WorldOrigin`, the match's
    origin on whole metres, a scoped service the world, the mover, the bodies, and the mesh colliders share, through
    which every conversion between local floats and absolute points or centimetres goes; CLAUDE.md's Units; a move of it,
    `OriginShift`, and `IOriginShiftHandler`, the game's part of one, below), and
    `DeterministicMath`, `Rotation`, `RigidTransform`, which with `Animation/` (skeletons, poses, clips, foot IK) are in
    `.claude/rules/animation.md`.

A game composes the engine in its `Program.cs`: `AddIteraEngine`, then its own (the starter's `AddGame`, which registers
its `IChunkGenerator` and calls `AddIteraVoxels()` before its own loading tasks), then the host with `ValidateScopes`
and `ValidateOnBuild`, then `GameLoop.Run()`. A game with voxel terrain registers an `IChunkGenerator` and calls
`AddIteraVoxels()` (the voxel models, the loose bodies, the character mover, and the play area are the core's, and read
the world when there is one); a game without it plays matches with no world (`MatchServices.Terrain` is null; its
`Voxels` and the other terrain members throw). `IVoxelPalette` (grey), `IVoxelTintMap` (none), `IVoxelPassability` (nothing passable),
`MenuStyle` (plain), and `IMenu` (the engine's `MenuLayer`; `.claude/rules/ui.md`) have engine defaults it may replace. Its own GPU passes are `IRenderPass` singletons it registers
(`.claude/rules/rendering.md`, "Passes a game adds"), and its image views (a minimap, a camera's screen) are declared
in its `IRunStartup` (`RenderViews`, "Image views" there), as are its own on/off settings (`PlayerSettings.RegisterSwitch`,
`.claude/rules/ui.md`). `tests/Itera.Engine.Tests/Matches/EngineComposition.cs` is the
least a game adds (a floor generator with the terrain, which `terrain: false` leaves out, and one fixed and one frame
system), over which `MatchFlowTests`, `MatchLifetimeTests`, `CompositionTests`, and `HeadlessMatchTests` run, with and
without terrain.

The match's origin moves through `OriginShifts` (`Matches/`, scoped): `ShiftTo(point)` puts it on the whole metres
nearest the point (within the edit format's ±167.77 km) and moves everything kept in local positions the other way, so
nothing moves in the world: Bepu's bodies and statics (`PhysicsWorld.Shift`), the engine's components
(`OriginShifts.ShiftedComponents`: the camera's, the characters' feet now and before, a climber's target, mesh
placements and loose bodies' poses now and before), the world's streaming centres, the retired models, the point
lights' flashes, the particles' queued bursts, attractors, and stress place, the sounds queued, held, waiting, and
playing, the frame view's camera (`RenderView.Camera`, which the listener and some commands read before the next frame
sets it), and then the game's
`IOriginShiftHandler`s in their registration order (the starter's `FoxRouteShift`). It moves only between steps and
frames, or in the first fixed system's update (`EcsWorld.OriginMayMove`; a move asked anywhere else throws, and asking
for the place it is at returns false wherever it is asked), so every system of a step or a frame sees one origin; it allocates nothing but its Debug line. A position far from the new origin rounds to its
floats there. Mods are not told. A match's first origin is where the game's match starts: its `IOriginStart` (the
starter's `StartPose`, its eye), read at the match's first step, "origin", once the session has settled the seed; with
none, the map's middle. Then `OriginShiftSystem` (`.claude/rules/systems.md`) follows the active camera:
`OriginShifts.Follow` moves the origin to the camera's whole metres once it is `Engine:Origin:ShiftMetres` (512; 8 to
4096) out on any axis, and a jump the next step makes (`tp`) asks first (`RequestFollow`), so the origin goes to the
jump's end, not back to the camera still where it was. The console's run-only `origin move DX DY DZ` (`EngineCommands`,
with `origin` printing it and the camera's place) moves it too. The renderer, the particles' GPU pool, and the sound
follow it (`.claude/rules/rendering.md`, "Under a moved origin"); positions in messages are absolute
(`.claude/rules/sessions.md`).

The other folders are in their areas' files in `.claude/rules/`.
