#!/usr/bin/env bash
# Runs the GPU benchmark (Engine:Benchmark, BenchmarkSystem) of the game in src/Game once, full screen on one monitor, and writes to
# $BENCH_OUT/<label>/: the game's log (game.log), one screenshot per view (for imgdiff.py), and summary.json (parse.py).
# Prints one line: the world pass time per view in ms, the flight's, the exit code, and the sizes measured.
#
# Usage: run.sh <label> [--offscreen] [game arguments...]     for example: run.sh base --Engine:Renderer:Shadows=false
#   --offscreen   no window (Engine:Window:Mode=Offscreen) at BENCH_SIZE (default 2560x1440): the monitor stays free and a
#                 locked screen changes nothing, but no screenshots are taken (tools/control/game.py shots takes them).
#                 Frames are capped at 1000 a second, not the offscreen 240, so the GPU never idles and lowers its clocks
#                 (at 240 the views took up to 83 % longer). Its times are 1 to 3 % below a window's: compare offscreen
#                 runs only with offscreen runs.
#   BENCH_BIN     folder of the build to run (default: the repo's Release output). Copy the output folder elsewhere to
#                 keep building while a run goes on, and to compare two builds.
#   BENCH_OUT     results folder (default the game's data folder's Benchmarks/, data_folder.py).
#   BENCH_OUTPUT  sway output to go full screen on (default DP-1, the 2560x1440 monitor).
#   BENCH_AGENT   1: the data folder of the runs Claude starts (<DataFolder>-agent, data_folder.py --agent), so its runs
#                 leave the player's logs and benchmarks alone.
# Linux with sway only (swaymsg, jq, grim); python3 for parse.py. Development tool, not game code.
set -uo pipefail

