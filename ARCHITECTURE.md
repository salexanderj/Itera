# Itera architecture

This file describes Itera: what it is, what it is built from, how the code is arranged, how a game is composed on it,
which design directions are fixed, and what remains open. It is roadmap-level. Bugs and small tasks do not belong
here. The rules for working in each area are in `.claude/rules/`, and the instructions for a clone are in `CLAUDE.md`
and `GAME.md`.

## 1. What Itera is

Itera is a framework for 3D games in .NET 10 with a Vulkan renderer, an Arch ECS, Bepu physics, peer-to-peer sessions,
and mod support. A game is made by cloning the repository and growing the starter game in `src/Game` into its own.
Engine changes that a clone makes go back to Itera, and later engine changes come from Itera with a pull.

The engine does not require voxels in a game's design, but it supports them well. The world can be a destructible
volume of voxels with a stored density. It is generated from a seed on worker threads and streamed in rings of detail
around the players. It is edited with integer geometry that every peer replays alike, and drawn as smooth triangle
meshes.

A game also draws models read from glTF files, and extends the renderer. It sets the look and the sun each frame and
adds GPU passes of its own at five places in the frame. It gives a camera an orthographic lens, and draws views of the
world into images that its HUD or a mesh's material shows. Section 5 describes these, and section 8 the raster
pipeline under them.

Itera grew out of a game, and some figures in this file are measurements from games built on it. The dated measurements
and accounts that once stood in this file are in `docs/records/architecture-structure.md` and
`docs/records/architecture-directions.md`.

The numbers that shape the engine's technical decisions:

| Quantity | Value |
|---|---|
| World voxel edge | 1 cm to 1 m at level 0, set by the generator's `VoxelGrid`. Level k is twice level k-1. Models have a unit of their own |
| World extent | Whole level-0 chunks a game sets in its generator's grid, up to ±160 km each way, up and down too |
| Voxels in a dense grid of 10 cm voxels over ±512 m | About 1.5 x 10^11, too many to store (see "Voxel storage" in section 8) |
| Simulation rate | 60 fixed steps per second (`Engine:FixedStepHz`) |
| Players | Up to `Engine:Session:MaxPlayers`, peer to peer, the host authoritative |
| View distance | Set by `Engine:Renderer:ViewDistanceMetres`, up to 8 km, over the grid's levels |
| Render budget | The world pass at most 4 ms on a mid-range desktop GPU at 2560x1440 with shadows and materials |

Notes on outside projects that were consulted as references, not templates, are in `docs/references/`. The research and
decisions behind the engine are in `docs/`:

- `docs/framework-plan.md`: how the framework was made from the game it grew with.
- `docs/framework-limits.md`: what the engine blocks today, and the order of the work.
- `docs/rendering-redesign-brief.md` and `docs/rendering-redesign.md`: the outside review that began the renderer's
  redesign, and the redesign's decisions, measurements, and plans.
- `docs/rendering-extension.md`: what a game can change in the renderer, the choices offered for each, and why each was
  chosen.
- `docs/voxel-structures.md`: 64-trees, OpenVDB, NanoVDB, and other voxel structures compared with Itera's.
- `docs/voxel-objects.md`: voxel objects finer than the terrain.
- `docs/edit-log-compaction.md`: what a long edit log costs and the methods compared.
- `docs/map-size-and-origin.md`: the map's size and the moving origin.
- `docs/replication.md`: replicating entities from the host to its clients.
- `docs/anti-aliasing.md`: the research behind SMAA 1x before FSR 1.
- `docs/procedural-audio.md`: sound made in code, after Farnell's *Designing Sound*.
- `docs/sound-files.md`: sounds and music read from WAV and Ogg Vorbis files, the choices made, and the measurements.
- `docs/physics-materials-and-groups.md`: gravity, the solver, physics materials, layers and groups, triggers, and
  impact events, the choices made, and what the reviews found.
- `docs/ui-toolkit.md`: the UI toolkit.
- `docs/records/`: the dated measurements and accounts moved out of this file.

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
| NVorbis | 1.0.0-rc.2 | Decodes Ogg Vorbis sound files and music (`Audio/Files/VorbisDecoder`) | MIT and managed. A release candidate is pinned for its fixes over 0.10.5, whose seeks land off and can throw (`docs/sound-files.md`). It allocates as it decodes, so it runs on the sound render and music threads. |
| Silk.NET.Maths | 2.23.0 | Vector and matrix types used by Silk.NET APIs | Required by the windowing API surface. |
| Arch + Arch.System | 2.1.0 / 1.1.0 | Entity component system | Archetype-based, allocation-free queries, fast iteration. Game logic is systems over components. |
| BepuPhysics + BepuUtilities | 2.5.0-beta.29 | Rigid body physics: loose bodies and a game's bodies against the voxels and mesh colliders, with the game's materials, layers, triggers, and gravity. Characters move with Itera's own mover | Fast, SIMD-based, actively maintained. The 2.4 stable line only targets net6.0 and is old. |
| SkiaSharp + SkiaSharp.NativeAssets.Linux | 4.152.1 | 2D UI drawing | Solid text and vector rendering. Linux needs the explicit native package; Windows and macOS natives come with the main package. |
| BCnEncoder.Net | 2.3.0 | Block-compressed texture encoding, in `tools/Textures` only | Pure managed BC1 to BC7 encoder. |
| SharpGLTF.Core | 1.0.7 | Reads glTF 2.0 models (`Assets/GltfImport`), at load only | MIT, no dependencies, reads .gltf and .glb with skins and animations. The engine copies what it needs into its own arrays. It cannot read Draco or meshopt compression, which the importer refuses by name. |
| LiteNetLib | 2.1.4 | UDP transport and reliability for sessions over LAN and direct addresses | Small, dependency free, widely used for .NET games. |
| Steamworks.NET | 2024.8.0 (Steamworks SDK 1.60) | Internet play: Steam's networking sockets and relays (no port to open), friends-only lobbies, rich presence for joining from the friends list | A thin, faithful wrapper of the C++ API (MIT). The package has no native libraries. Valve's `steam_api` for Linux x64, macOS (universal), and Windows x64 from the same release are in `src/Itera.Engine/Native/Steam/` and copied next to the game. |
| Microsoft.Extensions.Hosting / DependencyInjection / Logging / Options | 10.0.12 | Configuration, DI, logging | Standard .NET application plumbing. |
| Serilog + Extensions.Hosting + Settings.Configuration + Sinks.Console + Sinks.File + Sinks.Async + Enrichers.Thread | 4.4.0 / 10.0.0 / 10.0.1 / 6.1.1 / 7.0.0 / 2.1.0 / 4.0.0 | Console and rolling log file behind `ILogger` | Standard, well tested. Per-run files, size rolling, async writes, per-category levels from configuration. Engine code only sees `Microsoft.Extensions.Logging`. |
| xunit | 2.9.3 | Tests | Default .NET test framework. |

Deliberate omissions:

- The `Silk.NET` metapackage. It pulls every binding Silk.NET has.
- `Arch.Relationships`. It depends on Arch 1.2 and does not work with Arch 2.x.
- `Arch.System.SourceGenerator`. Version 2.1.0 generates `[Query]` code that fails this build. It gives a nullable
  warning under warnings-as-errors, and duplicate public attribute types when two projects use it. The fixes exist only
  on its unreleased `master`.
- NativeAOT and trimming. Mods are dynamically loaded assemblies that need the JIT and reflection. Steam builds use
  composite ReadyToRun instead, published as a single file (`tools/steam/upload.sh`). The install folder's layout is in
  `tools/steam/README.md`.

## 3. Solution structure

```
Itera.sln
Directory.Build.props        shared compiler and runtime settings for every project
Directory.Build.targets      writes the sound code hash (see "Sound is made in code")
Directory.Packages.props     every package version
src/Itera.Modding            the mod contract: IGameMod, IModContext, ModManifest, ModManifestReader, FrameTime, FixedStepTime
src/Itera.Engine             everything that is not specific to one game
src/Game                     the starter game: the executable and its composition, which a clone grows into its own game
samples/Itera.SampleMod      reference mod, copied into the game's Mods/ folder on build
samples/Itera.MeshSample     a game without voxel terrain, with a README
tools/benchmark              scripts that run the GPU benchmark (Engine:Benchmark) and compare runs
tools/control                game.py: starts a game from a copy of the build and drives it through the control port
tools/steam                  upload.sh: tests, publishes, and uploads a build to a Steam branch
tools/Textures               makes the world materials' texture sets (compressed DDS files) and models' compressed textures
tools/sounds                 make_sounds.py: the sound files the engine's tests and the mesh sample read, made in code
tools/models                 the model toolkit: Python recipes Blender runs with no window, and build.py
tools/ModelCheck             checks a model as the engine reads it
tools/ModelView              draws a model with the engine itself
tools/tests                  timings.py: runs the tests and lists the slowest classes and tests
tests/Itera.Modding.Tests    manifest parsing and validation
tests/Itera.Engine.Tests     the engine, with subfolders that mirror src/Itera.Engine
tests/Game.Tests             the game's own tests
tests/Itera.MeshSample.Tests the mesh sample played without a window
tests/Itera.ModelView.Tests  the model stage played without a window
tests/Shared                 helpers compiled into the test projects that use them
```

The engine's tests run without the starter. `tests/Itera.Engine.Tests/Matches/EngineComposition.cs` is the least a game
adds to the engine: a floor generator, and one fixed and one frame system. The test projects, their helpers, and what
each test class checks are in `.claude/rules/tests.md`.

Dependency direction:

```
Itera.Modding   <-- Itera.Engine   <-- Game
      ^
      +-- Itera.SampleMod (and every other mod)
```

Rules:

- `Itera.Modding` references nothing from the engine. Mods reference only `Itera.Modding`. A mod build never pulls in
  Silk.NET, Bepu, Skia, or Arch, so the host can change any of those without breaking mods.
- `Itera.Engine` references `Itera.Modding` because the loader needs `IGameMod`. The loader lives in the engine so a
  future headless server can reuse it.
- `Game` references both and owns the composition root. Change engine files only to fix or extend the engine, and send
  those changes back to Itera. Keep game code in the game's folders, so a pull from Itera merges cleanly.
- The game names itself to the engine through `Engine:Application` (`Name`, and `DataFolder` for logs, saves, the shader
  cache, and `settings.json`).

`Itera.Engine` has one folder for each area. The table gives what each holds and the rules file that lists its files
and classes. `.claude/rules/engine.md` covers the root files and any folder without a row of its own.

| Folder | Holds | Rules file |
|---|---|---|
| `Time/` | The game clock, with its clamped frame delta, and the fixed-step accumulator | `engine.md` |
| `Windowing/` | The GLFW window: events, framebuffer size, refresh rate, exit request | `rendering.md` |
| `Input/` | Key, button, movement, and scroll events as one snapshot per frame and one per fixed step; pointer capture; key bindings | `ui-input.md` |
| `Rendering/` | The Vulkan context, swapchain, frame renderer, render thread, and every pass, with its memory, pipelines, GPU structs, voxel looks, materials, mesh passes, and shaders | `rendering.md` |
| `Meshing/` | The CPU mesher that turns voxel blocks into triangle meshes | `rendering.md` |
| `Particles/` | Particle emitters, recipes, and stock effects; the GPU side is in `Rendering/` | `particles.md` |
| `Native/` | Valve's `steam_api` library for each platform | `sessions.md` |
| `Audio/` | The sound service, mixer, recipes, files, music, synthesis, and devices | `audio.md` |
| `Loading/` | The loading screen and the loading tasks | `ui.md` |
| `Ecs/` | `EcsWorld` with its fixed and frame system groups, and the Debug-only checks of Arch calls | `engine.md` |
| `Components/` | The ECS components the engine's systems share | `systems.md` |
| `Systems/` | The systems a game registers: the player, cameras, physics glue, streaming centres, replication, and the benchmark | `systems.md` |
| `Maths/` | Deterministic maths, rotations, and the two frames of positions | `animation.md`, `engine.md` |
| `Assets/` | Mesh assets read from glTF files, with skinned models' rigs and clips | `mesh-assets.md` |
| `Animation/` | Skeletons, poses, clips, aim, and foot placement | `animation.md` |
| `Physics/` | The Bepu simulation, a game's bodies and colliders, the character mover, and loose bodies | `physics.md` |
| `Voxels/` | The voxel world: storage, streaming, edits, the edit log, and models | `voxels.md` |
| `Voxels/Generation/` | The world generation toolkit | `voxels.md` |
| `Objects/` | Voxel objects finer than the terrain | `objects.md` |
| `Navigation/` | The navigation grid and flow fields | `navigation.md` |
| `Networking/` | Sessions, the transports (LiteNetLib and Steam), and the wire protocol | `sessions.md` |
| `Replication/` | What a host sends each client of the entities a game marks | `replication.md` |
| `Spatial/` | `SpatialGrid`, where a set of entities stand by cells over the ground | `spatial.md` |
| `UI/` | The Skia UI surface, its layers, the console, the toolkit, and the menu | `ui.md` |
| `Commands/` | The console's commands | `ui-input.md` |
| `Control/` | The control port | `engine.md` |
| `Modding/` | The mod loader and dispatcher | `engine.md` |
| `Diagnostics/` | Logging, the session header, frame stats, and crash logging | `engine.md` |
| `Matches/` | A match's start, join, and end, its services and steps, and the run-wide startup | `sessions.md`, `engine.md` |
| `Saves/` | Saving a match and listing saves | `saves.md` |
| `Settings/` | The player's settings in `settings.json`, and tunables | `ui-menu.md`, `ui-input.md` |
| root | The options, their validator, the DI registrations, the run's steps, and `GameLoop` | `engine.md` |

