# Response Style & Language

- Use simple, direct, plain language.
- Use active subject-verb-object sentences.
- Strictly drop invented jargon and metaphorical abstractions (e.g., do not use
   terms like "lever," "gate," "spike," "seam," or "surface" metaphorically).
- State facts plainly without narrative "twists," dramatic reveals, or
    unnecessary hedging.

# What this repository is

Itera is a framework for 3D games in .NET 10, meant to be cloned: a game is made by cloning this repository and
growing the starter game in `src/Game` into it. The engine (`src/Itera.Modding`, `src/Itera.Engine`) is the
framework's; the game (`src/Game`, `tests/Game.Tests`, `GAME.md`) is each clone's own. Later engine changes come from
Itera with a pull from its remote, so:

- Game code stays in the game's folders. Change an engine file only to fix or extend the engine, in a way any game
  could use, and send that change back to Itera.
- This file and `.claude/` are the framework's. A game's own instructions go in `GAME.md`, imported below, and its own
  areas' rules in `.claude/rules/game-*.md`.
- Anything specific to one game belongs in the game. The engine names no game, and a game names itself to the engine
  through `Engine:Application` (`Name`, `DataFolder` for logs, saves, and caches) and `Engine:Window:Title`.

The engine streams a voxel world (`VoxelWorld`: columns at every level around streaming centres, generated on worker
threads, with edits), draws it as triangle meshes through Vulkan, runs an Arch ECS world at 60 Hz, steps loose bodies in
Bepu, plays procedural sound through OpenAL, draws its UI with Skia, carries sessions over LiteNetLib or Steam, saves a
world as its seed plus its edit log, and loads mods from `Mods/`. Input events become one `InputSnapshot` per frame
(`InputService.Frame`) and one per fixed step (`InputService.Tick`). Every match needs a world generator today; making
voxels optional is open work.

