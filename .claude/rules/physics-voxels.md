---
paths:
  - "src/Itera.Engine/Physics/{LooseBodyVoxels,SolidModelVoxels,SolidColumns,ILooseBodyRest,VoxelColliders}.cs"
  - "tests/Itera.Engine.Tests/Physics/{LooseBodiesWithoutTerrainTests,SolidModelVoxelsTests,VoxelCollidersTests,CoarseCollidersTests,SurfaceCollidersTests}.cs"
---

## Bodies and voxels

Loose bodies are made, kept, and removed as `.claude/rules/physics.md` says. The rest of the Bepu world is in
`.claude/rules/physics-world.md`.

### What a body collides with

- Statics of voxel boxes built near it (`VoxelColliders`), in cells of `VoxelColliders.CellVoxels` voxels on an edge:
  `VoxelColliders.CellSize` on a grid of 10 cm or finer, and fewer on a coarser one, down to a brick, so the edge stays
  near `VoxelColliders.MostCellCentimetres` (`VoxelColliders.CellShiftFor`). Keep a cell small: one is built whole as
  a body first reaches it, in that step.
- On voxels coarser than 1 cm a column's top voxel ends where its density puts the ground's surface, as a box to the
  millimetre (`PhysicsWorld.SurfaceBox`).
- Bodies whose parts are of coarse voxels collide with the level nearest their voxels (`VoxelColliders.CoarseLevelOf`),
  in cells of the same size in that level's voxels.
- The voxel objects the world places that reach into a cell near a body (`.claude/rules/objects-meeting.md`).
- A body with no level loaded round it freezes (made kinematic) until one loads. So does a body round which an area of
  objects is not filled.
- A ragdoll's bodies freeze and thaw together. Bepu drops joints between kinematic bodies, and
  `VoxelColliders.GroupThawed` adds them again at the thaw.
- Cut bodies (`BodyKind.Cut`, a `CutPiece` entity) touch the voxels, the voxel objects' statics, and the mesh
  colliders, not other bodies or characters. Where their own
  level is not loaded they collide with the finest coarser level that is, up to the coarse collider level
  (`BodyKinds.BaseLevel`, `Level`).
- A game's mesh colliders and statics collide with bodies of every level (`.claude/rules/physics-rays.md`).

### Solid models

Solid models that are not loose bodies (`SolidModelVoxels`, `SolidColumns`) are solid to the mover and met by rays. Bepu's
bodies and the camera do not meet them.

- `SetPose` places one each step at an absolute point (`WorldPoint`: a local position with the match's origin).
- Its voxels are the model's unit at their level (`Add`'s unit, 1 cm by default). Each box is walked in the solid's own
  voxels.
- `SolidModelVoxels.Raycast` walks a ray from an absolute point column by column through each placed solid's box.
  `CollisionSources` asks it.
- Each column's runs are met by height (`SolidColumns.Runs`). The answer is a `SolidHit`: the solid's id, the distance,
  and the face entered, turned into the world. The ray allocates nothing.

### Without voxel terrain

The core registers `VoxelModels`, `LooseBodies`, and `LooseBodyVoxels` either way.

- The models keep their trees in their own memory, freed when the match ends. With terrain, a lifted piece's tree lives in
  the world's memory. There are no lifts, no `VoxelColliders`
  (so no body freezes), and no grid to settle into.
- Bodies meet the game's mesh colliders, fall out under the play area's fall limit, and are read by the mover in cells
  of `Engine:Physics:CollisionCentimetres`.
- A ball that rests for `Engine:Physics:SettleSeconds` goes to the game's `ILooseBodyRest`, registered in the match's
  scope and asked by `LooseBodySystem`. Its answers:
  - `Keep`, and no handler, leaves the ball where it is. It is asked again once the ball has moved (`LooseBody.Kept`).
  - `Remove` removes the ball with its model.
  - `TakeOver` removes the body and leaves the model to the game (`LooseBodies.Release`, with where it lay from
    `LooseBodies.Rested`). The game draws it and later removes it with `VoxelModels.Remove`, never `Free`.
- A save without terrain holds no bodies, and `SettleThrown` does nothing.
- `MapResetSystem`'s `reset` only cleans up and counts the reset without a world.

### Files in `src/Itera.Engine`

- `Physics/`:
  - The simulation and stand-ins: `PhysicsWorld`, with a game's bodies in `PhysicsWorld.Bodies.cs`, the warm-up in
    `PhysicsWorld.WarmUp.cs`, and the trigger zones in `PhysicsWorld.Triggers.cs`.
  - The records by handle: `BodyKinds`. Materials: `PhysicsMaterials`. Bounces: `PhysicsContacts`. Layers:
    `PhysicsLayers`. Triggers: `PhysicsTriggers`, `TriggerOverlap`.
  - Voxel box statics: `VoxelColliders`.
  - Loose bodies: `LooseBodies` (table, rest, and removal), with `.Pieces.cs` for balls and lifted pieces, `.Parts.cs`
    for cut parts and ragdolls, and `.Hulls.cs` for large hulls.
  - Loose bodies and solid models as voxels: `LooseBodyVoxels`, `SolidModelVoxels`, `SolidColumns`.
  - The mover: `VoxelCharacterMover`, with the mesh colliders as its cells in `MeshColumns`.
  - A game's mesh colliders and the shared ray: `MeshColliders`, `CollisionSources`, `SurfaceHit`.
  - The hit shapes over the last steps: `HitHistory`, `HitShapes`.
  - Bepu's callback structs. The voxels' surface tags are `Voxels/IVoxelSurfaceTags`.
- `Systems/`: `PhysicsReadSystem`, `LooseBodySystem`, `LooseBodyRoom`, `StandInSystem`, `DebugBallSystem`,
  `LooseBodyModelSystem` (`.claude/rules/systems.md`).

### Package notes

- `BepuPhysics 2.5.0-beta.29` is a prerelease and is pinned on purpose. Callback structs live in `Engine/Physics/`.
- `Statics.SetShape` and `StaticReference.SetShape` move a static's bounds but keep the old shape. Change a static's
  shape with `Statics.GetDescription` and `ApplyDescription`. Never free the old shape after `SetShape`.
