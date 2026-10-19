---
paths:
  - "src/Itera.Engine/Objects/{ObjectDamage,DamagedObject,ObjectEditing,ObjectLabeller,FallenParts}.cs"
  - "tests/Itera.Engine.Tests/Objects/{ObjectDamageTests,ObjectDamageMatchTests}.cs"
---

## Voxel objects: damage, cuts, and fallen parts

Kinds, placement, and play are in `.claude/rules/objects.md`.

### Damage

`ObjectDamage` (scoped with terrain, `MatchVoxels.Damage`) is initialised at the "objects" step after the objects' world
and updated after it each loop iteration.

The world hands each edit it logs to it (`VoxelWorld.ObjectEdits`, `IObjectEdits`). It applies the edit at once, in the
log's order, to the objects of the filled areas that the edit's bounds reach.

- Which objects an edit reaches follows the log's index: an object is reached where the log indexes the edit in a level-0
  column under the object's reach (`VoxelWorld.IndexesUnder`, which is what a replay finds). An edit wholly past the
  map's sides, or above or below its height, changes no object.
- `ObjectEditing` does the work with integer maths through `ObjectTurns.FromObject`. Each voxel's sample points are turned
  into the world's centimetres and read by the edit's own `Contains`.
- Clears, clears of some materials, sets of air, and lifts take a voxel by the share they cover
  (`VoxelEditing.ChangeByShare` on `VoxelEditing.Parts` of it). Lifted object voxels go, not into the piece.
- A clear of some materials covers boxes and spheres (`VoxelEdit.MaterialsSphere`). A crater of the ground's materials
  leaves objects of their own whole.
- A paint recolours a solid voxel whose middle it covers. A paint of air changes no object.
- Fills, sets of a material, and pieces put back change only the world, so objects never gain voxels.
- An object that holds none of a clear's materials is passed over unread, live and on replay. Its materials are its
  kind's (`VoxelObjectKind.Materials`) and any a paint gave its copy (`DamagedObject.Materials`).
- A voxel a clear of some materials cannot change is passed over before its share is counted. So is a voxel whose sample
  points' box misses a sphere. Neither changes a result.
- Whether an edit changes an object at all is answered at its first changed voxel.
- A clear with a maker counts what it takes from objects, in cubic centimetres, into the game's `IVoxelRemovals` with the
  world's. It counts before anything changes. It does not count an object whose copy the cap dropped, which may differ
  between peers.

An object an edit changes keeps a `DamagedObject`:

- Its changed 8-voxel bricks over its kind's voxels, each copied whole at its first change. It is read through
  `IObjectVoxelSource`, as the kinds and shapes are.
- Its parts' models, made again once an update, at most `PartsPerUpdate` parts, in the order the objects changed. The
  buffers they are made in are made at `Initialize` where the run has kinds.
- Each part model is built from the changed voxels by the kind's rule (`PartLevel.Build`'s sampling). Level 0 is the
  kind's with the changed bricks over it. The coarser levels are sampled again over each changed brick. A part whose
  border holds a changed brick is built with it.
