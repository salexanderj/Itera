#!/usr/bin/env python3
"""Reads a trace of the timed zones (the console's zones start / zones stop, the data folder's Traces/) and prints, for
the main thread, each zone's time a fixed step and its longest, slowest first; then how busy each thread was over the
trace. Usage: zones.py TRACE.json [--top N] (N zones, 40 by default).

A zone's time a step is its total over the trace divided by the trace's fixed steps (its "step" zones), so the systems'
times add up to the step's. Nested zones are counted in their own row and inside the zone round them. A thread's busy
share counts its outermost zones alone.
"""
import argparse
import json
from collections import defaultdict

parser = argparse.ArgumentParser()
parser.add_argument("trace")
parser.add_argument("--top", type=int, default=40)
args = parser.parse_args()

events = json.load(open(args.trace, encoding="utf-8"))["traceEvents"]
names = {e["tid"]: e["args"]["name"] for e in events if e.get("ph") == "M" and e.get("name") == "thread_name"}
spans = [e for e in events if e.get("ph") == "X"]
if not spans:
    raise SystemExit("zones.py: the trace has no zones")
main = next((tid for tid, name in names.items() if "main" in name), spans[0]["tid"])
start = min(e["ts"] for e in spans)
end = max(e["ts"] + e["dur"] for e in spans)
seconds = (end - start) / 1e6

total = defaultdict(float)
count = defaultdict(int)
longest = defaultdict(float)
for e in spans:
    if e["tid"] == main:
        total[e["name"]] += e["dur"] / 1000
        count[e["name"]] += 1
        longest[e["name"]] = max(longest[e["name"]], e["dur"] / 1000)

steps = count.get("step", 0)
frames = count.get("frame", 0)
print(f"{seconds:.1f} s traced: {steps} fixed steps, {frames} frames built on {names.get(main, main)}")
print(f"{'zone':48s} {'ms a step':>10s} {'ms each':>9s} {'longest':>9s} {'count':>7s}")
for name in sorted(total, key=total.get, reverse=True)[: args.top]:
    per_step = total[name] / steps if steps else 0
    print(f"{name:48s} {per_step:10.3f} {total[name] / count[name]:9.3f} {longest[name]:9.2f} {count[name]:7d}")

print()
print("busy share of each thread (its outermost zones):")
by_thread = defaultdict(list)
for e in spans:
    by_thread[e["tid"]].append((e["ts"], e["ts"] + e["dur"]))
for tid, intervals in sorted(by_thread.items(), key=lambda item: names.get(item[0], str(item[0]))):
    intervals.sort()
    busy, reach = 0.0, None
    for low, high in intervals:
        if reach is None or low >= reach:
            busy += high - low
            reach = high
        elif high > reach:
            busy += high - reach
            reach = high
    print(f"  {names.get(tid, tid)!s:40s} {busy / (end - start) * 100:5.1f} %")