if [[ $# -lt 1 ]]; then
    echo "usage: run.sh <label> [--offscreen] [game arguments...]" >&2
    exit 2
fi

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
label="$1"
shift
offscreen=0
if [[ "${1:-}" == "--offscreen" ]]; then
    offscreen=1
    shift
fi
size="${BENCH_SIZE:-2560x1440}"
window_args=()
if ((offscreen)); then
    window_args=(--Engine:Window:Mode=Offscreen "--Engine:Window:Width=${size%x*}" "--Engine:Window:Height=${size#*x}"
        --Engine:Window:OffscreenFps=1000)
fi
# The label names a folder that is deleted first: an empty, "/", ".", or ".." label once reached the whole Benchmarks
# folder.
if [[ ! "$label" =~ ^[A-Za-z0-9._-]+$ || "$label" == "." || "$label" == ".." ]]; then
    echo "run.sh: the label must be letters, digits, '.', '_', or '-' (not '.' or '..'); it is '$label'" >&2
    exit 2
fi
agent=()
if [[ "${BENCH_AGENT:-}" == 1 ]]; then
    agent=(--agent)
fi
data="$(python3 "$here/data_folder.py" "${agent[@]}")"
folder="$(python3 "$here/data_folder.py" --name "${agent[@]}")"
out="${BENCH_OUT:-$data/Benchmarks}/$label"
bin="${BENCH_BIN:-$repo/src/Game/bin/Release/net10.0}/Game"
output="${BENCH_OUTPUT:-DP-1}"
logs="$data/Logs"

if [[ ! -x "$bin" ]]; then
    echo "no build at $bin; run 'dotnet build -c Release' or set BENCH_BIN" >&2
    exit 2
fi

rm -rf "$out"
mkdir -p "$out"

# VSync off: mailbox presentation, so the GPU never waits for the display (and runs while the screen is locked).
# Stats every second: parse.py averages the seconds inside each measurement.
# Steam off: the Steam API loads Steam's overlay layer into the Vulkan chain (item 5 part B), which earlier runs lacked.
# The render scale (fixed: dynamic render scaling off), field of view, windowed mode, shadows, and anti-aliasing (on, as
# shipped; SMAA runs only when MSAA is off) given here, so the player's settings.json (the menu's Settings) changes no
# measurement (the game's "Settings: ..." line shows what applied); arguments after them still win, so
# --Engine:Renderer:RenderScale=0.67 measures that scale and --Engine:Renderer:AntiAliasing=false the picture without
# anti-aliasing.
"$bin" --Engine:Steam:Enabled=false --Engine:Session:Mode=Offline --Engine:Benchmark:Enabled=true --Engine:Window:VSync=false \
    --Engine:Renderer:DynamicRenderScaling=false --Engine:Renderer:RenderScale=1 \
    --Engine:Camera:FieldOfViewDegrees=90 --Engine:Window:Fullscreen=false \
    --Engine:Renderer:Shadows=true --Engine:Renderer:AntiAliasing=true \
    --Engine:Logging:StatsIntervalSeconds=1 "--Engine:Application:DataFolder=$folder" "${window_args[@]}" "$@" \
    > "$out/console.txt" 2>&1 &
pid=$!
# A script stopped early (Ctrl+C, an error) stops the game too, rather than leaving it full screen.
trap 'kill -TERM "$pid" 2> /dev/null' EXIT
trap 'exit 130' INT TERM

if ((!offscreen)); then
    for _ in $(seq 100); do
        if swaymsg -t get_tree | jq -e "[.. | select(.pid? == $pid)] | length > 0" > /dev/null 2>&1; then
            break
        fi
        sleep 0.1
    done
    swaymsg "[pid=$pid] move container to output $output, focus, fullscreen enable" > /dev/null
fi

log=""
for _ in $(seq 100); do
    log="$(ls -t "$logs"/*-"$pid".log 2> /dev/null | head -1)"
    [[ -n "$log" ]] && break
    sleep 0.1
done

# One screenshot per view, 2 s into its measurement. A locked screen gives black screenshots.
# The game writes stats every second, so a log that stops growing for 60 s means it hangs (2026-09-27: the main thread
# spun inside GLFW's wait for 16 minutes): the run is stopped, marked STALLED, and the script says so.
shots=0
deadline=$((SECONDS + 1800))
stalled=0
last_size=-1
last_growth=$SECONDS
while kill -0 "$pid" 2> /dev/null; do
    if ((SECONDS > deadline)); then
        kill -TERM "$pid"
        echo "TIMEOUT" >> "$out/console.txt"
        break
    fi

    size=$(stat -c %s "$log" 2> /dev/null || echo 0)
    if [[ "$size" != "$last_size" ]]; then
        last_size=$size
        last_growth=$SECONDS
    elif ((SECONDS - last_growth > 60)); then
        stalled=1
        echo "STALLED: the log did not grow for 60 s" >> "$out/console.txt"
        kill -TERM "$pid"
        sleep 5
        kill -KILL "$pid" 2> /dev/null
        break
    fi

    count=$(grep -c 'Benchmark view .* measuring at' "$log" 2> /dev/null || true)
    if ((!offscreen && count > shots)); then
        sleep 2
        name=$(grep 'Benchmark view .* measuring at' "$log" | sed -n "$((shots + 1))p" |
            sed -E 's/.*Benchmark view ([0-9]+) ([a-z-]+) measuring.*/\1-\2/')
        grim -o "$output" "$out/$name.png" 2> /dev/null || echo "no screenshot of $name" >> "$out/console.txt"
        shots=$((shots + 1))
    fi
    sleep 0.2
done

wait "$pid"
echo "exit $?" >> "$out/console.txt"
if ((stalled)); then
    cp "$log" "$out/game.log"
    echo "$label STALLED: the game's log stopped growing; see $out/game.log"
    exit 3
fi
cp "$log" "$out/game.log"
status=0
if ! python3 "$here/parse.py" "$out/game.log" > "$out/summary.json"; then
    echo "$label: some views have no samples; see $out/game.log" >&2
    status=4
fi
if ((offscreen)); then
    output="offscreen"
fi
python3 - "$out/summary.json" "$label" "$output" <<'PY'
import json, subprocess, sys
summary, label, output = json.load(open(sys.argv[1])), sys.argv[2], sys.argv[3]
views = " ".join(f"{name}={v['voxels_ms']:.3f}" for name, v in summary["views"].items())
flight = summary.get("flight")
flight_text = f"{flight['voxels_ms']:.3f}" if flight else "-"
sizes = sorted({v["size"] for v in summary["views"].values()})
print(f"{label} {views} flight={flight_text} exit={summary['exit']} size={','.join(sizes)}")
try:
    outputs = json.loads(subprocess.run(["swaymsg", "-t", "get_outputs"], capture_output=True, text=True).stdout)
    mode = next(o["current_mode"] for o in outputs if o["name"] == output)
    expected = f"{mode['width']}x{mode['height']}"
    if sizes and sizes != [expected]:
        print(f"WARNING: measured at {','.join(sizes)}, not {output}'s {expected}: full screen did not take (monitor off?)")
except Exception:
    pass
PY
exit "$status"
