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
framework's. The game (`src/Game`, `tests/Game.Tests`, `GAME.md`) is each clone's own. Later engine changes come from
Itera with a pull from its remote, so:

- Game code stays in the game's folders. Change an engine file only to fix or extend the engine, in a way any game
  could use, and send that change back to Itera.
- This file and `.claude/` are the framework's. A game's own instructions go in `GAME.md`, imported below, and its own
  areas' rules in `.claude/rules/game-<area>.md`.
- Anything specific to one game belongs in the game. The engine names no game: a game names itself through
  `Engine:Application` (`Name`, `DataFolder`) and `Engine:Window:Title`.

Voxel terrain is optional: a game that registers no generator and does not call `AddIteraVoxels` plays matches without
it. `ARCHITECTURE.md` holds the full description, the dependencies, the design decisions, and the open work. Read it
before changing anything structural. `docs/` holds the write-ups behind decisions.

# How to work

- Area details are in `.claude/rules/`, one file per area (listed below). Claude Code loads a rules file when a file it
  covers is opened with the Read, Write, or Edit tool. `cat`, `grep`, `sed`, and the Grep tool do not load it. Open the
  files you will change with the Read tool. Before you plan, discuss, or review work in an area, read its rules file.
- Before you write or change a doc (this file, `GAME.md`, a rules file, a skill, `ARCHITECTURE.md`, or `docs/`), load
  the `docs` skill: it says where each fact goes, and what the documentation tests check.
