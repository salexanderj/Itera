#!/usr/bin/env python3
"""Starts a published build of the game with no graphics, plays a short offline match, and checks it ended cleanly.

It runs what a player gets: the published folder's own executable, its ReadyToRun code, its packages, and its native
libraries. It passes when the run ends with exit code 0 and its log holds no Error or Fatal line, and, with
--steam-library, when Steam's library loaded: Steam is turned on with Valve's test app (480), so with no Steam client
the start stops at the client, after both of Steam's libraries have loaded.

    python3 tools/tests/published.py publish/linux-x64 --data /tmp/published-data --steam-library
"""

import argparse
import glob
import os
import shutil
import subprocess
import sys
import time

SCRIPT = "wait loaded; wait ticks 300; quit"
NO_STEAM_LIBRARY = "no Steam library for this platform"
SHOWN = ("Match 1:", "Loading: done", "Steam:", "Audio:", "Shutdown complete")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folder", help="the published folder, which holds Game or Game.exe")
    parser.add_argument("--data", required=True, help="a data folder for the run, emptied first (its Logs/ is read)")
    parser.add_argument("--seconds", type=float, default=300, help="how long the run may take (default 300)")
    parser.add_argument("--steam-library", action="store_true", help="fail when the log says Steam has no library here")
    arguments = parser.parse_args()

    executable = os.path.join(arguments.folder, "Game.exe" if os.name == "nt" else "Game")
    if not os.path.isfile(executable):
        print(f"No executable at {executable}.")
        return 1

    data = os.path.abspath(arguments.data)
    shutil.rmtree(data, ignore_errors=True)
    command = [
        executable,
        "--Engine:Session:Mode=Offline",
        "--Engine:Window:Mode=None",
        f"--Engine:Application:DataFolder={data}",
        "--Engine:Audio:Volume=0.01",
        "--Engine:Steam:Enabled=true",
        "--Engine:Steam:AppId=480",
        "--Engine:Console:ExitOnFailure=true",
        f"--Engine:Console:Run={SCRIPT}",
    ]
    print("Running: " + " ".join(command), flush=True)
    began = time.monotonic()
    try:
        code = subprocess.run(command, cwd=arguments.folder, timeout=arguments.seconds).returncode
    except subprocess.TimeoutExpired:
        print(f"The run did not end within {arguments.seconds:.0f} s.")
        code = None
    print(f"Exit code {code} after {time.monotonic() - began:.0f} s.")

    logs = sorted(glob.glob(os.path.join(data, "Logs", "*.log")), key=os.path.getmtime)
    if not logs:
        print(f"No log in {os.path.join(data, 'Logs')}.")
        return 1
    with open(logs[-1], encoding="utf-8", errors="replace") as file:
        lines = [line.rstrip() for line in file]
    print(f"Log: {logs[-1]} ({len(lines)} lines)")
    for line in lines:
        if any(text in line for text in SHOWN):
            print("  " + line)

    problems = [line for line in lines if "[ERR]" in line or "[FTL]" in line]
    warnings = sum(1 for line in lines if "[WRN]" in line)
    print(f"{len(problems)} Error or Fatal line(s), {warnings} Warning(s).")
    for line in problems:
        print("  " + line)

    failed = code != 0 or bool(problems)
    if arguments.steam_library and any(NO_STEAM_LIBRARY in line for line in lines):
        print("Steam's library did not load, on a platform that has one.")
        failed = True
    print("FAILED" if failed else "Passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
