# ARCHITECTURE.md, sections 1 to 7: text moved out

A record of what the clean-up of `ARCHITECTURE.md`'s sections 1 to 7 took out of it. Each block is the old text,
verbatim, with the lines it stood on. The docs rules keep dates, measurements, and accounts of the starter game out of
that file. Nothing here is kept current.

## Zorf TD, shelved (old lines 21 to 24)

This was true in Itera's own repository and not in a game's clone, so section 1 no longer says it.

> Itera was built with a tower defence game, Zorf TD, which is shelved: its code, docs, and assets are kept in the archive
> repository `salexanderj/ZorfTD-archive` on GitHub (private; its `main` ends at the tag `zorftd-shelved`), and nothing of
> its design is part of the framework. Some figures below come from that game's world (the benchmark's views, a crowd of a
> thousand goblin models); they are named as measurements.

## The starter's values in the numbers table (old lines 30, 31, and 35)

> | World voxel edge | 1 cm to 1 m at level 0, set by the generator's `VoxelGrid` (10 cm in the starter); level k is twice level k-1; models have a unit of their own, 1 cm in the starter |
> | World extent | whole level-0 chunks a game sets in its generator's grid, up to ±160 km each way, up and down too; by default about ±512 m, from -51.2 m to +102.4 m at 10 cm |
> | View distance | 256 m by default (`Engine:Renderer:ViewDistanceMetres`, up to 8 km); the starter's 8 km over 9 levels |

## Why GLFW's timed wait is not used (old lines 240 to 242)

The reason stays in section 4 without the duration.

> requested, sleep on a managed event until the next fixed step is due (a request for a frame from the render thread ends
> the wait at once; GLFW's own `glfwWaitEventsTimeout` is not used: on Wayland it once spun at full load inside the call
> for 16 minutes after a focus change), read the clock (delta clamped to 0.25 s), run Steam's callbacks, poll the session

## The world upload's cost (old lines 270 to 274)

> `MeshView`), and the world's meshes (`WorldMeshes.Upload`: it follows the voxel change list, reads blocks and starts
> their meshing nearest the view first and stops reading after 2 ms, stages the finished meshes, the models' too, into the
> slot's staging region, picks each column's level, and writes the frame's block draws, the models' draws from
> `ModelInstances`, the point lights, and the shadow cascades' lists into the slot's regions, and each due view's lists
> into its own; it takes 0.12 to 0.35 ms a frame when nothing loads), the mesh assets' lists (`MeshAssetDraws.Write`, the

## Shader reload times (old lines 310 to 313)

> - **Shader reloads.** Before recording the frame, the render thread compiles every pass's shaders from
>   `Engine:Renderer:ShaderSourceDirectory` (`IReloadsShaders`). Only when all of them compiled does it wait for the device
>   and make their pipelines again; an error keeps the old ones and is reported with its file and line. It takes about
>   0.6 s for one edited shader and 10 ms when the SPIR-V cache holds them all. Idle iterations allocate nothing.

## The loading screen's times (old lines 315 to 330)

> **Loading screen (`LoadingScreen`, `Engine:Loading`).** From the loop's first iteration the loading screen covers the
> picture until every `ILoadingTask` is done: the engine's Sounds (every one-shot recipe in buffers, rendered or read from
> `SoundCache/` in the data folder), Physics (the physics warm-up), World (every column the streaming centres want, at every level, resident, then an
> upload that has seen them), Navigation (the grid and fields taken, or none on a client or without a source), and Warm-up
> (the edit warm-up), then the game's own tasks, registered after them (`ModelsLoadingTask` waits for a list of models to
> reach the GPU). Loading is a phase of the loop, not a wait before it: streaming, uploads, fixed steps, the session, and
> the console run as usual, because most of what it waits for moves on only while they do. While it covers the picture the
> game gets empty input snapshots (Escape and the console still work), fixed steps dropped are reported in its own line
> instead of the frame stats' Warning, and the frame carries a `LoadingDraw`: the render thread still records the mesh
> copies and the particle simulate call, then draws `loading.frag` (a retro ring of voxels, `LoadingPass`) over the whole
> swapchain image instead of the world, then the UI, where `LoadingLayer` lists the tasks. When all are done, or after
> `MaxSeconds` (default 15, a Warning naming what had not finished), it fades out over 0.3 s, blended over the world. The
> starter's covers the picture for about 3.5 s in Debug (the world; the edit warm-up runs one round there, since Debug
> code is never optimised) and 2 s in Release. After a reset of the world (the console's `reset`, or the host's on a
> client) it covers the picture again (`LoadingScreen.BeginAgain`) and waits for every task afresh: the tasks already done
> report done at once, World waits for the view generated again, and Navigation for the grid filled again.

## Offscreen frames at 60 (old lines 332 to 336)

> **Window modes (`Engine:Window:Mode`).** A run draws into a window; or offscreen (`Offscreen`), at
> `Engine:Window:Width` x `Height` into a headless surface (`VK_EXT_headless_surface`) with nothing on the desktop, for
> screenshots and checks; or with no graphics (`None`): no window, Vulkan, or render thread, while the fixed steps and the
> frame group run. Nothing paces a headless present, so offscreen frames are capped at `Engine:Window:OffscreenFps` (240
> by default): the world uploads a share each frame, and at 60 the starter's view did not load in 15 s.

