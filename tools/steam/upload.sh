#!/usr/bin/env bash
# Builds the game (src/Game) for Windows x64 and Linux x64 and uploads both to Steam with steamcmd, as one build set
# live on the beta branch "testing". tools/steam/README.md has the one-time setup and what testers do. The App ID is the
# game's Engine:Steam:AppId (src/Game/appsettings.json); the depots and the account come from src/Game/steam.env.
#
# Usage: upload.sh [--preview] [--no-live] [--branch NAME] [--no-test]
#   --preview      steamcmd lists what it would upload and uploads nothing
#   --no-live      upload without setting the build live on a branch (the first upload, before "testing" exists)
#   --branch NAME  set the build live on NAME instead of "testing" (never the default branch: Steam refuses that)
#   --no-test      skip "dotnet test"
#   STEAMCMD       steamcmd command (default steamcmd)
# src/Game/steam.env (the game's own; shell assignments, sourced) gives:
#   STEAM_WINDOWS_DEPOT and STEAM_LINUX_DEPOT  the app's depot IDs (Steamworks: SteamPipe, Depots)
#   STEAM_BUILDER  the steamcmd account; it must have logged in by hand once
# Writes everything under publish/steam/ (ignored by git): content/<rid>/, scripts/, and output/ (steamcmd's logs and
# its cache, which makes later uploads faster). Development tool, not game code.
set -euo pipefail

preview=0
live=1
branch=testing
run_tests=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --preview) preview=1 ;;
        --no-live) live=0 ;;
        --branch)
            branch="${2:?--branch needs a name}"
            shift
            ;;
        --no-test) run_tests=0 ;;
        *)
            echo "usage: upload.sh [--preview] [--no-live] [--branch NAME] [--no-test]" >&2
            exit 2
            ;;
    esac
    shift
done
if [[ "$live" == 1 && ( -z "$branch" || "$branch" == default ) ]]; then
    echo "steamcmd cannot set a build live on the default branch; set it on the Steamworks builds page" >&2
    exit 2
fi

steamcmd="${STEAMCMD:-steamcmd}"
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
root="$repo/publish/steam"
cd "$repo"

settings="$repo/src/Game/appsettings.json"
app_id="$(jq -r '.Engine.Steam.AppId // 0' "$settings")"
name="$(jq -r '.Engine.Application.Name // "Itera"' "$settings")"
if [[ "$app_id" == 0 ]]; then
    echo "the game has no Steam App ID: set Engine:Steam:AppId in $settings" >&2
    exit 2
fi

if [[ -f "$repo/src/Game/steam.env" ]]; then
    # shellcheck source=/dev/null
    source "$repo/src/Game/steam.env"
fi
windows_depot="${STEAM_WINDOWS_DEPOT:?set STEAM_WINDOWS_DEPOT in src/Game/steam.env}"
linux_depot="${STEAM_LINUX_DEPOT:?set STEAM_LINUX_DEPOT in src/Game/steam.env}"
builder="${STEAM_BUILDER:?set STEAM_BUILDER in src/Game/steam.env}"

# Every build is a commit, so the log header's version (1.0.0+<commit>) names the build a tester ran. A preview uploads
# nothing, so it only warns.
if ! git diff --quiet HEAD || [[ -n "$(git status --porcelain -- src samples tools)" ]]; then
    if [[ "$preview" == 0 ]]; then
        echo "uncommitted changes; commit them first so the build matches a commit:" >&2
        git status --short >&2
        exit 1
    fi
    echo "warning: uncommitted changes (allowed for a preview)" >&2
fi
commit="$(git rev-parse --short HEAD)"
description="$name $commit $(date +%Y-%m-%d)"

mkdir -p "$root/output" "$root/scripts"
if [[ "$run_tests" == 1 ]]; then
    # Debug runs the extra checks (EcsChecks, Debug-only throws); Release is what ships, and some code differs there, so
    # both are tested before an upload.
    for configuration in Debug Release; do
        log="$root/output/test-$configuration.log"
        echo "Testing in $configuration (output in publish/steam/output/test-$configuration.log)..."
        if ! dotnet test -c "$configuration" > "$log" 2>&1; then
            tail -30 "$log" >&2
            echo "tests failed in $configuration; nothing uploaded" >&2
            exit 1
        fi
    done
fi

