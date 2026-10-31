---
name: run-game
description: "How to check a change in the game (src/Game) on the Itera engine, the cheapest way first (tests, HeadlessMatch scenarios, runs with no graphics, offscreen screenshot sets, a window last), and how to run and drive it: options on the command line, unattended scripted runs, exit codes, the start-up log lines, the console and its commands, and the control port (tools/control/game.py). Use before checking a change, starting the game, driving it with game.py or console commands, or reading a run's log."
---

# Running and driving the game

The build, test, and run commands are in `CLAUDE.md`. The game is `src/Game`, and its executable is
`src/Game/bin/<Config>/net10.0/Game`. Its data folder holds its logs, saves, and settings:
`~/.local/share/<DataFolder>/`, where `DataFolder` is `Engine:Application:DataFolder` in `src/Game/appsettings.json`
(`python3 tools/benchmark/data_folder.py` prints it).

Runs Claude starts use `<DataFolder>-agent` instead (`data_folder.py --agent` prints it), so the player's
`settings.json` and logs are never touched. They play at 1 % of the master volume, so they stay quiet beside the
user's own sound. `tools/control/game.py` and `validate.sh` pass both, and so does `tools/benchmark/run.sh` with
`BENCH_AGENT=1`. A run by hand gives `--Engine:Application:DataFolder=<DataFolder>-agent --Engine:Audio:Volume=0.01`.

Files beside this one:

- `startup-log.md`: the lines a run logs as it starts and ends, the exit codes, and what a log file holds.
- `options.md`: which options to set for which check.
- The generated reference lists every option with its default and range (`docs/reference/engine-options.md`, the
  game's `game-options.md`), and every console command with its arguments and help (`docs/reference/game-commands.md`).

## Checking a change

Check a change the cheapest way that can see it. Open a window only when the window itself is what changed (its
swapchain, presenting, focus, or full screen): a window takes the user's desktop, and offscreen and no-graphics runs
load faster. `game.py start` runs offscreen unless given `--window` or `--fullscreen`.

| What changed | Check it with |
|---|---|
| Logic, maths, a service, a system's rules | A test in memory (`.claude/rules/tests.md`): `dotnet test --filter`. |
| Behaviour in the real world and systems (moving, physics, streaming, a game feature) | A scenario test over `HeadlessMatch` with `Engine:Physics:Threads=1` (the `scaffold` skill). Or a run with no graphics (`--Engine:Window:Mode=None`) whose startup script reads the state (`pos`, `ecs list COMPONENT`, `ecs show player`, log lines) and quits: about 5 s in Debug. |
| Minutes of play (resting, streaming over time, drift) | No graphics with `--Engine:FreeRun=true` and `wait ticks N` (60 a second): 10 minutes in about 4 s in Debug. |
| The picture | `game.py shots VIEW... --out DIR --compare OLD` (offscreen; four views in about 15 s in Debug), then read the PNGs. `tools/benchmark/imgdiff.py` exits 1 when two sets differ. A figure's motion: `tools/control/moving.py` (a sheet and a GIF from its side). |
| Something to try by hand | `game.py start`, then `run -s`, `query`, `status`, `shot`, and `input`. Offline, `pause`, `step N`, and `timescale`. For edits without a restart, `shaders reload` or `dotnet watch`. |
| Vulkan code | `validate.sh --offscreen` while iterating, and both standard profiles with a window before it is done (the `vulkan` skill). |
| Sessions | The in-process session tests. Or a host and a client with no graphics on 127.0.0.1, with `--Engine:Session:Port` apart from a running game's. |
| GPU time | `tools/benchmark/run.sh` with `BENCH_AGENT=1` on a quiet machine (ask the user first: another game moves timings by 20 % or more). Compare offscreen runs with offscreen runs and windowed with windowed: offscreen measures 1 to 3 % lower. |

Pitfalls:

- `wait seconds N` counts real time, not the game's: with `timescale` or `FreeRun`, wait for `ticks` instead.
- `FreeRun` needs `Mode=None`. With no graphics, `screenshot` and the port's `shot` refuse, and `wait frames` counts the
  frames built after the steps.
- The port's `run` runs commands outside any script, so a `wait` there fails: use `run -s`, or the port's own `wait`.
- `pause`, `resume`, `step`, and `timescale` work only offline. `pause` and `step` refuse while the world loads.
- Physics repeats run to run only with `Engine:Physics:Threads=1`. A test or comparison that needs equal results sets
  it; the default spreads physics over threads.
- `HeadlessMatch.WaitUntilLoaded` waits on the clock. The column workers and the navigation thread need real time, so
  stepping alone never loads the world. It returns once the columns are resident and the navigation grid is taken.
- Debug builds are optimised but run Debug-only checks: time game logic in Release.
- The console's `settings`, `shadows`, and `sound volume` write the data folder's `settings.json`. Run in the agent
  data folder (`game.py` and the tools do).
- Two screenshot sets of one build agree exactly only with dynamic render scaling off (`shots` turns it off) and on a
  quiet machine.

## Options

Options go after `--` as `--Section:Key=value`: the engine's under `Engine:`, the game's under `Game:`. The generated
reference lists them all with their defaults and ranges, and `options.md` says which to set for which check. An invalid
value or an unknown key under `Engine` or `Game` stops the game before the window opens, with exit code 1 and a message
naming the key.

## Unattended runs

Run with no window: `--Engine:Window:Mode=None` when the check needs no picture (the fastest), `Offscreen` when it does.
A startup script runs a check on its own and ends the run. Give the commands in `Engine:Console:Run` (or a file of
command lines in `Engine:Console:RunFile`), with `wait` between them, `screenshot PATH`, and `quit`. With
`--Engine:Console:ExitOnFailure=true`, a failed command or a wait past its time ends the run with exit code 1:

```bash
timeout --preserve-status -s INT 120 src/Game/bin/Debug/net10.0/Game --Engine:Session:Mode=Offline \
  --Engine:Window:Mode=Offscreen --Engine:Steam:Enabled=false --Engine:Application:DataFolder=<DataFolder>-agent \
  --Engine:Audio:Volume=0.01 --Engine:Console:ExitOnFailure=true "--Engine:Console:Run=wait loaded; balls 4; wait seconds 2; pos; screenshot 0.5 /tmp/shot.png; quit"
```

- That run takes about 6 s in Debug: 7 s in a window, 4.7 s with no graphics and no screenshot, and 4.6 s offscreen in
  Release. Its log goes to `~/.local/share/<DataFolder>-agent/Logs/`.
- Without the session mode the run stays at the menu.
- In a window, add `--Engine:Window:VSync=false`, so a locked screen does not stop the frames.
- `timeout` is the safety net. A background run started from a script ignores SIGINT, so send it SIGTERM. `timeout`
  signals the process and its process group, so the game receives SIGINT twice at once, which `ExitSignalPolicy` takes
  as one request.
- A run shows the loading screen for about 4.5 s, in Debug or Release. For a short check of play, add
  `--Engine:Loading:Enabled=false`.

## The control port

The control port (`Engine:Control`, off unless `Enabled`; `ControlService`) lets `tools/control/game.py` drive a
running game (its README has every verb). Through it a tool:

- starts the game from a copy of the build, and restarts it with the same options (`game.py restart`);
- runs console commands and gets their answers and log lines (`run -s` runs them as a script, so `wait` and
  `screenshot` hold the commands after them, and the reply comes when all have run);
- reads the match's entities as JSON (`query`) and its steps (`status`: tick, paused, time scale, and the player's
  position);