## 4. Runtime model

**Composition root.** `Program.cs` builds a host builder (`Microsoft.Extensions.Hosting`) for configuration (`appsettings.json`, environment
variables, `--Engine:Key=Value` arguments), logging, and DI. It then resolves `GameLoop` and calls `Run()` on the main
thread. The host's own `Run()` is not used.

**Services.** Every service has a light constructor and an explicit `Initialize()` method. A run's services are
singletons. A match's services are Scoped: its session, world, physics, navigation, save, ECS world, the game's systems
and stores, the HUD layers, and the loading tasks. Each match is a DI scope of its own (`MatchServices`), made when it
starts and disposed when it ends, so one run plays many matches (see "A run plays many matches" in section 8). A
singleton never takes a scoped service in its constructor. `Program.cs` turns on DI's scope checks in every
environment.

Options are validated when `GameLoop` is resolved (`EngineOptionsValidator`, and `UnknownOptionKeys` for misspelt
keys). A bad value stops the game with exit code 1.

**The run's steps.** The order is written in two places, each step naming its start and its shutdown action. The run's
is `GameLoop.CreateSteps`:

1. network (LiteNetLib's log; no socket)
2. Steam (the Steam API when the client runs and the game has an App ID; optional)
3. settings file (`settings.json` read over the options)
4. window
5. Vulkan context
6. swapchain
7. frame sync
8. GPU memory (the allocator and the staging buffer)
9. input
10. shader compiler, with a smoke-test compile
11. audio (OpenAL or a silent fallback, and the engine's sound recipes)
12. settings (the player's volume and render scale applied; `settings.json` written at shutdown)
13. renderer (every pass, and the timestamp query pool)
14. UI (the surface, the engine's console commands, and the menu for the input)
15. game (`IRunStartup.Start`: the game's run-wide registries, recipes, keybindings, on/off settings, and image views)
16. mods
17. mesh assets (the models declared, read)
18. mesh assets on the GPU
19. image views (each declared view's records, images, and sets)
20. render passes (the game's own)
21. audio thread (the music tracks' headers are read first, on the main thread)
22. render thread
23. control port (off unless `Engine:Control:Enabled`; last, so it stops first)

Steam starts before the Vulkan instance, whose layers it adds. Every Vulkan object is made in these steps. Starting or
ending a match makes no Vulkan call, and the render thread runs from its start to the exit.

**The match's steps.** `MatchServices` holds the match's list:

1. session (offline and hosting settle the match at once; a client connects, and `SessionService.PollJoin` carries the
   join on once a loop iteration, while the menu shows its progress, until the host's edit log is downloaded)
2. origin (the match's first origin, from the game's start pose)
3. physics
4. voxels (memory and worker threads, for the match's seed)
5. joined world (a client loads the host's edits, a named save its own)
6. navigation
7. voxel physics
8. objects
9. save (the last save written at the match's end)
10. tint (the match's tint map for the particles' colours)
11. world on the GPU
12. match UI (the match's console commands and the HUD layers)
13. ECS world and systems
14. save sections (a named save's game state handed to the game's `ISaveSection`s before the first step)
15. mods (`IGameMod.OnMatchStarted`)
16. loading (the loading screen takes the match's tasks)

The steps after the session begin once a frame saying "Starting..." has been shown. The session settles the match before anything
reads the seed, and `SessionService.Match` throws until then. A game's generator and start pose take the seed from it.
The ECS world comes after physics and voxels so that systems can use them in `Initialize`. A match ends with its
session's goodbye, its steps in reverse, and its scope disposed. The run then puts back what it kept of the match: the
view, the title, and the particles. `.claude/rules/sessions.md` describes the steps in more detail.

**Main loop (main thread).** Each iteration does this, in order:

1. Handle the window events that wait. When no frame is requested, sleep on a managed event until the next fixed step is
   due. A frame request from the render thread ends the sleep at once. GLFW's own timed wait is not used, because on
   Wayland it can spin at full load inside the call.
2. Read the clock, with the delta clamped to 0.25 s.
3. Run Steam's callbacks, and poll the session. A joining client's join goes on, a host answers hellos and sends
   joining clients the world, and a client applies the host's edits.
4. Update input: lock or release the pointer.
5. Take `MatchControl`'s requests: a client whose session ended, a leave, or a start. A start ends the match now first.
   Matches start and end here, between iterations' work.
6. While a match's world exists, update the voxel world. It installs finished columns, unloads far ones, and queues
   new ones, a bounded number each iteration.
7. Run the console's commands (`DevConsole.RunPending`). They run on the main thread between fixed steps, never inside
   a query, and also while no frames are drawn.
8. Run the control port's requests (`ControlService.RunPending`).
9. While a match's world exists, run the due fixed steps: at most `Engine:MaxFixedStepsPerFrame` for each iteration.
   Excess time is dropped. A match's start restarts the count at tick 0.
10. Build a frame when the render thread has asked for one.

Each fixed step does this, in order:

1. Take the input since the last step (`InputService.BeginTick`, read by fixed systems through `ITickInput`).
2. Run the ECS fixed group. Systems receive `FixedStepTime`. Then the parts the step's edits cut off objects become
   bodies (`Fallen.Make`).
3. Apply the large edits that wait (`ApplyPendingEdits`).
4. Bring the Bepu statics near dynamic bodies up to date with the step's edits and the bodies' positions
   (`VoxelColliders.Update`).
5. Step the physics.
6. Take the loose bodies' new poses for the character mover (`LooseBodyVoxels.Refresh`).
7. Call `IGameMod.OnFixedUpdate`.
8. Take the fields that the navigation thread finished.
9. Send the session's updates. A host sends each joined client the edits made since the last step, in order.
10. Let the autosave capture the match as the step left it (`Saves.AfterFixedStep`).

The loop ends when the session of a match the options started ends, because the host quit, the connection was lost, or
the host sent something it could not accept. Any other match's end goes back to the menu. An exception in the
iteration's work while a match the menu or the console started is on ends that match unsaved (`MatchFlow.EndAfterFault`,
logged at Error). It owes the render thread the frame it took the request for (`FrameExchange.RequestTaken`), and the
run goes on at the menu. A device failure (`VulkanException`) and an exception in a match the options started still end
the run.

**Building a frame.** If the render thread asked for a frame and no match's world exists (the menu, a join, a match's
start), the iteration builds one from the loading screen's ring with the UI over it. It uploads and draws nothing of a
world. Otherwise it builds a frame in this order:

1. Take the input snapshot of the events since the last frame (`InputService.Frame`), and clear `DebugLines` and
   `ModelInstances`.
2. Start the event lights (`PointLights`).
3. Run the ECS frame group. Systems receive `FrameTime`, including `Alpha`. One of them puts the active camera in
   `RenderView`.
4. Call `IGameMod.OnUpdate`.
5. Draw the UI (`UISurface.Draw`). Each layer updates, and the surface is drawn again only when a layer changed,
   appeared, or went.
6. Start the image views due this frame (`RenderViewPass.BeginViews`).
7. Update the world's meshes (`WorldMeshes.Upload`). It follows the voxel change list, starts meshing nearest the view
   first, stages the finished meshes, and writes the frame's draw lists into the slot's regions. Its reads are limited
   to a share of the frame's time.
8. Write the mesh assets' lists (`MeshAssetDraws.Write`), end the views (`RenderViewPass.EndViews`), and write the debug
   lines, the particles (`ParticlePass.Write`), and a game's render passes (`RenderPasses.Write`).
9. Hand the frame over with the camera and the counts, through `FrameExchange.Publish`.

Between taking the request and publishing, frame systems may write the GPU memory of the request's frame slot. Nothing
else on the main thread touches it. `.claude/rules/rendering.md` has the details of each part.

**Control port (`ControlService`, `Engine:Control`).** A development tool, so a check needs no restart. It is off unless
`Engine:Control:Enabled` is true, and it listens on TCP 127.0.0.1. `tools/control/game.py` drives it. Any program on the
machine can connect, so shipped builds never turn it on. Each line a tool sends is a JSON request and gets one JSON
reply line. A line that is not a JSON object ends its connection, and a last line without its end is never run, so a
web page's request runs nothing. A browser sends such requests to any address a page names.

A thread accepts connections, and a thread for each connection reads its requests one at a time. Each request is queued
for the main thread, and the loop is woken (`WindowService.Wake`). Only `log` is answered on the connection thread, from
`ControlLog`. The main thread starts the other requests in `RunPending`, between fixed steps where console commands run.
The port obeys the render thread's rule: the main thread only queues screenshots (`Screenshots`) and shader reloads
(`ShaderReloads`), which the next `RenderFrame` carries. The `run-game` skill lists the port's operations.

- **Screenshots.** On the render thread, `ScreenshotPass` copies the swapchain image into a readback buffer, after the
  UI or before it. A worker writes the PNG with Skia (`ScreenshotEncoder`).
- **Shader reloads.** Before recording the frame, the render thread compiles every pass's shaders from
  `Engine:Renderer:ShaderSourceDirectory` (`IReloadsShaders`). It waits for the device and makes the pipelines again
  only when all of them compiled. An error keeps the old pipelines and is reported with its file and line.

**Loading screen (`LoadingScreen`, `Engine:Loading`).** From the loop's first iteration, the loading screen covers the
picture until every `ILoadingTask` is done. The engine's tasks are Sounds, Physics, World, Objects, Navigation, and Warm-up. The
game's own tasks come after them. Loading is a phase of the loop, not a wait before it. Streaming, uploads, fixed steps,
the session, and the console run as usual, because most of what loading waits for moves on only while they do. The
game gets empty input snapshots meanwhile, and Escape and the console still work.

The frame carries a `LoadingDraw`. The render thread still records the mesh copies. It skips the particles, whose clock
stops (`ParticlePass.SkipSimulate`), and draws the loading shader over the whole swapchain image instead of the world
(`LoadingPass`), then the UI. After
`MaxSeconds` the screen gives up waiting for a task and logs a Warning naming it. After a reset of the world it covers
the picture again (`LoadingScreen.BeginAgain`) and waits for every task afresh. `.claude/rules/ui.md` has the details.

**Window modes (`Engine:Window:Mode`).** A run draws into a window. Or it draws offscreen (`Offscreen`) into a headless
surface with nothing on the desktop, for screenshots and checks. Or it runs with no graphics (`None`): no window,
Vulkan, or render thread, while the fixed steps and the frame group run. Nothing paces a headless present, so offscreen
frames are capped at `Engine:Window:OffscreenFps`. The world uploads a share each frame, so a low cap makes a view
load slowly. `.claude/rules/rendering.md` has the modes' details.

**Render loop (render thread).** Each frame, the render thread does this:

1. Wait on the frame slot's fence and reset its command pool (`FrameRenderer.BeginFrame`), and acquire an image.
2. Ask the main thread for a frame (`FrameExchange.Request`), and wait for it.
3. Record the frame, submit it, and present it (`FrameRenderer.Present`).

The frame's commands run in this order:

1. The copies from the slot's staging region into the mesh heap, and a new sky field copied into its image.
2. The lights sorted into the view's clusters, and the particle passes when particles can be alive.
3. The world (`MeshPass.Record`): skinning and a game's `Compute` passes, the sun's shadow cascades, the models shown
   whole, the blocks nearest first, the fading models, the mesh assets, a game's `Opaque` passes, the sky, the blended mesh assets, and a game's
   `Transparent` passes. The world is drawn into multisampled images that are resolved as rendering ends.
4. Each image view due (`RenderViewPass.Record`), the particles, and the debug lines.
5. A game's `Scene` passes, SMAA when the world is drawn with one sample a pixel, and FSR 1's upscale when the render
   scale is below 1.
6. A game's `Screen` passes. The world, the views, and a game's passes are left out while the loading screen covers the
   picture.
7. The loading screen while it covers the picture or fades out, and the UI when it changed or shows anything.
8. The transition for presenting, and timestamps (`GpuPass`) around each part of the frame.

Barriers are in the `vulkan` skill's `synchronization.md`. The render thread flushes the slot's regions of the line
buffer and of the mesh passes' buffers when their memory is not coherent, so the main thread makes no Vulkan call for
them. It reads the slot's GPU timestamps from its last frame after the fence wait (`GpuTimer.Collect`). The frame's
`ViewConstants`, built on the main thread from the camera and the image size, go to the passes as push constants.

The swapchain is recreated by one set of rules with no Vulkan call (`SwapchainState`): a resize to a new size, a VSync
change, or an out-of-date or suboptimal result. The device's values are read in `Initialize` or later, never in a
constructor, because they throw until `Initialize` has selected the device.

The request comes right after the render thread's blocking point (present and acquire under FIFO). Frames are built at
the display rate from freshly read input. When presentation blocks, for example while the window is hidden, only the
render thread waits. No request comes, and the main loop keeps running every fixed step. The render thread also sleeps
instead of spinning when the window is minimised.

**Threads.** The main thread runs windowing, input, ECS, physics, the session, and all mod callbacks. GLFW requires it,
macOS strictly. LiteNetLib's own thread only moves packets and keeps connections alive, and what arrives waits for the
main thread's poll. The other threads are:

- **The render thread** alone uses the Vulkan queues, the swapchain, and the per-frame command pools. While it runs,
  the main thread makes no Vulkan call except flushing the staging region it wrote (`vkFlushMappedMemoryRanges`, which
  has no externally synchronized parameter and does nothing on coherent memory).
- **The hand-off** between the two is synchronous. The render thread waits while the main thread builds a frame, then
  records, submits, and presents while the main thread handles events and fixed steps. It does not draw frame N while
  frame N+1 is simulated. Recording is cheap and the GPU works asynchronously anyway, and this keeps latency low.
- **Physics** uses Bepu's own worker threads inside the step, a quarter of the processors (1 to 6), so Bepu does not crowd out
  the voxel workers and the render thread.
- **Sound** has a thread of its own (`AudioService`), which alone makes OpenAL calls once it runs. The main thread hands
  it each iteration's sounds, music commands, and the listener through `AudioExchange` and `MusicExchange`. A music
  thread decodes the music ahead of it (`MusicStreamer`). A sound render thread renders the one-shot recipes.
- **Voxel workers** (`VoxelWorkers`, half of the processors by default, below normal priority, which on Linux changes nothing: every
  thread runs at the process's nice value) generate columns. They
  hand finished columns to the main thread, which alone changes the live voxel world. The same threads free the memory
  of unloaded columns, ahead of any generation, so unloading costs the main thread a few microseconds for each column.
- **Others** are the navigation thread (it fills its grid and builds the fields alone), the edit warm-up's thread, builds a game starts on the thread pool, the
  screenshot writer, and the control port's accept and connection threads.

**Shutdown.** A match that is on ends first. Its session says goodbye, so the peers are told this game has left, and
its steps run in reverse. Then every run step whose initialisation started shuts down in reverse order. Each thread is
given a few seconds to stop, and a step that throws is logged and skipped, so the remaining steps still run.

If the render thread does not stop in time, it is stuck inside a present, for example with the window hidden. The
window and every Vulkan object are then left to the OS, and the other steps still run. The same holds for every other
thread that writes memory the shutdown frees. The voxel memory is left to the OS when a column worker or the edit
warm-up does not stop (`VoxelWorld.LeaveMemoryAtExit`). The OpenAL device is left open when the audio thread does not
stop (`IAudioDevice.LeaveOpen`). Each logs a Warning.

SIGINT (Ctrl+C) and SIGTERM request this shutdown. A second signal more than 0.5 s after the first ends the process at
once (`ExitSignalPolicy`). When the first signal arrives while the loop is inside a mod's callback, a Warning names the
mod and the callback, since a mod that hangs never lets the loop see the request. Every `Dispose` is idempotent. Native
libraries loaded through Silk.NET are not unloaded. Unloading them at exit crashed the process, because their exit
handlers ran after the library was unmapped.

**Units.** Metres and seconds everywhere in simulation and physics. Voxel coordinates (`VoxelCoord`, edits, rays) are
absolute integers in centimetres from the map's middle. Positions kept in metres are local, from the match's origin
(`WorldOrigin`). Points that cross it are absolute `WorldPoint`s (see "Units and numbers" in section 8).

**Diagnostics.** Every run writes one log file in the data folder's `Logs` folder. `Engine:Application:DataFolder` gives
the data folder: a name under `LocalApplicationData`, or a full path. The file receives every level the global minimum
allows, and the console shows Information and above. Serilog sits behind `Microsoft.Extensions.Logging`, so engine and
mod code only use `ILogger`. The `run-game` skill describes the file's header, its lines, retention, and the exit codes.

- `GameLoop` logs the duration of every initialisation step, the reason the loop ended, each shutdown step, and a final
  line with uptime and exit code.
- `FrameStats` logs a line at an interval with the frames built, the times between frames, fixed steps, dropped steps,
  managed bytes allocated on the main and render threads, garbage collections, and GPU time. A Debug line gives the
  frame's CPU times (`FrameCpuTimings`), each as an average and a longest: the wait from the render thread's request
  until the main thread takes it, the main thread's build, and the render thread's recording, present, and fence wait
  with acquire. The allocation numbers are the whole-loop check for the zero-allocation rule. In the steady state the
  main thread's should come only from log lines, and the render thread's should be 0.
- Each system's own zero-allocation test runs with tiered compilation off in the test projects. Every method is then
  optimised at its first call, so no test measures what unoptimised library code allocates.
- `VoxelWorld` and `WorldMeshes` log a line at the same interval from running counts, so the lines cost nothing to
  build. `tools/benchmark/parse.py` reads these lines, so they keep their form.
- Exceptions that escape initialisation or the loop are logged at Critical with the phase, frame, and tick. Shutdown
  still runs, and the process exits with code 1. In a match the menu or the console started, the loop's exception ends
  only the match, at Error.
- `UnhandledExceptionLogger` covers exceptions on other threads and unobserved task faults, and flushes the file before
  the process dies. `Program.cs` uses a Serilog bootstrap logger so failures before the host exists are reported too.
- LiteNetLib's internal log is routed through `NetLoggerAdapter`. Vulkan validation messages map to Error, Warning,
  Debug, and Trace.
- Native crashes, such as a segmentation fault inside a driver, cannot be caught by managed code. The file is flushed to
  disk every second, so at most the last second is lost.

## 5. How a game is composed on the engine

A game is an executable that registers the engine, then itself, and runs `GameLoop`. The starter, `src/Game`, shows how:

- `Program.cs` builds the host as in section 4. It adds `AddIteraEngine(configuration)` and then `AddGame(configuration)`,
  and turns on `ValidateScopes` and `ValidateOnBuild`. It calls `GameServiceCollectionExtensions.ValidateOptions`, so a
  game's option sections are read and checked before the window opens, and a key under `Game` that no section holds is
  named. It installs the crash handlers and returns `GameLoop.Run()` as the exit code.
- `AddIteraEngine` registers the engine's services with `TryAdd`, so a game's later registration of the same service
  type wins. The loading tasks and UI layers are lists a game adds to. The engine registers no ECS system.
- The engine also registers what systems share: `PlayArea`, `PlayerInputBlock`, `WorldResets`, and `MovementKeys`. It
  registers the voxel models, the loose bodies, the bodies as the mover's cells, and the character mover. These work
  with the terrain when there is one, and with the mesh colliders alone when there is not.
- The engine registers the match's identity (`IMatchIdentity`), which the hello, the welcome, and a lobby's match key
  carry. With voxel terrain it is the generator's id and version. Without it, it is the application's name at version
  1. A game may register its own.
- `AddIteraVoxels()` (`VoxelServiceCollectionExtensions`) adds the voxel terrain: each match's `VoxelWorld` and
  `MatchVoxels` (the match's terrain services together, `MatchServices.Terrain`), its `VoxelColliders`,
  `NavigationService`, `IBenchmarkHost`, the voxel objects (`VoxelObjects`, `ObjectWorld`, `ObjectRays`,
  `ObjectDamage`, `FallenParts`), and the World, Objects, Navigation, and Warm-up loading tasks. They come after the
  engine's Sounds task and before the game's own. A game with terrain registers an `IChunkGenerator` and calls it.
- A game without terrain plays matches with no voxel world. It has no voxels, navigation, or voxel physics steps, and
  nothing of a world on the GPU. Its join payload and its save hold the header alone: its match identity, seed, step
  rate, and match data. The session, physics, ECS, UI, audio, particles, mesh assets, mods, the character mover over its
  mesh colliders, and loose bodies work as usual. A game's `ILooseBodyRest` decides what happens to loose bodies once
  they rest, as there is no grid to settle into. `WorldMeshes` meshes and draws its voxel models alone.
- These have engine defaults that a game may replace: `IVoxelPalette`, `IVoxelTintMap`, `IVoxelPassability`,
  `MenuStyle`, and `IMenu`. Without an `INavGridSource` there is no navigation.
- A game adds an `IAtmosphereSource` for its sky, an `INavGridSource` for walkers, `ILoadingTask`s, `IMatchUiLayer`s
  for its HUD, `ISessionGameHandler`s for its messages, `IRenderPass`es for its own GPU work, and console commands. Its
  `IRunStartup` declares its models, its image views (`RenderViews`), its keys, and its own on/off settings
  (`PlayerSettings.RegisterSwitch`, kept in `settings.json`).
- The game registers its own options under `Game:`, with validators, and `UnknownOptionKeys` names a misspelt key. It
  registers its world (a generator, a palette, an atmosphere, and a start pose taken from the match's seed), its run
  startup, its HUD layers, and its systems.
- Registration order is run order within a group, and the registration says why. A game puts its own systems in this
  order where they belong. A system that reads the player's move goes after `PlayerMovementSystem`, and one that reads
  the camera goes after `CameraExtractSystem`. One that adds models goes after `LooseBodyModelSystem`, so loose bodies
  are never the ones left out of a full frame. The game's `SystemOrderTests` pins the order.
- `appsettings.json` holds the engine's defaults for the game under `Engine:` and the game's own under `Game:`. The
  game's `ShippedSettingsTests` checks the shipped file. `Engine:Steam:AppId` is 0 until the game has one, and at 0
  Steam stays off.
- Mods load from `Mods/` next to the executable (`Engine:Mods:Directory`). `Engine:Mods:Enabled` false loads none, for
  a game that takes no code mods yet.

A new game keeps `src/Game`, `Game.csproj`, and `tests/Game.Tests` as its home, so the tools need no setting. It grows
the generator, palette, systems, and options in place. `.claude/skills/scaffold/SKILL.md` has the steps for each kind of
addition.

### Mesh assets are drawn beside the world

A mesh asset is a triangle mesh with texture coordinates and materials. It is read from a glTF 2.0 file at the run's
start (`Assets/`, SharpGLTF at load only) or built by a game, and drawn in the world's pass with the world's light.

- `MeshAssets` holds them for the run, by name and index, and a game declares its files at its run startup. The run's
  step "mesh assets on the gpu" puts every asset's vertices, indices, materials, and textures into device-local memory
  once (`MeshAssetPass`). Nothing is loaded or freed during play.
- Frame systems add instances to `MeshInstances`. `MeshAssetDraws` culls them against the view and both cascades and
  writes one indirect command for each primitive with instances. `MeshPass.Record` draws them inside its own
  renderings, so they meet the world's depth, multisampling, and resolve.
- The vertex shader pulls its vertices from a storage buffer, as the world's does. The fragment shader reads the
  material's textures from one array indexed for each pixel. Blended materials draw after the sky, sorted back to front
  for each object, casting no shadow. The reasons are in `docs/rendering-extension.md`.
- A skinned asset has a `Rig`, sampled on the main thread in floats for drawing only. The deterministic skeletons of
  `Animation/` are separate. A compute pass skins every posed instance once for each frame, so the camera's and both
  cascades' draws read one copy.

`.claude/rules/mesh-assets.md` and `.claude/rules/rendering.md` describe the formats, limits, and costs.

### A game's models are built by recipes

A game builds the models it makes itself from recipes: Python files in its repository that Blender runs with no window
(`tools/models`, the `models` skill). The engine's own importer checks each file (`ModelInspection`, run by
`tools/ModelCheck` and a game's tests), and the engine itself draws it for judging (`tools/ModelView`, or the game's
world through `Engine:Assets:Extra` and `viewpoint`).

- A recipe rebuilds the same bytes every time, so a model can be built again, read in a diff, and checked on every
  build. A model made by hand in a live editor session cannot.
- Most failures of code written for Blender are API calls that changed between versions. The toolkit pins one Blender
  and keeps a list of the changes, and a model server in the editor would fix neither.
- The checks live in the engine, in one place, so what passes is what the engine loads.
- Blender, not a modelling tool of the engine's own: a tool of its own would have to match Blender's mesh editing,
  baking, weighting, and animation before it helped. `docs/model-recipes.md` has the record.

### A game adds passes of its own

A game's GPU work is an `IRenderPass` it registers (`Rendering/RenderPasses.cs`). The run's "render passes" step gives
each pass the targets of its stage. Each frame, the main thread asks it to write its frame slot's data after the frame
systems, and the render thread asks it to record at its stage. The five stages are `Compute`, `Opaque`, `Transparent`,
`Scene`, and `Screen`, in the order of the render loop above.

A game's shaders sit in `Shaders/Game/` beside the engine's and include its `view.glsl`. Each pass is timed apart.
Nothing records a game's passes while the loading screen covers the picture, and image views do not draw them. The
stages chosen, the limit on the number of passes, and the other choices offered are in `docs/rendering-extension.md`.
The mesh sample's aim highlight is the example.

### A camera's lens

The active camera's `CameraLens` is perspective or orthographic. An orthographic camera's position is the middle of
what it shows, and its near plane may lie behind it. Everything measured from a perspective view's eye is therefore
measured from that middle: the fog, the terrain's levels, the lights and their clusters, and the two shadow cascades.
The shaders take what differs from `view.glsl`, and `MeshView` culls to the view's box and sorts by depth. The
console's `lens ortho HEIGHT` shows any view orthographically for a run.

### Image views

A game draws views of the world into images of its own (`RenderViews`). It declares them at its run's start, each with
a size and how often it is drawn. It asks for one with a camera each frame it wants it, and shows one on the HUD in a
rectangle.

Each view keeps its own lists and records of a frame, and draws with the mesh passes' pipelines and shaders through
sets that name them. It draws with one sample a pixel (four doubled an update's time), without shadows or particles, after the world's pass. The
terrain's levels stay the main camera's. The colour is copied into an image only shaders read. The UI pass draws it
under the HUD's pixels, and a mesh material may show it. Nothing draws a view while the loading screen covers the
picture. The reasons for these limits are in `docs/rendering-extension.md`.

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
`IGameMod`. `apiVersion` must equal `ModdingApi.Version`. Mods are loaded in ordinal folder-name order. `dependencies`
is reserved and not yet enforced.

**Matches.** Mods load once per run, after the run's services exist and before any match. `OnModLoaded` gets the run's
services. A match's services, such as the world or the ECS world, are Scoped and cannot be had from them. A run may play
many matches (see "A run plays many matches" in section 8). Each start calls `OnMatchStarted` with the match's
services, once its world and systems exist. Each end calls `OnMatchEnded` before they are freed, where a mod drops what
it holds of them. `OnFixedUpdate` and `OnUpdate` run only during a match. Both are default interface methods, so a mod
without them needs no change but its `apiVersion`.

**Isolation.** Each mod gets its own collectible `AssemblyLoadContext`. Assemblies that the host already has resolve to
the host's copies: the framework, `Itera.Modding`, `Itera.Engine`, and every package in the game's output folder. So
`IGameMod` is one type for everyone. Anything else resolves from the mod folder through its `deps.json`. A mod's
project should reference `Itera.Modding` with `Private="false"` so it does not ship its own copy.

**Errors.** A mod that throws in any callback is marked faulted, and the exception is logged. It receives no further
`OnUpdate`, `OnFixedUpdate`, or `OnMatchStarted`. It still receives `OnMatchEnded`, `OnGameExit`, and `OnModUnloaded`. A
mod that fails to load is skipped. A mod whose `OnModLoaded` throws is removed and its load context unloaded at once,
with no `OnModUnloaded`. The loader never throws into the game loop.

**Timing.** `ModDispatcher` times each update call. A mod's `OnFixedUpdate` joins the fixed systems' times, so a slow
mod shows in the stats' slowest-systems line. A callback over 50 ms that is the mod's longest yet logs a Warning with
its name. The call in progress is kept for the shutdown's signal handler.

**Unloading.** `ModLoader.Unload(id)` calls `OnModUnloaded`, drops the load context, and forces a few collections to
check whether the mod was really released. `LastUnloadWasCollected` reports the result, and a warning is logged when it
is false. Unload is best effort. A mod that subscribes to a static event, starts a thread, or defines ECS component
types stays in memory until the process exits, because Arch keeps a static registry. Reload is a developer
convenience, and restart is the reliable path.

**Safety.** .NET has no sandbox. Mods run with the game's full privileges. Mods are trusted code.

**What mods can do today.** A mod receives lifecycle callbacks, logs, and resolves any engine service from
`IModContext.Services`. A narrower, versioned API surface for mods is open work. In a session only the host edits the
world. A mod checks `SessionService.IsAuthority` before calling `VoxelWorld.Apply` or `TryLift`, which throw on a
client. The host sends a mod's edits to the clients as it does a game's.

## 7. The engine's components and systems

`src/Itera.Engine/Components/` and `src/Itera.Engine/Systems/` hold what most games on a voxel world need. The engine
registers no system. A game registers those it wants, in run order, in its composition (section 5). All of them run on
the main thread.

- Fixed systems run once for each 60 Hz step and receive `FixedStepTime`: the step length and the tick. They change
  the simulation.
- Frame systems run once for each built frame and receive `FrameTime`: the delta, the total, the frame index, and
  `Alpha`, how far the frame lies between the last two steps. They present the simulation. What they write (the camera's pose, the frame's draw lists) does not change it, except
  that the flying camera is where the streaming centres follow.
- Components are plain structs with public fields. A component that holds a position in metres keeps it local to the
  match's origin, and `OriginShifts` moves it when the origin moves.

`OriginShiftSystem` is the first fixed system, so the whole step runs in one origin. Registered anywhere else, it throws
at the first step. The other systems fall in these groups:

- The player and the cameras: input, movement, and the camera that follows the player, with the sky and sun a game
  sets each frame.
- The world and bodies: streaming centres, the console's `cleanup` and `reset`, loose bodies, stand-ins for characters,
  and the debug aids. A host centres streaming on the local player and on a disc round each remote player
  (`Engine:Voxels:RemotePlayerRadiusMetres`). Offline or on a host, it also centres on each loose body away from every
  player.
- Mesh assets: the system that hands every placed mesh to the renderer, and the one that moves the clips' times.
- Trigger zones, replication, and the benchmark.

`RemoteBody` is a tag for a client's copy of a loose body that its host simulates. The loose bodies' rest and removal systems, the
physics read, and the map reset skip it, and `LooseBodyModelSystem` draws it. Nothing in the engine, the samples, or the game adds it to an
entity. Only the engine's tests do.

`.claude/rules/systems.md` lists each component and system with its order, and `.claude/rules/physics.md` the physics
ones. `.claude/rules/replication.md` describes the replication systems.

## 8. Design directions

Each direction says what the engine does and why. How an area works now is in its rules file, named in the direction.
Measurements and dated accounts are in `docs/records/architecture-directions.md`.

### Units and numbers

- **Metres and seconds** are the simulation units. Voxel coordinates are absolute integers in centimetres from the
  map's middle (`VoxelCoord`): voxels, edits, saves, and what peers send. Integers keep every peer's edits and saves
  the same.
- **Two frames for positions.** A float loses precision far from the middle of a large map. So the simulation and the
  renderer keep positions in metres (`Vector3`: the ECS components, Bepu bodies and statics, rays, the mover's feet, the
  camera) local to the match's origin. The origin (`WorldOrigin`) is a scoped service that every match service shares,
  and it is always on whole metres.
- A point that crosses the origin is an absolute `WorldPoint`, three doubles in metres. Examples are a start pose, the
  console's `tp` and `pos`, a benchmark view, the play area, a call into a generator or an atmosphere source, a solid
  model's pose, and the tint map's read. A double holds a centimetre exactly past the edit format's 167 km.
- The two frames meet only through `WorldOrigin`, in doubles. A local float becomes absolute as origin plus local, which
  is exact because the origin is whole metres. It becomes centimetres or cells only as floor((local + origin) * scale).
  `WorldPoint.ToCentimetres` counts a point within 10 nm under a centimetre's edge as on it, so a centimetre written in
  metres reads back as itself. So the voxels that a move, a ray, a collider, or a settling ball reads 150 km out are
  those it reads at the middle, bit for bit (`FarFromTheMiddleTests`). `VoxelCoord` has no conversion from metres,
  so no code turns a local float into centimetres without the origin.
- Mesh colliders keep an absolute anchor each, in whole metres, with their triangles from it. The mover's cells, the
  voxel colliders' statics, and the loose bodies' pivots are worked out in absolute doubles and posed locally.
- **The origin follows the camera.** A match's first origin is where its game starts it (`IOriginStart`). Then
  `OriginShiftSystem`, first in a game's fixed systems, moves it to the camera's whole metres once the camera is
  `Engine:Origin:ShiftMetres` out on any axis. A local float near the camera then rounds by at most about 30
  micrometres. A far `tp` asks for the move first (`RequestFollow`), so the origin goes to the jump's end and the jump
  lands exactly, not back to the camera where it was. `OriginShiftSystem.Initialize` moves the origin a metre and back,
  so the first move in play does not compile the code.
- A move (`OriginShifts.ShiftTo`) shifts every body and static in Bepu, the engine's components that keep positions
  (current and previous together, so the blend between steps holds), the world's streaming centres, retired models,
  light flashes, and queued particles. Then it calls the game's `IOriginShiftHandler`s. It happens between steps and
  frames, or in the first fixed system's update, so a step sees one origin.
- The renderer draws everything local to the origin. It takes the origin back only for what is drawn by place (the
  surfaces' noise, strata, tint, and texture tiles), for choosing and ordering the world's columns, and for snapping
  the shadow maps' texels. The particles' GPU pool and the playing sounds move with it (`.claude/rules/rendering.md`,
  "Under a moved origin"). The GPU cost of all this is small (0.4 to 1.0 % of the world pass).
- Peers keep their own origins, so a game's message carries a position absolute (`MessageWriter.WritePosition`: 1/64 cm
  to ±335 km). A host steps the bodies near a far client in its own origin's floats, which round to 1 mm at 10 km and
  8 mm at 100 km.
- **Positions in metres use `System.Numerics`** (`Vector3`, `Matrix4x4`, `Quaternion`), which Bepu takes directly.
  `Silk.NET.Maths` stays at the edges of Silk.NET APIs (window and swapchain sizes).
- **Anything every peer must compute alike** uses `DeterministicMath` (below), never `System.Random` or platform maths.

### Voxel storage

The world is a sparse hierarchy, not a dense grid. A dense 10 cm grid over the world would hold about 1.5 x 10^11
voxels. At 1 cm it would hold 4 x 10^13 (5 TB at one bit each).

- A chunk is 128^3 voxels held as a 64-tree: a root node of 4 x 4 x 4 cells of 32 voxels, each a node of 4 x 4 x 4
  bricks of 8 voxels, each brick 8 x 8 x 8 voxels. A node keeps 64-bit masks of its children that are not air and that
  are one material. A brick, node, or chunk that is all air or all one material collapses into its parent, so
  homogeneous regions cost one list entry. There are 256 materials; 0 is air.
- Each world voxel also has a density (`VoxelDensity`): how much of its cube is solid, 0 to 255. A solid voxel's is 128
  to 255 and an air voxel's 0 to 127. So everything that reads materials sees the same world, and the density only
  places the drawn surface within a voxel. A brick keeps a block of densities only when some voxel's is not its
  material's default. The generator gives the ground's density from its height. Edits change voxels by the share they
  cover. Models keep only materials, except an object kind's, which keep densities too.
- The tree was compared with deeper 64-trees, OpenVDB, NanoVDB, and sparse voxel DAGs and kept
  (`docs/voxel-structures.md`). The chunk tree is already a 64-tree over bricks the size of VDB's leaves. NanoVDB's
  fixed-size nodes would take 1.5 to 5.5 times the memory, its tree cannot change after building, and its GPU build
  tools need CUDA. OpenVDB is C++ with no .NET binding.
- Storage is native memory (`VoxelMemory`) in fixed-size block pools, capped by `Engine:Voxels:MemoryBudgetMegabytes`.
  Edits may pass the cap. At the cap, streaming stops loading and resumes once 5 % of the budget has been freed. Each
  pool takes whole slabs, so used memory at the cap is often well below the budget, and the resume level counts from
  where use stood when the cap was reached.
- **The grid is a value each world owns** (`VoxelGrid`, from its generator): the base voxel edge in centimetres, the
  bounds in whole level-0 chunks, and the conversions between centimetres and voxel indices (`GridLevel` for a level).
  A game sizes its world with `VoxelGrid.Sized`, or with the constructor in chunks. The most is ±160 km on any axis, up
  and down too (`MostReachCentimetres`), which fits the edit format's 2^24 cm with room for the play area's margin.
- The grid is part of what the generator's id and version stand for. So peers and saves agree on it without carrying
  it, and a change of size raises the version.
- A column stores a window of its layers (the generator's range, `IChunkGenerator.DescribeRange`, and the layers its
  edits reach), with one fill below it and one above it. So a map's height costs memory only where something is. Its
  mesh column keeps blocks only for the runs of layers that can hold a surface (`VoxelWorld.CollectSurfaceLayers`).
  Columns and generation jobs hold as many layers as their world's grid.
- Level-0 voxels may be 1 cm to 1 m (`VoxelGrid.MostBaseCentimetres`). Coarse voxels suit terrain a game deforms in
  large strokes, for little memory, since the engine already handles voxels that large at its coarse levels. A shape
  under about a voxel shows only as a soft dip in the densities, walls and caves come in voxel steps, and a piece lifted
  from the ground keeps the voxels' steps. An edit measures a voxel on a limited number of sub-rows, so a very small change does
  nothing.
- The limit on a map's size is one of storage and streaming, not of precision. Positions are floats from the match's
  origin, which follows the camera. Navigation's window follows the players over any map. Code where the world and a
  model meet uses the grid's and the model's units, never a level's 2^k.
- **A column is the unit of streaming.** Level k (voxels of `Edge(k)` cm, chunks of 128 of them) loads the columns whose
  nearest point is within the level-0 streaming radius times 2^k of a streaming centre, and unloads those beyond that
  plus one level-k column. The radius is `Engine:Voxels:StreamingRadiusMetres`; its default of 0 takes it from the grid.
- The distance is to the column's nearest point, so a disc smaller than a column still loads the columns it meets.
  Every level holds a full disc, so the renderer can use a coarser level where a finer column is still loading.
  `Engine:Voxels:Levels` sets how many levels there are, and `Engine:Renderer:ViewDistanceMetres` ends the view sooner.
- Views to 8 km work with 7 to 10 levels. Nine levels reach 17 km, so an 8 km view draws its last kilometres from level
  8.
- **Load order.** First the coarsest level's columns, nearest first: that level is drawn to the view distance, so a
  missing column is a hole. Then the columns the view draws now, nearest first in their own level, coarse before fine at
  equal distance. Then the ring beyond reach. Each level keeps its own sorted list, rebuilt when a centre has moved half
  of that level's column. Without this order, flying faster than loading would shrink every level to a small disc round
  the camera and leave holes in the far view.
- **Only a peer's own view needs full detail.** On the host, a remote player's centre needs the level-0 columns right
  around it (a 16 m disc), and edits reach the log whether or not a column is loaded. So remote centres get a smaller
  radius and no coarse levels (`StreamingCentre`, `VoxelWorld.SetStreamingCentres`).
- Chunks carry a level in their keys, the generator, and edits. An edit reaches the resident columns of every level, and
  a voxel is tested at its level's sample point (`VoxelGrid.SampleAt`).

### The world is its seed plus its edit log

A chunk that is not resident is not stored anywhere. A column is rebuilt by generating it from the seed and replaying,
in order, the edits that reach it (`EditLog` indexes them by column at every streamed level). Saves and late joiners
receive the seed, the generator id and version, and the edit log (`VoxelWorld.SaveEdits` and `LoadEdits`). There is no
chunk file format. A save loads only with the same generator version.

- Edits (`VoxelEdit`) use integer geometry and integer tests, so every peer gets the same voxels. A sphere, capsule, or
  box changes each voxel by the share of it the shape covers, measured on up to 10 x 10 sub-rows along y and z, each to
  the centimetre along x. So a dent smaller than a voxel lowers its density a little, and `CountRemoved` counts exactly
  what was taken, in cubic centimetres. A clear takes that share of what is solid in the voxel, so one repeated on a
  spot keeps taking.
- A lift clears voxels as a dig does and keeps the removed voxels as a piece named by its sequence (`VoxelPiece`). The
  log keeps the pieces. A piece edit puts a piece back into air only, its pivot at a voxel corner and turned by a
  quaternion rounded to 16-bit integers. Each level's sample point is turned back into the piece with an integer
  rotation matrix, so every machine gets the same voxels.
- A reset (`VoxelEditOperation.Reset`, the console's `reset`) is an edit too: an empty box that covers no voxels, after
  which no earlier edit counts. `EditLog` clears its column index at it and `VoxelWorld` unloads every column to
  generate again. The log keeps the earlier edits, so sequences go on and every reader follows it in place. A session's
  clients get the reset as an edit, saves and join payloads carry it, and the loop covers the picture with the loading
  screen again when `VoxelWorld.LastReset` moves. Code that reads the edit log must handle it.
- The save is the log stream with the match settings and a game's sections; `EditLogSerializer` lists the formats and
  what each added. An older build refuses a newer format. A save holds only the edits after the last reset,
  renumbered from 1. A join payload keeps every edit.
- The log indexes at most 2^24 column entries since its last reset (`EditLog.MaxIndexEntries`). They are counted over
  all ten levels whatever a game streams, so a host, its clients, and a save's next load agree.
- Compaction (`docs/edit-log-compaction.md`): a long log is small, but replaying edits into a column costs.
  `VoxelEditing.ApplyToRegion` fills each row's span of a shape at once instead of testing each voxel, with the same
  bytes. Snapshots of edited chunks, which would let old edits and pieces put back leave memory, stay open work.

### Voxel columns are generated on worker threads

`VoxelWorkers` generate columns and free them after unloading. The main thread installs them, applies edits, and alone
changes the live world. Every change is recorded in `VoxelChangeLog`, which each consumer (the world's meshes, physics'
collidables) reads at its own pace through a `VoxelChangeCursor`. A reader that falls a full ring behind has lost
changes and reloads everything it holds.

- **Large edits apply over fixed steps.** An edit whose box covers more than 128^3 level-0 voxels
  (`VoxelWorld.DeferVoxels`) is logged, sent, and saved at once, but only marks the resident columns it reaches
  (`Column.EditsPending`). `ApplyPendingEdits`, once a fixed step after the ECS fixed group, applies each such
  column's waiting edits in order, `Engine:Voxels:EditColumnsPerStep` columns a step. The budget is a count and not a
  time, so a slow machine keeps the same pace as its host. A smaller edit applies at once, after the waiting edits of
  any column it reaches, so each column takes edits in the log's order.
- Navigation reads edits only up to `AppliedEditCount`. Other readers (raycasts, the mover) may see a large edit's old
  voxels for a few steps.
- **The edit warm-up.** .NET compiles hot code again, optimised, only once no new code has been compiled for a moment,
  and a game's start-up puts that off. `StartEditWarmUp` (from the voxels' start-up step, once a run) applies shapes
  to throwaway chunks on a thread of its own, round after round, so the edit code is optimised before play. It stops
  when the rounds have settled, when nothing has been compiled for a while, or at a time limit (`EditWarmUpStop`). The loading
  screen waits for it.

### World generation toolkit

World generation is seeded and deterministic so every peer builds the same map from a seed the host chooses. A
generator (`IChunkGenerator`) answers `DescribeColumn` (which layers of a column are air or one material, exactly) and
`Fill` (a chunk's materials and densities). Several worker threads call it at once.

- Use integer hashing of the seed and coordinates, never `System.Random`, and never iterate hash sets or dictionaries
  in generation order.
- Use basic floating-point operations only (+, -, *, /, sqrt). `Math.Sin`, `Pow`, `Exp` and similar come from each
  platform's maths library and can differ in the last bit.
- Use no `System.Numerics` vector helpers either. In .NET 10 `Vector2.Lerp`, `Reflect`, and `Transform` use
  `MultiplyAddEstimate`, which may fuse the multiply and add on one machine and not on another.
- A generator's `Version` rises whenever the same seed would give other voxels. Sessions and saves refuse a different
  one.

The engine's toolkit is in `Voxels/Generation/` (`.claude/rules/voxels.md`):

- `HeightfieldGenerator`, a base for ground. A subclass gives the height, the surface material, the layers below, and
  features written over the ground. The base caches a column's heights per thread and allocates nothing after a
  thread's first column. It gives the ground's voxels their density from the height. A voxel a feature made solid or
  air takes its material's default density (`VoxelDensity.Agree`), so a feature's voxels are whole. A layer is described
  as air or one material only where every voxel is wholly so.
- Shapes with exact row spans (the range of x a row of sample points covers, in + - * / and sqrt, so every level and
  every machine fills the same voxels): `Capsule`, `Ellipsoid`, `Prism`, `CutEllipsoid`, and `VoxelRows`.
- Noise and placement: `FractalNoise`, `JitteredGrid`, `ScalarFieldGrid`, `SeedCache<T>`, and `FieldTintMap`.

### Deterministic maths and rotations

`DeterministicMath` (`Maths/`) gives the same bits on every machine: SplitMix integer hashing, seeded draws, gradient
noise, exact integer roots, and `Turns` and `CosSin` from their series. It uses integer operations and scalar double
`+ - * /` and `Math.Sqrt` only. `Rotation` is a 3x3 matrix of doubles built from turns through `CosSin` and combined
with scalar operations only, with `RigidTransform` (a rotation and a position). A body turned the same way fills the
same voxels on every machine, and poses blend through quaternions. Their pinned hashes (`DeterministicMathTests`) guard
every generator and body built on them.

### Poses and foot IK

`Animation/` poses jointed bodies of voxel parts (`.claude/rules/animation.md`). No engine system uses it, so a game
drives it.

- A `BodyPose` is a turn per bone, so one pose fits every skeleton of a plan whatever its lengths, mirrored and
  blended.
- An `AnimationClip` is keyframes written in C# (poses and times, no asset files) that add onto the pose under it. So a
  landing's dip keeps a run's arm swing.
- `FootPlacement` puts any number of feet on the voxel ground, probing through `VoxelCharacterMover`. It lowers the root
  by what a planted foot must reach down, bends each leg by two-bone IK, and tilts the feet to the slope. On flat
  ground it leaves a pose as it is.
- All of it allocates nothing. A game builds the bodies (the shapes that fill each part's voxels), which are its own.

### The raster pipeline draws the world as triangle meshes

The renderer draws the resident voxels as triangle meshes through a conventional raster pipeline.
`docs/rendering-redesign.md` holds the decisions, the measurements, and the history of how it replaced a raymarcher.
The rules are in `.claude/rules/rendering.md`.

- **Meshes.** `WorldMeshes` (`Rendering/Meshes/`) meshes the resident columns block by block. A block is a 32-voxel cell
  of a chunk, read with a 2-voxel apron on the main thread and meshed on `MeshWorkers` threads by `SurfaceNets`: naive
  Surface Nets, one vertex per cell, at the mean of the points where the density crosses one half. A block owns the
  edges whose lower voxel it holds, so blocks side by side meet exactly. Each vertex also carries how many of its
  cell's surface corners are each of the mesh's eight most common materials (`Weights`), which the fragment shader
  blends across a border by height.
- `WorldMeshes` follows the voxel change list, meshes a column only while it can be drawn, starts blocks nearest the
  view first, and reads no new block after 2 ms of a frame, so edits show within a few frames.
- **Joins between levels of detail are stitched, not skirted.** Each frame `LevelBalance` keeps every drawn column
  within one level of its neighbours, corners included. A block along a side past which a coarser column is drawn is
  meshed again by `SurfaceNets.MeshTransition` from its own voxels and the coarser level's block. Its outer cells take
  the coarser level's vertices, so the two meet. About nine in ten of the open edges along a join close. The levels'
  samples (each at its own voxels' centres, which never coincide) disagree in a few places, and no published method
  stitches Surface Nets sampled this way. An exact method is open work.
- **The mesh heap** is one device-local buffer, which is also the index buffer (`Engine:Renderer:MeshMegabytes`).
  `GpuRangeHeap` gives each block's mesh a range of it. Finished meshes reach it through the frame slot's staging
  (`StagingWriter`). A full heap first frees the meshes of wanted columns that neither the camera's pass nor the far
  cascade drew in the last frame (`WorldMeshes.Evict`). They stay wanted and are meshed again when the view needs
  them. Only when nothing is left to free does a block go without room, and the heap warns.
- **Draws.** Each frame `WorldMeshes` picks a level for each column. A coarse column splits into its four finer ones
  when its nearest part is within the finer level's reach times `Engine:Renderer:MeshReachShare`. It culls blocks to
  the view and the shadow cascades and writes an indexed indirect command for each block. The device must offer
  `multiDrawIndirect` and `drawIndirectFirstInstance`. The camera's commands are sorted nearest first, so the depth test
  rejects hidden blocks before they are shaded. There is no depth pre-pass, which measured slower. `mesh.vert` reads the
  vertices from the heap, with no vertex input.
- **Shadows.** With `Engine:Renderer:Shadows` on, the sun has two cascades (`ShadowCascades.Fit`): squares round the
  bounding spheres of two slices of the view. Each reaches further towards the sun to catch casters outside the view,
  and each is snapped to whole texels. They are drawn from the blocks and models whose bounding spheres meet them,
  with only the faces turned away from the sun, so a lit surface never meets its own depth. The near cascade is
  contact-hardening: a blocker search measures the gap between caster and surface, and a blur over a Poisson disc
  follows that gap for a sun 2 degrees across, so shadows are sharp where a caster meets the surface. The far cascade
  uses 3 x 3 lookups.
- The far cascade's blocks are chosen one level coarser than the camera's (`FarShadowCutShare`), in a pass that is not
  balanced. `LevelBalance` keeps the camera's columns within a level of their neighbours for its stitched joins, and
  the far cascade draws plain meshes. Balanced, the far pass split its columns back to the camera's, and the coarser
  cut saved nothing. Unbalanced, the shadow maps took about a third less time in the benchmark's flight, with the same
  picture but for a few blobs of shadow edge.
- `Engine:Renderer:DistantShadowMetres` (off by default) adds a third cascade for the distance. It is drawn only while
  the sun is low (`MeshLighting.DistantStrength`), because under a high sun a 4 m boulder's shadow is a few of its
  texels. It uses the terrain's blocks alone, listed in the camera's pass, so models and mesh assets cast nothing into
  it. Listing its blocks in the far pass instead cost the same and drew the same picture, since the far pass then
  chose the camera's columns. It doubles the shadow maps' time. It changes little on gentle ground, since the
  normal offset leaves out the shadows of anything under about 4 m. A game with taller ground gains more.
- **Anti-aliasing.** `Engine:Renderer:MsaaSamples` draws the world into multisampled colour and depth images of its own,
  resolved as rendering ends into the target (the mean) and the depth image (sample 0, which the particles and lines
  test against). SMAA 1x runs only when MSAA is off. The settings' Anti-aliasing is one choice of Off, SMAA, MSAA 2x,
  4x, and 8x, applied at once.
- **Look and light.** A material is a colour blended towards its tint colour by the tint map (a game's, from any field),
  with a glow. Its pattern comes from a texture set: a recipe (`mesh-surface.glsl`'s `surfacePattern`, the kinds
  `VoxelPattern` names) baked once into colour, normal, height, and occlusion layers, or a photograph. The sets are
  stored as block-compressed DDS files (BC7, BC5, BC4) and read three ways by the surface's facing. No recipe is worked
  out per pixel.
- `mesh.frag` lights each pixel with the sun through the cascades, sky and ground light dimmed by the vertex's openness
  and scaled by a sky-visibility field, and point lights. Then come the air, the tone curve (a filmic fit of ACES after
  an exposure), the grade, and a dither against bands in the 8-bit image (`lighting.glsl`, shared with the particles).
  The air fades the surface into the sky's colour, each colour at its own rate, and adds the light of haze glows
  (`air.glsl`). A glowing surface keeps its colour, leaves the grade, and adds its glow after it. `world-sky.frag`
  draws the sky where the depth is still 0. The depth is the reverse form of `view.glsl`: the lens's near plane divided
  by the distance along the view direction, so 0 is the sky and nearer is greater.
- The engine's own look (`RenderLook.Default`) is stylized: less saturated and darker, darker on the sides, warm
  sunlight and cool sky light, dark tones towards teal and light ones towards orange. A game may replace it.
- **The look is data** (`RenderLook`). The art direction's values were shader constants. Now they sit in a per-slot
  storage buffer the shaders read as `LOOK`. They come from a game's `IAtmosphereSource.LookAt` at the camera each frame,
  and the console's `look` tunes them live (in code, not in the settings file: `docs/rendering-extension.md`). What the
  look gives with the atmosphere (sunlight, sky and ground light, the sky's colours, the grade, the haze) is worked out
  once a frame on the CPU. That left the pictures as they were and made the world pass 1.2 to 2.2 % faster. Reading
  the buffer at every pixel instead was slower.
- **Atmosphere** (`RenderView.SetAtmosphere`, in `ViewConstants`) comes from a game's `IAtmosphereSource` at the camera,
  so a game can differ by place. It holds the sun's direction (moving as the game says), the sky colour, the sun's
  colour, haze, overcast, and the grade. Under the horizon it is night: no sunlight and no shadow maps (`SunPath`). A
  game's moon is the same light, pointed at the moon above the horizon with a dim colour.
- **The sky field** (`SkyField`) holds how much of the open sky each cell of a box round the view sees. A worker builds
  it from the resident columns, and `mesh.frag` samples it once a pixel to scale sky and ground light.
- **Point lights.** Each world block with glowing vertices gives one light, and events give others (`PointLights`). The
  brightest `Engine:Renderer:MostLights` near the view go to the GPU, where a compute pass (`mesh-clusters.comp`) sorts
  them into clusters of the view, so each pixel loops over its own cluster's lights only.

### Models are drawn beside the world

A model is a small voxel volume drawn at any position and turn: a part of a character's body, a loose body's piece, an
item.

- `VoxelModels` (main thread) holds each model as one chunk tree per level in a voxel memory (the world's with
  terrain, its own without), with a voxel unit of its own, until its owner frees it. `WorldMeshes.Models.cs` meshes each
  level of each model as it does the world, on the same workers and before the world's blocks, and keeps it in the mesh
  heap in a range of its own. A model counts as uploaded (`VoxelModels.SetUploaded`) once all its blocks are in; loading
  tasks wait for that.
- A replaced model (a wound) draws its old meshes until the new ones are all in (`VoxelModels.Revision`). A removed
  model's ranges go the next frame. A settled loose body's model is retired, not removed: it is drawn at its last pose
  until no world block is pending, so a ball never vanishes for a frame between its body and its voxels.
- Frame systems add models to `ModelInstances`. `GameLoop` hands the list to `WorldMeshes.Upload`, which keeps the draws
  in view or casting into a cascade, groups them by model level, and writes each block of a level as one indexed
  indirect command whose instances are that level's draws (`MeshModelDraw`). `mesh.vert` and `mesh-shadow.vert`
  compiled with `MESH_MODEL` place and turn them, and `mesh.frag` takes the draw's tint instead of the tint map's.
- The models are drawn in the world's pass, tested against the world's depth and written into the same depth image, so
  models hide each other and the ground hides them.
- `ModelShadow` says how a model meets the sun's shadow maps: `Casts` (drawn, and casting; the default), `None` (drawn,
  and casting nothing: first-person arms and what they hold), and `Only` (not drawn, and casting: the local player's own
  body in first person).
- Tint is one value per model, set by the game. `VoxelTintSampler` blends the tint map on the CPU as the shader does,
  so a loose body matches the ground it settles into.

### Voxel objects are volumes of their own, finer than the world

Trees, bushes, rocks, and props are voxel objects (`Objects/`; `docs/voxel-objects.md` has the choices and
measurements, `.claude/rules/objects.md` the details). Each kind is a volume of voxels finer than the world's, built in
code and registered at the run's start. The world's generator places them. They are never written into the world's fine
levels.

- Near the camera an object is drawn as its kind's part models, turned to its heading, at three levels by distance that
  fade into each other by a screen-space dither. From a hand-over level K up, the world's voxels hold a copy of it at its
  own size, written by the column jobs, which the world draws as ground. The coarsest model fades out over its copy,
  and a column splits below K only once its objects are known and their models are on the GPU. So the switch follows
  the distance and nothing goes undrawn.
- Objects are shaded as the world's surfaces (its materials, texture sets, and tint map), so a model and its copy
  match.
- Characters, bodies, rays, and navigation meet objects by their voxels (`ObjectColumns`, the objects' Bepu statics near
  bodies, `ObjectRays`, `NavObjects`). Edits damage them by share, the same live and on replay. A part cut off is removed
  by an edit of its own (`RemovePart`), the world's owner deciding the cut, and falls as a cut body each peer makes.
- A game places objects during play, and removes whole ones (its own or the generator's), by edits of their own
  (`PlaceObject`, `RemoveObject`, logged by the world's owner through `ObjectWorld.TryPlace` and `TryRemove`). Every
  peer replays them from the log as it does any edit: an area's fill takes them after the generator's objects, in the
  log's order.
- A kind's collider compounds are made at the match's start, since one made when a body first came near took a step of
  about 90 ms for a large kind. A far copy keeps a thin upright part a voxel wide, so far trunks never vanish, while caps
  and rims do not grow.
- Far copies come from the generator's placer alone, so a kind that is copied is neither placed nor removed this way. A
  game whose view ends before the hand-over to copies registers its kinds without them.

### Characters move with Itera's own controller against the level-0 voxels; Bepu is for loose bodies

Itera's narrow-phase filter makes no contacts between two collidables that are not dynamic (`NarrowPhaseCallbacks`;
Bepu itself would ask about them). So a Bepu character controller would need a dynamic body held upright by a custom
constraint, or sweeps, which give no way out of a start inside a surface and may miss thin obstacles. Either would need
collidables built from the voxels and rebuilt after every edit near a character. `VoxelCharacterMover` instead reads the
chunk trees directly (`VoxelWorld.Collision.cs`), so a dig changes collision at once. Moving characters into Bepu was
rejected: voxel collision would be needed around every character at all times, and the step, slope, and ledge rules
would be lost. The rules are in `.claude/rules/physics.md`.

- The body is an upright cylinder with flat ends, not a capsule. Every trunk, boulder, and curved wall is a staircase of
  voxel steps, and a circle takes its contact normals from the voxel corners, so it slides along them. A capsule would
  need sphere tests against thousands of voxels and slips off ledges. The flat bottom stands on the highest voxel under
  it.
- It collides with the exact level-0 voxels, which are loaded within level 0's radius of the streaming centre. On voxels
  coarser than 1 cm it stands on the ground's surface inside a voxel, where the density crosses one half, blended
  bilinearly between neighbouring columns whose surfaces differ by less than the slope limit over one voxel. So a slope
  under the limit is a ramp, and a ledge, a stair, or a wall stays sharp.
- It steps up onto ledges no higher than its step, keeps to the ground walking down, lands falls, stops jumps at
  ceilings, and lifts a body out of voxels placed into it. It never moves a body while a column its body and move cover
  is not resident. The map's edge is a wall at every height. Passable materials (`IVoxelPassability`: foliage) let it
  through.
- It reads a game's mesh colliders (`MeshColliders`) as the same cells (`MeshColumns`), taking the higher top, the lower
  bottom, or either cell of the two, without virtual calls. Only the cells a triangle passes through block, so a closed
  mesh has no inside. A column's surface is the triangle's plane at the column's centre, so a mesh slope is walked as a
  ramp.
- Over terrain its cells may be finer than the voxels (`Engine:Physics:TerrainCellCentimetres`), so it meets objects,
  solid models, and meshes at their own size over coarse ground. It then reads the voxels as fine cells of ground under
  their surfaces, blended across the voxels' columns with no seam (`FineGround`). Without terrain it reads the
  colliders alone in cells of `Engine:Physics:CollisionCentimetres`, with no column to wait for, and its map edge at the
  play area's world edge (`PlayArea.WorldEdgeMetres`). Its tests run the same bodies on voxels and on triangles built
  from the same features (`MoverFeatureTests`, `DensityGroundTests`).
- Moves split into parts of at most 24 voxels, each slid through a 2D grid of the cells that block anywhere in the
  body's height. The grid is up to four strips of 128 cells side by side, so a body is at most 2 m in radius
  (`MaxRadiusMetres`). The queries stop early where they can, and a move remembers each column's surface.
- A slope limit (`MovementStats` max slope) tells slopes from ledges by two rules. A step up is kept only when the
  ground stays level just beyond it. Ground is steep when the body rests on a thin rim and the ground also rises more
  steeply than the limit within a few centimetres in one of eight directions. Seen from any side the rim and the rise
  are the same, so zig-zagging does not climb a slope. On steep ground the mover does not step up, and
  `PlayerMovementSystem` slides the player down, steers it only as in the air, and does not let it jump.

### Loose bodies are pieces of voxels that Bepu moves

A thrown ball, a lifted piece, or a part cut off a model is a loose body (`LooseBodies`). Bepu sees it as the convex hull
of its voxels, with continuous collision where it is not a cut body. A compound of a piece's boxes was tried first and
cost up to 137 ms a step.

- **The world near a body.** Bepu sees the voxels near its dynamic bodies only (`VoxelColliders`). Each cell of 32 voxels
  a side that a body's bounds reach, widened by its movement in a step, becomes one static compound of boxes over the
  cell's blocking voxels (`VoxelBoxes`). On a grid coarser than 10 cm a cell has fewer voxels, down to a brick, so its
  edge stays near 3.2 m (`VoxelColliders.CellShiftFor`): a cell is built whole when a body first reaches it, and a
  32 m cell of 1 m voxels took about 7 ms. The change list rebuilds a cell after an edit, which wakes bodies resting on
  it. A cell no body has needed for a second is removed. A body whose columns are not resident is frozen (made
  kinematic) and moves on once they are. On voxels coarser than 1 cm a column's top voxel ends where its density puts
  the ground's surface (`PhysicsWorld.SurfaceBox`), so a ball rests on the drawn floor.
- A custom Bepu shape reading the chunk trees was rejected: Bepu's worker threads would read the voxel world, which is
  main-thread only.
- **Life.** A ball is made from nothing (`LooseBodies.TryCreateBall`), not in the edit log. After 3 s at rest
  (`Engine:Physics:SettleSeconds`) and not reaching into a character, it goes back into the grid as a sphere of its
  material, or as a piece edit if it is a lifted piece. Without voxel terrain the game's `ILooseBodyRest` decides. A
  body that falls 10 m below the world is removed. When more than `Engine:Physics:MaxLooseBodies` are needed, the oldest
  are removed (`LooseBodyRoom`), a ragdoll whole. Whatever removes a body destroys its entity in the same step. Each
  entity keeps its body's birth stamp (`LooseBody.Born`), so `PhysicsReadSystem` can tell a handle Bepu gave to a
  newer body.
- **Characters and bodies act on each other.** The mover reads loose bodies as voxels of its cells (`LooseBodyVoxels`),
  so characters are blocked by bodies and stand on them, but push rather than climb them. Each character near a body
  has a stand-in (`PhysicsWorld.AddStandIn`): a Bepu cylinder of its size and mass that never tips and touches those
  bodies only. It is placed where the character ended each step, at its velocity plus a limited acceleration towards
  the velocity it wanted, so a character pushes with about its own weight. The change of velocity bodies give a
  stand-in is added to the character's velocity after steering.
- **Cut bodies and ragdolls.** Parts cut off a jointed model become bodies of several models that are removed after
  resting instead of put into the grid (`CutPiece`, `BodyKind.Cut`). They touch only the voxels, and they collide
  without sweeps, which keeps piles of them cheap. `LooseBodies.TryCreateRagdoll` makes a ragdoll of one compound body
  per part, joined where the parts met by a ball socket, a swing limit, and a twist limit (`JointAxis` says which axis a
  joint turns about least). A ragdoll rests only when all its bodies rest, freezes and thaws as one, and is removed
  whole.
- **The physics warm-up.** Bepu makes its solver's code for each kind of joint and its collision code for each pair of
  shapes and kind of contact at run time. So ahead-of-time compilation (ReadyToRun) covers none of it, and a game's
  first ragdoll made the main thread wait while hundreds of Bepu's methods were compiled. `PhysicsWorld.StartWarmUp`
  (from the match's physics step, once a run, `WarmUps.Physics`) builds a world of its own on a thread of its own, drops
  into it what the engine's bodies are made of, steps it, and frees it. The loading screen waits for it. A few kinds of
  contact are still compiled as they first occur.

### A game sets the physics

A game sets the match's gravity and each body's share of it, and the solver's substeps, iterations, and damping. It names
physics materials (friction, and bounciness set exactly by a pass after each step, since Bepu's contacts have no
restitution) for its bodies, statics, mesh colliders, and voxel materials. It sets collision layers and groups. It gets
trigger zones that report the bodies and characters coming in and going out, and impact events for the bodies and
statics it marks.

The engine's records by handle (`BodyKinds`) carry each body's and static's settings to the narrow phase on Bepu's
worker threads, and every removal resets them. Gravity is each peer's own. Down stays -Y for characters, the mover, and
the terrain. Characters fall by their strength's share of the Earth's gravity and keep their jump's launch speed. The
rules are in `.claude/rules/physics-contacts.md`, the choices and findings in `docs/physics-materials-and-groups.md`.

### Networking is host-authoritative

One peer is the host and owns the simulation. Deterministic lockstep is ruled out because Bepu does not guarantee
bit-identical floating point across CPUs. Voxel deformation is synchronised as edits (position, shape, operation), never
as voxel data. A host is a game run, with or without graphics (`Engine:Window:Mode=None`), and holds slot 0 as a player.
There is no host migration, and the session ends when the host quits. The rules are in `.claude/rules/sessions.md`.

- **Sessions** (`Networking/`). A match starts as Offline, Host, or Join, from the menu, the console, or
  `Engine:Session:Mode`. Offline is the host role with no socket, so `SessionService.IsAuthority` is the one check.
- Messages travel through `ISessionTransport` (LiteNetLib's `NetworkService`, or Steam's `SteamTransport`) on a
  reliable ordered channel (the join and edits) or a sequenced one (per-tick state). Writers reuse one buffer, so
  sending allocates nothing. `MessageReader` throws on anything short or oversized, because a peer is not trusted.
- `SessionProtocol.Version` rises whenever any of the engine's messages changes shape or a kind is added. A game
  versions its own messages in its `ISessionMatchData.Fingerprint`, which the hello carries. `WireFingerprintTests` pin
  a fingerprint of every message's bytes with the version, so a change to the wire fails them until the version rises.
  `SimulatedNetwork` wraps a transport to add delay, jitter, and loss, so a host and a client can be tested as if far
  apart.
- **The handshake** is plain messages, so every transport shares it. The client's `Hello` carries the protocol version,
  the match identity (`IMatchIdentity`), the step rate, and the fingerprints of the replicated kinds and of the game's
  data. The host answers `Welcome`, or closes the connection with a reason. A mismatch in any of them is a refusal, so
  peers that disagree about the world never play together. A hello that comes before the host's world is made
  (`VoxelWorld.IsInitialized`) takes its slot and waits. A string a peer sends is made printable before it is logged
  (`LogText.Printable`).
- **The join payload** is the host's saved edit log (`SaveEdits`, `EditLogSerializer`), saved when the hello is
  accepted. It goes in chunks behind the welcome while the transport has room (`ISessionTransport.ReliableRoom`), then
  `JoinDone`. The edits made since follow it. The client refuses a payload over `MaxJoinMegabytes` before downloading
  it. A game message that arrives before `JoinDone` is dropped.
- **Edits.** After each fixed step the host sends each joined client the edits it has not been sent, each with its
  maker. The client applies them in order. A gap or an invalid edit ends its session with an error. After loading, a
  client's world follows the host (`VoxelWorld.FollowHost`): `Apply` and `TryLift` throw, so a mod or a game checks
  `IsAuthority` before editing.
- **A game's messages** are kinds from `SessionService.FirstGameKind`, each handled by an `ISessionGameHandler` that
  states which way its kind travels (`SessionMessageDirection`). The session drops a peer that sends a kind meant for
  the other direction, and a peer whose handler throws. A roster message gives every game each slot's player and its
  join, a number the host gives each join, so a per-slot system forgets what it kept for a slot whose join changed
  (`SlotJoins`). A game tests its messages with damaged bodies and hostile values in every field.
- **Replication** (`Replication/`, `.claude/rules/replication.md`, the reasons in `docs/replication.md`). A game
  registers kinds of entity at its run's start (`ReplicatedKinds`) and marks entities on the host
  (`ReplicationService.Add`). Thirty times a second the host sends each client the entities in its reach whose state
  the client has not acknowledged, highest priority first. Each goes as its changes from the client's last
  acknowledged state, in unreliable packets that fit one transport packet, until that client's budget is spent. A
  client replies every step with the packets it read, estimates the host's tick, and shows each entity 100 ms behind,
  blended between the states round that time.
- A client's own player is predicted (`Predict`): it moves at once from its commands, which go to the host with every
  reply. The host moves its copy from them and sends the client its state after the last command applied. A client that
  predicted otherwise takes the host's state, runs its later commands again (`CharacterStepper`), and eases what is
  drawn (`EyeSmoothing.Correction`). A client's tool use is a request: the host applies it and sends the numbered edit
  to everyone, so nothing shows early on the client.
- **The host quitting** says so (`Bye`). Its clients go back to the menu with the reason, or, when the options started
  the match, end with exit code 0. A lost connection or a protocol error does the same, with exit code 1 for a match
  the options started. Each match has a session of its own (Scoped), on the run's transports, which close at its end and
  open again for the next.
- **Saves.** `SaveService` writes the host's or an offline match's world and the game's own state (its
  `ISaveSection`s, written after the edit log) to `Saves/NAME.save` in the data folder. It saves on a timer
  (`Engine:Save:AutosaveMinutes`), at the console's `save`, and when the match ends cleanly.
- A save is captured on the main thread between steps, with the edit log frozen (`EditLog.Freeze`), so its written
  edits never change. An autosave is written on a background thread, so play does not stall. The stream goes to a
  temporary file, the old save is kept as `NAME.save.bak`, and any failure leaves the old save. A damaged file starts
  the match from the backup with a warning. A save of another generator, generator version, or step rate, or of a newer
  build or section, is refused before play, so no write replaces it. A client never saves.

### Steam

`SteamService` starts the Steam API at startup when `Engine:Steam:Enabled`, the game has an App ID (`Engine:Steam:AppId`),
and the Steam client runs. Without the client, or where Steam has no library (Linux and Windows on arm64), it logs a
Warning and the game plays offline or over Direct. Starting the Steam API loads Steam's overlay and fossilize Vulkan
layers into the game. The overlay draws one best-practices warning of its own, so `validate.sh` runs with Steam off.
The rules are in `.claude/rules/sessions.md`.

- A match through Steam (`Transport=Steam`) carries its session on `SteamTransport`. A host listens on a P2P socket
  through Steam's relays, so no port is opened. It opens a friends-only lobby and sets its rich presence's `connect`,
  so a friend who picks "Join game" gets the game started with those arguments (`SteamLaunchArguments`). A host
  accepts a connection only from a member of its lobby or a Steam friend. It checks a friend first, because Steam may
  tell it of a new lobby member only after that member's connection arrives.
- The menu's join page lists the friends who host (`SteamFriendList`), with "Other version" where a lobby's match key is
  not this build's.
- The handshake, the join, and the edits are the same messages as on LiteNetLib. Steam refuses any message while a
  connection's send buffer is full, and a reliable message over 512 KB. So the transport skips a sequenced message
  while too much is queued to its peer, holds a refused reliable message with every later one behind it to send again
  in order at the next poll, and ends a peer with too much held.
- Test builds reach testers through Steam: `tools/steam/upload.sh` publishes Release builds for `win-x64` and
  `linux-x64` (self-contained) and uploads them with steamcmd to a branch; its README has the setup.

### Navigation by flow fields over a grid

Navigation (`NavigationService`, host and offline only) fills a grid of 50 cm cells (`NavGrid.CellCentimetres`) on a
thread of its own from a game's `INavGridSource` and builds the flow fields the game asks for. A game without a source
has no navigation. The rules are in `.claude/rules/navigation.md`.

- **The grid** (`NavGrid`) is one layer of cells, each with its floor, its headroom, and a cost class. The game's
  source fills a rectangle of it from its own knowledge of the world (the layout as generated, not generated voxels).
  Floors are absolute centimetres in an `int`, so any map's heights fit.
- **The window** (`Engine:Navigation:WindowCells`). A map that fits is covered whole and never moved. On a larger one
  the grid is a square that follows a focus (`NavigationService.Follow`), moving by tiles. The thread fills only the
  strips coming in and stamps on them the logged edits since the last reset that reach them. Each field keeps the
  window it was built over, and a goal past the window stands in at its nearest edge cell, so walkers head that way
  until the window reaches it. So the cost of a field depends on the window and not on the map's size.
- A cell's rule, from the source or from the voxels (`NavGridScanner`), at its centre: anything rising at most
  `LowCentimetres` over the ground raises the floor, anything higher blocks the cell, and the headroom runs to the
  lowest blocking voxel over the floor. Passable materials count as air. A read takes the floor from the density
  surface over the highest blocking voxel (`VoxelWorld.SurfaceOver`).
- **Walkable for a size** (`NavSize`: a height, a radius, and a climb). Entering a cell costs its class's price
  (`NavCostTable`), or the break cost where the size cannot stand, or the steep cost for a rise over the climb. A block
  can be a price to break through or a wall to walk round. Blocked cells are dear, not closed, so every cell has a
  route.
- **The fields** (`NavFieldSpec`: size, costs, goal; `FlowFieldBuilder`) run Dijkstra backwards from each field's goal
  over the eight neighbours, with a ring of buckets as the queue, since every cost is a small whole number. Each cell
  records its step towards the goal. The builder allocates nothing once its arrays have grown. The engine knows
  nothing of teams; a game's fields are its own.
- **Edits** (`NavigationService.AfterFixedStep`). Each new edit's cells are read again from the voxels when its
  columns are loaded, at a limited count a step, and otherwise stamped from its shape (a dig lowers the floor, stone
  raises it or blocks the cell) and read once the voxels load. Changed rows are copied to the thread's own grid, and at
  most every 0.5 s the thread builds every field again while walkers keep the old ones until the new are swapped in
  whole. A partial recompute was measured first and not built, because a whole rebuild runs off the main thread in about
  a second. After a reset the thread fills the grid again first.
- Moving walkers along a field, spreading them in lanes, steering round each other, and breaking through blocks are a
  game's. `NavigationDebugLines` draws a field's arrows round the camera.

### The UI is drawn by Skia into a CPU surface and blended over the picture

The rules are in `.claude/rules/ui.md`, with input and the console in `ui-input.md` and the menu in `ui-menu.md`.

- `UISurface` draws its layers (`IUiLayer`) into memory it owns, four bytes a pixel, at the size of each frame's
  request, so the surface and the pass always agree. The order is the run's layers below the match's, then the match's
  (`IMatchUiLayer`s, a game's HUD), then the run's above them: the menu and the console. It draws only when a layer
  changed, appeared, went, or moved, and then only the areas of those layers, up to four, and reports each area's rows
  as a band. So an unchanged UI costs nothing, and a HUD changing in several corners copies only their bands, not the
  rows between them. A layer's bounds hold everything it draws, which each bounded layer's tests check (`LayerInk`).
- Skia's text calls allocate, so the UI allocates when it draws and never on a frame with nothing to draw. Skia's Vulkan
  backend is a later optimisation.
- `UiPass` keeps the pixels in a device-local storage buffer the size of the swapchain image, not an image. The
  fragment shader (`ui.frag`) reads the pixel at `gl_FragCoord`, so no layout change or sampler is needed. The main
  thread writes the changed bands into the slot's region of an upload buffer of its own, and the render thread copies
  them, a region each in one copy, before the draw. Skia premultiplied in sRGB, so the shader divides by alpha, turns the colour linear, and
  multiplies again before the premultiplied blend into the sRGB image. The blend is in linear light, so see-through UI
  shows more of the world than its colours say; it is left as it is. A screen larger than the device's largest storage
  buffer gets no UI and a Warning.
- **One sizing rule** sizes the UI: a unit is a pixel of a 1080-pixel-high screen, scaled with the screen's height and
  the player's UI scale. So the menu, the console, the stats line, and the loading list keep their share of the screen
  at every size. The font is JetBrains Mono Regular (SIL Open Font License 1.1, its licence beside it in
  `src/Itera.Engine/UI/Fonts/`), so text looks the same on every platform.
- **The console** (`DevConsole`, `ConsoleLayer`) takes the input instead of the game while it is open. Its commands
  (`ConsoleCommands`) have a scope: `Local` commands change only this game, `Match` commands work offline or on a host
  since they change what the session shares, and `Offline` commands work offline only. A game registers its commands
  in `Initialize`, and a match's commands go when it ends.
- **The menu** (`MenuLayer`, a layer of the UI toolkit) is the run's. A game gives it a look (`MenuStyle`), adds pages
  (`MenuPages`) and Settings rows (`SettingsRows`), or swaps it for its own (`IMenu`, which can show the engine's
  `SettingsPage`). It reaches the run through public services: `MatchControl`, `MatchCommands`, `SteamFriendList`,
  `JoinRequests`, and `SaveFiles`.
- A game's own text box takes the typing while it holds it (`IFrameInput.SetTextFocus`, `TextEntry`). Characters come
  from GLFW's own callback as whole code points, since Silk.NET's `KeyChar` cuts one past U+FFFF to 16 bits. Escape
  goes to the console, the menu when open, the game's text box, the game's screens (`IEscapeHandler`, newest first), its
  panel, and then opens the menu.

### A run plays many matches

A match is a DI scope (`MatchServices`, section 4). Every service holding a match's state is Scoped, so a new match gets
new ones and the old are freed with the scope, which no reset method could promise for the dozens of stores a game
keeps. A run-wide service that touches the match is handed it (`WorldMeshes.BeginMatch`, `UISurface.SetMatchLayers`,
`ConsoleCommands.BeginMatch`, `ParticleColours.Tint`, `MatchControl.Current`, `LoadingScreen.Initialize`,
`ModLoader.MatchStarted`, a game's panel and drawn pointer through `IFrameInput.SetPanel` and `SetDrawnPointer`, and
the fixed systems' `SystemTimes`) and drops it at the end. DI's scope checks catch a singleton that takes a scoped
service. `MatchLifetimeTests` plays several seeds in one process and checks that nothing of an ended match stays: its
services collected, its voxel memory freed, its Arch world gone, the console's commands back to the run's.

The render thread runs from start to exit. Every Vulkan object is made once, the mesh heap is sized from the options,
and a match's GPU state is CPU bookkeeping. Stopping the thread could time out while presentation blocks, which
abandons the graphics. With no match's world, frames are the loading screen's ring with the UI over it. The session
starts without blocking (`SessionService.Begin`, then `PollJoin` once a loop iteration, the menu showing the download),
so the window answers while a client joins. The warm-ups run once per run (`WarmUps`). Leaving a match while it loads
waits for the navigation thread (5 s at most).

### A render scale with FSR 1, and dynamic scaling

`Engine:Renderer:RenderScale` and the console's `scale` set the share of the screen's size, on each axis, that the
world, the models, the particles, and the debug lines draw at. The main thread builds the view at that size. The render
thread sizes the drawn image from the swapchain's size and the frame's scale (`RenderScale.Apply`). `UpscalePass` owns
the source image and the upscaled image, both the screen's size, made on the render thread the first time a frame is
drawn below 1 and again for a new screen. The picture fills the source's top-left part, as the depth image holds it, so
a new scale makes no new image. EASU takes the drawn size as its input viewport, and holds its reads near the picture's
right and bottom edges inside it. Nothing waits for the device: each frame slot has its own descriptor sets, pointed at
the images only after that slot's fence, and replaced or retired images are destroyed once the frame before has passed its fence
(`FrameImages`). EASU reads the source through the UNORM view, because FSR 1 wants sRGB-encoded values. RCAS writes the
swapchain image. The UI draws after it at full size. FSR 1 runs on any GPU brand; FSR 2 and DLSS were not chosen.

`DynamicRenderScale` (`Engine:Renderer:DynamicRenderScaling`) runs on the main thread before each view is made. It
steers by the GPU's time of the whole frame (`GpuTimings`), not the frame's wall time: the scale changes only the GPU's
work, so a frame the CPU holds up would only lose sharpness. The target is the lower of
`Engine:Renderer:DynamicScalingThreshold` and the screen's refresh rate (`WindowService.RefreshRate`), VSync on or off.
The threshold is 60 by default: a higher one lowers the scale on a fast screen and softens the picture. It aims for
95 % of the target's frame time.

Much of the GPU's time does not shrink with the pixels: the shadow maps, skinning, uploads, the particles' simulation,
image views, the UI, and the upscale itself. So each frame's time is split into that fixed part
(`DynamicRenderScale.FixedPasses`, from the frame's own pass times, `GpuTimings.LastFrame`) and a rest that follows the
square of the scale. A step looks at 8 frames. It predicts their worst frame as their mean fixed part plus their
largest pixel part, the 90th percentile of 8. When that is over the aim, the scale drops at once to where it meets the
aim, or to the least when the mean fixed part alone is over it. A square root of the whole time's excess would drop too
little and take several steps. The largest pixel part catches single slow frames that a mean would let through. A frame
slow in its fixed passes, such as a large upload, does not move the scale, because a lower scale cannot make it faster.
When the mean is under 90 % of the aim, the
scale rises by a small step, so it does not swing. Between the two it holds. Off, the scale is the settings' Render
scale, from `Engine:Renderer:RenderScale`, which the benchmark sets with scaling off; the console's `scale auto` gives
control back.

Effects give way before the resolution, as a game's readability needs. `EffectsBudget.Share` is the share of cosmetic
effects drawn, under the player's Effects detail. A step over the aim lowers the share first and the scale only once
the share is at its least. A step with time to spare raises the scale first and the share only at scale 1. Detail
particle recipes thin by the share, and a game's grass source may read it. The engine never thins what play depends on.

### Anti-aliasing with SMAA 1x

AMD asks for an anti-aliased, tone-mapped, sRGB picture before FSR 1. CMAA2 is cheaper but lacks diagonal handling and a
GLSL version. Only TAA fixes features under a pixel, at FSR 2's complexity (`docs/anti-aliasing.md`). `SmaaPass` runs
SMAA's three passes (`smaa.glsl`, SMAA.hlsl unchanged, MIT, the HIGH preset) between the world, models, particles, and
lines and FSR 1. With it on, they draw into a scene image of the drawn size at every scale, and the blend writes the
swapchain image (at scale 1) or FSR 1's source.

- It runs only when the world is drawn with one sample a pixel (`MsaaSamples` 1); 4x MSAA is the default.
- Edges are found by luma in sRGB-encoded values and blended in linear light.
- SMAA's lookup textures are copied in the first anti-aliased frame from a buffer the main thread filled before the
  render thread started.
- The console's `aa edges` and `aa weights` show what SMAA found.
- It smooths the steps along edges. It does not fix features under a pixel that flicker. SMAA's stencil step and depth
  predication are not built.

### Particles live on the GPU

Particles are cosmetic: they are never sent over the network, nothing is read back into the game, and they cost the main
thread only the bursts. The rules are in `.claude/rules/particles.md`.

- Game code queues bursts (`ParticleBurst`) with `ParticleEmitter.Emit`, from fixed or frame systems. They wait for the
  next frame built, at most `Engine:Particles:MaxBurstsPerFrame` bursts and `MaxSpawnsPerFrame` particles, and the rest
  are dropped with one Warning.
- A recipe (`ParticleRecipe`, registered by name in `ParticleRecipes`) gives the ranges each particle draws its own values
  from, and how it moves and looks. The whole recipe table is sent every frame. `StockEffects` registers seven general
  recipes a game may use.
- The pool (`Engine:Particles:Capacity`) is a ring. When particles are made faster than they die, the oldest are
  overwritten.
- `ParticlePass` runs after the world's pass in frames that draw the world while particles can be alive. A spawn
  dispatch, then a simulate dispatch over the pool's span that can be alive, move each particle, meet the world, and
  list those in view as cubes or sprites with atomic counts that are the two indirect draws' instance counts. A
  counting sort by bins of depth orders the sprites back to front. Then the cubes (tumbling to a stop, lit as the world
  is, writing depth) and the sprites (premultiplied, testing depth only) are drawn.
- Particles meet the world through the camera's depth image: a particle that moves to just behind the surface the image
  shows bounces off it, sticks, or dies as its recipe says, with the slope from the neighbouring depths. Off the image,
  behind something, or near the camera it meets nothing, and a resting particle falls again where the image shows its
  support gone. A lit particle takes its sunlight from the shadow cascades
  and its sky light from the sky field.
- Large blended sprites cost fill rate, not count.

### Sound is made in code

Sound is made from models of physics, after Andy Farnell's *Designing Sound* (`docs/procedural-audio.md`). Synthesis is
good and cheap for physical sounds (hits, footsteps, digs, wind, fire, explosions) and poor for voices and music. The
rules are in `.claude/rules/audio.md`.

- A recipe is a small model of a sound's physics driven by one number, its level (0 to 1), registered by name in
  `SoundRecipes`. A `OneShotRecipe` is rendered at start, `Levels` times `VariationsPerLevel` variations at fixed seeds,
  into OpenAL buffers. An event plays the variation nearest its level, never the same one twice running, with its own
  gain and pitch.
- The rendered buffers are kept in `SoundCache/` in the data folder, in a file per recipe. Its key holds the format,
  rate, levels, variations, name, the build's configuration, and the sound code: a hash of the source files under the
  `Audio/` folders of the recipe's assembly and the engine, written as assembly metadata by `Directory.Build.targets`.
  So a later start reads the cache in about 0.01 s, and only a change to sound code renders a recipe again
  (`Engine:Audio:Cache` turns it off).
- A `StreamRecipe` (a beam, wind, fire) makes a `SoundGenerator` that the audio thread runs live in small blocks, with a
  few blocks queued, following the level the game gives it.
- Game code queues one-shots with `AudioService.Play` and holds streams with `Sustain`: a stream plays while it is
  sustained, for a moment after the last, then fades out. `Publish`, once a main-loop iteration after the frame, hands
  them and the listener (the camera) to the audio thread through `AudioExchange`, which is one lock around fixed
  arrays.
- A render thread (`RecipeRenderer`, below normal priority) renders the one-shot recipes in the order registered. The
  audio thread (`AudioMixer`) places the listener, starts one-shots, refills streams, frees ended voices, and puts the
  next rendered recipe into buffers. Sounds play while recipes render. A one-shot whose recipe is not in buffers yet is
  dropped as late and counted.
- A recipe that throws is logged and stays silent, and a stream whose generator throws is ended. The other sounds play
  on.
- A pool of `Engine:Audio:MaxVoices` OpenAL sources plays everything. A sound whose gain at the listener is too low is
  never started, and when every voice plays, a louder sound takes the quietest one-shot's voice. Once a second the
  mixer asks the device whether its output is still there and moves to the default output when it went.
- Sound is cosmetic and never sent over a session. Recipes need no determinism across machines (`MathF` is allowed),
  but every filter and resonator state is flushed below 1e-20 once a block, since .NET cannot flush denormal floats. With
  no device, or `Engine:Audio:Enabled` false, no thread starts and every call does nothing.
- The engine's own recipes are the menu's clicks and two test sounds (`EngineSounds`). A game registers its own in an
  `IRunStartup`.

Sounds can also be read from files, and music streams from them (`docs/sound-files.md`). A `FileOneShotRecipe` (several
files, each a variation) or `FileLoopRecipe` (one file, played while sustained, round its loop points) is a recipe like
any other. The sound render thread reads its WAV or Ogg Vorbis files whole, and the audio thread puts each into a buffer
at its own rate.

Music (`MusicPlayer`, `AudioService.Music`) is tracks declared by name, one heard at a time. A music thread decodes
ahead into rings. The audio thread plays two decks of its own beside the voice pool and cross-fades them at equal power.
A loop goes round its points with no gap through a second decoder waiting at the loop's start. The volumes are the
master (the listener's gain), and under it Effects (every voice of the pool) and Music (the decks). A file that cannot
be read is logged at Error and plays nothing.

### Vulkan, shaders, threads, and GPU memory

- **Vulkan 1.3 is the minimum** on the loader and the device. Dynamic rendering and synchronization2 are core.
  `DeviceRequirements` lists every required extension and feature, including `scalarBlockLayout`, `multiDrawIndirect`,
  `drawIndirectFirstInstance`, `textureCompressionBC`, and `D32_SFLOAT` as a depth attachment. RADV, NVIDIA, Intel, and
  MoltenVK 1.4 all expose 1.3.
- **Shaders are GLSL files compiled at runtime** with Shaderc for Vulkan 1.3, with `#include`, and cached under the data
  folder. The cache key covers the expanded source and the compiler settings.
- **Rendering runs on its own thread**, so no presentation stall can stop the simulation or networking, which a
  host-authoritative game needs. The render thread pulls one frame at a time from the main thread (`FrameExchange`); ECS
  stays on the main thread. Frame systems and mods' `OnUpdate` pause while no frames are drawn; fixed steps never do.
- **GPU memory uses Itera's own block sub-allocator** (`Rendering/Memory/`), with no VMA binding. Blocks are 64 MB per
  memory type, or an eighth of a smaller heap. A dedicated allocation serves resources of half a block or more. Buffers
  and optimal-tiling images live in separate blocks, and host-visible memory is mapped once. The main thread writes
  upload data into the staging region of the frame slot it builds (`StagingBuffer`); the render thread records the
  copies.
- **Structs that shaders read use scalar block layout** (`layout(scalar)`, the Vulkan 1.2 `scalarBlockLayout` feature),
  so they match .NET's layout without padding. Each has a test that checks it against the compiled SPIR-V. Entity data
  reaches the GPU as a copy made each frame; Arch chunk memory cannot be handed to Vulkan directly.
- **Validation.** Debug builds enable `VK_LAYER_KHRONOS_validation` with synchronization and best-practices checks, and
  validation errors reach the log at Error. `.claude/skills/vulkan/validate.sh` runs the game and fails on any
  validation warning or error.

## 9. Open work

Engine work that is known and not done is in Itera's [GitHub issues](https://github.com/salexanderj/Itera/issues).
Features not built carry the label `enhancement`, and bugs the label `bug`. The `issues` skill says how to file, find,
and close them.

## 10. Platform support

Targets: Linux, Windows, macOS, x64 and arm64. `Game` declares the runtime identifiers (`IteraSupportedRids`) so
`dotnet publish -r <rid>` works without edits.

Verified so far: Linux only (Ubuntu, Wayland, AMD RX 5700 XT on Mesa RADV, .NET SDK 10.0.112). Steam runs on Linux x64.
macOS x64 and Windows x64 have its libraries but are unverified, and arm64 plays without Steam. CI builds and runs the
tests on Windows x64 and macOS arm64 each night (`.claude/rules/ci.md`), with no GPU, window, or sound device. The game
itself has not run on either. What is in place for them, and what is not:

- Every native package ships binaries for all three platforms, including MoltenVK for macOS. But no package ships a
  Vulkan loader for macOS. GLFW for macOS looks only for `libvulkan.1.dylib`, Silk.NET.GLFW 2.23 has no binding for
  `glfwInitVulkanLoader`, and Silk.NET.MoltenVK.Native ships the driver and its ICD file but no loader. So on a Mac
  without a system loader the window step ends the run (exit code 1).
- The fix, to be made and checked on a Mac: load `libMoltenVK.dylib`, create `Vk` on it, and pass its
  `vkGetInstanceProcAddr` to `glfwInitVulkanLoader` (a P/Invoke into the same `libglfw.3.dylib`) before any windowing
  call. The other way is to ship the Khronos loader in an app bundle.
- On macOS the Vulkan context enables `VK_KHR_portability_enumeration` and, when the device exposes it,
  `VK_KHR_portability_subset`. The context requires Vulkan 1.3, which the bundled MoltenVK 1.4 provides. Presentation
  runs on the render thread, not the main thread. MoltenVK allows that, but it has not been run on a Mac yet.
- Paths use `Path.Combine`; string comparisons are ordinal; the shader cache and the data folder live under
  `Environment.SpecialFolder.LocalApplicationData`.
- `.gitattributes` normalises line endings so checkouts hold the same bytes on every platform.

Linux notes: Wayland cannot set window position or icon. Pointer lock for mouse look works on Wayland (Sway, GLFW 3.4.0
through Silk.NET 2.23). GLFW offers raw motion, so the pointer uses `CursorMode.Raw`. Movement keeps arriving at and past
the window edge, the main thread allocates nothing extra while the mouse moves, and the pointer reappears where it was
on release. Not yet seen: losing focus while the pointer is captured (the code releases the pointer then). A left click
captures the pointer and Escape frees it.

The validation layer (`vulkan-validationlayers`) and `vulkan-tools` are installed on the development machine, so Debug
builds run with validation on, including synchronization and best-practices checks (set through `VK_EXT_layer_settings`
when the layer offers it). They are optional elsewhere. Debug builds warn when the layer is missing and continue.

Tooling: LazyVim with the `lang.dotnet` extra (OmniSharp, csharpier, netcoredbg). csharpier is also a local dotnet tool
pinned in `dotnet-tools.json`, so `dotnet csharpier check .` works after `dotnet tool restore` on any machine. The
solution is in the classic `.sln` format because OmniSharp does not open `.slnx`.
