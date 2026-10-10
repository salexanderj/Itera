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
radius and material, `VoxelWorld.MakeBall`, shared; the console's `balls` drops a row, `DebugBallSystem`) and goes back
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
Solid models that are not loose bodies (`SolidModelVoxels`, `SolidColumns`) are solid to the mover alone.

The character mover (`VoxelCharacterMover`): an upright cylinder that slides along walls, steps up onto ledges up to its
step height, walks up no slope past its max slope (steeper ground slides it down and it cannot jump there), lands, and
walks through passable materials (`IVoxelPassability`). On voxels coarser than 1 cm it stands on the ground's surface
inside a voxel, where its density crosses one half (`VoxelWorld.SurfaceOver`), blended between neighbouring columns
that differ by less than the slope limit over a voxel. The map's edge is a wall at every height (`AddMapEdge`). It waits
while the voxels round it are not loaded. Its widest body is 2 m in radius (`MaxRadiusMetres`).

## Mesh colliders and the shared ray

A game's static triangle colliders (`MeshColliders`, scoped to the match): `Add(asset, transform, tagsOf)` registers a
mesh asset placed by a transform, each triangle tagged by its material's name through `tagsOf`, or `Add(positions,
indices, transform, tags)` triangles built in code; `Remove` takes one away. Only registered colliders collide; drawing
stays apart. Add them once the match's physics exists (from a system's `Initialize` on). The triangles are kept in the
world's space with a grid of 4 m buckets over x and z (made again at the first query after a change, which allocates;
other queries allocate nothing), and each collider is also a Bepu `Mesh` static, its triangles wound clockwise seen
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
    `SolidColumns`), the character mover (`VoxelCharacterMover`), a game's mesh colliders (`MeshColliders`) and the ray
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
