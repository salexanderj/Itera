---
paths:
  - "tests/**"
---

## Tests

Test names say what they check, and each area's rules file names its own test classes. This file holds what every test
needs.

### The projects

- `tests/Itera.Modding.Tests`: the mod contract.
- `tests/Itera.Engine.Tests`: the engine. Its folders mirror `src/Itera.Engine/`: `tests/Itera.Engine.Tests/<Area>/`
  tests `src/Itera.Engine/<Area>/`.
- `tests/Game.Tests`: the game's own. Its files belong to the game's `game-*.md` rules files by their paths.
- `tests/Itera.MeshSample.Tests`: the sample without voxels. It plays through `HeadlessMatch`, clicks its own menu by
  label (`SampleMenuTests` over `SampleMenuHarness`, with the engine's hooks for a game's menu tests:
  `MatchControlStates`, `MatchControl.PendingStart` and `PendingLeave`, `PlayerSettings.WithoutFile`, and a
  `MusicPlayer` with no audio thread), and hears its sounds with no device (`AudioService.QueueWithoutThread`).
  `PhysicsDemoTests` plays its physics corner.
- `tests/Shared/` is compiled into the projects that use it (below).

### Shared helpers (`tests/Shared`)

- `SystemAllocation`: measures a system's allocation.
- `ArchStartup`, `SerialCollectionDefinition`, `RunsLongAttribute`, `LongFirstCollectionOrderer`, `xunit.runner.json`:
  how the classes run (below).
- `VoxelLoading`: loads a test's voxel world.
- `FakeTransport`: an in-memory session transport with no sockets and no clocks.
- `HostileValues` and `Watchdog`: hostile field values and damaged message bodies, and rows run on a worker thread that
  fail past a wall-time limit, which `ITERA_TEST_TIME_SCALE` scales on a slower machine.
- `GpuLayout`: compares a C# struct with a compiled shader's block, so a game's tests check its own passes' structs.
- `LayerPicture`: draws a UI layer alone into a bitmap or PNG.
- `LayerInk`: finds what a UI layer draws outside its `Bounds`. A HUD layer's tests draw it at a few screen sizes and
  expect none.
- `WireFingerprint`: pins the wire's bytes with the protocol version. It reads the engine's internals, so the game's
  tests leave it out.
- `RepositoryDocs` and `ReferenceDocs`: the documentation and reference tests' checks (`.claude/skills/docs/SKILL.md`).

### Rules

- Every system has a zero-allocation test and a behaviour test.
- Every measured window starts with `SystemAllocation.StartMeasurement()`, which collects gen0 first so another
  thread's collection cannot add phantom bytes. A long window measures with `SystemAllocation.BytesAllocatedWithoutCollection`,
  which runs it again when a collection fell in it.
- Test classes run in parallel (`xunit.runner.json`). `ArchStartup`, a module initializer, makes Arch safe for that
  before any test: it registers every value type of the engine, the game (the assembly named `Game`), and the test
  assembly as a component, and gives `World.Worlds` room so it never grows. A component type from another library must
  be added there.
- A class that reads the whole process (a garbage collection that must find a match gone, timings) goes in
  `SerialCollectionDefinition`, which runs alone after the rest.
- A class whose tests take more than about 20 s carries `[RunsLong]`, so it starts first.
- Some checks run only in Debug. `tools/steam/upload.sh` runs the suite in Debug and in Release.
- The test projects run with tiered compilation off (`TieredCompilation` in their csproj files), so every method is
  optimised at its first call and an allocation test never measures unoptimised library code.
- Pinned hashes guard determinism: the noise and series trigonometry (`DeterministicMathTests`), fractal noise, the
  heightfield generator's test world, chunks, pieces, save bytes, and the game's world (`tests/Game.Tests/WorldTests`).
  A changed world hash means the generator's `Version` must rise.
- Anything the engine sends between games is pinned in `WireFingerprintTests` with `SessionProtocol.Version`. A game's
  message handlers take damaged bodies and hostile values in every field, each row under the `Watchdog`
  (`HostileValues`).
- Write a test of a game feature through `HeadlessMatch` when it needs the real world and systems (below).

### The checklist tests

They fail when a place that must change with a feature is missed (`.claude/skills/scaffold`):