- Each is a model of its own owning its kind's window. The old trees are freed after `VoxelModels.Replace`. It is drawn
  once on the GPU (`ObjectDamage.PartsOf` gives the kind's until then).
- It shares its kind's part's meshes for every 32-voxel block of each level its changes do not reach
  (`VoxelModels.ShareBlocks` with `DamagedObject.ChangedBlocks`). A block is untouched when none of its reads, apron
  included, holds a voxel of a changed brick (`ModelBlocks.Reading`). A damaged part meshes, and holds in the mesh heap,
  its changed blocks alone.

Replay and the cap:

- When an area fills, each of its objects replays the edits since the last reset that reach it
  (`VoxelWorld.CollectEdits`, the log's columns under its reach). Damage made before the area loaded, before the match
  (a save, a joiner's download), or while its copy was dropped shows the same bit for bit.
- When an area drops, its objects' copies go. A reset drops every copy.
- At most `Engine:Objects:MostDamaged` objects keep a copy. Past it, the copy of the damaged object farthest from every
  streaming centre goes, with a Warning once a match. It is made again by replay, `RebuildsPerUpdate` an update, once it
  is nearer than the farthest kept.
- The cap counts objects, not bytes. A copy is its changed bricks and its rebuilt parts' models.

Readers keep what they read until `ObjectDamage.ReadChanges` names the object with a box. The names come in a ring of
`ObjectDamage.KeptChanges` changes. Past the ring, readers read everything again.

- An edit's change is the edit's bounds and two of the object's voxels and 2 cm round them, within its reach. So a crater
  in a long wall has its readers read the columns round it again, not the wall's.
- A copy placed, removed, dropped, healed, or cut changes its whole reach.
- The mover's chunks under the box are made again.
- The statics of the kind's parts that the box reaches across x and z, at every height (`VoxelColliders.ReachedParts`), are
  swapped. A part that holds a changed brick gets a compound of its own voxels. A part with none gets the kind's. A
  part's untouched static is not swapped, so a body asleep on it stays asleep.
- Rays read the damage as they go.

`ObjectEditing.RuleVersion` is hashed into the kinds' fingerprint, so into the match's identity, with the copies' rule.
Raise it with any change to what an edit does to objects.

### Cuts

Cuts are the world owner's: a session's host, or an offline game. A client follows the host's removals.

- Each edit that takes voxels from an object marks it. A replayed edit does too, when its area fills or its dropped copy is
  made again, so a cut made meanwhile is looked at.
- Once an update, `ObjectDamage` hands at most `AsksPerUpdate` marked objects' snapshots to `ObjectLabeller`. A
  `LabelSnapshot` holds the object's kind, the copy it was taken from, and a copy of its changed bricks. An object waits
  until the areas of the objects whose copies may reach its own are filled.
- `ObjectLabeller` runs on a thread of its own. An exception there answers no pieces and is logged at Error once a match.
- It splits the object's solid voxels into pieces joined across faces, of any material, leaves too. It keeps pieces by the
  kind's rule (`ObjectRoots`, `Register`'s fourth argument):
  - `Ground`, the default, keeps the pieces holding a voxel at the object's foot, its bottom at or below its anchor. A
    dig through every foot voxel fells the whole object. A piece of the kind's own voxels joined to no foot voxel falls at
    its first cut, as build kinds do. A kind with no foot voxels keeps its largest piece.
  - `Largest` keeps the largest piece, the first of two as large.
- The answer counts when it comes back for the copy it was taken from and the object has not changed since
  (`DamagedObject.Revision`). Otherwise the object is asked again. A copy made since, after a reset or a drop, is asked
  anew.
- Each piece that falls is logged as a removal: `VoxelEdit.RemovePart`, with the piece's first voxel in scan order as its
  seed and its bounds turned into the world's centimetres as its box.
- Then come the clears of the far copies that those pieces alone filled. At each level the kind is copied at, the object's
  shares (`ObjectCopies.Shares` and `KeepThin` over its whole `RangeOf`) are computed twice: as the copy was written,
  from the kind's grid that the column jobs read, and as it would be without the pieces, from a grid of the snapshot's
  voxels less the pieces', made by the kind's rule (`ObjectShareBuilder`).
- A world voxel is cleared when it lies within a voxel of the pieces' box turned into the world, is solid as written and
  not without them, and is solid in no neighbour's copy. The neighbours are the objects whose reach and a coarsest copied
  voxel meet the object's, each read over its part of the box. A voxel an earlier cut cleared may be cleared again.
- Each clear is an edit of its own: `VoxelEditOperation.ClearCopies`, made by `VoxelEdit.ClearCopies(level, box, size,
  materials)`. It names one world level, in its `Material` byte. The kind's materials below 128 are the `ClearMaterials`
  set. It works by sample point, in boxes of runs along x of one row. Objects, other levels, and navigation ignore it
  (`VoxelEdit.AppliesAtLevel`).
- The kinds' tables are made at the "objects" step (`VoxelObjectKind.MakeTables`) for the thread to read them.

### Parts cut off an object

A part cut off an object goes by an edit of its own: `VoxelEditOperation.RemovePart`, made by `VoxelEdit.RemovePart(id,
seed, box, size)`.

- The object's id is in `Piece`. The kind's seed voxel is in `Turn`, 16 bits an axis. The box is air where the part lay,
  for the log's index.
- Each seed axis is below `VoxelEdit.MostSeed`. `VoxelEdit.RemovePart` throws otherwise.
- The world's voxels never change (`VoxelEdit.ChangesWorld` false). Columns do not replay it, and navigation reads the
  cells under its box again.
- `ObjectDamage` takes from the object every solid voxel joined to the seed across faces, the same on every peer. It takes
  nothing for another object, a box missing its reach or height, or a seed of air or outside the kind. A copy the cap
  dropped takes the removal at its replay.
- Applied live and not replayed, the voxels go to `ObjectDamage.Fallen`, with the air voxels round them that hold density,
  the soft side of their surface.

### Fallen parts

`FallenParts` (scoped, `MatchVoxels.Fallen`) runs in the match's fixed step after the fixed group. It makes a cut body of
each part that fell. A part of fewer than `FallenParts.FewestVoxels` solid voxels just goes.

- It makes a model for each of the kind's parts that it holds solid voxels of. The model has its border's voxels, owns its
  own window's faces and a low side's where no part of it lies beyond, and has three levels with densities by the kind's
  rule. It is placed and turned where it lay.
- It makes one body (`LooseBodies.TryCreate(parts)`, shaded as the world, touching the voxels and the objects' statics).
  The entity has `LooseBody` and `CutPiece`. Room is made as for a thrown ball.
- Its density is `FallenParts.DensityKilogramsPerCubicMetre`.
- The body goes after it rests, or after it was made (`LooseBodySystem.CutRestSeconds`, `CutMostSeconds`).
- Each peer makes its own bodies. None is made where its copy was dropped or its area is not filled.
