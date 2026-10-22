---
paths:
  - "src/Itera.Engine/Voxels/**"
  - "src/Itera.Engine/Systems/{StreamingCentreSystem,MapResetSystem,WorldResets}.cs"
  - "src/Game/WorldGeneration/**"
  - "tests/Itera.Engine.Tests/Voxels/**"
  - "tests/Itera.Engine.Tests/Systems/{StreamingCentreSystem,MapResetSystem}Tests.cs"
  - "tests/Shared/VoxelLoading.cs"
---

## Voxel world and edits

### Edits and streaming

An edit larger than a chunk's worth of level-0 voxels is logged and sent at once, but applied to the resident columns over the following fixed steps. `Engine:Voxels:EditColumnsPerStep` sets how many columns a step applies (`VoxelWorld.ApplyPendingEdits`). Level 0 and the nearest columns go first. A smaller edit applies at once. `StartEditWarmUp` makes the edit code fast before the first edit.

Streaming centres (`StreamingCentre`) are:

- the player's eye;
- on a host, a level-0 disc around each remote player (`Engine:Voxels:RemotePlayerRadiusMetres`). The host moves them, and the mover waits where its voxels are not loaded;
- on a host or offline, a level-0 disc of the same radius around each `StreamedCharacter` away from every player, up to
  `StreamingCentreSystem.MaxCharacterCentres`. A game marks the characters it keeps moving far from every player;
- on a host or offline, a level-0 disc around each loose body away from every player. `StreamingCentreSystem` stretches the disc ahead by a little travel, up to a limit, and widens it by the widest voxel object kind's reach where the world places objects (`VoxelWorld.ObjectReachMetres`). It serves a limited number of bodies, each streaming the levels up to the one it collides with. A loose body where level 0 is not loaded freezes in the air.

A level's wanted columns are found again when a centre comes or goes, changes its radius, or moves half a column. A disc
narrower than a column is also found again when it meets a column it did not meet before, so a small disc never walks
off its columns (`VoxelWorld.SetStreamingCentres`).

### World generation

