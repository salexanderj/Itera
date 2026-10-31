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
  tests `src/Itera.Engine/<Area>/`, and `docs/reference/engine-rules.md` names the rules file each folder loads.
- `tests/Game.Tests`: the game's own. Its files belong to the game's `game-*.md` rules files by their paths.
- `tests/Itera.MeshSample.Tests`: the sample without voxels, played through `HeadlessMatch`. `SampleMenuTests` clicks
  its menu by label (`SampleMenuHarness`, over the engine's hooks for a game's menu tests: `MatchControlStates`,
  `MatchControl.PendingStart` and `PendingLeave`, `PlayerSettings.WithoutFile`, a `MusicPlayer` with no audio thread).
  It hears its sounds with no device (`AudioService.QueueWithoutThread`). `PhysicsDemoTests` plays its physics corner.
- `tests/Itera.ModelView.Tests`: the model stage (`tools/ModelView`), played through `HeadlessRun`.
- `tests/Shared/` is compiled into the projects that use it (below).

### Shared helpers (`tests/Shared`)

- `SystemAllocation`: measures a system's allocation.
- `ArchStartup`, `SerialCollectionDefinition`, `RunsLongAttribute`, `LongFirstCollectionOrderer`, `xunit.runner.json`:
  how the classes run (below).
- `VoxelLoading`: loads a test's voxel world.
- `FakeTransport`: an in-memory session transport with no sockets and no clocks.
- `HostileValues` and `Watchdog`: hostile field values and damaged message bodies, and rows run on a worker thread that
  fail past a wall-time limit.
- `TestTime`: every wait's wall-time limit (`Limit`, `Deadline`), and the `Watchdog`'s, times `ITERA_TEST_TIME_SCALE`.
- `GpuLayout`: compares a C# struct with a compiled shader's block, so a game's tests check its own passes' structs.
- `LayerPicture`: draws a UI layer alone into a bitmap or PNG.
- `LayerInk`: finds what a UI layer draws outside its `Bounds`. A HUD layer's tests draw it at a few screen sizes and
  expect none.
- `WireFingerprint`: pins the wire's bytes with the protocol version. It reads the engine's internals, so the game's
  tests leave it out.
- `RepositoryDocs` and `ReferenceDocs`: the documentation and reference tests' checks (`.claude/skills/docs/SKILL.md`).
- `ModelChecks`: every model a run declares, checked as the engine reads it and held to a game's budgets.

### Rules

- Every system has a zero-allocation test and a behaviour test.
- Every measured window starts with `SystemAllocation.StartMeasurement()`, which collects gen0 first so another
  thread's collection cannot add phantom bytes. A long window measures with `SystemAllocation.BytesAllocatedWithoutCollection`,
  which runs it again when a collection fell in it.
- Test classes run in parallel (`xunit.runner.json`). Before any test, `ArchStartup`, a module initializer, registers
  every value type of the engine, the game (the assembly `Game`), and the test assembly as an Arch component, and gives
  `World.Worlds` room so it never grows. A component type from another library is added there.
- A class that reads the whole process (a garbage collection that must find a match gone, timings) goes in
  `SerialCollectionDefinition`, which runs alone after the rest.
- A class whose tests take more than about 20 s in all is split into classes of independent tests (below). A class
  over about 5 s in a run of the suite carries `[RunsLong]`, so it starts first: started late, it ends the run.
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

### Keeping tests light

The suite runs often, so each test pays only for what it checks. `tools/tests/timings.py` lists the slowest classes
and tests.

- xunit runs the test classes in parallel but a class's tests one after another, so a run lasts at least as long as its
  longest class. It makes a new instance of the class for each test: a constructor or field initialiser runs per test.
- Make a costly setup that the tests only read once a class, in an `IClassFixture`: a game's composition and its
  `HeadlessRun`, a voxel world, a shader compiler. Keep per-test state (an ECS world, a match) in the test.
- A class fixture shares its state across its tests. Each test resets what the fixture can reset, and puts back any
  other shared state it changes: a tunable, a key's binding, a setting, a queued sound.
- A test that fails only in a full run and passes alone points first at state shared across tests: a class fixture,
  a run-wide service, or a static.
- Play a large map only for what it alone has. Step a match only until the checked state, in a loop with a cap, and set
  a long timer's tunable low instead of stepping through it.
- Measure a system's allocation at the end of a behaviour test that reaches the state, not in a scenario of its own.
- Inside a loop over many items, build no assert message and call no assert per item: compare spans, or test the
  condition and call `Assert.Fail` with its message only on a mismatch.
- A random or exhaustive check runs the trials its branches need, and each theory row takes a branch of its own.
- No wall-clock assert, no `Thread.Sleep` to order threads, and no test that only prints timings: measurements go in
  `tools/benchmark`. Wait on an event or on the state itself, with a `TestTime` cap. A stepped match waits for its
  threads (navigation, the voxel workers) before it steps on.
- Every test asserts what its name says, and no more than one behaviour fails it.

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
- Engine system tests use `PhysicsTestHarness`, `MeshGroundHarness`, `FlatGenerator`, `FlatTestGenerator`,
  `RampGenerator`, `FakeTickInput`, and `FakeFrameInput` (`tests/Itera.Engine.Tests/Systems/`).
- Voxel tests use small generators (`SlopeGenerator`, `DomeGenerator`, `BoxesGenerator`, `GridShapesGenerator`,
  `TranslatedGenerator`) and `PlacingGenerator`. Mover tests use `TestTriangles` and `OpeningGenerator`.
  `ObjectLabeller.Manual` labels on the test's thread. `MusicHarness` gives `FakeDecoder`.
- Replication tests use `ReplicationHarness`. Asset tests use `GltfBuilder`. Audio tests use `FakeAudioDevice`,
  `WavBuilder`, and `MusicHarness`. Animation tests use `HumanoidTestSkeleton`. Render-view tests use `FakeViewLists`.
  UI toolkit tests use `FixedTextMeasurer` and `UISurface`.
- GPU code is tested without a device: `WorldMeshesTests`, `WorldMeshesLevelsTests`, and `WorldMeshesModelsTests`
  run the world's meshes over a draw target and staging in memory (`WorldMeshesTestBase`).

### Package notes

- xunit 2.9.3 and `Microsoft.NET.Test.Sdk` 17.14.1 are pinned (`Directory.Packages.props`) as `dotnet new xunit`
  emits them on the pinned SDK.
