---
name: models
description: How to make a game's models, their rigs, clips, and textures with Itera's model toolkit - a Python recipe run by Blender with no window (tools/models), checked as the engine reads it (tools/ModelCheck), and drawn by the engine itself (tools/ModelView). Use before writing or changing a model recipe, a model's glTF file, its rig, clips, points, or textures, or anything under tools/models, tools/ModelCheck, tools/ModelView, or a game's src/Game/ModelRecipes.
---

# Making models

Every model a game ships is built by a recipe: a Python file in the game's `src/Game/ModelRecipes/<Name>/` that
Blender runs with no window. The recipe lives in the repository beside the model it builds, so anyone can build the
model again, read what changed in a diff, and check it the same way each time. Do not build a model by hand in a live
Blender session, and never edit a `.glb` file directly.

Three tools do the work:

| Tool | What it does |
| --- | --- |
| `tools/models/build.py` | Runs a recipe in the pinned Blender, exports the `.glb`, checks the scene, renders a view sheet, compares outlines with reference images, then runs the two tools below. Also `views`, `verify`, `docs`, and `selftest`. Its README has every option. |
| `tools/ModelCheck` | Reads the `.glb` through the engine's own importer and lists what the engine would refuse, warn of, drop, or find over budget. |
| `tools/ModelView` | A neutral stage on the engine: a grey ground with a 10 cm grid and the engine's own light, for the engine's own pictures of a model. |

## Before you start

1. **Get reference images.** They help shape quality more than anything else. A model built from words alone comes out
   plain. Ask the user for a picture of the subject from the front, and from the side or the top where its shape needs
   it: concept art, a photograph, or a sketch on a plain background. Put each beside the recipe and name it in
   `REFERENCES`. Without them, say so before you start, and ask the user to look at the result.
2. **Read the game's rules for the subject:** its design page, its budget class in the game's `budgets.json` (in
   `src/Game/ModelRecipes/`), and its rig. A humanoid keeps the game's own skeleton, its joint names, order, and rest pose, so every clip plays.
3. **Read `blender-api.md` beside this file.** Blender's Python API changed a great deal in 4.x and 5.x, and wrong API
   calls are the most common reason a script fails. Build the local API reference once with `tools/models/build.py
   docs`, and grep it before using an operator or a property the toolkit does not already use.
4. **Read `recipes.md` beside this file** for the pattern that fits: a prop, a part with points, a humanoid variant, a
   creature, clips, or materials. Start from the crate in Itera's starter game (its recipe is
   ModelRecipes/Crate/Crate.py there).

## The loop

1. Write the recipe: `NAME`, `CLASS`, `REFERENCES`, `MATCH`, and `def build(ctx)` (the toolkit's README has the
   contract). Use the helpers in `tools/models/itera_models/`. They export, rig, key, and bake the way the engine needs.
2. Build it: `tools/models/build.py build src/Game/ModelRecipes/<Name>/<Name>.py`. It takes seconds. Read every
   problem and warning in its summary. A Python error ends the build with the end of Blender's log printed.
3. Look at the view sheet it names (open the PNG with the Read tool): the shaded and wireframe rows from front, back,
   left, right, three-quarter, and top. Compare it with the references and the silhouette scores. Fix the recipe and
   build again until the model is right, not only until it passes.
