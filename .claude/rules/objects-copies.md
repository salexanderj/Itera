---
paths:
  - "src/Itera.Engine/Objects/{ObjectCopies,ObjectShareGrid}.cs"
  - "tests/Itera.Engine.Tests/Objects/ObjectCopiesTests.cs"
---

## Voxel objects: far copies and drawing

Kinds, placement, and play are in `.claude/rules/objects.md`.

### Far copies

`ObjectCopies` (internal) is made at the world's `Initialize`, before its workers start, which seals the kinds. From the
hand-over level up, a kind is copied into the world's voxels at each level whose voxels are no wider than the kind's
`CopiesUpToCentimetres` (`Register`'s third argument). With 0, the default, the limit is half its box's widest side, so a
smaller thing is not copied where its copy would be one voxel or less.

A copy keeps its object's size at every level:

- The kind keeps, for each copied level, a grid of blocks half a world voxel across over its box (`ObjectShareGrid`, made
  from the parts' voxels by `ObjectShareBuilder`, `VoxelObjectKind.SharesFrom`).
- Each block holds the mean density of the kind's voxels in it, 0 to 255, and their commonest solid material.
- A world voxel's share of an object (`ObjectCopies.Shares`) is read at its eight half-size cubes' middles, turned into
  the object. A cube with nothing, or all full, within a block of each is that (`ObjectShareGrid.AroundAt`). Otherwise
  the share is the mean of its 64 quarter-size cubes' middles, so a turned grid does not alias.
- A voxel whose middle has nothing within two blocks is passed over (`AnyNear`).
- The voxel's material is that of the fullest of the eight.
- A thin upright part, such as a trunk or a pole, is kept a voxel wide (`ObjectCopies.KeepThin`). A voxel with a share of
  at least `ThinShare`, none of its eight neighbours across greater (ties all kept), and some share above and below it, is
  made solid at `KeptShare`. Far trunks never vanish, and caps and rims do not grow.
- `ObjectCopies.Materials` holds the kinds' materials.
- Headings turn through `ObjectTurns`: cosines and sines of the deterministic series in 16-bit fixed point. `ToObject`
  serves the world's integers and `Rotation` serves drawing. Every peer writes the same voxels.

A column job at a copied level (`ColumnJob.Run`) writes the copies:

1. It asks the placer for the objects near the column (`Find`, each worker its own list).
2. It widens its layers to the copies' heights, as the edits' are. An empty range keeps its boundary.
3. After the generator's fill and before the edits, it writes each object's shares into the air of its mixed layers and
   its layers of air they reach. The range is the object's box turned by its heading and a voxel round it (`RangeOf`).
4. From half the maximum share an air voxel turns solid, of the object's material and at its share as its density. Under
   half, an air voxel's density rises to the share, so the surface lies where the object's does.
5. Ground stays. Where two objects fill one voxel, the first in the placer's order keeps it. A layer of one solid
   material keeps it.

- A copy's share under half beside the ground lifts the ground's surface by under a voxel where an object meets it, such
  as a trunk's foot. It does so also in a stitched seam just below the hand-over level, where the models are drawn over it.
- Raise `ObjectCopies.RuleVersion` with any change to how copies are written.
- The far copies take the world's edits at its coarse levels as any voxel does. The host clears a removed part's copies
  (`.claude/rules/objects-damage.md`).

### Drawing

`WorldMeshes.Objects.cs` draws objects. Each pass records the level-K columns it split below K (`CameraSplit`, whose
columns the near cascade and the image views share; `FarSplit`).

- A level-K column splits below K only once the areas whose objects reach into it are filled (`ObjectWorld.IsKnown`) and
  the kinds' models are on the GPU (`VoxelObjects.AllUploaded`; `WorldMeshes.ObjectsReady`, in `Select` and the balance's
  `CanSplit`). Otherwise it draws its copies, so no object goes undrawn between its copy and its model. A column not
  meshed itself splits regardless.
- An object's part models go into a pass's lists when a level-K column under its reach is split there. They go into the
  camera's when the column is known and nearer than the hand-over's fade's end. They are turned by `ObjectTurns.Rotation`
  about the object's anchor.
- An object's level follows its anchor's distance: its own voxels within level 0's reach times the mesh share, twice them
  within level 1's, else four times. Each level fades into the next over `LevelFadeMetres` each side of the distance
  between them.
- The coarsest level fades out over its far copy, over `HandOverFadeMetres` (`WorldMeshes.HandOverFade`). Where the
  hand-over's fade begins before level 2's band (a hand-over level of 1), every level drawn fades out over it. The fade starts at the farthest an object of a split level-K
  column can lie: the reach that splits it, the column's width's diagonal, and the widest kind's reach. The hand-over
  follows the distance alone, not the columns.
- A fade gives each draw a share of a screen-space dither's thresholds (`MeshModelDraw.Shown`, `mesh-fade.glsl`). The
  `MESH_FADE` variants of `mesh.frag` and `mesh-wire.frag` draw the camera's list's fading draws, written last. They draw
  only the pixels whose threshold lies in the share, and 0 is every pixel. Two levels fading into each other take the two
  sides of one share. No world shader discards anything.
- The copy stays drawn whole under the model. Where both draw, they show together, the copy's surface about at the model's.
  The copy and the model also show together nearer than the fade's band.
- The fades are the camera's. The cascades cast each object's one level by its distance, whole. The image views draw the
  coarsest level, whole.
- An object under a split column has no copy, so its model never fades out.
- Objects are gathered after the game's models and the retired ones, their areas nearest the camera first (an insertion
  sort: no allocation). An area is passed over when no column within the widest kind's reach of it is split and it lies
  past the fade's end.
- When a list's draws need more records than it holds, every model level takes the same share, the first gathered first.
  So neither a kind nor a part of one, such as a tree's crowns, drops whole.
- The draws' tint is below 0 (`MeshModelDraw.Tint`). `mesh.vert`'s model variant and `mesh.frag` shade it as the world's
  surfaces: the mesh's material weights and palette, the texture sets, the tint map at the point, and the world's look. A
  model and its copy match.
- A stitched block of the level below K reads the coarser block with the objects' materials made air (`MaskCopies`), so no
  copy shows in its seam. The objects' materials should be their own, not the ground's.
