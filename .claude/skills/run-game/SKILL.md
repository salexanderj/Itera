---
name: run-game
description: "How to check a change in the game (src/Game) on the Itera engine, the cheapest way first (tests, HeadlessMatch scenarios, runs with no graphics, offscreen screenshot sets, a window last), and how to run and drive it: options on the command line, unattended scripted runs, exit codes, the start-up log lines, the console and its commands, and the control port (tools/control/game.py). Use before checking a change, starting the game, driving it with game.py or console commands, or reading a run's log."
---

# Running and driving the game

The build, test, and run commands are in CLAUDE.md's Commands block. The game is `src/Game`
(`src/Game/bin/<Config>/net10.0/Game`); its data folder, logs included, is `~/.local/share/<DataFolder>/`, where
`DataFolder` is `Engine:Application:DataFolder` in `src/Game/appsettings.json` (the starter's `IteraGame`;
`python3 tools/benchmark/data_folder.py` prints it). Runs Claude starts use `<DataFolder>-agent` instead
(`IteraGame-agent`; `data_folder.py --agent`): `tools/control/game.py` and `validate.sh` pass it,
`tools/benchmark/run.sh` with `BENCH_AGENT=1`, and a run by hand gives `--Engine:Application:DataFolder=IteraGame-agent`,
so the player's `settings.json` and logs are never touched.

## Checking a change

Check a change the cheapest way that can see it, and open a window only when the window itself is what changed (its
swapchain, presenting, focus, or full screen): a window takes the user's desktop, and offscreen and no-graphics runs
load faster. `game.py start` runs offscreen unless given `--window` or `--fullscreen`.

| What changed | Check it with |
|---|---|
| Logic, maths, a service, a system's rules | A test in memory (`.claude/rules/tests.md`): `dotnet test --filter`. |
| Behaviour in the real world and systems (moving, physics, streaming, a game feature) | A scenario test over `HeadlessMatch` with `Engine:Physics:Threads=1` (`tests/Game.Tests/ScenarioTests`, the `scaffold` skill); or a run with no graphics (`--Engine:Window:Mode=None`) whose startup script reads the state (`pos`, `ecs list COMPONENT`, `ecs show player`, log lines) and quits: about 5 s in Debug. |
| Minutes of play (resting, streaming over time, drift) | No graphics with `--Engine:FreeRun=true` and `wait ticks N` (60 a second): 10 minutes in about 4 s in Debug. |
| The picture | `game.py shots VIEW... --out DIR --compare OLD` (offscreen; four views in about 15 s in Debug), then read the PNGs; `tools/benchmark/imgdiff.py` exits 1 when two sets differ. |
| Something to try by hand | `game.py start`, then `run -s`, `query`, `status`, `shot`, and `input`; `pause`, `step N`, and `timescale` offline; `shaders reload` or `dotnet watch` for edits without a restart. |
| Vulkan code | `validate.sh --offscreen` while iterating; both standard profiles with a window before it is done (the `vulkan` skill). |
| Sessions | The in-process session tests; or a host and a client with no graphics on 127.0.0.1 (`Mode=None`, `--Engine:Session:Port` apart from a running game's). |
| GPU time | `tools/benchmark/run.sh` with `BENCH_AGENT=1` on a quiet machine (ask the user; another game moves timings by 20 % or more): `--offscreen` runs against `--offscreen` runs, windowed against windowed (offscreen measures 1 to 3 % lower). |

Pitfalls:

- `wait seconds N` counts real time, not the game's: with `timescale` or `FreeRun`, wait for `ticks` instead.
- `FreeRun` needs `Mode=None`. With no graphics `screenshot` and the port's `shot` refuse, and `wait frames` counts the
  frames built after the steps.
- The port's `run` runs commands outside any script, so a `wait` there fails: use `run -s` (or the port's own `wait`).
- `pause`, `resume`, `step`, and `timescale` work only offline; `pause` and `step` refuse while the world loads.
- Physics repeats run to run only with `Engine:Physics:Threads=1` (a test or comparison that needs equal results sets
  it); the default spreads it over threads.
