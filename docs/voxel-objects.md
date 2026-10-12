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

## The choices after the prototype

Lucy, 2026-10-11, from the figures and pictures above:

- **Hand-over at K=2.** Fine models reach about 106 m, and 40 cm copies take over past them.
- **The starter's trees at 5 cm.** A kind may use any size under the world's voxel. At 2 cm a tree costs nearly twice
  the world pass's budget, so 2 cm suits only small objects.
- **Damaged objects share meshes.** A damaged object meshes again only the 32-voxel blocks an edit touched, and shares
  the rest with its kind.
- **The world pass stays within 4 ms with objects** in every starter view, forest views included, and the mesh heap
  within its 512 MB.
- **Far copies keep every coarse voxel the object touches,** from a coarse grid each kind keeps for each level, so thin
  trunks stay and far crowns grow by up to one coarse voxel.
- **Objects keep densities like the world's.** Kinds store how full each voxel is, models are meshed with them, and edits
  change them by share, so smooth rocks and crowns show no rings.

## After playing the forests

Lucy, 2026-10-12, after walking the starter's forests: whole areas of far objects shrank or grew at once as she walked
(each level-K column switching between its copies and its models), and the boulders were still the ground's voxels.

- **Boulders are objects:** lumpy, 5 cm, four sizes (2, 3, 4.5, and 6 m) of two shapes each (world version 4).
- **No column splits below K until its objects are ready:** known and their models on the GPU, so no object goes
  undrawn between its copy and its model.
- **Far copies keep the object's size:** each coarse voxel takes the share of it the object fills, at its heading, and a
  thin upright part (a trunk) is kept one coarse voxel wide so it never vanishes. Before, a copy kept every coarse voxel
  the object touched, so a far crown grew by up to one coarse voxel.
- **Dithered fades:** a model's levels fade into each other over 2 m each side of the distance between them, and the
  camera draws an object's coarsest model over its copy out to the hand-over's fade's end, fading it out over its last
  30 m; the copy stays drawn whole under it.
- **No lit strip at an object's foot.** The shadow lookup's depth bias (0.0003 of a cascade's depth, about 5 cm in the
  near cascade) lit the ground within it of a caster's shaded side. Terrain boulders hid it, their voxels curving into
  the ground; an object meets the ground at a sharp crease, so a bright strip showed between its foot and its shadow.
  The bias is gone (the 1.5-texel normal offset stays): the strip closes, and the forest, start, and boulder views under
  a 45, 20, 15, and 10 degree sun show no acne, the terminators a little sharper.

## What the drawing leaves

- **Copies and models together.** Nearer than the hand-over's fade's end, an object in a column that did not split
  shows its model and its copy together. The copy's surface lies about at the model's: a lumpy model and its smoother
  copy cross each other in places, and a far trunk's copy, a coarse voxel wide, is wider than its model.
- **Shadows do not fade.** The cascades cast each object's one model level, chosen by its distance, whole, where its
  column split; elsewhere its copy casts. A shadow can change by up to a voxel where an object passes from one level to
  the next.
