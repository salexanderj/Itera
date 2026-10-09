#!/usr/bin/env python3
"""Compares two sets of screenshots picture by picture: the share of pixels that differ by more than 2 levels in any
channel, and the largest difference. The aim dot at the centre is left out. With a third argument, writes a mask of the
differing pixels per picture there.
Usage: imgdiff.py <a> <b> [mask-dir]
  <a> and <b> are two PNG files, two folders of PNGs (compared by file name), or two benchmark labels under $BENCH_OUT.
  Needs Pillow and NumPy. Exit code 1 when any picture differs or is missing from <b>.
"""
import glob
import os
import sys

import numpy as np
from PIL import Image

from data_folder import data_folder


def pairs(a, b):
    """The (name, a, b) files to compare: two files, two folders, or two benchmark labels."""
    if os.path.isfile(a) and os.path.isfile(b):
        return [(os.path.basename(a), a, b)]
    if not (os.path.isdir(a) and os.path.isdir(b)):
        root = os.environ.get("BENCH_OUT", str(data_folder() / "Benchmarks"))
        a, b = os.path.join(root, a), os.path.join(root, b)
    return [(os.path.basename(path), path, os.path.join(b, os.path.basename(path)))
            for path in sorted(glob.glob(os.path.join(a, "*.png")))]


run_a, run_b = sys.argv[1], sys.argv[2]
masks = sys.argv[3] if len(sys.argv) > 3 else None
differs = False
compared = 0
for name, path_a, path_b in pairs(run_a, run_b):
    if not os.path.exists(path_b):
        print(f"{name}: missing from {run_b}")
        differs = True
        continue
    compared += 1
    a = np.asarray(Image.open(path_a).convert("RGB")).astype(int)
    b = np.asarray(Image.open(path_b).convert("RGB")).astype(int)
    if a.shape != b.shape:
        print(f"{name}: sizes differ, {a.shape} and {b.shape}")
        differs = True
        continue
    if a.max() == 0 or b.max() == 0:
        print(f"{name}: a screenshot is black (a locked screen?)")
        continue
    difference = np.abs(a - b).max(axis=2)
    height, width = difference.shape
    difference[height // 2 - 4 : height // 2 + 5, width // 2 - 4 : width // 2 + 5] = 0
    count = int((difference > 2).sum())
    differs = differs or count > 0
    print(f"{name:18s} differing {count:8d} px ({100 * count / difference.size:.4f} %), largest difference {difference.max():3d}")
    if masks and count:
        os.makedirs(masks, exist_ok=True)
        Image.fromarray(((difference > 2) * 255).astype(np.uint8)).save(os.path.join(masks, name))

if compared == 0:
    print(f"nothing to compare in {run_a}")
    differs = True
sys.exit(1 if differs else 0)
