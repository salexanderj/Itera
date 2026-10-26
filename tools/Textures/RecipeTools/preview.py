#!/usr/bin/env python3
"""Lit previews of texture sets, to judge a recipe before the game: each set drawn 1.28 m, 5.12 m, and 15.36 m across,
lit by a sun 30 degrees over the tile from its upper left and a dim sky scaled by the occlusion, with the lit
lightness's spread printed for each width. The widest view shows the tile's repeat, and a spread near 0 there means no
shape reads from far off.

    python3 preview.py OUT.png LABEL FOLDER [LABEL FOLDER ...]

A FOLDER holds colour.png, normal.png, occlusion.png, and metres.txt, as make.py writes them or the game bakes them.
"""

import sys

import numpy as np
from PIL import Image, ImageDraw

SUN = np.array([1.0, 0.85, 0.7]) * 1.6
SKY = np.array([0.5, 0.5, 0.55]) * 0.45
LIGHT = np.array([-np.cos(np.radians(30)) * 0.77, np.cos(np.radians(30)) * 0.64, np.sin(np.radians(30))])
LIGHT /= np.linalg.norm(LIGHT)
CELL = (800, 450)
WIDTHS = (1.28, 5.12, 15.36)


def to_linear(s):
    return np.where(s <= 0.04045, s / 12.92, ((s + 0.055) / 1.055) ** 2.4)


def srgb(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * x ** (1 / 2.4) - 0.055)


def load(folder):
    def read(name):
        return np.asarray(Image.open(f"{folder}/{name}").convert("RGB"), np.float32) / 255

    colour = to_linear(read("colour.png"))
    n = read("normal.png") * 2 - 1
    # The tile's frame: x along the rows, y up the rows (the image's first row at the bottom), z out of it.
    normal = np.stack([n[..., 0], -n[..., 1], n[..., 2]], -1)
    occlusion = read("occlusion.png")[..., 0]
    with open(f"{folder}/metres.txt", encoding="utf-8") as f:
        metres = float(f.read())
    return colour, normal, occlusion, metres


def shrink(a, f):
    if f == 1:
        return a
    h, w = a.shape[:2]
    return a[: h - h % f, : w - w % f].reshape(h // f, f, w // f, f, *a.shape[2:]).mean((1, 3))


def view(maps, width):
    """The set width metres across, as an image of CELL's size, and the spread of its lit lightness."""
    colour, normal, occlusion, metres = maps
    want = int(round(width * colour.shape[0] / metres))
    f = max(1, want // (CELL[0] * 2))
    c, n, o = shrink(colour, f), shrink(normal, f), shrink(occlusion, f)
    w = want // f
    h = int(round(w * CELL[1] / CELL[0]))
    reps = (int(np.ceil(h / c.shape[0])), int(np.ceil(w / c.shape[1])))

    def tiled(a):
        return np.tile(a, reps + (1,) * (a.ndim - 2))[:h, :w]

    c, n, o = tiled(c), tiled(n), tiled(o)
    lit = c * (SUN * np.clip((n * LIGHT).sum(-1), 0, None)[..., None] + SKY * o[..., None])
    image = Image.fromarray((srgb(lit[::-1]) * 255 + 0.5).astype(np.uint8)).resize(CELL, Image.LANCZOS)
    luma = np.asarray(image, np.float32) @ np.array([0.2126, 0.7152, 0.0722]) / 255
    return image, float(luma.std() / max(luma.mean(), 1e-6))


def sheet(sets, out):
    """One row of the three views for each (label, folder) in sets, written to out; prints each view's spread."""
    pad = 28
    s = Image.new("RGB", (CELL[0] * len(WIDTHS), (CELL[1] + pad) * len(sets)), (20, 20, 20))
    d = ImageDraw.Draw(s)
    for r, (label, folder) in enumerate(sets):
        maps = load(folder)
        spreads = []
        for i, width in enumerate(WIDTHS):
            image, spread = view(maps, width)
            spreads.append(spread)
            s.paste(image, (i * CELL[0], r * (CELL[1] + pad) + pad))
            d.text(
                (i * CELL[0] + 8, r * (CELL[1] + pad) + 8),
                f"{label}: {width:g} m across, spread {spread:.0%}",
                fill=(230, 230, 230),
            )
        print(f"{label}: lit spread " + ", ".join(f"{w:g} m {p:.1%}" for w, p in zip(WIDTHS, spreads)))
    s.save(out)


if __name__ == "__main__":
    if len(sys.argv) < 4 or len(sys.argv) % 2 != 0:
        sys.exit(__doc__)
    sheet(list(zip(sys.argv[2::2], sys.argv[3::2])), sys.argv[1])
