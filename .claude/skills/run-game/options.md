# Options for checks

Which options to set for which check. Every option, with its default, range, and what it does, is in the generated
reference: `docs/reference/engine-options.md`, and the game's `game-options.md`.

## Matches and sessions

- `--Engine:Session:Mode=Offline|Host|Join` starts a match (the default, `Menu`, stays at the menu). Commands that look
  at a world need a match.
- A client gives `--Engine:Session:Address=127.0.0.1:47600` (or `lobby:ID`, `steam:ID`). `Port` sets the host's port,
  and `Transport=Steam` hosts through Steam. `--Engine:Steam:Enabled=false` keeps Steam off.
- `--Engine:Save:Name=castle` starts from `Saves/castle.save` and writes it every `Engine:Save:AutosaveMinutes` on a
  background thread, and at quit; 0 minutes writes only at quit.
- Two games on one machine: add `--Engine:Voxels:WorkerThreads=4` to each, since each takes a 512 MB mesh heap.
- As if far apart: `--Engine:Session:AddedDelayMilliseconds=100` on a client delays both ways of its link.
  `AddedJitterMilliseconds` and `AddedLossPercent` act on what it sends; give loss to the host too, to lose what it
  sends. `--Engine:Replication:InterpolationMilliseconds` sets how far behind the host a client shows what it
  replicates.

## No window

- `--Engine:Window:Mode=Offscreen` draws at `Engine:Window:Width` x `Height` into a headless surface
  (`VK_EXT_headless_surface`; its UNORM images are viewed as sRGB, so the picture is the window's). Nothing shows on the
  desktop, a locked screen changes nothing, and input comes only from the control port. Screenshots,
  `validate.sh --offscreen`, and `run.sh <label> --offscreen` work as with a window, and `game.py start` uses it unless
  given `--window`.
- `--Engine:Window:Mode=None` runs with no graphics at all: no window, Vulkan, or render thread
  (`Engine ready on no graphics` in about 60 ms). The fixed steps run and the frame group after them, and nothing is
  drawn. The loading screen waits only for the world's columns (about 1.5 s), screenshots refuse, and `wait frames`
  counts those frames. `game.py start --headless` gives it.
- `--Engine:FreeRun=true`, only with `Mode=None`, runs the steps back to back. Ten minutes of play (`wait ticks 36000`)
  take about 4 s in Debug and under 1 s in Release.
- A host and a client both without graphics play a session on 127.0.0.1. Give `--Engine:Session:Port` to keep off a
  running game's port.

## Repeatable runs and timing

- `--Engine:Physics:Threads=1` makes physics repeat run to run; the default spreads it over a quarter of the
  processors.
- `--Engine:Renderer:DynamicRenderScaling=false` with `RenderScale=0.75` holds the render scale.
- `--Engine:Window:VSync=false` keeps a locked screen from stopping the frames, and `--Engine:Ui:Stats=true` shows the
  frame stats.

## The world and the view

- `--Engine:Voxels:Seed=7` picks the world. `Levels=1` streams level 0 only, a fraction of the memory.
- `--Engine:Voxels:DebugWalkSpeed=5` flies the debug camera round a 100 m circle, `Engine:Camera:CircleHeightMetres` up.
- `--Engine:Renderer:DistantShadowMetres` adds a third shadow cascade for hills in the distance, drawn only while the
  sun is under 30 degrees: check it with the console's `sun` or `time` low.
- `--Engine:Renderer:Shadows=false` draws no shadow maps. `--Engine:Renderer:PreferredDevice=RAPHAEL` picks a device by
  any part of its name (this machine's integrated GPU). `--Engine:Renderer:DebugLines=true` draws the debug lines.
- `--Engine:Renderer:ShaderSourceDirectory=$PWD/src/Itera.Engine/Rendering/Shaders`, then the console's
  `shaders reload`, compiles the engine's shaders from the repository. A game's own shaders come from `Shaders/Game/`
  beside the executable, or from the repository with `GameShaderSourceDirectory=$PWD/src/Game/Shaders`, which
  `game.py start` gives when the folder exists.
- `--Engine:Ui:MenuPausesOffline=true` holds an offline match's steps while the menu is open.

## Systems, the console, and logging

- `--Engine:Particles:Enabled=false`, `--Engine:Audio:Enabled=false`, `--Engine:Audio:Cache=false`, and
  `--Engine:Loading:Enabled=false` turn those parts off.
- `--Engine:Control:Enabled=true` opens the control port (127.0.0.1:47650; `Port`). `--Engine:Benchmark:Enabled=true`
  runs the benchmark (`tools/benchmark`).
- `"--Engine:Console:Run=balls 8; pos"` runs commands once the first match's world and systems exist; with `wait`, as
  a script. `--Engine:Console:RunFile=PATH` runs a file of command lines (`#` starts a comment) after `Run`'s.
  `--Engine:Console:ExitOnFailure=true` ends the run with exit code 1 when a startup command fails.
- `--Serilog:MinimumLevel:Default=Verbose` logs everything. `--Serilog:MinimumLevel:Override:Itera.Engine.Modding=Verbose`
  with `--Engine:Logging:ConsoleLevel=Trace` logs one area to the console. `--Engine:Logging:FileEnabled=false` writes no
  log file.
