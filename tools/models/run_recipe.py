"""Runs one model recipe inside Blender. build.py starts it; it is not run by hand.

    blender -b --factory-startup --python-exit-code 1 --python run_recipe.py -- RECIPE OUT_GLB WORK_DIR

A recipe is a Python file with:

    NAME = "Crate"                 # required: the model's name, and the name of the recipe's folder and .glb file
    CLASS = "prop"                 # optional: the budget class the checker holds the model to
    BUDGETS = "budgets.json"       # optional: the budget file, from the recipe's folder (default ../budgets.json)
    UNIT = 1.0                     # optional: metres a unit of the file, for the checker (default 1.0)
    REFERENCES = {"front": "front.png"}   # optional: reference images beside the recipe, by view (front, back,
                                          # side, top; see itera_models/silhouette.py for each view's direction)
    MATCH = 0.85                   # optional: the least silhouette match each reference must reach (0 to 1)
    LOOPS = ["Idle", "Walk"]       # optional: clips that loop, so their last frame must hold their first pose
    FEET = ["foot_l", "foot_r"]    # optional: the bones whose tails touch the ground
    PLANTED = {"Idle": 0, "Walk": None}   # optional: clips checked for feet that slide, each with its own speed over
                                          # the ground (m/s), or None for the speed its feet show

    def build(ctx):                # required: makes the model in bpy.context.scene
        ...

build.py reads NAME, CLASS, BUDGETS, UNIT, and REFERENCES from the file's text without running it, so each must be a
plain literal. `ctx` is an itera_models.context.Context: `ctx.name`, `ctx.recipe_dir`, `ctx.out_path`, `ctx.work_dir`,
`ctx.cache_dir`, `ctx.problem(text)`, `ctx.note(text)`, `ctx.fetch(url, sha256, name)`, and
`ctx.export(objects, clips, vertex_colors)`. The recipe's folder is on sys.path, so a recipe may import a module
beside it. UNIT only goes to the checker: Blender's own scene stays in metres.

What this script does, in order: empties the scene and sets metric units; calls build(ctx); exports the scene if the
recipe did not call ctx.export(); checks the scene; checks the clips that LOOPS and PLANTED name
(itera_models/motion.py); renders the view sheet; compares the model's silhouettes with the
REFERENCES images (a problem under MATCH); saves WORK_DIR/NAME.blend; and writes WORK_DIR/report.json. The export comes before the checks and the sheet, so the cameras and the copies the sheet makes
never reach the .glb. Any exception ends Blender with a non-zero exit code and the traceback in the log.
"""

import importlib.util
import json
import os
import sys
import time
import traceback
from pathlib import Path

import bpy

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))
# Importing a recipe or a helper module must not leave __pycache__ folders in the recipe's folder.
sys.dont_write_bytecode = True

from itera_models import checks, motion, selection, silhouette, views
from itera_models.context import Context

SHEET_SIZE = 384


def cache_root() -> Path:
    """The cache folder: $ITERA_CACHE, or ~/.cache/itera."""
    return Path(os.environ.get("ITERA_CACHE") or Path.home() / ".cache" / "itera")


