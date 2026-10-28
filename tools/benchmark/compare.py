#!/usr/bin/env python3
"""Compares two groups of benchmark runs view by view: the mean world pass time ("voxels") of each group, its range over
the runs, and the change; then the same for the models' draws in the views that drew models, and for the shadow maps
(both part of the world's time), the anti-aliasing (SMAA) and upscale passes, and the game's own render passes when
either group ran them; and the whole frame's GPU time and its slowest 1 %, and the host's fixed step and its longest,
when both groups' summaries have them.
Usage: compare.py <name-a> <run,run,...> <name-b> <run,run,...>   (runs are labels under $BENCH_OUT)

Take the runs of the two builds in turn (a, b, a, b, ...) so that drift in GPU clocks or temperature affects both.
"""
import json
import os
import statistics
import sys

from data_folder import data_folder

root = os.environ.get("BENCH_OUT", str(data_folder() / "Benchmarks"))


def load(runs):
    return [json.load(open(os.path.join(root, run, "summary.json"))) for run in runs.split(",")]


name_a, a, name_b, b = sys.argv[1], load(sys.argv[2]), sys.argv[3], load(sys.argv[4])


def times(group, view, key):
    return [(r["flight"] if view == "flight" else r["views"][view]).get(key, 0.0) for r in group]


def table(title, key, views):
    print(f"{title:12s} {name_a:>24s} {name_b:>24s} {'change':>8s}")
    for view in views:
        xa, xb = times(a, view, key), times(b, view, key)
        ma, mb = statistics.mean(xa), statistics.mean(xb)
        text_a = f"{ma:6.3f} ({min(xa):.3f}-{max(xa):.3f})"
        text_b = f"{mb:6.3f} ({min(xb):.3f}-{max(xb):.3f})"
        change = f"{100 * (mb - ma) / ma:+7.1f}%" if ma > 0 else "       -"
        print(f"{view:12s} {text_a:>24s} {text_b:>24s} {change:>8s}")


views = [v for v in a[0]["views"] if all(v in r["views"] for r in a + b)]
views += ["flight"] if a[0].get("flight") and b[0].get("flight") else []
table("voxels", "voxels_ms", views)
drawn = [v for v in views if max(times(a, v, "models_ms") + times(b, v, "models_ms")) > 0]
if drawn:
    print()
    table("models", "models_ms", drawn)
for title, key in (
    ("shadow maps", "shadow_maps_ms"),
    ("anti-alias", "aa_ms"),
    ("upscale", "upscale_ms"),
    ("game passes", "game_ms"),
):
    if max(t for v in views for t in times(a, v, key) + times(b, v, key)) > 0:
        print()
        table(title, key, views)
# The budgets' own figures: summaries made before parse.py read them have none, so a group without them is left out.
for title, key in (
    ("frame GPU", "frame_gpu_ms"),
    ("frame p99", "frame_gpu_p99_ms"),
    ("step", "step_ms"),
    ("longest step", "step_max_ms"),
):
    if all(min(times(group, v, key)) > 0 for group in (a, b) for v in views):
        print()
        table(title, key, views)
for name, group in ((name_a, a), (name_b, b)):
    print(f"{name}: exit codes {[r['exit'] for r in group]}")
