---
paths:
  - "src/Itera.Engine/Objects/{VoxelObjectKinds,VoxelObjectShape,VoxelObjects,ObjectWorld,ObjectPlacement,ObjectTurns,ObjectVoxelSources,ObjectColumnRuns}.cs"
  - "tests/Itera.Engine.Tests/Objects/{VoxelObjectKindsTests,VoxelObjectShapeTests,VoxelObjectsTests,ObjectWorldTests,ObjectPlacingTests,PlacingGenerator,ObjectTurnsTests}.cs"
---

## Voxel objects: kinds, placement, and play

Trees, bushes, rocks, and props of voxels finer than the world's (`src/Itera.Engine/Objects/`, namespace
`Itera.Engine.Objects`). The choices and measurements are in `docs/voxel-objects.md`. Voxel objects are split over these
rules files:

- This file: shapes, kinds, the models, the objects the world places, and placing and removing objects during play.
- `.claude/rules/objects-damage.md`: edits that damage objects, cuts, and fallen parts.
- `.claude/rules/objects-copies.md`: the far copies in the world's coarse levels, and how objects are drawn.
- `.claude/rules/objects-meeting.md`: how characters, rays, bodies, and navigation meet objects.

### Shapes and kinds

A game builds each kind's voxels in a `VoxelObjectShape`: a box of voxels of its own size and the point it stands on.

- `UnitCentimetres` is the voxel size, finer than the world's. Each voxel has a material and a density.
- `AnchorCentimetres` is the point the object stands on, from the box's corner.
- `FillEllipsoid`, `FillCapsule`, and `FillBox` take centimetres from the box's corner. They write densities from the
  shape's distance to each voxel's centre, one half less the distance in voxels. A voxel is solid exactly when its
  centre lies inside, and the surface meshes smooth.
- Shapes join by the larger density. The voxel takes the material of the shape that fills it more.
- `Set` and `Clear` change one voxel. `Set` fits the density to the material: a solid material's is one half or more.
  A smooth shape written voxel by voxel sets air where its share is under one half, or its surface moves out to the
  voxels' centres.
- Build with integer maths, + - * /, and square roots only, as generators do. Every peer must build the same voxels.

`VoxelObjectKinds` (run-wide, registered in `AddIteraEngine`) takes kinds by name at the game's `IRunStartup`.

- A run holds at most `VoxelObjectKinds.MaxKinds` kinds. `Register` and `TryFind` use a `VoxelObjectKindId`. `Register` also takes `CopiesUpToCentimetres` and the kind's
  `ObjectRoots`, described in the files for copies and damage.
- Each match's start seals the kinds for the world's grid. A kind whose voxels are not finer than the world's stops the
  start. A kind needs a solid voxel.
- A kind is split into parts (`VoxelObjectKind.Parts`, empty ones left out). Each part is kept as three levels of 8-voxel
  bricks: its voxels, twice them, and four times them.
- A coarser voxel holds the mean density of those it covers. It is solid from one half. Its material is the one at its
  sample point where that is solid, else the commonest solid one.
