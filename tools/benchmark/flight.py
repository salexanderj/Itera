#!/usr/bin/env python3
"""The flight of benchmark runs minute by minute: frames, upload pass time (over all frames and over the frames that had
uploads, and the longest), MB of meshes uploaded per second, the mesh heap in use, blocks waiting to be meshed, and the
world pass. For long flights:
run.sh <label> --Engine:Benchmark:MeasureSeconds=1 --Engine:Benchmark:SettleSeconds=0 --Engine:Benchmark:FlightSeconds=600
Usage: flight.py <run>...   (labels under $BENCH_OUT)
"""
import os
import re
import sys
from datetime import datetime

from data_folder import data_folder

root = os.environ.get("BENCH_OUT", str(data_folder() / "Benchmarks"))


def stamp(line):
    return datetime.strptime(line[:23], "%Y-%m-%d %H:%M:%S.%f").timestamp()


for run in sys.argv[1:]:
    start = end = None
    gpu, meshes, full = [], [], 0
    for line in open(os.path.join(root, run, "game.log"), encoding="utf-8"):
        if not line[:1].isdigit():
            continue
        t = stamp(line)
        if "Benchmark flight measuring" in line:
            start = t
        if "Benchmark flight done" in line:
            end = t
        if "heap is full" in line:
            full += 1
        m = re.search(
            r"GPU passes over [\d.]+ s \((\d+) frames measured\): upload in (\d+) frames avg ([\d.]+) ms max ([\d.]+) ms, "
            r"voxels avg ([\d.]+) ms",
            line,
        )
        if m:
            gpu.append((t, int(m[1]), int(m[2]), float(m[3]), float(m[4]), float(m[5])))
        m = re.search(r"Meshes over .*? (\d+) pending,.*? ([\d.]+) MB uploaded, heap ([\d.]+) MB of", line)
        if m:
            meshes.append((t, int(m[1]), float(m[2]), float(m[3])))
    if start is None or end is None:
        print(f"== {run}: no complete flight")
        continue
    print(f"== {run}: flight {end - start:.0f} s, heap full {full} time(s)")
    print(" min  frames  upload ms (all / upload frames / max)  MB/s  heap MB  pending  voxels ms")
    minute = 0
    while start + 60 * minute < end:
        a, b = start + 60 * minute, min(start + 60 * (minute + 1), end)
        g = [x for x in gpu if a < x[0] <= b]
        u = [x for x in meshes if a < x[0] <= b]
        frames = sum(x[1] for x in g)
        upload_frames = sum(x[2] for x in g)
        if frames == 0:
            break
        upload_all = sum(x[2] * x[3] for x in g) / frames
        upload_in = sum(x[2] * x[3] for x in g) / upload_frames if upload_frames else 0
        upload_max = max((x[4] for x in g), default=0)
        megabytes = sum(x[2] for x in u)
        voxels = sum(x[1] * x[5] for x in g) / frames
        heap = u[-1][3] if u else 0
        pending = max((x[1] for x in u), default=0)
        print(
            f" {minute:3d}  {frames:6d}  {upload_all:9.3f} / {upload_in:6.3f} / {upload_max:5.2f}"
            f"          {megabytes / (b - a):4.0f}  {heap:7.0f}  {pending:7d}  {voxels:9.3f}"
        )
        minute += 1