def load_recipe(path: Path):
    """Imports the recipe file as a module and checks that it has what a recipe needs."""
    folder = str(path.parent)
    if folder not in sys.path:
        sys.path.insert(0, folder)
    spec = importlib.util.spec_from_file_location("recipe_" + path.stem, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not isinstance(getattr(module, "NAME", None), str) or not module.NAME:
        raise ValueError(f'{path}: the recipe needs NAME = "..."')
    if not callable(getattr(module, "build", None)):
        raise ValueError(f"{path}: the recipe needs def build(ctx)")
    return module


def reset_scene() -> None:
    """Empties the scene and sets metric units, one unit a metre."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    units = bpy.context.scene.unit_settings
    units.system = "METRIC"
    units.scale_length = 1.0
    units.length_unit = "METERS"


def compare_references(recipe, recipe_dir: Path, context: Context) -> dict:
    """Each REFERENCES image's silhouette match with the model's, 0 to 1 by view; a problem for a missing image, or for
    a match under the recipe's MATCH."""
    references = {view: recipe_dir / file for view, file in getattr(recipe, "REFERENCES", {}).items()}
    missing = [str(path) for path in references.values() if not path.is_file()]
    if missing:
        context.problem(f"reference images not found: {', '.join(missing)}")
        return {}
    if not references:
        return {}
    objects = selection.resolve(context.export_objects)
    scores = silhouette.compare(objects, {view: str(path) for view, path in references.items()})
    least = getattr(recipe, "MATCH", None)
    for view, score in scores.items():
        if least is not None and score < least:
            context.problem(f"the {view} silhouette matches its reference by {score:.2f}, under MATCH {least}")
    return {view: round(score, 3) for view, score in scores.items()}


def check_motion(recipe, context: Context) -> dict | None:
    """The clip checks of the recipe's LOOPS, FEET, and PLANTED on the model's armature, or None when it names none."""
    loops = list(getattr(recipe, "LOOPS", []))
    feet = list(getattr(recipe, "FEET", []))
    planted = dict(getattr(recipe, "PLANTED", {}))
    if not (loops or feet or planted):
        return None
    armatures = [obj for obj in selection.resolve(context.export_objects) if obj.type == "ARMATURE"]
    if len(armatures) != 1:
        context.problem(f"LOOPS, FEET, and PLANTED need a model with one armature; it has {len(armatures)}")
        return None
    return motion.check(armatures[0], loops, feet, planted)


def main(arguments: list) -> None:
    if len(arguments) != 3:
        raise SystemExit("usage: run_recipe.py -- RECIPE OUT_GLB WORK_DIR")
    recipe_path, out_path, work_dir = (Path(argument).resolve() for argument in arguments)
    work_dir.mkdir(parents=True, exist_ok=True)
    timings: dict = {}
    started = time.perf_counter()

    def stage(name: str, since: float) -> float:
        now = time.perf_counter()
        timings[name] = round(now - since, 3)
        return now

    recipe = load_recipe(recipe_path)
    cache_dir = cache_root() / "inputs"
    cache_dir.mkdir(parents=True, exist_ok=True)
    context = Context(recipe.NAME, recipe_path.parent, out_path, work_dir, cache_dir)

    now = time.perf_counter()
    reset_scene()
    recipe.build(context)
    now = stage("build", now)

    if context.export_settings is None:
        context.export()
    now = stage("export", now)
    if not out_path.is_file():
        raise RuntimeError(f"the recipe did not write {out_path}")

    scene_checks = checks.scene(context.export_objects)
    now = stage("checks", now)

    motion_checks = check_motion(recipe, context)
    now = stage("motion", now)

    sheet_path = work_dir / f"{recipe.NAME}-sheet.png"
    views.sheet(sheet_path, objects=context.export_objects, size=SHEET_SIZE)
    now = stage("sheet", now)

    similarity = compare_references(recipe, recipe_path.parent, context)
    now = stage("references", now)

    bpy.ops.wm.save_as_mainfile(filepath=str(work_dir / f"{recipe.NAME}.blend"), compress=False)
    now = stage("save", now)
    timings["total"] = round(now - started, 3)

    report = {
        "recipe": str(recipe_path),
        "name": recipe.NAME,
        "blender": bpy.app.version_string,
        "output": str(out_path),
        "timings": timings,
        "export_settings": context.export_settings,
        "scene": scene_checks,
        "motion": motion_checks,
        "problems": context.problems + scene_checks["problems"] + (motion_checks or {}).get("problems", []),
        "warnings": scene_checks["warnings"],
        "notes": context.notes,
        "sheet": str(sheet_path),
        "references": similarity,
    }
    (work_dir / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    try:
        main(sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else [])
    except SystemExit:
        raise
    except BaseException:
        traceback.print_exc()
        sys.stderr.flush()
        sys.exit(1)
