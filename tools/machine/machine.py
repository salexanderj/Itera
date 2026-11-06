#!/usr/bin/env python3
"""Shares this machine between the builds, tests, game runs, and benchmarks that sessions and people start.

One lock, machine-wide, for every checkout of Itera and of the games made from it. Three kinds of hold:

- shared: a build, a filtered test run, a game run. Any number at once.
- suite: a whole test suite. Shared, and one suite at a time.
- alone: a benchmark, a timing run, or the user's "busy". Nothing else at once.

A request to hold the machine alone goes first: for its first 5 minutes of waiting (PRIORITY_SECONDS), new shared
holds wait behind it. After that they go ahead again, so a holder that never ends (a game left running) holds up the
request alone, not every session's builds. Each hold records who
holds it (its kind, label, folder, and process), so a refusal or a wait names the holders.

    machine.py run [--shared|--suite|--alone] [--label TEXT] [--wait S] -- COMMAND...   # COMMAND under the lock
    machine.py hold [--shared|--suite|--alone] --watch PID [--label TEXT] [--wait S]   # held until process PID ends
    machine.py busy [--hours H] [LABEL...]   # the user's: hold the machine alone until 'free' or H hours
    machine.py free                          # end every busy hold
    machine.py status [--line]               # who holds the machine, and who waits

The process that holds the lock keeps its file open where no child inherits it, so a build server left behind by a
build never holds the machine. Commands under a hold get ITERA_MACHINE (the kind) in their environment: a script that
locks for itself does nothing under an alone hold, and refuses to hold alone under a shared one. When other holds
share the machine, a shared hold gives its command ITERA_TEST_TIME_SCALE=2 (unless set), so the tests' wait limits
stretch (tests/Shared/TestTime.cs). hold prints the ITERA_TEST_TIME_SCALE its command gets (the caller's own when set),
for the shell to export.

Exit codes: a command's own; 75 when the machine stayed held past the wait; 2 for a usage error. Without fcntl
(Windows) every hold is granted at once. Standard library only; a development tool, not game code.
"""

import argparse
import json
import os
import signal
import subprocess
import sys
import tempfile
import time
from pathlib import Path

try:
    import fcntl
except ImportError:  # Windows: no lock, every hold is granted at once.
    fcntl = None

MARKER = "ITERA_MACHINE"
FOLDER_VARIABLE = "ITERA_MACHINE_DIR"
SCALE_VARIABLE = "ITERA_TEST_TIME_SCALE"
SHARED_SCALE = "2"
KINDS = ("shared", "suite", "alone")
BUSY = "busy: "
HELD = 75
PRIORITY_SECONDS = 300
GAME_RUN = "game.py start"
POLL_SECONDS = 0.25
REPORT_SECONDS = 60


class Busy(Exception):
    """The machine stayed held past the wait, or the hold cannot be granted here."""


def folder():
    """The lock's folder: ITERA_MACHINE_DIR, else the user's runtime folder, else the temporary folder."""
    named = os.environ.get(FOLDER_VARIABLE)
    if named:
        path = Path(named)
    elif os.environ.get("XDG_RUNTIME_DIR"):
        path = Path(os.environ["XDG_RUNTIME_DIR"]) / "itera-machine"
    else:
        path = Path(tempfile.gettempdir()) / f"itera-machine-{os.getuid()}"
    for part in ("holders", "pending"):
        (path / part).mkdir(parents=True, exist_ok=True)
    return path


def alive(pid):
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def entries(part, base=None):
    """The live entries of 'holders' or 'pending'. An entry whose process has ended is removed."""
    base = base or folder()
    found = []
    for path in sorted((base / part).glob("*.json")):
        try:
            entry = json.loads(path.read_text())
        except (OSError, ValueError):
            continue
        if alive(entry.get("pid", 0)):
            entry["path"] = path
            found.append(entry)
        else:
            path.unlink(missing_ok=True)
    return found


def write_entry(part, pid, kind, label, base):
    path = base / part / f"{pid}-{kind}-{time.time_ns()}.json"
    path.write_text(json.dumps({"pid": pid, "kind": kind, "label": label, "folder": os.getcwd(), "since": time.time()}))
    return path


