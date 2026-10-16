# Itera architecture

This file describes Itera: what it is, what it is built from, how the code is arranged, how a game is composed on it,
which design directions are fixed, and what remains open. It is roadmap-level. Bugs and small tasks do not belong
here. The rules for working in each area are in `.claude/rules/`, and the instructions for a clone are in `CLAUDE.md`
and `GAME.md`.

## 1. What Itera is

Itera is a framework for 3D games in .NET 10 with a Vulkan renderer, an Arch ECS, Bepu physics, peer-to-peer sessions,
and mod support. A game is made by cloning the repository and growing the starter game in `src/Game`; engine changes
that a clone makes go back to Itera, and later engine changes come from Itera with a pull. The engine does not require
voxels in the game's design, but it supports them well: the world can be a destructible volume of 10 cm voxels with a
stored density, generated from a seed on worker threads, streamed in rings of detail around the players, edited with
integer geometry that every peer replays alike, and drawn as smooth triangle meshes. A game also draws models read from
glTF files, and extends the renderer: it sets the look and the sun each frame, adds GPU passes of its own at five places
in the frame, gives a camera an orthographic lens, and draws views of the world into images that its HUD or a mesh's
material shows (section 5: the mesh assets, a game's own passes, a camera's lens, and image views; the look and the sun
in section 8, "The raster pipeline draws the world as triangle meshes").

Itera was built with a tower defence game, Zorf TD, which is shelved: its code, docs, and assets are kept in the archive
repository `salexanderj/ZorfTD-archive` on GitHub (private; its `main` ends at the tag `zorftd-shelved`), and nothing of
its design is part of the framework. Some figures below come from that game's world (the benchmark's views, a crowd of a
thousand goblin models); they are named as measurements.

The numbers that shape the engine's technical decisions:

| Quantity | Value |
|---|---|
| World voxel edge | 1 cm to 1 m at level 0, set by the generator's `VoxelGrid` (10 cm in the starter); level k is twice level k-1; models have a unit of their own, 1 cm in the starter |
| World extent | whole level-0 chunks a game sets in its generator's grid, up to ±160 km each way, up and down too; by default about ±512 m, from -51.2 m to +102.4 m at 10 cm |
| Voxels in a dense grid of that extent | about 1.5 x 10^11 (too many to store; see "Voxel storage") |
| Simulation rate | 60 fixed steps per second (`Engine:FixedStepHz`) |
| Players | up to `Engine:Session:MaxPlayers` (4 by default), peer to peer, the host authoritative |
| View distance | 256 m by default (`Engine:Renderer:ViewDistanceMetres`, up to 8 km); the starter's 8 km over 9 levels |
| Render budget | the world pass at most 4 ms on a mid-range desktop GPU at 2560x1440 with shadows and materials (the budget of the renderer's redesign) |

Notes on outside projects that were consulted as references, not templates, are in `docs/references/` (the Stride
engine so far). The research and decisions behind the engine are in `docs/`:

- `docs/framework-plan.md`: how the framework was made from the game it grew with.
- `docs/rendering-redesign-brief.md` and `docs/rendering-redesign.md`: the outside review that began the renderer's
  redesign, and the redesign's decisions, measurements, and plans (the renderer's history).
- `docs/rendering-extension.md`: what a game can change in the renderer (the look, the sun, blended materials, its own
  passes, lenses, image views), the choices offered for each, and why each was chosen.
- `docs/voxel-structures.md`: 64-trees, OpenVDB, NanoVDB, and other voxel structures compared with Itera's.
- `docs/edit-log-compaction.md`: what a long edit log costs and the methods compared.
- `docs/anti-aliasing.md`: the research behind SMAA 1x before FSR 1.
- `docs/procedural-audio.md`: sound made in code, after Farnell's *Designing Sound*.
- `docs/sound-files.md`: sounds and music read from WAV and Ogg Vorbis files, the choices made, and the measurements.
- `docs/physics-materials-and-groups.md`: gravity, the solver, physics materials and the exact bounce, layers and
  groups, triggers, and impact events a game sets, the choices made, and what the reviews found.

## 2. Dependencies and their roles

Versions are pinned in `Directory.Packages.props` (central package management).