- When you add or change one of these, also do what follows. The `scaffold` skill has the steps. The checklist tests
  (`ChecklistTests`, `ShippedSettingsTests`, `SystemOrderTests`, `WorldTests`) fail when one is missed.
  - Anything the engine sends between games: raise `SessionProtocol.Version` and pin the new fingerprint in the
    engine's `WireFingerprintTests`. A game's own messages are the game's: it versions them in its
    `ISessionMatchData.Fingerprint` and tests them with damaged bodies (`HostileValues` and `Watchdog` in `tests/Shared`).
  - What a generator makes, or its grid's size: a changed world hash or grid means the generator's `Version` must rise.
    The starter's is pinned in `tests/Game.Tests/WorldTests`.
  - A new system: a zero-allocation test and a behaviour test. An engine system's go in
    `tests/Itera.Engine.Tests/Systems`, a game's in its tests. A system the game registers takes its place in the game's
    `SystemOrderTests`.
  - A new option: its range in `EngineOptionsValidator` (the engine's) or the game's validator, and its default in the
    game's `appsettings.json` (`ShippedSettingsTests`).
  - A console command, option, system, tunable, replicated kind, or save section added or changed: write the generated
    reference again (`docs/reference/`; the `docs` skill), which `ReferenceTests` checks.
  - Code that reads the edit log: handle `VoxelEditOperation.Reset`, an empty box after which no earlier edit counts,
    and `RemovePart`, `PlaceObject`, and `RemoveObject`, which change objects alone (`VoxelEdit.ChangesWorld`).
  - A local position that a component or service keeps across steps moves when the origin does (`OriginShifts` and its
    `ShiftedComponents`; the game's `IOriginShiftHandler`s). A component's vector field left out fails
    `OriginShiftsTests` or the game's `ChecklistTests`.
  - A game's render pass, image view, camera lens, or on/off setting: the `scaffold` skill's steps (no test catches a
    missed one).
- Builds, tests, and game runs:
  - Load the `run-game` skill before you start, drive, or check the game. Check a change the cheapest way that can see
    it. The skill holds the options, logs, console commands, and control port.
  - `dotnet build` must end with zero warnings, and `dotnet test` runs every test project (some checks run only in
    Debug). Debug builds are optimised but run Debug-only checks, so take timings from a Release build.
  - Before you run or add tests, read `.claude/rules/tests.md`.
  - `DOTNET_ENVIRONMENT=Development` shows Debug log lines on the console too.
  - `dotnet watch` builds into `bin/Debug` in place, so no other build or test run may share it.
  - Runs you start use the data folder `<DataFolder>-agent`. `tools/control/game.py` and `validate.sh` pass it,
    `tools/benchmark/run.sh` takes `BENCH_AGENT=1`, and a run by hand gives
    `--Engine:Application:DataFolder=<DataFolder>-agent`. The console's `settings`, `shadows`, and `sound volume` write
    the data folder's `settings.json`, so in any other folder they change the player's.
  - A run misbehaves: read the newest log in `~/.local/share/<DataFolder>/Logs/` first.
- Where the details are: `docs/reference/engine-rules.md` lists the engine's rules files and what each covers
  (generated), and `GAME.md`'s table the game's. Arch's package notes are in the `ecs` skill.

```bash
dotnet run --project src/Game [-- OPTIONS]    # opens at the menu without --Engine:Session:Mode
dotnet build -c Release && src/Game/bin/Release/net10.0/Game   # voxel numbers need Release
tools/control/game.py start [--release] [--window|--headless] [-- ARGS]   # a copy of the build, offscreen by default
tools/control/game.py shots VIEW... --out DIR [--compare DIR]   # screenshots of views, compared with a set
tools/control/game.py run "balls 8; pos"   # console commands in the running game (-s: as a script)
DOTNET_WATCH_SUPPRESS_EMOJIS=1 dotnet watch --non-interactive --project src/Game run -- --Engine:Control:Enabled=true
BENCH_AGENT=1 tools/benchmark/run.sh LABEL --offscreen [ARGS]   # GPU benchmark (ask the user first)
ITERA_WRITE_REFERENCE=1 dotnet test --filter ReferenceTests   # write docs/reference/ again
```

## Subagents

- Read-only work that splits into independent parts (searching or reviewing files, checking findings, reading logs,
  web research) goes to `reader` agents (`.claude/agents/reader.md`) in parallel. Give each one part and everything it
  needs, since it starts without the conversation. Check its claims before acting on them. During a benchmark, test
  suite, or validation run, have readers review the change made so far.
- Self-contained parts of a broad change with a clear spec (a class and its tests, option plumbing, mechanical
  edits, test rebasing, tool scripts) may go to `writer` agents (`.claude/agents/writer.md`), each in its
  own worktree. A writer fast-forwards to local `main`, so commit what it needs first. Review its staged diff, apply it,
  build, test, and commit here, then remove the worktree and branch.
- Keep design decisions, Vulkan passes, look-setting shaders, anything sent between games, game runs, benchmarks,
  validation runs, and commits in the main session.

# Starting a game from Itera

1. Clone Itera and keep it as a remote (`git remote rename origin itera`), to pull engine changes later.
2. In `src/Game/appsettings.json` set `Engine:Application:Name`, `DataFolder` (two games on one machine need two), and
   `Engine:Window:Title`. For Steam set `Engine:Steam:AppId`, and a `steam.env` in `src/Game` for `tools/steam/upload.sh`.
3. Rewrite `GAME.md`, and grow `src/Game`: its world (`WorldGeneration/`), its systems (registered in
   `GameServiceCollectionExtensions` in run order), its options under `Game:`, and its tests in `tests/Game.Tests`.
   The folder, project, and namespace stay `Game`, so the tools and skills find it.

# Solution map

Dependency direction: `Itera.Modding <- Itera.Engine <- Game`. Mods reference only `Itera.Modding`, with
`Private="false"`.

- `src/Itera.Modding`: the mod contract only (`IGameMod`, `IModContext`, `ModManifest`). No engine references.
- `src/Itera.Engine`: the engine. `.claude/rules/engine.md` lists its folders.
- `src/Game`: the starter game (`GAME.md`). `tests/`: xUnit, one project per `src/` project.
- `samples/Itera.SampleMod` is the reference mod, and `samples/Itera.MeshSample` a game without voxel terrain.
- `tools/`: `control`, `benchmark`, `steam`, and `Textures`, each with a README.

# Conventions

- Package versions live only in `Directory.Packages.props`: never put a `Version` on a `PackageReference`.
- Warnings are errors, with `latest-recommended` analyzers. Tune rules in `.editorconfig`; do not disable analyzers.
- Logging in the engine uses the `[LoggerMessage]` source generator on `partial` classes. Compute expensive log
  arguments into a local first, because CA1873 flags inline expressions.
- Log levels: Trace for per-frame or per-call detail (off by default); Debug for per-event detail a bug report needs;
  Information for lifecycle milestones; Warning when degraded but continuing; Error when an operation failed and the
  game continues; Critical when the game cannot continue. Per-frame logging must be Trace.
- Sinks are set in code (`AddIteraLogging`, from `Engine:Logging`), levels in the `Serilog` section. Code uses
  `Microsoft.Extensions.Logging` only. `Serilog` appears in `Diagnostics/` and `Program.cs` alone.
- Services have light constructors and an explicit `Initialize()`. The run's are singletons. A service that holds a
  match's state is Scoped (each match is a DI scope, `MatchServices`), and is resolved only from the match's steps on.
  A singleton never takes a scoped service in its constructor: it is given the match's when one starts (`BeginMatch`,
  an argument, or `MatchControl.Current`) and drops it at the end. Registries that refuse a name twice are run-wide,
  made at the run's `game` step (`IRunStartup`). The run's order is in `GameLoop`, the match's in `MatchServices`.
  Every `Dispose` is idempotent.
- Windowing, input, ECS, physics, the session, and mod callbacks run on the main thread. The threads of LiteNetLib and
  the control port only move data to it (`SessionService.Poll`, `ControlService.RunPending`).
- All Vulkan queue work (fence waits, acquire, recording, submit, present, swapchain recreation) runs on the render
  thread (`RenderThread`). From `RenderThread.Start` until it has stopped, the main thread makes no Vulkan call except
  `vkFlushMappedMemoryRanges` on the memory of the frame slot it builds (`StagingBuffer.Flush`). It hands data to the
  render thread only through `FrameExchange`.
- Units are metres and seconds. Voxel coordinates (`VoxelCoord`: voxels, edits, rays, saves) are absolute integers in
  centimetres (`VoxelConstants.CentimetresPerMetre`) from the map's middle. A world voxel at level k is
  `VoxelWorld.Grid.Edge(k)` cm across (`VoxelGrid`), a model's voxel its own unit (`VoxelModels`). Code where the two
  meet uses those, never a level's 2^k.
- Positions kept in metres (`System.Numerics`' `Vector3` and `Matrix4x4`; `Silk.NET.Maths` only at Silk.NET API edges)
  are local, from the match's origin (`WorldOrigin`, whole metres). A point that crosses the origin (a start pose, `tp`,
  a message) is an absolute `WorldPoint` (doubles). They meet only through `WorldOrigin`: a local float becomes
  centimetres or cells only as floor((local + origin) * scale), in doubles.
- What every peer must compute alike from a seed (world generation, posed bodies) uses `DeterministicMath` and
  `Rotation`, never `Math.Sin` or `System.Numerics` helpers.
- Code that must not allocate (per frame, per tick, per edit) follows these rules, each learned from a measured
  allocation:
  - No reflection-backed helpers such as `Enum.IsDefined`, `Enum.GetName`, or an enum's `ToString()`. Their caches
    are held weakly, so after any garbage collection the next call allocates. Compare enum values directly.
  - No spans of constant numbers or enums kept as static data (`(ReadOnlySpan<int>)[1, 0, -1]`, or a static
    `ReadOnlySpan<int>` property written as `=> [1, 0, -1, ...]`), and no collection expression passed as a
    `ReadOnlySpan<int>` argument. Use spans over `static readonly` arrays. Span lists of structs written in place are
    fine.
  - No lambda that captures a method's parameters or a local whose scope is the whole method: the closure is made on
    entry to the method, even when it returns early. Move the lambda into a method of its own.
  - Skia's `SKCanvas.DrawText` of a string makes its text again on every call. Texts from a fixed list drawn at each
    redraw are made once as text blobs (`TextBlobs`). Texts that change still draw as strings.
  - An Arch `CommandBuffer` allocates about 1.8 KB for each `Add` and `Remove` of a tag. A system that does that
    often collects the entities during its query and changes them after it.
- Time values are `readonly record struct`s passed by `in`: fixed systems receive `FixedStepTime`, frame systems
  `FrameTime`.
- ECS components are plain structs with public fields (CA1051 is off for that reason).
- Linux, Windows, and macOS are all targets, only Linux is tested. Use `Path.Combine`, ordinal string comparisons, and
  `OperatingSystem.Is*()` guards for anything platform specific. No `XDG_*` variables, shell commands, or `/` string
  concatenation.
- Do not dispose Silk.NET API containers (`Vk`, `Shaderc`, `ALContext`, `AL`). Unloading those native libraries at exit
  crashes the process. Destroy the Vulkan and OpenAL objects only.
- Format with csharpier, a local dotnet tool (`dotnet tool restore` after a clone): `dotnet csharpier check .` before
  a commit, `dotnet csharpier format .` to fix. LazyVim formats with Mason's copy; keep the two versions in step.
- Commit messages in imperative mood. Git identity is repo-local.
- Work and commit on `main`; no feature branches (one developer). A temporary local branch a tool needs (a writer's
  worktree) is fine: delete it once its changes are on main.
- Vulkan work uses the `vulkan` skill, ECS work the `ecs` skill, and code that moves ECS data to the GPU or defines a
  struct a shader reads the `ecs-vulkan` skill. A PreToolUse hook (`.claude/hooks/skill_guard.py`) blocks file writes,
  and Bash commands that name the guarded folders, until the matching skill is loaded in the session. A subagent loads
  its own.

# The game

@GAME.md
