#!/usr/bin/env python3
"""Tests of machine.py and the hook that uses it (.claude/hooks/machine_lock.py), in a lock folder of their own.

    python3 -m unittest discover -s tools/machine
"""

import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[1] / ".claude" / "hooks"))

import machine  # noqa: E402
import machine_lock  # noqa: E402

MACHINE = str(HERE / "machine.py")


@unittest.skipIf(machine.fcntl is None, "no lock on this platform")
class MachineTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.saved = {name: os.environ.get(name) for name in (machine.FOLDER_VARIABLE, machine.MARKER)}
        os.environ[machine.FOLDER_VARIABLE] = self.folder.name
        os.environ.pop(machine.MARKER, None)
        self.processes = []

    def tearDown(self):
        for process in self.processes:
            if process.poll() is None:
                process.kill()
            process.wait()
        for name, value in self.saved.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value
        self.folder.cleanup()

    def start(self, *arguments):
        process = subprocess.Popen([sys.executable, MACHINE, *arguments], stderr=subprocess.DEVNULL)
        self.processes.append(process)
        return process

    def until(self, condition, seconds=5):
        deadline = time.monotonic() + seconds
        while not condition():
            self.assertLess(time.monotonic(), deadline, "the condition never came true")
            time.sleep(0.05)

    def holders(self, kind=None):
        return [e for e in machine.entries("holders") if kind is None or e["kind"] == kind]

    def test_shared_holds_share_the_machine(self):
        first = machine.acquire("shared", "first", 1)
        second = machine.acquire("shared", "second", 1)
        self.assertEqual(2, len(self.holders("shared")))
        first.release()
        second.release()
        self.assertEqual([], self.holders())

    def test_a_hold_alone_waits_for_the_shared_holds(self):
        shared = machine.acquire("shared", "build", 1)
        with self.assertRaises(machine.Busy) as refusal:
            machine.acquire("alone", "benchmark", 0.3)
        self.assertIn("build", str(refusal.exception))
        shared.release()
        machine.acquire("alone", "benchmark", 1).release()

    def test_a_shared_hold_waits_behind_a_request_to_hold_alone(self):
        shared = machine.acquire("shared", "build", 1)
        waiting = self.start("run", "--alone", "--label", "benchmark", "--", "sleep", "0")
        self.until(lambda: machine.entries("pending"))
        with self.assertRaises(machine.Busy) as refusal:
            machine.acquire("shared", "late build", 0.3)
        self.assertIn("benchmark", str(refusal.exception))
        shared.release()
        self.assertEqual(0, waiting.wait(5))

    def test_a_request_of_an_ended_process_is_not_waited_for(self):
        ended = subprocess.Popen(["true"])
        ended.wait()
        machine.write_entry("pending", ended.pid, "alone", "gone", machine.folder())
        machine.acquire("shared", "build", 0.5).release()

    def test_a_request_waiting_past_its_turn_no_longer_holds_up_shared_holds(self):
        waiting = subprocess.Popen(["sleep", "5"])
        self.processes.append(waiting)
        path = machine.write_entry("pending", waiting.pid, "alone", "benchmark", machine.folder())
        with self.assertRaises(machine.Busy):
            machine.acquire("shared", "build", 0.2)
        entry = json.loads(path.read_text())
        entry["since"] -= machine.PRIORITY_SECONDS
        path.write_text(json.dumps(entry))
        machine.acquire("shared", "build", 0.5).release()

    def test_a_game_run_is_named_with_how_to_stop_it(self):
        held = machine.acquire("shared", "game.py start, debug, port 47650", 1)
        self.assertIn("game.py stop", machine.holders_text(machine.folder()))
        held.release()

    def test_a_command_under_a_shared_hold_cannot_hold_alone(self):
        os.environ[machine.MARKER] = "shared"
        with self.assertRaises(machine.Busy):
            machine.acquire("alone", "benchmark", 0.1)

    def test_under_a_hold_alone_every_hold_is_granted_at_once(self):
        alone = machine.acquire("alone", "benchmark", 1)
        os.environ[machine.MARKER] = "alone"
        machine.acquire("shared", "its own build", 0.1).release()
        alone.release()

    def test_one_suite_at_a_time(self):
        suite = machine.acquire("suite", "suite", 1)
        machine.acquire("shared", "build", 0.3).release()
        with self.assertRaises(machine.Busy):
            machine.acquire("suite", "second suite", 0.3)
        suite.release()

    def test_a_hold_watching_a_process_ends_with_it(self):
        watched = subprocess.Popen(["sleep", "0.6"])
        self.processes.append(watched)
        answer = subprocess.run(
            [sys.executable, MACHINE, "hold", "--watch", str(watched.pid), "--label", "shell"],
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertEqual(0, answer.returncode, answer.stderr)
        self.assertEqual(1, len(self.holders("shared")))
        watched.wait()
        self.until(lambda: not self.holders())

    def test_a_shared_hold_beside_another_stretches_the_test_limits(self):
        first = machine.acquire("shared", "first", 1)
        second = machine.acquire("shared", "second", 1)
        self.assertEqual(machine.SHARED_SCALE, machine.environment(second).get(machine.SCALE_VARIABLE))
        first.release()
        second.release()

    def test_the_hook_stretches_the_test_limits_while_others_share_the_machine(self):
        saved = os.environ.pop(machine.SCALE_VARIABLE, None)
        try:
            self.assertEqual("", machine_lock.scale_of("shared"))
            other = machine.acquire("shared", "other", 1)
            self.assertEqual(machine.SHARED_SCALE, machine_lock.scale_of("suite"))
            self.assertEqual("", machine_lock.scale_of("alone"))
            os.environ[machine.SCALE_VARIABLE] = "5"
            self.assertEqual("", machine_lock.scale_of("shared"))
            other.release()
        finally:
            os.environ.pop(machine.SCALE_VARIABLE, None)
            if saved is not None:
                os.environ[machine.SCALE_VARIABLE] = saved

    def test_a_wrapped_command_holds_the_machine_with_its_kind_and_scale(self):
        command = machine_lock.wrapped('echo "$ITERA_MACHINE $ITERA_TEST_TIME_SCALE"', "shared", "test", 5, "2")
        answer = subprocess.run(["bash", "-c", command], capture_output=True, text=True, timeout=10)
        self.assertEqual((0, "shared 2"), (answer.returncode, answer.stdout.strip()), answer.stderr)

    def test_a_hold_given_to_a_started_process_lasts_as_long_as_it(self):
        held = machine.acquire("shared", "game", 1)
        game = subprocess.Popen(["sleep", "0.6"], pass_fds=held.fds)
        self.processes.append(game)
        held.give(game.pid)
        self.assertEqual([game.pid], [e["pid"] for e in self.holders()])
        with self.assertRaises(machine.Busy):
            machine.acquire("alone", "benchmark", 0.2)
        game.wait()
        machine.acquire("alone", "benchmark", 1).release()

    def test_busy_holds_alone_until_free(self):
        self.assertEqual(0, subprocess.run([sys.executable, MACHINE, "busy", "playing"], capture_output=True).returncode)
        self.assertEqual(["busy: playing"], [e["label"] for e in self.holders("alone")])
        with self.assertRaises(machine.Busy):
            machine.acquire("shared", "build", 0.2)
        subprocess.run([sys.executable, MACHINE, "free"], capture_output=True)
        self.until(lambda: not self.holders())
        machine.acquire("shared", "build", 1).release()

    def test_the_hook_refuses_a_foreground_build_while_the_machine_is_held_alone(self):
        alone = machine.acquire("alone", "benchmark", 1)
        event = {"tool_name": "Bash", "tool_input": {"command": "dotnet build"}}
        decision = machine_lock.decide(event)["hookSpecificOutput"]
        self.assertEqual("deny", decision["permissionDecision"])
        self.assertIn("benchmark", decision["permissionDecisionReason"])
        event["tool_input"]["run_in_background"] = True
        self.assertIn("updatedInput", machine_lock.decide(event)["hookSpecificOutput"])
        alone.release()


class HookTests(unittest.TestCase):
    def test_kinds(self):
        cases = {
            "dotnet build -v q 2>&1 | tail -3": "shared",
            "cd tests && dotnet test --filter X > log": "shared",
            "dotnet test > log": "suite",
            "dotnet build && dotnet test tests/Game.Tests": "suite",
            "dotnet build -c Release && BENCH_AGENT=1 tools/benchmark/run.sh base --offscreen": "alone",
            "dotnet build && src/Game/bin/Release/net10.0/Game --Engine:Session:Mode=Offline": "shared",
            "dotnet build-server shutdown": None,
            "ls src/Game/bin/Release/net10.0/Game": None,
            "python3 tools/tests/timings.py": None,
            "tools/control/game.py start": None,
            "python3 tools/machine/machine.py run -- dotnet build": None,
            "git status": None,
        }
        for command, kind in cases.items():
            self.assertEqual(kind, machine_lock.kind_of(command), command)

    def test_the_hook_keeps_the_command_and_its_fields(self):
        if machine.fcntl is None:
            self.skipTest("no lock on this platform")
        with tempfile.TemporaryDirectory() as folder:
            os.environ[machine.FOLDER_VARIABLE] = folder
            try:
                tool_input = {"command": "# a comment first\ndotnet build", "description": "Build", "timeout": 5000}
                decision = machine_lock.decide({"tool_name": "Bash", "tool_input": tool_input})
            finally:
                os.environ.pop(machine.FOLDER_VARIABLE)
        changed = decision["hookSpecificOutput"]["updatedInput"]
        self.assertNotIn("permissionDecision", decision["hookSpecificOutput"])
        self.assertTrue(changed["command"].endswith("\n# a comment first\ndotnet build"))
        self.assertEqual(("Build", 5000), (changed["description"], changed["timeout"]))

    def test_the_wrapper_exports_only_plain_values(self):
        # In a session isolated in a worktree, Claude Code refuses an export whose value it cannot read.
        lines = machine_lock.wrapped("dotnet build", "shared", "x", 5, "2").splitlines()
        exports = [line for line in lines if "export" in line]
        self.assertEqual(2, len(exports))
        for line in exports:
            self.assertRegex(line, r"^export [A-Z_]+=[a-z0-9]+$")
        self.assertNotIn("$(", "\n".join(lines))

    def test_the_switch_turns_the_hook_off(self):
        os.environ[machine_lock.SWITCH] = "off"
        try:
            self.assertIsNone(machine_lock.decide({"tool_name": "Bash", "tool_input": {"command": "dotnet build"}}))
        finally:
            os.environ.pop(machine_lock.SWITCH)

    def test_a_fault_lets_the_call_run(self):
        hook = HERE.parents[1] / ".claude" / "hooks" / "machine_lock.py"
        answer = subprocess.run([sys.executable, str(hook)], input="not json", capture_output=True, text=True)
        self.assertEqual((0, ""), (answer.returncode, answer.stdout))


if __name__ == "__main__":
    unittest.main()