| Package | Version | Role | Why this one |
|---|---|---|---|
| Silk.NET.Windowing + .Windowing.Glfw | 2.23.0 | Window creation, event loop | Bundles GLFW 3.4 for Linux, Windows, macOS, with Wayland support. Chosen over SDL because the Vulkan path is better exercised. |
| Silk.NET.Input + .Input.Glfw | 2.23.0 | Keyboard, mouse, gamepad | Matches the windowing backend. |
| Silk.NET.Vulkan + .Extensions.KHR + .Extensions.EXT | 2.23.0 | Rendering API | Vulkan is the one modern API available on all three platforms (through MoltenVK on macOS). |
| Silk.NET.MoltenVK.Native | 2.23.0 | Vulkan on macOS | Ships `libMoltenVK.dylib` so no user install is needed. |
| Silk.NET.Shaderc + .Shaderc.Native | 2.23.0 | GLSL to SPIR-V at runtime, targeting Vulkan 1.3 | No build-time shader toolchain needed; enables shader hot reload. |
| Silk.NET.OpenAL + Silk.NET.OpenAL.Soft.Native | 2.23.0 / 1.23.1 | Audio output | OpenAL Soft is bundled for every platform, so a game runs without a system OpenAL. |
| NVorbis | 1.0.0-rc.2 | Decodes Ogg Vorbis sound files and music (`Audio/Files/VorbisDecoder`) | MIT and managed; a release candidate pinned for its fixes over 0.10.5, whose seeks land off and can throw (`docs/sound-files.md`). It allocates as it decodes, so it runs on the sound render and music threads. |
| Silk.NET.Maths | 2.23.0 | Vector and matrix types used by Silk.NET APIs | Required by the windowing API surface. |
| Arch + Arch.System | 2.1.0 / 1.1.0 | Entity component system | Archetype-based, allocation-free queries, fast iteration. Game logic is systems over components. |
| BepuPhysics + BepuUtilities | 2.5.0-beta.29 | Rigid body physics: loose bodies and a game's bodies against the voxels and mesh colliders, with the game's materials, layers, triggers, and gravity (characters move with Itera's own mover) | Fast, SIMD-based, actively maintained. The 2.4 stable line only targets net6.0 and is old. |
| SkiaSharp + SkiaSharp.NativeAssets.Linux | 4.152.1 | 2D UI drawing | Solid text and vector rendering. Linux needs the explicit native package; Windows and macOS natives come with the main package. |
| BCnEncoder.Net | 2.3.0 | Block-compressed texture encoding, in `tools/Textures` only (texture sets, models' compressed textures) | Pure managed BC1 to BC7 encoder. |
| SharpGLTF.Core | 1.0.7 | Reads glTF 2.0 models (`Assets/GltfImport`), at load only | MIT, no dependencies, reads .gltf and .glb with skins and animations; the engine copies what it needs into its own arrays. It cannot read Draco or meshopt compression, which the importer refuses by name. |
| LiteNetLib | 2.1.4 | UDP transport and reliability for sessions over LAN and direct addresses | Small, dependency free, widely used for .NET games. |
| Steamworks.NET | 2024.8.0 (Steamworks SDK 1.60) | Internet play: Steam's networking sockets and relays (no port to open), friends-only lobbies, rich presence for joining from the friends list | A thin, faithful wrapper of the C++ API (MIT). The package has no native libraries: Valve's `steam_api` for Linux x64, macOS (universal), and Windows x64 from the same release are in `src/Itera.Engine/Native/Steam/` and copied next to the game. |
| Microsoft.Extensions.Hosting / DependencyInjection / Logging / Options | 10.0.12 | Configuration, DI, logging | Standard .NET application plumbing. |
| Serilog + Extensions.Hosting + Settings.Configuration + Sinks.Console + Sinks.File + Sinks.Async + Enrichers.Thread | 4.4.0 / 10.0.0 / 10.0.1 / 6.1.1 / 7.0.0 / 2.1.0 / 4.0.0 | Console and rolling log file behind `ILogger` | Standard, well tested. Per-run files, size rolling, async writes, per-category levels from configuration. Engine code only sees `Microsoft.Extensions.Logging`. |
| xunit | 2.9.3 | Tests | Default .NET test framework. |

Deliberate omissions:

- The `Silk.NET` metapackage. It pulls every binding Silk.NET has.
- `Arch.Relationships`. It depends on Arch 1.2 and does not work with Arch 2.x.
- `Arch.System.SourceGenerator`. Version 2.1.0 generates `[Query]` code that fails this build (a nullable warning under
  warnings-as-errors, and duplicate public attribute types when two projects use it). The fixes exist only on its
  unreleased `master`.
- NativeAOT and trimming. Mods are dynamically loaded assemblies that need the JIT and reflection. Steam builds use
  composite ReadyToRun instead, published as a single file (`tools/steam/upload.sh`; the install folder's layout is in
  `tools/steam/README.md`).

## 3. Solution structure

```
Itera.sln
Directory.Build.props        shared compiler and runtime settings for every project
Directory.Build.targets      writes the sound code hash (see "Sound is made in code")
Directory.Packages.props     every package version
src/Itera.Modding            the mod contract: IGameMod, IModContext, ModManifest, ModManifestReader, FrameTime, FixedStepTime
src/Itera.Engine             everything that is not specific to one game
src/Game                     the starter game: the executable, its composition, a generator, a palette, a start pose
samples/Itera.SampleMod      reference mod, copied into the game's Mods/ folder on build
samples/Itera.MeshSample     a game without voxel terrain: mesh ground and blocks with colliders, the fox, climbing, balls,
                             a render pass, a screen showing an image view
tools/benchmark              scripts that run the GPU benchmark (Engine:Benchmark) and compare runs
tools/control                game.py: starts a game from a copy of the build and drives it through the control port
tools/steam                  upload.sh: tests, publishes, and uploads a build to a Steam branch
tools/Textures               makes the world materials' texture sets (compressed DDS files) from photographs or baked recipes,
                             and models' compressed textures (model mode)
tests/Itera.Modding.Tests    manifest parsing and validation
tests/Itera.Engine.Tests     the engine: see below
tests/Game.Tests             the starter: its composition and system order, shipped settings, options, and generator,
                             the fox, the minimap's system and HUD layer, and the starter played without a window
                             (`ScenarioTests`, about 6 s)
tests/Itera.MeshSample.Tests the mesh sample played without a window, its yard's meshes, its fox, its render pass and
                             the system that aims it, and its yard camera
tests/Shared                 helpers compiled into the test projects that use them (SystemAllocation, ArchStartup,
                             SerialCollectionDefinition, RunsLongAttribute and LongFirstCollectionOrderer, VoxelLoading,
                             FakeTransport, HostileValues and Watchdog, WireFingerprint, LayerInk)
```

`tests/Itera.Engine.Tests` holds the tests of time, mod loading, shader compilation, GPU struct layout, GPU memory
ranges, input events and pointer capture, ECS checks, the engine's components and systems, deterministic maths, voxel
storage, streaming, raycasts, edits, and collision queries, the character mover, loose bodies and ragdolls, the
navigation grid and flow fields, poses and foot placement, the Surface Nets mesher, the world's meshes and mesh pass
(upload, draw order, shadow cascades, stitching, and the sky field against a fake mesh target), particles, sound, the
UI and menu, the console, sessions over a fake transport (with wire fingerprints), saves, the match flow, and the
lifetime of a match (an ended match leaves nothing behind). `tests/Itera.Engine.Tests/Matches/EngineComposition.cs`
is the least a game adds to the engine (a floor generator and one fixed and one frame system), so the engine's behaviour
is tested without the starter.

Dependency direction:

```
Itera.Modding   <-- Itera.Engine   <-- Game
      ^
      +-- Itera.SampleMod (and every other mod)
```

Rules:

- `Itera.Modding` references nothing from the engine. Mods reference only `Itera.Modding`, so a mod build never pulls in
  Silk.NET, Bepu, Skia, or Arch, and the host can change any of those without breaking mods.
- `Itera.Engine` references `Itera.Modding` because the loader needs `IGameMod`. The loader lives in the engine so a
  future headless server can reuse it.
- `Game` references both and owns the composition root. Change engine files only to fix or extend the engine, and send
  those changes back to Itera; keep game code in the game's folders, so a pull from Itera merges cleanly.
- The game names itself to the engine through `Engine:Application` (`Name`, and `DataFolder` for logs, saves, the shader
  cache, and `settings.json`).

`Itera.Engine` folders:

| Folder | Contents |
|---|---|
| `Time/` | `GameClock` (clamped frame delta), `FixedStepAccumulator` (60 Hz steps, capped per frame; paused by the console, held by the menu over an offline match) |
| `Windowing/` | `WindowService`: creates the GLFW window, pumps events, exposes framebuffer size, refresh rate, and exit request |
| `Input/` | `InputService` (collects key, button, movement, and scroll events into one `InputSnapshot` per frame and one per fixed step), `InputAccumulator`, `PointerCapture` (a left click locks the pointer for mouse look; Escape goes to the menu at once and frees it), `IPanelInput` (a panel that takes the free pointer), `ITextInput` (where typing goes: the console, the menu's text box, or a game's, `SetTextFocus`), `IEscapeHandler` (a game's screen that Escape closes before the menu opens), `IKeyboardFocus` (the console takes the keys while open), `IMenuInput` and `UiPointer` (the menu), `KeyBindings`, `KeyAction`, and `InputBinding` (the actions a game registers and the Settings page's rebinding), `ITickInput` and `IFrameInput` (what fixed and frame systems read) |
| `Rendering/` | `VulkanContext`, `DeviceRequirements`, `VulkanSwapchain`, `SwapchainState`, `FrameSync`, `FrameRenderer` (fence wait, acquire, barriers, recording the passes, submit, present, recreate), `GpuTimer`, `CameraView` (with its `CameraProjection`) and `RenderView` (the camera, its lens, and the atmosphere of the frame being built), `Atmosphere`, `RenderThread`, `FrameExchange` (hands each frame from the main thread to the render thread), `DebugLines` and `DebugLinePass`, `UiPass`, `RenderScale` and `UpscalePass`, `SmaaPass` and `SmaaTextures`, `FrameImages`, `DynamicRenderScale`, `LoadingPass`, `ParticlePass`, `Screenshots`, `ScreenshotPass`, `ScreenshotEncoder`, `DepthTarget`, `ShadowCascades`, `IWorldOnGpu`, `ModelInstances` (the models frame systems ask to draw this frame, each with its `ModelShadow`), `MeshInstances` (the mesh assets they ask to draw, and the posed ones' skinning matrices), `PointLights` (event lights), `RenderPasses` (a game's own passes, `IRenderPass`, at five stages of the frame), `RenderViews` and `RenderViewPass` (image views: views a game draws into images, which the HUD and mesh materials show), `RenderLook` and `LookTuning` (the look a game sets each frame, and the console's `look` over it), `SunPath` (how much light the sun gives round the horizon) |
| `Rendering/Memory/` | `GpuMemoryAllocator` (own block sub-allocator: 64 MB blocks, dedicated allocations for large resources, persistent mapping, flush and invalidate), `MemoryTypeSelector`, `BlockSuballocator`, `GpuBuffer`, `GpuImage`, `StagingBuffer` (one upload region per frame slot), `GpuRangeHeap` (first-fit ranges of 256-byte granules in one buffer, for the mesh heap) |
| `Rendering/Pipelines/` | `PipelineFactory`: shader modules, pipeline layouts with push constants, full-screen, line, and triangle graphics pipelines without vertex input (the mesh shaders read their vertices from storage buffers) for dynamic rendering, and compute pipelines |
| `Rendering/GpuData/` | Structs that shaders read, each with a `GpuLayout` test: `ViewConstants` (camera vectors, sun, sky, frame slot, view distance, image size, switches, near and far; push constants, 116 bytes), `MeshVertex`, `MeshDraw`, `MeshModelDraw`, `MeshMaterial`, `MeshLighting` and `MeshLight`, the mesh assets' `MeshAssetVertex`, `MeshAssetDraw`, `MeshAssetMaterial`, `MeshSkinVertex`, `MeshSkinGroup`, and `MeshSkinConstants`, the look a frame slot holds (`LookConstants`, `look.glsl`), the passes' constants (`SmaaConstants`, `UiConstants`, `UpscaleConstants`, `LoadingConstants`, `ParticleConstants`), the particles' records, and `DebugLineVertex` |
| `Rendering/Voxels/` | `IVoxelPalette` and `VoxelLook` (how each material is drawn), `IVoxelTintMap` (a 256 x 256 map over the world that blends each material from its colour to its tint colour) and `VoxelTintSampler` (reads it on the CPU as the shader does), `StagingWriter` |
| `Rendering/Materials/` | `MaterialTextureSets` and `DdsFile`: the materials' texture sets read at start into texture arrays |
| `Rendering/Meshes/` | `WorldMeshes` (the world and the models as triangle meshes; partial files for the draws, the models, the point lights, the sky field, and the stitches between levels), `MeshWorkers` (the threads that mesh), `MeshColumn`, `LevelBalance`, `MeshPass` (the mesh heap, the frame's buffers, the shadow maps, the multisampled images, the recording), `MeshView` (the frame's frustum, the cascades' fit with `CascadeBounds`, and the point lights, shared by every list drawn in `MeshPass`), `MeshFrame`, `SkyField`, the mesh assets' `MeshAssetPass` (their buffers, textures, set 1, pipelines, and skinning), `MeshAssetLayout`, `MeshAssetDraws`, and `MeshAssetFrame`, an image view's records and sets (`RenderViewTarget`; `MeshPass.Views.cs` and `MeshAssetPass.Views.cs` draw it), and the interfaces that tests replace (`IMeshDrawTarget`, `IStagingRegions`, and `IRenderViewLists` (`IMeshTargets.cs`)) |
| `Meshing/` | `SurfaceNets` (the CPU mesher: a 32-voxel block of materials and densities with a 2-voxel apron into vertices and 16-bit indices, with a transition mode beside a coarser level) and `MeshBuffers` |
| `Rendering/Shaders/` | GLSL files copied to `Shaders/` in the output: the full-screen, mesh, mesh asset, skinning, shadow, sky, FSR, SMAA, loading, UI, debug-line, particle, cluster, and material-bake shaders, and the include-only files (`view.glsl`, `mesh-asset-data.glsl`, `turns.glsl`, `mesh-data.glsl`, `mesh-light.glsl`, `mesh-surface.glsl`, `mesh-shade.glsl` (the light and grade every surface of the mesh pass takes), `surface-noise.glsl`, `lighting.glsl`, `sky.glsl`, `particle-data.glsl`, `particle-constants.glsl`, `look.glsl`); a game's own shaders are the tree's `Game/` folder (`Shaders/Game/` in the output); `ShaderCompiler` (GLSL to SPIR-V through Shaderc with a disk cache), `ShaderSource` (`#include` expansion) and `ShaderRoots` (where the engine's and the game's shaders are read from), `ShaderReloads` and `IReloadsShaders` |
| `Particles/` | `ParticleEmitter` (bursts queued for the next frame, the pool's ring, attractors, the stress test), `ParticleBurst`, `ParticleRecipe` and `ParticleRecipes` (by name, at most 128), `ParticleStats`, `ParticleColours`, `StockEffects` (seven general recipes); the GPU side is `Rendering/ParticlePass` |
| `Native/` | Valve's `steam_api` library for each platform (`Native/Steam/`, with a README naming the release), copied next to the game. |
| `Audio/` | `AudioService` (where game code queues sounds), `AudioMixer` (the audio thread's work), `RecipeRenderer`, `AudioExchange`, `AudioStats`, `SoundRecipe` (`OneShotRecipe`, `StreamRecipe`, `SoundGenerator`; `FileRecipe`, `FileOneShotRecipe`, and `FileLoopRecipe`, sounds read from files) and `SoundRecipes` (by name, at most 256), `Files/` (the WAV and Ogg Vorbis readers), the music (`MusicPlayer`, `MusicTrack`, `MusicExchange`, `MusicFeed`, `MusicStreamer`, `MusicDecks`), `SoundEvent` and `SoundLoop`, `EngineSounds`, `SoundCache`, `WavWriter`; `Synthesis/` (`SoundRandom`, `Dsp`, `OnePole`, `StateVariableFilter`, `NoiseSource`, `ModalBank`); the device (`IAudioDevice`, `OpenAlAudioDevice`, `NullAudioDevice`, `AudioDeviceFactory`) |
| `Loading/` | `LoadingScreen`, `ILoadingTask` and `LoadingProgress`, `EngineLoadingTasks` (Sounds, Physics, World, Navigation, Warm-up), `ModelsLoadingTask` (waits for a list of models to reach the GPU) |
| `Ecs/` | `EcsWorld` (Arch world plus fixed and frame system groups), `IFixedUpdateSystem`, `IFrameUpdateSystem`, `EcsChecks` and `CheckedWorldExtensions` (Debug-only precondition checks for Arch calls) |
| `Components/` | The ECS components the engine's systems share: cameras, characters and players, loose bodies, and `NetEntity` |
| `Systems/` | The systems a game registers: the player, the cameras, physics glue, streaming centres, the benchmark, and debug aids (see "The engine's components and systems") |
| `Maths/` | `DeterministicMath`, `Rotation`, `RigidTransform`, and `Octahedral` (the packed normals and tangents of mesh vertices) |
| `Assets/` | Mesh assets (`MeshAsset`, `MeshAssets`): models read from glTF files at the run's "mesh assets" step (`GltfImport`, `TextureDecode`, `MipChain`, `MeshTangents`, `TextureReading`; a model's compressed textures `CompressedTextures`) and meshes a game builds; a skinned model's `Rig`, `RigClip` with `RigTrack`, and `JointTransform` |
| `Animation/` | Skeletons, poses, clips, aim, and foot placement |
| `Physics/` | `PhysicsWorld` (Bepu simulation, pool, thread dispatcher, shared voxel box shapes, characters' stand-ins; a game's bodies, statics, and trigger zones), callbacks, `BodyKinds` (the records by handle), `PhysicsMaterials`, `PhysicsLayers`, `PhysicsContacts` (bounces and impact events), `PhysicsTriggers`, `TriggerOverlap`, `VoxelColliders` (with the voxel objects' statics near bodies), `LooseBodies`, `LooseBodyVoxels`, `SolidModelVoxels` and `SolidColumns`, `VoxelCharacterMover`, `MeshColliders` (a game's static triangle colliders, also Bepu mesh statics), `MeshColumns` (the colliders as the mover's cells), `ObjectColumns` (the voxel objects as the mover's cells, which navigation shares) with `ObjectColumnReader` (one object's column), `CollisionSources` (the ray over the terrain, the voxel objects, the colliders, and the solid models), `SurfaceHit` |
| `Voxels/` | `VoxelWorld` (streaming, edits, change list, stats, `Raycast`, collision queries, block reads), `VoxelGrid` and `GridLevel`, `VoxelDensity`, `IVoxelPassability`, `VoxelBoxes`, `VoxelMemory` (native block pools), `BrickCodec`, `ChunkTree`, `Column`, `VoxelEditing` and `VoxelEdit`, `VoxelPiece` and `PieceRuns`, `VoxelModels` (small voxel volumes drawn at any position and turn), `EditLog` and `EditLogSerializer`, `VoxelChangeLog`, `IChunkGenerator`, `VoxelWorkers` and `ColumnJob`, `LevelDistances`, `StreamingCentre`, `VoxelConstants` |
| `Voxels/Generation/` | The world generation toolkit (see "World generation") |
| `Objects/` | Voxel objects finer than the terrain: a kind's voxels (`VoxelObjectShape`), the run's kinds (`VoxelObjectKinds`), a match's models of them (`VoxelObjects`, `ObjectsLoadingTask`), a world's placer (`IObjectPlacer`, `ObjectPlacement`, `ObjectId`), the objects placed near the loaded world (`ObjectWorld`), their far copies in the world's voxels at their own size (`ObjectCopies`, `ObjectShareGrid` and `ObjectShareBuilder`), rays through them (`ObjectRays`, `ObjectHit`), the damage edits do to them (`ObjectDamage`, `DamagedObject`, `ObjectEditing`, the voxel sources `IObjectVoxelSource`) the cuts the world's owner finds (`ObjectLabeller`, `ObjectRoots`), and the bodies of the parts cut off them (`FallenParts`), and the headings' turns (`ObjectTurns`; `docs/voxel-objects.md`) |
| `Navigation/` | `NavigationService` (its window, `NavArea`), `NavGrid`, `NavSize`, `NavCostTable`, `NavFieldSpec`, `INavGridSource` (the game's part), `FlowFieldBuilder` and `FlowField`, `NavGridScanner` (with the voxel objects in its reads and stamps), `NavObjects` (the objects as generated, for the navigation thread's fills), `NavigationDebugLines` |
| `Networking/` | `SessionService` (hosting or joining; `.Host.cs`, `.Client.cs`), `ISessionTransport` with `NetworkService` (LiteNetLib) and `SteamTransport` over `ISteamPlatform`, `SteamService`, `SteamAddress`, `SteamLaunchArguments`, `SteamFriendList`, `SessionProtocol` (message kinds, `MessageWriter`, `MessageReader`), `MatchSettings` and `ISessionGameHandler`, `GameMessageHandler`, `SessionAddress`, `NetLoggerAdapter`, and the helpers `NetEntities`, `PlayerIds`, `TickRing`, and `JitterPlayback` |
| `Replication/` | `ReplicationService` (`.Host.cs`, `.Client.cs`: what a host sends each client of the entities a game marks, and how a client shows them), `ReplicatedKinds` with `IReplicatedKind`, `ReplicatedSchema` and its fields (`ReplicatedFields.cs`: `ReplicatedState`, `PositionField`, `AngleField`, `RotationField`, `ScalarField`, `IntegerField`, `FlagsField`), `ReplicatedStateCodec` |
| `UI/` | `UISurface` (the UI's pixels: a Skia CPU surface that draws its layers when one changes and reports the rows that changed), `IUiLayer`, `IMatchUiLayer`, `ConsoleLayer` and `DevConsole`, `StatsLayer`, `AimDotLayer`, `LoadingLayer`, `CentredMessageLayer`, `TextBlobs`, `TextLine`, `TextEntry` (a text box's typing), `UiFonts` (the font and the UI's sizing rule) and `LayerFont`, `Amounts`, `WheelSteps`, `Toolkit/` (a UI described in code: `Ui`'s layout, input, focus, and dump; `UiStyle`, `UiPainter`, `UiToolkit`, `UiLayer`), and `Menu/` (`IMenu`, `MenuLayer` on the toolkit, `MenuPages` and `IMenuPage` (a game's pages in it), `SettingsPage` (the Settings page as a part a game's menu shows too) and `SettingsRows` (a game's switches on it), `MenuStyle`, `RadialMenu`, `RadialRing`, `ScrollBar`) |
| `Commands/` | `ConsoleCommands` (name, usage, help, scope, owner), `CommandLine`, `EngineCommands`, `MatchCommands`, `SettingsCommands`, `ParticleCommands`, `SoundCommands` |
| `Control/` | `ControlService` (the control port), `ControlRequest`, `InputScript` |
| `Modding/` | `ModLoader`, `ModLoadContext`, `ModDispatcher`, `HostSharedAssemblies`, `LoadedMod`, `ModContext` |
| `Diagnostics/` | `LoggingServiceCollectionExtensions` (`AddIteraLogging`, Serilog sinks), `LogSession`, `LogPaths`, `LogRetention`, `SessionInfo`, `FrameStats`, `GpuTimings`, `ConsoleLogBuffer`, `ControlLog`, `UnhandledExceptionLogger` |
| `Matches/` | `MatchFlow` (a match's start, join, and end), `MatchStart`, `MatchContext`, `MatchServices` (a match's services and steps), `MatchControl`, `JoinRequests` (a friend's Steam join, whatever menu shows), `WarmUps`, `IRunStartup` |
| `Saves/`, `Settings/` | `SaveService`, `SaveFiles` (the saves a menu offers); `PlayerSettings` (the menu's settings and a game's own on/off switches, `GameSwitch`, kept in `settings.json`) and `CommandLineKeys` (the keys the command line gave, which win over the file) |
| root | `EngineOptions`, `EngineOptionsValidator`, `UnknownOptionKeys`, `EngineServiceCollectionExtensions` (`AddIteraEngine`), `VoxelServiceCollectionExtensions` (`AddIteraVoxels`), `EngineSteps`, `GameLoop`, `ExitSignalPolicy` |

## 4. Runtime model

**Composition root.** `Program.cs` builds a `HostApplicationBuilder` for configuration (`appsettings.json`, environment
variables, `--Engine:Key=Value` arguments), logging, and DI, then resolves `GameLoop` and calls `Run()` on the main
thread. The host's own `Run()` is not used.

**Services.** Every service has a light constructor and an explicit `Initialize()` method. A run's services are
singletons; a match's (its session, world, physics, navigation, save, ECS world, the game's systems and stores, the HUD
layers, and the loading tasks) are Scoped: each match is a DI scope of its own (`MatchServices`), made when it starts
and disposed when it ends, so one run plays many matches (see "A run plays many matches"). A singleton never takes a
scoped service in its constructor; `Program.cs` turns on DI's scope checks in every environment. The order is written in
two places, each step naming its init and shutdown actions. The run's, `GameLoop.CreateSteps`:

1. network (LiteNetLib's log; no socket), 2. Steam (the Steam API when the client runs and the game has an App ID;
optional), 3. settings file (`settings.json` read over the options), 4. window (full screen when the player's settings
say so), 5. Vulkan context, 6. swapchain (VSync as the settings say), 7. frame sync, 8. GPU memory (allocator and
staging buffer), 9. input, 10. shader compiler (with a smoke-test compile of `smoke-test.vert`), 11. audio (OpenAL or
silent fallback, and the engine's sound recipes), 12. settings (the player's volume and render scale applied;
`settings.json` written at shutdown), 13. renderer (every pass: the depth image, the mesh pass and the world's meshes
(the heap is sized from the options alone, and the meshes follow a match's world only from `BeginMatch`), the particle,
line, UI, upscale, SMAA, and loading passes, and the timestamp query pool), 14. UI (the surface at the swapchain's size,
the engine's console commands, the console as the keyboard focus, and the menu for the input), 15. game
(`IRunStartup.Start`: the game's run-wide registries, its sound and particle recipes, keybindings, on/off settings, and
image views), 16. mods, 17. mesh assets (the models declared, read), 18. mesh assets on the GPU, 19. image views (each
declared view's records, images, and sets), 20. render passes (the game's own), 21. audio thread (the music tracks'
headers read first, on the main thread; the music thread, with tracks, and the sound render thread, which renders the one-shot recipes'
variations or reads them from the sound cache, and reads the sounds from files, while the audio thread plays what is
ready), 22. render thread, 23. control port (off unless `Engine:Control:Enabled`; last, so it stops first). Steam starts before the Vulkan instance, whose layers it adds. Every
Vulkan object is made here, so starting or ending a match makes no Vulkan call and the render thread runs from start to
exit.

A match's, `MatchServices`: first the session (offline and hosting settle the match at once; a client connects, and
`SessionService.PollJoin` carries the join on once a loop iteration while the menu shows its progress, until the host's
edit log is downloaded), then, once a frame saying "Starting…" has been shown, physics (the physics warm-up once per
run), voxels (memory and worker
threads, for the match's seed; the edit warm-up once per run), joined world (a client loads the host's edits, a named
save its own), navigation, voxel physics, objects (the object kinds' models, `VoxelObjects`, the placed objects, `ObjectWorld`, and their damage, `ObjectDamage`), save (the
last save written at the match's end, after an autosave still being written), tint (the match's tint map for the
particles' colours), world on the GPU (`WorldMeshes.BeginMatch`), match UI (the match's console commands, which go when
it ends, and the HUD layers), ECS world and systems, save sections (a named save's game state handed to the game's
`ISaveSection`s before the first step; at the end the last save captured while the systems still exist), mods
(`IGameMod.OnMatchStarted`), and loading (the loading screen takes the match's tasks). The session settles the match before anything reads the seed (`SessionService.Match` throws
until then; a game's generator and start pose take the seed from it). ECS comes after physics and voxels so systems can
use them in `Initialize`. A match ends with its session's goodbye, its steps in reverse, and its scope disposed; the run
puts back what it kept of it (the view, the title, the particles). Options are validated (`EngineOptionsValidator`, and
`UnknownOptionKeys` for misspelt keys) when `GameLoop` is resolved; a bad value stops the game with exit code 1.

**Main loop (main thread).** Each iteration: handle the window events that wait (GLFW's poll), then, when no frame is
requested, sleep on a managed event until the next fixed step is due (a request for a frame from the render thread ends
the wait at once; GLFW's own `glfwWaitEventsTimeout` is not used: on Wayland it once spun at full load inside the call
for 16 minutes after a focus change), read the clock (delta clamped to 0.25 s), run Steam's callbacks, poll the session
(a joining client's join goes on; a host answers hellos and sends joining clients the world; a client applies the host's
edits), update input (lock or release the pointer), take `MatchControl`'s requests (a client whose session ended, a
leave, or a start, which ends the match now first; matches start and end here, between iterations' work), and, while a
match's world exists, update the voxel world (install at most 8 finished columns, unload at most 16 far ones, queue new
ones), run the console's commands (`DevConsole.RunPending`: the log lines since the last iteration go into the
scrollback, then `Engine:Console:Run` the first time a match's world exists, then the lines typed; so commands run on
the main thread between fixed steps, never inside a query, and also while no frames are drawn), then the control port's
requests (`ControlService.RunPending`), then run the due fixed steps while a match's world exists (at most 8 per
iteration; excess time is dropped; the match's start restarts the count at tick 0). Each fixed step takes the input
since the last step (`InputService.BeginTick`, read by fixed systems through `ITickInput`), then runs the ECS fixed
group (systems receive `FixedStepTime`), then applies the large edits that wait (`ApplyPendingEdits`), brings the Bepu
statics near dynamic bodies up to date with the step's edits and the bodies' positions (`VoxelColliders.Update`), then
the physics step, then takes the loose bodies' new poses for the character mover (`LooseBodyVoxels.Refresh`), then
`IGameMod.OnFixedUpdate`, then the fields that the navigation thread finished are taken, then the session's send (a host
sends each joined client the edits made since the last step, in order). The loop also ends when the session of a match
the options started ends (the host quit, the connection was lost, or the host sent something it could not accept); any
other match's end goes back to the menu. An exception in the iteration's work while a match the menu or the console
started is on ends that match unsaved (`MatchFlow.EndAfterFault`, logged at Error), owes the render thread the frame it
took the request for (`FrameExchange.RequestTaken`), and the run goes on at the menu with "The match ended because of an
error"; a device's failure (`VulkanException`) and an exception in a match the options started still end the run. If the
render thread has asked for a frame with no match's world (the menu, a join, a match's start), the iteration builds one
of the loading screen's ring with the UI over it, uploading and drawing nothing of a world. Otherwise it builds one: the
input snapshot of the events since the last frame (`InputService.Frame`), `DebugLines` and `ModelInstances` cleared, the
event lights started (`PointLights`), the ECS frame group (systems receive `FrameTime`, including `Alpha`; one of them
puts the active camera in `RenderView`), `IGameMod.OnUpdate`, the UI draw (`UISurface.Draw` at the request's size: each
layer updates, and the surface is drawn again only when a layer changed, appeared, or went), the image views due this
frame started (`RenderViewPass.BeginViews`: each one's constants from the camera a frame system asked with, and its
`MeshView`), and the world's meshes (`WorldMeshes.Upload`: it follows the voxel change list, reads blocks and starts
their meshing nearest the view first and stops reading after 2 ms, stages the finished meshes, the models' too, into the
slot's staging region, picks each column's level, and writes the frame's block draws, the models' draws from
`ModelInstances`, the point lights, and the shadow cascades' lists into the slot's regions, and each due view's lists
into its own; it takes 0.12 to 0.35 ms a frame when nothing loads), the mesh assets' lists (`MeshAssetDraws.Write`, the
views' too), the views ended with the frame's lights and the HUD's panels (`RenderViewPass.EndViews`), the debug lines
while `lines` has them on, the particles (`ParticlePass.Write` drains the bursts queued since the last frame, with their
pool slots, and writes them, the recipes, and the attractors into the slot's regions), a game's render passes' slot
memory (`RenderPasses.Write`), and hands it over, with the camera, the upload's copy count, the line count, and the mesh
frame's counts, through `FrameExchange.Publish`. Between taking the request and publishing, frame systems may write the
GPU memory of the request's frame slot.

**Control port (`ControlService`, `Engine:Control`).** A development tool, so a check needs no restart: off unless
`Engine:Control:Enabled` is true, it listens on TCP 127.0.0.1 (port 47650), and `tools/control/game.py` drives it. Any
program on the machine can connect, so shipped builds never turn it on. Each line a tool sends is a JSON request and gets
one JSON reply line. A thread accepts connections, and a thread per connection reads its requests one at a time, queues
each for the main thread, and wakes the loop (`WindowService.Wake`); only `log` is answered on the connection thread, from
`ControlLog`. The main thread starts them in `RunPending`, between fixed steps where console commands run, and checks the
waiting ones each iteration against the game's clock and the frames built. The ops:

- `run`: console commands, whose answers also show in the in-game console.
- `log`: log lines of Debug and above, from `ControlLog`, a numbered ring that a second Serilog sink fills only while the
  port is on.
- `wait`: for loading, frames, seconds, or a log line.
- `gpu`: each pass's time over the next frames, from a second set of sums in `GpuTimings` that leaves the stats line's
  alone.
- `status`.
- `shot`: a screenshot.
- `reload`: the shaders compiled again.
- `input`: keys, buttons, look, the wheel (`scroll`), the pointer at a pixel (`point`, for the menu), typed text
  (`type`), waits, and capturing the pointer without the window's focus (`InputScript`, through `InputService.Inject*`).

A reply carries the log lines of Information and above written while its request ran. The port obeys the render thread's
rule: the main thread only queues screenshots (`Screenshots`) and shader reloads (`ShaderReloads`), which the next
`RenderFrame` carries.

- **Screenshots.** On the render thread, `ScreenshotPass` records a copy of the swapchain image into a readback buffer,
  after the UI or before it. The swapchain has `TransferSrc` usage where the surface offers it. It reads the copy after
  the slot's fence, and a worker writes the PNG with Skia, cut and scaled as asked (`ScreenshotEncoder`).
- **Shader reloads.** Before recording the frame, the render thread compiles every pass's shaders from
  `Engine:Renderer:ShaderSourceDirectory` (`IReloadsShaders`). Only when all of them compiled does it wait for the device
  and make their pipelines again; an error keeps the old ones and is reported with its file and line. It takes about
  0.6 s for one edited shader and 10 ms when the SPIR-V cache holds them all. Idle iterations allocate nothing.

**Loading screen (`LoadingScreen`, `Engine:Loading`).** From the loop's first iteration the loading screen covers the
picture until every `ILoadingTask` is done: the engine's Sounds (every one-shot recipe in buffers, rendered or read from
`SoundCache/` in the data folder), Physics (the physics warm-up), World (every column the streaming centres want, at every level, resident, then an
upload that has seen them), Navigation (the grid and fields taken, or none on a client or without a source), and Warm-up
(the edit warm-up), then the game's own tasks, registered after them (`ModelsLoadingTask` waits for a list of models to
reach the GPU). Loading is a phase of the loop, not a wait before it: streaming, uploads, fixed steps, the session, and
the console run as usual, because most of what it waits for moves on only while they do. While it covers the picture the
game gets empty input snapshots (Escape and the console still work), fixed steps dropped are reported in its own line
instead of the frame stats' Warning, and the frame carries a `LoadingDraw`: the render thread still records the mesh
copies and the particle simulate call, then draws `loading.frag` (a retro ring of voxels, `LoadingPass`) over the whole
swapchain image instead of the world, then the UI, where `LoadingLayer` lists the tasks. When all are done, or after
`MaxSeconds` (default 15, a Warning naming what had not finished), it fades out over 0.3 s, blended over the world. The
starter's covers the picture for about 3.5 s in Debug (the world; the edit warm-up runs one round there, since Debug
code is never optimised) and 2 s in Release. After a reset of the world (the console's `reset`, or the host's on a
client) it covers the picture again (`LoadingScreen.BeginAgain`) and waits for every task afresh: the tasks already done
report done at once, World waits for the view generated again, and Navigation for the grid filled again.

**Window modes (`Engine:Window:Mode`).** A run draws into a window; or offscreen (`Offscreen`), at
`Engine:Window:Width` x `Height` into a headless surface (`VK_EXT_headless_surface`) with nothing on the desktop, for
screenshots and checks; or with no graphics (`None`): no window, Vulkan, or render thread, while the fixed steps and the
frame group run. Nothing paces a headless present, so offscreen frames are capped at `Engine:Window:OffscreenFps` (240
by default): the world uploads a share each frame, and at 60 the starter's view did not load in 15 s.

**Render loop (render thread).** Each frame: wait on the frame slot's fence and reset its command pool
(`FrameRenderer.BeginFrame`), acquire an image, ask the main thread for a frame (`FrameExchange.Request`), wait for it,
then record the frame, submit, and present (`FrameRenderer.Present`). The frame's commands: when the main thread staged
meshes, the copies from the slot's staging region into the mesh heap (`MeshPass.RecordUploads`, one `vkCmdCopyBuffer`
between two barriers), and a new sky field copied into its 3D image, under the loading screen too; the lights sorted
into the view's clusters; while particles can be alive, the particle passes (the counters reset, the spawn and simulate
dispatches, and the counters copied to the slot's readback region); the swapchain image's transition; the world
(`MeshPass.Record`: the posed mesh assets skinned and a game's `Compute` passes, the sun shadow cascades (two, three
with the distant one) drawn into the layers of one depth map image, then the camera's blocks drawn nearest first, the models, the mesh assets, a game's
`Opaque` passes, the sky where the depth is still 0, the blended mesh assets back to front, and a game's `Transparent`
passes, into multisampled colour and depth images that are resolved as rendering ends into the target and the depth
image); in a fresh frame, each image view due (`RenderViewPass.Record`: its own world pass at one sample, then a copy
into the image the shaders and the HUD read); the particle cubes and then sprites while particles can be alive (one
indirect draw each, tested against the depth image); the debug lines when there are any (tested against the depth image
and drawn over the picture, which is loaded, not cleared); a game's `Scene` passes; SMAA's three passes when the world
is drawn with one sample a pixel and anti-aliasing is on; FSR 1's upscale when the render scale is below 1; a game's
`Screen` passes (the world, the views, and a game's passes are left out while the loading screen covers the picture);
the loading screen while it covers the picture or fades out; the UI when it changed or shows anything (the changed rows
copied from the slot's region of the UI upload buffer into the UI's storage buffer between two barriers, then a
full-screen triangle whose fragment shader draws the image views the HUD shows, reads its pixel, and blends it,
premultiplied, over the picture); the transition for presenting; and timestamps (`GpuPass`) around the frame, the mesh
copies, the particles' simulation and draws, the world (with its shadow maps, its models, its mesh assets, and their
skinning apart), the image views, a game's passes (each, and their sum), the lines, SMAA, the upscale, the loading
screen, and the UI (barriers are in the `vulkan` skill's `synchronization.md`). The render thread flushes the slot's
regions of the line buffer and of the mesh passes' draw, command, and light buffers when their memory is not coherent,
so the main thread makes no Vulkan call for them. After the fence wait it reads the slot's GPU timestamps from its last
frame (`GpuTimer.Collect`). The frame's `ViewConstants`, built on the main thread from the camera and the image size, go
to the passes as push constants. It recreates the swapchain when needed, by one set of rules with no Vulkan call
(`SwapchainState`): a resize to a size the swapchain was not made for, a VSync change, an out-of-date acquire or
present, or a suboptimal one once for each size; a resize repeating the size, or a suboptimal swapchain already made for
its size, makes none. The device's values (limits, memory, queue families, timestamp bits, name) are read in
`Initialize` or later, never in a constructor: they throw until `Initialize` has selected the device; its handles stay
null until then. Because the request comes right after the render thread's blocking point (present and acquire under
FIFO), frames are built at the display rate from freshly read input. When presentation blocks, for example while the
window is hidden, only the render thread waits: no request comes, and the main loop keeps running every fixed step. It
also sleeps instead of spinning when the window is minimised.

**Threads.** Besides those below: the navigation thread (the grid's fill, with a `Parallel.For`, and the fields' builds),
the edit warm-up's thread, the sound render thread, builds a game starts on the thread pool, the screenshot writer, and
the control port's accept and connection threads. Windowing, input, ECS, physics, the session (LiteNetLib's own thread
only moves packets and keeps connections alive; what arrives waits for the main thread's poll), and all mod callbacks run
on the main thread (GLFW requires it, macOS strictly). The render thread alone uses the Vulkan queues, the swapchain, and
the per-frame command pools; while it runs, the main thread makes no Vulkan call except flushing the staging region it
wrote (`vkFlushMappedMemoryRanges`, which has no externally synchronized parameter and does nothing on coherent memory).
The hand-off is synchronous: the render thread waits while the main thread builds a frame, then records, submits, and
presents while the main thread handles events and fixed steps. It does not draw frame N while frame N+1 is simulated;
recording is cheap and the GPU works asynchronously anyway, and this keeps latency low. Physics uses Bepu's own worker
threads inside the step (a quarter of the processors, 1 to 6 threads, so Bepu does not crowd out the voxel workers and
the render thread). Sound has a thread of its own (`AudioService`), which alone makes OpenAL calls once it runs; the main
thread hands it each iteration's sounds, music commands, and the listener through `AudioExchange` and `MusicExchange`,
and a music thread decodes the music ahead of it (`MusicStreamer`). Voxel columns are generated on their own
background threads (`VoxelWorkers`, half of the processors by default, below normal priority, which on Linux changes
nothing: every thread runs at the process's nice value); they hand finished columns to the main thread, which alone
changes the live voxel world. The same threads free the memory of unloaded columns, ahead of any generation, so unloading
costs the main thread a few microseconds per column.

**Shutdown.** A match that is on ends first: its session says goodbye (the peers are told this game has left), and its
steps run in reverse (loading, mods' `OnMatchEnded`, save sections (the last save captured), ECS, match UI, the world on
the GPU, tint, save (the named save written), voxel physics, navigation, whose thread is given 5 s, voxels with their workers stopped and their memory freed,
physics, then the session's socket). Then every run step whose initialisation started shuts down in reverse order: the
control port (each connection given 1 s), the render thread (stopped, then the device drained), the audio thread (the sound render thread and
the music thread stopped first, each given 2 s, then the audio thread, which frees its sources and buffers), mods (`OnGameExit`, then `OnModUnloaded` in reverse load order), game, UI, renderer, settings
(`settings.json` written), audio, shader compiler, input, GPU memory, frame sync, swapchain, Vulkan context, window,
Steam (the Steam API shut down), and network. If the render thread does not stop within 2 s, it is stuck inside a present
(the window is hidden); the window and every Vulkan object are then left to the OS, and the other steps still run. The
same holds for every other thread that writes memory the shutdown frees: the voxel memory is left to the OS when a column
worker or the edit warm-up does not stop within 2 s (`VoxelWorld.LeaveMemoryAtExit`), and the OpenAL device is left open
when the audio thread does not stop within 2 s (`IAudioDevice.LeaveOpen`); each logs a Warning. SIGINT (Ctrl+C) and
SIGTERM request this shutdown; a second signal more than 0.5 s after the first ends the process at once
(`ExitSignalPolicy`); when the first arrives while the loop is inside a mod's callback, a Warning names the mod and the
callback, since a mod that hangs never lets the loop see the request. Every `Dispose` is idempotent. Native libraries
loaded through Silk.NET are not unloaded; unloading them at exit crashed the process because their exit handlers ran after
the library was unmapped.

**Units.** Metres and seconds everywhere in simulation and physics. Voxel coordinates (`VoxelCoord`, edits, rays) are
absolute integers in centimetres from the map's middle. Positions kept in metres are local, from the match's origin
(`WorldOrigin`); points that cross it are absolute `WorldPoint`s (Units and numbers, below).

**Diagnostics.** Every run writes one log file, `<data folder in lower case>-<date>-<time>-<pid>.log`, under
`LocalApplicationData/<DataFolder>/Logs` (`~/.local/share/<DataFolder>/Logs` on Linux; the folder comes from
`Engine:Application:DataFolder`). The file receives every level the global minimum allows (Debug by default); the console
shows Information and above. Serilog sits behind `Microsoft.Extensions.Logging`, so engine and mod code only use
`ILogger`.

- The file starts with a session header: product version with git commit, build configuration, OS, architecture,
  runtime, processor count, memory, process id, GC mode, debugger state, environment, base directory, command line, log
  file path, and a few engine options: the window's size and VSync, the preferred device, validation, the mods' folder,
  the step rate and the most steps a frame, the console's level, the stats' interval, and the voxels' radius, budget,
  workers, seed, and walk speed (as the options give them; nothing from `settings.json`, which the settings step reads
  later).
- `GameLoop` logs the duration of every initialisation step, the reason the loop ended, each shutdown step, and a final
  line with uptime and exit code. A shutdown step that throws is logged and skipped so the remaining steps still run.
- `FrameStats` logs one line every 10 seconds: frames built, average and maximum time between frames (measured, not
  clamped) with the median, 95th, and 99th percentiles, fixed steps, dropped steps, managed bytes allocated on the main
  thread and on the render thread (in total and per frame), garbage collections per generation, heap size, and the
  average and longest GPU time per frame (timestamp queries; a Debug line gives each pass). Frames and steps are counted
  separately, so the line still appears while no frames are drawn. Dropped steps also produce a warning. The allocation
  numbers are the whole-loop check for the zero-allocation rule: in the steady state the main thread's should come only
  from log lines, and the render thread's should be 0. Each system's own zero-allocation test runs with tiered
  compilation off in the test projects, so every method is optimised at its first call and no test measures what
  unoptimised library code allocates; the suite takes about 4 % longer for it. A Debug line gives the fixed steps' CPU
  time by part and the eight slowest fixed systems (`SystemTimes`).
- `VoxelWorld` logs one line per stats interval: columns resident and pending, mixed chunks, stored bricks, memory used
  and reserved against the budget, columns loaded and unloaded, jobs that ran out of memory, average and maximum column
  job time, the longest `Update` split into install, rebuild, unload, and queue, and edits. These come from running counts,
  so the line costs nothing to build. Reaching the memory budget logs a Warning, at most one a minute; loading again logs
  Information.
- `WorldMeshes` logs one line per stats interval: the columns it follows, the blocks with meshes and those pending, the
  mesh jobs started, the meshes uploaded and the empty ones, the megabytes uploaded, the mesh heap's use against its size,
  the blocks and triangles drawn in the last frame and into each shadow cascade, the models followed and meshed and the
  model blocks drawn, resyncs, the lights shaded, and the main thread's time in the upload (average per frame and
  longest). `MeshPass` logs the heap's size, the draw limit, and the samples a pixel at start. A full heap logs a Warning,
  again only after it had room between, and a frame whose block list or model list is full logs one once a match.
  `tools/benchmark/parse.py` reads these lines, so they keep their form. A `Particles:` line follows while any are alive.
- `GameLoop.BuildFrame` logs a Warning once when a frame's models fill the 32,768 that `ModelInstances` holds; the rest
  are not drawn.
- When no frame was presented for more than 0.5 s (a hidden or minimised window), the render thread logs the gap at Debug
  once presenting resumes.
- Exceptions that escape initialisation or the loop are logged at Critical with the phase, frame, and tick, shutdown still
  runs, and the process exits with code 1. In a match the menu or the console started, the loop's exception ends only the
  match, at Error, and the run goes on at the menu. `UnhandledExceptionLogger` covers exceptions on other threads and
  unobserved task faults and flushes the file before the process dies. `Program.cs` uses a Serilog bootstrap logger so
  failures before the host exists are reported too.
- Retention runs at startup: files beyond the newest 20 or older than 30 days are deleted. Files roll at 50 MB. All of
  this is configurable under `Engine:Logging`; levels and per-category overrides live under `Serilog` in
  `appsettings.json` and can be set on the command line.
- LiteNetLib's internal log is routed through `NetLoggerAdapter`; Vulkan validation messages map to Error, Warning,
  Debug, and Trace; the mod load context logs every assembly resolution at Trace.
- Native crashes (a segmentation fault inside a driver or native library) cannot be caught by managed code. The file is
  flushed to disk every second, so at most the last second is lost.

## 5. How a game is composed on the engine

A game is an executable that registers the engine, then itself, and runs `GameLoop`. The starter, `src/Game`, shows how:

- `Program.cs` builds the host as in section 4, adds `AddIteraEngine(configuration)` and then `AddGame(configuration)`,
  turns on `ValidateScopes` and `ValidateOnBuild`, calls `GameServiceCollectionExtensions.ValidateOptions` (a game's
  option sections are read and checked before the window opens, and a key under `Game` that no section holds is named),
  installs the crash handlers, and returns `GameLoop.Run()` as the exit code.
- `AddIteraEngine` registers the engine's services with `TryAdd`, so a game's later registration of the same service
  type wins (the loading tasks and UI layers are lists a game adds to). The engine registers no ECS system. It also registers what systems share: `PlayArea`
  (round the match's grid with terrain, else `PlayArea.WithoutTerrain`, the bounds of a 10 cm grid, which a game whose
  meshes reach farther replaces), `PlayerInputBlock`, `WorldResets`, and `MovementKeys`; the voxel models (in the voxel
  world's memory with terrain, where lifted pieces lie, else their own), the loose bodies (which meet the terrain
  through its colliders, or the mesh colliders alone), the bodies as the mover's cells, and the character mover (over
  the terrain when there is one, the mesh colliders, and the loose bodies); and the match's identity (`IMatchIdentity`: the generator's id and version with
  voxel terrain, else the application's name at version 1; a game may register its own), which the hello, the welcome,
  and a lobby's match key carry.
- `AddIteraVoxels()` (`VoxelServiceCollectionExtensions`) adds the voxel terrain: each match's `VoxelWorld` and
  `MatchVoxels` (the match's terrain services together, `MatchServices.Terrain`), its `VoxelColliders`,
  `NavigationService`, `IBenchmarkHost`, and the World, Navigation, and Warm-up loading tasks, after the engine's Sounds
  and before the game's own. A game with terrain registers an
  `IChunkGenerator` and calls it. A game without it plays matches with no voxel world: no voxels, navigation, or voxel
  physics steps, nothing of a world on the GPU, a join payload and a save of the header alone (its match identity, seed,
  step rate, and match data), and the session, physics, ECS, UI, audio, particles, mesh assets, mods, the character
  mover over its mesh colliders, and loose bodies (balls and cut bodies, which a game's `ILooseBodyRest` decides about
  once they rest, as there is no grid to settle into) as usual, and `WorldMeshes` meshes and draws its voxel models
  alone.
- These have engine defaults that a game may replace: `IVoxelPalette` (grey), `IVoxelTintMap` (none),
  `IVoxelPassability` (nothing passable), `MenuStyle` (plain), `IMenu` (the engine's `MenuLayer`), and, for navigation, nothing (without an `INavGridSource`
  there is no navigation). A game adds an `IAtmosphereSource` for its sky, an `INavGridSource` for walkers,
  `ILoadingTask`s, `IMatchUiLayer`s for its HUD, `ISessionGameHandler`s for its messages, `IRenderPass`es for its own
  GPU work ("A game adds passes of its own", below), and console commands; its `IRunStartup` declares its models, its
  image views (`RenderViews`, "Image views", below), its keys, and its own on/off settings
  (`PlayerSettings.RegisterSwitch`, kept in `settings.json`).
- The starter's `AddGame` (`GameServiceCollectionExtensions`) registers its options (`Game:Player`: the body's height
  and whether to start flying or in third person; `Game:World:CameraPose`: where the player and the debug camera start;
  `Game:Minimap:Metres`: how much the minimap shows across), checked by validators and `UnknownOptionKeys`; its world
  (`GameGenerator`, `GamePalette`, `GameAtmosphere`, and a scoped `StartPose` from the match's seed, which the session
  settles first); `GameRunStartup`, an `IRunStartup` that makes the movement keys first so the Keybindings tab lists
  them before a game's own, then declares the fox and the minimap (its image view, its key, and its switch); the
  minimap's HUD layer; and its systems.
- Registration order is run order within a group, and the registration says why. The starter's two groups, in order, are
  in `GAME.md` ("Systems"): the engine's systems with its own (`PlayerSpawnSystem`, `FoxSystem`, `MinimapSystem`) among
  them. A game puts its own systems in this order where they belong: a system that reads the player's move goes after
  `PlayerMovementSystem`, one that reads the camera after `CameraExtractSystem`, and one that adds models goes after
  `LooseBodyModelSystem`, so loose bodies are never the ones left out of a full frame. `SystemOrderTests` in the game's
  tests pins the order.
- `appsettings.json` holds the engine's defaults for the game under `Engine:` (the name and data folder, the window, the
  renderer, the voxels, the session) and the game's own under `Game:`. The shipped file is checked by
  `ShippedSettingsTests`. `Engine:Steam:AppId` is 0 until the game has one; at 0 Steam stays off.
- The starter's world is `GameGenerator`, a `HeightfieldGenerator` (rolling hills of grass with gravel on the low ground,
  5 cm of surface over dirt to 1 m, then stone) on a 10 cm grid, with voxel objects of 5 cm voxels on it (`GameObjects`:
  boulders, broadleaf and young trees, bushes, and rocks; placed by `GameForests` from `JitteredGrid`s and a forest
  noise), with eight materials (`GameMaterials`), each with a look in `GamePalette` and a texture set under
  `src/Game/Textures/` (made by `tools/Textures`, copied next to the executable). `BenchmarkViews` gives the benchmark its views of that world; runs of
  different views do not compare.
