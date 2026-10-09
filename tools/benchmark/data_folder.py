#!/usr/bin/env python3
"""The game's data folder (logs, benchmarks), named by Engine:Application:DataFolder in src/Game/appsettings.json.

Printed when run, for run.sh. Development tool, not game code.
"""
import json
import os
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


def data_folder():
    """~/.local/share/<DataFolder> (XDG_DATA_HOME if set); the engine's default name, Itera, without the setting."""
    try:
        settings = json.loads((REPO / "src" / "Game" / "appsettings.json").read_text())
        name = settings["Engine"]["Application"]["DataFolder"] or "Itera"
    except (OSError, ValueError, KeyError, TypeError):
        name = "Itera"
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / name


if __name__ == "__main__":
    print(data_folder())
