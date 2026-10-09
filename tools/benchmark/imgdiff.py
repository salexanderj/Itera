#!/usr/bin/env python3
"""Compares two benchmark runs' screenshots view by view: the share of pixels that differ by more than 2 levels in any
channel, and the largest difference. The aim dot at the centre is left
out. With a third argument, writes a mask of the differing pixels per view there.
Usage: imgdiff.py <run-a> <run-b> [mask-dir]   (labels under $BENCH_OUT; needs Pillow and NumPy)
"""
import glob
import os
import sys

import numpy as np
from PIL import Image

from data_folder import data_folder

root = os.environ.get("BENCH_OUT", str(data_folder() / "Benchmarks"))
run_a, run_b = sys.argv[1], sys.argv[2]
masks = sys.argv[3] if len(sys.argv) > 3 else None
for path_a in sorted(glob.glob(os.path.join(root, run_a, "*.png"))):
    name = os.path.basename(path_a)
    path_b = os.path.join(root, run_b, name)
    if not os.path.exists(path_b):
        continue
    a = np.asarray(Image.open(path_a).convert("RGB")).astype(int)
    b = np.asarray(Image.open(path_b).convert("RGB")).astype(int)
    if a.shape != b.shape:
        print(f"{name}: sizes differ, {a.shape} and {b.shape}")
        continue
    if a.max() == 0 or b.max() == 0:
        print(f"{name}: a screenshot is black (a locked screen?)")
        continue
    difference = np.abs(a - b).max(axis=2)
    height, width = difference.shape
    difference[height // 2 - 4 : height // 2 + 5, width // 2 - 4 : width // 2 + 5] = 0
    count = int((difference > 2).sum())
    print(f"{name:18s} differing {count:8d} px ({100 * count / difference.size:.4f} %), largest difference {difference.max():3d}")
    if masks and count:
        os.makedirs(masks, exist_ok=True)
        Image.fromarray(((difference > 2) * 255).astype(np.uint8)).save(os.path.join(masks, name))