- Mods load from `Mods/` next to the executable; the sample mod's build copies itself there.

A new game keeps `src/Game`, `Game.csproj`, and `tests/Game.Tests` as its home, so the tools need no setting, and grows
the generator, palette, systems, and options in place.

### Mesh assets are drawn beside the world

A mesh asset is a triangle mesh with texture coordinates and materials, read from a glTF 2.0 file at the run's start
(`Assets/`, SharpGLTF at load only) or built by a game, and drawn in the world's pass with the world's light.

- `MeshAssets` holds them for the run, by name and index; a game declares its files at its run startup. The run's step
  "mesh assets on the gpu", before the render thread, puts every asset's vertices, indices, materials, and textures (one
  image each, every mip) into device-local memory once (`MeshAssetPass`); nothing is loaded or freed during play. A
  texture is decoded at load (RGBA8 or RG8, its mips made on the CPU), or read from the model's compressed textures (BC7
  or BC5, a quarter of the size) that `tools/Textures` made offline from the same levels (`CompressedTextures`), when
  their hash matches the model's image.
- Frame systems add instances to `MeshInstances` (asset, transform, tint, `ModelShadow`; at most 16,384 a frame).
  `MeshAssetDraws` culls them by bounding sphere against the view and both cascades (the `MeshView` the world's lists
  share) and writes one indirect command per primitive with instances, in three lists and four pipeline ranges (opaque or
  masked, one-sided or two-sided). `MeshPass.Record` draws them inside its own renderings (`IMeshPassDraws`): into each
  cascade, and into the main view after the models, so they meet the world's depth, multisampling, and resolve.
- The vertex shader pulls its vertices from a storage buffer, as the world's does, and projects with `viewClip`. The
  fragment shader reads the material's textures from one array indexed per pixel (`nonuniformEXT`, needing descriptor
  indexing of sampled images) and ends in `mesh-shade.glsl`'s `shadeSurface`, the light and grade every surface of the
  pass takes. Blended materials draw after the sky, sorted back to front per object, casting no shadow (sorted per
  object, not order-independent, and without shadows, for the reasons in `docs/rendering-extension.md`); objects that
  cross, or one inside another, can show in the wrong order from some sides. Metalness and roughness are kept, not
  drawn.
- A skinned asset has a `Rig` (its glTF skin's joints, parents first, in metres, with its clips, `RigClip`), sampled on
  the main thread in floats for drawing only (the deterministic skeletons of `Animation/` are separate). A frame system
  poses an instance by writing its rig's skinning matrices (`MeshInstances.AddPosed`, `Rig.WritePalette`); before the
  cascades, a compute pass (`mesh-skin.comp`) skins every posed instance once into its frame slot's region of the same
  vertex buffer, and the camera's and both cascades' draws read that copy (`MeshAssetDraw.VertexDelta`), so skinning is
  done once a frame however many lists draw it. A posed instance is culled by its joints' posed places grown by how far
  their vertices reach. `MeshAnimator` and `MeshAnimatorSystem` keep clip clocks in game time, which stop while the
  match is paused.
- Cost, on the RX 5700 XT at 2560x1440 at the starter's views, offscreen with MSAA 4 unless said (Release, 2026-10-09):
  the starter's one fox adds 0.015 to 0.019 ms to the world pass where it is drawn (0.7 to 1.1 %), and nothing where
  it is not; 200 foxes (576 triangles each) in front of the `start` view add 0.20 ms standing and 0.28 ms running, of
  which the skinning pass takes 0.05 ms and the camera's draws 0.085 ms (the rest is the cascades), 0.25 ms with one
  sample and SMAA, 0.29 ms with eight samples, and 0.28 ms in a window. In views where they only cast shadows,
  200 running foxes add 0.13 to 0.16 ms.

### A game adds passes of its own

A game's GPU work is an `IRenderPass` it registers (`Rendering/RenderPasses.cs`; at most eight, named apart). The run's
"render passes" step, after the mesh assets reach the GPU and before the render thread, gives each a `RenderPassSetup`
for its stage's targets (formats, samples, the mesh pass's set with the look and the light, the allocator, the shader
compiler, and helpers for layouts, pipelines, and storage buffer sets); each frame the main thread asks it to write its
frame slot's data after the frame systems, and the render thread asks it to record at its stage: `Compute` (before the
shadow maps), `Opaque` (in the world's multisampled rendering, before the sky), `Transparent` (in it, after the sky and
the engine's blended surfaces), `Scene` (on the resolved, drawn-size image and depth, after the particles and lines), or
`Screen` (on the swapchain image after the upscale, before the UI). A game's shaders sit in `Shaders/Game/` beside the
engine's and include its `view.glsl`. Each pass is timed apart (the `gpu` reply, a Debug line), and their sum is the
`game passes` of the Debug line `GPU passes over ...`. Nothing records a game's passes while the loading screen covers
the picture, and image views do not draw them. The mesh sample's aim highlight is the example: a frame system finds the
block the player aims at, and its pass draws a glow round it at the `Transparent` stage. The stages chosen, the limit of
eight, and the other choices offered are in `docs/rendering-extension.md`.

### A camera's lens

The active camera's `CameraLens` is perspective (a vertical field of view from a near plane on) or orthographic (an
image a height in metres across, of parallel rays, between a near and a far along the view). An orthographic camera's
position is the middle of what it shows, and its near may lie behind it, so everything measured from a perspective
view's eye is measured from that middle: the fog, the terrain's levels, the lights and their clusters, and the two
shadow cascades, which then lie round it both ways along the view. The shaders take what differs from `view.glsl` (the
projection, the sky's and fog's rays, the cascade's depth); `MeshView` culls to the view's box and sorts by depth. A
perspective view draws from its lens's near plane (5 cm for the engine's cameras). The console's `lens ortho HEIGHT`
shows any view orthographically for a run.

