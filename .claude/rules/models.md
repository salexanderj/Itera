---
paths:
  - "tools/models/**"
  - "tools/ModelCheck/**"
  - "tools/ModelView/**"
  - "tests/Itera.ModelView.Tests/**"
  - "src/Game/ModelRecipes/**"
---

## The model tools: recipes, the checker, and the stage

How to make a model is the `models` skill's. This file holds the rules for changing the tools themselves, and where
their parts are. The engine's side (the import, `ModelInspection`, points, `Engine:Assets:Extra`) is in `mesh-assets.md`;
`viewpoint` is in `rendering.md`; `meshes place` is in `systems-meshes.md`.

### The parts

- `tools/models/`: the toolkit, in Python. `build.py` (standard library only) runs Blender, the checker, and the
  texture tool. `run_recipe.py` is what Blender runs for a recipe. `itera_models/` holds the helpers a recipe imports,
  which run inside Blender. `engine_views.py` and `tile_pictures.py` make the engine's pictures for `build.py views`.
  `merge_clips.py` merges clips from files on one rig. `retarget.py` is what Blender runs for `build.py retarget`:
  motion capture onto a model's rig, written on the model's own nodes (`tools/models/itera_models/gltf_clips.py`) for
  `merge_clips.py` to add. `clip_recipe.py` is what Blender runs for `build.py clips`: a clip recipe's key poses
  (`tools/models/itera_models/keyed.py`) on a model's rig, written on its nodes the same way, its frames drawn by
  `tools/models/itera_models/strips.py`. `blender.json` pins Blender's version. Its README has every command, the
  recipe contract, and the exit codes.
- `tools/ModelCheck/`: the checker's command line over `ModelInspection`, which holds all its rules.
- `tools/ModelView/`: the neutral stage, a program on the engine with no voxel world (`ModelStage`): `StageGround`,
  `StageSystem` (the ground and the camera at a match's start), and the engine's systems that pose and draw meshes.
- A game's recipes are in `src/Game/ModelRecipes/<Name>/`, its budgets by class in a `budgets.json` there, and its built
  models in `src/Game/Models/<Name>/`.

### Rules

- What the engine requires of a file is checked in one place: `ModelInspection`, which reads the file through the
  engine's own importer. The toolkit's export sets the options that meet it. Its scene checks find what a file cannot
  show (unweighted vertices before export, open edges, unapplied scale). Do not copy the engine's rules into Python.
- Every command that runs Blender runs the pinned version only, with `--factory-startup` and `--python-exit-code 1`, and
  never changes Blender's preferences or add-ons.
- A recipe's output must be the same bytes on every build. `verify` checks the committed models, and `selftest` the
  test recipe. A helper must not put a time, a random number, or a machine's path into a file it writes.
- A helper that makes temporary objects, scenes, images, or settings removes or restores them, so the export and the
  sheet see the model alone.
- `build.py` uses the standard library only. The helpers use only what Blender ships (bpy, bmesh, mathutils, numpy).
- Downloads go through the toolkit's fetch helper: into the cache (`$ITERA_CACHE`, or `~/.cache/itera`), checked against
  the SHA-256 a recipe pins, with a zip's members written by their file names alone.
- `ModelView` stays neutral: the engine's own look and sky, no game code, and nothing drawn but the ground and the
  models named on its command line.

### Tests

- `tools/models/build.py selftest`: the plain-Python tests (a file with the line `PLAIN_PYTHON = True`), every other
  `tools/models/tests/test_*.py` inside Blender in one process, then the test recipe built, checked, and built again
  byte for byte. It is not part of `dotnet test`, since it needs Blender.
- `ModelInspectionTests` and `MeshPointsTests` (the engine's side), `StageTests` (`tests/Itera.ModelView.Tests`), and a
  game's `GameModelsTests` over `ModelChecks`.
