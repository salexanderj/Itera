"""Tiles PNG pictures into one sheet, in order, a few to a row: what build.py views makes of the engine's pictures.

Run inside Blender (for its image reading and numpy): blender -b --factory-startup --python-exit-code 1 --python
tile_pictures.py -- OUT.png COLUMNS CELL IN.png [IN.png ...]. Each picture is scaled to CELL by CELL pixels (nearest)
and laid out left to right, top to bottom, with a thin dark line between cells.
"""

from __future__ import annotations

import sys

import bpy
import numpy as np

GAP = 4


def read(path: str) -> np.ndarray:
    """A picture's pixels as a (height, width, 4) float array, row 0 at the top."""
    image = bpy.data.images.load(path, check_existing=False)
    try:
        width, height = image.size
        pixels = np.empty(width * height * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
    finally:
        bpy.data.images.remove(image)
    return pixels.reshape(height, width, 4)[::-1]


def scaled(pixels: np.ndarray, cell: int) -> np.ndarray:
    height, width = pixels.shape[:2]
    rows = (np.arange(cell) * height // cell).clip(0, height - 1)
    columns = (np.arange(cell) * width // cell).clip(0, width - 1)
    return pixels[rows][:, columns]


def tile(out_path: str, columns: int, cell: int, paths: list) -> None:
    rows = (len(paths) + columns - 1) // columns
    width = columns * cell + (columns + 1) * GAP
    height = rows * cell + (rows + 1) * GAP
    canvas = np.zeros((height, width, 4), dtype=np.float32)
    canvas[..., :3] = 0.08
    canvas[..., 3] = 1
    for index, path in enumerate(paths):
        row, column = divmod(index, columns)
        top = GAP + row * (cell + GAP)
        left = GAP + column * (cell + GAP)
        canvas[top : top + cell, left : left + cell] = scaled(read(path), cell)
    image = bpy.data.images.new("sheet", width, height, alpha=True)
    image.pixels.foreach_set(canvas[::-1].ravel())
    image.filepath_raw = out_path
    image.file_format = "PNG"
    image.save()


if __name__ == "__main__":
    arguments = sys.argv[sys.argv.index("--") + 1 :]
    tile(arguments[0], int(arguments[1]), int(arguments[2]), arguments[3:])