### Image views

A game draws views of the world into images of their own (`RenderViews`): it declares them at its run's start (at most
four, each a size and how often it is drawn), asks for one with a camera each frame it wants it, and shows one on the
HUD in a rectangle. Each view keeps its own lists and records of a frame and draws with the mesh passes' pipelines and
shaders through sets that name them, with one sample a pixel, without shadows or particles, after the world's pass; the
terrain's levels stay the main camera's, and the world's, the models', and the mesh assets' lists take what each view's
own frustum holds. Its colour is copied into an image only shaders read, which the UI pass draws under the HUD's pixels,
and a mesh material may show it (one slot of the asset pass's texture array for each view; the mesh sample's screen
shows a camera over its yard). Nothing draws a view while the loading screen covers the picture. The starter's minimap
is the example: an orthographic camera at the player's eye looking down, 64 m across, every fourth frame, in the
top-right corner; an update cost 0.74 ms of GPU time on the RX 5700 XT (1.59 ms at the world's 4x samples, which is why
views draw with one). With no view drawn (the benchmark hides the minimap), the world pass took the same time as before
image views on every benchmark view (-0.4 % to +0.0 %, two offscreen runs each at 2560x1440, 2026-10-10). Why views have
no shadows or particles, draw at one sample, share the camera's terrain, and number at most four:
`docs/rendering-extension.md`.

## 6. Modding

A mod is a folder under `Mods/` next to the executable:

```
Mods/
  MyMod/
    mod.json
    MyMod.dll
    MyMod.deps.json      optional, generated by the SDK; lets the mod ship private dependencies
```

`mod.json`:

```json
{
  "id": "my.mod",
  "name": "My Mod",
  "version": "1.0.0",
  "entryAssembly": "MyMod.dll",
  "entryType": "MyMod.Entry",
  "apiVersion": 2
}
```

`id` is lower-case letters, digits, `.`, `_`, `-`. `entryType` is optional when the assembly has exactly one public
`IGameMod`. `apiVersion` must equal `ModdingApi.Version` (2). Mods are loaded in ordinal folder-name order.
`dependencies` is reserved and not yet enforced.

**Matches.** Mods load once per run, after the run's services exist and before any match (`OnModLoaded` gets the run's
services; a match's, such as the world or the ECS world, are Scoped and cannot be had from them). A run may play many
matches (section 8): each start calls `OnMatchStarted` with the match's services, once its world and systems exist, and
each end `OnMatchEnded`, before they are freed, where a mod drops what it holds of them. `OnFixedUpdate` and `OnUpdate`
run only during a match. Both are default interface methods, so a mod without them needs no change but its `apiVersion`.

