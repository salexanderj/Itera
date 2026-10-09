---
name: run-game
description: "How to run, drive, and check the game (src/Game) on the Itera engine: options on the command line, unattended runs and signals, the start-up log lines, the console and its commands, and the control port (tools/control/game.py). Use before starting the game, driving it with game.py or console commands, reading a run's log, or checking a change in the game."
---

# Running and driving the game

The build, test, and run commands are in CLAUDE.md's Commands block. The game is `src/Game`
(`src/Game/bin/<Config>/net10.0/Game`); its data folder, logs included, is `~/.local/share/<DataFolder>/`, where
`DataFolder` is `Engine:Application:DataFolder` in `src/Game/appsettings.json` (the starter's `IteraGame`;
`python3 tools/benchmark/data_folder.py` prints it).

## Options

Options go after `--` as `--Section:Key=value`. The engine's are under `Engine:`, the game's under `Game:` (`GAME.md`
lists the game's). Commonly used:

- Matches: `--Engine:Session:Mode=Offline|Host|Join` (default `Menu`; the lines that look at a world need a match),
  `Address=127.0.0.1:47600` (or `lobby:ID`, `steam:ID`), `Port`, `Transport=Steam`; `--Engine:Steam:Enabled=false`;
  `--Engine:Save:Name=castle` (start from `Saves/castle.save` and write it at quit). Two games on one machine: add
  `--Engine:Voxels:WorkerThreads=4` to each (each takes a 512 MB mesh heap).
- World: `--Engine:Voxels:Seed=7`, `Levels` (4 by default, 1 to 10: 10, 20, 40, and 80 cm voxels on a 10 cm world;
  `Levels=1` is level 0 only, loaded to 59.2 m, a fraction of the memory), `StreamingRadiusMetres` (59.2: level 0 loads
  to it and reaches it less two 12.8 m columns, 33.6 m), `WorkerThreads`, `EditColumnsPerStep` (1 to 64),
  `DebugWalkSpeed=5` (the debug camera flies a 100 m circle, `Engine:Camera:CircleHeightMetres` up, 20 by default), `Level0ReachMetres` (0: the radius less two columns) and
  `ReachGrowth` (2; 1.5 draws each level 1.5 times as far as the one before instead of twice; `LevelDistances`).
- The starter: `--Game:World:CameraPose=x,y,z,yaw,pitch` (metres and degrees; `pos` prints it),
  `--Game:Player:StartFlying=true`, `ThirdPerson=true`, `HeightMetres=2.5` (1.2 to 2.5).
- Renderer (the world and the models as meshes): `MeshFinestLevel` (0), `MeshReachShare` (0.5: each level drawn to
  half its reach, 10 cm voxels to 16.8 m), `ViewDistanceMetres` (256; 0 for the coarsest level's radius), `MsaaSamples`
  (4: 1, 2, 4, or 8), `MeshMegabytes` (512: 64 to 4096; full, it frees the meshes of columns the view does not draw),
  `ShadowMapTexels` (2048: 512 to 4096), `Shadows=false` (no shadow maps: 0.5 to 1.8 ms less at 1440p),
  `PreferredDevice=RAPHAEL` (any part of a device's name, any case; this machine's iGPU), `DebugLines=true`,
  `DynamicRenderScaling=false` with `RenderScale=0.75`, `DynamicScalingThreshold=60`, `AntiAliasing=false` (SMAA, which
  runs only with `MsaaSamples=1`), `ShaderSourceDirectory=$PWD/src/Itera.Engine/Rendering/Shaders` (then `shaders
  reload`), all under `Engine:Renderer`; `--Engine:Window:VSync=false`, `--Engine:Ui:Stats=true`.
- Other systems: `--Engine:Physics:SettleSeconds` (0.5 to 60), `MaxLooseBodies` (1 to 1024);
  `--Engine:Particles:Enabled=false` (`Capacity` 16,384 to 1,048,576); `--Engine:Audio:Enabled=false` (`Volume` 0 to
  1, default 0.1; `MaxVoices` 8 to 255), `--Engine:Audio:Cache=false`; `--Engine:Loading:Enabled=false`;
  `--Engine:Control:Enabled=true` (127.0.0.1:47650, `Port`); `--Engine:Benchmark:Enabled=true` (`tools/benchmark`);
  `"--Engine:Console:Run=balls 8; pos"` (commands run once the first match's world and systems exist).
- Logging: `--Serilog:MinimumLevel:Default=Verbose`; `--Serilog:MinimumLevel:Override:Itera.Engine.Modding=Verbose
  --Engine:Logging:ConsoleLevel=Trace`; `--Engine:Logging:FileEnabled=false`.

## The start-up log and exit codes

A successful run logs the run's steps first: `Network: ready; no socket until a session starts`, `Steam: off ...` (the
starter has no App ID; a game with one logs `Steam: signed in as ...` or a Warning), window, Vulkan instance, one line
per physical device, the selected device, swapchain, input devices, shader compiler and its smoke test, audio device,
`UI: Skia CPU surface ...`, `Loaded mod itera.samplemod`, `Audio: thread started ...`, `Render thread started`, `Engine
ready ... entering main loop`. Then the match's: `Match 1: offline, seed N` (or `host, seed N`, `host through Steam,
...`, or `join ADDRESS`), `Session: offline, seed N` (or `hosting on UDP port ...`, `hosting through Steam ...`, or
`joined ADDRESS in slot N, seed N; downloaded ...`; with a save name, a `Save: starting from NAME ...` or `Save: NAME
does not exist yet ...` line before it), physics, `Meshes: N material texture set(s) ...`, `Voxels: N worker
thread(s), budget ..., generator game.world v1` (on a client `Session: loaded N edit(s) of the host's world`; from a
save `Save: loaded N edit(s) of NAME`), the ECS world, the game's lines (the starter's `Player: 1.70 m tall, feet at
...` and `Debug camera at ...`), `Loading: waiting for N task(s)`, and `Loading: done after N s (...)`. Quit in the
menu, the close button, Ctrl+C (SIGINT), SIGTERM, or SIGHUP ends the run with exit code 0 and shutdown lines in
reverse order, the match's first, ending with `Shutdown complete after N s; exit code 0`. A second signal more than 0.5
s after the first ends the process at once (exit code 130 for SIGINT). An exception in initialisation or the loop is
logged at Critical, shutdown still runs, and the exit code is 1 (in a menu match it ends only the match). An invalid
option (for example `--Engine:FixedStepHz=0`) or an unknown key under `Engine` or `Game` (for example
`--Engine:Renderer:VSync`; `UnknownOptionKeys`) stops the game before the window opens, with exit code 1 and a message
naming the key.

