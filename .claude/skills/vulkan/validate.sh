#!/usr/bin/env bash
# Builds the game (src/Game, Debug), runs it for a few seconds, then lists every [Vulkan] Warning or Error line from
# the new log. Debug builds turn on core, synchronization, and best-practices validation in code
# (VulkanContext, through VK_EXT_layer_settings); this script checks the log says so.
# Exit code 0 means a clean run. Usage: validate.sh [seconds] [game arguments...]   (default 25: the Debug loading
# screen takes about 5 s, and only after it are the world, the models, the fade, and the upscale drawn). The starter
# game draws no models until something makes a loose body or the game adds models; pass
# --Engine:Renderer:DynamicRenderScaling=false --Engine:Renderer:RenderScale=0.75 for the upscale at a fixed scale.
# SMAA records only with one sample a pixel: add --Engine:Renderer:MsaaSamples=1 (the default is 4).
# The run keeps its own data folder (the game's DataFolder with "-agent" after it, as tools/control/game.py's runs), so
# it never writes the player's settings.json and its log is found there.
# Linux only (log path under ~/.local/share, GNU timeout, vulkaninfo). Development tool, not game code.
#
# The setting names ("validate_sync", "validate_best_practices") were checked against validation layer
# 1.4.341: on that version they reproduce the TopOfPipe acquire-barrier hazard (see reference.md).
# After a layer upgrade: in FrameRenderer.Record, change the acquire barrier's source stage
# (PipelineStageFlags2.ColorAttachmentOutputBit, the first TransitionImage call) to TopOfPipeBit,
# confirm this script fails with WRITE_AFTER_READ, restore it, then update VERIFIED_LAYER_VERSION.
# Last done on 2026-09-22 with the synchronization2 barriers: 10 WRITE_AFTER_READ lines, then PASS.
set -euo pipefail

VERIFIED_LAYER_VERSION="1.4.341"

seconds="${1:-25}"
shift || true
repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
data_folder_name="$(python3 "$repo/tools/benchmark/data_folder.py" --agent --name)"
logs="$(python3 "$repo/tools/benchmark/data_folder.py" --agent)/Logs"
binary="$repo/src/Game/bin/Debug/net10.0/Game"

layer_version="$(vulkaninfo --summary 2>/dev/null | awk '$1 == "VK_LAYER_KHRONOS_validation" { print $(NF-2) }')"
if [[ -z "$layer_version" ]]; then
    echo "FAIL: VK_LAYER_KHRONOS_validation not found by vulkaninfo" >&2
    exit 2
fi
if [[ "$layer_version" != "$VERIFIED_LAYER_VERSION" ]]; then
    echo "FAIL: validation layer is $layer_version; the settings here were verified on $VERIFIED_LAYER_VERSION." >&2
    echo "      Re-verify as described at the top of this script, then update VERIFIED_LAYER_VERSION." >&2
    exit 2
fi

dotnet build "$repo" -c Debug -v quiet -nologo

before="$(ls -t "$logs"/*.log 2>/dev/null | head -1 || true)"

# Under sway, the game's window goes full screen on DP-1 (the development screen), as tools/control/game.py places it;
# without this sway opens it on the focused screen.
place_window() {
    command -v swaymsg > /dev/null || return 0
    for _ in $(seq 1 100); do
        local pid
        pid="$(pgrep -nf "^$binary( |$)" || true)"
        # Read whole, not piped into grep -q: with pipefail a large tree's early exit would read as no match.
        local tree
        tree="$(swaymsg -t get_tree 2> /dev/null || true)"
        if [[ -n "$pid" && "$tree" == *"\"pid\": $pid,"* ]]; then
            swaymsg "[pid=$pid] move container to output DP-1, focus, fullscreen enable" > /dev/null 2>&1 || true
            return 0
        fi
        sleep 0.1
    done
}
place_window &

run_exit=0
# Steam off: starting the Steam API loads Steam's overlay and fossilize Vulkan layers into the game, and the overlay's
# own small image draws a best-practices warning (seen 2026-09-26) that is not Itera's.
timeout --preserve-status -s INT "$seconds" "$binary" --Engine:Steam:Enabled=false --Engine:Session:Mode=Offline \
    "--Engine:Application:DataFolder=$data_folder_name" "$@" > /dev/null 2>&1 || run_exit=$?
wait

log="$(ls -t "$logs"/*.log 2>/dev/null | head -1 || true)"
if [[ -z "$log" || "$log" == "$before" ]]; then
    echo "FAIL: no new log file in $logs" >&2
    exit 2
fi

echo "Log: $log"
echo "Game exit code: $run_exit"

if ! grep -q "validation on (sync, best practices)" "$log"; then
    echo "FAIL: full validation was not active; the instance line should read 'validation on (sync, best practices)'" >&2
    grep "instance created" "$log" >&2 || true
    exit 2
fi

problems="$(grep -E '\[(WRN|ERR|FTL)\].*\[Vulkan\]' "$log" || true)"
if [[ -n "$problems" ]]; then
    echo "$problems"
    echo "FAIL: $(printf '%s\n' "$problems" | wc -l) Vulkan validation Warning/Error lines" >&2
    exit 1
fi

if [[ "$run_exit" -ne 0 ]] || ! grep -q "Shutdown complete" "$log"; then
    echo "FAIL: the run did not shut down cleanly; read the log" >&2
    exit 1
fi

echo "PASS: no Vulkan validation warnings or errors (core + sync + best practices)"