**Isolation.** Each mod gets its own collectible `AssemblyLoadContext`. Assemblies that the host already has (framework,
`Itera.Modding`, `Itera.Engine`, every package in the game's output folder) resolve to the host's copies, so `IGameMod` is
one type for everyone. Anything else resolves from the mod folder through its `deps.json`. A mod's project should
reference `Itera.Modding` with `Private="false"` so it does not ship its own copy.

**Errors.** A mod that throws in any callback is marked faulted, the exception is logged, and it receives no further
`OnUpdate`, `OnFixedUpdate`, or `OnMatchStarted`. It still receives `OnMatchEnded`, `OnGameExit`, and `OnModUnloaded`. A
mod that fails to load is skipped; one whose `OnModLoaded` throws is removed and its load context unloaded at once, with
no `OnModUnloaded`. The loader never throws into the game loop.

**Timing.** Each update call is timed (`ModDispatcher`): a mod's `OnFixedUpdate` joins the fixed systems' times as "Mod ID",
so a slow mod shows in the stats' slowest-systems line, and a callback over 50 ms that is the mod's longest yet logs a
Warning with its name. The call in progress is kept for the shutdown's signal handler.

**Unloading.** `ModLoader.Unload(id)` calls `OnModUnloaded`, drops the load context, and forces a few collections to check
whether the mod was really released. `LastUnloadWasCollected` reports the result and a warning is logged when it is
false. Unload is best effort: a mod that subscribes to a static event, starts a thread, or defines ECS component types
(Arch keeps a static registry) will stay in memory until the process exits. Reload is a developer convenience; restart is
the reliable path.

**Safety.** .NET has no sandbox. Mods run with the game's full privileges. Mods are trusted code.

**What mods can do today.** Receive lifecycle callbacks, log, and resolve any engine service from `IModContext.Services`.
A narrower, versioned API surface for mods is open work. In a session only the host edits the world: a mod checks
`SessionService.IsAuthority` before calling `VoxelWorld.Apply` or `TryLift`, which throw on a client; the host sends a
mod's edits to the clients as it does a game's.

## 7. The engine's components and systems

`src/Itera.Engine/Components/` and `src/Itera.Engine/Systems/` hold what most games on a voxel world need. The engine
registers no system: a game registers those it wants, in run order, in its composition (section 5). All run on the main
thread. Fixed systems receive `FixedStepTime` (step length and tick), frame systems `FrameTime` (delta, total, frame index,
and `Alpha`, how far the frame lies between the last two steps). Components are plain structs with public fields.

**Components.**

- Cameras: `CameraPose` (position, yaw, pitch), `CameraLens` (perspective or orthographic), `FlyCamera`, and the tag
  `ActiveCamera` (exactly one entity has it).
- Characters and players: the tags `LocalPlayer` (the player this machine's input controls), `RemotePlayer` (a peer's
  player, drawn from the states it sends), and `Bodiless` (no body for now: it does not move, push, or take a stand-in);
  `PlayerCommand` (movement, look, `PlayerButtons`, and `Actions`, the game's own button bits, which the engine never
  reads); `PlayerMode` (`Flying`, `Crouching`); `ThirdPersonView`; `CharacterSize` (height, radius, eye height, step
  height, and mass from `ForHeight`); `CharacterPosition` (now and after the previous step, so frames draw between them);
  `CharacterVelocity`; `CharacterContact`; `EyeSmoothing`; and `MovementStats` (speeds, jump height, accelerations, and the
  steepest slope, which a game may write each step from its own rules; `Default` is a person's).
- Physics: `LooseBody` (the Bepu body's handle and birth stamp), `BodyPose`, `BodyBox`, `CutPiece`, `RemoteBody`, and
  `CharacterStandIn`.
- Mesh assets: `MeshPlacement` (the pose at the last two steps and a scale), `DrawnMesh` (the asset's index, a tint, a
  `ModelShadow`), `MeshAnimator` (a clip, its time at the last two steps, a speed, the clip it fades from, a clip
  blended in with its weight and shift, and a count of its changes), `SmoothedPose` (a mesh whose changes are smoothed as
  it is drawn), and the tag `ConsoleMesh` (placed by
  the console's `meshes`).
- Networking: `NetEntity`, the stable id a host gives an entity it sends. Entities are local to each game; this id is what
  every game shares.

**The player and the cameras.**

- `PlayerInputSystem` (fixed) builds the local player's `PlayerCommand` from the step's input by the movement keybindings
  (`MovementKeys`, registered once a run in the order the Keybindings tab lists them: Forward, Back, Left, Right, Jump,
  Sprint, Crouch, Fly down, Third person), and the look from the mouse while the pointer is captured. The console's `fly`
  and `camera` are its commands. `PlayerInputBlock.Blocked` makes the input read as nothing pressed, for a screen that takes
  the player out of play.
- `PlayerMovementSystem` (fixed) moves every entity with a command and `MovementStats` through the voxels with
  `VoxelCharacterMover`: steering faster on walkable ground than in the air, jumping, gravity, sliding on steep ground,
  pushes from loose bodies, crouching (each character crouches while Crouch is held, to 0.77 of its height; it stands
  once the standing body fits), and backwards walking at 0.7 of its speed, the same for the local player, a remote one
  a host moves, and a game's characters; one character's step is also `CharacterStepper.Step` (scoped), to run a step
  again (a client's replay after the host's correction). The eye eases after a step or a change of eye
  height (`EyeSmoothing`). A game's `IMovementMode` may take a walking player's step first (a climb), giving the
  velocity, step height, and whether it jumped, and seeing the command both as the crouch limits it and as asked, with `MovementProbes` (a wall ahead by three rays of the shared ray, its ledge's top by a
  ray down, room to stand by the mover) and the move's `WallNormal`; `ClimbingMode` is the engine's ready-made one
  (grab, climb, hang, jump off, let go, pull up), which the starter registers for its boulders. `pos` and `tp` are its
  commands.
- `PlayerCameraSystem` (frame) puts the active camera at the eye between the last two steps, looking where the command
  looks plus the mouse since the step, or behind the right shoulder (`ThirdPersonCamera`, pulled in by the voxels); it
  makes the camera entity (`CameraRules.CreateCamera`). `DebugFlyCameraSystem` flies it while the player flies, kept to the
  `PlayArea` (the world and 1 km round it). `CameraExtractSystem` hands the camera to the renderer. `AtmosphereSystem` sets
  the frame's sky, sun, haze, and grade, and its look, from a game's `IAtmosphereSource` at the camera.
- `ToolAim` gives where the local player aims from and along, and the first voxel a tool's ray meets past passable voxels.

**The world and bodies.**

- `StreamingCentreSystem` (fixed, last) centres voxel streaming on the local player's eye (or the active camera while
  flying), on a host round each remote player (a 4 m level-0 disc), and offline or on a host round each loose body away
  from every player (a 3 m disc stretched ahead along its velocity, at most 32).
- `MapResetSystem` (fixed, first) runs the console's `cleanup` (every loose body and particle) and `reset` (the world back
  to the seed's, counted in `WorldResets` for a game's systems to put their own things back).
- `PhysicsReadSystem` reads each loose body's pose and each stand-in's push after a physics step. `StandInSystem` gives a
  character near a thrown body a stand-in. `LooseBodySystem` puts bodies that rested 3 s back into the grid and removes
  cut bodies. `LooseBodyRoom` makes room when the body limit is reached. `LooseBodyModelSystem` hands the bodies' models to
  the renderer. `DebugBallSystem` is the console's `balls`. `DebugOutlineSystem` draws bodies' boxes and stand-ins with the
  debug lines. `TriggerCharacterSystem` (fixed, after the stand-ins) tests the characters with a `TriggerSensor` against
  the match's trigger zones and writes `CharacterTriggers`.
- `MeshDrawSystem` (frame) hands every placed mesh asset to the renderer, its pose blended by `Alpha`, a posed one with
  its clip's skinning matrices (blended between two clips while one fades into the other), a smoothed one's changes
  carried from the pose drawn before (`PoseSmoothing`: dead blending, drawn only); `meshes` is its command
  (list, place, test, clear, with clips). `MeshAnimatorSystem` (fixed) moves the clips' times and fades on.

**The benchmark.** `BenchmarkSystem` (frame, after the fly camera; a game registers it when `Engine:Benchmark:Enabled`, with
its own list of `BenchmarkView`s) puts the camera at each view of the seed's world in turn, waits until the world is
loaded and on the GPU (`IBenchmarkHost`, `EngineBenchmarkHost`), waits `SettleSeconds`, logs the start and end of a
measurement of `MeasureSeconds`, flies a circle for `FlightSeconds`, and asks the game to exit. `tools/benchmark/` runs it
and compares runs from the log lines, which keep their form.

## 8. Design directions

### Units and numbers

- **Metres and seconds** are the simulation units. Voxel coordinates are absolute integers in centimetres from the
  map's middle (`VoxelCoord`): voxels, edits, saves, and what peers send.
- **Two frames for positions.** What the simulation and the renderer keep in metres (`Vector3`: the ECS components, Bepu
  bodies and statics, rays, the mover's feet, the camera) is local: measured from the match's origin (`WorldOrigin`, a
  scoped service every match service shares, always on whole metres). A point that crosses the origin (a start pose,
  the console's `tp` and `pos`, a benchmark view, the play area, a call into a generator or an atmosphere source, a
  solid model's pose, the tint map's read) is an absolute `WorldPoint`, three doubles in metres, which hold a
  centimetre exactly past the edit format's 167 km. The two meet only through `WorldOrigin`, in doubles: a local float
  becomes absolute as origin + local (exact, since the origin is whole metres), and centimetres or cells only as
  floor((local + origin) * scale); a centimetre written in metres reads back as itself (`WorldPoint.ToCentimetres`
  counts a point within 10 nm under a centimetre's edge as on it). So the voxels a move, a ray, a collider, or a
  settling ball reads 150 km out are those it reads at the middle, bit for bit (`FarFromTheMiddleTests`, on a world
  moved there, `TranslatedGenerator`), the origin there. `VoxelCoord.FromMetres` and `ToMetres` went for that reason.
  Mesh colliders keep an absolute anchor each (whole metres) with their triangles from it; the mover's cells, the voxel
  colliders' statics, and the loose bodies' pivots are worked out in absolute doubles and posed locally. A match's
  first origin is where its game starts it (`IOriginStart`, the starter's start eye); then `OriginShiftSystem`, first
  in a game's fixed systems, moves it to the camera's whole metres once the camera is `Engine:Origin:ShiftMetres` (512)
  out on any axis, so a local float near the camera rounds by at most about 30 micrometres, and a far `tp` moves it to
  the jump's end first. A move (`OriginShifts.ShiftTo`, to whole metres) shifts every body and static in Bepu, the engine's
  components that keep positions (current and previous together, so the blend between steps holds), the world's
  streaming centres, retired models, light flashes, and queued particles move the other way, then the game's
  `IOriginShiftHandler`s; it happens between steps and frames or in the first fixed system's update, so a step sees
  one origin.
  A walk with the origin following the player every 100 steps matches one without within a millimetre over 45 m (the
  one without rounds its feet to floats 30 to 40 m out, losing about a micrometre a step), and balls at rest settle
  into the same voxels. Moving it with 384 balls and their voxel statics took 0.6 ms in a Debug test on a busy machine
  (12 ms the first time, mostly compiling). The renderer draws everything local to it and takes it back only for what
  is drawn by place (the surfaces' noise, strata, tint, and texture tiles), choosing and ordering the world's columns
  from the view's absolute place and snapping the shadow maps' texels in absolute terms; the particles' GPU pool and
  the sounds playing move with it (`.claude/rules/rendering.md`, "Under a moved origin"). Pictures of the starter's
  start, far, high, and down views before and after the console's `origin move 300 0 -200` differed in 7 to 15 of
  921,600 pixels, each on a silhouette's edge (2026-10-10); resting debris kept its place. On the GPU the whole item
  (the grid's size through sparse columns) costs 0.4 to 1.0 % of the world pass: the benchmark at 256 m and four
  levels, offscreen at 2560x1440 on a quiet machine, two runs each in turn, took 1.736 ms at the start against 1.724
  before it, 2.366 against 2.346 at `high`, and 2.639 against 2.613 in the flight (the shadow maps the same), from the
  shaders' origin and texture tile work (2026-10-10). A flight of 2.8 km at
  500 m/s moved the origin 5 times and ended within a centimetre of where its 336 frames should take it; a walk with the
  origin following every 8 m matched one where it stayed within a millimetre. Peers keep their own origins, so a
  game's message carries a position absolute (`MessageWriter.WritePosition`: 1/64 cm to ±335 km); a host steps the
  bodies near a far client in its own origin's floats (1 mm at 10 km, 8 mm at 100 km).
- **Positions in metres use `System.Numerics`** (`Vector3`, `Matrix4x4`, `Quaternion`), which Bepu takes directly.
  `Silk.NET.Maths` stays at the edges of Silk.NET APIs (window and swapchain sizes).
- **Anything every peer must compute alike** uses `DeterministicMath` (below), never `System.Random` or platform maths.

### Voxel storage

The world is a sparse hierarchy, not a dense grid. A dense 10 cm grid over the world is about 1.5 x 10^11 voxels; at 1 cm
it was 4 x 10^13 (5 TB at one bit each).

- A chunk is 128^3 voxels held as a 64-tree: a root node of 4 x 4 x 4 cells of 32 voxels, each a node of 4 x 4 x 4 bricks
  of 8 voxels, each brick 8 x 8 x 8 voxels (at a 10 cm base a level-0 chunk is 12.8 m, its cells 3.2 m, and its bricks 80
  cm). A node keeps a 64-bit mask of children that are not air, a mask of children that are one material, and a packed
  child list read by rank. A brick, node, or chunk that is all air or all one material collapses into its parent, so
  homogeneous regions cost one list entry.
- A brick is a 64-byte occupancy mask plus a 12-byte record, and, when it holds more than one solid material, a block with
  a 16-byte palette and 1, 2, or 4 index bits per voxel, or one byte per voxel above 16 materials. There are 256
  materials; 0 is air. A brick of air and one material takes 76 bytes.
- Each world voxel also has a density (`VoxelDensity`): how much of its cube is solid, 0 to 255, a solid voxel's 128 to 255
  and an air voxel's 0 to 127, so everything that reads materials sees the same world and the density only places the
  drawn surface within a voxel. A brick keeps a block of one byte per voxel only when some voxel's density is not its
  material's default (air empty, solid full). The generator gives the ground's density from its height; edits change
  voxels by the share they cover. Models keep only materials, except an object kind's, which keep densities too.
- The tree was compared with deeper 64-trees, OpenVDB, NanoVDB, and sparse voxel DAGs and kept
  (`docs/voxel-structures.md`). The chunk tree is already a 64-tree over bricks the size of VDB's leaves. NanoVDB's
  fixed-size nodes would take 1.5 to 5.5 times the memory, its tree cannot change after building, and its GPU build tools
  need CUDA. OpenVDB is C++ with no .NET binding.
- Storage is native memory (`VoxelMemory`): fixed-size block pools in 1 MB slabs, capped by
  `Engine:Voxels:MemoryBudgetMegabytes` (1024 by default). Edits may pass the cap. At the cap, streaming stops loading and
  resumes once 5 % of the budget has been freed. Used memory at the cap is often well below the budget, because each pool
  takes whole slabs, so the resume level counts from where use stood when the cap was reached, and that use is kept as the
  practical capacity. Only the first Warning in any minute is logged at Warning; the stats line counts jobs that ran out of
  memory. Each thread keeps up to 64 free blocks of each pool and takes or gives back 32 at a time under the pool's lock.
- The world's grid is a value each world owns (`VoxelGrid`, from its generator): the base voxel edge in centimetres, the
  bounds in whole level-0 chunks, and the conversions between centimetres and voxel indices (`GridLevel` for a level). A
  game sizes its world (`VoxelGrid.Sized(base, halfWidthMetres, floorMetres, topMetres)`, each bound rounded out to
  whole chunks, or the constructor in chunks): at most ±160 km on any axis, up and down too (`MostReachCentimetres`,
  inside the edit format's 2^24 cm with room for the play area's margin). A column stores a window of its layers (the
  generator's range, `IChunkGenerator.DescribeRange`, and the layers its edits reach), with one fill below it and one
  above it; its mesh column keeps blocks (about 4 KB a layer) only for the runs of layers that can hold a surface
  (`VoxelWorld.CollectSurfaceLayers`: a layer of more than one fill, or one whose fill differs from the layer above
  it or from the same layer of the column past its +x or +z side or corner, whose faces with it its blocks own, and a
  layer each side). Measured 2026-10-10: 64 columns of the hills test generator stored 111 layers in a world 12
  layers tall and in one 25,000 layers tall; a ball set 100 km up widened its column to 7,813 stored layers (62 KB of
  chunk state) and two runs that can hold a surface; a ball 60 m up over 1 cm voxels left blocks for 11 of the 45
  layers the column stores; a ray 100 km down took 0.6 ms in Debug. The grid
  is part of what the generator's id and version stand for, so peers and saves agree on it without carrying it, and a
  change of size raises the version. A grid of a base alone has the default bounds: whole chunks covering ±500 m, the
  floor at the first chunk at or below -40.96 m, and the top at whole chunks above 81.92 m rounded up to a multiple of
  8; at a 10 cm base, ±512 m, -51.2 m, and +102.4 m. Columns and generation jobs hold as many layers as their world's
  grid. Level-0 voxels may be 1 cm to 1 m (`VoxelGrid.MostBaseCentimetres`): coarse voxels suit terrain a game deforms
  in large strokes (craters, a heightmap's ground) for little memory, since the engine already handles voxels that large
  at its coarse levels; a shape under about a voxel shows only as a soft dip in the voxels' densities (and one under
  about a tenth of a voxel, which meets none of the 10 sub-rows an edit measures a voxel on each way, changes nothing),
  walls and caves come in voxel steps, and a piece lifted from the ground, read a centimetre at a time, keeps the
  voxels' steps. The limit is one of storage and streaming: positions in metres are floats from the match's origin, which
  follows the camera, so they keep their precision anywhere on the map (a host's bodies near a far client excepted),
  and navigation's window follows the players over any map. Code where the world and a model meet uses the
  grid's and the model's units, never a level's 2^k.
- A column (the chunks at one x, z) is the unit of streaming. Level k (voxels of `Edge(k)` cm, chunks of 128 of them)
  loads the columns whose nearest point is within the level-0 streaming radius * 2^k of a streaming centre
  (`Engine:Voxels:StreamingRadiusMetres`; 0, the default, takes it from the grid: 59.2 m on voxels of 10 cm and finer,
  4.625 level-0 columns on coarser ones, 592 m at 1 m) and unloads those beyond that plus one level-k column. The
  distance is to the column's nearest point, so a disc smaller than a column (a body's 3 m disc in a 12.8 m column)
  still loads the columns it meets. Every level holds a full disc, so the renderer can use a coarser level where a finer
  column is still loading. `Engine:Voxels:Levels` (4 by default: levels of 10 to 80 cm; 1 to 10) sets how many, and
  `Engine:Renderer:ViewDistanceMetres` (256 m; up to 8 km) ends the view sooner. Each level draws to a share of its voxel size
  (`Level0ReachMetres` and `ReachGrowth`, `LevelDistances`): 10 cm voxels to 16.8 m, 20 cm to 33.6 m, 40 cm to 67.2 m,
  80 cm to the view.
- Views to 8 km (measured 2026-10-10 on the starter's 8 km map, Release, offscreen at 2560x1440, seed 1, after the
  world loaded; at the start and at `distant`, 3.5 km out): with 7 to 10 levels and views of 2, 4, and 8 km, 542 to 672
  columns were resident (140 to 155 MB of voxels), the mesh heap held 209 to 276 MB of its 512 (nothing freed), the
  camera drew 708 to 1,243 blocks (1.3 to 2.4 million triangles; the list takes 32,768) and the cascades 248 and 563 at
  the start, the main thread spent 0.5 to 2 ms a frame on the meshes, and the world loaded in 2 to 3 s (once 5.3 s;
  a busy machine). Nine levels reach 17 km, so an 8 km view draws its last kilometres from level 8 (25.6 m voxels). The
  world pass's GPU time (RX 5700 XT, the benchmark offscreen at 2560x1440 on a quiet machine): at the start 1.81 ms
  with a 2 km view at any of 7 to 10 levels, 1.82 to 1.87 ms with 4 or 8 km at 8 to 10 levels, and 1.93 ms with 7
  levels at 4 or 8 km (their farthest level, 6.4 m voxels, is finer, so more triangles: 2.06 million against 1.59); at
  `high`, 40 m up, 2.91 to 3.07 ms (2.37 with the engine's 256 m and four levels: more ground is drawn where there was
  sky); at `distant` 1.92 to 2.06 ms; the shadow maps 0.08 to 0.18 ms throughout. The starter's 8 km at nine levels:
  1.85 ms at the start, 3.00 at `high`, 1.98 at `distant` (two runs within 0.2 %), against 1.74, 2.37, and 1.76 at
  256 m and four levels. Two faults showed only past 7 levels and were fixed: the level
  balance marked every finest column under each drawn column (262,144 for one level-9 column; 4.2 s a frame at 9
  levels), and a coarse column with a child past the view's end or the map's edge never split, so a 2 km view at 10
  levels drew the start from 51.2 m voxels.
- Load order: first the coarsest level's columns, nearest first (that level is drawn to the view distance, so a missing
  column is a hole); then columns the view draws now, nearest first in their own level, coarse before fine at equal
  distance; then the ring beyond reach, by metres beyond reach. Each level keeps its own sorted list, rebuilt when a centre
  has moved half of that level's column. Flying faster than loading would otherwise shrink every level to a small disc
  around the camera, leaving holes in the far view.
- Only a peer's own view needs full detail. On the host a remote player's centre needs the level-0 columns right around it
  (a 4 m disc), and edits reach the log whether or not a column is loaded, so remote centres get a smaller radius and no
  coarse levels (`StreamingCentre`, `VoxelWorld.SetStreamingCentres`).
- Chunks carry a level in their keys, the generator, and edits. An edit reaches the resident columns of every level, and a
  voxel is tested at its level's sample point (`VoxelGrid.SampleAt`).

### The world is its seed plus its edit log

A chunk that is not resident is not stored anywhere: a column is rebuilt by generating it from the seed and replaying, in
order, the edits that reach it (`EditLog` indexes them by column at every streamed level). Saves and late joiners receive
the seed, the generator id and version, and the edit log (`VoxelWorld.SaveEdits` and `LoadEdits`); there is no chunk file
format. A save loads only with the same generator version.

- Edits (`VoxelEdit`: sphere, box, or capsule; set, clear, paint, fill air, or lift; a box cleared of some materials; or a
  piece put back) use integer geometry and integer tests, so every peer gets the same voxels. A sphere, capsule, or box
  clear, fill, or set changes each voxel by the share of it the shape covers, measured on up to 10 x 10 sub-rows along y
  and z, each to the centimetre along x, so a dent smaller than a voxel lowers its density a little and
  `CountRemoved` counts exactly what was taken, in cubic centimetres. A clear takes that share of what is solid in the
  voxel, so one repeated on a spot keeps taking.
- A lift clears voxels as a dig does and keeps the voxels it removed as a piece named by its sequence (`VoxelPiece`, one
  chunk tree of at most 128 voxels a side in the voxel memory). The log keeps the pieces; save formats write them as runs
  of one material. A piece edit puts a piece back into air only, its pivot at a voxel corner and turned by a quaternion
  rounded to 16-bit integers; each level's sample point is turned back into the piece with an integer rotation matrix, so
  every machine gets the same voxels.
- A reset (`VoxelEditOperation.Reset`, the console's `reset`) is an edit too: an empty box that covers no voxels, after
  which no earlier edit counts. `EditLog` clears its column index at it, `VoxelWorld` unloads every column to generate
  again, and the log keeps the earlier edits so sequences go on and every reader follows it in place: a session's clients
  get it as an edit, saves and join payloads carry it, and the loop covers the picture with the loading screen again when
  `VoxelWorld.LastReset` moves. Code that reads the edit log must handle it.
- Edit log formats: the save is the log stream with the match settings (the format is 8, whose generator id carries the
  voxel object kinds' fingerprint where the world places objects, and which ends with a game's sections, none in a join
  payload; formats 1 to 7 still read, those before 7 with their plain generator id taken for such a world too, and an
  older build refuses a newer format). A save holds only the edits after the last reset, renumbered from 1; a join payload
  keeps every edit. The log indexes at most 2^24 column entries since its last reset (`EditLog.MaxIndexEntries`, counted
  over all ten levels whatever a game streams, so a host, its clients, and a save's next load agree).
- Compaction (`docs/edit-log-compaction.md`): a long log is small (50,000 edits make a 2.5 MB payload) but replaying edits
  into a column costs. `VoxelEditing.ApplyToRegion` fills each row's span of a shape at once instead of testing each voxel,
  16 times faster at level 0 with the same bytes. Snapshots of edited chunks, which would let old edits and pieces put back
  leave memory, stay open.

### Voxel columns are generated on worker threads

`VoxelWorkers` generate columns and free them after unloading; the main thread installs them, applies edits, and alone
changes the live world. Every change is recorded in `VoxelChangeLog` (column loaded, column unloaded, brick cell changed,
chunk changed with a mask of its 64 blocks of 32 voxels), which each consumer (the world's meshes, physics' collidables)
reads at its own pace through a `VoxelChangeCursor`. The change list is a ring of 65,536 entries; a reader that falls a full
ring behind has lost changes and reloads everything it holds (a resync).

Large edits apply over fixed steps. An edit whose box covers more than 128^3 level-0 voxels (`VoxelWorld.DeferVoxels`) is
logged, sent, and saved at once, but only marks the resident columns it reaches (`Column.EditsPending`);
`ApplyPendingEdits`, once a fixed step after the ECS fixed group, applies each such column all its waiting edits in order,
level 0 first and nearest a streaming centre first, `Engine:Voxels:EditColumnsPerStep` (4) columns a step. It is a count
and not a time, so a slow machine keeps the same pace as its host. A smaller edit applies at once, after the waiting edits
of any column it reaches, so each column takes edits in the log's order. Navigation reads edits only up to
`AppliedEditCount`; other readers (raycasts, the mover) may see a large edit's old voxels for a few steps.
`StartEditWarmUp` (from the voxels' start-up step, once a run) applies shapes to throwaway chunks on a thread of its own,
round after round, so that .NET compiles the edit code again, optimised; .NET counts calls only once no new code has been
compiled for a moment, which the game's start-up puts off, so the warm-up goes on until two rounds in a row take under half
the first-pass time, eight rounds in a row have taken within a tenth of the best round and that is under three quarters of
the first second's best (composite ReadyToRun code, about 42 ms a round precompiled, about 90 while .NET measures it, then
28 for good), nothing in the process has been compiled for 0.75 s (`JitInfo.GetCompiledMethodCount`: the code was
optimised by the second round), or 10 s have passed (`EditWarmUpStop`). In a game's Steam build the process went on
compiling a few methods a second after the edit code had settled, so the quiet came only at about 8 s and the loading
screen waited that long; the steady rounds stop it at 3.8 s (a build from `dotnet build` stops at 1.6 s, its rounds
halving; 2026-10-15).

### World generation toolkit

World generation is seeded and deterministic so every peer builds the same map from a seed the host chooses. A generator
(`IChunkGenerator`) answers `DescribeColumn` (which layers of a column are air or one material, exactly) and `Fill` (a
chunk's materials and densities), and is called from several worker threads at once. Use integer hashing of the seed and
coordinates, never `System.Random`, and never iterate hash sets or dictionaries in generation order. Use basic
floating-point operations only (+, -, *, /, sqrt): `Math.Sin`, `Pow`, `Exp` and similar come from each platform's maths
library and can differ in the last bit. No `System.Numerics` vector helpers either: in .NET 10 `Vector2.Lerp`, `Reflect`,
and `Transform` use `MultiplyAddEstimate`, which may fuse the multiply and add on one machine and not on another. A
generator's `Version` rises whenever the same seed would give other voxels; sessions and saves refuse a different one.

The engine's toolkit is in `Voxels/Generation/`:

- `HeightfieldGenerator`, a base for ground. A subclass gives the height at a point (`HeightAt`, centimetres), the surface
  material (`SurfaceAt`), the layers below it (`GroundLayers`: a surface depth, bands by depth, then the deep material),
  and features written over the ground (`CreateFeatures`: a per-thread `GroundFeatures` set of `IGroundFeature`s, each
  saying whether it touches a box and how it fills a chunk). The base keeps a per-thread cache of the column's heights
  (its 128 x 128 points and a ring of one around them, the surface materials, and the least and most each row reaches)
  and builds it again only when asked for another column, level, or seed. It gives exact descriptions, fills a row that
  lies wholly in air or in the deep material in one call, allocates nothing after a thread's first column, and gives the
  ground's voxels their density from the height (`GroundDensity`: the share of each voxel below the ground, fitted to the
  material it already gives). A voxel a feature made solid or air takes its material's default density
  (`VoxelDensity.Agree`), so a feature's voxels are whole. A layer is described as air or one material only where every
  voxel is wholly so.
- Shapes with exact row spans (the range of x a row of sample points covers, in + - * / and sqrt, so every level and
  every machine fills the same voxels): `Capsule`, `Ellipsoid` (an integer fill), `Prism` with `PrismBuilder` (convex
  solids of `ShapePlane`s: boxes, frustums, pyramids, pushed outlines), `CutEllipsoid` (a leaning ellipsoid cut by
  planes), and `VoxelRows` (filling and replacing a row's range at a level).
- Noise and placement: `FractalNoise` (octaves of gradient noise, each with its own cell size and amplitude, and
  `SoftClip`), `JitteredGrid` (one candidate point a cell, chosen and jittered by a hash, found for a box),
  `ScalarFieldGrid` (any field of x and z worked out on a grid fixed to the world and read bilinear, one per worker),
  `SeedCache<T>` (something built once per seed, such as a layout, read without a lock), and `FieldTintMap` (a tint map from
  a field, so materials shift between their colour and tint colour across the world close to the generator's own choices).

The starter's `GameGenerator` is a `HeightfieldGenerator` that uses `FractalNoise`: a whole world of hills in about a
hundred lines; its placer (`GameForests`) scatters boulders, trees, bushes, and rocks with `JitteredGrid`s.

### Deterministic maths and rotations

`DeterministicMath` (`Maths/`) gives the same bits on every machine: SplitMix integer hashing, seeded draws, gradient noise,
exact integer roots, and `Turns` and `CosSin` from their series, using integer operations and scalar double `+ - * /` and
`Math.Sqrt` only. `Rotation` is a 3x3 matrix of doubles built from turns through `CosSin` and combined with scalar
operations only, with `RigidTransform` (a rotation and a position); a body turned the same way fills the same voxels on
every machine, and poses blend through quaternions. Their pinned hashes (`DeterministicMathTests`) guard every generator
and body built on them.

### Poses and foot IK

`Animation/` poses jointed bodies of voxel parts; no engine system uses it, so a game drives it. A `Skeleton` is up to 32
bones with a parent, a rest turn, a joint axis, a share of the look, and its legs (`LegChain`: hip, knee, ankle, foot).
A `BodyPose` is a turn per bone, so one pose fits every skeleton of a plan whatever its lengths, mirrored and blended. An
`AnimationClip` is keyframes written in C# (poses and times, no asset files) that add onto the pose under it, so a
landing's dip keeps a run's arm swing. `Aim` spreads a look at a point over the skeleton's look bones. `LimbPose` turns a
bone towards a direction and reaches two bones to a point. `FootPlacement` puts any number of feet on the voxel ground
(probing the ground under each foot through `VoxelCharacterMover`), moves each ankle by the ground under it, lowers the
root by what a planted foot must reach down, bends each leg by two-bone IK, and tilts the feet to the slope; it leaves a
pose as it is on flat ground. `SegmentPlacement` lays a legless body along the ground, `SupportPoints` gives a part's lowest
corners for resting on, and `StanceGround` rests a body that is off its legs on its support points with the slope's tilt.
`CharacterPoses` stores each body's current and previous pose, lift, sway, and yaw, so frames draw between steps. All of
it allocates nothing. A game builds the bodies (the shapes that fill each part's voxels), which are its own.

### The raster pipeline draws the world as triangle meshes

The renderer draws the resident voxels as triangle meshes through a conventional raster pipeline. `docs/rendering-redesign.md`
holds the decisions, the measurements, and the history of how it replaced a raymarcher.

- **Meshes.** `WorldMeshes` (`Rendering/Meshes/`) meshes the resident columns block by block. A block is a 32-voxel cell of
  a chunk, read with a 2-voxel apron on the main thread (`VoxelWorld.TryReadBlock`) and meshed on `MeshWorkers` threads by
  `SurfaceNets`: naive Surface Nets, one vertex per cell, at the mean of the points where the density crosses one half
  along the cell's crossing edges, a normal from a 4 x 4 x 4 gradient of the densities, an openness from the mean density
  round each of the cell's corners blended to the vertex's place, and the material that most of the cell's surface corners
  have (a model's blocks at their materials' default densities, unless it keeps densities, as an object kind's does). A block owns the edges whose lower voxel it holds, so
  blocks side by side meet exactly. Each vertex also carries how many of its cell's surface corners are each of the mesh's
  eight most common materials (`Weights`), which the fragment shader blends across a border by height. The levels from
  `Engine:Renderer:MeshFinestLevel` to the coarsest streamed are meshed. `WorldMeshes` follows the voxel change list,
  meshes a column only while it can be drawn, starts blocks nearest the view first, and reads no new block after 2 ms of a
  frame. A 2 m dig's blocks are meshed and staged within 3 to 11 ms (2 to 5 frames).
- **Joins between levels of detail** are stitched, not skirted. Each frame `LevelBalance` keeps every drawn column within one level of
  its neighbours, corners included. A block along a side past which a coarser column is drawn is meshed again by
  `SurfaceNets.MeshTransition` from its own voxels and the coarser level's block, its outer cells taking the coarser
  level's vertices, so the two meet; the stitched mesh has its own heap range and is made when first needed. About nine in
  ten of the open edges along a join close; the levels' samples (each at its own voxels' centres, which never coincide)
  disagree in a few places, and no published method stitches Surface Nets sampled this way. An exact method is open work.
- **The mesh heap.** `Engine:Renderer:MeshMegabytes` (512 by default, 64 to 4096, lowered to the device's limits) of one
  device-local buffer, which is also the index buffer. `GpuRangeHeap` gives each block's mesh a range of 256-byte granules:
  a palette, the vertices (`MeshVertex`, 16 bytes), then 16-bit indices. Finished meshes reach it through the frame slot's
  staging (`StagingWriter`). A full heap first frees the meshes of wanted columns that neither the camera's pass nor the far
  cascade drew in the last frame (`WorldMeshes.Evict`, farthest first, at most 128 MB a frame); they stay wanted and are
  meshed again when the view needs them. Only when nothing is left to free does a block go without room and the heap warn.
- **Draws.** Each frame `WorldMeshes` picks a level for each column (a coarse column splits into its four finer ones when
  its nearest part is within the finer level's reach times `Engine:Renderer:MeshReachShare`, 0.5 by default), culls blocks
  to the view and the shadow cascades, and writes a `MeshDraw` and an indexed indirect command for each block (the device
  must offer `multiDrawIndirect` and `drawIndirectFirstInstance`). The camera's commands are sorted nearest first by the
  squared distance to each block's centre, so the depth test rejects hidden blocks before they are shaded; there is no
  depth pre-pass, which measured slower. `mesh.vert` reads the vertices from the heap, with no vertex input.
- **Shadows.** With `Engine:Renderer:Shadows` on, the sun has two cascades (`ShadowCascades.Fit`): squares round the
  bounding spheres of the view's slices from 0 to 30 m and from 25 m to 200 m (or the view distance), each reaching 100
  m further towards the sun (more as the sun sinks, up to 300 m at the horizon) and snapped to whole texels, as depth
  maps of `Engine:Renderer:ShadowMapTexels` (2048) texels. They are drawn from the blocks and models whose bounding
  spheres meet them, with only the faces turned away from the sun, so a lit surface never meets its own depth. The near
  cascade blends into the far one over 5 m, and the far cascade's shadows fade out over its last tenth. In the near
  cascade the shadows are contact-hardening: a blocker search (8 taps, the maps read as depths) measures the gap between
  caster and surface, and 16 comparison taps over a Poisson disc blur by the gap for a sun 2 degrees across, sharp where
  a caster meets the surface; the far cascade uses 3 x 3 lookups. The far cascade's blocks are chosen one level
  coarser than the camera's (`FarShadowCutShare`), in a pass that is not balanced (`LevelBalance` keeps the camera's
  columns within a level of their neighbours, for its stitched seams; the far cascade draws plain meshes). Balanced, as
  it was until 2026-10-11, the far pass split its columns back to the camera's in every view of the starter, so the
  coarser cut saved nothing; unbalanced, its shadow maps took 29 to 34 % less in the benchmark's flight (0.142 to 0.094
  ms at the engine's sun, 0.297 to 0.212 ms at 10 degrees) and 10 to 21 % less at `distant`, the world pass 0.6 to 3.4 %
  less, with the same picture but for a few blobs of shadow edge. The views at the map's middle did not change: a camera
  on the corner where columns of every level meet makes both passes choose the fewest columns, a ring of 12 a level.
  With `Engine:Renderer:DistantShadowMetres` (0, none,
  by default; 400 to 8000) a third cascade, the distant one, covers the view from where the far one starts to fade
  (180 m) to that depth or the view's end, with 3 x 3 lookups; the far cascade's last tenth blends into it, and it fades
  out over its own last tenth. It is drawn only while the sun is under 30 degrees over the horizon, faded in down to 25
  (`MeshLighting.DistantStrength`; under a higher sun a 4 m boulder's shadow is a few of its texels), from the terrain's
  blocks alone, listed in the camera's pass (the camera's levels, so its casters are the surfaces drawn there; the
  models and mesh assets cast nothing into it), its casters reaching three times as far towards the sun. Its map is a
  third layer of the shadow maps (16 MB at 2048 texels), its texels about 2.4 m wide at 2 km; the world's command lists
  are four a slot (`MeshPass.WorldLists`), about 4 MB more of host-visible memory whether it is on or not. Measured on
  the starter's 8 km map at nine levels with a 2 km distant cascade and a sun 10 degrees up (the benchmark offscreen at
  2560x1440, two runs each, 2026-10-10): it doubles the shadow maps' time, 0.19 to 0.40 ms more (2.4 to 5.1 million
  more shadow triangles), so the world pass takes 9 to 18 % longer (1.93 to 2.11 ms at the start, 3.20 to 3.58 at
  `high`); the extra lookups cost almost nothing. Listing its blocks in the far pass instead cost the same and drew the
  same picture, since the far pass then chose the camera's columns (below). It changes the starter's picture little, because the 1.5-texel normal offset (3.6 m) leaves out the
  shadows of anything under about 4 m, the boulders included, and the starter's gentle slopes cast only under a very
  low sun: at 12 degrees 0.4 % of the start view's pixels changed, at 4 degrees 2.9 % of `high`'s, the back slopes of
  the hills 200 m to 4 km out in shadow. A game with taller ground (cliffs, mountains) gains more.
- **Anti-aliasing.** `Engine:Renderer:MsaaSamples` (4 by default; 1, 2, 4, or 8, lowered to what the device offers) draws
  the world into multisampled colour and depth images of its own, resolved as rendering ends into the target (the mean) and
  the depth image (sample 0, which the particles and lines test against). SMAA 1x runs only when MSAA is off (below). The
  settings' Anti-aliasing is one choice of Off, SMAA, MSAA 2x, 4x, and 8x, applied at once.
- **Look and light.** A material is a colour blended towards its tint colour by the tint map (a game's, from any field),
  with a glow. Its pattern comes from a texture set: a recipe (`mesh-surface.glsl`'s `surfacePattern`, the kinds
  `VoxelPattern` names) baked once into colour, normal, height, and occlusion layers by the console's `materials bake`
  and `tools/Textures`, or a photograph, stored as block-compressed DDS files (BC7, BC5, BC4) read three ways by the
  surface's facing and filtered anisotropically. No recipe is worked out per pixel. `mesh.frag` lights each pixel with
  the sun through the cascades, sky and ground light dimmed by the vertex's openness (never below 35 %) and scaled by a
  sky-visibility field, point lights, fog, the tone curve (a filmic fit of ACES after an exposure), and the grade
  (`lighting.glsl`, shared with the particles). A glowing surface keeps its colour, leaves the grade, and adds its glow
  after it. `world-sky.frag` draws the sky where the depth is still 0. The depth is the reverse form of `view.glsl`: the
  lens's near plane (5 cm) divided by the distance along the view direction, so 0 is the sky and nearer is greater (an
  orthographic view's falls evenly from 1 at its near end to 0 at its far end). The art direction (a stylized look, less
  saturated and darker, darker on the sides, warm sunlight and cool sky light, dark tones towards teal and light ones
  towards orange) is the engine's look, `RenderLook.Default`, which the starter keeps.
- **The look** (`RenderLook`, in a per-slot storage buffer the shaders read as `LOOK`): the art direction's values,
  which were shader constants (the world's saturation and side shading, the light's tints, sky and ground light, the
  occlusion floor, fog, the sky's shares and sun disc, exposure, the grade's strength, the split tone, and glow), from a
  game's `IAtmosphereSource.LookAt` at the camera each frame, and tuned live with the console's `look` (in code, not in
  the settings file: `docs/rendering-extension.md`). What the look gives with the atmosphere (the sunlight, sky and
  ground light, the sky's colours, the grade's hue and amounts, the haze's density) is worked out once a frame on the
  CPU: moving the constants into data left the starter's pictures as they were (within 1 level) and, with that, made the
  world pass 1.2 to 2.2 % faster (0.02 to 0.05 ms at 2560x1440 offscreen, 2026-10-10; read from the buffer at every
  pixel instead, it was 1.4 to 2.1 % slower).
- **Atmosphere** (`RenderView.SetAtmosphere`, in `ViewConstants`): the sun's direction (moving as the game says; under
  the horizon it is night: no sunlight and no shadow maps, `SunPath`; a game's moon is the same light, pointed at the
  moon above the horizon with a dim colour), the sky colour, the sun's colour, haze (fog that thickens with distance),
  overcast (a flatter sky, a hidden sun disc, more sky light), and grade, from a game's `IAtmosphereSource` at the
  camera, so a game can differ by place.
- **The sky field** (`SkyField`) holds how much of the open sky each cell of a box of 200 x 100 x 200 cells round the view
  sees, a cell being a voxel of the level nearest 64 cm. The main thread reads it from the resident columns, at most 1 ms a
  frame, a worker spreads it through the air, and it is built again when the view is 32 m from the box's middle or 16 m
  above or below it, or 2 s after an edit; it is copied into a 3D texture that `mesh.frag` samples once a pixel to scale sky
  and ground light.
- **Point lights.** Each world block with glowing vertices gives one light, and events give others (`PointLights`: `Add` for
  a light that shines while something happens, `Flash` for one that fades). The brightest `Engine:Renderer:MostLights` (128
  of at most 256) within 48 m go to the GPU, and a compute pass (`mesh-clusters.comp`) sorts them into 16 x 9 x 24 clusters
  of the view, each keeping up to 32, so each pixel loops over its own cluster's.
- **Cost** (Release, 2560x1440, render scale 1, 4x MSAA, shadows on, the benchmark's views): the world pass takes 1.7 to
  2.6 ms before materials and 2.6 to 3.9 ms with them, of it the shadow maps 0.3 to 1.4 ms. Over no anti-aliasing SMAA adds
  0.23 to 0.31 ms, MSAA 2x 0.40 to 0.46 ms, 4x 0.53 to 0.78 ms, and 8x 0.97 to 1.41 ms. The main thread spends 0.12 to 0.35
  ms a frame in `WorldMeshes.Upload`.

### Models are drawn beside the world

A model is a small voxel volume drawn at any position and turn: a part of a character's body, a loose body's piece, an item.

- `VoxelModels` (main thread) holds each model as one chunk tree per level (up to 3) in a voxel memory (the world's with
  terrain, its own without), with a voxel unit of its own (1 cm in the starter; an object kind's its own), meshed with
  densities where it keeps them (an object kind's), until its owner frees it. `WorldMeshes.Models.cs` meshes each level of each model as it
  does the world: the level is cut into 32-voxel blocks starting one voxel before its box, each block read with a 2-voxel
  apron of air and meshed on the same workers, before the world's blocks, and kept in the mesh heap in a range of its own. A
  model counts as uploaded (`VoxelModels.SetUploaded`) once all its blocks are in; loading tasks wait for that. A replaced
  model (a wound) draws its old meshes until the new ones are all in (`VoxelModels.Revision`); a removed model's ranges go
  the next frame. A settled loose body's model is retired, not removed: it is drawn at its last pose until no world block is
  pending, so a ball never vanishes for a frame between its body and its voxels.
- Frame systems add models to `ModelInstances` (model, level, corner, turn, tint, and a `ModelShadow`; at most 32,768 a
  frame). `GameLoop` hands the list to `WorldMeshes.Upload`, which keeps the draws whose bounding sphere is in view, or
  casts into a shadow cascade, groups them by model level with a counting sort, and writes each block of a level as one
  indexed indirect command whose instances are that level's draws (`MeshModelDraw`). `mesh.vert` and `mesh-shadow.vert`
  compiled with `MESH_MODEL` place and turn them, and `mesh.frag` takes the draw's tint instead of the tint map's. The
  models are drawn in the world's pass, tested against the world's depth and written into the same depth image, so models
  hide each other and the ground hides them; they are timed as `Models`, a part of `Voxels`.
- `ModelShadow` says how a model meets the sun's shadow maps: `Casts` (drawn, and casting; the default), `None` (drawn, and
  casting nothing: first-person arms and what they hold), and `Only` (not drawn, and casting: the local player's own body in
  first person).
- Tint is one value per model, set by the game (`VoxelTintSampler` blends the tint map on the CPU as the shader does, so a
  loose body matches the ground it settles into).
- Cost: 1,000 near goblin models (each 19 parts of 1 cm voxels at coarser levels with distance) added 1.07 ms to the world
  pass at the benchmark's `crowd-near` view and about 0.6 ms at `crowd-far`.

### Voxel objects are volumes of their own, finer than the world

Trees, bushes, rocks, and props are voxel objects (`Objects/`; `docs/voxel-objects.md` has the choices and
measurements, `.claude/rules/objects.md` the details): each kind is a volume of voxels finer than the world's (5 cm in
the starter's 10 cm world), built in code and registered at the run's start; the world's generator places them; they
are never written into the world's fine levels.

- Near the camera an object is drawn as its kind's part models, turned to its heading, at three levels by distance that
  fade into each other by a screen-space dither; from a hand-over level K up (2 by default) the world's voxels hold a
  copy of it at its own size, written by the column jobs, which the world draws as ground. The coarsest model fades out
  over its copy across 30 m past the farthest point where a level-K column can split, and a column splits below K only
  once its objects are known and their models are on the GPU, so the switch follows the distance and nothing goes
  undrawn.
- Objects are shaded as the world's surfaces (its materials, texture sets, and tint map), so a model and its copy
  match.
- Characters, bodies, rays, and navigation meet objects by their voxels (`ObjectColumns`, the objects' Bepu statics near
  bodies, `ObjectRays`, `NavObjects`); edits damage them by share, the same live and on replay, and a part cut off is
  removed by an edit of its own (`RemovePart`), the world's owner deciding the cut, and falls as a cut body each peer
  makes.
- A game places objects during play, and removes whole ones (its own or the generator's), by edits of their own
  (`PlaceObject`, `RemoveObject`, logged by the world's owner through `ObjectWorld.TryPlace` and `TryRemove`), which
  every peer replays from the log as it does any edit: an area's fill takes them after the generator's objects, in the
  log's order. Far copies come from the generator's placer alone, so a kind that is copied is neither placed nor
  removed this way; a game whose view ends before the hand-over to copies registers its kinds without them.
- Cost: the world pass stays within 4 ms in the starter's views but one, looking down over a forest from 40 m (4.72 ms),
  left as future work.

### Characters move with Itera's own controller against the level-0 voxels; Bepu is for loose bodies

Itera's narrow-phase filter makes no contacts between two collidables that are not dynamic (`NarrowPhaseCallbacks`;
Bepu itself would ask about them), so a Bepu character controller would need a dynamic body held upright by a custom
constraint, or sweeps (which give no way out of a start inside a surface and may miss thin obstacles), and either needs
collidables built from the voxels and rebuilt after every edit near a character. `VoxelCharacterMover` instead reads the chunk trees directly (`VoxelWorld.Collision.cs`), so a dig
changes collision at once:

- The body is an upright cylinder with flat ends, not a capsule: every trunk, boulder, and curved wall is a staircase of
  voxel steps, and a circle takes its contact normals from the voxel corners, so it slides along them; a capsule would need
  sphere tests against thousands of voxels and slips off ledges. Its flat bottom stands on the highest voxel under it.
- It collides with the exact level-0 voxels, which are loaded within level 0's radius of the streaming centre. On voxels
  coarser than 1 cm its distances are in centimetres and it stands on the ground's surface inside a voxel, where its density
  crosses one half, blended bilinearly between neighbouring columns whose surfaces differ by less than the slope limit over
  one voxel (so a slope under the limit is a ramp and a ledge, a stair, or a wall stays sharp). It steps up onto ledges no
  higher than its step (from the ground, and in the air where a movement mode asks, `CharacterShape.StepsInAir`, so a
  jump that nearly clears a wall lands on it), keeps to the ground walking down, lands falls, stops jumps at ceilings, and lifts a body out of
  voxels placed into it. It never moves a body while a column its body and move cover is not resident. The map's edge is a
  wall at every height. Passable materials (`IVoxelPassability`: foliage) let it through.
- It reads a game's mesh colliders (`MeshColliders`) as the same cells (`MeshColumns`: the spans each triangle makes in
  each column, made 32 by 32 columns at a time at their first read, at most 256 such chunks kept), taking the higher top,
  the lower bottom, or either cell of the two, without virtual calls. Only the cells a triangle passes through block (a
  closed mesh has no inside), one on a cell's boundary the cell behind its front, so a closed mesh of a voxel box blocks
  the box's outer voxels, and a column's surface is the triangle's plane at the column's centre, so a mesh slope is
  walked as a ramp. Over terrain its cells may be finer than the voxels (`Engine:Physics:TerrainCellCentimetres`), so it
  meets objects, solid models, and meshes at their own size over coarse ground: it then reads the voxels as fine cells of
  ground under their surfaces, blended across the voxels' columns with no seam (`FineGround`; on smooth 1 m hills the 10
  cm mover's feet keep within about 2 cm of the 1 m mover's, and a walking move costs about 2.4 times as much). Without
  terrain it reads the colliders alone in cells of `Engine:Physics:CollisionCentimetres` (10 by
  default), with no column to wait for, and its map edge at the play area's world edge (`PlayArea.WorldEdgeMetres`). Its
  tests run the same bodies on voxels and on triangles built from the same features (`MoverFeatureTests`,
  `DensityGroundTests`). A move over triangles alone in 10 cm cells costs about 1.5
  times one over 10 cm voxels in the tests' walks (30 against 20 microseconds); in 1 cm cells, about 6.5 times (130
  microseconds), as each column is read one by one where the voxels are read a brick's mask at a time.
- Moves split into parts of at most 24 voxels, each slid through a 2D grid of the cells that block anywhere in the body's
  height: strips of 128 cells side by side, up to four, so a body is at most 2 m in radius (`MaxRadiusMetres`). The queries
  stop early where they can, and a move remembers each column's surface. A player's move costs about 8 microseconds on flat
  ground in Release.
- A slope limit (`MovementStats` max slope, 45 degrees by default). Two rules tell slopes from ledges. A step up is kept only
  when the ground stays level just beyond it. Ground is steep when the body rests on a thin rim and the ground also rises
  more steeply than the limit within a few centimetres in one of eight directions; seen from any side the rim and the rise
  are the same, so zig-zagging does not climb a slope. On steep ground the mover does not step up, and
  `PlayerMovementSystem` slides the player down, steers it only as in the air, and does not let it jump.
- Moving characters into Bepu was rejected: voxel collision would be needed around every character at all times, and the
  step, slope, and ledge rules would be lost.

### Loose bodies are pieces of voxels that Bepu moves

A thrown ball, a lifted piece, or a part cut off a model is a loose body (`LooseBodies`), seen by Bepu as the convex hull of
its voxels (2000 kg per cubic metre of blocking voxels) with continuous collision where it is not a cut body. A compound of
a piece's boxes was tried first and cost up to 137 ms a step.

- **The world near a body.** Bepu sees the voxels near its dynamic bodies only (`VoxelColliders`): each cell of 32 voxels a
  side a body's bounds reach, widened by its movement in a step, becomes one static compound of boxes over the cell's
  blocking voxels (`VoxelBoxes`: a uniform part of the tree is one box, a mixed brick's runs are merged in x, y, then z).
  On voxels coarser than 1 cm a column's top voxel ends where its density puts the ground's surface (`PhysicsWorld.SurfaceBox`),
  so a ball rests on the drawn floor. The change list rebuilds a cell after an edit (at most 8 a step), which wakes bodies
  resting on it; a cell no body has needed for a second is removed. A body whose columns are not resident is frozen (made
  kinematic) and moves on once they are. A body whose parts are of coarse voxels collides with the level nearest those
  voxels (`VoxelColliders.CoarseLevelOf`), and `BodyKinds` keeps each body's level and each static's. A custom Bepu shape
  reading the chunk trees was rejected: Bepu's worker threads would read the voxel world, which is main-thread only.
- **Life.** A ball is made from nothing (`LooseBodies.TryCreateBall`: a round model of 1 cm stone voxels, one per radius and
  material, not in the edit log). After 3 s at rest (`Engine:Physics:SettleSeconds`) and not reaching into a character, it
  goes back into the grid as a sphere of its material filled into the air, or as a piece edit if it is a lifted piece;
  without voxel terrain the game's `ILooseBodyRest` decides (`.claude/rules/physics.md`). One that falls 10 m below the
  world is removed. At most 384 exist (`Engine:Physics:MaxLooseBodies`); when more are needed the
  oldest are removed (`LooseBodyRoom`), a ragdoll whole. Whatever removes a body destroys its entity in the same step, and
  each entity keeps its body's birth stamp (`LooseBody.Born`) so `PhysicsReadSystem` can tell a handle Bepu gave to a newer
  body.
- **Characters and bodies act on each other.** The mover reads loose bodies as voxels of its cells (`LooseBodyVoxels`: a
  brick a query reaches is filled on demand with the voxels whose centres land on a body's blocking voxels, cached for the step), so
  characters are blocked by bodies and stand on them, but push rather than climb them. Each character within 3 m of a body of
  a lifted piece or ball has a stand-in (`PhysicsWorld.AddStandIn`): a Bepu cylinder of its size and mass that never tips
  and touches those bodies only, placed where the character ended each step at its velocity plus at most 10 m/s^2 towards
  the velocity it wanted, so a character pushes with about its own weight. The change of velocity bodies give a stand-in is
  added to the character's velocity after steering.
- **Cut bodies and ragdolls.** Parts cut off a jointed model become bodies of several models that are removed after resting
  instead of put into the grid (`CutPiece`, `BodyKind.Cut`); they touch only the voxels. `LooseBodies.TryCreateRagdoll`
  makes a ragdoll of one compound body per part, joined where the parts met by a ball socket, a swing limit, and a twist
  limit (0.6 radians either way; `JointAxis` says which axis a joint turns about least). A ragdoll rests only when all its
  bodies rest, freezes and thaws as one (its joints are added again at the thaw), and is removed whole. The engine's
  ragdoll and cut-body physics are tested but have no caller until a game cuts models. Cut bodies collide without sweeps
  (passive continuous detection), which keeps piles of them cheap.
- **The physics warm-up.** Bepu makes its solver's code for each kind of joint and its collision code for each pair of
  shapes and kind of contact at run time (through reflection and generics over its own structs), so ahead-of-time
  compilation (ReadyToRun) covers none of it: in a game's first ragdoll the main thread waited while about 400 of Bepu's
  methods were compiled, a step of 44 ms in a composite ReadyToRun build and 66 ms in a Debug build (2026-10-15). `PhysicsWorld.StartWarmUp` (from the match's physics step, once a run, `WarmUps.Physics`) builds a world of
  its own with the match's settings and Bepu callbacks on a thread of its own, drops into it what the engine's bodies are
  made of (two people's ragdolls onto a voxel cell's static of boxes and a mesh ramp, a pile of convex hulls of three
  sizes, a client's kinematic mirror, a cut part's compound of hulls, a stand-in, and a trigger zone), steps it 4 s (they
  land, sleep, wake, and a ragdoll freezes and thaws), takes it all away, and frees it, in about 0.2 s; the loading screen
  waits for it (Physics). The first deaths then took a step of 4.9 ms at most, and a few kinds of contact are still
  compiled as they first occur, a few at a step.

### A game sets the physics

A game sets the match's gravity and each body's share of it (characters fall by its strength's share of the Earth's and
keep their jump's launch speed), the solver's substeps, iterations, and damping (options, and during a match), physics
materials by name (friction, and bounciness set exactly by a pass after each step, since Bepu's contacts have no
restitution) on its bodies, statics, mesh colliders, and voxel materials (a voxel cell a static per material), collision
layers and groups, trigger zones that report the bodies and characters coming in and going out, and impact events for
the bodies and statics it marks. The engine's records by handle (`BodyKinds`) carry each body's and static's settings to
the narrow phase on Bepu's worker threads; every removal resets them. Gravity is each peer's own, and down stays -Y for
characters, the mover, and the terrain. The rules are in `.claude/rules/physics.md`, the choices and findings in
`docs/physics-materials-and-groups.md`.

### Networking is host-authoritative

One peer is the host and owns the simulation. Deterministic lockstep is ruled out because Bepu does not guarantee
bit-identical floating point across CPUs. Voxel deformation is synchronised as edits (position, shape, operation), never as
voxel data. A host is a game run, with or without graphics (`Engine:Window:Mode=None`), and holds slot 0 as a player;
there is no host migration, and the session ends when the host quits.

- **Sessions** (`Networking/`). The menu's buttons and the console's `play`, `host`, and `join` start a match as Offline,
  Host, or Join; `Engine:Session:Mode` (Menu, Offline, Host, or Join, with `Address`, `host:port`) starts one at once without
  the menu; offline is the host role with no socket, so `SessionService.IsAuthority` is the one check. Messages travel
  through `ISessionTransport` (LiteNetLib's `NetworkService`, or Steam's `SteamTransport`) on a reliable ordered channel (the
  join and edits) or a sequenced one (per-tick state). Each message starts with a kind byte; numbers are little-endian and
  strings UTF-8 behind a 16-bit length, written into one reused `MessageWriter`, so sending allocates nothing, and read by
  `MessageReader`, which throws on anything short or oversized (a peer is not trusted). `SessionProtocol.Version` rises
  whenever any message changes shape or a kind is added, a game's included. `WireFingerprintTests` pin a fingerprint of
  every message's bytes with the version, so a change to the wire fails them until the version rises.
  `SimulatedNetwork` wraps a transport to add delay, jitter, and loss (`Engine:Session:AddedDelayMilliseconds`,
  `AddedJitterMilliseconds`, `AddedLossPercent`), so a host and a client can be tested as if far apart.
- **The handshake** is plain messages, so every transport shares it: the client sends `Hello` (protocol version, the
  match identity's id and version (`IMatchIdentity`: the generator's with voxel terrain, its object kinds' fingerprint after its id where the world places objects), fixed step rate, name); the
  host answers `Welcome` (the client's slot from 1, the match: seed, step rate, identity; the join payload's size and edit
  count; a game's match data), or refuses by closing the connection with a reason (another protocol, identity, or step
  rate, a full session, no hello within the connect timeout, or, through
  Steam, a player neither in the host's lobby nor its friend). A second hello is a protocol error. `MaxPlayers` counts the
  host.
- **The join payload** is the host's saved edit log (`SaveEdits`, `EditLogSerializer`), saved when the hello is accepted,
  in chunks of 16 KB behind the welcome, sent from the host's polls while the transport has room for them
  (`ISessionTransport.ReliableRoom`: any amount on LiteNetLib, 256 KB queued at most on Steam), then `JoinDone`; the client
  has joined from then on, and the edits made since the payload follow it. The client refuses a payload over
  `MaxJoinMegabytes` before downloading it, keeps it in memory, and loads it once its world exists. A game message that
  arrives before `JoinDone` is dropped.
- **Edits.** After each fixed step the host sends each joined client the edits it has not been sent, each with its maker
  (the slot of the player whose action made it, or 255), a lift with its piece as runs of one material. The client applies
  them in order; a gap, an invalid edit, or one past the edit log's index budget ends its session with an error. After
  loading, a client's world follows the host (`VoxelWorld.FollowHost`): `Apply` and `TryLift` throw, so a mod or a game
  checks `IsAuthority` before editing.
- **A game's messages** are kinds from `SessionService.FirstGameKind` (64) to the `ISessionGameHandler` of their kind
  (`GameMessageHandler` hands one kind to a method), registered in DI so each exists before the session step. Each handler
  states which way its kind travels (`SessionMessageDirection`: to clients, to the host, or both), and the session drops a
  peer that sends a kind meant for the other direction before the handler sees it; a handler that throws anything drops that
  peer too. The game writes a message with `BeginMessage` and sends it with `SendToHost`, `SendTo(slot)`, or
  `SendToAll(exceptSlot)`. A roster message gives every game each slot's player (`SessionService.Players`) and its join, a
  number the host gives each join, so a per-slot system forgets what it kept for a slot whose join changed (`SlotJoins`).
  The host's `ISessionMatchData` writes a game's match data into the welcome. Helpers: `NetEntities` (the ids a host gives
  the entities it sends, with a byte kind the game defines, checked alive at every lookup; `PlayerIds`), `TickRing` (the last
  16 values by sender tick, since unreliable messages may arrive out of order), and `JitterPlayback` (a few ticks' delay for
  smooth playback). A game tests its messages with damaged bodies and hostile values in every field.
- **Replication** (`Replication/`, `.claude/rules/replication.md`, the reasons in `docs/replication.md`): a game
  registers kinds of entity at its run's start (`ReplicatedKinds`: fields as quantised lanes, a handler, a reach, a
  priority) and marks entities on the host (`ReplicationService.Add`). Thirty times a second the host sends each client
  the entities in its reach with a state the client has not acknowledged, highest priority first, each as its changes
  from the client's last acknowledged state, in unreliable packets that fit one transport packet, until that client's
  64 KB/s is spent (what a send leaves is kept, up to a send's share of 128 KB/s); removals go in the same packets until
  acknowledged. A client replies every
  step with the packets it read, estimates the host's tick, and shows each entity 100 ms behind it, blended between the
  states round that time (`ReplicationSendSystem`, `ReplicationReceiveSystem`, `ReplicationReplySystem`). The kinds'
  fingerprint is in the hello. A client's own player is predicted (`Predict`): it moves at once from its commands, which
  go to the host with every reply (the last 8, quantised as the host reads them); the host moves its copy from them,
  and sends the client its state after the last command applied; a client that predicted otherwise (1 cm, or another
  contact) takes the host's state, runs its later commands again (`CharacterStepper`), and eases what is drawn
  (`EyeSmoothing.Correction`). A client's tool use is a request: the host applies it and sends the numbered edit to
  everyone, and nothing shows early on the client.
- **The host quitting** says so (`Bye`): its clients go back to the menu with the reason, or, when the options started the
  match, end with exit code 0; a lost connection or a protocol error does the same, with exit code 1 for a match the options
  started. Each match has a session of its own (Scoped), on the run's transports, which close at its end and open again for
  the next.
- **Saves.** `SaveService` writes the host's or an offline match's world and the game's own state to
  `Saves/NAME.save` in the data folder (`Engine:Save:Name`, 1 to 64 letters, digits, spaces, '-', or '_'): every
  `Engine:Save:AutosaveMinutes` (5) of the match's steps, at the console's `save`, and when the match ends cleanly,
  after putting thrown bodies still loose back into the grid (an autosave leaves loose bodies out). A game's state is
  its `ISaveSection`s (scoped, each a name, a version, `Capture`, and `Restore`), written after the edit log in format 8.
  A save is captured on the main thread between steps, the edit log frozen (`EditLog.Freeze`: its written edits never
  change and its pieces' memory never moves, so only the segments' references and the pieces are copied) and each
  section's bytes taken; an autosave's is written on a background thread with a scratch of its own, one write at a
  time, and the match's end waits for it before the world's memory goes. The last save is captured at the "save
  sections" step's shutdown, before the ECS world goes, and written at the save step's. The stream goes to
  `NAME.save.tmp`, the old save is kept as `NAME.save.bak`, and the new one takes its place; any failure leaves the old
  save and no temporary file, and a section that throws when captured leaves the last save and the match going on. A
  match with a name starts from the file when it exists, read whole and checked, or from the backup with a warning when
  the file is damaged (the next write keeps the damaged file as `NAME.save.damaged`; a save that is whole but of another
  generator or a newer build is refused, so no write replaces it, and a deleted save starts a new world), with the save's
  seed and match data; the sections go to the game's at the "save sections" step, after the systems are made and before
  the first step (`SaveService.StartedFromSave` tells a system the match resumes). A save of another step rate,
  generator, or generator version, or a section of a newer version than the game's, ends the match before it is played.
  After an exception nothing is written, and the last save stays. A client never saves.

### Steam

`SteamService` starts the Steam API at startup when `Engine:Steam:Enabled` (default true), the game has an App ID
(`Engine:Steam:AppId`, 0 by default, which leaves Steam off), and the Steam client runs, and logs the player's Steam name
and id; without the client, or where Steam has no library (Linux and Windows on arm64), it logs a Warning and the game
plays offline or over Direct. A player's name in a session is `Engine:Session:PlayerName` when set, else the Steam name (cut
to 32 characters), else "Player". A run Steam did not start gets `steam_appid.txt` next to the game, and the working
directory is moved there for the Steam API's start only. Steam's library is loaded from next to the game by a resolver
(`NativeLibrary.SetDllImportResolver`), since Steamworks.NET names only `steam_api`. Starting the Steam API loads Steam's
overlay and fossilize Vulkan layers into the game; the overlay draws one best-practices warning of its own, so
`validate.sh` runs with Steam off.

- A match through Steam (`Transport=Steam`: the menu's Host Game when Steam runs, a join of `lobby:ID` or `steam:ID`)
  carries its session on `SteamTransport`: a host listens on a P2P socket (virtual port 0, through Steam's relays, so no
  port is opened), opens a friends-only lobby (`MaxPlayers` members; lobby data `itera_players`, `itera_max`, and
  `itera_match`, the match key: protocol, the match identity's id and version, step rate), and sets its rich presence's `connect` to
  `+connect_lobby ID`. A friend who picks "Join game" while the game is not running gets it started with those arguments,
  which `SteamLaunchArguments` turns into the menu joining `lobby:ID`. A host accepts a connection only from a member of its
  lobby or a Steam friend; anyone else is refused with the reason. A friend is checked first, since Steam may tell the host
  of a new lobby member after the member's connection arrives.
- The menu's join page lists the friends who host (`SteamFriendList`): each friend whose game is this app in a lobby, or
  whose rich presence's `connect` names one, with the lobby's players and limit and "Other version" where its match key is
  not this build's. Steam's persona, lobby-data, and avatar callbacks reach it through a second listener
  (`ISteamFriendsListener`).
- The handshake, the join, and the edits are the same messages as on LiteNetLib: reliable messages on channel 0, unreliable
  ones (no Nagle delay) on channel 1. A disconnect's kind, reason, and text travel as Steam's end reason in the app range.
  Messages are read from Steam's message structs through one poll group without allocating. Steam refuses a reliable message
  over 512 KB (a join chunk is 16 KB), and any message while a connection's send buffer is full: the transport skips a
  sequenced message while more than 32 KB are queued to its peer, holds a refused reliable message with every later one
  behind it to send again in order at the next poll, and ends a peer with more than 8 MB held.
- Test builds reach testers through Steam: `tools/steam/upload.sh` publishes Release builds for `win-x64` and `linux-x64`
  (self-contained) and uploads them with steamcmd to a branch; its README has the setup.

### Navigation by flow fields over a grid

Navigation (`NavigationService`, host and offline only) fills a grid of 50 cm cells (`NavGrid.CellCentimetres`) on a
thread of its own from a game's `INavGridSource` and builds the flow fields the game asks for. A game without a source
has no navigation.

- **The grid** (`NavGrid`): one layer of cells, each with its floor (absolute centimetres in an `int`, so any map's
  heights fit), its headroom (in 2 cm, up to 5.1 m; 0 is blocked), and a cost class. The game's source fills a rectangle
  of it from its own knowledge of the world (the layout as generated, not generated voxels) and says what the ground is
  at a cell.
- **The window** (`Engine:Navigation:WindowCells`, 1024: 512 m): a map that fits is covered whole and never moved; on a
  larger one the grid is a square that follows a focus (`NavigationService.Follow`, else the first streaming centre),
  moving by 32 m tiles when its corner for the focus lies a quarter of it away, or the focus nears its edge. The thread
  fills only the strips coming in and stamps on them the logged edits since the last reset that reach them; those wait
  to be read from the voxels as any stamped edit does. Each field keeps the window it was built over, and a goal past
  the window stands in at its nearest edge cell, so walkers head that way until the window reaches it. About 6 MB a
  grid (two are kept), 1 MB a field, and 1 MB a size at 1024. A walker on open ground crossed 2 km of a 4 km map to its
  goal with 11 moves of the 1024-cell window, each building its field in 30 ms in Release (153 ms in Debug); on a large
  map a whole field takes far less than the old map-wide grid's, whatever the map's size. The
  rule for a cell, from the source or from the voxels (`NavGridScanner`), at its centre: anything rising at most
  `LowCentimetres` over the ground raises the floor, anything higher blocks the cell, and the headroom runs to the lowest
  blocking voxel over the floor; passable materials count as air. A read takes the floor from the density surface over the
  highest blocking voxel (`VoxelWorld.SurfaceOver`), within about a centimetre of the source's ground.
- **Walkable for a size** (`NavSize`: a height, a radius, and a climb): a cell and every cell the body's circle reaches from
  its centre hold its height. Entering a cell costs its class's price (`NavCostTable`), or the break cost where the size
  cannot stand, or the steep cost for a rise over the climb. A block can be a price to break through or a wall to walk
  round; blocked cells are dear, not closed, so every cell has a route.
- **The fields** (`NavFieldSpec`: size, costs, goal; `FlowFieldBuilder`): Dijkstra backwards from each field's goal over
  the eight neighbours, with a ring of buckets as the queue (every cost is a small whole number), recording each cell's step
  towards the goal; allocation-free once its arrays have grown. The engine knows nothing of teams; a game's fields are its
  own. A field takes about 0.2 s in Release.
- **Edits** (`NavigationService.AfterFixedStep`): each new edit's cells are read again from the voxels when its columns are
  loaded, at most 128 cells a step, otherwise stamped from its shape (a dig lowers the floor, stone raises it or blocks the
  cell) and read once the voxels load; the stamped edits are checked in turn, 32 a step, and an edit that covers no cell's
  centre column is passed over. A stamp and a read judge what stands from the source's ground at the cell
  (`INavGridSource.GroundAt`), and a far removal of some materials digs only when it removes ground (`IsGround`). Changed rows
  are copied to the thread's own grid, and at most every 0.5 s the thread builds every field again while walkers keep the
  old ones until the new are swapped in whole. A partial recompute was measured first and not built: a whole rebuild runs
  off the main thread in about a second. After a reset the thread fills the grid again first.
- Moving walkers along a field, spreading them in lanes, steering round each other, and breaking through blocks are a
  game's; `NavigationDebugLines` draws a field's arrows round the camera.

### The UI is drawn by Skia into a CPU surface and blended over the picture

- `UISurface` draws its layers (`IUiLayer`: the run's below the match's in registration order, then the match's
  `IMatchUiLayer`s, a game's HUD, set when a match starts, then the run's above them: the menu and the console) into memory
  it owns, four bytes a pixel (Skia's BGRA 8888, premultiplied), at the size of each frame's request, so the surface and the
  pass always agree. It draws only when a layer changed, appeared, went, or moved, and then only the area of those layers
  (what they cover now and covered before): it clears that area and draws again each visible layer that meets it, every
  layer clipped to its own bounds, and reports the area's rows, so an unchanged UI costs nothing and a change copies only its
  band. A layer's bounds hold everything it draws, which each bounded layer's tests check (`LayerInk`). Skia's text calls
  allocate, so the UI allocates when it draws and never on a frame with nothing to draw. Skia's Vulkan backend is a later
  optimisation.
- `UiPass` keeps the pixels in a device-local storage buffer the size of the swapchain image, not an image: the fragment
  shader (`ui.frag`) reads the pixel at `gl_FragCoord`, so no layout change or sampler is needed. The main thread writes the
  changed rows into the slot's region of an upload buffer of its own; the render thread copies them before the draw. The
  buffers are made again, on the render thread with the device idle, only when the swapchain's size changes. Skia
  premultiplied in sRGB, so the shader divides by alpha, turns the colour linear, and multiplies again before the
  premultiplied blend into the sRGB image. A screen larger than the device's largest storage buffer gets no UI and a
  Warning.
- The console (`DevConsole`, drawn by `ConsoleLayer` over the top 40 % of the screen): ` opens and closes it. While it is
  open, `InputService` sends it keys, characters, pasted text, and the wheel instead of the game, the pointer is free, and
  clicks do nothing. Commands (`ConsoleCommands`) have a scope: `Local` commands change only this game; `Match` commands
  work offline or on a host, since they change what the session shares; `Offline` commands work offline only. A refusal
  names the reason. Commands run between fixed steps, and each run is logged at Information, which is its echo in the
  scrollback. `Engine:Console:Run` runs commands once the first match's world and systems exist, for unattended runs. A game
  system that owns a key registers its commands in `Initialize`; a match's commands go when it ends. The engine's commands
  are in `EngineCommands`, `MatchCommands`, `SettingsCommands`, `ParticleCommands`, and `SoundCommands`.
- The stats line (`StatsLayer`, F1, `stats`, `Engine:Ui:Stats`) shows frames per second, frame time, the last frame's GPU
  time, the render scale, fixed steps per second, the session, and the living particles. The font is JetBrains Mono Regular
  (SIL Open Font License 1.1, its licence beside it in `src/Itera.Engine/UI/Fonts/`), so text looks the same on every
  platform. One sizing rule sizes the UI: a unit is a pixel of a 1080-pixel-high screen, scaled with the screen's height
  and the player's UI scale (75 to 150 %), so the menu, the console, the stats line, and the loading list keep their share
  of the screen at every size: the console's, stats line's, and loading list's text is `Engine:Ui:FontPixels` units (12
  px at 720, 24 px at 1440, never under 10), and the menu's 32.4 units (3 % of the height).
- The main menu (`MenuLayer`, a layer of the UI toolkit) is the run's: Play Offline, Host Game, Join Game, Settings, and
  Quit, with a join page of the Steam friends who host and an address box, and, over a match at Escape, Resume, Settings,
  Leave Match, and Quit. The settings (the volume with music's and the effects' under it, mouse sensitivity, field of view, the UI scale, dynamic render scaling,
  shadows, anti-aliasing, VSync, full screen, and the keybindings) apply at once and live in `settings.json`, below the
  command line. A game gives the menu its look (`MenuStyle`), adds its own pages (`MenuPages`) and its switches to the
  Settings page (`SettingsRows`), or swaps the menu for its own (`IMenu`, which can show the engine's `SettingsPage`), which reaches the run through the same public services: matches
  (`MatchControl`, `MatchCommands`), quitting, the player's settings, the Steam friends hosting (`SteamFriendList`,
  which the loop updates) and their joins (`JoinRequests`), and the saves (`SaveFiles`). With
  `Engine:Ui:MenuPausesOffline` the menu holds an offline match's steps while open.
- A game's own text box takes the typing while it holds it (`IFrameInput.SetTextFocus`, `TextEntry`): every key but `
  and the characters go to it, none to the game. Characters come from GLFW's own callback as whole code points, since
  Silk.NET's `KeyChar` cuts one past U+FFFF to 16 bits. Escape goes to the console, the menu when open, the game's text
  box, the game's screens (`IEscapeHandler`, newest first), its panel, and then opens the menu.

### A run plays many matches

A match is a DI scope (`MatchServices`, section 4): every service holding a match's state is Scoped, so a new match gets
new ones and the old are freed with the scope, which no reset method could promise for the dozens of stores a game keeps. A
run-wide service that touches the match is handed it (`WorldMeshes.BeginMatch`, `UISurface.SetMatchLayers`,
`ConsoleCommands.BeginMatch`, which removes every command registered during the match at its end, `ParticleColours.Tint`,
`MatchControl.Current`, `LoadingScreen.Initialize` with the match's tasks, `ModLoader.MatchStarted`, a game's panel and
drawn pointer through `IFrameInput.SetPanel` and `SetDrawnPointer`, and the fixed systems' `SystemTimes`) and drops it at
the end. DI's scope checks catch a singleton that takes a scoped service. `MatchLifetimeTests` plays several seeds in one
process and checks that nothing of an ended match stays (its services collected, its voxel memory freed, its Arch world
gone, the console's commands back to the run's).

The render thread runs from start to exit: every Vulkan object is made once, the mesh heap sized from the options, and a
match's GPU state is CPU bookkeeping. Stopping it could time out while presentation blocks, which abandons the graphics.
With no match's world, frames are the loading screen's ring with the UI over it. The session starts without blocking
(`SessionService.Begin`, then `PollJoin` once a loop iteration, the menu showing the download), so the window answers while
a client joins. The warm-ups run once per run (`WarmUps`). Leaving a match while it loads waits for the navigation thread
(5 s at most).

### A render scale with FSR 1, and dynamic scaling

`Engine:Renderer:RenderScale` (0.5 to 1) and the console's `scale` set the share of the screen's size, on each axis, that
the world, the models, the particles, and the debug lines draw at. The main thread builds the view at that size; the render
thread sizes the drawn image from the swapchain's size and the frame's scale (`RenderScale.Apply`). `UpscalePass` owns the
source image (the swapchain's format, made mutable with a list of it and its UNORM twin) and the upscaled image (10 bits a
colour), made on the render thread the first time a frame is drawn below 1 and again when a size changes. Nothing waits for
the device: each frame slot has its own descriptor sets, pointed at the images only after that slot's fence, and images
replaced or retired are destroyed once the frame before has passed its fence (`FrameImages` keeps them for 600 frames at
scale 1). EASU reads the source through the UNORM view, because FSR 1 wants sRGB-encoded values; RCAS (sharpness 0.2 stops)
writes the swapchain image. The UI draws after it at full size. FSR 1 runs on any GPU brand; FSR 2 and DLSS were not
chosen.

`DynamicRenderScale` (on by default, `Engine:Renderer:DynamicRenderScaling`) runs on the main thread before each view is
made and sets `RenderView.RenderScale` in whole hundredths. It steers by the GPU's time of the whole frame (`GpuTimings`),
not the frame's wall time: the scale changes only the GPU's work, so a frame the CPU holds up would only lose sharpness.
The target is the lower of `Engine:Renderer:DynamicScalingThreshold` (120 fps by default; the menu offers 30, 60, 75, 90,
100, 120, 144, 165, 180, 240, and 360) and the screen's refresh rate (`WindowService.RefreshRate`), VSync on or off. Each
step takes the mean of 8 measured frames after skipping the 3 that a change has not reached; it aims for 95 % of the
target's frame time; over that it lowers the scale at once by the square root of the excess, by at least 0.01; under 90 %
of it it raises the scale by at most 0.02; between the two it holds. The least scale is 0.5. Off, the scale is
`Engine:Renderer:RenderScale`, which the benchmark sets with scaling off; the console's `scale auto` gives control back.

### Anti-aliasing with SMAA 1x

AMD asks for an anti-aliased, tone-mapped, sRGB picture before FSR 1; CMAA2 is cheaper but lacks diagonal handling and a
GLSL version; only TAA fixes features under a pixel, at FSR 2's complexity (`docs/anti-aliasing.md`). `SmaaPass` runs
SMAA's three passes (`smaa.glsl`, SMAA.hlsl unchanged, MIT, the HIGH preset) between the world, models, particles, and
lines and FSR 1, so with it on they draw into a scene image of the drawn size at every scale, and the blend writes the
swapchain image (at scale 1) or FSR 1's source. It runs only when the world is drawn with one sample a pixel
(`MsaaSamples` 1); 4x MSAA is the default. Edges are found by luma in sRGB-encoded values and blended in linear light.
SMAA's lookup textures are copied in the first anti-aliased frame from a buffer the main thread filled before the render
thread started. The console's `aa edges` and `aa weights` show what SMAA found. It costs 0.23 to 0.31 ms at 2560x1440.
On Zorf TD's view along its players' path (a measurement of that game's world, at 2560x1440) the world took 1.26 ms with
one sample, 1.65 with two, 1.95 with four, and 2.52 with eight, and SMAA added 0.30. It smooths the steps along edges;
it does not fix features under a pixel that flicker. SMAA's stencil step and depth predication are not built.

### Particles live on the GPU

Game code queues bursts (`ParticleBurst`: a recipe, a count, a shape of point, ball, segment, or disc, a direction whose
length scales the speed, a velocity, a tint, an attractor, a seed) with `ParticleEmitter.Emit`, from fixed or frame systems;
they wait for the next frame built, at most `Engine:Particles:MaxBurstsPerFrame` (4096) bursts and `MaxSpawnsPerFrame`
(65,536) particles, the rest dropped with one Warning. A recipe (`ParticleRecipe`, registered by name in
`ParticleRecipes`, at most 128, the whole table sent every frame) gives the ranges each particle draws its own values from:
speed, spread, life, size, colour and brightness, spin; and how it moves and looks: gravity, drag, what it does at the world
(none, bounce, stick, die), lit or glowing, fade in and out, a sprite's cover and stretch. `StockEffects` registers seven
general recipes (`flare`, `sparks`, `embers`, `debris`, `smoke`, `dust-puff`, `grit`) a game may use. The pool
(`Engine:Particles:Capacity`, 262,144, 64 bytes each) is a ring: when particles are made faster than they die the oldest
are overwritten. Nothing is read back into the game: particles are cosmetic, never sent over the network, and cost the main
thread only the bursts. `ParticlePass` runs after the world's pass in frames that draw the world while particles can be
alive: a spawn dispatch, a simulate dispatch over the whole pool that moves each particle, meets the world, and lists those in
view as cubes or sprites with atomic counts that are the two indirect draws' instance counts; then the cubes (tumbling to a
stop, lit as the world is, writing depth) and the sprites (premultiplied, testing depth only). Particles meet the world
through the camera's depth image: a particle that moves to just behind the surface the image shows (within 0.3 m and its
step) bounces off it, sticks, or dies as its recipe says, with the slope from the neighbouring depths; off the image,
behind something, or within 0.6 m of the camera it meets nothing, and a resting particle falls again where the image shows
its support gone. A lit particle takes its sunlight from the shadow cascades and its sky light from the sky field. The
console's `particles` tests recipes by name and runs a stress test. Measured with about 237,000 alive: the move takes 0.16
ms and the draws 0.84 to 0.89 ms; 16,000 large smoke sprites cost 3.9 ms, so large blended sprites cost fill rate, not
count.

### Sound is made in code

Sound is made from models of physics, after Andy Farnell's *Designing Sound* (`docs/procedural-audio.md`): synthesis is
good and cheap for physical sounds (hits, footsteps, digs, wind, fire, explosions) and poor for voices and music. A
recipe is a small model of a sound's physics driven by one number, its level (0 to 1), registered by name in
`SoundRecipes` (at most 256, sounds from files too). A `OneShotRecipe` is rendered at start (seconds for a game with many recipes, more in
Debug), `Levels` times `VariationsPerLevel` variations (4 by 4) at fixed seeds, into OpenAL buffers, and kept in
`SoundCache/` in the data folder (a file per recipe keyed by the format, rate, levels, variations, name, the build's
configuration, and the sound code: a hash of the source files under the `Audio/` folders of the recipe's assembly and
the engine, written as assembly metadata by `Directory.Build.targets`, so a later start reads it in about 0.01 s and
only a change to sound code renders it again; `Engine:Audio:Cache` turns it off). An event plays the variation nearest
its level, never the same one twice running, with its own gain and pitch. A `StreamRecipe` (a beam, wind, fire) makes a
`SoundGenerator` that the audio thread runs live, 512 samples at a time with four blocks queued (about 43 ms at 48 kHz),
following the level the game gives it.

Game code queues one-shots with `AudioService.Play` (a gain, a pitch, and a reach) and holds streams with `Sustain` (a recipe
and a key: a stream plays while it is sustained, for 0.1 s after the last, then fades out over 80 ms); `Publish`, once a
main-loop iteration after the frame, hands them and the listener (the camera) to the audio thread (`AudioExchange`: one
lock around fixed arrays, at most 512 waiting events and 128 loops). A render thread (`RecipeRenderer`, below normal
priority) renders the one-shot recipes in the order registered; the audio thread (`AudioMixer`, every 4 ms) places the
listener, starts one-shots, refills streams, frees ended voices, and puts the next rendered recipe into buffers. Sounds play
while recipes render; a one-shot whose recipe is not in buffers yet is dropped as late and counted. A recipe that throws is
logged and stays silent, and a stream whose generator throws is ended; the other sounds play on. A pool of
`Engine:Audio:MaxVoices` (64) OpenAL sources plays everything, each up to a gain of 4 after distance; a sound whose gain at
the listener is under 0.002 is never started, and when every voice plays, a louder sound takes the quietest one-shot's voice.
Once a second the mixer asks the device whether its output is still there and moves to the default output when it went.
Sound is cosmetic and never sent over a session. Recipes need no determinism across machines (`MathF` is allowed), but every
filter and resonator state is flushed below 1e-20 once a block, since .NET cannot flush denormal floats. A recipe's
samples are mono 16-bit (a sound from a file keeps its rate, and its two channels when kept stereo). With no device, or `Engine:Audio:Enabled` false, no thread starts and every call does nothing. The engine's own
recipes are the menu's clicks and two test sounds (`EngineSounds`); a game registers its own in an `IRunStartup`. Rendering
34 one-shots of 520 variations took 0.8 to 2.7 s at start in Release and 12 to 15 s in Debug, on the render thread, 26.7 MB
of buffers.

Sounds can also be read from files, and music streams from them (`docs/sound-files.md`). A `FileOneShotRecipe` (1 to 16
files, each a variation) or `FileLoopRecipe` (one file, played while sustained, round its loop points) is a recipe like
any other; the sound render thread reads its WAV or Ogg Vorbis files whole (at most 30 s, a loop 120 s) and the audio thread
puts each into a buffer at its own rate, a stereo one kept stereo at the listener when the recipe says so. Music
(`MusicPlayer`, `AudioService.Music`) is tracks declared by name, one heard at a time: a music thread decodes ahead
into rings, the audio thread plays two decks of its own beside the voice pool and cross-fades them at equal power, and
a loop goes round its points with no gap through a second decoder waiting at the loop's start. The volumes are the
master (the listener's gain), and under it Effects (every voice of the pool) and Music (the decks). A file that cannot
be read is logged at Error and plays nothing.

### Vulkan, shaders, threads, and GPU memory

- **Vulkan 1.3 is the minimum** on the loader and the device. Dynamic rendering and synchronization2 are core;
  `DeviceRequirements` lists every required extension and feature, including `scalarBlockLayout`, `multiDrawIndirect`,
  `drawIndirectFirstInstance`, `textureCompressionBC`, and `D32_SFLOAT` as a depth attachment. RADV, NVIDIA, Intel, and
  MoltenVK 1.4 all expose 1.3.
- **Shaders are GLSL files compiled at runtime** with Shaderc for Vulkan 1.3, with `#include`, and cached under the data
  folder. The cache key covers the expanded source and the compiler settings.
- **Rendering runs on its own thread**, so no presentation stall can stop the simulation or networking, which a
  host-authoritative game needs. The render thread pulls one frame at a time from the main thread (`FrameExchange`); ECS
  stays on the main thread. Frame systems and mods' `OnUpdate` pause while no frames are drawn; fixed steps never do.
- **GPU memory uses Itera's own block sub-allocator** (`Rendering/Memory/`), with no VMA binding. Blocks of 64 MB per
  memory type, or an eighth of a smaller heap; a dedicated allocation for resources of half a block or more; buffers and
  optimal-tiling images in separate blocks; host-visible memory mapped once. The main thread writes upload data into the
  staging region of the frame slot it builds (`StagingBuffer`); the render thread records the copies.
- **Structs that shaders read use scalar block layout** (`layout(scalar)`, the Vulkan 1.2 `scalarBlockLayout` feature), so
  they match .NET's layout without padding. Each has a test that checks it against the compiled SPIR-V. Entity data
  reaches the GPU as a copy made each frame; Arch chunk memory cannot be handed to Vulkan directly.
- **Validation.** Debug builds enable `VK_LAYER_KHRONOS_validation` with synchronization and best-practices checks, and
  validation errors reach the log at Error. `.claude/skills/vulkan/validate.sh` runs the game and fails on any validation
  warning or error.

## 9. Open work

Engine work that is known and not done:

- **The mesh path, later.** Built: glTF models with textures (decoded at load or compressed offline), skinning, clip
  fades, mesh colliders, and matches without voxel terrain (`samples/Itera.MeshSample`). Not built: moving colliders
  (doors, platforms) for the mover, hands and feet placed on walls (IK on mesh rigs), walking on walls and ceilings,
  loading models during play, MikkTSpace tangents, clips from other files by joint name, a bridge from a mesh rig to
  `Skeleton` for foot IK and aim, `KHR_texture_transform`, morph targets, and GPU culling (blended materials came
  with the next item).
- **Rendering a game can extend** (finished 2026-10-10): the look is data (`RenderLook`), the sun moves, with night,
  mesh assets' blended materials are drawn sorted per object, a game adds passes of its own at five stages
  (`IRenderPass`), a camera's lens may be orthographic, and a game draws views into images (`RenderViews`, the starter's
  minimap; a mesh material may show one, the mesh sample's screen). Not built: image views with shadows and particles,
  split screen, HDR rendering, order-independent transparency, transparent voxel materials, a physically based sky, spot
  lights, and shadowed point lights (each with the choices offered in `docs/rendering-extension.md`). A low sun's shadow
  maps cost more (the starter's start view at 2560x1440: 0.15 ms at the engine's sun, 0.19 ms at 30 degrees, 0.36 ms at
  15, 0.40 ms at 5; their longer reach adds 0.01 to 0.03 ms of that); at night they cost 0.015 ms.
- **Typed text, a menu a game replaces, and the UI toolkit** (built 2026-10-11): a game's text box and Escape screens,
  its own menu or pages and Settings rows in the engine's, and a toolkit that lays out, drives, and draws its screens
  under one sizing rule with a player's UI scale (`docs/ui-toolkit.md`). Not built: gamepads (the research is in that
  file), IME composition (GLFW 3.4 has none), nine-slice images, a safe-area margin, rich text, right-to-left text, and
  drawing again only the elements that changed. The UI pass blends in linear light, so see-through UI shows more of the
  world than its colours say (left as it is).
- **Sound files and music** (built 2026-10-11): sounds read from WAV and Ogg Vorbis files (NVorbis 1.0.0-rc.2), played
  as one-shots or looping while sustained, round the loop points their files name; music streamed from files on a
  thread of its own and cross-fading at equal power on two decks, the menu's track an option; Master, Music, and Effects
  volumes on a Sound tab; the mesh sample's sounds and music made in code (`docs/sound-files.md`). Not built: EFX
  filters and reverb, Doppler, playlists, a track's own volume, ducking, sounds from files past 120 s that are not
  music, more than two channels, and Opus, FLAC, or MP3.
- **Physics a game sets** (built 2026-10-11): gravity and each body's share, the solver's settings, physics materials with
  an exact bounce on bodies, mesh colliders, and voxel materials, characters gripping by the ground, layers and groups,
  trigger zones, and impact events (`docs/physics-materials-and-groups.md`). Not built: materials per mesh triangle and
  per lifted piece, a mover filtered by layer, impacts while a pair touches, mesh and swept triggers, rays by layer,
  gravity on the wire, down other than -Y for characters. The plain step costs what it did; every body marked and bouncy,
  7 to 11 % more while bodies touch.
- **Large maps** (built 2026-10-10): a map's size set in its generator's grid (up to ±160 km, up and down too), voxels of
  1 cm to 1 m, positions kept local to a match's origin with absolute types (`WorldPoint`, `VoxelCoord`) wherever
  they cross it, and moves of the origin following the camera (`OriginShiftSystem`, `OriginShifts`; the renderer,
  particles, and sound follow them), positions in messages absolute, and the starter's map 8 km across (broad relief
  past 1 km), seen whole (an 8 km view over nine levels, with lighter haze), and views to 8 km (levels 8 to 10;
  measured above, "Views to 8 km"), a third shadow cascade for the distance as an option (`DistantShadowMetres`), a nav grid that moves with the players over any map (`Engine:Navigation:WindowCells`),
  and columns that store only the layers round their ground and edits, so a map's height costs memory only where
  something is, with the renderer's level choice counting a column's height (a camera high over the ground meshes no
  fine level under it). Left: separate physics simulations for far peers, a tint map that moves with the view,
  atmospheres by region, and navigation windows per group of players, for a game that needs them.
- **What replication does not do yet** (`docs/replication.md`): loose bodies as replicated kinds (a client moving its copy of each to the host's poses), a group far from
  every player sent as one record, a short history of positions for judging hits, and edits stamped with the host's
  tick.
- **An exact method for the joins between levels of detail**, closing the holes of a voxel or two where the levels disagree on
  which cells the surface crosses.
- **Voxel objects finer than the terrain** (built 2026-10-12): kinds of finer voxels placed by the generator, drawn as
  models near the camera and as copies at their own size in the world's coarse levels with dithered fades between them,
  met by characters, bodies, rays, and navigation, damaged by edits, and cut into falling parts; the starter's forests
  and boulders (`docs/voxel-objects.md`). Not built: lifts that take objects' voxels into the piece, a support check
  when the ground is dug from under an object, felled parts that stay, objects that move, kinds read from files, far
  forests past a kind's highest copied level, and a hand-over level per kind. Performance to win back: the world pass
  looking down over a forest from 40 m (the starter's `forest-high`, 4.72 ms against the 4 ms budget), where GPU
  culling of the models, fewer far copies' triangles, or a shorter hand-over fade would each help.
- **Underground play**: lighting without the sky, connectivity or occlusion culling for caves, layered navigation, and the
  levels chosen by three-dimensional distance. The sky field's rebuild after edits could be local with summaries kept with
  the columns.
- **Low-end hardware.** Presets for weak GPUs (no MSAA, one cheap shadow cascade, coarser meshes) were measured on an
  integrated GPU and not built; shadow and material quality rows for the Graphics tab wait for them.
- **Mod API hardening.** A stable, versioned service surface for mods instead of the raw service provider, dependency
  ordering between mods, and a mod project template.
- **Continuous integration** that builds and tests on Linux, Windows, and macOS.
- **Edit log**: snapshots of edited chunks so old edits and put-back pieces leave memory, and saving on a timer, off the main
  thread.
- **Particles**: trails as ribbons, soft particles (fading where a sprite meets a surface), sprites at half resolution for
  heavy smoke, and blood-style decals.
- **Sound**: filters by distance and occlusion (OpenAL's EFX), reverb by place, Doppler, and voices (recordings play
  from files since 2026-10-11, `docs/sound-files.md`).
- **Skia's Vulkan backend** for the UI, and dirty rectangles narrower than whole rows.
- **Steam on macOS arm64** (the package has no osx-arm64 build of the managed wrapper) and the two-account join test.

## 10. Platform support

Targets: Linux, Windows, macOS, x64 and arm64. `Game` declares the runtime identifiers (`IteraSupportedRids`) so
`dotnet publish -r <rid>` works without edits.

Verified so far: Linux only (Ubuntu, Wayland, AMD RX 5700 XT on Mesa RADV, .NET SDK 10.0.112). Steam runs on Linux x64;
macOS x64 and Windows x64 have its libraries but are unverified, and arm64 plays without Steam. Windows and macOS are
unverified until a CI matrix or a physical machine runs the build and the game. What is in place for them, and what is not:

- Every native package ships binaries for all three platforms, including MoltenVK for macOS. But no package ships a Vulkan
  loader for macOS: GLFW for macOS looks only for `libvulkan.1.dylib`, Silk.NET.GLFW 2.23 has no binding for
  `glfwInitVulkanLoader`, and Silk.NET.MoltenVK.Native ships the driver and its ICD file but no loader, so on a Mac without a
  system loader the window step ends the run (exit code 1). The fix, to be made and checked on a Mac: load
  `libMoltenVK.dylib`, create `Vk` on it, and pass its `vkGetInstanceProcAddr` to `glfwInitVulkanLoader` (a P/Invoke into the
  same `libglfw.3.dylib`) before any windowing call; or ship the Khronos loader in an app bundle.
- On macOS the Vulkan context enables `VK_KHR_portability_enumeration` and, when the device exposes it,
  `VK_KHR_portability_subset`. The context requires Vulkan 1.3, which the bundled MoltenVK 1.4 provides. Presentation runs on
  the render thread, not the main thread; MoltenVK allows that, but it has not been run on a Mac yet.
- Paths use `Path.Combine`; string comparisons are ordinal; the shader cache and the data folder live under
  `Environment.SpecialFolder.LocalApplicationData`.
- `.gitattributes` normalises line endings so checkouts hold the same bytes on every platform.

Linux notes: Wayland cannot set window position or icon. Pointer lock for mouse look works on Wayland (Sway, GLFW 3.4.0
through Silk.NET 2.23): GLFW offers raw motion, so the pointer uses `CursorMode.Raw`; movement keeps arriving at and past
the window edge, the main thread allocates nothing extra while the mouse moves, and the pointer reappears where it was on
release. Not yet seen: losing focus while the pointer is captured (the code releases the pointer then). A left click
captures the pointer and Escape frees it. The validation layer (`vulkan-validationlayers`) and `vulkan-tools` are installed
on the development machine, so Debug builds run with validation on, including synchronization and best-practices checks
(set through `VK_EXT_layer_settings` when the layer offers it). They are optional elsewhere; Debug builds warn when the layer
is missing and continue.

Tooling: LazyVim with the `lang.dotnet` extra (OmniSharp, csharpier, netcoredbg). csharpier is also a local dotnet tool
pinned in `dotnet-tools.json`, so `dotnet csharpier check .` works after `dotnet tool restore` on any machine. The solution
is in the classic `.sln` format because OmniSharp does not open `.slnx`.