def describe(entry):
    minutes = (time.time() - entry.get("since", time.time())) / 60
    text = f"{entry['kind']} '{entry['label']}' (pid {entry['pid']}, {entry['folder']}, {minutes:.0f} min)"
    if entry["label"].startswith(GAME_RUN):
        text += f": a game still running; 'tools/control/game.py stop' in {entry['folder']} ends it"
    return text


def first_in_line(base):
    """The requests to hold alone that new shared holds wait behind: those waiting less than PRIORITY_SECONDS."""
    now = time.time()
    return [e for e in entries("pending", base) if now - e.get("since", now) < PRIORITY_SECONDS]


def holders_text(base):
    holding = [describe(e) for e in entries("holders", base)]
    waiting = [describe(e) for e in entries("pending", base)]
    parts = []
    if holding:
        parts.append("held by " + "; ".join(holding))
    if waiting:
        parts.append("waiting to hold it alone: " + "; ".join(waiting))
    return ", ".join(parts) or "no holder recorded"


class Held:
    """A granted hold: its open lock files and its entry. release() ends it; ending the process ends it too."""

    def __init__(self, fds=(), paths=(), kind=None, label=None):
        self.fds = list(fds)
        self.paths = list(paths)
        self.kind = kind
        self.label = label

    def give(self, pid):
        """Hands the hold to process PID, started with the lock files open (pass_fds); this process keeps none of it."""
        if self.fds:
            base = folder()
            for path in self.paths:
                path.unlink(missing_ok=True)
            self.paths = [write_entry("holders", pid, self.kind, self.label, base)]
        for fd in self.fds:
            os.close(fd)
        self.fds = []

    def release(self):
        for path in self.paths:
            path.unlink(missing_ok=True)
        for fd in self.fds:
            os.close(fd)
        self.fds, self.paths = [], []


def take(path, mode, deadline, base, report, ready):
    """Locks PATH in MODE once READY() is true, by the deadline, telling REPORT what it waits for now and then."""
    fd = os.open(path, os.O_RDWR | os.O_CREAT, 0o600)
    told = None
    while True:
        if ready():
            try:
                fcntl.flock(fd, mode | fcntl.LOCK_NB)
                return fd
            except BlockingIOError:
                pass
        now = time.monotonic()
        if now >= deadline:
            os.close(fd)
            raise Busy("the machine is busy: " + holders_text(base))
        if report and (told is None or now - told >= REPORT_SECONDS):
            report("waiting for the machine: " + holders_text(base))
            told = now
        time.sleep(POLL_SECONDS)


def acquire(kind, label, wait, holder=None, report=None):
    """Holds the machine as KIND for process HOLDER (this one by default), waiting at most WAIT seconds."""
    if kind not in KINDS:
        raise ValueError(kind)
    outer = os.environ.get(MARKER)
    if fcntl is None or outer == "alone":
        return Held()
    if kind == "alone" and outer:
        raise Busy(
            f"this command already holds the machine ({outer}), so it cannot hold it alone; "
            "run the benchmark or timing run in a command of its own"
        )
    base = folder()
    holder = holder or os.getpid()
    deadline = time.monotonic() + wait
    fds = []
    pending = write_entry("pending", holder, kind, label, base) if kind == "alone" else None
    try:
        if kind == "suite":
            fds.append(take(base / "suite", fcntl.LOCK_EX, deadline, base, report, lambda: True))
        if kind == "alone" or outer:
            ready = lambda: True  # noqa: E731
        else:
            # A shared hold waits behind a request to hold alone; one already inside a hold does not, or it would wait
            # for itself.
            ready = lambda: not first_in_line(base)  # noqa: E731
        mode = fcntl.LOCK_EX if kind == "alone" else fcntl.LOCK_SH
        fds.append(take(base / "lock", mode, deadline, base, report, ready))
        return Held(fds, [write_entry("holders", holder, kind, label, base)], kind, label)
    except BaseException:
        for fd in fds:
            os.close(fd)
        raise
    finally:
        if pending:
            pending.unlink(missing_ok=True)