## Unattended runs

For unattended runs, send SIGINT after a few seconds:
`timeout --preserve-status -s INT 25 src/Game/bin/Debug/net10.0/Game --Engine:Session:Mode=Offline
--Engine:Window:VSync=false` (without the mode it stays at the menu; VSync off, so a locked screen does not stop the
frames). A background run started from a script ignores SIGINT; send it SIGTERM. (`timeout` signals the process and
its process group, so the game receives SIGINT twice at once; `ExitSignalPolicy` treats that as one request.) A Debug
run shows the loading screen for about 10 s (the edit warm-up; Release less), so a shorter run never draws the world;
add `--Engine:Loading:Enabled=false` for a short check of play.

## The control port

The control port (`Engine:Control`, off unless `Enabled`; `ControlService`) lets `tools/control/game.py` (README there)
drive a running game: start it from a copy of the build, run console commands and get their answers and log lines,
wait for loading, frames, seconds, or a log line, measure the GPU passes, take screenshots from the game itself
(`ScreenshotPass`), reload the shaders from the repository (`Engine:Renderer:ShaderSourceDirectory`; `shaders reload`:
every pass compiles, then the pipelines are made again; an error keeps the old ones), and send input (keys, buttons,
look, the wheel, a capture of the pointer that needs no focus, and `point X Y` and `type TEXT` for the menu). It reads
lines of at most 1 MiB and takes 16 tools at once; a wait-for-log pattern has 50 ms a line. Use it instead of
restarts. `game.py start` adds `--Engine:Window:VSync=false` only with `--fullscreen`; add it after `--` otherwise when
the screen may lock. Under `dotnet watch` (Debug), a changed method body or a new method applies to the running game in
about 1 to 3 s, from the method's next call (constructors and static initialisers do not run again); an edit it cannot
apply restarts the game. Watch builds into `bin/Debug` in place, so no other build or test run may share it.

