---
paths:
  - "tests/**"
---

## Tests

- `tests/Itera.Modding.Tests`, `tests/Itera.Engine.Tests`, `tests/Game.Tests` (the game's own), and
  `tests/Itera.MeshSample.Tests` (the sample without voxels, played through `HeadlessMatch`; its own menu clicked by
  label, `SampleMenuTests` over `SampleMenuHarness`, with the engine's hooks for a game's tests: `MatchControlStates`,
  `MatchControl.PendingStart` and `PendingLeave`, `PlayerSettings.WithoutFile`, and a `MusicPlayer` with no audio
  thread; its pages drawn by `SampleMenuPicturesTests`; its sounds heard with no device through
  `AudioService.QueueWithoutThread`, `SampleSoundSystemTests`, and its files read, `SampleSoundsTests`; its physics
  corner played, `PhysicsDemoTests`: a ramp ball through the gate into the zone, the gate holding the player, a pad
  ball's bounce and clang, the floating and slow balls), with subfolders that
  mirror `src/`. `tests/Shared/` holds `SystemAllocation`, `ArchStartup`, `SerialCollectionDefinition`,
  `RunsLongAttribute` and `LongFirstCollectionOrderer`, `xunit.runner.json`, `VoxelLoading` (load a test's voxel world),
  `FakeTransport` (an in-memory session transport: no sockets, no clocks), `HostileValues` and `Watchdog` (hostile field
  values and damaged message bodies, and rows run on a worker thread that fail past 1 s of wall time, times
  `ITERA_TEST_TIME_SCALE` on a slower machine), `GpuLayout` (a C# struct against a compiled shader's block, so a game's
  tests check its own passes' structs too), `LayerPicture` (a UI layer drawn alone into a bitmap or PNG), `LayerInk` (what a UI layer draws outside its `Bounds`: a HUD layer's tests
  draw it at a few screen sizes and expect none, as `tests/Game.Tests/MinimapLayerTests` does, beside its hiding,
  redraw, and zero-allocation tests), and `WireFingerprint` (the wire's bytes pinned with the protocol version; it reads
  the engine's internals, so the game's tests leave it out), compiled into the projects that use them.
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
- A game's real match without a window or GPU: `HeadlessRun` (the game's run-wide registries, once) and `HeadlessMatch`
  (`Play(seed)`, `WaitUntilLoaded()`, `Step(ticks)` with a poll of the session before each and a frame after each,
  `Input` for injected keys, `World`, `Services`; disposing ends the match) over the game's composition, with
  `Engine:Physics:Threads=1` so the steps repeat: the starter's `tests/Game.Tests/ScenarioTests` (standing, walking a
  second, balls that rest and go back into the world) and the engine's `Matches/HeadlessMatchTests`. A host and a
  client in one process: each from its own services with an `ISessionTransports` handing out a `FakeTransport` on one
  `FakeNetwork`, `Host(seed)` on one run and `Join(host)` on the other (both sessions polled until the download ends),
  then both stepped in turn (`HeadlessMatchTests`: a client with terrain following the host's edits). Write a game
  feature's test this way when it needs the real world and systems.
- The origin's moves: `Matches/OriginShiftsTests` (what moves and what does not, the components' vectors each moved or
  listed as no position, only between steps or in the first fixed system's update, following a point only once it is
  far, no allocation) and `Systems/OriginShiftSystemTests` (the camera followed past the distance, a jump's request
  first); the game's `ChecklistTests` checks its own components' vectors against its handlers, and its `ScenarioTests`
  a walk with the origin following the camera every 8 m against one that stays, and a flight of 2.8 km (a far `tp`
  landing exactly, the origin never much more than 512 m from the camera, the flight ending within a centimetre), and
  the map's edge 4 km out.
  Positions in messages: `Networking/MessageReaderTests` (1/64 cm to ±335 km, peers with other origins, hostile
  bytes).
- Composition: `tests/Itera.Engine.Tests/Matches/EngineComposition.cs` builds the engine's real services with the least
  a game adds; over it `CompositionTests` (the run and a match resolve, systems keep their registration order),
  `MatchFlowTests` (starting, joining, leaving, a host that quits, an exception in a match), and `MatchLifetimeTests`
  (three offline matches in one process, each collected when it ends) run, and `Configuration/UnknownOptionKeysTests`
  checks the engine's keys. The game's `SystemOrderTests` builds its real composition from its shipped settings and
  compares the systems' order with its own list (keep `GAME.md`'s list the same); its `ShippedSettingsTests` checks that every value in its
  `appsettings.json` is the code's default.
