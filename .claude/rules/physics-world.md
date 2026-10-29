---
paths:
  - "src/Itera.Engine/Physics/{BodyKinds,PhysicsWorld,PhysicsWorld.Bodies,PhysicsWorld.WarmUp,PoseIntegratorCallbacks}.cs"
  - "tests/Itera.Engine.Tests/Physics/{PhysicsBodiesTests,PhysicsGravityTests,PhysicsSolverTests,PhysicsShiftTests,PhysicsWarmUpTests}.cs"
---

## The Bepu world

Loose bodies are in `.claude/rules/physics.md`. Materials, layers, and triggers are in
`.claude/rules/physics-contacts.md`.

### Records by handle and a game's own bodies

The engine keeps a record for each Bepu body and static by handle (`BodyKinds`). The narrow phase reads the records on
Bepu's worker threads, and the main thread writes them between steps.

- A body's record holds its `BodyKind`, the voxel level it collides with now and the one it was made for, its ragdoll, a
  game's `PhysicsMarks`, its physics material, its share of gravity, its layer and group (`physics-contacts.md`), and
  `Born`, which tells apart the bodies that took one handle.
- A static's record holds its voxel level, its physics material, its layer and group, its marks, and `Born`.
- A handle never recorded reads the default: a loose body at level 0 in no ragdoll, or a static at level 0.
- Every removal resets its record: `PhysicsWorld.RemoveBody` and `RemoveStatic`, which `LooseBodies`, `RemoveStandIn`,
  `VoxelColliders`, and `MeshColliders` use. A body or static that takes a removed one's handle starts afresh.
- In Debug builds `PhysicsWorld.Step` throws when a recorded body was removed past `RemoveBody`.

A game adds its own bodies and statics (`PhysicsWorld.Bodies.cs`):

- `PhysicsWorld.AddBody(description, settings)` records a loose body. It touches the voxels, the game's statics,
  bodies, and stand-ins. It takes the material of `PhysicsBodySettings.Material` and moves with the origin.
- It is frozen where the voxels round it are not loaded, unless it has `PhysicsMarks.KeepsMovingUnloaded`.
- It is not one of `LooseBodies`, so `PhysicsReadSystem` does not read it and nothing counts or settles it.
- `AddStatic` adds a static that bodies of every voxel level touch, as a mesh collider is.
- `RemoveBody` and `RemoveStatic` remove them. The shapes stay the game's. `RemoveBody` first calls `BodyRemoving`,
  which the voxel colliders set to `Forget`.
- `BornOf` and `Holds(body, born)` tell a reused handle apart.
- At a match's end the physics goes before the scope's services are disposed, with every body, static, and shape in
  it. A game's service removes its own as it is disposed only while `PhysicsWorld.IsInitialized`.
- A body added straight through `Simulation.Bodies` has no record and reads the default. Never remove it past
  `RemoveBody` once recorded.

### The solver and damping

- `Engine:Physics:Substeps` and `VelocityIterations` set Bepu's solver. Use `SolveDescription`, never the `(int, int)`
  tuple, which this Bepu swaps.
- `Engine:Physics:LinearDamping` and `AngularDamping` set the shares of speed and spin a body loses each second. A value
  of 1 stops every body each step.
- A game changes them during a match with `PhysicsWorld.SetSolver`, which scales the impulses kept for the next solve to
  the new substeps, and `SetDamping`.
- The console does it with `physics solver I S` and `physics damping L A` (`PhysicsCommands`, which prints the match's
  bodies, statics, solver, damping, and gravity).
- The integrator is `PoseIntegratorCallbacks`, reading `IntegrationSettings`. With more than one substep, Bepu
  integrates a body with constraints once a substep and one without once over the step. Each lane uses its own duration,
  and damping is e^(t ln(1 - share)).
- `PushOn` reads a stand-in's push with the gravity, damping, and substeps the last step ran with, per substep while
  bodies touched it.

### Gravity

- `Engine:Physics:Gravity` is the match's gravity on every dynamic body (`PhysicsWorld.Gravity`, `DefaultGravity`). A
  game changes it during a match with `SetGravity`, which wakes every sleeping body. The console does it with
  `physics gravity X Y Z`.
- Each body falls by a share of it, kept in its record: `SetGravityScale`, or `AddBody`'s `gravityScale`. A share of 1 is
  normal, 0 floats, and a negative share rises. Setting it wakes the body.
- While every share is 1 (`BodyKinds.ScaledBodies` is 0) the integrator reads none. Otherwise it reads each active
  lane's share from its record through `ActiveSet.IndexToHandle` (`IntegrationSettings.Kinds`, `Bodies`).
- `GravityShare` is the gravity's strength over the Earth's (`StandardGravity`), exactly 1 at the default.
  `PlayerMovementSystem`, given the match's `PhysicsWorld`, makes characters fall by their own `MovementStats.Gravity`
  times it, sliding too. It keeps a jump's launch speed of their own gravity, so under less gravity they jump higher.
- A movement mode is given the stats with the scaled gravity. Down stays -Y for characters, the mover, and the terrain.
- `PushOn` takes the stand-in's share.
- With terrain, a floating ball (share 0) at rest settles into a floating sphere of voxels. A game makes floaters as its
  own bodies (`AddBody`).

### The physics warm-up

`PhysicsWorld.StartWarmUp` (`PhysicsWorld.WarmUp.cs`) runs from the match's physics step, once a run
(`WarmUps.Physics`). The loading screen's Physics task waits for it.

- It builds a world of its own with the match's settings and Bepu callbacks, on a thread of its own.
- Into it fall two people's ragdolls, a pile of convex hulls, a kinematic mirror, a cut part, a stand-in, and a trigger
  zone. They land on a voxel cell's static of boxes and a mesh ramp.
- It steps the world, then takes everything away and frees it. Disposing the world stops it at its next step.
- Bepu makes its solver and collision code for each joint, shape pair, and contact kind at run time, which ReadyToRun
  cannot compile ahead. The warm-up makes the first ragdoll of a run cheap.
- A new kind of body, shape, or joint the engine makes goes into it too (`Exercise`).
- Check it with the run's JIT list: `DOTNET_JitStdOutFile` and `DOTNET_JitDisasmSummary=1` on a Release build. First-time
  compiles are those not marked Tier1.