4. See it in the engine: `tools/models/build.py views <recipe>` draws it on the neutral stage, and `--world PLACE`
   draws it in the game's own world, light, and fog at one of its `tp` places. Read `sheet.png`. Add `--clip
   NAME@SECONDS` for poses. The engine draws only base colour, normal, occlusion, and emission, so judge the look here,
   not in Blender.
5. Ship it, with the `scaffold` skill's steps for a model (`assets.md`): a `Declare` line, a row in
   `src/Game/Models/SOURCES.md` naming the recipe and every source it downloads with its licence, and its budget class
   in the game's test of its models (`GameModelsTests`, through `ModelChecks`). Run `tools/models/build.py verify`,
   then commit the recipe, the `.glb`, and its `.textures/` folder together.

## What the build holds a model to

The export settings, the scene checks, and `tools/ModelCheck` hold every model to what the engine reads. A build with
any problem exits 1.

| Rule | Why |
| --- | --- |
| No Draco or meshopt compression, no KTX2 or Basis images | The engine refuses them by name. |
| Modifiers applied, metres, +Y up, the model's front towards +z (Blender's -Y) | The engine reads the file as it is. |
| At most four bone influences a vertex, normalised; no vertex with none | The engine keeps four, and the glTF reader refuses a skinned vertex that weighs nothing. Blender's automatic weights fail with only a warning, so the checks count unweighted vertices. |
| One armature, no morph targets, keys only on bones | The engine poses one skin and draws no morphs. |
| Only the clips named are exported, each starting at time 0 | Blender's exporter writes every action it finds. |
| Named materials, joints, clips, and points; no two alike | A game finds them by name. |
| Tangents with a normal map | The engine makes its own tangents another way, which differ at mirrored UV seams. |
| No texture tiling at export (bake instead) | Tiling exports as a texture transform, which the engine does not apply. |
| Within the class's budget: triangles, joints, materials, texture size, height, rig, clips, points | The game's numbers. |
| Each reference outline matched to at least `MATCH` | The shape stays true to what the user asked for. |
| Each clip in `LOOPS` ends in its first pose, with no jolt at the seam; no foot of `FEET` slides in a clip of `PLANTED` | A clip that jumps or skates shows at every repeat. |

Open edges only warn, since a leaf card or a banner may need them. Edges shared by three or more faces are a problem.

## What goes well and what does not

- **Hard-surface parts** (crates, weapons, armour plates, tower parts) built from boxes, cylinders, bevels, and
  booleans come out well. Name their points (muzzle, grip) with the points helper.
- **Organic shapes** (creatures, faces, bodies) come out plain and lumpy from code alone. Build them against reference
  outlines with a `MATCH`, block the form out first, and ask the user to look at the engine's pictures before going on.
- **Weights:** copy them from a body that is already weighted where you can (armour, clothing, a new body on the
  game's rig). Use rigid parts, each on one bone, for mechanical things. Use automatic weights on one closed proxy
  mesh, then copy them to the real mesh, for a creature.
- **Motion:** mechanical, cyclic, and stylised motion keyed in code works (a lid, a gun's recoil, a creature's gait).
  Name the clips that loop in `LOOPS`, and the feet and the clips they stand or walk in in `FEET` and `PLANTED`: the
  build then fails a loop that does not close and a foot that slides. A clip's speed in `PLANTED` is its own speed
  over the ground, not a body's tuned speed. Give `None`, so the build takes the speed its feet show and prints it.
  The game keeps that number in one place, and its test compares it with the clip (`ClipGround` in `tests/Shared`).
  Natural human motion does not come out well from code: take it from motion capture and retarget it to the game's
  rig.
- **Licences:** use CC0 sources (Poly Haven, ambientCG, CMU motion capture) or CC BY ones with credit (100STYLE).
  Models and clips from SMPL, AMASS, or HumanML3D are non-commercial, and so are the motion models trained on them; do
  not use them. Mixamo's terms bar shipping its raw files. Files bought for a game stay in the game's private
  repository, never in Itera.
- **AI-generated meshes and textures** are the user's decision, not part of this toolkit. Do not add them without it.

## When something fails

- **A Python error in Blender:** read the traceback. An AttributeError or TypeError on an API name usually means the
  name changed: look it up in `blender-api.md` and the local reference.
- **A checker problem:** its message names what the engine would do. Fix the recipe, not the file.
- **A silhouette under `MATCH`:** compare the view sheet with the reference. Look for proportions first, then the
  outline. Check that the reference is the view the recipe names (`tools/models/itera_models/silhouette.py` gives each
  view's direction).
- **The engine draws it wrong** (dark, inside out, lit oddly): look at the normals and the winding in the wireframe
  row. Look at the materials' names and the base colour texture. Run `views --world PLACE` to see it in the game's
  light.
- **`verify` differs:** a recipe, a helper, or the pinned Blender changed the output. Look at the model, then build it
  again and commit the new file.

## The pinned Blender

The toolkit refuses any Blender but the version in `tools/models/blender.json`. To move it, install the new version,
run `tools/models/build.py selftest` and `verify`, update `blender-api.md` from the new release notes, and change the
pin only when both pass.

## Enforcement

`.claude/hooks/skill_guard.py` denies file writes under `tools/models/` and `src/Game/ModelRecipes/`. It also denies
Bash commands that name either folder, unless the command only reads. The denial lasts until the session transcript
shows this skill was loaded: a `Skill` call for `models`, or a typed `/models`, after the last `/clear`. A denied call
names the fix: load this skill, then retry. A subagent loads the skill itself.