- `ControlServiceTests` runs the control port over a real loopback connection. `MenuLayerTests` and the other UI tests
  draw on a surface and check pixels, redraws, and allocation.
- Some checks run only in Debug. The test projects run with tiered compilation off (`TieredCompilation` in their csproj
  files): every method is optimised at its first call, so an allocation test never measures what unoptimised library
  code allocates (Assert.InRange's comparer once failed one); the suite takes a little longer (ARCHITECTURE.md).
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
  `DomeGenerator`, `BoxesGenerator`: a flat floor with boxes of given materials); `VoxelGridTests`, `TallWorldTests` (a world 25,000 layers tall
  storing what one 12 tall does; an edit 100 km up, its two runs of surface layers, rays to it and down 100 km), and
  `BaseTenWorldTests`, worlds on a 10 cm grid (`GridShapesGenerator`: shapes whose faces lie off the 10 cm lines)
  checked against the shapes at each voxel's sample point; pinned content hashes; rays and
  collision queries against voxel-by-voxel walks; row spans against the point test (`VoxelEditingTests`); large edits
  spread over steps (`DeferredEditTests`); resets (`VoxelResetTests`); pieces and save formats (`VoxelPieceTests`);
  models (`ModelEditingTests`); `EditLogTests` (with a game's sections after the pieces, damaged sections, and a frozen
  log writing only the edits it had).
- `Itera.Engine.Tests/Systems/`: each engine system's behaviour and allocation, over `PhysicsTestHarness`,
  `FlatGenerator`, `FlatTestGenerator`, `RampGenerator`, `FakeTickInput`, and `FakeFrameInput`; the play area
  (`PlayAreaTests`); `LooseBodySystem` without voxel terrain, with a game's rest handler (`LooseBodyRestTests`, over
  `MeshGroundHarness`); `ClimbingMode` over mesh walls through `PlayerMovementSystem` (`ClimbingModeTests`).
