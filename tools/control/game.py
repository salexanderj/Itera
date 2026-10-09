#!/usr/bin/env python3
"""Drives the running game (src/Game) through its control port (Engine:Control, ControlService).

Starts the game from a copy of the build (so a build cannot change files the running game has loaded), sends console
commands, takes screenshots, reads the log, waits for the game, measures the GPU passes, reloads shaders, sends input,
and stops it. See README.md next to this file. Standard library only; a development tool, not game code.

Every command prints the reply's answers, failures, and log lines, then any fields of its own; the exit code is 0 when
the reply is ok, 1 when not, and 2 when the game cannot be reached.
"""

import argparse
import json
import os
import shutil
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_PORT = 47650

GAME = REPO / "src" / "Game"


def data_folder_name():
    """The game's Engine:Application:DataFolder from its appsettings.json (the engine's default, Itera, without it)."""
    try:
        settings = json.loads((GAME / "appsettings.json").read_text())
        return settings["Engine"]["Application"]["DataFolder"] or "Itera"
    except (OSError, ValueError, KeyError, TypeError):
        return "Itera"


def data_folder():
    """The game's data folder: .NET's LocalApplicationData, then the game's DataFolder."""
    name = data_folder_name()
    if os.name == "nt":
        return Path(os.environ["LOCALAPPDATA"]) / name
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / name
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / name


DATA = data_folder()
EXECUTABLE = "Game.exe" if os.name == "nt" else "Game"
RUN_ROOT = DATA / "Run"
STATE_FILE = RUN_ROOT / "state.json"
LOGS = DATA / "Logs"
SHADERS = REPO / "src" / "Itera.Engine" / "Rendering" / "Shaders"


def load_state():
    try:
        return json.loads(STATE_FILE.read_text())
    except (OSError, ValueError):
        return {}


def save_state(state):
    RUN_ROOT.mkdir(parents=True, exist_ok=True)
    STATE_FILE.write_text(json.dumps(state, indent=2))


def port_of(args):
    return args.port or load_state().get("port") or DEFAULT_PORT


def game_executable(state):
    """The game copy that state.json says was started, or None."""
    folder = state.get("folder")
    return Path(folder) / EXECUTABLE if folder else None


def alive(pid, state=None):
    """True while pid runs and, when the state names the started copy, is that game: after the game ends its pid may be
    handed to another program, which stop must never signal."""
    if not pid:
        return False
    expected = game_executable(state or {})
    if os.name == "nt":
        return alive_windows(pid, expected)
    try:
        os.kill(pid, 0)
    except OSError:
        return False
    # A zombie still answers kill 0; its state says so.
    try:
        with open(f"/proc/{pid}/stat") as stat:
            if stat.read().split(")")[-1].split()[0] == "Z":
                return False
    except OSError:
        return True
    if expected is None:
        return True
    try:
        running = os.readlink(f"/proc/{pid}/exe").removesuffix(" (deleted)")
    except OSError:
        return True
    return os.path.realpath(running) == os.path.realpath(expected)


def alive_windows(pid, expected):
    """alive() on Windows: the process runs (exit code STILL_ACTIVE) and its image is the started copy."""
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return False
    try:
        code = wintypes.DWORD()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != 259:  # STILL_ACTIVE
            return False
        if expected is None:
            return True
        size = wintypes.DWORD(32768)
        name = ctypes.create_unicode_buffer(size.value)
        if not kernel32.QueryFullProcessImageNameW(handle, 0, name, ctypes.byref(size)):
            return True
        return os.path.normcase(os.path.realpath(name.value)) == os.path.normcase(os.path.realpath(expected))
    finally:
        kernel32.CloseHandle(handle)


def stop_steps():
    """How stop ends the game after its quit command: signals and how long to wait for each. Windows has no SIGKILL;
    os.kill there ends the process at once (TerminateProcess)."""
    if os.name == "nt":
        return ((None, 10), (signal.SIGTERM, 5))
    return ((None, 10), (signal.SIGTERM, 10), (signal.SIGKILL, 5))