def environment(held):
    """The environment for a hold's command: ITERA_MACHINE, and ITERA_TEST_TIME_SCALE when the machine is shared."""
    result = dict(os.environ)
    if held.kind:
        result.setdefault(MARKER, held.kind)
    scale = shared_scale(held) if held.kind != "alone" else ""
    if scale and not result.get(SCALE_VARIABLE):
        result[SCALE_VARIABLE] = scale
    return result


def command_scale(scale):
    """The ITERA_TEST_TIME_SCALE a hold's command gets: the caller's own when it has one, else SCALE (maybe none)."""
    return os.environ.get(SCALE_VARIABLE) or scale


def shared_scale(held):
    """ITERA_TEST_TIME_SCALE for a shared hold's command: 2 when other holds share the machine, else none."""
    if fcntl is None or not held.paths:
        return ""
    mine = set(held.paths)
    others = [e for e in entries("holders") if e["path"] not in mine]
    return SHARED_SCALE if others else ""


def say(text):
    print(f"machine: {text}", file=sys.stderr, flush=True)


def holds_lock_in_script(kind, label, wait=3600.0):
    """For a script that locks for itself: the hold, or None after saying why it was refused."""
    try:
        return acquire(kind, label, wait, report=say)
    except Busy as refusal:
        say(str(refusal))
        return None


def cmd_run(args):
    if not args.command:
        say("run needs a command after --")
        return 2
    label = args.label or " ".join(args.command)[:80]
    try:
        held = acquire(args.kind, label, args.wait, report=say)
    except Busy as refusal:
        say(str(refusal))
        return HELD
    process = subprocess.Popen(args.command, env=environment(held) if held.kind else dict(os.environ))

    def stop(signum, _frame):
        process.send_signal(signum)

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        return process.wait()
    finally:
        held.release()


def daemon_hold(kind, label, wait, watch, until, answer):
    """In a detached process: holds the machine until process WATCH ends or the time UNTIL, then exits."""
    held = Held()

    def end(_signum, _frame):
        held.release()
        os._exit(0)

    signal.signal(signal.SIGTERM, end)
    signal.signal(signal.SIGHUP, signal.SIG_IGN)

    def report(text):
        answer(f"wait {text}")

    try:
        held = acquire(kind, label, wait, holder=os.getpid(), report=report)
    except Busy as refusal:
        answer(f"fail {refusal}")
        os._exit(0)
    answer(f"ok {shared_scale(held) if kind != 'alone' else ''}")
    try:
        while (watch is None or alive(watch)) and (until is None or time.time() < until):
            time.sleep(0.5)
    finally:
        held.release()
    os._exit(0)


def detached_hold(kind, label, wait, watch, until):
    """Starts a detached holder and waits for its answer. Returns (granted, scale or refusal)."""
    reader, writer = os.pipe()
    first = os.fork()
    if first == 0:
        os.close(reader)
        os.setsid()
        if os.fork() != 0:
            os._exit(0)
        # The holder must not keep the caller's output open, or the caller's reader waits for the holder to end.
        null = os.open(os.devnull, os.O_RDWR)
        for fd in (0, 1, 2):
            os.dup2(null, fd)
        pipe = os.fdopen(writer, "w", buffering=1)

        def answer(line):
            try:
                pipe.write(line.replace("\n", " ") + "\n")
                if not line.startswith("wait "):
                    pipe.close()
            except (OSError, ValueError):
                pass

        try:
            daemon_hold(kind, label, wait, watch, until, answer)
        finally:
            os._exit(1)
    os.close(writer)
    os.waitpid(first, 0)
    with os.fdopen(reader) as pipe:
        for line in pipe:
            word, _, rest = line.rstrip("\n").partition(" ")
            if word == "wait":
                say(rest)
            elif word == "ok":
                return True, rest
            elif word == "fail":
                return False, rest
    return False, "the holder ended without an answer"