- `Itera.Engine.Tests/Voxels/Generation/`: the shapes' row spans against point tests, fractal noise, the jittered grid,
  the scalar field, the field tint map with `VoxelTintSampler`, and the heightfield generator (every description against
  its filled voxels, a column's range and its layers standing for every layer, fills against a voxel-by-voxel reference, `HillsGenerator`).
- `Itera.Engine.Tests/Objects/`: voxel objects over `PlacingGenerator` (flat stone placing a test's objects): kinds'
  shapes and registry, models, `ObjectWorld`'s areas, far copies (`ObjectCopiesTests`: the shares summing to the
  object's volume at four headings and voxels wholly inside solid, a thin trunk kept a voxel wide, a kind's share grids
  those of its voxels, a column job's voxels the rule's; a pinned hash), turns, and rays (`ObjectRaysTests`:
  random rays against every solid voxel's box, a ray across stretches 40 m out, the shared ray, a tool's ray through a
  bush, the third-person camera pulled in by a trunk, the movement probes on a trunk, no allocation), and damage
  (`ObjectDamageTests`: a turned box against the cubes it holds wholly or misses, damage made live against damage
  replayed when the area fills bit for bit (also across a reset), a dig past the map's edge changing nothing, the damage
  pinned (with and without a part's removal), a part removed whole and falling only live, a removal bound by its box and
  never changing the world, the labeller's pieces by each root rule, a cut on the host logging the removal it cuts
  off (a client following labels nothing; a cut replayed when its area fills too; an answer for a copy a reset dropped
  changing nothing, the labeller run on the test's thread, `ObjectLabeller.Manual`), its far copies cleared where only the piece filled them (kept through a
  reload and on a client), hostile part removals and clears of copies, a saved log with a removal loading the same damage, a client following its host, a reset, the cap dropping the farthest copy and replaying it near, a mover far behind the changes, an area's
  copies going and coming back, a clear's count of what it takes for `IVoxelRemovals`, paint and fill, damaged parts'
  models against the kind's rule over their voxels across a part's border, the mover, rays, and statics meeting a dug
  object, a bush painted to rock stopping a character, a damaged part's unmarked blocks reading what its kind's do (so
  sharing their meshes is right), a second dig allocating nothing; `ObjectDamageMatchTests`: a body on a rock in a match of the engine's real
  composition, falling when the rock is dug away; a trunk's top cut off falling as a body onto its stump; a cut alone
  dropping the top).
- `Itera.Engine.Tests/Replication/`: `ReplicatedStateCodecTests` (fields read back within their steps, changes from a
  baseline, damaged records refused or read within range, the blend, schemas and kinds) and `ReplicationServiceTests`
  over `ReplicationHarness` (a host and a client in one process without terrain, test movers and players, the clock
  stepped after the join; `SimulatedNetwork` for delay, jitter, and loss): following a little behind, through 100 ms
  each way, removals, reach, the budget, refused kinds, damaged snapshots, a bad reply; the replication systems'
  behaviour and allocation in `Systems/`.
- `Itera.Engine.Tests/Maths/` and `Animation/`: the deterministic maths and rotations, the octahedral encoding, the
  origin's conversions at up to ±160 km (`WorldOriginTests`), and skeletons, clips, aiming, and foot IK over a humanoid
  test skeleton (`HumanoidTestSkeleton`).
- `Itera.Engine.Tests/Assets/`: glTF files written in the test (`GltfBuilder`: accessors over a data-URI buffer or a
  .glb's BIN chunk, PNG images, skins, joints, and animations) read by `GltfImportTests` and `RigImportTests` (a rig
  posed against SharpGLTF's own world matrices); `RigClipTests` (step, linear, and cubic sampling); `RigShapingTests` (a
  joint by name, the globals, a mask and a blend, a turn in the asset's space under a turned parent, two-bone IK reaching
  and stretching, turning, moving, and reaching shaped exactly as with every joint's globals worked out, no allocation); `MeshAssetsTests`,
  `TextureDecodeTests`, `MipChainTests`, `MeshTangentsTests`; a model's compressed textures (`CompressedTexturesTests`:
  read where they match, each mismatch decoded with a warning, the image's hash wherever the model keeps it).
- `Itera.Engine.Tests/Physics/`: a game's bodies and the records by handle (`PhysicsBodiesTests`: a reused handle
  starts afresh, freezing and forgetting, `Holds`), physics materials (`PhysicsMaterialsTests`: the registry's rules,
  the default pair exactly the engine's contact, drops at bounciness 0, 0.5, and 0.8 at 1 and 4 substeps, small drops
  landing at every place within a step, a body bouncing where the removed body that had its handle rested, two balls of
  bounciness 1 trading speeds, a bouncing ball coming to rest, a mesh collider's material, no allocation; in
  `VoxelCollidersTests` an ice box sliding down the 30 degree slope and a bouncy ball rolling over cells' borders no
  higher than a plain one; voxel materials' in `VoxelPhysicsMaterialsTests`: a static for each physics material of a
  cell, each with its own voxels' boxes, none split with nothing mapped, a box sliding down a slope of ice voxels, a
  cleared material's static removed, a ball of its voxel material's, the mapping's rules, the build timed for one and
  three materials, no allocation; the ground a move reports in `GroundMaterialTests`: a mesh floor's collider material
  read as it is now and its tags, past the floor's edge, voxels' by their voxel material, none in the air, no
  allocation; layers and groups in `PhysicsLayersTests`: the registry and its table both ways, the engine's rules
  first, a ball through a wall of a layer it does not touch, a group falling through itself, a match's table change
  waking a sleeper, the mover blocked by a gate of any layer and reading no body that does not touch characters, no
  allocation; trigger zones in `PhysicsTriggersTests`: a ball through a zone entering and leaving once and falling as
  without it, one asleep inside staying in, removals giving a removed leave, a zone moved onto a resting ball and off,
  layers, a kinematic mirror seen, each shape, a move of the origin, one thread against four, drops, no allocation; the
  characters' in `Systems/TriggerCharacterSystemTests`; impact events in `ImpactEventsTests`: drops from 1 and 4 m
  reporting their speed, place, normal, and materials, a ball rolling to rest asleep giving nothing more, two marked
  balls meeting and parting, a marked static, a removal, the origin, drops, no allocation), mesh colliders (`MeshCollidersTests`: two-sided rays across buckets, tags, a ball that
  lands on a collider's Bepu static at any voxel level) and the shared ray (`CollisionSourcesTests`); the colliders as
  the mover's cells (`MeshColumnsTests`); the voxel objects as its cells (`ObjectColumnsTests`: cells against a
  cell-by-cell reference, a trunk, a rock's top, material, and tags, a low stone, a bush, waiting for unfilled areas, the
  map's edge, chunks made again when the objects change, no allocation; `ObjectColumnReaderTests`: a kind's columns'
  runs against its shape's voxels across its parts, one object's spans against a cell-by-cell reference for cells as
  wide as its voxels, wider, and narrower, at four headings, below its anchor, with passable voxels, and damaged, and the
  runs made once a set of passable materials) and as statics near bodies (`ObjectCollidersTests`,
  made before the objects' world is initialised as a match makes them: a ball on a rock's top, a ball rolled at a trunk,
  a plank turned from its anchor, passable leaves, two physics materials, a body frozen until the objects are known,
  statics going with the cells and kept while the objects change, a cut body on a trunk, a kind's boxes against its
  voxels, no allocation); the mover through test features (walls, 1 cm steps, a pit, a ceiling, leaves)
  and 10,000 random moves that never leave it inside a voxel, the same test bodies run on voxels and on triangles built
  from the same features (`MoverFeatureTests`: `VoxelCharacterMoverTests` and `MeshMoverFeatureTests` over a world of
  air; `DensityGroundTests` at 10 cm: `DensityMoverTests` and `MeshDensityGroundTests` with no world, both from
  `TestTriangles`), the mover in 1 m cells under a lintel (`CoarseCeilingMoverTests`, over `OpeningGenerator`: a body
  rising under it falls back and is never grounded in the air, one on its top stands there, a wall beside the opening
  stops one in the air, and one walks through), the mover over colliders alone (`MeshMoverTests`: a thin wall, a slope along no axis, a collider
  added and removed, the map's edge), and over voxels and colliders in the same cells (`MixedGroundMoverTests`); loose
  bodies without voxel terrain (`LooseBodiesWithoutTerrainTests`, over `Systems/MeshGroundHarness`: mesh ground, models
  in their own memory, the mover reading the bodies); the movement modes' probes over meshes and voxels
  (`MovementProbesTests`, `MovementProbesVoxelTests`); wide bodies (`WideBodyMoverTests`); statics of voxel boxes,
  coarse colliders (`CoarseCollidersTests`), ragdolls (`RagdollTests`), thrown and settled pieces, the mover among
  loose bodies (`MoverWithBodiesTests`), and solid models (`SolidModelVoxelsTests`); far from the middle
  (`FarFromTheMiddleTests`: a world moved 150 km out by `Voxels/TranslatedGenerator`, the origin there; the density
  ground's tests on its voxels and on mesh colliders placed there, `DensityMoverFarTests` and
  `MeshDensityGroundFarTests`; rays, a ball, and mesh rays against the middle's bit for bit, a walk within 0.1 mm);
  the origin moved under Bepu (`PhysicsShiftTests`: sleeping, kinematic, and moving bodies and statics) and under loose
  bodies on voxels (`ShiftedBodiesTests`: a resting ball, and a move's cost with 384 bodies, logged); lag compensation's hit history
  (`HitHistoryTests`: the ray against a capsule by hand and against a march, an entity's capsules read at a tick as the
  ray takes them, a character met where it was at a fractional
  tick and not where it is, the shooter ignored, the nearer entity and the part, an entity met only near its records, the
  ring's 32 steps, a move of the origin between records, `Forget` and `Clear`, the limits, no allocation).
- `Itera.Engine.Tests/Rendering/`: `Materials/MaterialTextureSetsTests` (DDS files read back, sets loaded and left out,
  a material packed with its set), `RenderLookTests` (the engine's look pinned to the shaders' former constants, every
  value copied and tunable), `SunPathTests` (daylight and night round the horizon, the test day, compass angles),
  `ViewConstantsTests` (the push constants against the shaders, the lens: a perspective near plane, an orthographic box
  and its depth), `ShadowCascadesTests` (slices and orthographic boxes inside their cascades, a fit local to a moved
  origin mapping points as one at the middle), `RenderViewTests` (the
  frame's view: its atmosphere, and what a match's end puts back), `Meshes/` (`MeshPassTests` (the shaders' structs; the fading shaders, and fade shares that meet
  showing each threshold once), `MeshAssetDrawsTests`
  (the asset lists, posed instances, and the blended list sorted back to front), `MeshViewTests` (the frame's frustum,
  cascades, and lights), `LevelBalanceTests` (random trees against
  marking every finest column, a level-9 column), `MeshColumnTests`, `WorldMeshesTests` over a draw target and staging
  in memory: the seams' stitched meshes, a full heap, the edits' latency, an image view's lists over `FakeViewLists`,
  the same blocks drawn local to a moved origin, a column cut by the map's edge or wider than the view still split, a
  ball set far over a heightfield's ground widening its column and drawn from blocks for its runs alone, no fine blocks
  under a camera high over the ground, a model sharing a base's meshes meshing only its marked block; objects drawn as models under split columns and within
  the hand-over's fade, their levels fading into each other by distance with complementary shares and out over
  their copies past the hand-over, the fading draws written last, a level-K column
  splitting only once its objects are known),
  `RenderViewsTests` (declaring, the cadence, panels), `Memory/GpuRangeHeapTests`, `GpuLayoutTests` (of
  `tests/Shared/GpuLayout`, also `DescriptorSets`), `Memory/GpuMemoryAllocatorTests`, `DeviceChoiceTests`,
  `RenderThreadTests` (a fake renderer), `RenderPassesTests` (a game's pass at every stage in `EngineComposition`, none
  made or written without graphics; too many, or names empty or used twice, refused; no picture read unless a pass says so), the passes' shaders against their
  push constants (`UiPassTests`, `UpscalePassTests`, `LoadingPassTests`, `ParticlePassTests`, with the pool's origin), `ScreenshotTests`,
  `ShaderReloadTests`.
- Elsewhere in `Itera.Engine.Tests`: `Configuration/` (`EngineOptionsValidatorTests`), `Networking/` (`SessionServiceTests`, `SessionTuningTests` (a host's match tunables reaching a joining client and
each change, local ones staying, a client's range kept, defaults back at the end), `LoopbackTests`, `SteamTransportTests`, `SimulatedNetworkTests` (delay, jitter, and loss over `FakeTransport` with a hand-stepped clock),
  `SteamFriendListTests`, `SessionGameMessageTests`), `Saves/SaveServiceTests`, `SaveSectionsTests` (a game's sections by name and version, a newer or damaged one
stopping the start, the backup for a damaged save and not for a newer build's, a deleted save starting afresh, an
autosave on a background thread, a section that throws when captured, a capture holding its moment, no allocation until
a save is due), `SaveMatchTests` (the last save captured while the ECS world lives, restored before the first step), and
`SaveFilesTests` (a temporary folder), `Matches/JoinRequestsTests`, `Matches/MatchControlStatesTests` (the hooks for a game's menu tests against a real match), `Navigation/`
  (flow fields on small grids, paths on them (`NavPathFinderTests`: a flow field's cost, round walls, no corner cut, a
  ramp before a step, a sealed goal and the budget ending nearest, searches in turn on one finder finding what finders
  of their own find, no allocation), the scanner with a floor 409 m down,
  and the service; the voxel objects,
  `NavigationObjectsTests`: as generated stamped onto the fill and read alike, a trunk cut through opening once its top
  is removed and blocked again after a reset, a cell waiting until its objects are known and a removal logged before
  then waiting as an edit, a moved window stamping the objects in its strips, no allocation; and the service's moving
  window,
  `NavigationWindowTests`: moves by tiles, the map's edge, a moved window against one placed there at the start, an edit
  past it, a reset during a move, a walker 2 km to its goal, no allocation), `Audio/` (`AudioMixerTests` with the one-shots across a move of the origin, `AudioServiceTests`,
  `SynthesisTests`, `SoundCacheTests`; the files' readers, `WavReaderTests` over `WavBuilder`, `VorbisDecoderTests` and
  `SoundFilesTests` over the Ogg fixtures `tools/sounds/make_sounds.py fixtures` made; `FileSoundTests` and
  `FileLoopTests` through the mixer on `FakeAudioDevice`; `MusicTests` over `MusicHarness`, the music's parts pumped
  round by round on in-memory files, `FakeDecoder`, a loop's join checked frame for frame; `OpenAlLoopbackTests` on
  OpenAL Soft's loopback device), `Particles/`, `Commands/` (`MatchCommandsTests` too), `UI/` (`AimDotLayerTests`, `TextBlobsTests`,
  `DevConsoleTests`, `MenuLayerTests` and `MenuJoinPageTests` (the menu worked by what its elements read),
  `MenuPicturesTests` (every menu page drawn at 1280x720 and 2560x1440; `ITERA_UI_PICTURES` keeps them, for a look test),
  `EngineLayerBoundsTests` and `UiFontsTests` (the sizing rule), `RadialMenuTests`, `TextLineTests`; `Toolkit/`:
  `UiLayoutTests` pinning rectangles with `FixedTextMeasurer`, `UiInputTests`, `UiLayerTests` through `UISurface` and
  `LayerPicture`), `Input/`
  (`MenuInputTests`, `KeyBindingsTests`, `KeyboardFocusTests`, `InputInjectionTests`, `TextFocusTests`), `Settings/` (with `TunablesTests`: names, ranges, sealing, scopes, and `tune`), `Loading/`,
  `Control/`, `EngineStepsTests` (the start-up steps), `ModLoaderIntegrationTests` (loads the real sample mod and
  checks that it unloads), and `ModDispatcherTests` (a throwing mod isolated, callbacks timed).

### Package notes

- `dotnet new xunit` on SDK 10.0.112 emits xunit 2.9.3 with `Microsoft.NET.Test.Sdk` 17.14.1;
  those versions are pinned as emitted.