- waits for loading, frames, fixed steps (`ticks`), seconds, or a log line;
- measures the GPU passes: a game's own by name (`Game NAME`) and their sum (`Game`), and the image views' updates
  (`ImageViews`);
- takes screenshots from the game itself (`ScreenshotPass`), and reloads the shaders from the repository
  (`shaders reload`: every pass compiles, then the pipelines are made again, and an error keeps the old ones);
- sends input: keys, buttons, look, the wheel, a capture of the pointer that needs no focus, and `point X Y` and
  `type TEXT` for the menu.

Use it instead of restarts. It reads lines of at most 1 MiB and takes 16 tools at once.

`game.py shots VIEW... --out DIR [--compare DIR] [--size WxH] [--release]` starts the game offscreen. It takes a
screenshot of each view once its world has loaded and its light settled (`Engine:Camera:StartFlying`, `tp VIEW`, `wait view`), stops the
game, and compares the set with an earlier one (`tools/benchmark/imgdiff.py`). A view is one of the game's `tp` places
or `x,y,z,yaw,pitch`.

`game.py start` runs offscreen unless given `--window` (a window on HDMI-A-1) or `--fullscreen` (full screen on DP-1,
VSync off). With `--window`, add `-- --Engine:Window:VSync=false` when the screen may lock.

Under `dotnet watch` (Debug), a changed method body or a new method applies to the running game in about 1 to 3 s,
from the method's next call. Constructors and static initialisers do not run again, and an edit watch cannot apply
restarts the game. Watch builds into `bin/Debug` in place, so no other build or test run may share it.

```bash
DOTNET_WATCH_SUPPRESS_EMOJIS=1 dotnet watch --non-interactive --project src/Game run -- --Engine:Control:Enabled=true
```

`DOTNET_ENVIRONMENT=Development` shows Debug log lines on the console too.

## The console

The console (`DevConsole`, `ConsoleLayer`) opens and closes with the backquote key; Escape also closes it. While it is
open, keys, characters, and the wheel go to it:

- Enter runs the line, with commands separated by `;`. Up and Down step through the history, and Tab completes a
  command's name. Page Up and Page Down scroll, and Ctrl+V pastes.
- It shows log lines of Information and above, and the commands' answers. A setting given no value prints it.
- Commands run between fixed steps, each logged at Information (`Console: ...`).
- Commands run as scripts (`DevConsole.Hold`): the lines typed are one, and the startup commands (`Run`, then
  `RunFile`'s lines) another. `wait` holds the commands after it in its script, and `screenshot` holds them until its
  PNG is written. A hold past its time (300 s for `wait` unless given, 60 s for a screenshot) fails and drops the rest.
- `help` lists the commands (`ConsoleCommands`), and `docs/reference/game-commands.md` lists every one with its
  arguments, where it may run, and its help. In a session, the commands that change what the session shares work only
  on the host, and `pause`, `resume`, `step`, and `timescale` only offline.
