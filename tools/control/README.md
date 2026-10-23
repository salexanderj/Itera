# game.py: driving a running game

`game.py` starts the game (`src/Game`) and then drives it through the game's control port (`Engine:Control`, `ControlService`).
One game stays open across many checks: console commands, screenshots, shader edits, and input go to it without a
restart. It uses only Python's standard library. It is a development tool, not game code.

```bash
tools/control/game.py start [--release] [--window|--fullscreen|--headless] [--no-wait] [--menu] [-- game arguments]
tools/control/game.py shots VIEW... --out DIR [--compare DIR] [--size 1280x720] [--release] [--settle 2.5]
tools/control/game.py run [-q] "tp 0 20 0"  # console commands; quote a line with ';' in it; -q leaves out log lines
tools/control/game.py run -s "step 60; wait ticks 60; pos"   # as a script: waits hold the rest; replies when done
tools/control/game.py query [player | ID[:VERSION] | COMPONENT] [--most N]   # the match's entities as JSON
tools/control/game.py shot [path] [--scale 0.5] [--crop x,y,w,h] [--no-ui]
tools/control/game.py reload [--timeout 60]               # compile the shaders again from the repository
tools/control/game.py gpu [--frames 240] [--timeout 30]   # each GPU pass's time over the next frames
tools/control/game.py input "capture on" "hold W 1" "look 200 0" "click left 0.5" [--timeout 60]
tools/control/game.py wait loaded | frames N | ticks N | seconds N | log PATTERN [--timeout S]
tools/control/game.py log [--new] [--level debug] [--tail 40]
tools/control/game.py status                # frames, clock, steps (tick, paused, held by the menu, time scale), loading, role, origin, camera (absolute),
                                            # entities, and the player's position, velocity, contact, and mode
tools/control/game.py stop                  # quit, then SIGTERM after 10 s, then SIGKILL
tools/control/game.py restart [same options as start]
```

Every command takes `--port N` before its name to reach a game on another control port.

## start

`start` copies the build into `Run/debug/` (or `Run/release/`) in the agent data folder (the game's `DataFolder` with
`-agent` after it, `~/.local/share/IteraGame-agent/` for the starter), links the large `runtimes/` folder, and starts
the copy with that data folder, so it never writes the player's `settings.json` and its logs and screenshots stay
apart. A `dotnet build` then cannot overwrite a file the running game has loaded. It adds these
arguments before yours:

- `--Engine:Control:Enabled=true`
- `--Engine:Steam:Enabled=false`
- `--Engine:Renderer:ShaderSourceDirectory=<repo>/src/Itera.Engine/Rendering/Shaders`
- `--Engine:Renderer:GameShaderSourceDirectory=<repo>/src/Game/Shaders`, when the game has that folder
- `--Engine:Application:DataFolder=<DataFolder>-agent`
- `--Engine:Session:Mode=Offline` (straight into an offline match), unless `--menu`

`--menu` starts at the main menu and does not wait for loading:
click its buttons with `input "point X Y" "click left"` (X and Y in the frame's pixels, as a full-size screenshot shows
them), and start matches with the console's `play`, `host`, `join`, and `leave`.

The game runs offscreen unless asked for a window (below), so nothing opens on the desktop. With `--window`, its window
goes to HDMI-A-1 with swaymsg; with `--fullscreen`, full screen on DP-1 with VSync off, which GPU timings need. Without
sway, the window stays where the system puts it.

`start` then waits for the port and for the loading screen to finish. `--no-wait` skips the wait for loading. The
state file `Run/state.json` holds the pid, the port, and the log's path. `start` refuses to start a second game while
the first still runs.

## No window, and screenshot sets

`start` runs the game with `--Engine:Window:Mode=Offscreen` (`--offscreen` says so, and is the default): no window
opens, the game draws into a headless surface at `Engine:Window:Width` x `Height`, and input comes only from this port
(`input`). It also loads faster than in a window: the world uploads a share each frame, and offscreen frames run at
up to 240 a second, a window's at the monitor's rate (3.2 s against 7.7 s on HDMI-A-1, Debug). `start --headless` runs
it with `--Engine:Window:Mode=None`: no graphics at all, the steps and the frame group run and nothing is drawn (`shot`
refuses); add `-- --Engine:FreeRun=true` to run the steps back to back.

