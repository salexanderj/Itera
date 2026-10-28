#!/usr/bin/env python3
"""Reads a benchmark run's game.log into per-view GPU times; prints JSON. Usage: parse.py game.log > summary.json

For each view, the frame stats lines (one per second with --Engine:Logging:StatsIntervalSeconds=1) that fall wholly
between its "measuring" and "done" lines are averaged, weighted by frames: the world pass time ("voxels", its shadow
maps and the models' draws included), its range over those seconds, the longest frame, the upload pass per frame, the
shadow maps' and the models' part of the world pass, the anti-aliasing (SMAA) and upscale passes per frame, and the game's
own render passes and the image views per frame (0 for a game with none or a log from before them); and from
the meshes' stats lines, the megabytes uploaded, the triangles drawn in the camera's view and the shadow maps, and the
mesh heap in use. From the frame summary lines, the whole frame's GPU time, weighted by frames, and its slowest 1 %: the
highest of the seconds' 99th percentiles; and the frames shown a second, with the slowest 1 % of the times between them. From the fixed steps' lines, the step's time, the sum of its parts' averages
weighted by steps, the systems' part, and the longest step. The same for the flight. Logs from before the raymarcher left
main (2026-10-06) give the voxel pass and the GPU passes as before, and no mesh figures.
"""
import json
import re
import statistics
import sys
from datetime import datetime


def stamp(line):
    return datetime.strptime(line[:23], "%Y-%m-%d %H:%M:%S.%f").timestamp()


lines = open(sys.argv[1], encoding="utf-8").read().splitlines()
gpu = []  # (time, frames, voxels avg, voxels max, upload frames, upload avg, shadow maps avg)
models = {}  # time: (models frames, models avg, models max)
meshes = []  # (time, uploaded megabytes, heap megabytes used, triangles, shadow triangles)
post = {}  # time: (anti-aliasing, upscale, game passes, image views: frames and avg each)
frame = []  # (time, frames, GPU avg, GPU p99, frame p99)
steps = []  # (time, steps, step avg as the sum of its parts', systems avg, longest step)
windows = {}
order = []
exit_code = None
for line in lines:
    if len(line) < 23 or not line[0].isdigit():
        continue
    t = stamp(line)
    m = re.search(
        r"GPU passes over [\d.]+ s \((\d+) frames measured\): upload in (\d+) frames avg ([\d.]+) ms max ([\d.]+) ms, "
        r"voxels avg ([\d.]+) ms max ([\d.]+) ms",
        line,
    )
    if m:
        maps = re.search(r"of the world, shadow maps avg ([\d.]+) ms", line)
        gpu.append((t, int(m[1]), float(m[5]), float(m[6]), int(m[2]), float(m[3]), float(maps[1]) if maps else 0.0))
        aa = re.search(r"anti-aliasing in (\d+) frames avg ([\d.]+) ms", line)
        up = re.search(r"upscale in (\d+) frames avg ([\d.]+) ms", line)
        game = re.search(r"game passes in (\d+) frames avg ([\d.]+) ms", line)
        views = re.search(r"image views in (\d+) frames avg ([\d.]+) ms", line)
        post[t] = (
            int(aa[1]) if aa else 0,
            float(aa[2]) if aa else 0.0,
            int(up[1]) if up else 0,
            float(up[2]) if up else 0.0,
            int(game[1]) if game else 0,
            float(game[2]) if game else 0.0,
            int(views[1]) if views else 0,
            float(views[2]) if views else 0.0,
        )
        m = re.search(r"models in (\d+) frames avg ([\d.]+) ms max ([\d.]+) ms", line)
        if m:
            models[t] = (int(m[1]), float(m[2]), float(m[3]))
        continue
    m = re.search(
        r"Meshes over .*? ([\d.]+) MB uploaded, heap ([\d.]+) MB of \d+ MB used; \d+ blocks(?: \(\d+ stitched\))? and "
        r"(\d+) triangles drawn in the last frame, .*?\d+(?:, \d+,)? and \d+ blocks and (\d+) triangles into the shadow "
        r"cascades",
        line,
    )
    if m:
        meshes.append((t, float(m[1]), float(m[2]), int(m[3]), int(m[4])))
        continue
    m = re.search(
        r"Frame stats over [\d.]+ s: (\d+) frames, .*GPU avg ([\d.]+) ms max [\d.]+ ms; frame p50 [\d.]+ p95 [\d.]+ "
        r"p99 ([\d.]+) ms, GPU p50 [\d.]+ p95 [\d.]+ p99 ([\d.]+) ms",
        line,
    )
    if m:
        frame.append((t, int(m[1]), float(m[2]), float(m[4]), float(m[3])))
        continue
    m = re.search(r"Fixed steps over [\d.]+ s \((\d+) steps\): longest ([\d.]+) ms, [^;]*; systems avg ([\d.]+) ms", line)
    if m:
        # The step's parts follow the semicolon, each with its average.
        parts = re.findall(r"avg ([\d.]+) ms", line.split(";", 1)[1])
        steps.append((t, int(m[1]), sum(float(p) for p in parts), float(m[3]), float(m[2])))
        continue
    m = re.search(r"Benchmark view (\d+) ([a-z-]+) measuring at (\d+)x(\d+)", line)
    if m:
        windows[m[2]] = [t, None, f"{m[3]}x{m[4]}"]
        order.append(m[2])
        continue
    m = re.search(r"Benchmark view (\d+) ([a-z-]+) done", line)
    if m:
        windows[m[2]][1] = t
        continue
    if "Benchmark flight measuring" in line:
        windows["flight"] = [t, None, ""]
        continue
    if "Benchmark flight done" in line:
        windows["flight"][1] = t
        continue
    m = re.search(r"Shutdown complete after .* exit code (\d+)", line)
    if m:
        exit_code = int(m[1])


