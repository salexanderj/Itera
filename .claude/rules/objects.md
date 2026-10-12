---
paths:
  - "src/Itera.Engine/Objects/**"
  - "tests/Itera.Engine.Tests/Objects/**"
---

## Voxel objects

Trees, bushes, rocks, and props of voxels finer than the world's (`src/Itera.Engine/Objects/`, namespace
`Itera.Engine.Objects`; the choices and measurements are in `docs/voxel-objects.md`).

- A game builds each kind's voxels in a `VoxelObjectShape`: a box of voxels of its own size (`UnitCentimetres`, 1 to
  100, finer than the world's), each a material and a density, and the point it stands on (`AnchorCentimetres`, from the
  box's corner). `FillEllipsoid`, `FillCapsule`, and `FillBox` (centimetres from the box's corner) write densities from
  the shape's distance to each voxel's centre (one half less the distance in voxels), so a voxel is solid exactly when
  its centre lies inside and the surface meshes smooth; shapes join by the larger density, the voxel taking the material
  of the shape that fills it more; `Set` and `Clear` change one voxel. Build with integer maths, + - * /, and square
  roots only, as generators do: every peer must build the same voxels.
- `VoxelObjectKinds` (run-wide, registered in `AddIteraEngine`) takes kinds by name at the game's `IRunStartup`
  (`Register`, `TryFind`, at most 4,096; `VoxelObjectKindId`) and is sealed at each match's start for the world's grid:
  a kind whose voxels are not finer than the world's stops the start. A kind is split into parts (`VoxelObjectKind.Parts`,
  empty ones left out), each kept as three levels of 8-voxel bricks (its voxels, twice,
  four times them): a coarser voxel holds the mean density of those it covers, solid from one half, of the material at
  its sample point where that is solid, else the commonest solid one. A kind needs a solid voxel. `Fingerprint`
  stands for every kind's name and voxels in order.
- `VoxelObjects` (scoped, with voxel terrain; `MatchVoxels.Objects`) makes every part a model at the match's "objects"
  step, after the voxel physics: three levels with densities (`VoxelModels.Add(levels, size, 0, unit, densities:
  true)`), each part's corner from the kind's anchor (`PartsOf`); `ObjectsLoadingTask` waits for them on the GPU (done once
  made without graphics).
- A world places its objects through its generator (`IChunkGenerator.Objects`, an `IObjectPlacer`, null for none; a
  `HeightfieldGenerator` subclass overrides its virtual `Objects`, since a property of its own would not stand for the
  interface's; thread
  safe and pure like `Fill`, so a change to its placements raises the generator's `Version`): `Find(seed, box, found)`
  adds every `ObjectPlacement` (its `ObjectId`, the same on every peer, `ObjectId.Of` a jittered point; its kind; its
  anchor at absolute centimetres; its heading in steps of a turn over `ObjectPlacement.HeadingSteps`, 4,096) whose
  anchor lies in a half-open box, in a fixed order. `ObjectWorld` (scoped, with voxel terrain; `MatchVoxels.Placed`)
  keeps the objects of areas a level-K column wide (K: `Engine:Objects:HandOverLevel`, 2 by default, 1 to 9), each while
  any of its columns below level K is resident, following the world's change list after the world's update each loop
  iteration (`MatchVoxels.Update`) and filling at most `FillsPerUpdate` (8) areas an update, those nearest a streaming
  centre first (a resync after the change list overflows keeps the areas still wanted); `Near(box, found)` gives the
  objects whose kind's reach (`VoxelObjectKind.ReachCentimetres`, at any heading; `MostReachCentimetres`) meets a box
  within the map, false where an area under it is not filled. A placer's unknown kind or heading, or an anchor outside
  the area asked for, stops the fill. With a hand-over level at or past the world's levels, every streamed level counts.
- Far copies (`ObjectCopies`, internal; made at the world's `Initialize`, before its workers start, which seals the
  kinds): from the hand-over level up, a kind is copied into the world's voxels at each level whose voxels are no wider
  than its `CopiesUpToCentimetres` (`Register`'s third argument; 0, the default, is half its box's widest side, so a
  smaller thing is not copied where its copy would be one voxel or less). Each copy is a lattice over the kind's box
  (`ObjectCopyGrid`, blocks a quarter of a world voxel, built once from per-block counts and box sums): a world voxel whose
  sample point lies in a block takes the commonest material of the kind's voxels it could touch at any heading (within
  half its diagonal across and half its edge up and down), else air, so every world voxel the object touches is solid
  and a thin trunk stays. A column job at a copied level (`ColumnJob.Run`) asks the placer for the objects near the
  column (`Find`, each worker its own list), widens its layers to the copies' heights (as the edits' are: an empty range
  keeps its boundary), and writes the copies into the air of its mixed layers and its layers of air they reach, whole,
  after the generator's fill and before the edits; a layer of one solid material keeps it. Headings turn through
  `ObjectTurns` (cosines and sines of the deterministic series in 16-bit fixed point; `ToObject` for the world's
  integers, `Rotation` for drawing), so every peer writes the same voxels. `ObjectCopies.Materials` holds the kinds'
  materials.
