---
paths:
  - "src/Itera.Engine/Physics/{LooseBodies,LooseBodies.Hulls,LooseBodies.Parts,LooseBodies.Pieces}.cs"
  - "src/Itera.Engine/Systems/{LooseBodySystem,LooseBodyRoom,StandInSystem,PhysicsReadSystem,DebugBallSystem,LooseBodyModelSystem}.cs"
  - "src/Itera.Engine/Components/PhysicsComponents.cs"
  - "tests/Itera.Engine.Tests/Physics/{LooseBodiesTests,LooseBodyCellsTests,RagdollTests,ThrownBallTests,ShiftedBodiesTests}.cs"
  - "tests/Itera.Engine.Tests/Systems/{LooseBodySystem,LooseBodyRest,StandInSystem,PhysicsReadSystem,DebugBallSystem,LooseBodyModelSystem}Tests.cs"
  - "tests/Itera.Engine.Tests/Systems/PhysicsTestHarness.cs"
---

## Loose bodies

Physics is split over these rules files:

- This file: making, keeping, and removing loose bodies, ragdolls, and stand-ins.
- `.claude/rules/physics-voxels.md`: what bodies collide with, solid models, play without voxel terrain, the folder's map,
  and the Bepu package notes.
- `.claude/rules/physics-world.md`: the records by handle, a game's own bodies, the solver, gravity, and the warm-up.
- `.claude/rules/physics-contacts.md`: physics materials, bounces, impact events, layers, groups, and trigger zones.
- `.claude/rules/physics-mover.md`: the character mover and its building blocks.
- `.claude/rules/physics-rays.md`: mesh colliders, the shared ray, and the hit history.

A ball, a lifted piece, or a part cut off a model is a loose body. Bepu moves it as the convex hull of its voxels
(`LooseBodies`). A piece of more than `LooseBodies.MaxHullPoints` box corners is hulled from its farthest points in
`LooseBodies.HullDirections` directions.

### Making, keeping, and removing bodies

- A ball is made from nothing (`LooseBodies.TryCreateBall`): one shared piece of 1 cm voxels for each radius and
  material (`VoxelModels.MakeBall`). The console's `balls` drops a row (`DebugBallSystem`, which does not check a ball's
  space).
- A ball goes back into the grid as a sphere of its material, filled into the air after it rests for
  `Engine:Physics:SettleSeconds` (`LooseBodySystem`, the console's `settle`). A lifted piece goes back as a piece edit.
- `Engine:Physics:MaxLooseBodies` limits the bodies. More removes the oldest, a ragdoll whole (`LooseBodyRoom`).
- Cut bodies go a set time after they rest, also when frozen, and at the latest a set time after they were made
  (`LooseBodySystem.CutRestSeconds`, `CutMostSeconds`).
- A body made still counts as resting at once.
- Bepu runs on `Engine:Physics:Threads` worker threads. One thread makes the steps repeat run to run, for tests and
  fast-forward runs.
- Cut parts are hulled and weighed in their model's voxels.

### Ragdolls

- `LooseBodies.TryCreateRagdoll` makes a ragdoll from a model's parts with joints from its skeleton.
- For a figure drawn from a mesh, a ragdoll is made from shapes (`RagdollShape`: a capsule between two points, or a
  sphere, each with a radius and a mass). Those bodies have no models, so nothing draws them.
- A ragdoll of shapes may start turning round a point, as a body toppling about its feet: each body turns at the spin
  and moves as its middle turns round the point.
- The game reads their poses (`PivotOf`, `OrientationOf`) and poses what it draws with them (the rig's `Place`). `Holds`
  with `BornOf` tells a reused handle. No engine code calls either one.

### Characters and bodies

- The mover reads balls as voxels (`LooseBodyVoxels`). Characters are blocked by them and stand on them, but push
  rather than climb them.
- A body under the feet within the step height carries a character up onto it only where the character fits over its
  top. A solid's face moved into a character, such as a gate's leaf, never carries it up a step: the character stays,
  and what overlaps it does not hold it.
- A character near a body has a stand-in, a Bepu cylinder of its mass (`StandInSystem`, `CharacterStandIn`). It is
  absent while `Bodiless`, and removed at the next step when its character is destroyed or loses the component.
- `PhysicsReadSystem` reads each body's pose and each stand-in's push after a step. `LooseBodyModelSystem` hands the
  bodies' models to the renderer.
- When the match's origin moves, `PhysicsWorld.Shift` moves every body and static by the whole metres it moved. It writes
  their poses and the broad phase's bounds alone. Nothing wakes, and velocities, joints, and contacts keep
  (`OriginShifts`, `.claude/rules/engine.md`).
