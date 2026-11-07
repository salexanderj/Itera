---
paths:
  - "src/Itera.Engine/Physics/{VoxelCharacterMover,FineGround,MeshColumns,MovementProbes,StepWorkers}.cs"
  - "tests/Itera.Engine.Tests/Physics/{CoarseCeilingMoverTests,CoarseVoxelTests,DensityMoverTests,FarFromTheMiddleTests,FineGroundTests,FineMoverTests,GiantMoverTests,GroundMaterialTests,LargeBodyMoverTests,MapEdgeMoverTests,MeshColumnsTests,MeshMoverTests,MixedGroundMoverTests,MovementProbesTests,MoverRestTests,MoverWithBodiesTests,MoverWorkersTests,StepWorkersTests,VoxelCharacterMoverShapeTests,VoxelCharacterMoverTests,WideBodyMoverTests,WorkersWorld}.cs"
---

## The character mover

`VoxelCharacterMover` is an upright cylinder. It slides along walls, steps up onto ledges up to its step height, lands,
and walks through passable materials (`IVoxelPassability`). It walks up no slope past its max slope: steeper ground slides
it down and it cannot jump there. The mover is made by the core (`AddIteraEngine`) and used by movement modes
(`.claude/rules/systems.md`).

- It steps up from the ground, and in the air where its shape asks (`CharacterShape.StepsInAir`, a movement mode's
  `MovementModeMove.StepsInAir`).
- A step height of 0 turns off stepping up and keeping to the ground.
- Its widest body is `MaxRadiusCells` of its cells in radius (`MaxRadiusMetres`): 2 m in cells of 1 cm, 20 m in cells
  of 10 cm. A move's cost grows with the cells under the body.
- Over coarse voxels, its windows of surfaces are made for a body 2 m in radius. The first move of a wider body makes
  them again, larger, once for each mover and worker; that move allocates.

### Large bodies

With `Engine:Physics:LargeBodyCellCentimetres` set over terrain, the core gives the mover a second one in those coarser
cells (`VoxelCharacterMover.Large`). Every body wider than `Engine:Physics:LargeBodyRadiusMetres` moves there, so a
giant reads fewer cells.

- The mover hands such a body's `Move`, `Overlaps`, `OverlapsAnything`, `StandsUnchanged`, `GroundTop`, and `Prepare`
  to it. Callers use the one mover. Its stamp is the same, as both read the same world, colliders, and objects.
- The large mover has object columns of its own in its cells, and the same loose bodies in its cells
  (`LooseBodyVoxels.Coarser`). They refresh with the mover's. Each solid model added, placed, or removed in the mover's
  cells is in the large mover's too, by the same id (`SolidModelVoxels.Mirror`).
- A worker has a large mover of its own, and a pass begins and ends on both.
- `MaxRadiusMetres` is then the large mover's.
- The cells must be coarser than the mover's and a whole divisor of the voxels, or the voxels themselves; else the
  match does not start.
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
- A run of `GroundTop` reads keeps the surfaces it reads: no move or query between them, the world's `Stamp` the same,
  and each read within `GroundRunCells` of the run's first. So a figure's reads round its feet cost little more than
  one. The answers are a forgetting mover's (`KeepsGroundSurfaces`).
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

### A body at rest

`Stamp` gives a `GroundStamp` of what a move reads that can change while a body stands still. It holds the edits applied
in every column they reach (`VoxelWorld.AppliedEditCount`), the newest reset, the mesh colliders' version, and the voxel
objects' version and damage. `StandsUnchanged` says whether the same move would answer the same now. It is false when:

- the columns round the feet are not resident, or the objects' areas there are not filled;
- a reset came;
- a change since reaches round the body: an edit logged, a collider added or removed (`MeshColliders.ChangesMeet`), or
  a change the objects' damage names (`ObjectDamage.ChangesMeet`). An edit still waiting in some column
  (`VoxelWorld.ApplyPendingEdits`) is read again at each ask until it applies;
- more colliders or damage changed than their rings keep;
- a loose body or solid model reaches round it.

Round the body means within its radius and a margin across, its step under it, and its height over it, with two of the
world's voxels more each way. An object placed or removed counts by its edit's box, its reach and height, since the
damage names none where its area is not filled. A part cut off counts through the damage's change. An area of objects
filled or dropped elsewhere changes nothing: a filled area holds what the log gives it. A mover whose `ObjectColumns`
has no damage takes any change of the objects, wherever it is.