## The cost of tiered compilation off in tests (old lines 435 to 440)

>   numbers are the whole-loop check for the zero-allocation rule: in the steady state the main thread's should come only
>   from log lines, and the render thread's should be 0. Each system's own zero-allocation test runs with tiered
>   compilation off in the test projects, so every method is optimised at its first call and no test measures what
>   unoptimised library code allocates; the suite takes about 4 % longer for it. A Debug line gives the fixed steps' CPU
>   time by part (the systems, the large edits, the voxel colliders, the physics, the bodies as voxels, and the step's
>   tail: the mods, navigation, the session, and the saves) and the eight slowest fixed systems (`SystemTimes`).

## Diagnostics detail (old lines 416 to 468)

The log's header, its retention, and the stats lines' contents. The `run-game` skill's `startup-log.md` describes the log in less detail.

> **Diagnostics.** Every run writes one log file, `<data folder in lower case>-<date>-<time>-<pid>.log`, under
> `LocalApplicationData/<DataFolder>/Logs` (`~/.local/share/<DataFolder>/Logs` on Linux; the folder comes from
> `Engine:Application:DataFolder`). The file receives every level the global minimum allows (Debug by default); the console
> shows Information and above. Serilog sits behind `Microsoft.Extensions.Logging`, so engine and mod code only use
> `ILogger`.
>
> - The file starts with a session header: product version with git commit, build configuration, OS, architecture,
>   runtime, processor count, memory, process id, GC mode, debugger state, environment, base directory, command line, log
>   file path, and a few engine options: the window's size and VSync, the preferred device, validation, the mods' folder,
>   the step rate and the most steps a frame, the console's level, the stats' interval, and the voxels' radius, budget,
>   workers, seed, and walk speed (as the options give them; nothing from `settings.json`, which the settings step reads
>   later).
> - `GameLoop` logs the duration of every initialisation step, the reason the loop ended, each shutdown step, and a final
>   line with uptime and exit code. A shutdown step that throws is logged and skipped so the remaining steps still run.
> - `FrameStats` logs one line every 10 seconds: frames built, average and maximum time between frames (measured, not
>   clamped) with the median, 95th, and 99th percentiles, fixed steps, dropped steps, managed bytes allocated on the main
>   thread and on the render thread (in total and per frame), garbage collections per generation, heap size, and the
>   average and longest GPU time per frame (timestamp queries; a Debug line gives each pass). Frames and steps are counted
>   separately, so the line still appears while no frames are drawn. Dropped steps also produce a warning. The allocation
>   numbers are the whole-loop check for the zero-allocation rule: in the steady state the main thread's should come only
>   from log lines, and the render thread's should be 0. Each system's own zero-allocation test runs with tiered
>   compilation off in the test projects, so every method is optimised at its first call and no test measures what
>   unoptimised library code allocates; the suite takes about 4 % longer for it. A Debug line gives the fixed steps' CPU
>   time by part (the systems, the large edits, the voxel colliders, the physics, the bodies as voxels, and the step's
>   tail: the mods, navigation, the session, and the saves) and the eight slowest fixed systems (`SystemTimes`).
> - `VoxelWorld` logs one line per stats interval: columns resident and pending, mixed chunks, stored bricks, memory used
>   and reserved against the budget, columns loaded and unloaded, jobs that ran out of memory, average and maximum column
>   job time, the longest `Update` split into install, rebuild, unload, and queue, and edits. These come from running counts,
>   so the line costs nothing to build. Reaching the memory budget logs a Warning, at most one a minute; loading again logs
>   Information.
> - `WorldMeshes` logs one line per stats interval: the columns it follows, the blocks with meshes and those pending, the
>   mesh jobs started, the meshes uploaded and the empty ones, the megabytes uploaded, the mesh heap's use against its size,
>   the blocks and triangles drawn in the last frame and into each shadow cascade, the models followed and meshed and the
>   model blocks drawn, resyncs, the lights shaded, and the main thread's time in the upload (average per frame and
>   longest). `MeshPass` logs the heap's size, the draw limit, and the samples a pixel at start. A full heap logs a Warning,
>   again only after it had room between, and a frame whose block list or model list is full logs one once a match.
>   `tools/benchmark/parse.py` reads these lines, so they keep their form. A `Particles:` line follows while any are alive.
> - `GameLoop.BuildFrame` logs a Warning once when a frame's models fill the 32,768 that `ModelInstances` holds; the rest
>   are not drawn.
> - When no frame was presented for more than 0.5 s (a hidden or minimised window), the render thread logs the gap at Debug
>   once presenting resumes.
> - Exceptions that escape initialisation or the loop are logged at Critical with the phase, frame, and tick, shutdown still
>   runs, and the process exits with code 1. In a match the menu or the console started, the loop's exception ends only the
>   match, at Error, and the run goes on at the menu. `UnhandledExceptionLogger` covers exceptions on other threads and
>   unobserved task faults and flushes the file before the process dies. `Program.cs` uses a Serilog bootstrap logger so
>   failures before the host exists are reported too.
> - Retention runs at startup: files beyond the newest 20 or older than 30 days are deleted. Files roll at 50 MB. All of
>   this is configurable under `Engine:Logging`; levels and per-category overrides live under `Serilog` in
>   `appsettings.json` and can be set on the command line.
> - LiteNetLib's internal log is routed through `NetLoggerAdapter`; Vulkan validation messages map to Error, Warning,
>   Debug, and Trace; the mod load context logs every assembly resolution at Trace.
> - Native crashes (a segmentation fault inside a driver or native library) cannot be caught by managed code. The file is
>   flushed to disk every second, so at most the last second is lost.

