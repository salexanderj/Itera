#!/usr/bin/env python3
"""Runs a texture recipe and writes its maps as tools/Textures reads them.

    python3 make.py RECIPE.py OUT [--texels N] [--preview SHEET.png]

A recipe is a Python file with METRES (the tile's width), optionally TEXELS (2048 by default) and SEED (1), and a
function make(tile) that takes a tiles.Tile and returns a tiles.Maps. --texels makes it smaller to try it quickly;
the recipe's sizes are in metres, so it looks the same. --preview also writes a lit preview sheet (preview.py).
"""

import argparse
import importlib.util
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import preview  # noqa: E402
import tiles  # noqa: E402


def load(path):
    name = os.path.splitext(os.path.basename(path))[0].replace("-", "_")
    spec = importlib.util.spec_from_file_location(f"recipe_{name}", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("recipe")
    parser.add_argument("out")
    parser.add_argument("--texels", type=int)
    parser.add_argument("--preview")
    args = parser.parse_args()

    started = time.monotonic()
    recipe = load(args.recipe)
    tile = tiles.Tile(recipe.METRES, args.texels or getattr(recipe, "TEXELS", 2048), getattr(recipe, "SEED", 1))
    maps = recipe.make(tile)
    if maps.colour.shape != (tile.texels, tile.texels, 3) or maps.height.shape != (tile.texels, tile.texels):
        sys.exit(f"{args.recipe}: make gave a colour of {maps.colour.shape} and a height of {maps.height.shape}")
    tiles.write(args.out, tile, maps)
    mean = maps.colour.reshape(-1, 3).mean(0)
    print(
        f"{os.path.basename(args.recipe)}: {tile.texels} texels over {tile.metres:g} m, height {maps.height.min():.3f} "
        f"to {maps.height.max():.3f} m, colour mean {mean.round(4)} (linear), {time.monotonic() - started:.1f} s"
    )
    if args.preview:
        preview.sheet([(os.path.splitext(os.path.basename(args.recipe))[0], args.out)], args.preview)


if __name__ == "__main__":
    main()
