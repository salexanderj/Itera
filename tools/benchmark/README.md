# GPU benchmark tools

Scripts that run the game's benchmark mode (`Engine:Benchmark`, `BenchmarkSystem`) and read its logs. Linux with sway
only (`swaymsg`, `jq`, `grim`), Python 3, and Pillow and NumPy for `imgdiff.py`. Development tools, not game code. They
run the game in `src/Game` and find its data folder from `Engine:Application:DataFolder` in its `appsettings.json`
(`data_folder.py`).

## What the benchmark does

With `--Engine:Benchmark:Enabled=true` the camera visits the game's fixed views of the seed's world (`BenchmarkViews`
in `src/Game`; the starter's nine: `start`, `start-low`, `down`, and `up` at the start pose, `high` 40 m over the
world's middle, `far` 200 m out along +x looking back, `distant` 3,500 m out along +x looking back, and `forest` and
`forest-high` inside a forest at eye height and 40 m over it). At each it waits until every column is loaded and on the
GPU, waits `SettleSeconds` (2), and measures for `MeasureSeconds` (8). Then it flies the automatic circle at 5 m/s for
`FlightSeconds` (60) and exits. The log marks the start and end of each measurement. A game that changes its world or
its views starts a new series: runs of other views do not compare.

The number that matters is the world pass's GPU time (`voxels` in the stats line: the shadow maps, the meshes, the
models, and the sky), from the frame stats lines, one per second. `parse.py` also takes the shadow maps' part, the
models' draws per frame (`models_ms`: the models drawn whole, before the ground; the fading ones count in `voxels`
alone), and from the meshes line the MB uploaded, the triangles drawn, and the mesh heap in use. A game's own render
passes count as their sum (`game_ms`); those at the `Compute`, `Opaque`, and `Transparent` stages are also inside
`voxels`, those at `Scene` and `Screen` are not. Image views are in neither: they count as `views_ms`, per frame. A
game's benchmark hides its views unless it asks for them (the starter's minimap hides while
`Engine:Benchmark:Enabled`), so its numbers stay comparable.

A game that holds a scene to a whole frame's budget reads `frame_gpu_ms`, the whole frame's GPU time, and
`frame_gpu_p99_ms`, the highest of the measured seconds' 99th percentiles. The frames shown are `fps`, a second, and
`frame_p99_ms`, the highest of the seconds' 99th percentiles of the time between two, with the main thread's work in it. A host's step is `step_ms`, the sum of its
parts' averages, with `systems_ms` its systems' part and `step_max_ms` its longest. `compare.py` prints them when both
groups have them. `parse.py` reads any older `game.log` again: `parse.py game.log > summary.json`.

## Measuring

```bash
dotnet build -c Release
cp -r src/Game/bin/Release/net10.0 /tmp/game-before         # keep a build to compare against
tools/benchmark/run.sh before-1                              # BENCH_BIN picks the build; default: the Release output
BENCH_BIN=/tmp/game-before tools/benchmark/run.sh before-2
tools/benchmark/compare.py before before-1,before-2 after after-1,after-2
tools/benchmark/imgdiff.py before-1 after-1                  # the same picture? (also two folders or two PNG files)
tools/benchmark/run.sh before-3 --offscreen                  # no window (BENCH_SIZE, 2560x1440): the monitor stays free; no screenshots
tools/benchmark/run.sh long --Engine:Benchmark:MeasureSeconds=1 --Engine:Benchmark:SettleSeconds=0 --Engine:Benchmark:FlightSeconds=600
tools/benchmark/flight.py long                               # uploads, the mesh heap, and pending blocks, minute by minute
```

- `run.sh` makes the window full screen on `BENCH_OUTPUT` (default `DP-1`) and runs with VSync off, so the GPU never
  waits for the display and its clocks stay up. It starts the match at once (`Engine:Session:Mode=Offline`, no menu)
  and gives the render scale (1, with dynamic render scaling off), field of view (90), windowed mode, shadows, and
  anti-aliasing (on, as shipped) on the command line, so the player's `settings.json` (the menu's Settings) changes no
  measurement; the log's `Settings: ...` line shows what applied, and `--Engine:Renderer:RenderScale=0.67` after the
  label measures another fixed scale (`--Engine:Renderer:AntiAliasing=false` the picture without anti-aliasing).
  `parse.py` also gives each view's anti-aliasing (SMAA) and upscale passes per frame, and the game's own render passes
  (their sum, `game_ms`), and `compare.py` prints them when either group ran them. The label must be letters, digits,
  `.`, `_`, or `-`. Stopping the script stops the game, and a view with no samples makes it exit non-zero. Results go
  to `BENCH_OUT` (default the data folder's `Benchmarks/<label>`): `game.log`, a screenshot per view, and
  `summary.json`. `BENCH_AGENT=1` runs in the data folder of the runs Claude starts (`<DataFolder>-agent`), so they
  leave the player's logs and benchmarks alone.
- Take the runs of two builds in turn (before, after, before, after, ...): runs of one build agree within about 1 %
  per view, so differences of about 1 % are at the edge of the noise.
- `--offscreen` runs with no window, its frames capped at 1000 a second (`Engine:Window:OffscreenFps`; at the offscreen
  240 the GPU was busy 54 to 70 % of the time and lowered its clocks, to 851 MHz against 1963 at the `up` view, which
  took 83 % longer; uncapped it is 99 % busy at every view). Its times are 1 to 3 %
  below a window's on every view (the starter's views, 2026-10-09), so compare offscreen runs only with offscreen runs.
- A run takes about 2.5 minutes and takes over the monitor (with `--offscreen`, not).

## Pitfalls

- A run whose game log stops growing for 60 s is stopped and reported as `STALLED` (exit code 3), with the log kept in
  its results folder.
- After an hour without input, swayidle locks the screen. Runs still measure correctly (VSync off), but screenshots are
  black; `imgdiff.py` says so. Later swayidle turns the monitors off, and then full screen does not take: `run.sh`
  prints a warning when a run was measured at another size than the monitor's.
- Every run writes a log to the data folder's `Logs/`, where only the newest 20 are kept. `run.sh` copies each run's log
  into its results folder.
- Views and the flight depend on the seed (1 by default); compare runs of the same seed.
- `run.sh` turns Steam off (`--Engine:Steam:Enabled=false`): the Steam API loads Steam's overlay and fossilize Vulkan
  layers into the game when the Steam client runs.
