---
paths:
  - "tests/**"
---

## Tests

- `tests/Itera.Modding.Tests`, `tests/Itera.Engine.Tests`, and `tests/Game.Tests` (the game's own), with subfolders
  that mirror `src/`. `tests/Shared/` holds `SystemAllocation`, `ArchStartup`, `SerialCollectionDefinition`, `RunsLongAttribute` and
  `LongFirstCollectionOrderer`, `xunit.runner.json`, `VoxelLoading` (load a test's voxel world), `FakeTransport` (an in-memory session transport: no sockets, no
  clocks), `HostileValues` and `Watchdog` (hostile field values and damaged message bodies, and rows run on a worker thread that fail past 1 s of
  wall time, times `ITERA_TEST_TIME_SCALE` on a slower machine),
  and `WireFingerprint` (the wire's bytes pinned with the protocol version; it reads the engine's internals, so the
  game's tests leave it out), compiled into the projects that use them.
- Every system has a zero-allocation test and a behaviour test. Every measured window starts with
  `SystemAllocation.StartMeasurement()`, which collects gen0 first so another thread's collection cannot add phantom
  bytes; a long window measures with `SystemAllocation.BytesAllocatedWithoutCollection`, which runs it again when a
  collection fell in it. Test classes run in parallel, eight at a time in the engine and game test projects (`xunit.runner.json`). Arch is made safe for that
  before any test (`ArchStartup`, a module initializer): it registers every value type of the engine, the game (the
  assembly named `Game`), and the test assembly as a component, since Arch's registry has no lock, and gives `World.Worlds` room so it never grows,
  since `World.Create` locks the array it may replace. A class that reads the whole process (a garbage collection that
  must find a match gone, timings) goes in `SerialCollectionDefinition`, which runs alone after the rest. Classes whose
  tests take more than about 20 s carry `[RunsLong]`, so they start first. `tools/steam/upload.sh` runs the suite in
  Debug and in Release.
- Pinned hashes guard determinism: the noise and series trigonometry (`DeterministicMathTests`), fractal noise, the
  heightfield generator's test world, chunks, pieces, save bytes, and the starter's world (`tests/Game.Tests/WorldTests`).
  A changed world hash means the generator's `Version` must rise.
- GPU code is tested without a device: `WorldMeshesTests` runs the world's meshes over a draw target and staging in
  memory and applies the copy regions to an emulated heap. `GpuLayout` tests compare each C# struct
  a shader reads with the compiled SPIR-V's layout. `FakeAudioDevice` runs the audio thread's work in memory.
- Sessions run in one process over `FakeTransport` (`SessionServiceTests`, `SessionGameMessageTests`), over
  `SteamTransport` on `FakeSteam.cs` (players, P2P connections, and lobbies in memory), and over a real LiteNetLib host
  on 127.0.0.1 (`LoopbackTests`). A game's message handlers take damaged bodies and hostile values in every field, each
  row under the `Watchdog` (`HostileValues`).
- A game's real match without a window or GPU: `HeadlessRun` (the game's run-wide registries, once) and
  `HeadlessMatch` (`Play(seed)`, `WaitUntilLoaded()`, `Step(ticks)` with a frame after each, `Input` for injected keys,
  `World`, `Services`; disposing ends the match) over the game's composition, with `Engine:Physics:Threads=1` so the
  steps repeat: the starter's `tests/Game.Tests/ScenarioTests` (standing, walking a second, balls that rest and go
  back into the world; about 6 s) and the engine's `Matches/HeadlessMatchTests`. Write a game feature's test this way
  when it needs the real world and systems.
- Composition: `tests/Itera.Engine.Tests/Matches/EngineComposition.cs` builds the engine's real services with the least
  a game adds; over it `CompositionTests` (the run and a match resolve, systems keep their registration order),
  `MatchFlowTests` (starting, joining, leaving, a host that quits, an exception in a match), and `MatchLifetimeTests`
  (three offline matches in one process, each collected when it ends) run, and `Configuration/UnknownOptionKeysTests`
  checks the engine's keys. The game's `SystemOrderTests` builds its real composition from its shipped settings and
  compares the systems' order with its own list (keep `GAME.md`'s list the same); its `ShippedSettingsTests` checks that every value in its
  `appsettings.json` is the code's default.
- `ControlServiceTests` runs the control port over a real loopback connection. `MenuLayerTests` and the other UI tests
  draw on a surface and check pixels, redraws, and allocation.
