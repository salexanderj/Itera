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

An edit larger than a chunk's worth of level-0 voxels (128^3: a 12.8 m cube in 10 cm voxels, so only a very large edit;
smaller edits apply at once) is logged and sent at once but applied to the resident columns
over the following fixed steps, 4 columns a step (`Engine:Voxels:EditColumnsPerStep`, a count so every game keeps the
same pace; `VoxelWorld.ApplyPendingEdits`), level 0 and the nearest columns first. `StartEditWarmUp` makes the edit code fast before the first edit. Streaming
centres (`StreamingCentre`): the player's eye; on a host, a 4 m level-0 disc around each remote player; on a host or
offline, a 3 m level-0 disc around each loose body away from every player (stretched ahead by 0.5 s of travel, at most
12 m, at most 32 bodies, each streaming the levels up to the one it collides with; `StreamingCentreSystem`), since a
loose body where level 0 is not loaded freezes in the air.

## World generation

A world is made by its generator (`IChunkGenerator`, the game's): `DescribeColumn` says which layers of a column are
air or one material, and `Fill` writes a chunk's materials and densities. The engine's toolkit is in
`Voxels/Generation/`:

- `HeightfieldGenerator`, a base for ground: a subclass gives the height at a point (`HeightAt`, centimetres), the
  surface material (`SurfaceAt`), the layers below it (`GroundLayers`: a surface depth, bands by depth, then the deep
  material), and features written over the ground (`CreateFeatures`: a per-thread `GroundFeatures` set of
  `IGroundFeature`s, which say whether they touch a box and fill a chunk; `GroundFeatureList` holds one kind). The base
  keeps a per-thread cache of the column's heights (its 128 x 128 points and a ring of one) and gives exact
  descriptions, fast row fills, and densities from the height (`GroundDensity`); a feature's voxels take whole densities
  (`VoxelDensity.Agree`). `SeedCache` keeps what a subclass builds once per seed. The starter's `GameGenerator` is one.
- Shapes with exact row spans (the range of x a row of sample points covers, in + - * / and sqrt): `Capsule`,
  `Ellipsoid` (an integer fill), `Prism` with `PrismBuilder` (convex solids of `ShapePlane`s: boxes, frustums,
  pyramids, pushed outlines), `CutEllipsoid` (a leaning ellipsoid cut by planes), and `VoxelRows` (filling and replacing
  a row's range at a level).
- Noise and placement: `FractalNoise` (octaves of `DeterministicMath.GradientNoise`, and `SoftClip`), `JitteredGrid`
  (one candidate point a cell, chosen and jittered by a hash, found for a box), `ScalarFieldGrid` (any field of x and
  z cached on a grid, read bilinear), and `FieldTintMap` (a tint map from a field).

The ground's voxels take their density (`VoxelDensity`) from its height, the share of each voxel below it, fitted to the
material it already gives; a voxel a feature made solid or air is whole. A layer is described as air or one material
only where every voxel is wholly so (the voxels' edges are tested, not their sample points).

### Files in `src/Itera.Engine`

  - `Voxels/`: storage (`ChunkKey`, `ChunkTree`, `BrickCodec`, `VoxelConstants`, `VoxelDensity`), the grid (`VoxelGrid`, a world's
    voxel size from its generator, where centimetres meet voxel indices; `GridLevel`), streaming, edits, generation threads
    (`VoxelWorkers`, `ColumnJob`), the change list (`VoxelChangeLog`), memory (`VoxelMemory`), lifted pieces
    (`VoxelPiece`, `PieceRuns`), models (`VoxelModels`), the log's file format (`EditLogSerializer`), what an edit's
    clears remove (`IVoxelRemovals`), and collision queries (`IVoxelPassability`, `VoxelBoxes`); the generation toolkit
    in `Generation/`. `VoxelWorld` is the
    service: streaming in `VoxelWorld.cs`, the edit path, makers, removals, reset, and saves in `.Edits.cs`, lifts and pieces in `.Pieces.cs`, the edit warm-up in `.WarmUp.cs`, rays in
    `.Raycast.cs`, collision in `.Collision.cs`, and the blocks the raster pipeline meshes (`TryReadBlock`,
    `BlockMayHaveSurface`) and the sky field reads (`TryReadOwnBlock`) in `.Blocks.cs`.

### Conventions

- A world voxel has a material and a density (`VoxelDensity`: how much of it is solid, 0 to 255; a solid voxel's 128
  to 255, an air voxel's 0 to 127, fitted where stored). The world's sphere, capsule, and box clears, fills, sets, and
  clears of some materials change each voxel by the share of it they cover (up to 10 x 10 sub-rows along y and z, each
  to the centimetre along x: a 10 cm voxel's cubic centimetres), its density with it, and the material follows the
  density across one half (`VoxelEditing.Densities.cs`: `ApplyToWorldChunk`, the dense `ApplyToRegion` with densities,
  `CountInWorldChunk`); a clear takes that share of what is solid in the voxel, so one repeated on a spot keeps
  taking. Lifts, paints, and pieces keep to sample points. Models keep only materials and the sample-point rules
  (`ApplyToChunk`, `CountInChunk`).
- The voxel world is its seed plus its edit log (the edits since its last reset: `VoxelEditOperation.Reset`, an empty
  box after which no earlier edit counts; readers of the log must handle it). `VoxelWorld` is main thread only; columns
  are generated, and freed after unloading, on `VoxelWorkers` threads. Voxel memory is native (`VoxelMemory`) and capped
  by `Engine:Voxels:MemoryBudgetMegabytes`. Generators (`IChunkGenerator`) are thread safe and deterministic on every
  platform: integer hashing and basic float operations (+, -, *, /, sqrt) only; no `System.Random`, no
  `Math.Sin`/`Pow`/`Exp`, no hash set or dictionary iteration order, and no `System.Numerics` vector helpers (in .NET 10
  `Vector2.Lerp`, `Reflect`, and `Transform` use `MultiplyAddEstimate`, which fuses the multiply and add on some
  machines and not on others). Edits use integer geometry. Change the generator's `Version` whenever the same seed would
  give other voxels.
