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

A world is made by its generator (`IChunkGenerator`, the game's): `DescribeColumn` says which layers of a column are air
or one material, and `Fill` writes a chunk's materials and densities. Its grid (`IChunkGenerator.Grid`) sets the world's
voxel size, 1 cm to 1 m (`VoxelGrid.MostBaseCentimetres`; a coarse world suits terrain deformed in large strokes, at the
cost of shapes under a voxel showing only as soft dips in the densities, walls in voxel steps, and the ground's 1 m
steps in a piece lifted from it; an edit measures a voxel on at most 10 sub-rows each way, so one under about a tenth of
a voxel changes nothing, and a small ball settling into coarse voxels leaves no trace;
`Engine:Voxels:StreamingRadiusMetres` 0 takes the radius from the grid, 4.625 level-0 columns where that passes 59.2 m),
and its bounds in whole level-0 chunks: `VoxelGrid.Sized(base, halfWidthMetres, floorMetres, topMetres)` rounds each
bound out to whole chunks, up to ±160 km each way, up and down too (`VoxelGrid.MostReachCentimetres`), and a grid of
a base alone has the default bounds (±512 m and -51.2 to 102.4 m at 10 cm). A column stores only a window of its layers
(`Column`: `First`, `Stored`; every layer below it `Below` and every layer above it `Above`, air or one material each;
`LayerAt` reads any layer): the generator's range (`IChunkGenerator.DescribeRange`: by default the whole column; the
heightfield base answers the layers round its ground and features, found by halving the column) and the layers its
edits reach (`ColumnJob`); an edit that changes a layer outside stores it (`Column.Widen`), and one that changes a layer
that was one fill posts `VoxelChangeKind.ColumnLayersChanged`. So a world's height costs memory only where its ground
and edits are: a column of a world 25,000 layers tall stores the layers one 12 layers tall does.
Streaming, edits, rays, collision, the mover's edge wall, the fall limit, and the play area follow the grid's bounds
(absolute; streaming centres and rays come in local metres and are read through the world's origin,
`VoxelWorld.Origin`, a hit's voxels absolute and `VoxelHit.Entry` taking the origin);
the grid is part of what the generator's id and version stand for, so a change of size raises the `Version`. The
engine's toolkit is in `Voxels/Generation/`:

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

  - `Voxels/`: storage (`ChunkKey`, `ChunkTree`, `BrickCodec`, `VoxelConstants`, `VoxelDensity`), the grid (`VoxelGrid`,
    a world's voxel size and bounds from its generator, where centimetres meet voxel indices; `GridLevel`), streaming,
    edits, generation threads (`VoxelWorkers`, `ColumnJob`), the change list (`VoxelChangeLog`), memory (`VoxelMemory`),
    lifted pieces (`VoxelPiece`, `PieceRuns`), models (`VoxelModels`), the log's file format (`EditLogSerializer`), what
    an edit's clears remove (`IVoxelRemovals`), and collision queries (`IVoxelPassability`, `VoxelBoxes`); the
    generation toolkit in `Generation/`. `VoxelWorld` is the service: streaming in `VoxelWorld.cs`, the edit path,
    makers, removals, reset, and saves in `.Edits.cs`, lifts and pieces in `.Pieces.cs`, the edit warm-up in
    `.WarmUp.cs`, rays in `.Raycast.cs`, collision in `.Collision.cs`, and the blocks the raster pipeline meshes
    (`TryReadBlock`, `BlockMayHaveSurface`) and the sky field reads (`TryReadOwnBlock`) in `.Blocks.cs`.

### Conventions

- A world voxel has a material and a density (`VoxelDensity`: how much of it is solid, 0 to 255; a solid voxel's 128
  to 255, an air voxel's 0 to 127, fitted where stored). The world's sphere, capsule, and box clears, fills, sets, and
  clears of some materials change each voxel by the share of it they cover (up to 10 x 10 sub-rows along y and z, each
  to the centimetre along x: a 10 cm voxel's cubic centimetres), its density with it, and the material follows the
  density across one half (`VoxelEditing.Densities.cs`: `ApplyToWorldChunk`, the dense `ApplyToRegion` with densities,
  `CountInWorldChunk`); a clear takes that share of what is solid in the voxel, so one repeated on a spot keeps
  taking. Lifts, paints, and pieces keep to sample points. Models keep materials (and an object kind's, densities: `.claude/rules/objects.md`) and the sample-point rules
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