class Connection:
    def __init__(self, port, timeout):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.file = self.socket.makefile("rw", encoding="utf-8", newline="\n")
        self.next_id = 1

    def ask(self, request):
        request = dict(request, id=self.next_id)
        self.next_id += 1
        self.file.write(json.dumps(request) + "\n")
        self.file.flush()
        line = self.file.readline()
        if not line:
            raise ConnectionError("the game closed the connection")
        return json.loads(line)

    def close(self):
        self.socket.close()


def ask(args, request, timeout=None, connect_seconds=2):
    """
    Sends one request and returns its reply. Tries to connect for connect_seconds (a game still starting opens its port
    last); exits with 2 when the game cannot be reached.
    """
    wait = timeout if timeout is not None else request.get("timeout", 60) + 10
    deadline = time.time() + connect_seconds
    while True:
        try:
            connection = Connection(port_of(args), wait)
            break
        except OSError as error:
            if time.time() >= deadline:
                print(f"no game on port {port_of(args)}: {error}", file=sys.stderr)
                sys.exit(2)
            time.sleep(0.2)
    try:
        return connection.ask(request)
    except (OSError, ConnectionError) as error:
        print(f"lost the game: {error}", file=sys.stderr)
        sys.exit(2)
    finally:
        connection.close()


def show(reply, quiet_log=False, extra_skip=()):
    for line in reply.get("answers", []):
        print(line.rstrip())
    for line in reply.get("failures", []):
        print("FAILED: " + line.rstrip())
    if reply.get("error"):
        print("ERROR: " + reply["error"])
    if not quiet_log:
        for line in reply.get("log", []):
            print(line)
    skip = {"id", "ok", "answers", "failures", "log", "error", *extra_skip}
    fields = {k: v for k, v in reply.items() if k not in skip}
    if fields:
        print(json.dumps(fields, indent=1))
    return 0 if reply.get("ok") else 1


# ---- commands that talk to a running game ----


def cmd_run(args):
    reply = ask(args, {"op": "run", "line": " ".join(args.line)})
    return show(reply, quiet_log=args.quiet)


def cmd_status(args):
    return show(ask(args, {"op": "status"}), quiet_log=True)


def cmd_log(args):
    state = load_state()
    request = {"op": "log", "level": args.level}
    if args.new and "log_next" in state:
        request["from"] = state["log_next"]
    else:
        request["tail"] = args.tail
    reply = ask(args, request)
    for line in reply.get("lines", []):
        print(line)
    state["log_next"] = reply.get("next", state.get("log_next", 0))
    save_state(state)
    return 0 if reply.get("ok") else 1


def cmd_wait(args):
    request = {"op": "wait", "for": args.what, "timeout": args.timeout}
    if args.what in ("frames", "seconds"):
        request["n"] = float(args.value or 1)
    elif args.what == "log":
        request["pattern"] = args.value or ""
    # A game that is still starting opens its port last: keep trying for the whole timeout.
    return show(ask(args, request, connect_seconds=args.timeout), quiet_log=True)


def cmd_gpu(args):
    reply = ask(args, {"op": "gpu", "frames": args.frames, "timeout": args.timeout})
    if not reply.get("ok"):
        return show(reply)
    print(f"{reply['frames']} frames in {reply['seconds']} s ({reply['fps']} fps)")
    for name, times in reply["passes"].items():
        print(f"  {name:<18} avg {times['avg_ms']:8.3f} ms   max {times['max_ms']:8.3f} ms   ({times['count']})")
    return 0


def cmd_shot(args):
    request = {"op": "shot", "scale": args.scale, "ui": not args.no_ui}
    if args.path:
        request["path"] = str(Path(args.path).resolve())
    if args.crop:
        request["crop"] = [int(v) for v in args.crop.split(",")]
    reply = ask(args, request)
    if reply.get("ok"):
        print(reply["path"])
        print(f"{reply['width']}x{reply['height']} of a {reply['screen_width']}x{reply['screen_height']} screen")
        return 0
    return show(reply)


def cmd_reload(args):
    return show(ask(args, {"op": "reload", "timeout": args.timeout}))


def cmd_input(args):
    return show(ask(args, {"op": "input", "actions": args.actions, "timeout": args.timeout}))