- A part owns at most `ObjectPart.OwnVoxels` voxels a side. It holds a border of its neighbours' voxels round it
  (`BorderVoxels`; fewer at the coarser levels: the mesher reads two past an edge for a vertex's normal and openness).
- A part's model box is its own voxels plus two borders (`ObjectPart.BorderVoxels` at level 0, fewer at coarser levels).
- Each part model owns the faces of its own voxels alone (`VoxelModels.SetOwned`, with `OpenLow` 1 on a side with no part
  beyond, which owns the faces against its border). The mesher's owned window is `SurfaceNets.Mesh(samples, densities,
  ownedMin, ownedMax, output)`. Parts meet with no seam, and each face is made once.
- `Fingerprint` stands for every kind's name and voxels in order.
- Kinds read a voxel through their parts' brick tables (`VoxelObjectKind.Voxel`). `VoxelObjectKind.MakeTables` makes the
  tables for the labeller thread, at the "objects" step.

### The models and the columns' runs

`VoxelObjects` (scoped, with voxel terrain; `MatchVoxels.Objects`) makes every part a model at the match's "objects" step,
after the voxel physics.

- A model has three levels with densities: `VoxelModels.Add(levels, size, 0, unit, densities: true)`. Each part's corner
  comes from the kind's anchor (`PartsOf`).
- `ObjectsLoadingTask` waits for the models on the GPU. It is done once they are made without graphics.
- It also makes every kind's columns' runs for the run's passable materials (`VoxelObjectKinds.MakeColumnRuns`). Navigation
  calls it first, before its thread starts, and logs its time at Debug. The "Objects:" line gives the time left for this
  step, and nothing where navigation made them all.
- `ObjectColumnRuns` holds, for each of the kind's voxel columns, the runs of its blocking voxels and of all its solid
  voxels. They are made from the parts' bricks, a brick of one material at once.
- The kind keeps them for the run (`VoxelObjectKind.ColumnRuns`, one for each set of passable materials asked for). They
  are made under a lock and read on any thread.

### The objects the world places

A world places its objects through its generator: `IChunkGenerator.Objects`, an `IObjectPlacer`, null for none. A
`HeightfieldGenerator` subclass overrides its virtual `Objects`, because a property of its own would not stand for the
interface's. A placer is thread safe and pure like `Fill`. A change to its placements raises the generator's `Version`.

- `Find(seed, box, found)` adds every `ObjectPlacement` whose anchor lies in a half-open box, in a fixed order.
- A placement holds its `ObjectId`, its kind, its anchor at absolute centimetres, and its heading in steps of a turn over
  `ObjectPlacement.HeadingSteps`. The id is the same on every peer: `ObjectId.Of` a jittered point.
- A placer's unknown kind or heading, or an anchor outside the area asked for, stops the fill.

`ObjectWorld` (scoped, with voxel terrain; `MatchVoxels.Placed`) keeps the objects of areas a level-K column wide. K is
`Engine:Objects:HandOverLevel`.

- It keeps an area's objects while any of the area's columns below level K is resident.
- It follows the world's change list after the world's update each loop iteration (`MatchVoxels.Update`). A resync after
  the change list overflows keeps the areas still wanted.
- It fills at most `FillsPerUpdate` areas an update, those nearest a streaming centre first.
- With a hand-over level at or past the world's levels, every streamed level counts.
- `Near(box, found)` gives the objects whose kind's reach meets a box within the map. The reach is
  `VoxelObjectKind.ReachCentimetres`, at any heading, and `MostReachCentimetres` is the widest. It gives false where an
  area under the box is not filled. The objects come in the areas' order, by z then x, and each area's own.
- `Near` reads only the area's cells of `IndexCellCentimetres` that the box meets. Each object is listed in every cell its
  reach meets. The list is made again at the first query after the area's objects change.
- `ObjectWorld.IsKnown` tells whether the areas under a box are filled. `ObjectWorld.Version` rises when an area is filled
  or gone.
- The match's identity (`GeneratorMatchIdentity`) carries the kinds' fingerprint after the generator's id, so games whose
  kinds differ do not join each other. The fingerprint holds the copies' rule (`ObjectCopies.RuleVersion`) and the
  damage's (`ObjectEditing.RuleVersion`). Saves and join payloads carry the same id (`VoxelObjectKinds.WorldId`, the
  log's generator id), so a save of other kinds is refused.

### Placing and removing objects during play

The world's owner, a session's host or an offline game, places and removes objects.

- `ObjectWorld.TryPlace(kind, anchor, heading, maker, out placement)` places an object. `CanPlaceAt` refuses a placing,
  among its checks, when the object's box would not be indexed under its anchor's area, or lies wholly above or below the
  map's height.
- `TryRemove(placement, maker)` removes a whole object, its own or the generator's.
- Each is an edit of its own: `VoxelEditOperation.PlaceObject` and `RemoveObject`, made by `VoxelEdit.PlaceObject` and
  `RemoveObject` from a placement and its kind's reach, bottom, and top.
- The edit holds a box of air over the object's reach across its anchor and its height. The id is in `Piece`
  (`ObjectId.Placed`: a hash of the anchor, kind, heading, and the edit's sequence, so a save's renumbering keeps it). The
  kind, the heading, and the anchor's height over the box's floor are in `Turn` (`VoxelEdit.PlacementOf`).
- The world's voxels never change (`ChangesWorld` false). The world checks each edit against the run's kinds as it takes
  it (`VoxelWorld.ObjectProblem`): the kind is one of the run's, not copied, and the box is the kind's. A host's
  `TryApply` refuses a bad one, and a client's world throws, which ends its session.
- `ObjectWorld` keeps objects wherever the run has kinds, with a generator's placer or not (`IsEnabled`, known before its
  `Initialize`). The match's identity carries the kinds' fingerprint wherever the run has kinds.
- A live placing or removal goes into or out of the filled area holding the anchor at once (`TakePlacing`, after the
  area's others). `ObjectDamage` frees a removed object's copy and names the change (`Record`) for the mover's columns and
  navigation, and `SetVersion` rises, which the colliders follow.
- An area's fill reads placed objects from the log after the generator's objects, in the log's order (`AddLogged`).
- A placed object takes the log's edits from its placing on (`ObjectPlacement.Since`), live and on replay. A fill reads its
  area's edits once for all its objects.
- A reset fills every area again at once (`RefillAll`).
- Far copies come from the generator's placer alone. A kind copied into the world's coarse levels is neither placed nor
  removed by an edit (`ObjectWorld.CanPlaceOrRemove`). A game whose view ends before the hand-over to copies registers its
  kinds without copies, with `Register`'s limit under the coarsest copied voxel.

### Models with a voxel size of their own

- `VoxelModels.Add` takes a unit (`UnitCentimetres`, `VoxelMetres`) and keeps densities (`Encode(voxels, densities,
  extent)`, `DecodeDensities`, `HasDensities`), which the renderer meshes the model with.
- `SolidModelVoxels.Add` and the cut parts' hulls and masses (`LooseBodies.TryCreate(parts)`) take the model's unit.
- `VoxelModels.Apply` edits materials only. An object's damage makes its models again (`ObjectDamage`).