# Release: Debug turns on the Vulkan validation layers. Self-contained: testers need no .NET install. Composite
# ReadyToRun: the game, its packages, and .NET are compiled ahead of time as one unit, so less is compiled while
# the game starts and at first use (measured against the JIT alone: the ground at the start loaded in 0.5 s instead of
# 1.6 s, and no fixed steps were dropped at startup instead of 5; about 27 MB more per platform; the same speed once
# warm). Mods are still compiled by the JIT. Single file: the game, its packages, and .NET are one Game file, so the
# install folder holds that, the native libraries, the .pdb files, appsettings.json, Fonts/, Shaders/, and Textures/
# instead of about 270 files. Each update downloads most of the single file, which composite ReadyToRun costs anyway.
for rid in win-x64 linux-x64; do
    echo "Publishing $rid..."
    rm -rf "$root/content/$rid"
    if ! dotnet publish src/Game -c Release -r "$rid" --self-contained -o "$root/content/$rid" \
        -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true -p:PublishSingleFile=true \
        > "$root/output/publish-$rid.log" 2>&1; then
        tail -30 "$root/output/publish-$rid.log" >&2
        echo "publish failed for $rid (exit code 134 is the dotnet CLI crashing; run again); nothing uploaded" >&2
        exit 1
    fi
done

for file in win-x64/Game.exe win-x64/steam_api64.dll linux-x64/Game linux-x64/libsteam_api.so; do
    if [[ ! -f "$root/content/$file" ]]; then
        echo "publish/steam/content/$file is missing; nothing uploaded" >&2
        exit 1
    fi
done
if [[ ! -x "$root/content/linux-x64/Game" ]]; then
    echo "publish/steam/content/linux-x64/Game is not executable; nothing uploaded" >&2
    exit 1
fi

# steam_appid.txt: Steam gives a game it starts its App ID itself, and Valve says not to ship the file (the game writes
# it only when Steam did not start it). The .xml files are API docs. Each depot drops the other platforms' Steam
# libraries, which the engine copies next to the game for every platform.
cat > "$root/scripts/depot_windows.vdf" << EOF
"DepotBuild"
{
	"DepotID" "$windows_depot"
	"ContentRoot" "$root/content/win-x64/"
	"FileMapping"
	{
		"LocalPath" "*"
		"DepotPath" "."
		"Recursive" "1"
	}
	"FileExclusion" "steam_appid.txt"
	"FileExclusion" "*.xml"
	"FileExclusion" "appsettings.Development.json"
	"FileExclusion" "libSkiaSharp.pdb"
	"FileExclusion" "libsteam_api.so"
	"FileExclusion" "libsteam_api.dylib"
}
EOF
cat > "$root/scripts/depot_linux.vdf" << EOF
"DepotBuild"
{
	"DepotID" "$linux_depot"
	"ContentRoot" "$root/content/linux-x64/"
	"FileMapping"
	{
		"LocalPath" "*"
		"DepotPath" "."
		"Recursive" "1"
	}
	"FileExclusion" "steam_appid.txt"
	"FileExclusion" "*.xml"
	"FileExclusion" "appsettings.Development.json"
	"FileExclusion" "steam_api64.dll"
	"FileExclusion" "libsteam_api.dylib"
}
EOF
set_live=""
if [[ "$live" == 1 ]]; then
    set_live="\"SetLive\" \"$branch\""
fi
cat > "$root/scripts/app_build.vdf" << EOF
"AppBuild"
{
	"AppID" "$app_id"
	"Desc" "$description"
	"Preview" "$preview"
	$set_live
	"ContentRoot" "$root/content/"
	"BuildOutput" "$root/output/"
	"Depots"
	{
		"$windows_depot" "$root/scripts/depot_windows.vdf"
		"$linux_depot" "$root/scripts/depot_linux.vdf"
	}
}
EOF

echo "Uploading \"$description\" as $builder (output in publish/steam/output/steamcmd.log)..."
# No password: steamcmd uses the login it saved when the account logged in by hand. NoPromptForPassword makes an
# expired login fail instead of waiting for input.
set +e
"$steamcmd" +@ShutdownOnFailedCommand 1 +@NoPromptForPassword 1 +login "$builder" \
    +run_app_build "$root/scripts/app_build.vdf" +quit 2>&1 | tee "$root/output/steamcmd.log"
status=${PIPESTATUS[0]}
set -e

if [[ "$status" != 0 ]] || grep -q "ERROR\|FAILED" "$root/output/steamcmd.log"; then
    if grep -q -i "password\|Steam Guard\|License expired" "$root/output/steamcmd.log"; then
        echo "the saved login has expired: run '$steamcmd +login $builder' by hand once, then upload again" >&2
    fi
    echo "upload failed (steamcmd exit code $status)" >&2
    exit 1
fi

if [[ "$preview" == 1 ]]; then
    echo "Preview of \"$description\" done; nothing uploaded."
elif [[ "$live" == 1 ]]; then
    echo "Uploaded \"$description\", live on branch \"$branch\"."
else
    echo "Uploaded \"$description\"; not live on any branch (set it live on the Steamworks builds page)."
fi