# ---- starting and stopping ----


def copy_build(configuration):
    """Copies the build output into Run/<configuration>/, linking the large runtimes/ folder."""
    source = GAME / "bin" / configuration.capitalize() / "net10.0"
    if not (source / EXECUTABLE).exists():
        print(f"no build at {source}; run dotnet build" + (" -c Release" if configuration == "release" else ""),
              file=sys.stderr)
        sys.exit(2)
    target = RUN_ROOT / configuration
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True)
    for entry in source.iterdir():
        if entry.name == "runtimes":
            # Native libraries a build does not rewrite: linked, not copied (about 600 MB), where links are allowed.
            try:
                (target / "runtimes").symlink_to(entry, target_is_directory=True)
            except OSError:
                shutil.copytree(entry, target / "runtimes")
        elif entry.is_dir():
            shutil.copytree(entry, target / entry.name, symlinks=True)
        else:
            shutil.copy2(entry, target / entry.name)
    built = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime((source / "Game.dll").stat().st_mtime))
    return target, built


def place_window(pid, fullscreen):
    """Moves the game's window with sway: to HDMI-A-1, or full screen on DP-1. Nothing without sway."""
    if not shutil.which("swaymsg"):
        return "no sway: the window stays where the system put it"
    for _ in range(100):
        tree = subprocess.run(["swaymsg", "-t", "get_tree"], capture_output=True, text=True).stdout
        if f'"pid": {pid},' in tree:
            break
        time.sleep(0.1)
    else:
        return "the window did not appear within 10 s"
    if fullscreen:
        command = f"[pid={pid}] move container to output DP-1, focus, fullscreen enable"
    else:
        command = f"[pid={pid}] move container to output HDMI-A-1"
    subprocess.run(["swaymsg", command], capture_output=True)
    return "full screen on DP-1" if fullscreen else "on HDMI-A-1"


def cmd_start(args):
    state = load_state()
    if alive(state.get("pid"), state):
        print(f"a game already runs (pid {state['pid']}); 'game.py stop' first", file=sys.stderr)
        return 2
    configuration = "release" if args.release else "debug"
    folder, built = copy_build(configuration)
    port = args.port or DEFAULT_PORT
    command = [
        str(folder / EXECUTABLE),
        "--Engine:Control:Enabled=true",
        f"--Engine:Control:Port={port}",
        "--Engine:Steam:Enabled=false",
        f"--Engine:Renderer:ShaderSourceDirectory={SHADERS}",
    ]
    if not args.menu:
        # Straight into an offline match; --menu starts at the menu (game arguments after -- can still name a mode).
        command.append("--Engine:Session:Mode=Offline")
    if args.fullscreen:
        command.append("--Engine:Window:VSync=false")
    command += args.game
    console = open(RUN_ROOT / f"{configuration}-console.txt", "w")
    process = subprocess.Popen(command, cwd=folder, stdout=console, stderr=subprocess.STDOUT,
                               start_new_session=True)
    log = next(iter(sorted(LOGS.glob(f"*-{process.pid}.log"))), None)
    save_state({"pid": process.pid, "port": port, "configuration": configuration, "folder": str(folder),
                "built": built, "started": time.time(), "log_next": 0})
    print(f"started {configuration} build of {built}, pid {process.pid}, port {port}")
    placement = place_window(process.pid, args.fullscreen)
    print("window: " + placement)
    deadline = time.time() + 60
    while True:
        if process.poll() is not None:
            print(f"the game ended with exit code {process.returncode}; see {RUN_ROOT / (configuration + '-console.txt')}",
                  file=sys.stderr)
            return 2
        try:
            Connection(port, 5).close()
            break
        except OSError:
            if time.time() > deadline:
                print("the control port did not open within 60 s", file=sys.stderr)
                return 2
            time.sleep(0.2)
    log = next(iter(sorted(LOGS.glob(f"*-{process.pid}.log"))), None)
    if log:
        state = load_state()
        state["log"] = str(log)
        save_state(state)
        print(f"log: {log}")
    if args.no_wait or args.menu:
        return 0
    started = time.time()
    reply = ask(args, {"op": "wait", "for": "loaded", "timeout": 120})
    if reply.get("ok"):
        print(f"loaded after {time.time() - started:.1f} s more")
        return 0
    return show(reply)