`ARCHITECTURE.md` holds the full description, the dependencies, the design decisions, and the open work. Read it before
changing anything structural. `docs/` holds the write-ups behind decisions: `voxel-structures.md`, `edit-log-compaction.md`,
`procedural-audio.md`, `anti-aliasing.md`, `rendering-redesign-brief.md` with `rendering-redesign.md` (the raster
renderer's decisions and measurements), and `framework-plan.md` (how Itera became a framework). `docs/references/`
holds notes on outside projects used as references.

# How to work

- Area details are in `.claude/rules/`, one file per area (listed below). Claude Code loads a rules file when a file it
  covers is opened with the Read, Write, or Edit tool. `cat`, `grep`, `sed`, and the Grep tool do not load it. Open the
  files you will change with the Read tool. Before you plan, discuss, or review work in an area, read its rules file.
- When you change these, also do this (details in the area's file):
  - Anything sent between games: raise `SessionProtocol.Version` and pin the new fingerprint in the engine's
    `WireFingerprintTests`. A game's own messages are the game's; test them with damaged bodies (`HostileValues` and
    `Watchdog` in `tests/Shared`).
  - What a generator makes: a changed world hash means its `Version` must rise (the starter's `GameGenerator`, pinned in
    `tests/Game.Tests/WorldTests`).
  - A new system: a zero-allocation test and a behaviour test; an engine system's in `tests/Itera.Engine.Tests/Systems`,
    a game's in its tests. A system the game registers takes its place in the game's `SystemOrderTests` and `GAME.md`.
  - A new option: its range in `EngineOptionsValidator` (the engine's) or the game's validator, and its default in the
    game's `appsettings.json` (`ShippedSettingsTests`).
  - A console command added or removed: the list in the `run-game` skill.
  - Code that reads the edit log: handle `VoxelEditOperation.Reset`, an empty box after which no earlier edit counts.
- Builds, tests, and game runs:
  - Load the `run-game` skill before you start, drive, or check the game.
  - Before you run or add tests, read `.claude/rules/tests.md`.
  - While `dotnet watch` runs, it builds into `bin/Debug` in place; no other build or test run may share it.
  - The console's `settings`, `shadows`, and `sound volume` write the real `settings.json`; give render options on the
    command line in test runs.
- Where the details are (read the area's file before changing it):
  - Rendering, shaders, shadows, render scale, the window: `.claude/rules/rendering.md`.
  - The voxel world, edits, streaming, and world generation: `.claude/rules/voxels.md`.
  - Loose bodies, ragdolls, the character mover, and Bepu: `.claude/rules/physics.md`.
  - The engine's components and systems (the player, cameras, streaming, the benchmark): `.claude/rules/systems.md`.
  - Poses, clips, and foot IK: `.claude/rules/animation.md`.
  - Particles: `.claude/rules/particles.md`.
  - Sound: `.claude/rules/audio.md`.
  - UI layers, the menu and settings, the loading screen, and input: `.claude/rules/ui.md`.
  - Sessions, Steam, saves, and what peers send: `.claude/rules/sessions.md`.
  - Navigation and flow fields: `.claude/rules/navigation.md`.
  - The engine's composition and its other folders: `.claude/rules/engine.md`.
  - Tests: `.claude/rules/tests.md`.
  - Running and driving the game (options, logs, the console's commands, the control port): the `run-game` skill.
  - Arch's package notes: the `ecs` skill.

## Subagents

- For read-only work that splits into independent parts (searching or reviewing many files, checking a list of
  findings against the code, reading logs or reports, web research such as techniques, documentation, or claims to
  verify), use Sonnet 5.5 subagents in parallel instead of doing it serially: the `reader` agent
  (`.claude/agents/reader.md`, Sonnet at high effort, read-only, with web search and fetch). Give each one part and
  everything it needs to know, since it starts without the conversation. Check a subagent's claims before acting on
  them.
- While a benchmark, test suite, or validation run is going, have readers review the change made so far (one part
  each: engine, shaders, game, tests and docs).
- Self-contained parts of a broad change with a clear spec (a CPU-side class and its tests, option plumbing,
  mechanical edits, test rebasing, tool scripts) may go to `writer` agents (`.claude/agents/writer.md`), each in its
  own worktree; it fast-forwards to local `main`, so commit what it needs first. It loads its area's skills itself
  (the hook checks each agent's own). Review its staged diff, apply it to main, build, test, and commit here, then
  remove the worktree and its branch.
- Keep design decisions, Vulkan passes, look-setting shaders, anything sent between games, game runs, benchmarks,
  validation runs, and commits in the main session.

# Starting a game from Itera

1. Clone Itera into the game's folder and keep Itera as a remote (`git remote rename origin itera`), to pull engine
   changes later.
2. In `src/Game/appsettings.json` set `Engine:Application:Name` and `DataFolder` (the game's folder for logs, saves, and
   caches; two games on one machine need two) and `Engine:Window:Title`; for Steam, `Engine:Steam:AppId`, and
   `src/Game/steam.env` for `tools/steam/upload.sh`.
3. Rewrite `GAME.md` for the game, and grow `src/Game`: its world (`WorldGeneration/`), its systems (registered in
   `GameServiceCollectionExtensions` in run order), its options under `Game:`, and its tests in `tests/Game.Tests`.
   The folder, project, and namespace stay `Game`, so the tools and skills find it.

# Commands

```bash
dotnet build                       # zero warnings expected; warnings are errors
dotnet test                        # tests/Itera.Modding.Tests, Itera.Engine.Tests, Game.Tests; some checks run only in Debug
dotnet run --project src/Game [-- OPTIONS]                       # opens at the menu without --Engine:Session:Mode
DOTNET_ENVIRONMENT=Development dotnet run --project src/Game     # Debug lines on the console too
dotnet build -c Release && src/Game/bin/Release/net10.0/Game     # voxel numbers: Debug generates about 5 times slower
tools/control/game.py start [--release] [--fullscreen] [--no-wait] [--menu] [-- game args]   # the game from a copy of the build, control port on; restart takes the same options
tools/control/game.py run "balls 8; pos"   # console commands in the running game; also shot, reload, gpu, input, wait, log, status, restart, stop
DOTNET_WATCH_SUPPRESS_EMOJIS=1 dotnet watch --non-interactive --project src/Game run -- --Engine:Control:Enabled=true   # C# edits applied live
tools/benchmark/run.sh <label> [game arguments]   # GPU benchmark (Engine:Benchmark) over the game's views and the circle; README there
tools/steam/upload.sh [--preview] [--no-live] [--branch NAME] [--no-test]   # test, publish win-x64 and linux-x64, upload to Steam's "testing" branch (or NAME); README there
dotnet run -c Release --project tools/Textures [-- SET ...]   # the materials' texture sets from baked recipes (src/Game/Textures/sources.json); README there
```

Game options, the start-up log lines, exit codes, unattended runs, the console and its commands, and the control port
are in the `run-game` skill.

Every run writes `~/.local/share/<DataFolder>/Logs/<datafolder>-<date>-<time>-<pid>.log` (`LocalApplicationData`
elsewhere) with Debug detail; read the newest file there first when a run misbehaves. It holds a session header, init
timings, and every 10 s a frame stats line (frames, frame times with median, 95th, and 99th percentiles, fixed steps,
bytes allocated per frame on the main and render threads, GCs, GPU time), Debug lines with each GPU pass, the fixed
steps' CPU time by part, and the eight slowest fixed systems (`SystemTimes`), a voxel stats line (columns, chunks,
bricks, memory against the budget, loads, unloads, job times), a meshes line (blocks meshed and pending, uploads, the
mesh heap, triangles drawn, the main thread's time), a `Particles:` line while any live, and the shutdown steps. The
console shows Information and above. Log lines from the render thread carry its thread id (for example `[T21]`)
instead of `[T1]`.

# Solution map

Dependency direction: `Itera.Modding <- Itera.Engine <- Game`. Mods reference only `Itera.Modding`, with
`Private="false"`.

- `src/Itera.Modding`: the mod contract only (`IGameMod`, `IModContext`, `ModManifest`, `ModManifestReader`,
  `FrameTime`, `FixedStepTime`, `ModdingApi.Version`, 2: mods hear of each match through `OnMatchStarted` with the
  match's services, and `OnMatchEnded`). No engine references.
- `src/Itera.Engine`: the engine. Its folders are listed in `.claude/rules/engine.md` and each area's file.
- `src/Game`: the starter game (`GAME.md`): `Program.cs`, `GameServiceCollectionExtensions` (`AddGame`), its world in
  `WorldGeneration/`, its systems in `Systems/`, `appsettings.json`, and the texture sets in `Textures/`.
- `tests/`: xUnit, one project per `src/` project (`.claude/rules/tests.md`).
- `samples/Itera.SampleMod`: the reference mod. Its build copies the output into
  `src/Game/bin/<Config>/net10.0/Mods/SampleMod/` and into the engine tests' `TestMods/`.
- `tools/control/`, `tools/benchmark/`, `tools/steam/`: READMEs there. They run the game in `src/Game` and read its data
  folder from its `appsettings.json` (`tools/benchmark/data_folder.py`).
- `tools/Textures`: makes the world materials' texture sets (compressed DDS files and `set.json`) in
  `src/Game/Textures/` from the bakes of the engine's recipes (or CC0 photographs) `sources.json` names; README there.

# Conventions

- Central package management: versions live only in `Directory.Packages.props`. Never put a
  `Version` on a `PackageReference`. Shared build settings live in `Directory.Build.props`.
- `TreatWarningsAsErrors` with `latest-recommended` analyzers and `EnforceCodeStyleInBuild`. Tune
  rules in `.editorconfig`; do not disable analyzers. Known tuned rules: CA1848 (suggestion),
  CA1031, CA1051, CA1062, CA2007 (off), CA1707 and CA1515 off under `tests/`, IDE0005 (unused usings) a warning,
  expression-bodied methods silent (IDE0022); CS1591 (a missing doc comment) is not enforced (`Directory.Build.props`).
- Logging in the engine uses the `[LoggerMessage]` source generator on `partial` classes. Compute
  expensive log arguments into a local first; CA1873 flags inline expressions.
- Log levels: Trace for per-frame or per-call detail (off by default); Debug for per-event detail a
  bug report needs (file only by default); Information for lifecycle milestones; Warning when
  degraded but continuing; Error when an operation failed and the game continues; Critical when the
  game cannot continue. Per-frame logging must be Trace.
- Sinks (console, file, retention, rolling) are configured in code in `AddIteraLogging` from
  `Engine:Logging`; minimum levels and per-category overrides come from the `Serilog` section.
  Code uses `Microsoft.Extensions.Logging` only; `Serilog` namespaces appear in `Diagnostics/` and
  `Program.cs` alone.
- Services have light constructors and an explicit `Initialize()`. The run's are singletons; a service that holds a
  match's state is Scoped (each match is a DI scope, `MatchServices`), and is resolved only from the match's steps on
  (some read the match's seed, which its session settles). A singleton never takes a scoped service in its constructor:
  it is given the match's when one starts (`BeginMatch`, an argument, or `MatchControl.Current`) and drops it at the
  end. Registries that refuse a name twice (recipes, definitions) are run-wide, made at the run's `game` step
  (`IRunStartup`). `Program.cs` turns on `ValidateScopes` and `ValidateOnBuild`. The order is stated in two places: the
  run's in `GameLoop`, the match's in `MatchServices`. Every `Dispose` is idempotent.
- Windowing, input, ECS, physics, the session, and mod callbacks run on the main thread (LiteNetLib's own thread only
  moves packets; its events wait for `SessionService.Poll`; the control port's threads only read requests and write
  replies, and the main thread runs them in `ControlService.RunPending`). All Vulkan queue
  work (fence waits, acquire, recording, submit, present, swapchain recreation) runs on the render
  thread (`RenderThread`). From `RenderThread.Start` until it has stopped, the main thread makes no
  Vulkan call except one on the memory of the frame slot it builds: `vkFlushMappedMemoryRanges` after
  writing staging (`StagingBuffer.Flush`). It has no externally synchronized parameter, and does nothing on
  coherent memory. It hands data to the render thread only through `FrameExchange`.
- Units are metres and seconds. Voxel coordinates (`VoxelCoord`, edits, rays) are integers in centimetres
  (`VoxelConstants.CentimetresPerMetre`), origin at the map centre. A world voxel at level k is
  `VoxelWorld.Grid.Edge(k)` cm across (`VoxelGrid`, its generator's), a model's voxel its own unit (`VoxelModels`; 1 cm
  by default): code where the two meet uses those, never a level's 2^k. Positions in metres use
  `System.Numerics` (`Vector3`, `Matrix4x4`); `Silk.NET.Maths` only at Silk.NET API edges. What every peer must
  compute alike from a seed (world generation, posed bodies) uses `DeterministicMath` and `Rotation`, never
  `Math.Sin` or `System.Numerics` helpers.
- Code that must not allocate (per frame, per tick, per edit) follows these rules, each learned from a measured
  allocation:
  - No reflection-backed helpers such as `Enum.IsDefined`, `Enum.GetName`, or an enum's `ToString()`. Their caches
    are held weakly, so after any garbage collection the next call allocates. Compare enum values directly.
  - No spans of constant numbers or enums kept as static data (`(ReadOnlySpan<int>)[1, 0, -1]`, or a static
    `ReadOnlySpan<int>` property written as `=> [1, 0, -1, ...]`), and no collection expression passed as a
    `ReadOnlySpan<int>` argument: they allocated in hot loops. Use spans over `static readonly` arrays. Span lists of
    tuples or other structs written in place are stack arrays and allocate nothing (measured).
  - No lambda that captures a method's parameters or a local whose scope is the whole method (such as a pattern
    variable of the method's first `if`): the closure is made on entry to the method, even when it returns early.
    Move the lambda into a method of its own.
  - Skia's `SKCanvas.DrawText` of a string makes its text again on every call (160 bytes in SkiaSharp 4). Texts
    from a fixed list drawn at each redraw are made once as text blobs (`TextBlobs`). The menu, the console, the
    loading screen, and the stats line still draw strings, whose text changes.
  - An Arch `CommandBuffer` allocates about 1.8 KB for each `Add` and `Remove` of a tag. A system that does that
    often collects the entities during its query and changes them after it.
- Time values are `readonly record struct`s passed by `in`. Fixed systems receive `FixedStepTime`
  (step length and tick), frame systems `FrameTime` (delta, total, frame index, `Alpha`).
- Engine options are checked at startup by `EngineOptionsValidator`. A new numeric option gets a
  range there.
- ECS components are plain structs with public fields (CA1051 is off for that reason).
- Cross-platform: Linux, Windows, and macOS are all targets, only Linux is tested. Use
  `Path.Combine`, ordinal string comparisons, and `OperatingSystem.Is*()` guards for anything
  platform specific. No `XDG_*` variables, no shell commands, no `/` string concatenation.
- Do not dispose Silk.NET API containers (`Vk`, `Shaderc`, `ALContext`, `AL`). Unloading those
  native libraries at exit crashes the process. Destroy the Vulkan and OpenAL objects only.
- Format with csharpier; it reads indent and width from `.editorconfig`. It is a local dotnet tool
  pinned in `dotnet-tools.json` at the repo root (`dotnet tool restore` after a fresh clone). Run
  `dotnet csharpier check .` before a commit and `dotnet csharpier format .` to fix. LazyVim formats
  with Mason's own copy at `~/.local/share/nvim/mason/bin/csharpier`; keep the two versions in step.
- Commit messages in imperative mood. Git identity is repo-local.
- Work and commit on `main`; no feature branches (one developer). A temporary local branch a tool needs (a writer's
  worktree) is fine: never pushed, deleted once its changes are on main.

# Compatibility notes

Package notes are with their areas: Steamworks.NET in `.claude/rules/sessions.md`, Bepu in `physics.md`, Arch in the `ecs` skill, SkiaSharp in `ui.md`, OpenAL in `audio.md`, Vulkan in `rendering.md`, and xunit in `tests.md`.

# Environment

- .NET SDK 10.0.112 pinned in `global.json` (`rollForward: latestPatch`).
- Development machine: Ubuntu, Wayland, AMD RX 5700 XT plus an AMD iGPU on Mesa RADV. The device
  selector prefers discrete GPUs; override with `Engine:Renderer:PreferredDevice`.
- libsdl2 is installed, so `WindowService` calls `Window.PrioritizeGlfw()` before creating the window.
- `vulkan-validationlayers` and `vulkan-tools` are installed (apt, 1.4.341). Debug builds enable
  `VK_LAYER_KHRONOS_validation` plus synchronization and best-practices checks (through `VK_EXT_layer_settings`;
  `Engine:Renderer:SyncValidation` and `BestPracticesValidation` turn them off) and log `validation on (sync, best
  practices)` in the instance line; validation errors reach the log at Error. Starting the Steam API loads Steam's
  overlay and fossilize implicit Vulkan layers (`~/.local/share/vulkan/implicit_layer.d/`) into the game; the overlay's
  own image draws one best-practices warning, so `validate.sh` runs with `--Engine:Steam:Enabled=false`. On a machine
  without the layer, Debug builds warn and continue. `vulkaninfo --summary` lists the layers and devices.
- Any Vulkan work uses the project skill `.claude/skills/vulkan/` (rules, Silk.NET notes, and
  `validate.sh`, which fails on any Vulkan validation warning or error; `validate.sh 30 "--Engine:Console:Run=settle 60; balls 8"`
  draws models). Any ECS work uses
  `.claude/skills/ecs/` (rules, Arch notes, Itera ECS design, and `probe.sh`, which re-checks Arch
  behaviour after a version change). Code that moves ECS data to the GPU, or defines a struct that a
  shader reads, uses `.claude/skills/ecs-vulkan/` (rules, layout and device notes, design, and
  `probe.sh`). A PreToolUse hook (`.claude/settings.json`, `.claude/hooks/skill_guard.py`) blocks file
  writes, and Bash commands that name the guarded folders, until the matching skill is loaded in the
  session (a subagent must load it itself): `vulkan` for `src/Itera.Engine/Rendering/` and `.cs` files that use `Silk.NET.Vulkan`; `ecs`
  for `src/Itera.Engine/Ecs/`, the `Systems/` and `Components/` folders of the engine and the game, and
  `.cs` files that use Arch; `ecs-vulkan`
  for `src/Itera.Engine/Rendering/GpuData/`, `.cs` files that use that namespace, and `.cs` files that
  use both Arch and `Silk.NET.Vulkan`.
- Editor: LazyVim with the `lang.dotnet` extra. The solution uses the classic `.sln` format because
  OmniSharp does not open `.slnx`.
- Shader cache: `~/.local/share/<DataFolder>/ShaderCache` on Linux (`LocalApplicationData` elsewhere).

# The game

@GAME.md