- Some checks run only in Debug. The test projects run with tiered compilation off (`TieredCompilation` in their
  csproj files): every method is optimised at its first call, so an allocation test never measures what unoptimised
  library code allocates (Assert.InRange's comparer once failed one); the suite takes about 4 % longer.
- Checklists as tests: `tests/Itera.Engine.Tests/ChecklistTests` (every numeric engine option has a range in the
  validator; every engine system has a `NameTests` class with an allocation test) and `tests/Game.Tests/ChecklistTests`
  (every console command is in the `run-game` skill's list; every numeric game option has a range; every game system has
  its tests); `ShippedSettingsTests` (every option in `appsettings.json`, each at the code's default) and `WorldTests`
  (the generator's version pinned with its hash).

Where tests live:

- `Itera.Engine.Tests/Meshing/`: `SurfaceNetsTests` (closed and consistently wound meshes, blocks side by side meeting
  exactly, an independent reference of the vertex rules), `SurfaceNetsTransitionTests` (fine blocks beside coarser ones
  on random shapes sampled per level: open edges at a seam, sides and corners, high and low; noise at every place and
  side), and `MeshVertexTests`.
- `Itera.Engine.Tests/Voxels/`: `VoxelBlocksTests` (blocks read for meshing against voxel-by-voxel reads), storage, edits, workers, and `VoxelWorld` on small test generators (`SlopeGenerator`,
  `DomeGenerator`, `BoxesGenerator`: a flat floor with boxes of given materials); `VoxelGridTests`, and
  `BaseTenWorldTests`, worlds on a 10 cm grid (`GridShapesGenerator`: shapes whose faces lie off the 10 cm lines)
  checked against the shapes at each voxel's sample point; pinned content hashes; rays and
  collision queries against voxel-by-voxel walks; row spans against the point test (`VoxelEditingTests`); large edits
  spread over steps (`DeferredEditTests`); resets (`VoxelResetTests`); pieces and save formats (`VoxelPieceTests`);
  models (`ModelEditingTests`); `EditLogTests`.
- `Itera.Engine.Tests/Systems/`: each engine system's behaviour and allocation, over `PhysicsTestHarness`,
  `FlatGenerator`, `FlatTestGenerator`, `RampGenerator`, `FakeTickInput`, and `FakeFrameInput`.
- `Itera.Engine.Tests/Voxels/Generation/`: the shapes' row spans against point tests, fractal noise, the jittered grid,
  the scalar field, the field tint map with `VoxelTintSampler`, and the heightfield generator (every description against
  its filled voxels, fills against a voxel-by-voxel reference, `HillsGenerator`).
- `Itera.Engine.Tests/Maths/` and `Animation/`: the deterministic maths and rotations, the octahedral encoding, and
  skeletons, clips, aiming, and foot IK over a humanoid test skeleton (`HumanoidTestSkeleton`).
- `Itera.Engine.Tests/Assets/`: glTF files written in the test (`GltfBuilder`: accessors over a data-URI buffer or a
  .glb's BIN chunk, PNG images) read by `GltfImportTests`; `MeshAssetsTests`, `TextureDecodeTests`, `MipChainTests`,
  `MeshTangentsTests`.
- `Itera.Engine.Tests/Physics/`: the mover through test features (walls, 1 cm steps, a pit, a ceiling, leaves) and
  10,000 random moves that never leave it inside a voxel; wide bodies (`WideBodyMoverTests`); statics of voxel boxes,
  coarse colliders (`CoarseCollidersTests`), ragdolls (`RagdollTests`), thrown and settled pieces, the mover among
  loose bodies (`MoverWithBodiesTests`), and solid models (`SolidModelVoxelsTests`).
- `Itera.Engine.Tests/Rendering/`: `Materials/MaterialTextureSetsTests` (DDS files read back, sets loaded and left out,
  a material packed with its set), `Meshes/` (`MeshPassTests`, `MeshViewTests` (the frame's frustum, cascades, and
  lights), `LevelBalanceTests`, `MeshColumnTests`,
  `WorldMeshesTests` over a draw target and staging in memory: the seams' stitched meshes, a full heap, the edits'
  latency), `Memory/GpuRangeHeapTests`, `GpuLayout` (also `DescriptorSets`), `Memory/GpuMemoryAllocatorTests`,
  `DeviceChoiceTests`, `RenderThreadTests` (a fake renderer), the passes' shaders against their push constants
  (`UiPassTests`, `UpscalePassTests`, `LoadingPassTests`, `ParticlePassTests`), `ScreenshotTests`, `ShaderReloadTests`.
- Elsewhere in `Itera.Engine.Tests`: `Configuration/` (`EngineOptionsValidatorTests`), `Networking/` (`SessionServiceTests`, `LoopbackTests`, `SteamTransportTests`,
  `SteamFriendListTests`, `SessionGameMessageTests`), `Saves/SaveServiceTests` (a temporary folder), `Navigation/`
  (flow fields on small grids, the scanner, the service), `Audio/` (`AudioMixerTests`, `AudioServiceTests`,
  `SynthesisTests`, `SoundCacheTests`), `Particles/`, `Commands/` (`MatchCommandsTests` too), `UI/` (`AimDotLayerTests`, `TextBlobsTests`,
  `DevConsoleTests`, `MenuLayerTests`, `MenuJoinPageTests`, `RadialMenuTests`, `TextLineTests`), `Input/`
  (`MenuInputTests`, `KeyBindingsTests`, `KeyboardFocusTests`, `InputInjectionTests`), `Settings/`, `Loading/`,
  `Control/`, `EngineStepsTests` (the start-up steps), `ModLoaderIntegrationTests` (loads the real sample mod and
  checks that it unloads), and `ModDispatcherTests` (a throwing mod isolated, callbacks timed).

### Package notes

- `dotnet new xunit` on SDK 10.0.112 emits xunit 2.9.3 with `Microsoft.NET.Test.Sdk` 17.14.1;
  those versions are pinned as emitted.
