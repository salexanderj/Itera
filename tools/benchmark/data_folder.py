#!/usr/bin/env python3
"""The game's data folder (logs, benchmarks), named by Engine:Application:DataFolder in src/Game/appsettings.json.

Printed when run, for run.sh; with --agent, the folder of the runs Claude starts (tools/control/game.py and
validate.sh): the same name with "-agent" after it; with --settings FILE, from another program's appsettings.json
(a sample's).
Development tool, not game code.
"""
import json
import os
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
AGENT_SUFFIX = "-agent"


def data_folder_name(agent=False, settings_file=None):
    """The game's DataFolder (the engine's default, Itera, without the setting), with AGENT_SUFFIX for agent runs."""
    try:
        path = Path(settings_file) if settings_file else REPO / "src" / "Game" / "appsettings.json"
        settings = json.loads(path.read_text())
        name = settings["Engine"]["Application"]["DataFolder"] or "Itera"
    except (OSError, ValueError, KeyError, TypeError):
        name = "Itera"
    return name + AGENT_SUFFIX if agent else name


def data_folder(agent=False, settings_file=None):
    """~/.local/share/<DataFolder> (XDG_DATA_HOME if set); the engine's default name, Itera, without the setting."""
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / data_folder_name(
        agent, settings_file
    )


if __name__ == "__main__":
    settings_file = sys.argv[sys.argv.index("--settings") + 1] if "--settings" in sys.argv else None
    if "--name" in sys.argv:
        print(data_folder_name("--agent" in sys.argv, settings_file))
    else:
        print(data_folder("--agent" in sys.argv, settings_file))
