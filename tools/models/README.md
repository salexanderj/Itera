# Model toolkit

Builds game models (`.glb` files) from Python recipes. Blender runs each recipe with no window, exports a glTF file the
engine accepts, checks the scene, renders a sheet of views to look at, and compares the model's outline with reference
images. Then `tools/ModelCheck` checks the file as the engine reads it, and `tools/Textures` compresses its textures.
`views` draws the result with the engine itself. A development tool, not game code.

```bash
tools/models/build.py build src/Game/ModelRecipes/Crate/Crate.py     # recipe to src/Game/Models/Crate/Crate.glb
tools/models/build.py build RECIPE.py --out DIR --work DIR           # elsewhere
tools/models/build.py build RECIPE.py --no-check --no-textures       # skip the checker, skip the texture tool
tools/models/build.py docs [--force]                                 # the local Blender API reference
tools/models/build.py views RECIPE.py|MODEL.glb [--clip Walk@0.4]   # drawn by the engine on a neutral stage
tools/models/build.py views RECIPE.py --world PLACE                  # drawn by the engine in the game's world
tools/models/build.py verify [RECIPE.py ...]                         # rebuild and compare with the committed files
tools/models/build.py retarget SPEC.json                             # motion capture onto a model's rig, as loops
tools/models/build.py clips CLIPS.py [--video]                       # clips keyed by poses on a model's rig
tools/models/build.py selftest                                       # the toolkit's own tests
```

`build.py` is plain Python (standard library only): run it with `python3`. It needs Blender, `dotnet`, and `git` (for
`docs`).

## Blender

The toolkit is pinned to one Blender version, in `blender.json`. Blender's Python API changes between versions, and the
recipes and the toolkit's tests are written for the pinned one. Every command that runs Blender first runs
`blender --version` and refuses to go on (exit code 2) when it is another version. To move the pin, install the new
version, run `selftest` and `verify`, and change `blender.json` only when both pass.

Blender is `$ITERA_BLENDER`, or `blender` on the `PATH`. Every run is
`blender -b --factory-startup --python-exit-code 1 --python SCRIPT -- ARGS`: no window, no user add-ons or settings, and
a Python exception ends Blender with a non-zero exit code. The toolkit never changes Blender's preferences or installs
add-ons.

`docs` builds the API reference of the pinned version from Blender's own sources into
`<cache>/blender-<version>/api/sphinx-in/` (`.rst` files; grep them for an operator or a property before using it). The
cache is `$ITERA_CACHE`, or `~/.cache/itera`. It clones only the folders it needs from `github.com/blender/blender` at
the tag `v<version>`, and skips the work when the folder exists unless `--force` is given.

## A recipe

A recipe is a Python file in its own folder, named for the model:

```python
NAME = "Crate"  # required: the model's name; its folder and .glb file are named so
CLASS = "prop"  # optional: the budget class the checker holds the model to
BUDGETS = "budgets.json"  # optional: the budget file, from the recipe's folder
UNIT = 1.0  # optional: metres a unit of the file, for the checker (default 1.0)
REFERENCES = {"front": "front.png"}  # optional: reference images beside the recipe, by view
REFERENCE_POSE = "idle"  # optional: the pose the references are drawn in (default rest)
MATCH = 0.85  # optional: the least silhouette match each reference must reach (0 to 1)
LOOPS = ["Idle", "Walk"]  # optional: clips that loop, so their last frame must hold their first pose
FEET = ["foot_l", "foot_r"]  # optional: the bones whose tails touch the ground
PLANTED = {"Idle": 0, "Walk": None}  # optional: clips checked for feet that slide, with their own speed (m/s)


def build(ctx):  # required: makes the model in bpy.context.scene
    ...
```

- `build.py` reads `NAME`, `CLASS`, `BUDGETS`, `UNIT`, `REFERENCES`, and `REFERENCE_POSE` from the file's text without
  running it, so each must be a plain literal.
