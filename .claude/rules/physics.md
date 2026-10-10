---
paths:
  - "src/Itera.Engine/Physics/**"
  - "src/Itera.Engine/Systems/{LooseBodySystem,LooseBodyRoom,StandInSystem,PhysicsReadSystem,DebugBallSystem,LooseBodyModelSystem}.cs"
  - "src/Itera.Engine/Components/PhysicsComponents.cs"
  - "tests/Itera.Engine.Tests/Physics/**"
  - "tests/Itera.Engine.Tests/Systems/{LooseBodySystem,StandInSystem,PhysicsReadSystem,DebugBallSystem,LooseBodyModelSystem}Tests.cs"
  - "tests/Itera.Engine.Tests/Systems/PhysicsTestHarness.cs"
---

## Loose bodies

A ball, a lifted piece, or a part cut off a model is a loose body: Bepu moves it as the convex hull of its voxels
(`LooseBodies`) against statics of voxel boxes built near it (`VoxelColliders`; on voxels coarser than 1 cm a column's
top voxel ends where its density puts the ground's surface, a box to the millimetre, `PhysicsWorld.SurfaceBox`; bodies
whose parts are of coarse voxels collide with the level nearest their voxels, `VoxelColliders.CoarseLevelOf`, in cells
of 32 voxels). A piece of more than 4,096 box corners is hulled from its farthest points in 42 directions
(`LooseBodies.MaxHullPoints`). A ball is made from nothing (`LooseBodies.TryCreateBall`: one piece of 1 cm voxels per
radius and material, `VoxelModels.MakeBall`, shared; the console's `balls` drops a row, `DebugBallSystem`) and goes back
into the grid as a sphere of its material filled into the air after 3 s at rest (`Engine:Physics:SettleSeconds`;
`LooseBodySystem`, the console's `settle`); a lifted piece goes back as a piece edit. At most 384 loose bodies exist
(`Engine:Physics:MaxLooseBodies`); more removes the oldest, a ragdoll whole (`LooseBodyRoom`). Bepu runs on
`Engine:Physics:Threads` worker threads (0: a quarter of the processors, 1 to 6; 1 makes the steps repeat run to run,
for tests and fast-forward runs). The mover reads balls as
voxels (`LooseBodyVoxels`: characters are blocked by them and stand on them, but push rather than climb them), and a
character near one has a stand-in, a Bepu cylinder of its mass (`StandInSystem`, `CharacterStandIn`; not while
`Bodiless`). Cut bodies (`BodyKind.Cut`, a `CutPiece` entity) touch only the voxels, not other bodies or characters;
where their own level is not loaded they collide with the finest coarser level that is, up to the coarse collider level
(`BodyKinds.BaseLevel`, `Level`). A body with no level loaded round it freezes (made kinematic) until one loads; a
ragdoll's bodies freeze and thaw together, and its joints, which Bepu drops between kinematic bodies, are added again at
the thaw (`VoxelColliders.GroupThawed`). Cut bodies go 30 s after they rest (also when frozen) and at the latest 60 s
after they were made (`LooseBodySystem.CutRestSeconds`, `CutMostSeconds`). A ragdoll is made from a model's parts with
joints from its skeleton (`LooseBodies.TryCreateRagdoll`); no engine code calls it today. `PhysicsReadSystem` reads each
body's pose and each stand-in's push after a step; `LooseBodyModelSystem` hands the bodies' models to the renderer.
When the match's origin moves, `PhysicsWorld.Shift` moves every body (awake, asleep, kinematic) and every static by the
whole metres it moved, writing their poses and the broad phase's bounds alone: nothing wakes, and velocities, joints,
and contacts, which are relative, keep (`OriginShifts`, `.claude/rules/engine.md`).
Solid models that are not loose bodies (`SolidModelVoxels`, `SolidColumns`) are solid to the mover alone, placed each
step at an absolute point (`SetPose` takes a `WorldPoint`: a local position with the match's origin).

Without voxel terrain (the core registers `VoxelModels`, `LooseBodies`, and `LooseBodyVoxels` either way): the models
keep their trees in their own memory (with terrain, the world's, where a lifted piece lies), freed when the match
ends; there are no lifts, no `VoxelColliders` (so no body ever freezes), and no grid to settle into. Bodies meet the
game's mesh colliders (Bepu mesh statics every level touches), fall out under the play area's fall limit, and are read
by the mover in cells of `Engine:Physics:CollisionCentimetres`. A ball that rests for `Engine:Physics:SettleSeconds`
goes to the game's `ILooseBodyRest` (registered in the match's scope; `LooseBodySystem` asks): `Keep` (and with no
handler) leaves it where it is, asked again once it has moved (`LooseBody.Kept`); `Remove` removes it with its model;
`TakeOver` removes the body and leaves the model to the game (`LooseBodies.Release`, with where it lay from
`LooseBodies.Rested`), which draws and later removes it (`VoxelModels.Remove`; never `Free`: every ball of its radius
and material shares its tree). A save without terrain holds no bodies (`SettleThrown` does
nothing). `DebugBallSystem` does not check a ball's space and `MapResetSystem`'s `reset` only cleans up and counts the
reset without a world.

The character mover (`VoxelCharacterMover`): an upright cylinder that slides along walls, steps up onto ledges up to its
step height, walks up no slope past its max slope (steeper ground slides it down and it cannot jump there), lands, and
walks through passable materials (`IVoxelPassability`). On voxels coarser than 1 cm it stands on the ground's surface
inside a voxel, where its density crosses one half (`VoxelWorld.SurfaceOver`), blended between neighbouring columns that
differ by less than the slope limit over a voxel; its distances are in centimetres, but the steepness rule looks at
least 0.8 of a cell around the body, so on voxels coarser than 10 cm a slope too steep to walk still reads as one (a
coarse voxel blurs a slope's foot, which a body may step onto). On cells wider than the body's radius, a slope past the
limit whose columns each rise less than the step height is the same voxels as steep stairs and is climbed a tread at a
time (30 cm voxels at 50 degrees, for a person). On coarse cells it reads the rest in the same cells: a loose body or
solid model blocks a cell only where it holds the cell's centre, so a ball smaller than a 1 m cell may not block it (its
stand-in still pushes the ball), and a thin mesh wall blocks the whole cell it passes through. The map's edge is a wall
at every height (`AddMapEdge`). It waits while the voxels round it are not loaded. Its widest body is 2 m in radius
(`MaxRadiusMetres`). It takes and gives local positions and reads absolute cells: a local float plus the match's origin
(`VoxelCharacterMover.Origin`, the world's or the colliders'), in doubles, times its cells a metre, so a move 150 km
out reads the cells a move at the middle does. It also reads the match's mesh colliders in the same cells (`MeshColumns`), combined with the
voxels by the higher top, the lower bottom, and either cell; without terrain it reads them and the loose bodies in cells
of `Engine:Physics:CollisionCentimetres` (10 by default), with no column to wait for, and its map edge at the play
area's world edge. The core makes it (`AddIteraEngine`: the world's constructor with terrain,
`VoxelCharacterMover(passability, colliders, cell, edge, bodies)` without), and the play area (`PlayArea`, round the
grid, or `PlayArea.WithoutTerrain`, a 10 cm grid's bounds, with a bounds constructor for a game to register its own).
Where a mesh's top is over the voxels' but its surface lies under theirs (a steep triangle half in the ground), the
voxels' surface holds the body.

Building blocks for a game's movement modes (`IMovementMode`, `.claude/rules/systems.md`): a move reports the last
wall it met (`CharacterMoveResult.WallNormal`, across x and z, out of the wall; zero for none; also
`CharacterContact.WallNormal`); a step height of 0 turns off stepping up and keeping to the ground; and
`MovementProbes` (scoped, in the core) gives `TryWallAhead` (three rays from the body's axis, over its step, at half
and nine tenths of its height, of the shared ray: a hit steeper than 60 degrees within the reach past its side, with
the nearest point, distance, and tags, and the mean of the hits' normals: a voxel's is the face the ray entered),
`TryLedgeTop` (a ray down from over the head, past the wall's face, onto walkable ground above the wall's point; none
where it starts inside the wall), and `HasRoom` (the mover's `OverlapsAnything`). Loose bodies are not in the shared
ray, so no probe meets them.

`MeshColumns` (internal): a column's spans, one for each triangle over the column's square: the lowest and highest cell
it blocks, its plane's height at the square's centre (a wall's highest point over the square), and its tags. Only the
cells a triangle passes through block: a closed mesh has no inside, so a body inside one is blocked only where a face
lies in its height. A triangle on a cell's boundary blocks the cell behind its front (before it is cut it moves 0.01
cell behind its front along each axis it reaches less than that along), so a floor at y = k blocks the cell under it, a
ceiling there the cell over it, and a closed mesh of a voxel box the box's outer voxels; a triangle that only touches a
column's corner leaves it alone. A column's surface is the plane at its centre, anywhere in its top cell or under it on
a slope (the voxels' lies within half a cell of their top; the mover reads surfaces for both). Chunks of 32 by 32
columns are made at their first read and kept until the colliders change (`MeshColliders.Version`), at most 256
(`MostChunks`; past that, the chunk read longest ago is dropped and its memory used for the new one, so bodies far apart
that need more at once make chunks again at every read); making one allocates until the cache is full. A move over
triangles alone costs more than one over voxels, more so in small cells (columns are read one by one, voxels a brick's
mask at a time; the figures are in ARCHITECTURE.md).

## Mesh colliders and the shared ray

A game's static triangle colliders (`MeshColliders`, scoped to the match): `Add(asset, transform, tagsOf)` registers a
mesh asset placed by a local transform, each triangle tagged by its material's name through `tagsOf`, or `Add(positions,
indices, transform, tags)` triangles built in code; `Add(asset, at, transform, tagsOf)` and `Add(positions, indices, at,
transform, tags)` place them from an absolute point (`WorldPoint`), which keeps a large or far mesh precise; `Remove`
takes one away. Only registered colliders collide; drawing
stays apart. Add them once the match's physics exists (from a system's `Initialize` on). Each collider keeps an anchor (the absolute
whole metres nearest its point) and its triangles from it, with a grid of 4 m buckets over absolute x and z (made again
at the first query after a change, which allocates; other queries allocate nothing), and each collider is also a Bepu
`Mesh` static posed at its anchor's local place, its triangles wound clockwise seen
from the front as Bepu asks (glTF's are counter-clockwise), one-sided in Bepu, at `BodyKinds.AnyLevel`: every loose body
touches it whatever its voxel level (`SetGameStatic`; `ClearStatic` on removal). Surface tags are up to 32 bits a game
defines: voxel materials through its `IVoxelSurfaceTags` (none by default), mesh materials by name at registration.
`CollisionSources` (scoped) is what rays meet: `Raycast` answers from the terrain (`VoxelWorld.Raycast`) and the
colliders (two-sided, nearest first through the buckets), the nearer winning, as a `SurfaceHit` (point, normal turned
towards the origin, distance, tags, source, and the voxel's material or the collider and triangle). The voxel tools'
ray (`ToolAim`) stays the terrain's own.

### Files in `src/Itera.Engine`

  - `Physics/`: the Bepu simulation and stand-ins (`PhysicsWorld`), voxel box statics (`VoxelColliders`), loose bodies
    (`LooseBodies`: table, rest, and removal; `.Pieces.cs` balls and lifted pieces; `.Parts.cs` cut parts and ragdolls;
    `.Hulls.cs` large hulls), loose bodies and solid models as voxels (`LooseBodyVoxels`, `SolidModelVoxels`,
    `SolidColumns`), the character mover (`VoxelCharacterMover`) and the mesh colliders as its cells (`MeshColumns`), a
    game's mesh colliders (`MeshColliders`) and the ray
    over them and the terrain (`CollisionSources`, `SurfaceHit`), and Bepu's callback structs. The voxels' surface tags
    are `Voxels/IVoxelSurfaceTags`.
  - `Systems/`: `PhysicsReadSystem`, `LooseBodySystem`, `LooseBodyRoom`, `StandInSystem`, `DebugBallSystem`,
    `LooseBodyModelSystem` (`.claude/rules/systems.md`).

### Package notes

- `BepuPhysics 2.5.0-beta.29` is a prerelease and is pinned on purpose. The 2.4 stable line only
  targets net6.0. Callback structs live in `Engine/Physics/`. Its `Statics.SetShape` (also `StaticReference.SetShape`,
  which calls it) moves a static's bounds but keeps the old shape; change a static's shape with
  `Statics.GetDescription` and `ApplyDescription` (freeing the old shape after `SetShape` crashed the game in a ball's
  sweep).
