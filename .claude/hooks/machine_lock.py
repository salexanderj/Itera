#!/usr/bin/env python3
"""PreToolUse hook on Bash: run a session's builds, tests, and game runs under the machine lock (tools/machine).

A command that runs dotnet build, test, run, watch, publish, pack, or msbuild, or a built Game, gets a first line that
holds the machine until the command's shell ends (machine.py hold --watch $$), and the command runs as it was. The
hold is shared; a dotnet test with no --filter holds it as a suite; a command that also runs the benchmark
(tools/benchmark/run.sh) or timings.py --alone holds it alone. The changed command goes through the usual permission
checks: the hook never approves a call. The hook chooses the command's ITERA_TEST_TIME_SCALE and writes it as a
literal, so a session isolated in a worktree can run the changed command.

When the machine is held alone, or a request to hold it alone waits, a call in the foreground is refused with who
holds it, and the session runs it again in the background, where it waits for the machine. Commands that run only the
scripts that lock for themselves (machine.py, run.sh, timings.py, game.py, validate.sh) are left alone.

ITERA_MACHINE_LOCK=off in Claude Code's environment turns the hook off. Any fault lets the call run unchanged.
"""

import json
import os
import re
import shlex
import sys
from pathlib import Path

MACHINE = Path(__file__).resolve().parents[2] / "tools" / "machine" / "machine.py"
SWITCH = "ITERA_MACHINE_LOCK"
MARKER = "ITERA_MACHINE"
SCALE = "ITERA_TEST_TIME_SCALE"
FOREGROUND_WAIT = 90
BACKGROUND_WAIT = 3600

DOTNET = re.compile(r"\bdotnet\s+(?:build(?![-\w])|test\b|run\b|watch\b|publish\b|pack\b|msbuild\b)")
GAME = re.compile(r"(?m)(?:^|[;&|(])\s*(?:[A-Z_][A-Z0-9_]*=\S*\s+)*\S*bin/(?:Debug|Release)/net[\d.]+/Game\b")
TEST = re.compile(r"\bdotnet\s+test\b([^;&|\n]*)")
ALONE = re.compile(r"tools/benchmark/run\.sh|timings\.py\b[^;&|\n]*--alone")
HANDLED = re.compile(r"\bmachine\.py\b|\bITERA_MACHINE=")


def kind_of(command):
    """The hold a command needs (shared, suite, or alone), or None when it needs none from the hook."""
    if HANDLED.search(command) or not (DOTNET.search(command) or GAME.search(command)):
        return None
    if ALONE.search(command):
        return "alone"
    if any("--filter" not in match.group(1) for match in TEST.finditer(command)):
        return "suite"
    return "shared"


def label_of(event, command):
    folder = Path(event.get("cwd") or os.getcwd()).name
    session = (event.get("session_id") or "")[:8]
    first = command.strip().splitlines()[0] if command.strip() else ""
    return f"{folder} {session}: {first[:70]}".strip()


def wrapped(command, kind, label, wait, scale=""):
    """COMMAND after a line that holds the machine, and the exports of its kind and, when given, its time scale."""
    hold = (
        f"python3 {shlex.quote(str(MACHINE))} hold --{kind} --watch $$ --wait {wait} --label {shlex.quote(label)}"
    )
    # Every export is a statement of its own with a literal value. In a session isolated in a worktree, Claude Code
    # refuses an export whose value it cannot read: one inside a condition, a ${NAME:-...}, or a program's output.
    lines = [f"{hold} >/dev/null || exit $?", f"export {MARKER}={kind}"]
    if scale:
        lines.append(f"export {SCALE}={scale}")
    return "\n".join([*lines, command])


def machine_module():
    sys.path.insert(0, str(MACHINE.parent))
    import machine  # noqa: PLC0415

    return machine


def scale_of(kind):
    """The time scale a command of KIND gets: 2 while other shared holds use the machine, else none.

    It is chosen here, as the command is changed, so the export is a literal. The command's own hold comes after, so
    every shared hold counts. A scale already in Claude Code's environment is kept: no export replaces it.
    """
    machine = machine_module()
    if kind == "alone" or machine.fcntl is None or os.environ.get(SCALE):
        return ""
    others = [e for e in machine.entries("holders", machine.folder()) if e["kind"] != "alone"]
    return machine.SHARED_SCALE if others else ""


def taken(kind):
    """Why a call in the foreground would wait for the machine, or None when it would not."""
    machine = machine_module()
    if machine.fcntl is None:
        return None
    base = machine.folder()
    holding = machine.entries("holders", base)
    waiting = machine.first_in_line(base)
    if waiting or any(e["kind"] == "alone" for e in holding):
        return machine.holders_text(base)
    if kind == "suite" and any(e["kind"] == "suite" for e in holding):
        return machine.holders_text(base)
    if kind == "alone" and holding:
        return machine.holders_text(base)
    return None


def decide(event):
    if os.environ.get(SWITCH, "").lower() == "off" or event.get("tool_name") != "Bash":
        return None
    tool_input = event.get("tool_input") or {}
    command = tool_input.get("command") or ""
    kind = kind_of(command)
    if kind is None:
        return None
    background = bool(tool_input.get("run_in_background"))
    if not background:
        reason = taken(kind)
        if reason:
            return {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": (
                        f"The machine is busy ({reason}). Run the same command again with run_in_background: it "
                        "waits for the machine, and its completion notice comes when it has run. "
                        "`tools/machine/machine.py status` shows who holds it."
                    ),
                }
            }
    wait = BACKGROUND_WAIT if background else FOREGROUND_WAIT
    changed = dict(tool_input)
    changed["command"] = wrapped(command, kind, label_of(event, command), wait, scale_of(kind))
    return {"hookSpecificOutput": {"hookEventName": "PreToolUse", "updatedInput": changed}}


def main():
    try:
        decision = decide(json.load(sys.stdin))
    except Exception:  # noqa: BLE001 - a fault in the hook never blocks a call
        return 0
    if decision:
        print(json.dumps(decision))
    return 0


if __name__ == "__main__":
    sys.exit(main())
