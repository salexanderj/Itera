# Models built by recipes: the record

Written 2026-10-22. A record of a decision and what was built and measured; it is not kept current. How the tools work
now is in `.claude/rules/models.md`, the `models` skill, and `tools/models/README.md`.

## The question

The user wanted Claude to make a game's 3D models, their rigs and animations, and their textures. Three ways were
weighed: Claude driving Blender interactively through a Blender MCP server; Claude writing Python recipes that Blender
runs with no window, kept in the repository; and a modelling tool of Itera's own, made for agents.

## What the research found (October 2026)

- Benchmarks of code written for Blender (3DCodeBench, May 2026) found that about 90% of failures were calls to API
  names that changed in Blender 5.0. Retries made the code run, but did not improve shapes. Judging one's own renders
  against a text prompt helped almost nothing. Measuring against a target the agent could look at from chosen views
  helped a great deal (3DHarnessBench, September 2026).
- One project built the same asset through an MCP server and through a script with no window and got the same file,
  byte for byte. The projects aimed at game assets built through scripts and used the server only to look.
- No verified example was found of an agent making a rigged, skinned, textured, animated character and exporting it to
  a game.
- A tool of Itera's own would have to rebuild mesh operations, UV unwrapping, baking, weighting, and animation editing
  before it beat Blender. No source measured that cost.
- The open motion models trained on SMPL, AMASS, or HumanML3D are non-commercial. Hunyuan's licence excludes the EU, UK,
  and South Korea. The AMD RX 5700 here runs no local 3D generation model.

The full report, its notes, and the Blender probes are outside the repository, with the user.

## What was decided

- Recipes run by Blender with no window are the way models are built. The user chose this on 2026-10-22, with most of
  the tooling in Itera for every game on it.
- The official Blender Lab MCP server is not used; a local copy of the pinned Blender's API reference, built from
  Blender's own generator, takes its place for looking up names.
- The engine checks files in one place, through its own importer. The engine draws models for judging, on a neutral
  stage by default and in the game's world on request.
- Named points read from glTF empties, and extra models named on the command line, were added to the engine. Drawing
  metalness and roughness, reloading models during play, and morph targets were left for later.
- Blender is pinned to 5.2.2, and the snap is held there.
- AI-generated meshes and textures are the user's decision and not part of the toolkit.

## What was built

| Commit | What |
| --- | --- |
| `5b942b2` | `ModelInspection` and `ImportFindings` in the engine, `ModelBudget`, and `tools/ModelCheck` |
| `3748806` | Named points (`MeshPoint`) read from glTF empties |
| `1144c1c` | `Engine:Assets:Extra`: models read without a rebuild |
| `539852d` | `viewpoint`, and `meshes place NAME at X Y Z YAW` |
| `275d8f0` | `tools/ModelView`, the neutral stage, and its tests |
| `135be54` | `MeshAssets.Files`, `ModelChecks`, and the starter's `GameModelsTests` |
| `d7c7c33` | `tools/models`: the runner, the helpers, `views`, `docs`, `verify`, and `selftest` |
| `ea79011` | The starter's crate, built by a recipe |

## What was measured

On the development machine (Ryzen, 16 threads, Radeon RX 5700, Linux), with Blender 5.2.2:

- Blender starts in about 1.3 s. A view sheet of six views and a wireframe row takes about 0.3 s.
- The crate (572 triangles, three 512-texel maps baked on the CPU) builds, checks, and compresses in about 15 s, and
  rebuilds byte for byte.
- `ModelView` starts, draws two screenshots, and quits in 1.6 s. `build.py views` draws six views and tiles them in
  under 10 s; in the starter's world, through `game.py`, in about 10 s.
- Baking a 7,872-face mesh's colour, normal, roughness, and occlusion at 1024 texels took 3.7 s; at 2048, 14.6 s.
- `selftest` runs 131 tests and two builds in about 15 s.

## Open

- A first trial of one asset of each kind (a prop, a weapon with points, a humanoid variant, a creature, a clip, a
  baked material), timed and checked against reference images. The games have no reference images yet.
- Metalness and roughness are read but not drawn by the engine, so a metal's look cannot be judged in the engine yet.