## The console

The console (`DevConsole`, `ConsoleLayer`): ` opens it, and ` or Escape closes it. While it is open, keys, characters,
and the wheel go to it. Enter runs the line (commands separated by `;`), Up and Down step through the history, Tab
completes a command's name, Page Up and Page Down scroll, and Ctrl+V pastes. It shows log lines of Information and
above and the commands' answers. Commands run between fixed steps, each logged at Information (`Console: ...`), and
`Engine:Console:Run` runs commands once the first match's world and systems exist. A setting given no value prints
it. `help` lists the commands (`ConsoleCommands`):

- Engine: `help`, `clear`, `play` [SEED], `host` [SEED] [steam|direct], `join` ADDRESS, `leave`, `settings` (volume,
  sensitivity, fov, scaling, threshold, shadows, aa off|smaa|2x|4x|8x, vsync, fullscreen; a test run that changes them
  writes the real `settings.json`: give render options on the command line instead, or keep a test data folder with
  `--Engine:Application:DataFolder=<name>-test`), `lines`, `shadows`, `scale` (the run's only), `aa` (on, off, edges,
  weights, auto; the run's only), `lights` (test N: N warm lights round the camera for ten minutes; clear), `view` (off,
  wire, levels, all, plain: the meshes' edges over the world, surfaces tinted by their voxels' size, both, or every
  block from its plain mesh with the seams open; the run's only), `materials` (textures or flat, and `all` for every
  world material: how the world's materials are drawn; `sets` lists the texture sets loaded; `set ID SET|none [MATCH]`
  draws material ID from a set, colour matched 0 to 1, or plain; `tune ID DETAIL RELIEF BLUR` keeps part of its set's
  colour variation and relief (0 to 1) and softens it by mip levels (0 to 4); `bake ID top|side [NAME]` writes material
  ID's recipe as images for `tools/Textures` into the data folder's `Bakes/`; the run's only), `stats`, `screenshot` (a
  PNG in the data folder's `Screenshots/`), `shaders reload`, `quit`, `particles` (on, off, recipes, test NAME [COUNT],
  pull NAME [COUNT], stress NAME BURSTS, stress off), `sound` (on, off, volume V, recipes, test NAME [LEVEL], wav NAME,
  wav all into `Sounds/`).
- The engine's systems, where a game registers them (the starter does): `fly` (the debug fly camera, or walk again
  where it is), `flyspeed`, `camera` (first or third), `pos` (the player's eye and look, the fly camera's while it flies,
  as `Game:World:CameraPose` takes it), `tp` (x y z [yaw pitch], a pasted pose, or a named place where a game gives places; the starter gives none), `settle` (how
  long a ball rests before it goes back into the grid), `cleanup` (removes every loose body and clears the particles),
  `reset` (the world as the seed made it, every edit undone, then `cleanup`; the loading screen covers the picture while
  the view is generated again, on clients too), `balls` [COUNT] [RADIUS] [MATERIAL] (balls dropped 3 m in front of the
  camera, of the game's default material unless given, the starter's stone: loose bodies that draw models; `settle 60`
  first keeps them bodies).
- The game's own commands go here (the starter has none).

In a session, `tp`, `settle`, `cleanup`, `reset`, and `balls` work only on the host.
