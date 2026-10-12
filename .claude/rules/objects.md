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
  a kind whose voxels are not finer than the world's stops the start. A kind is split into parts of at most 128 voxels a
  side (`VoxelObjectKind.Parts`, empty ones left out), each kept as three levels of 8-voxel bricks (its voxels, twice,
  four times them): a coarser voxel holds the mean density of those it covers, solid from one half, of the material at
  its sample point where that is solid, else the commonest solid one. A kind needs a solid voxel. `Fingerprint`
  stands for every kind's name and voxels in order.
- `VoxelObjects` (scoped, with voxel terrain; `MatchVoxels.Objects`) makes every part a model at the match's "objects"
  step, after the voxel physics: three levels with densities (`VoxelModels.Add(levels, size, 0, unit, densities:
  true)`), each part's corner from the kind's anchor (`PartsOf`); `ObjectsLoadingTask` waits for them on the GPU (done once
  made without graphics).
- A world places its objects through its generator (`IChunkGenerator.Objects`, an `IObjectPlacer`, null for none; thread
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
  (`ObjectCopies.RuleVersion`: raise it with any change to how copies are written). Saves do not record the kinds yet.
- A kind's parts own at most 120 voxels a side (`ObjectPart.OwnVoxels`) and hold a border of their neighbours' 4 voxels
  round them (`BorderVoxels`; 2 at level 1, 1 at level 2), so a part's model box is its own plus 8; each part model owns
  the faces of its own voxels alone (`VoxelModels.SetOwned`, with `OpenLow` 1 on a side with no part beyond, which owns
  the faces against its border; the mesher's owned window, `SurfaceNets.Mesh(samples, densities, ownedMin, ownedMax,
  output)`), so parts meet with no seam and each face is made once.
- Drawing (`WorldMeshes.Objects.cs`): each pass records the level-K columns it split below K (`CameraSplit`, whose columns
  the near cascade and the image views share; `FarSplit`); an object's part models go into a pass's lists when a level-K
  column under its reach is split there (an area is passed over when neither its column nor one round it is), at its own
  voxels within level 0's reach times the mesh share, twice them within level 1's, else four times (the views: four
  times), turned by `ObjectTurns.Rotation` about its anchor. Objects are gathered after the game's models and the retired
  ones; a model list too full for a level's draws takes as many as fit.
- Models may have a voxel size of their own (`VoxelModels.Add` with a unit; `UnitCentimetres`, `VoxelMetres`) and keep
  densities (`Encode(voxels, densities, extent)`, `DecodeDensities`, `HasDensities`), which the renderer meshes them with.
  `SolidModelVoxels.Add` and the cut parts' hulls and masses (`LooseBodies.TryCreate(parts)`) take the model's unit.
  `VoxelModels.Apply` still edits materials only.