`shots` starts the game offscreen (with dynamic render scaling and the stats line off, so every set has the same size),
flies the camera to each view (a game's `tp` place, or `x,y,z,yaw,pitch`), waits until the view's world is loaded and
on the GPU and its light has settled, writes `DIR/<view>.png` without the UI, stops the game, and with `--compare` runs
`tools/benchmark/imgdiff.py` against an earlier set (exit code 1 when a picture differs). It refuses while another game
started by `start` runs. An orthographic set: `--game "--Engine:Console:Run=lens ortho 120"` (120 m from the image's
top to its bottom, for the whole run); a view straight down is `X,40,Z,0,-89`. Repeat `--game` for more options:
`--game=--Engine:Voxels:Levels=9 --game=--Engine:Renderer:ViewDistanceMetres=8000`.

## Replies

Each command prints four things, where there are any:

- the answers of the console commands it ran
- their failures
- the log lines of Information and above written while it ran
- the reply's own fields

The exit code is:

- 0 when the reply is ok
- 1 when it is not
- 2 when no game answers

## Screenshots

`shot` copies the picture on the render thread and writes a PNG (`ScreenshotPass`), so it needs no compositor and
works wherever the window is.

- The default scale is 0.5. The image reader shrinks anything over about 1568 pixels anyway, so a smaller file costs
  nothing.
- `--crop` is in screen pixels, taken before the scale.
- `--no-ui` copies the picture before the UI is drawn over it.

The console's `screenshot [scale] [noui] [PATH]` writes to PATH, or to `Screenshots/` in the data folder.

## The figure moving

`moving.py KEYS --view right|left|front|back|top --out DIR` takes pictures of the player's figure moving, to judge its
motion. It drives a running offline game: it puts the player at `--start` (tp's place), pauses, holds the keys (joined
by `+`, as `ShiftLeft+W+A`), and steps a run-up. Then it steps `--every` ticks at a time for `--frames` frames. At each
stop it puts an orthographic camera (`viewpoint`, `lens ortho`) `--distance` metres off the figure's side, front, or
back, or above it, and takes a picture.

- It writes the frames, `sheet.png` (`--sheet` frames spread through the stride, in a row), and `moving.gif` (every
  frame, at the game's speed, or `--slow` times slower).
- `--pass "LABEL=COMMANDS"` runs a pass after those console commands, such as a tunable to compare. Repeat it: the
  sheet stacks the passes in rows, and the GIF plays them side by side.
- It needs Pillow. The game must be offline, since it pauses and steps it.

## Shaders

`reload` compiles every pass's shaders from the repository. Only when all of them compile does it make the pipelines
again. An error keeps the old pipelines and prints the compiler's message with the file and line. It takes about
0.6 s for an edited shader and 10 ms when nothing changed; the SPIR-V cache holds the unchanged ones. The console
command is `shaders reload`.

## Input

`input` runs its actions in order, on the game's clock:

- `down KEY`, `up KEY`, `tap KEY`, `hold KEY SECONDS`
- `click left|right|middle [SECONDS]`
- `look DX DY` (pointer pixels)
- `scroll STEPS`: the wheel, away from the user positive (build mode's sizes, the shoulder camera)
- `point X Y`: the pointer at that pixel of the frame, for the menu (a click after it clicks there)
- `type TEXT`: the rest of the line typed, as into the console or the menu's address box
- `capture on|off`
- `wait SECONDS`

KEY is a Silk.NET `Key` name (W, Space, Escape, ShiftLeft, ControlLeft, F3) or a digit. `capture on` captures the
pointer for look without the window's focus and leaves the real pointer alone. Escape frees it, as it frees a real
capture, and opens the menu over the match. With the pointer captured, `look` turns the view and `hold W 1` walks.

## C# changes

`game.py restart` runs a new build. For small changes, the game can instead run under `dotnet watch`, which applies
edits to the running Debug game:

```bash
DOTNET_WATCH_SUPPRESS_EMOJIS=1 dotnet watch --non-interactive --project src/Game run -- \
    --Engine:Control:Enabled=true --Engine:Steam:Enabled=false
```

Measured on 2026-09-29:

- A changed method body or a new method, in the engine or the game, applied in 0.6 to 2.8 s.
- A method runs its new body from its next call. Systems' `Update` methods and console commands change at once; the
  main loop, already running, does not. Constructors and static initialisers do not run again.
- An edit that cannot be applied (a field's type changed) restarts the game by itself, in about 7 s with
  `--Engine:Loading:Enabled=false`.

Watch builds into `bin/Debug` in place, so no other build or test run may share it. Run `game.py` commands against it
as usual.

## Timings

`gpu` averages each GPU pass over the next frames it measures, so a before and after need no 10 s stats line; a game's
own render passes come as `Game NAME` each and `Game` for their sum, and the image views as `ImageViews`. For
numbers to compare, start with `--release --fullscreen`. `tools/benchmark/run.sh` still takes its screenshots with
grim; the in-game `shot` could replace that later.

## Protocol

The port is TCP on 127.0.0.1 (default 47650). Each request and each reply is one JSON object on one line, for example
`{"id":1,"op":"run","line":"shadows off"}`. A line that does not start with `{` is answered with an error and ends the
connection, and a last line without its end is never run, so a web page's request to the port runs nothing. The ops are `run` (with `"script": true`, a script: waits and screenshots
hold the commands after them, and the reply comes when all have run), `log`, `wait`, `gpu`, `status`, `query`, `shot`,
`reload`, and `input`. `ControlService` documents their fields.

## Reading and stepping the game

`query` reads the match's entities (`EcsInspector`): `query player` (or an id as `ecs list` prints it, `12:1`) gives
every component of that entity, with vectors as arrays; `query CharacterPosition` the entities with that component and
its value; `query` alone the archetypes and their counts. The console's `ecs counts|list|show` prints the same as
text. Offline, `pause`, `resume`, `step N`, and `timescale X` control the fixed steps; send a step and its wait in one
script (`run -s "step 60; wait ticks 60; ecs show player"`): separately, the steps may have run before the wait
starts.

Any program on this machine can connect, so the port is off unless `Engine:Control:Enabled` is true, and Steam builds
never turn it on.
