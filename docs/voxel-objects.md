# Voxel objects finer than the terrain

Trees, bushes, rocks, and props as voxel volumes of their own, finer than the world's voxels (10 cm in the starter),
placed by the world generator, drawn as fine models near the camera and as coarse copies in the world's distant levels,
met by characters, bodies, rays, and navigation, damaged by edits, and cut into falling pieces. The plan's decisions
(Lucy, 2026-10-11), following decisions 8, 9, 15, and 16 of `rendering-redesign.md`:

- Objects are their own voxel volumes: not a coarse copy in the world with a fine look, and not a finer world grid.
- Near the camera an object is drawn as its fine model; farther, as a coarse copy written into the world's levels from
  a hand-over level K up (levels below K hold no object voxels).
- Objects are shaded like the ground (the world's texture sets and tint map), so a model and its far copy match.
- A part cut off falls as a cut body each peer makes, gone 30 to 60 s after it rests.
- Removing a part is a new edit operation: save format 7, protocol 39.
- Objects turn to any heading about the vertical, in fine fixed steps.
- A kind's voxels are any whole centimetres under the world's voxel.
- In this item: forests in the starter, navigation that sees objects, objects in image views, and counts of dug object
  material.

## The prototype (phase 0)

A throwaway prototype, built apart from `main` and not merged, measured what objects cost before any of them is built:

- **The tree.** One kind built in code: a 9 m tree 6 m wide, with a trunk 48 cm across at its foot and a crown of ten
  clumps whose outer 20 cm are a third holes, for a rough, leafy surface. Bark and leaves had plain colours.
- **Parts.** At 2, 4, or 5 cm voxels the tree is 18, 5, or 2 models of up to 128 voxels a side, each with three
  levels: its own voxels, twice them, and four times them.
- **Placement.** One tree in each 8 m cell of a jittered grid, at a chance of 0.7 (about one every 91 m²), over the
  whole map, with none within 15 m of the start.
- **Far copies.** The starter's generator wrote each tree's voxels, sampled at the world's sample points, into the
  world's levels K to 5 (3.2 m voxels). Below K the world held no tree voxels.
- **Fine models.** A frame system drew the fine models where the renderer had split a level-K column under a tree
  below K. A model drew at its own voxels within 17 m, twice them to 34 m, and four times them beyond.

Measured on the RX 5700 XT at 2560x1440 in a Release build, offscreen, with 4x MSAA, shadows on, and dynamic render
scaling off. Each figure is the mean over 10 s once the view had loaded, at the starter's `start` and `high` views. The
run without trees matched the figures measured for the 8 km view (1.85 and 3.00 ms) within 1 %. K=1, 2, and 3 hand over
at about 53, 106, and 212 m.

| Trees | Hand-over | World pass, ms | Models, ms (part of it) | Shadow maps, ms | Mesh heap, MB | Model triangles at start |
|---|---|---|---|---|---|---|
| none | | 1.84 / 2.99 | | 0.15 / 0.08 | 284 | |
| 4 cm | K=1 | 2.94 / 3.19 | 0.55 / 0.00 | 0.42 / 0.14 | 411 | 0.69 M |
| 4 cm | K=2 | 3.17 / 3.34 | 0.82 / 0.21 | 0.51 / 0.15 | 374 | 1.28 M |
| 4 cm | K=3 | 3.60 / 3.99 | 1.13 / 0.74 | 0.69 / 0.31 | 345 | 2.73 M |
| 2 cm | K=2 | 7.36 / 4.12 | 3.20 / 0.81 | 2.34 / 0.35 | 439 | 9.39 M |
| 5 cm | K=2 | 2.79 / 3.28 | 0.57 / 0.16 | 0.36 / 0.14 | 369 | 0.73 M |
| 4 cm, smooth crowns | K=2 | 2.54 / 3.25 | 0.38 / 0.15 | 0.32 / 0.14 | 369 | 0.66 M |
| 4 cm, copies to level 4 | K=2 | 3.16 / 3.33 | 0.82 / 0.21 | 0.52 / 0.15 | 357 | 1.28 M |
| 4 cm, copies to level 3 | K=2 | 3.12 / 3.31 | 0.81 / 0.21 | 0.51 / 0.15 | 336 | 1.28 M |

- **Per kind.** The tree is 4.29 million voxels at 2 cm, 536,000 at 4 cm, and 275,000 at 5 cm. Building its shape took
  929, 118, and 62 ms, and encoding its models 122, 28, and 17 ms.
- **Mesh memory per kind.** With no far copies, the 2 cm kind's meshes took about 65 MB of the mesh heap. The 4 and 5 cm
  kinds' took less than the heap's spread from run to run (about 5 MB).
- **Far copies.** At this density, copies up to level 5 took about 90 MB of the mesh heap, up to level 4 about 73 MB, and
  up to level 3 about 52 MB. A level-k leaf is drawn from about 16.8 x 2^k m to at most about 53 x 2^k m from the
  camera, so the forest then reaches at least 540, 270, or 135 m.
- **Voxel memory.** The far copies added 6 to 9 MB of voxel memory (154 MB without trees, 160 to 163 MB with them).
  Column jobs averaged 11 to 12 ms with or without trees, and loading took 2.0 s without trees and 2.1 s with them.
- **The high view at K=1.** From 40 m up, no column near the camera splits below level 1, so no fine model is drawn: the
  hand-over follows the distance in three dimensions.

What the pictures showed:

- **The three hand-over distances look much alike** from the start and from 30 m up. The step that shows is colour: the
  flat-coloured models are lighter than the world-shaded copies. Shading objects like the ground removes it.
- **A tree half in a split column** first drew partly, because its model followed only the column under its trunk.
  Drawing the model where any level-K column under the tree is split fixed it, as the plan says.
- **Thin trunks are lost.** At 20 and 40 cm the copies' sample points miss the 48 cm trunk under some trees, and their
  crowns float.
- **Part borders show.** Each part is meshed alone, so its surface closes at the part's border: a line runs round the
  crowns every 2.56 m at 2 cm and at 6.4 m at 5 cm. Parts must be meshed with their neighbours' voxels round them.
- **Smooth crowns show steps.** Models keep no densities, so a smooth crown shows the voxels' steps as rings. The leafy
  surface hides them, and costs about twice the GPU time.
- **Damaged copies cost memory.** A damaged copy of one part at 4 cm costs about a part's meshes (an estimate: 2 to
  3 MB), so a hundred damaged trees near the player would take 200 to 300 MB of the heap.