A world is made by its generator (`IChunkGenerator`, the game's):

- `DescribeColumn` says which layers of a column are air or one material.
- `Fill` writes a chunk's materials and densities.
- `Objects` places its voxel objects. The column jobs write their coarse copies into the levels from `Engine:Objects:HandOverLevel` up (`.claude/rules/objects-copies.md`).
- `Grid` (`IChunkGenerator.Grid`) sets the world's voxel size and bounds.
- `DescribeRange` gives the range of a column's layers worth storing (below).

Change the generator's `Version` whenever the same seed would give other voxels. The grid is part of what the generator's id and version stand for, so a change of size raises the `Version` too.

**The grid (`VoxelGrid`).**

- The voxel size lies between 1 cm and `VoxelGrid.MostBaseCentimetres`.
- A coarse world shows shapes under a voxel as soft dips in the densities. An edit measures a voxel on a limited number of sub-rows, so a change under a fraction of a voxel changes nothing.
- `Engine:Voxels:StreamingRadiusMetres` set to 0 takes the radius from the grid.
- `VoxelGrid.Sized(base, halfWidthMetres, floorMetres, topMetres)` rounds each bound out to whole level-0 chunks, up to `VoxelGrid.MostReachCentimetres` each way, up and down too. A grid of a base alone has the default bounds.
- Streaming, edits, rays, collision, the mover's edge wall, the fall limit, and the play area follow the grid's bounds. The bounds are absolute. Streaming centres and rays come in local metres and are read through the world's origin (`VoxelWorld.Origin`). A hit's voxels are absolute, and `VoxelHit.Entry` takes the origin.

**Columns.** A column stores only a window of its layers (`Column`: `First`, `Stored`).

- Every layer below the window is `Below`, and every layer above it is `Above`: air or one material each. `LayerAt` reads any layer.
- The window is the generator's range (`IChunkGenerator.DescribeRange`; by default the whole column) and the layers its edits reach (`ColumnJob`). The heightfield base answers the layers round its ground and features, found by halving the column.
- An edit that changes a layer outside the window stores it (`Column.Widen`). One that changes a layer that was one fill posts `VoxelChangeKind.ColumnLayersChanged`.
- A world's height therefore costs memory only where its ground and edits are.

**The toolkit** is in `Voxels/Generation/`:

- `HeightfieldGenerator`: a base for ground. A subclass gives:
  - the height at a point (`HeightAt`, centimetres);
  - the surface material (`SurfaceAt`);
  - the layers below it (`GroundLayers`: a surface depth, bands by depth, then the deep material);
  - features written over the ground (`CreateFeatures`: a per-thread `GroundFeatures` set of `IGroundFeature`s, which say whether they touch a box and fill a chunk; `GroundFeatureList` holds one kind).
- The base keeps a per-thread cache of the column's heights. It gives exact descriptions, fast row fills, and densities from the height (`GroundDensity`). A feature's voxels take whole densities (`VoxelDensity.Agree`). `SeedCache` keeps what a subclass builds once per seed. A game's generator may derive from the base.
- Shapes with exact row spans (the range of x a row of sample points covers, in + - * / and sqrt): `Capsule`, `Ellipsoid` (an integer fill), `Prism` with `PrismBuilder` (convex solids of `ShapePlane`s: boxes, frustums, pyramids, pushed outlines), `CutEllipsoid` (a leaning ellipsoid cut by planes), and `VoxelRows` (filling and replacing a row's range at a level).
- Noise and placement: `FractalNoise` (octaves of `DeterministicMath.GradientNoise`, and `SoftClip`), `JitteredGrid` (one candidate point a cell, chosen and jittered by a hash, found for a box), `ScalarFieldGrid` (any field of x and z cached on a grid, read bilinear), and `FieldTintMap` (a tint map from a field).

The ground's voxels take their density (`VoxelDensity`) from its height: the share of each voxel below it, fitted to the material it already gives. A voxel a feature made solid or air is whole. A layer is described as air or one material only where every voxel is wholly so. The test uses the voxels' edges, not their sample points.

### Conventions

- A world voxel has a material and a density (`VoxelDensity`): how much of it is solid, from 0 to 255. A solid voxel's density is 128 to 255, and an air voxel's is 0 to 127. Density is fitted where stored.
- The world's sphere, capsule, and box clears, fills, sets, and clears of some materials change each voxel by the share of it they cover. They measure the share on sub-rows along y and z, each to the centimetre along x. The density changes with it, and the material follows the density across one half (`VoxelEditing.Densities.cs`: `ApplyToWorldChunk`, the dense `ApplyToRegion` with densities, `CountInWorldChunk`). A clear takes that share of what is solid in the voxel, so one repeated on a spot keeps taking.
- These edits change no world voxel: a removal of an object's part (`VoxelEditOperation.RemovePart`), and an object placed or removed whole (`PlaceObject`, `RemoveObject`). A clear of objects' far copies (`ClearCopies`) works one level's copies by sample point. See `.claude/rules/objects.md`.
- Lifts, paints, and pieces keep to sample points. Models keep materials, and an object kind's keep densities (`.claude/rules/objects.md`). They follow the sample-point rules (`ApplyToChunk`, `CountInChunk`).
- The voxel world is its seed plus its edit log: the edits since its last reset. `VoxelEditOperation.Reset` is an empty box after which no earlier edit counts. Readers of the log must handle it.
- `VoxelWorld` is main thread only. Columns are generated, and freed after unloading, on `VoxelWorkers` threads.
- Voxel memory is native (`VoxelMemory`) and capped by `Engine:Voxels:MemoryBudgetMegabytes`.
- Generators (`IChunkGenerator`) are thread safe and deterministic on every platform. Use integer hashing and the basic float operations (+, -, *, /, sqrt) only. Do not use:
  - `System.Random`;
  - `Math.Sin`, `Pow`, or `Exp`;
  - the iteration order of a hash set or dictionary;
  - `System.Numerics` vector helpers, whose results differ between machines (`Vector2.Lerp`, `Reflect`, and `Transform` use `MultiplyAddEstimate`).
- Edits use integer geometry.

### Tests

`VoxelWorldTests`, `VoxelEditTests`, `VoxelEditingTests`, `DeferredEditTests`, `VoxelResetTests`, `VoxelGridTests`, `SizedWorldTests`, `TallWorldTests`, `BaseTenWorldTests`, `SparseColumnTests`, `VoxelBlocksTests`, `VoxelPieceTests`, `ModelEditingTests`, `EditLogTests`, `EditIndexBudgetTests`, `StreamingCentreSystemTests`, `MapResetSystemTests`. Under `Voxels/Generation/`: `HeightfieldGeneratorTests`, `GroundLayersTests`, `ShapeSpanTests`, `FractalNoiseTests`, `JitteredGridTests`, `ScalarFieldGridTests`, `FieldTintMapTests`. A game pins its generator's world in its own tests.

### Files in `src/Itera.Engine`

- `Voxels/`: storage (`ChunkKey`, `ChunkTree`, `BrickCodec`, `VoxelConstants`, `VoxelDensity`), the grid (`VoxelGrid`, a world's voxel size and bounds from its generator, where centimetres meet voxel indices; `GridLevel`), streaming, edits, generation threads (`VoxelWorkers`, `ColumnJob`), the change list (`VoxelChangeLog`), memory (`VoxelMemory`), lifted pieces (`VoxelPiece`, `PieceRuns`), models (`VoxelModels`), the log's file format (`EditLogSerializer`), what an edit's clears remove (`IVoxelRemovals`), and collision queries (`IVoxelPassability`, `VoxelBoxes`). The generation toolkit is in `Generation/`.
- `VoxelWorld` is the service:
  - streaming in `VoxelWorld.cs`;
  - the edit path, makers, removals, reset, saves, and the voxel objects' hook (`IObjectEdits`, `CollectEdits`) in `.Edits.cs`;
  - lifts and pieces in `.Pieces.cs`;
  - the edit warm-up in `.WarmUp.cs`;
  - rays in `.Raycast.cs`;
  - collision in `.Collision.cs`;
  - the blocks the raster pipeline meshes (`TryReadBlock`, `BlockMayHaveSurface`) and the sky field reads (`TryReadOwnBlock`) in `.Blocks.cs`.
- `Systems/`: `StreamingCentreSystem`, `MapResetSystem`, `WorldResets`.