- `REFERENCES` maps a view (`front`, `back`, `side`, `top`; `itera_models/silhouette.py` gives each view's direction)
  to an image of the subject's outline: a picture with a transparent background, or a shape on a plain background.
  After the sheet, the model's outline from that view is compared with it, both cropped to their boxes and scaled to
  the same height, so proportions count. The scores (0 to 1) print and go into `report.json`; one under `MATCH` is a
  problem.
- A reference may be drawn in another pose than the rig's rest pose (`itera_models/poses.py`): `rest`, `t-pose` (arms
  out level), `idle` (arms hanging a little out, palms in), or `anatomical` (arms hanging close, palms forward).
  `REFERENCE_POSE` names the references' pose, and a view given as `("side.png", "t-pose")` names its own. The model's
  armature is held in a reference's pose while its outline is drawn, and the build renders a sheet in each pose but
  rest beside the main one (`NAME-sheet-POSE.png`). Only the arms move, found by their bones' names
  (`upperarm_l`, `upper_arm.L`, `LeftArm`). The anatomical turn takes the palms to face down at rest.
- `BUDGETS` defaults to `../budgets.json` (beside the recipe's folder) when that file exists, else there is none. The
  checker gets `--budget` and `--class` only when the recipe has a class and a budget file exists.
- `UNIT` goes to the checker's `--unit`. Inside Blender the scene stays in metres.
- `LOOPS`, `FEET`, and `PLANTED` check the clips after the export (`itera_models/motion.py`). A clip in `LOOPS` fails
  when its last frame is off its first pose by more than 0.5 % of the rig's height, or when a bone's motion changes at
  the seam by more than twice the most it changes between two frames inside the clip. In each clip of `PLANTED`, a
  bone of `FEET` is on the ground while its tail is within 2 % of the rig's height of its lowest point in the clip. It
  fails when it moves more than 1 % of the height along the ground in one stretch there. The rig's height is from its
  lowest bone end to its highest at rest.
- The ground moves under the model at the clip's own speed: the speed over the ground a body moves at while the clip
  plays at its own pace, so a walk in place must move its planted foot back at that speed. `PLANTED` gives it in m/s,
  or `None` for the speed the clip's feet show on the ground. The summary prints the speed each clip shows either way.
- A game plays a stepping clip at its body's speed over the clip's own, so tuning how fast a body moves does not make
  its feet slide. The clip's own speed is a fact of the clip, not a tuned speed. The game measures it from the model
  as it loads (the engine's `ClipGround`, by the same rule as `None`), or keeps it in one place that its tests compare
  with the clip. So a recipe gives `None` rather than a copy of the game's number.
- The scene is empty when `build(ctx)` starts: no cube, camera, or light. Units are metric, one unit a metre.
- The recipe's folder is on `sys.path`, so a recipe can import a module beside it, and so can `itera_models`.
- The recipe may call `ctx.export(...)` itself, to choose what to export. If it does not, the toolkit exports the
  scene's visible meshes, armatures, and empties, with no clips.

`ctx` (`itera_models/context.py`):

| Member | What it is |
|---|---|
| `name`, `recipe_dir`, `out_path`, `work_dir`, `cache_dir` | The recipe's name and folder, the `.glb` it must write, the work folder, and `<cache>/inputs` for downloads. |
| `problem(text)` | Reports a problem. The build ends with exit code 1. |
| `note(text)` | Reports a note. Notes are printed and kept in `report.json`. |
| `problems`, `notes` | The lists so far. |
| `fetch(url, sha256=None, name=None)` | Downloads an input file once into `cache_dir`. See `itera_models/fetch.py`. |
| `polyhaven(asset, resolution="1k", maps=..., hashes=None)` | A Poly Haven texture's maps in the cache, `{map: path}` (CC0). |
| `ambientcg(asset, resolution="1K", sha256=None)` | An ambientCG material's maps in the cache, `{map: path}` (CC0). |
| `export(objects=None, clips=None, vertex_colors=False)` | Writes `out_path` through `export.glb`. Returns the settings used. |

## The helpers

All of them run inside Blender (`from itera_models import export, checks, views, points, rig, clips, motion, materials`).
Each module's docstrings say more.

- `export.glb(path, objects=None, clips=None, vertex_colors=False)` exports a binary glTF file with every option the
  engine needs: modifiers applied; no Draco, no meshopt, no gltfpack; PNG or JPEG images only; at most four joint
  influences (`export_influence_nb=4`); no morph targets; metres and +Y up (a model's front, glTF +Z, is Blender -Y); no
  cameras or lights; tangents when a material has a normal map. `objects` defaults to the scene's visible meshes,
  armatures, and empties. `clips` is a list of action names: only those are exported, as glTF animations named as the
  actions and starting at time 0. With no clips there is no animation at all. Vertex colours are exported only when
  `vertex_colors=True`. The scene's animation state and selection are put back afterwards. It returns the dict of options
  it passed to Blender's exporter.
- `checks.scene(objects=None)` returns a dict of counts and two lists, `problems` (the build fails) and `warnings`.
  Problems: edges shared by more than two faces, loose vertices and edges, faces with no area, an object scale other than 1, a texture
  or normal map on a mesh with no UV map, empty or unnamed material slots, vertices that weigh no bone, vertices on more
  than four bones, weight on a vertex group that names no bone, and more than one armature. Warnings: open edges (a hole,
  or a card such as a leaf or a banner), a material with no UV map where none is needed, unused vertex groups that name no bone, bone weights that do not sum to 1 (past
  1e-3), and an action with no slot. It also reports triangles after modifiers, bone and deform-bone counts, and each
  action's slot and frame range. Blender reports a failed automatic weighting only as a warning, so the check for
  vertices that weigh no bone is the one that catches it. A mesh parented to one bone is a rigid part, not skinned, and
  needs no weights.
- `views.sheet(path, objects=None, size=384, engine="BLENDER_WORKBENCH")` writes one labelled PNG: a shaded row and a
  wireframe row, each with the front, back, left, right, a three-quarter view, and the top. Front is the side the model
  faces in the engine. Left and right are the model's own left and right. The top view has the front at the bottom. The
  side and top views are orthographic. `views.posed(path, armature, clip, seconds, size=384)` draws the same sheet with
  an action set, `seconds` into the clip.
- `points.add_point(name, location, rotation=None, parent=None, bone=None, size=0.05)` adds a plain-axes empty that the
  engine reads as a named point (a muzzle, a grip). With `bone`, the point hangs under that bone of the armature
  `parent`: `location` is in the bone's own axes, from its head, and is exported as the point's translation under the
  joint. `rotation` is Euler angles in radians; no rotation exports as no rotation.
- `rig`: `import_reference(path)` (a .glb's armature and meshes), `transfer_weights(target, source, armature)` (a new
  body or armour weighted from a reference body, then limited to four bones and normalised), `auto_weights(obj,
  armature)` (bone heat; returns the vertices it left unweighted), `rigid_skin(parts, armature)` (each part on one
  bone), `limit_and_normalise(obj)`, and `weight_stats(obj)`.
- `clips`: `new_clip(armature, name, frame_start, frame_end=...)` (an action with its slot, as Blender 5 needs),
  `key_pose(armature, frame, pose)`, `bake(armature, start, end)` (constraints and IK to plain keys),
  `retarget(source, target, joint_map, start, end, hips=None, rest_frame=None, feet=None, aim=None)`,
  `remove_clip(name)`, and `clip_keys(armature)`. After a retarget, remove the source's clip: the exporter writes every
  action with bone keys it finds. `rest_frame` takes a frame holding the source's T-pose as its rest; `feet` measures
  the hips' heights from the ground (the source's feet over the clip), for a capture whose floor is not at its origin;
  `aim` copies only the direction of bones whose captured roll cannot be trusted (feet), turned by the difference of
  the two rests' directions.
- `gltf_clips`: a model's clips as glTF data on the model's own nodes. `corrections(armature, nodes)` finds each
  imported bone's fixed turn from its node, `sample(...)` reads a posed clip back onto the nodes, `repair(...)` mends a
  capture's broken samples, `settle(...)` makes a sampled cycle a loop played in place facing the model's front with
  its feet on the floor, `resample(...)` spreads it over the keys written, and `write(path, document, clips)` writes
  clips that `merge_clips.py` takes over.
- `fetch.zip_member(url, member, cache_dir, sha256)` fetches one file of a remote zip by range requests, as `fetch`
  does a whole file: one motion capture clip of an archive of gigabytes.
- `materials`: `pbr(name, color, normal, roughness, ao, metal, normal_directx, tiling)` and `pbr_from_maps(name, maps)`
  (from `ctx.polyhaven` or `ctx.ambientcg`), `grime(material)` (darker crevices and corners, seen in a bake), and
  `bake(obj, size, maps, out_dir=...)` (Cycles on the CPU; unwraps when there is no UV map; replaces the object's
  materials with one image-texture material). A `tiling` other than 1 exports as `KHR_texture_transform`, which the
  engine does not apply: bake such a material before the export.
- `silhouette`: `mask(objects, view)`, `image_mask(path)`, `iou(a, b)`, and `compare(objects, references)`, which
  `REFERENCES` uses.
- `motion.check(armature, loops, feet, planted, loop_gap=..., seam_jolt=..., contact=..., slide=...)` checks clips as
  `LOOPS`, `FEET`, and `PLANTED` do, with other limits when given (shares of the rig's height). Its numbers per clip
  go into `report.json`, and the build's summary prints each clip's loop gap, largest slide, and the speed its feet
  show. `motion.ground_speed(paths, seconds_per_frame, contact)` is that speed.

## The work folder

`<agent data folder>/ModelBuilds/<NAME>/` (the agent data folder is `~/.local/share/<DataFolder>-agent`, where
`DataFolder` is `Engine:Application:DataFolder` in `src/Game/appsettings.json`), or `--work DIR`:

- `blender.log`: all of Blender's output. On a failure `build.py` prints its last lines.
- `<NAME>-sheet.png`: the view sheet.
- `<NAME>.blend`: the scene after the build, to open in Blender and look at.
- `report.json`: the recipe, the Blender version, the time of each stage, the export settings used, the scene checks,
  the clip checks, problems, warnings, and notes, the sheet's path, and the silhouette matches.
- `views/` and `world-views/`: what `views` draws (below).

## views

`views` draws a model with the engine itself, from front, back, left, right, top (all orthographic), and a
three-quarter view (perspective), plus a three-quarter view of each `--clip CLIP@SECONDS` pose, and tiles them into
`sheet.png`. It reads the model through the engine's `Engine:Assets:Extra` option, so nothing is rebuilt
(`engine_views.py`).

- By default it draws on `tools/ModelView`'s neutral stage: a grey ground with a 10 cm grid, the engine's own sky and
  look, the same for every game, in under 10 s.
- `--world PLACE` draws it in the game's own world, light, and fog: the game starts offscreen through
  `tools/control/game.py`, goes to its `tp` place `PLACE`, puts the model on the ground there, and stops after.
- Given a recipe, it draws the recipe's built model. Given a `.glb`, `--unit` and `--name` set its unit and name.

## Exit codes

- `0`: the model is ready (`verify`: every file matches; `selftest`: every test passed).
- `1`: a problem. A recipe error, a scene problem, a problem the recipe reported, a checker problem, a failed texture
  compression, a file that differs, or a failed test.
- `2`: a wrong command line, a recipe file that is not a recipe, Blender missing, or Blender not the pinned version.

## verify

`verify` rebuilds each recipe (default: every `src/Game/ModelRecipes/*/*.py` that defines `NAME`) into a temporary
folder and compares the `.glb` with the committed `src/Game/Models/<NAME>/<NAME>.glb` byte for byte. It makes each clip
recipe's clips again too (a file there that names `MODEL` and `OUT` and no `NAME`) and compares them with its `OUT`.
`--against DIR` compares with `DIR/<NAME>/<NAME>.glb` instead. Blender's output has been byte-identical from run to run
for the test recipe (a skinned mesh with a clip, a point, and an image). A change to the pinned Blender, to a recipe, or to the
helpers can change the bytes: look at the model, then build it again and commit the new file.

## selftest

`selftest` runs the plain-Python tests (`tests/run.py --plain`: `test_build.py`, `test_merge_clips.py`), the Blender
tests (every other `tests/test_*.py`, in one Blender process), then builds `tests/recipes/TestCrate` with the checker and
the texture tool, and builds it again to check that the output is identical. A test file with the line
`PLAIN_PYTHON = True` is a plain-Python test.

## merge_clips.py

`merge_clips.py LIST.json` builds a skinned `.glb` from a base file and clips chosen from it and from other files on
the same rig. Its docstring has the list's format.

## retarget

`retarget SPEC.json` puts motion capture clips (BVH files, each in a zip) or an animation pack's actions (in a local
`.blend` file) on a model's rig and writes them into one `.glb` on the model's own nodes, which a `merge_clips.py` list
then adds to the model (`retarget.py`'s docstring has the spec's form). For each clip it:

- fetches the BVH by range requests and checks its pinned SHA-256, or appends the `.blend` file's armature with the
  action, its control rig worked out at each frame;
- turns the source to face as the model's rest does, and retargets it (`clips.retarget`, with the source's rest frame,
  its feet for the hips' heights, and the joints it aims);
- reads one cycle, the spec's frames `from` to `to`, back onto the model's nodes at every source frame;
- mends the joints its `repair` rules name: a sample next to a turn faster than `mostSpeed` (degrees a second), or
  further than `mostAngle` from the joint's rest, takes the turns between the good samples either side;
- turns the cycle to face the model's front by its hips (unless the clip or its source says `"face": false`, for a
  pack's clip turned to face the way it goes), takes its travel out of the hips so the game moves the body, sets its
  feet's lowest point at their rest's height, and spreads what is left at the seam over the cycle, so it loops; a clip
  with `"loop": false` (a death, a turn on the spot) is played once and kept as the source has it;
- spreads the cycle over the spec's keys a second, and reports the travel taken out (its speed and way), the samples
  mended, and `motion.py`'s checks: the speed the feet show, their largest slide, and the seam.

Capture is raw. Its feet are its worst part: their roll flips and they glitch for a few frames at a time, so aim them,
mend them, and leave the toes out of the map, which keeps the model's own foot shape. Look at the result with `views
--clip` before using it. The work folder is `<agent data folder>/ModelBuilds/retarget-<SPEC>/`, or `--work DIR`.

## clips

`clips CLIPS.py` makes a clip recipe's clips on an existing model's rig (`clip_recipe.py`'s docstring has the recipe's
form): each from a few key poses (`itera_models/keyed.py`), written into one `.glb` on the model's own nodes, which a
`merge_clips.py` list then adds to the model. For each clip it:

- stands the model in the first pose of its own clip `STAND`, which each clip starts and ends in;
- keys at each key pose the hips (moved and turned), the spine and the neck (turned), each collarbone, and a target
  and a pole for each hand and foot. A hand goes to a place (`at`), moves from where it stands (`by`), or reaches a way
  from its shoulder (`reach`, a share of the arm's length). An elbow and a knee point the way the key gives. The
  fingers keep the stand's pose. A part a key leaves out moves between the keys that name it;
- eases each key to the next as it asks (`both`, `in` for a blow, `out`, `linear`, `hold`), bakes the IK to plain keys
  every frame, and makes its mirror for the other side where the recipe asks;
- checks it (`keyed.check`): a joint under the ground (a little allowed for the joints named as touching it, and those
  below them), a hand or foot that falls short of its place (with where its limb's root was), a knee or elbow bent past
  a person's or a knee bent backwards, a joint named as planted that slides, and first and last poses off the stand;
- reports where its impacts put the joints named, for the game's hit windows, and draws a sheet of its frames from the
  side and from three quarters in front (`itera_models/strips.py`), with `--video` an MP4 too.

Each key must fall on a frame. A clip with a problem fails the command (exit 1). The work folder is
`<agent data folder>/ModelBuilds/clips-<RECIPE>/`, or `--work DIR`.