## The starter's composition (old lines 504 to 525)

The starter's own description is in its `GAME.md` and rules files.

> - The starter's `AddGame` (`GameServiceCollectionExtensions`) registers its options (`Game:Player`: the body's height
>   and whether to start flying or in third person; `Game:World:CameraPose`: where the player and the debug camera start;
>   `Game:Minimap:Metres`: how much the minimap shows across), checked by validators and `UnknownOptionKeys`; its world
>   (`GameGenerator`, `GamePalette`, `GameAtmosphere`, and a scoped `StartPose` from the match's seed, which the session
>   settles first); `GameRunStartup`, an `IRunStartup` that makes the movement keys first so the Keybindings tab lists
>   them before a game's own, then declares the fox and the minimap (its image view, its key, and its switch); the
>   minimap's HUD layer; and its systems.
> - Registration order is run order within a group, and the registration says why. The starter's two groups, in order, are
>   in `GAME.md` ("Systems"): the engine's systems with its own (`PlayerSpawnSystem`, `FoxSystem`, `MinimapSystem`) among
>   them. A game puts its own systems in this order where they belong: a system that reads the player's move goes after
>   `PlayerMovementSystem`, one that reads the camera after `CameraExtractSystem`, and one that adds models goes after
>   `LooseBodyModelSystem`, so loose bodies are never the ones left out of a full frame. `SystemOrderTests` in the game's
>   tests pins the order.
> - `appsettings.json` holds the engine's defaults for the game under `Engine:` (the name and data folder, the window, the
>   renderer, the voxels, the session) and the game's own under `Game:`. The shipped file is checked by
>   `ShippedSettingsTests`. `Engine:Steam:AppId` is 0 until the game has one; at 0 Steam stays off.
> - The starter's world is `GameGenerator`, a `HeightfieldGenerator` (rolling hills of grass with gravel on the low ground,
>   5 cm of surface over dirt to 1 m, then stone) on a 10 cm grid, with voxel objects of 5 cm voxels on it (`GameObjects`:
>   boulders, broadleaf and young trees, bushes, and rocks; placed by `GameForests` from `JitteredGrid`s and a forest
>   noise), with eight materials (`GameMaterials`), each with a look in `GamePalette` and a texture set under
>   `src/Game/Textures/` (made by `tools/Textures`, copied next to the executable). `BenchmarkViews` gives the benchmark its views of that world; runs of
>   different views do not compare.

## Mesh asset cost (old lines 563 to 568)

> - Cost, on the RX 5700 XT at 2560x1440 at the starter's views, offscreen with MSAA 4 unless said (Release, 2026-10-09):
>   the starter's one fox adds 0.015 to 0.019 ms to the world pass where it is drawn (0.7 to 1.1 %), and nothing where
>   it is not; 200 foxes (576 triangles each) in front of the `start` view add 0.20 ms standing and 0.28 ms running, of
>   which the skinning pass takes 0.05 ms and the camera's draws 0.085 ms (the rest is the cascades), 0.25 ms with one
>   sample and SMAA, 0.29 ms with eight samples, and 0.28 ms in a window. In views where they only cast shadows,
>   200 running foxes add 0.13 to 0.16 ms.

## The minimap's cost (old lines 606 to 612)

> shows a camera over its yard). Nothing draws a view while the loading screen covers the picture. The starter's minimap
> is the example: an orthographic camera at the player's eye looking down, 64 m across, every fourth frame, in the
> top-right corner; an update cost 0.74 ms of GPU time on the RX 5700 XT (1.59 ms at the world's 4x samples, which is why
> views draw with one). With no view drawn (the benchmark hides the minimap), the world pass took the same time as before
> image views on every benchmark view (-0.4 % to +0.0 %, two offscreen runs each at 2560x1440, 2026-10-10). Why views have
> no shadows or particles, draw at one sample, share the camera's terrain, and number at most four:
> `docs/rendering-extension.md`.

## The engine folders' table and the tests paragraph (old lines 108 to 190)

The folder table listed the files and classes of each `src/Itera.Engine` folder, and the tests paragraph listed what
the test projects hold. Each area's rules file lists its own files, and `.claude/rules/tests.md` lists the tests, so
both were cut to a line for each folder or project. The old text is in the repository's history.
