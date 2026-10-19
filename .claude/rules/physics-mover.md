---
paths:
  - "src/Itera.Engine/Physics/{VoxelCharacterMover,FineGround,MeshColumns,MovementProbes}.cs"
  - "tests/Itera.Engine.Tests/Physics/{CoarseCeilingMoverTests,CoarseVoxelTests,DensityMoverTests,FarFromTheMiddleTests,FineGroundTests,FineMoverTests,GroundMaterialTests,MapEdgeMoverTests,MeshColumnsTests,MeshMoverTests,MixedGroundMoverTests,MovementProbesTests,MoverWithBodiesTests,VoxelCharacterMoverShapeTests,VoxelCharacterMoverTests,WideBodyMoverTests}.cs"
---

## The character mover

`VoxelCharacterMover` is an upright cylinder. It slides along walls, steps up onto ledges up to its step height, lands,
and walks through passable materials (`IVoxelPassability`). It walks up no slope past its max slope: steeper ground slides
it down and it cannot jump there. The mover is made by the core (`AddIteraEngine`) and used by movement modes
(`.claude/rules/systems.md`).

- It steps up from the ground, and in the air where its shape asks (`CharacterShape.StepsInAir`, a movement mode's
  `MovementModeMove.StepsInAir`).
- A step height of 0 turns off stepping up and keeping to the ground.
- Its widest body is `MaxRadiusMetres` in radius.
- It waits while the voxels round it are not loaded.
- The map's edge is a wall at every height (`AddMapEdge`).
- Constructors: `VoxelCharacterMover(world, cell, passability, bodies, colliders)` with terrain, and
  `VoxelCharacterMover(passability, colliders, cell, edge, bodies)` without.
- The play area is `PlayArea`, round the grid, or `PlayArea.WithoutTerrain`, the bounds of a 10 cm grid. It has a bounds
  constructor for a game to register its own.

### Positions and cells

- It takes and gives local positions and reads absolute cells: a local float plus the match's origin
  (`VoxelCharacterMover.Origin`, the world's or the colliders'), in doubles, times its cells a metre. A move 150 km out
  reads the cells a move at the middle does.
- Its distances are in centimetres.
- It reads the voxels, the match's mesh colliders (`MeshColumns`), the voxel objects (`ObjectColumns`,
  `.claude/rules/objects-meeting.md`), and loose bodies, in the same cells. Voxels, meshes, and objects (`ObjectColumns`)
  combine by the highest top, the lowest bottom, and any cell.
- Without terrain it reads meshes, objects, and loose bodies in cells of `Engine:Physics:CollisionCentimetres`. It waits
  for no column, and its map edge is at the play area's world edge.
- Over terrain its cells may be finer than the voxels (`Engine:Physics:TerrainCellCentimetres`, a whole divisor of
  them). With the option at 0 the cells are the voxels'.

### Coarse voxels

On voxels coarser than 1 cm the mover stands on the ground's surface inside a voxel, where its density crosses one half
(`VoxelWorld.SurfaceOver`).

- The surface is blended between neighbouring columns that differ by less than the slope limit over a voxel.
- The ground's read reaches a cell or two over the feet. A column whose voxels or objects are open at the feet's cell
  with something over it, such as the lintel over a door, has its ground under that, not on the ceiling. Mesh colliders
  have no inside and are read as they are.
- The steepness rule looks at least 0.8 of a cell around the body. A slope too steep to walk still reads as one on
  voxels coarser than 10 cm. A coarse voxel blurs a slope's foot, which a body may step onto.
- On cells wider than the body's radius, a slope past the limit whose columns each rise less than the step height is the
  same voxels as steep stairs. The mover climbs it a tread at a time.
- A loose body or solid model blocks a coarse cell only where it holds the cell's centre. A ball smaller than a cell may
  not block it, but its stand-in still pushes the ball. A thin mesh wall blocks the whole cell it passes through.
- Where a mesh's top is over the voxels' but its surface lies under theirs, the voxels' surface holds the body.

### Finer cells over terrain

With `TerrainCellCentimetres` set, the mover meets objects, solid models, meshes, and loose bodies at their own size. It
reads the voxels through `FineGround`:

- A run of blocking voxels is ground in the fine cells from its bottom, whole, up to its surface. The surface is the
  density's over its highest voxel.
- The surface is blended across the voxels' columns with no seam where neighbours' surfaces lie within the ramp of each
  other. The blend is bilinear over each quarter of a column, between four points: its centre, its sides' middles, the
  mean of the two columns where they join, and its corner. The corner is the mean of the joined group of the four columns
  there. Where all join, it is the coarse mover's blend between centres.
- A fine cell is solid where its centre lies under the surface, so every fine column's surface lies within half a cell of
  its top.
- A voxel's column and those round it must be resident for the fine cells over it.
- A ledge under a voxel high is a step no higher than the body's. Cells of 1 m walk a column up to a cell over the feet.
- Most of a move on coarse voxels is reading the ground's surface under the footprint (`Surface`: a window of columns
  read and their blend). Within one move or query each question is worked out once and remembered, at most 16, and
  forgotten with the surfaces' cache at the next move. The answers are the same bits as a mover that works every answer
  out again (`FineMoverTests`, `RemembersSurfaces`).
- Navigation keeps its own reading of the objects in the voxels' cells.

### What the body stands on

A grounded move reports what the body stands on in `CharacterMoveResult.GroundMaterial` and `GroundTags`, kept in
`CharacterContact`.

- The source is the highest blocking voxel or mesh triangle within the grounded reach under the feet's centre. Failing
  that, it is the highest under four points half the radius out.
- A voxel's physics material comes from its voxel material (`PhysicsMaterials.OfVoxel`) and its tags from
  `IVoxelSurfaceTags`.
- A triangle's tags and its collider's material are read as they are at the move (`MeshColumns.TopAt`, a span keeping its
  collider).
- On a loose body or in the air, the result is the default material and no tags.
- The mover takes the run's materials and the voxels' tags as `Materials` and `SurfaceTags`. The core sets them. A mover
  made without them reads the default.

### Building blocks for movement modes

For a game's movement modes (`IMovementMode`, `.claude/rules/systems.md`):

- A move reports the last wall it met: `CharacterMoveResult.WallNormal` and `CharacterContact.WallNormal`, across x and z
  and out of the wall. Zero means none.
- `MovementProbes` (scoped, in the core) gives `TryWallAhead`, `TryLedgeTop`, and `HasRoom`.
  - `TryWallAhead` casts three rays of the shared ray from the body's axis, over its step, at half and nine tenths of its
    height. A hit steeper than 60 degrees within the reach past the body's side counts. The result is the nearest
    point, the distance, the tags, and the mean of the hits' normals. A voxel's normal is the face the ray entered.
  - `TryLedgeTop` casts a ray down from over the head, past the wall's face, onto walkable ground above the wall's point.
    It finds none where it starts inside the wall.
  - `HasRoom` is the mover's `OverlapsAnything`.
- Loose bodies are not in the shared ray, so no probe meets them.

### Mesh columns

`MeshColumns` (internal) holds a column's spans, one for each triangle over the column's square: the lowest and highest
cell it blocks, its plane's height at the square's centre, and its tags. A wall's height is its highest point over the
square.

- Only the cells a triangle passes through block. A closed mesh has no inside, so a body inside one is blocked only where
  a face lies in its height.
- A triangle on a cell's boundary blocks the cell behind its front. Before it is cut it moves 0.01 cell behind its front
  along each axis it reaches less than that along. A floor at y = k blocks the cell under it, a ceiling there blocks the
  cell over it, and a closed mesh of a voxel box blocks the box's outer voxels.
- A triangle that only touches a column's corner leaves it alone.
- A column's surface is the plane at its centre, anywhere in its top cell or under it on a slope. The voxels' surface lies
  within half a cell of their top, and the mover reads surfaces for both.
- Chunks of 32 by 32 columns are made at their first read and kept until the colliders change (`MeshColliders.Version`).
  `MostChunks` limits them. Past it, the chunk read longest ago is dropped and its memory used for the new one. Making a
  chunk allocates until the cache is full.