When true, it advances the stamp, so the next ask reads only later changes. `PlayerMovementSystem` uses it to skip the
move of a character at rest (`.claude/rules/systems.md`).

### Movers on other threads

`VoxelCharacterMover.CreateWorker` makes a mover for another thread. `PlayerMovementSystem` moves its characters on the
step's workers with them:

- The system gathers the characters, begins the pass, and makes each one's chunks from its feet, size, and velocity
  before steering. A chunk a mode's faster move reads past them a worker makes itself, which costs time alone. Each
  worker then takes the next character with a mover of its own and the movement mode's copy for it.
- A mode's copies are `IMovementMode.ForWorker`, one for each worker, made at the system's `Initialize`. A copy asks
  the world only through the worker's probes it is given (`MovementProbes.Mover` and `Sources`). It reads and writes
  only its character's components, and shares no state it writes. `ClimbingMode` has copies.
- The characters move on the main thread with one thread, with fewer than `PlayerMovementSystem.LeastForWorkers`, with
  a mode that has no copies, or where the system has no `MovementProbes` for them.
- A game's system may run a pass of its own the same way: `CreateWorker`, `BeginPass`, `Prepare`, and `EndPass` are
  public. A game reads the ground round its figures' feet on workers so (`GroundTop`).

A worker and its pass:

- A worker moves bodies through its owner's world, colliders, objects, and loose bodies, with its own windows, surface
  caches, and `FineGround`. Its answers are its owner's, bit for bit.
- It reads the chunks of columns its owner keeps (`MeshColumns`, `ObjectColumns`) and writes nothing of them. A chunk
  they do not hold, or hold of older colliders or objects, it makes in a few of its own (`OwnBuilds` counts them).
- It keeps no brick of a loose body it reads (`LooseBodyVoxels`).
- A pass runs in this order:
  - The owner begins it (`BeginPass`): the columns follow the colliders' and objects' changes, and the colliders are
    built.
  - The owner makes the chunks each body's move will read (`Prepare`): within its radius and its move across, and
    `PrepareMarginCells` more.
  - The workers move the bodies, each one body at a time.
  - The owner ends the pass (`EndPass`).
- Within a pass the columns keep every chunk read, past `MeshColumns.MostChunks` where they must. They stay past it after
  the pass, and the chunk read longest ago goes again at the next chunk made.
- `Prepare` makes no chunk of colliders or objects where there are none, as the mover reads none.
- While workers move bodies, nothing else moves one, reads the columns, refreshes the loose bodies, or changes the world.
- In Debug a mover throws on a second move or query while one runs.
- A worker's probes are `MovementProbes.CreateWorker`: the worker and a copy of the shared ray of its own
  (`CollisionSources.CreateWorker`), which marks the colliders' triangles and gathers objects in its own memory.
  `MovementProbes.Mover` and `Sources` give a movement mode's copy the mover and the ray to ask.

### The step's workers

`StepWorkers` (run-wide) are the threads a fixed step's parallel passes run on, the calling thread among them.

- `Run` runs a piece of work on the calling thread and on each helper that comes while the caller's own part runs,
  each given its index. It returns once the caller's part and those helpers' are done, and rethrows the first exception
  a worker met.
- A helper that comes after the caller's part skips the pass, so a pass never waits for a helper the busy processors
  have not run yet. The work leaves nothing to a worker by its index.
- Each worker takes the next item of the pass from a counter it shares, so each item's result is the same on any thread.
- Each worker's part is the zone `step workers.part`, and the caller's wait for the helpers in the pass
  `step workers.wait`.
- `Engine:Physics:StepThreads` sets their number. At 0 they are `StepWorkers.ThreadCount`: half the processors, 2 to 6,
  or 1 on a machine of one. At 1 every pass runs on the main thread.
- The helpers wait between passes. Running a pass allocates nothing; the work allocates what it does. `AllocatedBytes`
  counts what the work allocated on every thread, for tests.

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
- Chunks of 32 by 32 columns are made at their first read. A collider added or removed drops the chunks its box reaches,
  a cell more each way (`MeshColliders.ChangeBox`). Past the changes the colliders keep, every chunk goes. `MostChunks`
  limits them. Past it, the chunk read longest ago is dropped and its memory used for the new one. Making a
  chunk allocates until the cache is full.
