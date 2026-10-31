#!/usr/bin/env python3
"""Runs the test suite with per-test results, or reads a run's, and prints where its time goes.

xunit runs a test class's tests one after another and the classes in parallel, so a run lasts at least as long as its
longest class. This prints the run's wall time, the test-seconds summed, the slowest classes (their summed time, test
count, and when each started and ended in the run), and the slowest tests.

    python3 tools/tests/timings.py                      # dotnet test --no-build, then the report
    python3 tools/tests/timings.py --build -- -c Release   # builds first; arguments after -- go to dotnet test
    python3 tools/tests/timings.py --read DIR           # the report of an earlier run's .trx files
    python3 tools/tests/timings.py --max-wall 600       # fails when the run takes longer than 600 s (CI)
    python3 tools/tests/timings.py --threads 0          # one test class a processor at once, one project at a time

After the report it prints each failed test's message and the top of its stack trace.
"""

import argparse
import collections
import glob
import os
import re
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ElementTree
from datetime import datetime

NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def seconds(duration):
    hours, minutes, rest = duration.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(rest)


def moment(text):
    # .NET writes seven decimal places; Python reads six.
    return datetime.fromisoformat(re.sub(r"(\.\d{6})\d*", r"\1", text))


def read_results(folder):
    rows = []
    for path in glob.glob(os.path.join(folder, "**", "*.trx"), recursive=True):
        root = ElementTree.parse(path).getroot()
        classes = {}
        for test in root.iterfind(".//t:UnitTest", NAMESPACE):
            classes[test.get("id")] = test.find("t:TestMethod", NAMESPACE).get("className")
        for result in root.iterfind(".//t:UnitTestResult", NAMESPACE):
            rows.append(
                (
                    classes[result.get("testId")],
                    result.get("testName"),
                    seconds(result.get("duration")),
                    moment(result.get("startTime")),
                    moment(result.get("endTime")),
                    result.get("outcome"),
                    error(result),
                )
            )
    return rows


def error(result):
    """A failed result's message and the first lines of its stack trace, or None."""
    info = result.find("t:Output/t:ErrorInfo", NAMESPACE)
    if info is None:
        return None
    message = (info.findtext("t:Message", "", NAMESPACE) or "").strip()
    stack = (info.findtext("t:StackTrace", "", NAMESPACE) or "").strip().splitlines()[:STACK_LINES]
    return "\n".join([message, *stack])


STACK_LINES = 8


def failures(rows):
    failed = [row for row in rows if row[5] == "Failed"]
    if not failed:
        return
    print(f"\nFailed tests ({len(failed)})")
    for name, test, _, _, _, _, text in sorted(failed, key=lambda row: row[1]):
        print(f"\n--- {test}")
        print(text or "(no message)")


def report(rows, top, wall):
    if not rows:
        print("No test results found.")
        return
    start = min(row[3] for row in rows)
    end = max(row[4] for row in rows)
    failed = sum(1 for row in rows if row[5] != "Passed")
    print(f"{len(rows)} tests, {failed} not passed; {sum(row[2] for row in rows):.0f} test-seconds", end="")
    print(f"; {wall:.0f} s wall" if wall else f"; {(end - start).total_seconds():.0f} s from first to last")

    by_class = collections.defaultdict(lambda: [0.0, 0, None, None])
    for name, _, duration, began, ended, *_ in rows:
        entry = by_class[name]
        entry[0] += duration
        entry[1] += 1
        entry[2] = began if entry[2] is None or began < entry[2] else entry[2]
        entry[3] = ended if entry[3] is None or ended > entry[3] else entry[3]

    print(f"\nSlowest classes (a class's tests run one after another): seconds, tests, start and end in the run")
    for name, (total, count, began, ended) in sorted(by_class.items(), key=lambda item: -item[1][0])[:top]:
        print(
            f"{total:8.1f} {count:5d} {(began - start).total_seconds():6.0f} {(ended - start).total_seconds():6.0f}  {name}"
        )

    print("\nSlowest tests: seconds, then the class and test")
    for name, test, duration, *_ in sorted(rows, key=lambda row: -row[2])[:top]:
        short = test[len(name) + 1 :] if test.startswith(name + ".") else test
        print(f"{duration:8.2f}  {name.rsplit('.', 1)[-1]}.{short}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--read", metavar="DIR", help="report an earlier run's .trx files instead of running")
    parser.add_argument("--out", metavar="DIR", help="where the run's .trx files go (default: a new temporary folder)")
    parser.add_argument("--build", action="store_true", help="build first (default: dotnet test --no-build)")
    parser.add_argument("--top", type=int, default=25, help="classes and tests to list (default 25)")
    parser.add_argument("--max-wall", type=float, metavar="SECONDS", help="fail when the run takes longer than this")
    parser.add_argument(
        "--threads",
        type=int,
        metavar="N",
        help="test classes run at once (0: one a processor): one project at a time, each with xunit's MaxParallelThreads",
    )
    parser.add_argument("test_args", nargs="*", help="after --: arguments for dotnet test (a project, a --filter, -c)")
    arguments = parser.parse_args()

    if arguments.read:
        rows = read_results(arguments.read)
        report(rows, arguments.top, None)
        failures(rows)
        return 0

    folder = arguments.out or tempfile.mkdtemp(prefix="test-timings-")
    command = ["dotnet", "test", "--logger", "trx", "--results-directory", folder]
    if not arguments.build:
        command.append("--no-build")
    command += arguments.test_args
    if arguments.threads is not None:
        # -m:1 tests the projects one after another, so N is the classes at once in the whole run.
        command += ["-m:1", "--", f"xUnit.MaxParallelThreads={arguments.threads}"]
    print("Running: " + " ".join(command), flush=True)
    began = time.monotonic()
    log = os.path.join(folder, "dotnet-test.log")
    os.makedirs(folder, exist_ok=True)
    with open(log, "w", encoding="utf-8") as output:
        code = subprocess.call(command, stdout=output, stderr=subprocess.STDOUT)
    wall = time.monotonic() - began
    with open(log, encoding="utf-8", errors="replace") as output:
        lines = output.readlines()
    for line in lines:
        if "Passed!" in line or "Failed!" in line or "[FAIL]" in line:
            print(line.rstrip())
    print(f"Results and log: {folder}\n")
    rows = read_results(folder)
    if not rows:
        print("".join(lines[-10:]))
        print("No results. On an 'Internal CLR error', run `dotnet build-server shutdown` and run again.")
        return code or 1
    report(rows, arguments.top, wall)
    failures(rows)
    if code != 0 and not any(row[5] == "Failed" for row in rows):
        # No test failed, so the test host ended early or a project did not run: the log's end says why.
        print("\ndotnet test failed with no failed test. The end of its log:")
        print("".join(lines[-30:]))
    if arguments.max_wall and wall > arguments.max_wall:
        print(f"\nThe run took {wall:.0f} s, over the limit of {arguments.max_wall:.0f} s.")
        return code or 1
    return code


if __name__ == "__main__":
    sys.exit(main())
