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
- Models may have a voxel size of their own (`VoxelModels.Add` with a unit; `UnitCentimetres`, `VoxelMetres`) and keep
  densities (`Encode(voxels, densities, extent)`, `DecodeDensities`, `HasDensities`), which the renderer meshes them with.
  `SolidModelVoxels.Add` and the cut parts' hulls and masses (`LooseBodies.TryCreate(parts)`) take the model's unit.
  `VoxelModels.Apply` still edits materials only.
