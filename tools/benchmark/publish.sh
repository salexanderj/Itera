#!/usr/bin/env bash
# Publishes the game in src/Game for Linux as players get it, into a folder for run.sh's BENCH_BIN: Release,
# self-contained, composite ReadyToRun, and one file, as tools/steam/upload.sh publishes it. A plain `dotnet build`
# compiles each method the first time it runs, and tiered PGO compiles a hot one again, so a path that first runs in the
# middle of a run (a host's first sends to a client, the first blast) is several times slower for a second or two. This
# build compiles most of it ahead.
#
# Usage: publish.sh <folder>     for example: publish.sh /tmp/game-shipped; BENCH_BIN=/tmp/game-shipped run.sh shipped-1
# A folder that holds anything but an earlier publish (its Game file) is refused; an earlier publish is replaced.
# tools/steam/upload.sh and CI's publish check (.github/actions/publish-check) publish with these flags: change the three
# together. Development tool, not game code.
set -euo pipefail

if [[ $# -ne 1 || -z "$1" ]]; then
    echo "usage: publish.sh <folder>" >&2
    exit 2
fi

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
# Absolute, as the publish runs from the repository.
out="$(realpath -m "$1")"
if [[ -d "$out" && -n "$(ls -A "$out")" && ! -f "$out/Game" ]]; then
    echo "publish.sh: $out holds files but no earlier publish (no Game file); give an empty or new folder" >&2
    exit 2
fi

# A changed crossgen argument does not make an incremental publish compile the composite image again, so it is always
# made afresh.
rm -rf "$repo/src/Game/obj/Release/net10.0/linux-x64/R2R"
if [[ -d "$out" ]]; then
    rm -rf "$out"
fi

cd "$repo"
dotnet publish src/Game -c Release -r linux-x64 --self-contained -o "$out" \
    -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true -p:PublishSingleFile=true