def summarise(start, end):
    # A stats line at time T covers the second before it.
    samples = [g for g in gpu if start + 1.05 < g[0] <= end + 0.05]
    frames = sum(s[1] for s in samples)
    if frames == 0:
        return None
    mesh = [u for u in meshes if start + 1.05 < u[0] <= end + 0.05]
    drawn = [models.get(s[0], (0, 0.0, 0.0)) for s in samples]
    after = [post.get(s[0], (0, 0.0, 0, 0.0, 0, 0.0, 0, 0.0)) for s in samples]
    whole = [f for f in frame if start + 1.05 < f[0] <= end + 0.05]
    wholes = sum(f[1] for f in whole)
    stepped = [s for s in steps if start + 1.05 < s[0] <= end + 0.05]
    counted = sum(s[1] for s in stepped)
    return {
        "voxels_ms": sum(s[1] * s[2] for s in samples) / frames,
        "voxels_min_s": min(s[2] for s in samples),
        "voxels_max_s": max(s[2] for s in samples),
        "voxels_max_frame": max(s[3] for s in samples),
        "shadow_maps_ms": sum(s[1] * s[6] for s in samples) / frames,
        "seconds": len(samples),
        "frames": frames,
        "upload_ms_per_frame": sum(s[4] * s[5] for s in samples) / frames,
        "uploaded_mb": sum(u[1] for u in mesh),
        # The heap from the last line inside the measurement: the next view's jump may free meshes just after its end.
        "heap_mb": next((u[2] for u in reversed(mesh) if u[0] <= end), mesh[-1][2] if mesh else 0),
        "triangles": statistics.mean(u[3] for u in mesh) if mesh else 0,
        "shadow_triangles": statistics.mean(u[4] for u in mesh) if mesh else 0,
        # Per frame measured: a pass's average covers only the frames that ran it.
        "models_ms": sum(d[0] * d[1] for d in drawn) / frames,
        "models_max_frame": max(d[2] for d in drawn),
        "aa_ms": sum(p[0] * p[1] for p in after) / frames,
        "upscale_ms": sum(p[2] * p[3] for p in after) / frames,
        "game_ms": sum(p[4] * p[5] for p in after) / frames,
        "views_ms": sum(p[6] * p[7] for p in after) / frames,
        # The whole frame on the GPU, and the host's fixed step: 0 for a log without their lines.
        "frame_gpu_ms": sum(f[1] * f[2] for f in whole) / wholes if wholes else 0.0,
        "frame_gpu_p99_ms": max((f[3] for f in whole), default=0.0),
        # The frames shown: how many a second, and the highest of the seconds' 99th percentiles of the time between two.
        "fps": wholes / len(whole) if whole else 0.0,
        "frame_p99_ms": max((f[4] for f in whole), default=0.0),
        "step_ms": sum(s[1] * s[2] for s in stepped) / counted if counted else 0.0,
        "systems_ms": sum(s[1] * s[3] for s in stepped) / counted if counted else 0.0,
        "step_max_ms": max((s[4] for s in stepped), default=0.0),
    }


views = {}
for name in order:
    start, end, size = windows[name]
    summary = summarise(start, end) if end else None
    if summary:
        summary["size"] = size
        views[name] = summary
result = {"views": views, "exit": exit_code}
if "flight" in windows and windows["flight"][1]:
    result["flight"] = summarise(*windows["flight"][:2])
json.dump(result, sys.stdout, indent=1)

# A view that was measured but has no stats lines, or a run with no views at all, is a failed measurement: say so and
# exit non-zero, so run.sh does not report it as a result (audit U19-9).
missing = [name for name in order if name not in views]
if "flight" in windows and not result.get("flight"):
    missing.append("flight")
if missing or not order:
    print("parse.py: no samples for " + (", ".join(missing) if missing else "any view"), file=sys.stderr)
    sys.exit(1)
