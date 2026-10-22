#!/usr/bin/env python3
"""Runs the test suite with per-test results, or reads a run's, and prints where its time goes.

xunit runs a test class's tests one after another and the classes in parallel, so a run lasts at least as long as its
longest class. This prints the run's wall time, the test-seconds summed, the slowest classes (their summed time, test
count, and when each started and ended in the run), and the slowest tests.

    python3 tools/tests/timings.py                      # dotnet test --no-build, then the report
    python3 tools/tests/timings.py --build -- -c Release   # builds first; arguments after -- go to dotnet test
    python3 tools/tests/timings.py --read DIR           # the report of an earlier run's .trx files
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
                )
            )
    return rows


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
    for name, _, duration, began, ended, _ in rows:
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
    parser.add_argument("test_args", nargs="*", help="after --: arguments for dotnet test (a project, a --filter, -c)")
    arguments = parser.parse_args()

    if arguments.read:
        report(read_results(arguments.read), arguments.top, None)
        return 0

    folder = arguments.out or tempfile.mkdtemp(prefix="test-timings-")
    command = ["dotnet", "test", "--logger", "trx", "--results-directory", folder]
    if not arguments.build:
        command.append("--no-build")
    command += arguments.test_args
    print("Running: " + " ".join(command), flush=True)
    began = time.monotonic()
    log = os.path.join(folder, "dotnet-test.log")
    os.makedirs(folder, exist_ok=True)
    with open(log, "w", encoding="utf-8") as output:
        code = subprocess.call(command, stdout=output, stderr=subprocess.STDOUT)
    wall = time.monotonic() - began
    with open(log, encoding="utf-8") as output:
        for line in output:
            if "Passed!" in line or "Failed!" in line or "[FAIL]" in line:
                print(line.rstrip())
    print(f"Results and log: {folder}\n")
    rows = read_results(folder)
    if not rows:
        with open(log, encoding="utf-8") as output:
            print("".join(output.readlines()[-10:]))
        print("No results: an 'Internal CLR error' right after a build passes after `dotnet build-server shutdown`.")
        return code or 1
    report(rows, arguments.top, wall)
    return code


if __name__ == "__main__":
    sys.exit(main())