def cmd_stop(args):
    state = load_state()
    pid = state.get("pid")
    if not alive(pid, state):
        print("no game runs")
        return 0
    try:
        ask(args, {"op": "run", "line": "quit"}, timeout=5)
    except SystemExit:
        pass
    for signal_number, wait in stop_steps():
        if signal_number is not None:
            os.kill(pid, signal_number)
        deadline = time.time() + wait
        while time.time() < deadline and alive(pid, state):
            time.sleep(0.1)
        if not alive(pid, state):
            how = "quit" if signal_number is None else signal.Signals(signal_number).name
            print(f"stopped pid {pid} ({how})")
            return 0
    print(f"pid {pid} would not stop", file=sys.stderr)
    return 1


def cmd_restart(args):
    code = cmd_stop(args)
    if code != 0:
        return code
    return cmd_start(args)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--port", type=int, help=f"control port (default: the started game's, else {DEFAULT_PORT})")
    commands = parser.add_subparsers(dest="command", required=True)

    def add(name, function, help_text):
        sub = commands.add_parser(name, help=help_text)
        sub.set_defaults(function=function)
        return sub

    for name, function in (("start", cmd_start), ("restart", cmd_restart)):
        sub = add(name, function, "copy the build and start the game" if name == "start" else "stop, then start")
        sub.add_argument("--release", action="store_true", help="the Release build (default Debug)")
        sub.add_argument("--fullscreen", action="store_true", help="full screen on DP-1 with VSync off (for timings)")
        sub.add_argument("--no-wait", action="store_true", help="do not wait for the loading screen to finish")
        sub.add_argument("--menu", action="store_true", help="start at the menu instead of an offline match (no wait)")
        sub.add_argument("game", nargs=argparse.REMAINDER, help="game arguments, after --")
    add("stop", cmd_stop, "quit the game (then SIGTERM, then SIGKILL)")
    sub = add("run", cmd_run, "run console commands (';' separates them)")
    sub.add_argument("-q", "--quiet", action="store_true", help="leave out the log lines")
    sub.add_argument("line", nargs="+")
    add("status", cmd_status, "frames, clock, loading, role, camera")
    sub = add("log", cmd_log, "log lines: the last few, or --new since the last 'log --new'")
    sub.add_argument("--level", default="information", help="least level (default information)")
    sub.add_argument("--tail", type=int, default=40)
    sub.add_argument("--new", action="store_true")
    sub = add("wait", cmd_wait, "wait for: loaded, frames N, seconds N, log PATTERN")
    sub.add_argument("what", choices=["loaded", "frames", "seconds", "log"])
    sub.add_argument("value", nargs="?")
    sub.add_argument("--timeout", type=float, default=60)
    sub = add("gpu", cmd_gpu, "each GPU pass's time over the next frames")
    sub.add_argument("--frames", type=int, default=240)
    sub.add_argument("--timeout", type=float, default=30)
    sub = add("shot", cmd_shot, "a screenshot; prints its path")
    sub.add_argument("path", nargs="?")
    sub.add_argument("--scale", type=float, default=0.5)
    sub.add_argument("--crop", help="x,y,width,height in screen pixels")
    sub.add_argument("--no-ui", action="store_true", help="before the UI is drawn")
    sub = add("reload", cmd_reload, "compile the shaders again from the repo and rebuild the pipelines")
    sub.add_argument("--timeout", type=float, default=60)
    sub = add("input", cmd_input, "input actions: down/up/tap KEY, hold KEY S, click left|right [S], look DX DY, scroll STEPS, point X Y, "
                                  "type TEXT, capture on|off, wait S")
    sub.add_argument("actions", nargs="+", help="actions as quoted strings, e.g. 'tap Escape' 'hold W 1'")
    sub.add_argument("--timeout", type=float, default=60)

    args = parser.parse_args()
    if getattr(args, "game", None) and args.game[0] == "--":
        args.game = args.game[1:]
    sys.exit(args.function(args))


if __name__ == "__main__":
    main()