- `HeadlessMatch.WaitUntilLoaded` waits on the clock: the column workers need real time, so stepping alone never loads
  the world.
- Debug never optimises the engine's code (voxel work runs about 5 times slower): time game logic in Release.
- The console's `settings`, `shadows`, and `sound volume` write the data folder's `settings.json`: run in the agent
  data folder (`game.py` and the tools do).
- Two screenshot sets of one build agree exactly only with dynamic render scaling off (`shots` turns it off) and on a
  quiet machine.

## Options

Options go after `--` as `--Section:Key=value`. The engine's are under `Engine:`, the game's under `Game:` (`GAME.md`
lists the game's). Commonly used:

- Matches: `--Engine:Session:Mode=Offline|Host|Join` (default `Menu`; the lines that look at a world need a match),
  `Address=127.0.0.1:47600` (or `lobby:ID`, `steam:ID`), `Port`, `Transport=Steam`; `--Engine:Steam:Enabled=false`;
  `--Engine:Save:Name=castle` (start from `Saves/castle.save` and write it at quit). Two games on one machine: add
  `--Engine:Voxels:WorkerThreads=4` to each (each takes a 512 MB mesh heap).
- World: `--Engine:Voxels:Seed=7`, `Levels` (4 by default and 9 in the starter, 1 to 10: 10, 20, 40, and 80 cm voxels on a 10 cm world;
  `Levels=1` is level 0 only, loaded to 59.2 m, a fraction of the memory), `StreamingRadiusMetres` (0, from the grid:
  59.2 m on 10 cm voxels, where level 0 loads to it and reaches it less two 12.8 m columns, 33.6 m; 4.625 level-0
  columns on voxels over 10 cm, 592 m at 1 m), `WorkerThreads`, `EditColumnsPerStep` (1 to 64), `DebugWalkSpeed=5` (the
  debug camera flies a 100 m circle, `Engine:Camera:CircleHeightMetres` up, 20 by default), `Level0ReachMetres` (0: the
  radius less two columns) and `ReachGrowth` (2; 1.5 draws each level 1.5 times as far as the one before instead of
  twice; `LevelDistances`).
- The starter: `--Game:World:CameraPose=x,y,z,yaw,pitch` (metres and degrees; `pos` prints it),
  `--Game:Player:StartFlying=true`, `ThirdPerson=true`, `HeightMetres=2.5` (1.2 to 2.5), `--Game:Minimap:Metres=128`
  (16 to 256; 64 by default).
- Renderer (the world and the models as meshes): `MeshFinestLevel` (0), `MeshReachShare` (0.5: each level drawn to
  half its reach, 10 cm voxels to 16.8 m), `ViewDistanceMetres` (256, the starter's 8000; up to 8000; 0 for the coarsest level's radius), `MsaaSamples`
  (4: 1, 2, 4, or 8), `MeshMegabytes` (512: 64 to 4096; full, it frees the meshes of columns the view does not draw),
  `ShadowMapTexels` (2048: 512 to 4096), `DistantShadowMetres` (0, none; 400 to 8000: a third shadow cascade to that
  depth, past the far one's 200 m, for hills' shadows in the distance, drawn only while the sun is under 30 degrees, so
  check it with `sun` or `time` low), `Shadows=false` (no shadow maps: 0.5 to 1.8 ms less at 1440p),
  `PreferredDevice=RAPHAEL` (any part of a device's name, any case; this machine's iGPU), `DebugLines=true`,
  `DynamicRenderScaling=false` with `RenderScale=0.75`, `DynamicScalingThreshold=60`, `AntiAliasing=false` (SMAA, which
  runs only with `MsaaSamples=1`), `ShaderSourceDirectory=$PWD/src/Itera.Engine/Rendering/Shaders` (then `shaders
  reload`; a game's own shaders, `Game/`, come from `Shaders/Game/` beside the executable, or with
  `GameShaderSourceDirectory=$PWD/src/Game/Shaders` from the repository, which `game.py start` gives when the folder exists), all under `Engine:Renderer`; `--Engine:Window:VSync=false`, `--Engine:Ui:Stats=true`,
  `--Engine:Ui:MenuPausesOffline=true` (the menu holds an offline match's steps while open).
- No window: `--Engine:Window:Mode=Offscreen` draws at `Engine:Window:Width` x `Height` (1280x720) into a headless
  surface (`VK_EXT_headless_surface`; its UNORM images are viewed as sRGB, so the picture is the window's), capped at
  `Engine:Window:OffscreenFps` (240) frames a second; nothing shows on the desktop, a locked screen changes nothing, and
  input comes only from the control port. Screenshots, `validate.sh --offscreen`, and `run.sh <label> --offscreen` work
  as with a window. `tools/control/game.py start` uses it unless given `--window`. `--Engine:Window:Mode=None`: no
  graphics at all (no window, Vulkan, or render thread; `Engine ready on no graphics` in about 60 ms): the fixed steps
  run and the frame group after them, nothing is drawn, the loading screen waits only for the world's columns (about 1.5
  s), screenshots refuse, and `wait frames` counts those frames; `--Engine:FreeRun=true` (only with it) runs the steps
  back to back: 10 minutes of play (`wait ticks 36000`; `wait seconds` counts real time) take about 4 s in Debug and
  under 1 s in Release. A host and a client both without graphics play a session on 127.0.0.1 (`--Engine:Session:Port`
  to keep off a running game's). `game.py start --headless` adds it. `--Engine:Physics:Threads=1` (1 to 16; 0, the
  default, a quarter of the processors) makes physics repeat run to run.
- Navigation (a game with an `INavGridSource`): `--Engine:Navigation:WindowCells` (1024, 256 to 4096): the most 50 cm
  cells its grid is across; a larger map's grid is a window that follows the players.
- The origin: `--Engine:Origin:ShiftMetres` (512, 8 to 4096): how far the camera goes from the match's origin on any
  axis before `OriginShiftSystem` moves the origin to it (the Debug log's `Origin: moved to ...` lines; the control
  port's `status` gives the origin and the camera's absolute place).
- Other systems: `--Engine:Physics:SettleSeconds` (0.5 to 60), `MaxLooseBodies` (1 to 1024), `Substeps` (1; 1 to 8),
  `VelocityIterations` (8; 1 to 32), `LinearDamping` and `AngularDamping` (0.03; 0 to 1), `Gravity:X`, `Gravity:Y`, `Gravity:Z` (0, -9.81, 0; each -100
  to 100), `CollisionCentimetres`
  (the character mover's cells without voxel terrain, 1 to 10, default 10);
  `--Engine:Particles:Enabled=false` (`Capacity` 16,384 to 1,048,576); `--Engine:Audio:Enabled=false` (`Volume` 0 to
  1, default 0.1; `MusicVolume` and `EffectsVolume` 0 to 1 under it, default 1; `MaxVoices` 8 to 254; `MenuMusic` the track the
  engine's menu plays), `--Engine:Audio:Cache=false`; `--Engine:Loading:Enabled=false`;
  `--Engine:Control:Enabled=true` (127.0.0.1:47650, `Port`); `--Engine:Benchmark:Enabled=true` (`tools/benchmark`);
  `"--Engine:Console:Run=balls 8; pos"` (commands run once the first match's world and systems exist; with `wait`, a
  script), `--Engine:Console:RunFile=PATH` (a file of command lines, `#` comments, after `Run`'s),
  `--Engine:Console:ExitOnFailure=true` (a failed startup command ends the run with exit code 1; `quit CODE` sets
  another).
- Logging: `--Serilog:MinimumLevel:Default=Verbose`; `--Serilog:MinimumLevel:Override:Itera.Engine.Modding=Verbose
  --Engine:Logging:ConsoleLevel=Trace`; `--Engine:Logging:FileEnabled=false`.

## The start-up log and exit codes

A successful run logs the run's steps first: `Network: ready; no socket until a session starts`, `Steam: off ...` (the
starter has no App ID; a game with one logs `Steam: signed in as ...` or a Warning), window, Vulkan instance, one line
per physical device, the selected device, swapchain, input devices, shader compiler and its smoke test, audio device,
`UI: Skia CPU surface ...`, `Loaded mod itera.samplemod`, `Mesh assets: N model(s), ... read in N ms` (the models the game declared, with how many
textures came from their compressed textures; a file that cannot be read stops the run with exit code 1; a Warning for
compressed textures that do not match their image, an Information note for a model whose textures were all decoded), `Mesh assets on the GPU: N vertices, ... draws N MB a frame slot`
(and with a skinned model `... N skinned vertices; posed instances skinned into V vertices ...`), for each image
view a game declared `Image view NAME: WxH texels, one sample a pixel, drawn at most every N frame(s)` (the starter's
`minimap`; the mesh sample's `yard camera`), for each pass a game registered `Render pass of the game's: NAME at the
STAGE stage` (the starter has none; the mesh sample's is `aim highlight`), `Audio: thread started ...`, `Render thread started`, `Engine
ready ... entering main loop`. Then the match's: `Match 1: offline, seed N` (or `host, seed N`, `host through Steam,
...`, or `join ADDRESS`), `Session: offline, seed N` (or `hosting on UDP port ...`, `hosting through Steam ...`, or
`joined ADDRESS in slot N, seed N; downloaded ...`; with a save name, a `Save: starting from NAME ...` or `Save: NAME
does not exist yet ...` line before it), physics, `Meshes: N material texture set(s) ...`, `Voxels: N worker
thread(s), budget ..., generator game.world v2` (on a client `Session: loaded N edit(s) of the host's world`; from a
save `Save: loaded N edit(s) of NAME`), the ECS world, the game's lines (the starter's `Player: 1.70 m tall, feet at
...` and `Debug camera at ...`), `Loading: waiting for N task(s)`, and `Loading: done after N s (...)`. Quit in the
menu, the close button, Ctrl+C (SIGINT), SIGTERM, or SIGHUP ends the run with exit code 0 and shutdown lines in
reverse order, the match's first, ending with `Shutdown complete after N s; exit code 0` (the console's `quit CODE`
gives CODE, and a startup script that failed with `Engine:Console:ExitOnFailure` gives 1). A second signal more than 0.5
s after the first ends the process at once (exit code 130 for SIGINT). An exception in initialisation or the loop is
logged at Critical, shutdown still runs, and the exit code is 1 (in a menu match it ends only the match). An invalid
option (for example `--Engine:FixedStepHz=0`) or an unknown key under `Engine` or `Game` (for example
`--Engine:Renderer:VSync`; `UnknownOptionKeys`) stops the game before the window opens, with exit code 1 and a message
naming the key.

Every run writes `~/.local/share/<DataFolder>/Logs/<datafolder>-<date>-<time>-<pid>.log` (`LocalApplicationData`
elsewhere) with Debug detail; read the newest file there first when a run misbehaves. A log holds a session header, init
timings, and every 10 s a frame stats line (frames, frame times with median, 95th, and 99th percentiles, fixed steps,
bytes allocated per frame on the main and render threads, GCs, GPU time), Debug lines with each GPU pass (`GPU passes
over ...`, a game's passes and the image views among them), the fixed steps' CPU time by part, and the eight slowest
fixed systems (`SystemTimes`), a voxel stats line (columns, chunks, bricks, memory against the budget, loads, unloads,
job times), a meshes line (blocks meshed and pending, uploads, the mesh heap, triangles drawn, the main thread's time),
a `Particles:` line while any live, and the shutdown steps. The console shows Information and above. Log lines from the
render thread carry its thread id (for example `[T21]`) instead of `[T1]`.

## Unattended runs

Run with no window: `--Engine:Window:Mode=None` when the check needs no picture (the fastest), `Offscreen` when it does.
A startup script runs a check on its own and ends the run: `Engine:Console:Run` (and `Engine:Console:RunFile`, a file of
command lines) with `wait` between the commands, `screenshot PATH`, and `quit`, and
`--Engine:Console:ExitOnFailure=true` so a failed command or a wait past its time ends the run with exit code 1:

```bash
timeout --preserve-status -s INT 120 src/Game/bin/Debug/net10.0/Game --Engine:Session:Mode=Offline \
  --Engine:Window:Mode=Offscreen --Engine:Steam:Enabled=false --Engine:Application:DataFolder=IteraGame-agent \
  --Engine:Console:ExitOnFailure=true "--Engine:Console:Run=wait loaded; balls 4; wait seconds 2; pos; screenshot 0.5 /tmp/shot.png; quit"
```

That run takes about 6 s in Debug (in a window 7 s; with no graphics and no screenshot 4.7 s; Release offscreen 4.6 s)
and writes its log to `~/.local/share/IteraGame-agent/Logs/`. Without the session mode it stays at the menu; in a
window, add `--Engine:Window:VSync=false` so a locked screen does not stop the frames; the data folder keeps the
player's `settings.json` and logs apart (`tools/control/game.py`, `validate.sh`, and `run.sh` with `BENCH_AGENT=1` use
the same `<DataFolder>-agent`; `tools/benchmark/data_folder.py --agent` prints it). `timeout` is the safety net: a
background run started from a script ignores SIGINT; send it SIGTERM. (`timeout` signals the process and its process
group, so the game receives SIGINT twice at once; `ExitSignalPolicy` treats that as one request.) A Debug run shows the
loading screen for about 3.5 s (the world; Release about 2 s); add `--Engine:Loading:Enabled=false` for a short check of
play.

## The control port

The control port (`Engine:Control`, off unless `Enabled`; `ControlService`) lets `tools/control/game.py` (README there)
drive a running game: start it from a copy of the build, run console commands and get their answers and log lines
(`run -s`: as a script, so `wait` and `screenshot` hold the commands after them and the reply comes when all have run),
read the match's entities as JSON (`query`) and its steps (`status`: tick, paused, time scale, the player's position),
wait for loading, frames, fixed steps (`ticks`), seconds, or a log line, measure the GPU passes (a game's own by name,
`Game NAME`, and their sum, `Game`; the image views' updates, `ImageViews`), take screenshots from
the game itself (`ScreenshotPass`), reload the shaders from the repository (`Engine:Renderer:ShaderSourceDirectory`;
`shaders reload`: every pass compiles, then the pipelines are made again; an error keeps the old ones), and send input
(keys, buttons, look, the wheel, a capture of the pointer that needs no focus, and `point X Y` and `type TEXT` for the
menu). It reads lines of at most 1 MiB and takes 16 tools at once; a wait-for-log pattern has 50 ms a line. Use it
instead of restarts; `game.py restart` takes the options `start` was given again. `game.py shots VIEW... --out DIR [--compare DIR] [--size WxH] [--release]` starts the game
offscreen, takes a screenshot of each view once its world has loaded and its light settled (`fly on`, `tp VIEW`,
`wait view`), stops it, and compares with an earlier set (`tools/benchmark/imgdiff.py`): four views take about 15 s in
Debug, and sets of the same build agree to a few pixels (on a quiet machine, exactly). A view is a game's `tp` place
(the starter's benchmark views: `start`, `start-low`, `down`, `up`, `high`, `far`, `distant`) or `x,y,z,yaw,pitch`. `game.py start`
runs offscreen (no window) unless given `--window` (a window on HDMI-A-1) or `--fullscreen` (full screen on DP-1, VSync
off); with `--window`, add `-- --Engine:Window:VSync=false` when the screen may lock. Under `dotnet watch` (Debug), a
changed method body or a new method applies to the running game in about 1 to 3 s, from the method's next call
(constructors and static initialisers do not run again); an edit it cannot apply restarts the game. Watch builds into
`bin/Debug` in place, so no other build or test run may share it.

## The console

The console (`DevConsole`, `ConsoleLayer`): ` opens it, and ` or Escape closes it. While it is open, keys, characters,
and the wheel go to it. Enter runs the line (commands separated by `;`), Up and Down step through the history, Tab
completes a command's name, Page Up and Page Down scroll, and Ctrl+V pastes. It shows log lines of Information and
above and the commands' answers. Commands run between fixed steps, each logged at Information (`Console: ...`), and
`Engine:Console:Run` runs commands once the first match's world and systems exist. A setting given no value prints
it. Commands run as scripts (`DevConsole.Hold`): the lines typed are one, the startup commands (`Run`, then `RunFile`'s
lines) another. `wait` holds the commands after it in its script, and `screenshot` holds them until its PNG is
written; a hold past its time (300 s for `wait` unless given, 60 s for a screenshot) fails and drops the rest. The
control port's `run` runs commands outside any script, so `wait` there fails (the port has its own `wait`). `help`
lists the commands (`ConsoleCommands`):

- Engine: `help`, `clear`, `play` [SEED], `host` [SEED] [steam|direct], `join` ADDRESS, `leave`, `settings` (volume,
  musicvolume, effectsvolume, sensitivity, fov, uiscale 0.75 to 1.5, scaling, threshold, shadows, aa off|smaa|2x|4x|8x, vsync, fullscreen; a test run that changes them
  writes the data folder's `settings.json`: give render options on the command line instead, or run with the agent data
  folder, `--Engine:Application:DataFolder=<DataFolder>-agent`), `lines`, `shadows`, `scale` (the run's only), `aa` (on,
  off, edges, weights, auto; the run's only), `lights` (test N: N warm lights round the camera for ten minutes; clear),
  `view` (off, wire, levels, all, plain: the meshes' edges over the world, surfaces tinted by their voxels' size, both,
  or every block from its plain mesh with the seams open; the run's only), `materials` (textures or flat, and `all` for
  every world material: how the world's materials are drawn; `sets` lists the texture sets loaded;
  `set ID SET|none [MATCH]` draws material ID from a set, colour matched 0 to 1, or plain; `tune ID DETAIL RELIEF BLUR`
  keeps part of its set's colour variation and relief (0 to 1) and softens it by mip levels (0 to 4);
  `bake ID top|side [NAME]` writes material ID's recipe as images for `tools/Textures` into the data folder's `Bakes/`;
  the run's only), `look` (every value of the look, `RenderLook`, with `*` on those set here; NAME prints one; NAME VALUE,
  or NAME R G B for a colour, sets it over every frame's look; reset gives the game's back; the run's only), `sun`
  [AZIMUTH ELEVATION|off] (the sun towards AZIMUTH degrees from -z round towards +x and ELEVATION over the horizon, in
  place of the game's; under 2 degrees below the horizon it is night: no sunlight or shadow maps; the run's only),
  `time` [HH:MM|off] (the sun at a time of a test day: sunrise 06:00, the engine's sun at 12:00, sunset 18:00; the
  run's only), `lens` [ortho HEIGHT [NEAR FAR]|off] (the camera drawn through an orthographic lens: an image HEIGHT
  metres high round the camera, which is the middle of what it shows, from NEAR to FAR metres along the view, negative
  behind it, by default the view distance each way; off gives the camera's own lens back; the run's only), `origin`
  [move DX DY DZ] (the match's origin, which positions are measured from, and the camera's place from it and in the
  world; move moves it by whole metres, and everything with it, so nothing in the world moves: for checking that
  nothing does; a move of `Engine:Origin:ShiftMetres` or more leaves the camera that far out, so the next step follows
  it back; the run's only), `stats`, `ui` [press LABEL] (the toolkit layers shown, an element a line: kind, id, text,
  rectangle, state; press clicks the element reading LABEL, as a click on its middle would, for working a UI by what
  it says), `screenshot` [SCALE] [noui] [PATH.png] (a PNG at PATH.png or in the data folder's
  `Screenshots/`), `wait` (loaded, view (the view's world loaded and on the GPU, as the loading screen waits for), ticks
  N, frames N, or seconds N of real time, then an optional timeout in seconds), `shaders` [reload] (the folder shaders
  compile from; reload compiles them again), `quit` [CODE] (the exit code, 0 to 255), `ecs` (counts, list COMPONENT [N],
  show ID[:VERSION]|player: the match's entities and their components' fields, `EcsInspector`), `pause`, `resume`,
  `step` [N] (pauses, then N fixed steps), `timescale` [X] (0.05 to 16 for the match; offline only, as are pause,
  resume, and step; pause and step refuse while the match loads), `physics` (the match's bodies, statics, solver, damping, and gravity; solver ITERATIONS
  SUBSTEPS, damping LINEAR ANGULAR, gravity X Y Z for the match), `particles` (on, off, recipes, test NAME [COUNT], pull
  NAME [COUNT], stress NAME BURSTS, stress off), `sound` (on, off, volume V, recipes, files (the sounds read from
  files, each file's channels, rate, length, and problems), test NAME [LEVEL], play NAME (at the listener; a stream or file loop for 4 s), wav NAME [LEVEL] (with a level,
  that level's variations a file each, or 8 s of a stream at it), wav all into `Sounds/`), `music` (what plays: the decks' tracks and gains, underruns, and what the music thread decoded
  and allocated; list (each track as read, or why not), play NAME [SECONDS] (cross-fading, 2 s by default), stop
  [SECONDS]).
- The engine's systems, where a game registers them (the starter does): `fly` (the debug fly camera, or walk again where
  it is), `flyspeed`, `camera` (first or third), `pos` (the player's eye and look, the fly camera's while it flies, as
  `Game:World:CameraPose` takes it: absolute metres from the map's middle, whatever the match's origin), `tp` (x y z
  [yaw pitch], absolute too, a pasted pose, or a place the game names: the starter's
  benchmark views, `start`, `start-low`, `down`, `up`, `high`, `far`, and `distant`; a jump 512 m or more from the origin moves the
  origin there first, so it lands exactly), `settle` (how long a ball rests before it
  goes back into the grid), `cleanup` (removes every loose body and clears the particles), `reset` (the world as the
  seed made it, every edit undone, then `cleanup`; the loading screen covers the picture while the view is generated
  again, on clients too), `balls` [COUNT] [RADIUS] [MATERIAL] (balls dropped 3 m in front of the camera, of the game's
  default material unless given, the starter's stone: loose bodies that draw models; `settle 60` first keeps them
  bodies), `meshes` (the run's mesh assets, models read from glTF files, with their triangles, materials, textures,
  size, and a rig's joints and clips; `place NAME [SCALE] [CLIP [SECONDS]]` puts one 4 m in front of the camera on the
  ground, facing it, playing CLIP or held at SECONDS of it (repeatable shots); `test COUNT [NAME [CLIP]]` puts COUNT, at
  most 4096, in rows ahead, the first asset unless named, each from another time of CLIP; `clear` removes those it
  placed; this game only, on clients too).
- The game's own commands go here (the starter has none).

In a session, `tp`, `settle`, `cleanup`, `reset`, and `balls` work only on the host; `pause`, `resume`, `step`, and
`timescale` only offline.