- The match's identity (`GeneratorMatchIdentity`) carries the kinds' fingerprint after the generator's id where the
  world places objects, so games whose kinds differ do not join each other; the fingerprint holds the copies' rule
  (`ObjectCopies.RuleVersion`: raise it with any change to how copies are written) and the damage's
  (`ObjectEditing.RuleVersion`). Saves and join payloads carry the same id (`VoxelObjectKinds.WorldId`, the log's
  generator id), so a save of other kinds is refused.
- A kind's parts own at most 112 voxels a side (`ObjectPart.OwnVoxels`; a kind 5.6 m wide at 5 cm is one part across)
  and hold a border of their neighbours' 8 voxels round them (`BorderVoxels`; 4 at level 1, 2 at level 2: the two past
  an edge the mesher reads for a vertex's normal and openness), so a part's model box is its own plus 16; each part model owns
  the faces of its own voxels alone (`VoxelModels.SetOwned`, with `OpenLow` 1 on a side with no part beyond, which owns
  the faces against its border; the mesher's owned window, `SurfaceNets.Mesh(samples, densities, ownedMin, ownedMax,
  output)`), so parts meet with no seam and each face is made once.
- Drawing (`WorldMeshes.Objects.cs`): each pass records the level-K columns it split below K (`CameraSplit`, whose columns
  the near cascade and the image views share; `FarSplit`); an object's part models go into a pass's lists when a level-K
  column under its reach is split there (an area is passed over when neither its column nor one round it is), at its own
  voxels within level 0's reach times the mesh share, twice them within level 1's, else four times (the views: four
  times), turned by `ObjectTurns.Rotation` about its anchor. Objects are gathered after the game's models and the retired
  ones, their areas nearest the camera first (an insertion sort: no allocation), an area passed over when no column
  within the widest kind's reach of it is split; when a list's draws need more records than it holds, every model level
  takes the same share, the first gathered first, so neither a kind nor a part of one (a tree's crowns) drops whole. An
  object whose reach spans a split column and one that is not draws its model whole beside its copy in the column that
  is not: at the hand-over distance, the two show together on that side (docs/voxel-objects.md). Their draws' tint is below 0 (`MeshModelDraw.Tint`),
  which `mesh.vert`'s model variant and `mesh.frag` shade as the world's surfaces (the mesh's material weights and
  palette, the texture sets, the tint map at the point, the world's look), so a model and its copy match. A stitched
  block of the level below K reads the coarser block with the objects' materials made air (`MaskCopies`), so no copy
  shows in its seam; the objects' materials should be their own, not the ground's.
- Characters meet objects as ground (`ObjectColumns`, internal; the mover's `Objects`, set where the world has terrain):
  in the mover's cells, a cell is blocked where its centre, turned into an object, lands on its solid voxel that is not
  passable (`IVoxelPassability`: a tree's leaves where the game says so); each run of blocked cells keeps its object's
  surface over the column's centre (the top of the highest solid voxel there) and that voxel's material, which the mover
  reads as the ground's (`PhysicsMaterials.OfVoxel`, `IVoxelSurfaceTags`). It answers the voxel world's and the mesh
  colliders' queries, made 32 by 32 columns at a time and kept until `ObjectWorld.Version` changes (an area filled or
  gone) or `ObjectDamage.ReadChanges` names an object under it, at most 256 chunks; a move waits where the areas round it within the map are not filled (`Known`, `ObjectWorld.IsKnown`). Kinds read a voxel through
  their parts' brick tables (`VoxelObjectKind.Voxel`).
- Rays meet objects (`ObjectRays`, scoped with terrain): the first solid voxel of any object along a ray (passable ones
  too, as the world's ray), as an `ObjectHit` (the object, its kind, its voxel's index in the kind, the face entered
  turned into the world's frame, zero from inside, the material, and where the ray enters and leaves that voxel); a ray
  that only touches an object's box, or starts on its side going away, meets nothing; the ray is walked in stretches
  of 16 m, each asking `ObjectWorld.Near` for the objects whose reach meets it (unfilled areas' objects are not met),
  turned into each object's voxels and stepped through them in its box; at most 10 km. `CollisionSources` answers from
  it too (`SurfaceSource.VoxelObject`, `SurfaceHit.ObjectId`), so the movement probes and climbing (`ClimbingMode`)
  meet objects by their voxels' tags; `ToolAim.Raycast(CollisionSources, ...)` goes past objects' passable voxels round
  the eye as past the terrain's; the third-person camera is pulled in by objects where given them
  (`ThirdPersonCamera`'s `objects`, `PlayerCameraSystem`'s, `ToolAim.Look`'s).
- Bodies meet objects (`VoxelColliders.Objects.cs`): each cell of statics near a body finds, at its first build, the
  objects whose kind's reach meets it and whose height range does (`ObjectWorld.Near`), and each such object gets a
  static for each physics material of its voxels: its kind's `BigCompound` of boxes over its parts' own voxels that are
  not passable (`VoxelBoxes.Build(kind, damaged, excluded)`, in the kind's unit, `PhysicsWorld.UnitBox`; made at its
  first need and shared by every object of the kind; a damaged object's of its own, below), posed at its anchor's local place and turned by `ObjectTurns.Rotation`, at
  `BodyKinds.AnyLevel` (bodies of every level touch it, cut bodies too) in the voxels' layer. An object's statics stay
  while a cell reaches it (counted) and go with the last; a change of the objects (`ObjectWorld.Version`) makes every
  cell find its objects again, the new taken before the old let go. A body round which the areas within the map are not
  filled (`ObjectWorld.IsKnown`) freezes as a body over columns not resident does. Objects' statics take no densities:
  their voxels' boxes are whole (at 5 cm, up to 2.5 cm off the drawn surface). `VoxelColliders.ObjectCount` counts them.
- Damage (`ObjectDamage`, scoped with terrain, `MatchVoxels.Damage`, initialised at the "objects" step after the
  objects' world, updated after it each loop iteration): the world hands each edit it logs to it (`VoxelWorld.ObjectEdits`,
  `IObjectEdits`), which applies it at once to the objects of the filled areas the edit's bounds reach where the log
  indexes it in a level-0 column under the object's reach (`VoxelWorld.IndexesUnder`: what a replay finds; an edit wholly
  past the map's sides, or above or below its height, changes no object), in the log's order (`ObjectEditing`, integer maths through `ObjectTurns.FromObject`, each voxel's sample points turned into the
  world's centimetres and read by the edit's own `Contains`): clears, clears of some materials, sets of air, and lifts
  (whose object voxels go, not into the piece) take a voxel by the share they cover (`VoxelEditing.ChangeByShare` on
  `VoxelEditing.Parts` of it); a paint recolours a solid voxel whose middle it covers (a paint of air changes no object); fills, sets of a material, and
  pieces put back change only the world, so objects never gain voxels. A clear with a maker counts what it takes from
  them, in cubic centimetres, into the game's `IVoxelRemovals` with the world's, before anything changes (not from an object whose copy the cap
  dropped, which may differ between peers). An object an
  edit changes keeps a `DamagedObject`: its changed 8-voxel bricks over its kind's voxels (copied whole at their first
  change), read through it (`IObjectVoxelSource`, as the kinds and shapes are); its parts' models are made again once an
  update, at most `PartsPerUpdate` (8) parts an update in the order the objects changed, from those voxels by the kind's
  rule (`PartLevel.Build`'s sampling; level 0 the kind's with the changed bricks
  over it, the coarser levels sampled again over each changed brick), a part whose border holds a changed brick with
  it, each a model of its own owning its kind's window (the old trees freed after `VoxelModels.Replace`), drawn once on
  the GPU (`ObjectDamage.PartsOf`; the kind's until then). It shares its kind's part's meshes for every 32-voxel block of
  each level its changes do not reach (`VoxelModels.ShareBlocks` with `DamagedObject.ChangedBlocks`: a block none of whose
  reads, apron included, holds a voxel of a changed brick, `ModelBlocks.Reading`), so a damaged part meshes, and holds
  in the mesh heap, its changed blocks alone. When an area fills, each of its objects replays the edits
  since the last reset that reach it (`VoxelWorld.CollectEdits`, the log's columns under its reach), so damage made
  before it loaded, before the match (a save, a joiner's download), or while its copy was dropped, shows the same bit
  for bit; when an area drops, its objects' copies go. A reset drops every copy. At most `Engine:Objects:MostDamaged`
  (256) objects keep a copy: past it, the copy of the damaged object farthest from every streaming centre goes (a
  Warning once a match), and it is made again by replay, `RebuildsPerUpdate` (2) an update, once it is nearer than the
  farthest kept. Readers keep what they read until `ObjectDamage.ReadChanges` names the object (a ring of 256 changes;
  past it, read everything again): the mover's chunks under its reach are made again, its statics' shapes swapped for
  compounds of its own voxels, and rays read it as they go. The far copies take the world's edits at its coarse levels
  as any voxel does; a removed part's copy stays (`RemovePart` changes no world voxel). The rule is `ObjectEditing.RuleVersion`, hashed into the kinds' fingerprint (so the match's
  identity) with the copies' rule: raise it with any change to what an edit does to objects. The cap counts objects,
  not bytes (a copy is its changed bricks, 1 KB each, and its rebuilt parts' models).
- A part cut off an object goes by an edit of its own (`VoxelEditOperation.RemovePart`, `VoxelEdit.RemovePart(id,
  seed, box, size)`: the object's id in `Piece`, the kind's seed voxel in `Turn`, 16 bits an axis, below 1,024; a box
  of air where the part lay, for the log's index): the world's voxels never change (`VoxelEdit.ChangesWorld` false;
  columns do not replay it; navigation ignores it), and `ObjectDamage` takes from the object every solid voxel joined
  to the seed across faces, the same on every peer (nothing for another object, a box missing its reach or height, or a
  seed of air or outside the kind; a copy the cap dropped takes it at its replay). Applied live (not replayed) to a
  copy, the voxels go to `ObjectDamage.Fallen`, with the air voxels round them that hold density (the soft side of their
  surface), and `FallenParts`
  (scoped, `MatchVoxels.Fallen`, run in the match's fixed step after the fixed group) makes a cut body of them: a
  model for each of the kind's parts it holds solid voxels of, with its border's (owning its own window's faces, and a low
  side's where no part of it lies beyond), three levels with densities by the kind's rule, placed and turned
  where they lay, one body (`LooseBodies.TryCreate(parts)`, 800 kg a cubic metre, shaded as the world, touching the
  voxels and the objects' statics, gone 30 s after it rests or 60 s after it was made; each peer makes its own, and none
  where its copy was dropped or its area not filled) with its entity (`LooseBody`, `CutPiece`), room made
  as for a thrown ball.
- Models may have a voxel size of their own (`VoxelModels.Add` with a unit; `UnitCentimetres`, `VoxelMetres`) and keep
  densities (`Encode(voxels, densities, extent)`, `DecodeDensities`, `HasDensities`), which the renderer meshes them with.
  `SolidModelVoxels.Add` and the cut parts' hulls and masses (`LooseBodies.TryCreate(parts)`) take the model's unit.
  `VoxelModels.Apply` still edits materials only; an object's damage makes its models again (`ObjectDamage`).
