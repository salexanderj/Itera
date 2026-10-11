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

A grounded move reports what the body stands on (`CharacterMoveResult.GroundMaterial` and `GroundTags`, kept in
`CharacterContact`): the highest blocking voxel or mesh triangle within the grounded reach under the feet's centre,
else the highest under four points half the radius out; a voxel's physics material by its voxel material
(`PhysicsMaterials.OfVoxel`) and its tags by `IVoxelSurfaceTags`, a triangle's tags and its collider's material as it
is at the move (`MeshColumns.TopAt`, a span keeping its collider); on a loose body or in the air the default and no
tags. The mover takes the run's materials and the voxels' tags as `Materials` and `SurfaceTags` (the core sets them;
a mover made without them reads the default).

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

## A game's own bodies and statics, and the records

The engine keeps a record for each Bepu body and static by handle (`BodyKinds`: a body's `BodyKind`, the voxel level
it collides with now and the one it was made for, its ragdoll, a game's `PhysicsMarks`, its physics material, its share
of gravity, and `Born`, which of the bodies that took the handle it is; a static's voxel level and physics material); the narrow phase reads them on Bepu's worker threads, and they are
written on the main thread between steps. A handle never recorded reads the default (a loose body at level 0, in no
ragdoll; a static at level 0). Every removal resets its record (`PhysicsWorld.RemoveBody` and `RemoveStatic`, which
`LooseBodies`, `RemoveStandIn`, `VoxelColliders`, and `MeshColliders` use), so a body or static that takes a removed
one's handle starts afresh; in Debug builds `PhysicsWorld.Step` throws when a recorded body was removed past
`RemoveBody`. A game adds its own with `PhysicsWorld.AddBody(description, settings)` (recorded as a loose body: it
touches the voxels, the game's statics, bodies, and stand-ins; frozen where the voxels round it are not loaded, unless
`PhysicsMarks.KeepsMovingUnloaded`; of `PhysicsBodySettings.Material`; moved with the origin; not one of `LooseBodies`, so not counted, settled, or read by
`PhysicsReadSystem`) and `AddStatic` (touched by bodies of every voxel level, as a mesh collider), removes them with
`RemoveBody` and `RemoveStatic` (the shapes stay the game's), and tells a reused handle apart with `BornOf` and
`Holds(body, born)`. `RemoveBody` first calls `BodyRemoving`, which the voxel colliders set to `Forget`. A body added
straight through `Simulation.Bodies` has no record (it reads the default) and must not be removed past `RemoveBody`
once recorded.

## The solver and damping

`Engine:Physics:Substeps` (1, 1 to 8) and `VelocityIterations` (8, 1 to 32) set Bepu's solver (`SolveDescription`, never
the `(int, int)` tuple, which this Bepu swaps), and `LinearDamping` and `AngularDamping` (0.03, 0 to 1; 1 stops every
body each step) the shares of speed and spin a body loses each second; a game changes them during a match with
`PhysicsWorld.SetSolver` (the impulses kept for the next solve scaled to the new substeps) and `SetDamping`, and the
console with `physics solver I S` and `physics damping L A` (`PhysicsCommands`, which prints the match's bodies, statics,
solver, damping, and gravity). The integrator (`PoseIntegratorCallbacks`, reading `IntegrationSettings`) works one
substep exactly as before; with more, Bepu integrates a body with constraints once a substep and one without once over
the step, so each lane's own duration is used (damping as e^(t ln(1 - share))). `PushOn` reads a stand-in's push with
the gravity, damping, and substeps the last step ran with, per substep while bodies touched it.

## Gravity

`Engine:Physics:Gravity` (`X`, `Y`, `Z`: 0, -9.81, 0; each -100 to 100) is the match's gravity on every dynamic body
(`PhysicsWorld.Gravity`, `DefaultGravity`); a game changes it during a match with `SetGravity`, which wakes every
sleeping body, and the console with `physics gravity X Y Z`. Each body falls by a share of it (`SetGravityScale`, or
`AddBody`'s `gravityScale`: 1 by default, 0 floats, negative rises; it wakes the body), kept in its record; while every
body's share is 1 (`BodyKinds.ScaledBodies` is 0) the integrator reads none, and otherwise reads each active lane's from
its record through `ActiveSet.IndexToHandle` (`IntegrationSettings.Kinds`, `Bodies`). `GravityShare` is the gravity's
strength over the Earth's (`StandardGravity`, 9.81; exactly 1 at the default): `PlayerMovementSystem`, given the match's
`PhysicsWorld`, makes characters fall by their own `MovementStats.Gravity` times it (sliding too), and keeps a jump's
launch speed of their own gravity, so under less gravity they jump higher (half gravity: twice the height and the time);
a movement mode is given the stats with the scaled gravity. Down stays -Y for characters, the mover, and the terrain.
`PushOn` takes the stand-in's share. A floating ball (share 0) at rest settles into a floating sphere of voxels with
terrain: a game makes floaters as its own bodies (`AddBody`).

## Physics materials and bounces

A physics material (`PhysicsMaterial`: friction 0 to 4, bounciness 0 to 1) is registered by name once a run, in the
game's `IRunStartup` or a mod's, in `PhysicsMaterials` (run-wide; at most 64; `PhysicsMaterialId`, a byte), which the
first match seals (a later `Register` throws). Material 0 is `default`, friction 1 and no bounce, which every body and
static has unless given another: two of them give exactly the contact the engine had before materials. A body takes one
at `AddBody` (`PhysicsBodySettings.Material`) or later (`SetMaterial`, `MaterialOf`), a static at `AddStatic`, and a
mesh collider at `Add` (`PhysicsBodySettings`; `SetSettings`, `SettingsOf`); `physics materials` lists them. A voxel
material takes one by `PhysicsMaterials.MapVoxel` (before the first match; `OfVoxel`), the voxels at most 8 among them
with the default (`MostVoxelMaterials`): `VoxelColliders` builds a cell's voxels of each physics material into a static
of their own (one walk of the cell each, the other materials left out with the passable ones, `PartCount`; a cell of
one material is one static, and a world that maps no voxel material walks and builds as it did before materials), and
counts a cell of several towards `MaxRebuiltCells` as that many; a ball made from nothing takes its voxel material's
(`LooseBodies.TryCreateBall`), a lifted piece the default. Splitting costs: over a block of a random material in each
voxel, cells of three physics materials built in about twice the time of one, with 1.85 times the boxes. The narrow
phase (`NarrowPhaseCallbacks`) reads both sides' records (`BodyKinds.ContactOf`, a table of `ContactMaterial` the match
copies at its start): friction is the product of their square roots (sqrt(a·b)), and every pair has the engine's
springs (30 Hz, critically damped, recovery at most 2 m/s), so a bouncy body sinks no further than any other.

Bepu has no coefficient of restitution: its contacts stop a pair. A pair with a bouncy side (bounciness the larger of
the two; stand-ins left out) is recorded by the narrow phase when it touches or will by the step's end at its speeds
with the match's gravity (within 1 mm), with its deepest contact and the speed the two meet at there (their speed
before the step, and what gravity adds or takes, by each side's share, over the gap still between them), on each
worker's buffer (`PhysicsContacts`: 4,096 pairs, shared by the workers, at least 1,024 each). A pair is two bodies, or
a body and a static, and every voxel static counts as one, the voxel ground: a ball that rolls from one cell's static
into the next has not begun touching anything. After the step, on the main thread, the buffers are sorted by pair (so
the threads' order does not count below the capacities), one contact kept a pair (the deepest), and each pair that was
not touching the step before and came together at 0.5 m/s or more (`MinimumBounceSpeed`) is pushed apart at the
contact until it separates at its bounciness times that speed, by its masses and inertias (`PhysicsContacts.Bounce`;
statics and kinematic bodies take none); slower pairs rest and roll as Bepu solves them. Removing a body or static
forgets its pairs, so one that takes its handle next begins afresh. The bounce does not depend on the substeps; a ball
then rises a step's rise short of the curve (at 60 Hz, about 3 cm from a 1 m drop at bounciness 0.8), less what
damping takes. A ball rolling over voxels hops up to about 2 cm at a cell's border at 4 m/s (Bepu's contact with the
next cell's edge, whatever its material); a bouncy one bounces where such a hop lands at 0.5 m/s or more. Not bounced: a
pair that strikes while already touching (a ball rolling into a wall of the voxels it rolls on). `physics` prints the
bounces set and the contacts dropped past the buffers.

## Layers and groups

A collision layer (`PhysicsLayerId`) is registered by name once a run, in the game's `IRunStartup` or a mod's, in
`PhysicsLayers` (run-wide; at most 32, layer 0 `default`; the first match seals the names). Which layers touch is a
table of pairs, the same both ways (`PhysicsLayers.SetTouches`; every pair touches until set apart), with the layer the
characters' stand-ins are in (`CharacterLayer`) and the voxels' statics (`VoxelLayer`), the default unless set; each
match copies them at its start and changes its own table with `PhysicsWorld.SetTouches` (which wakes every sleeping
body; `Touches`). A body or static takes a layer and a group at `AddBody` and `AddStatic` (`PhysicsBodySettings.Layer`
and `Group`), a mesh collider by its settings, or later by `SetLayer` (a body is woken; a static wakes every sleeping
body), read by `LayerOf` and `GroupOf`; members of one group other than 0 never touch each other (a game's numbers).
The narrow phase applies the engine's rules first (stand-ins, cut bodies, voxel levels), then the layers and groups
(`BodyKinds.LayersAllow`): layers only take pairs away. A loose body whose layer does not touch the characters' is not
read by the mover (`LooseBodies.CopyPoses`) and gets no stand-in (`AnyPieceNear`), so characters pass through it; the
mover reads every mesh collider whatever its layer, so a gate of a layer balls do not touch still blocks players.
`physics layers` prints each layer, the characters' and the voxels', and the layers it does not touch (the match's
table in a match, else the run's).

## Trigger zones

A trigger zone (`PhysicsWorld.AddTrigger(shape, position, orientation, layer)`: a `TriggerShape` box, sphere, or capsule
along its local y, local to the origin and moved with it; at most 256, `MostTriggers`; `MoveTrigger`, `RemoveTrigger`,
`HoldsTrigger`, `TriggerOf`, and a `TriggerId` of its handle and `Born`) is a kinematic body that never sleeps and stops
nothing (`BodyKind.Trigger`). The narrow phase pairs it with every body its layers allow (moving, asleep: an awake
kinematic body meets the sleeping ones, frozen, or a client's mirror; not statics, stand-ins, or other zones), records
a pair with a contact of depth 0 or more on its worker's buffer (`PhysicsTriggers`: 4,096 pairs shared by the workers,
at least 1,024 each), and makes no constraint. After the step the overlaps are sorted by zone and body (the threads'
order does not count below the capacities) and set against the last step's: `PhysicsWorld.TriggerEvents`
(`TriggerEvent`: `Entered` or `Left`, the zone, the body and its `Born`, and `Removed`), read by the systems of the next
step, those of bodies and zones removed since the step before first (`RemoveBody`, `RemoveTrigger`: a removed `Left`
for each body that was in); `IsInside`. Overlaps and events past the buffers are dropped and counted
(`TriggerEventsDropped`, a Warning once a match; a dropped overlap reads as a body leaving and entering again). A zone
sees only what overlaps it at a step's end (no sweep): a body that moves more than the zone's depth and its own width
in a step can cross a thin zone unseen (a 20 cm ball at 30 m/s through a zone 20 cm deep). A zone's layer is its
record's (`SetLayer` on its body changes it for bodies and characters alike). Characters are not Bepu bodies: `TriggerCharacterSystem`
(`.claude/rules/systems.md`) tests each entity with a `TriggerSensor` as an upright capsule (`TriggerOverlap`) and writes
the match's `CharacterTriggers` (`CharacterTriggerEvent`, with the entity). `physics` prints the zones and the overlaps
dropped.

## Mesh colliders and the shared ray

A game's static triangle colliders (`MeshColliders`, scoped to the match): `Add(asset, transform, tagsOf)` registers a
mesh asset placed by a local transform, each triangle tagged by its material's name through `tagsOf`, or `Add(positions,
indices, transform, tags)` triangles built in code; `Add(asset, at, transform, tagsOf)` and `Add(positions, indices, at,
transform, tags)` place them from an absolute point (`WorldPoint`), which keeps a large or far mesh precise; `Remove`
takes one away. Only registered colliders collide; drawing
stays apart. Add them once the match's physics exists (from a system's `Initialize` on). Each collider keeps an anchor (the absolute
whole metres nearest its point) and its triangles from it, with a grid of 4 m buckets over absolute x and z (made again
at the first query after a change, which allocates; other queries allocate nothing), and each collider is also a Bepu
`Mesh` static posed at its anchor's local place, of its settings' physics material, its triangles wound clockwise seen
from the front as Bepu asks (glTF's are counter-clockwise), one-sided in Bepu, at `BodyKinds.AnyLevel`: every loose body
touches it whatever its voxel level (`SetGameStatic`; `ClearStatic` on removal). Surface tags are up to 32 bits a game
defines: voxel materials through its `IVoxelSurfaceTags` (none by default), mesh materials by name at registration.
`CollisionSources` (scoped) is what rays meet: `Raycast` answers from the terrain (`VoxelWorld.Raycast`) and the
colliders (two-sided, nearest first through the buckets), the nearer winning, as a `SurfaceHit` (point, normal turned
towards the origin, distance, tags, source, and the voxel's material or the collider and triangle). The voxel tools'
ray (`ToolAim`) stays the terrain's own.

### Files in `src/Itera.Engine`

  - `Physics/`: the Bepu simulation and stand-ins (`PhysicsWorld`; a game's bodies and statics in `PhysicsWorld.Bodies.cs`;
    the records by handle, `BodyKinds`; physics materials, `PhysicsMaterials`, the bounces, `PhysicsContacts`, the layers, `PhysicsLayers`, and the trigger zones,
    `PhysicsWorld.Triggers.cs`, `PhysicsTriggers`, `TriggerOverlap`), voxel box statics (`VoxelColliders`), loose bodies
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