- `tests/Itera.Engine.Tests/ChecklistTests`: every numeric engine option has a range in the validator, and every engine
  system has a `NameTests` class with an allocation test.
- `tests/Game.Tests/ChecklistTests`: every console command has its help (the generated `game-commands.md` lists them),
  every numeric game option has a range, and every game system has its tests. It also checks the game's components' vectors against its origin
  handlers.
- `ShippedSettingsTests`: every option in `appsettings.json` is at the code's default.
- `SystemOrderTests`: the game's real composition, built from its shipped settings, has its systems in the order it
  pins. The generated `docs/reference/game-systems.md` lists that order.
- `WorldTests`: the generator's version is pinned with its hash.
- `DocumentationTests` and `ReferenceTests`: the docs' limits and the generated `docs/reference/` files
  (`.claude/skills/docs/SKILL.md`).

### Test doubles and harnesses

- Sessions run in one process over `FakeTransport` (`SessionServiceTests`), over `SteamTransport` on `FakeSteam.cs`
  (players, P2P connections, and lobbies in memory), and over a real LiteNetLib host on 127.0.0.1 (`LoopbackTests`).
  `SimulatedNetwork` adds delay, jitter, and loss over `FakeTransport` with a hand-stepped clock.
- `HeadlessRun` makes the game's run-wide registries once. `HeadlessMatch` plays a real match over the game's
  composition with no window or GPU: `Play(seed)`, `WaitUntilLoaded()`, `Step(ticks)` (the session polled before each
  tick, a frame after each), `Input` for injected keys, `World`, `Services`. Disposing it ends the match. Use
  `Engine:Physics:Threads=1` so the steps repeat.
- A host and a client in one process: each from its own services, with an `ISessionTransports` that hands out a
  `FakeTransport` on one `FakeNetwork`, `Host(seed)` on one run and `Join(host)` on the other. Poll both sessions until
  the download ends, then step both in turn.
- `tests/Itera.Engine.Tests/Matches/EngineComposition.cs` builds the engine's real services with the least a game adds.
- Engine system tests use `PhysicsTestHarness`, `MeshGroundHarness`, `FlatGenerator`, `FlatTestGenerator`,
  `RampGenerator`, `FakeTickInput`, and `FakeFrameInput` (`tests/Itera.Engine.Tests/Systems/`).
- Voxel tests use small generators (`SlopeGenerator`, `DomeGenerator`, `BoxesGenerator`, `GridShapesGenerator`,
  `TranslatedGenerator`) and `PlacingGenerator`. Mover tests use `TestTriangles` and `OpeningGenerator`.
  `ObjectLabeller.Manual` labels on the test's thread. `MusicHarness` gives `FakeDecoder`.
- Replication tests use `ReplicationHarness`. Asset tests use `GltfBuilder`. Audio tests use `FakeAudioDevice`,
  `WavBuilder`, and `MusicHarness`. Animation tests use `HumanoidTestSkeleton`. Render-view tests use `FakeViewLists`.
  UI toolkit tests use `FixedTextMeasurer` and `UISurface`.
- GPU code is tested without a device: `WorldMeshesTests` runs the world's meshes over a draw target and staging in
  memory, and `GpuLayout` compares structs with the compiled SPIR-V.

### Folders of `tests/Itera.Engine.Tests`

- `Voxels/`, `Objects/`, `Navigation/`, `Replication/`, `Rendering/`, `Meshing/`, `Audio/`, `Particles/`, `Physics/`,
  `Spatial/`, `Assets/`, `Animation/`, `Maths/`, `Networking/`, `Saves/`: see the area's rules file.
- `UI/`, `Input/`, `Settings/`, `Loading/`, `Commands/`: `.claude/rules/ui.md`, `ui-input.md`, and `ui-menu.md`.
- `Systems/`: each engine system's behaviour and allocation, `.claude/rules/systems.md`.
- `Matches/`, `Ecs/`, `Time/`, `Diagnostics/`, `Modding/`, `Control/`, `Configuration/`, and the project's root files:
  `.claude/rules/engine.md`.

### Package notes

- `dotnet new xunit` on the pinned SDK emits xunit 2.9.3 with `Microsoft.NET.Test.Sdk` 17.14.1. Those versions are
  pinned as emitted, in `Directory.Packages.props`.