def cmd_hold(args):
    if fcntl is None or os.environ.get(MARKER) == "alone":
        print(command_scale(""))
        return 0
    if not alive(args.watch):
        say(f"process {args.watch} is not running")
        return 2
    granted, text = detached_hold(args.kind, args.label or f"process {args.watch}", args.wait, args.watch, None)
    if not granted:
        say(text)
        return HELD
    print(command_scale(text))
    return 0


def cmd_busy(args):
    if fcntl is None:
        say("no lock on this platform")
        return 2
    label = BUSY + (" ".join(args.label) or "the user")
    until = time.time() + args.hours * 3600
    say("holding the machine alone; builds and tests running now finish first")
    granted, text = detached_hold("alone", label, args.hours * 3600, None, until)
    if not granted:
        say(text)
        return HELD
    say(f"the machine is yours until 'machine.py free' or {args.hours:g} h")
    return 0


def cmd_free(_args):
    ended = 0
    for part in ("holders", "pending"):
        for entry in entries(part):
            if entry["label"].startswith(BUSY):
                try:
                    os.kill(entry["pid"], signal.SIGTERM)
                    ended += 1
                except ProcessLookupError:
                    pass
                entry["path"].unlink(missing_ok=True)
    say(f"ended {ended} busy hold(s)" if ended else "no busy hold")
    return 0


def status_line(base):
    holding = entries("holders", base)
    waiting = entries("pending", base)
    alone = [e for e in holding if e["kind"] == "alone"]
    if alone:
        text = f"machine alone: {alone[0]['label']}"
    elif holding:
        suites = sum(1 for e in holding if e["kind"] == "suite")
        text = f"machine shared by {len(holding)}" + (" (suite)" if suites else "")
    else:
        text = "machine free"
    if waiting:
        text += f"; waiting: {waiting[0]['label']}"
    return text


def cmd_status(args):
    if fcntl is None:
        print("no lock on this platform")
        return 0
    base = folder()
    if args.line:
        print(status_line(base))
        return 0
    holding = entries("holders", base)
    waiting = entries("pending", base)
    print("free" if not holding else "held:")
    for entry in holding:
        print("  " + describe(entry))
    if waiting:
        print("waiting to hold it alone:")
        for entry in waiting:
            print("  " + describe(entry))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="name", required=True)

    def kinds(sub, alone):
        group = sub.add_mutually_exclusive_group()
        group.add_argument("--shared", dest="kind", action="store_const", const="shared", help="a build, test, or game")
        group.add_argument("--suite", dest="kind", action="store_const", const="suite", help="a whole test suite")
        if alone:
            group.add_argument("--alone", dest="kind", action="store_const", const="alone", help="nothing else at once")
        sub.set_defaults(kind="shared")
        sub.add_argument("--label", help="who holds it, as status shows it")
        sub.add_argument("--wait", type=float, default=3600, help="seconds to wait for the machine (default 3600)")

    run = commands.add_parser("run", help="run a command under the lock")
    kinds(run, alone=True)
    run.add_argument("command", nargs=argparse.REMAINDER, help="the command, after --")
    run.set_defaults(function=cmd_run)
    hold = commands.add_parser("hold", help="hold the machine until a process ends (for a shell's commands)")
    kinds(hold, alone=True)
    hold.add_argument("--watch", type=int, required=True, help="the process whose end releases the hold")
    hold.set_defaults(function=cmd_hold)
    busy = commands.add_parser("busy", help="hold the machine alone, for the user")
    busy.add_argument("--hours", type=float, default=4, help="the hold ends after this (default 4)")
    busy.add_argument("label", nargs="*", help="what the machine is busy with")
    busy.set_defaults(function=cmd_busy)
    free = commands.add_parser("free", help="end every busy hold")
    free.set_defaults(function=cmd_free)
    status = commands.add_parser("status", help="who holds the machine")
    status.add_argument("--line", action="store_true", help="one short line, for a status line")
    status.set_defaults(function=cmd_status)

    args = parser.parse_args(argv)
    if getattr(args, "command", None) and args.command[:1] == ["--"]:
        args.command = args.command[1:]
    return args.function(args)


if __name__ == "__main__":
    sys.exit(main())
