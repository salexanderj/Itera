---
paths:
  - "src/Itera.Engine/Physics/{PhysicsMaterials,PhysicsContacts,PhysicsLayers,PhysicsTriggers,PhysicsWorld.Triggers,NarrowPhaseCallbacks,TriggerOverlap}.cs"
  - "tests/Itera.Engine.Tests/Physics/{ImpactEventsTests,PhysicsLayersTests,PhysicsMaterialsTests,PhysicsTriggersTests,VoxelPhysicsMaterialsTests}.cs"
---

## Physics materials, layers, and triggers

What a game registers for its bodies. The write-up behind these is `docs/physics-materials-and-groups.md`. The steps for
adding one are in the `scaffold` skill.

### Physics materials and bounces

A physics material (`PhysicsMaterial`: friction and bounciness) is registered by name once a run, in the game's
`IRunStartup` or a mod's. The registry is `PhysicsMaterials`, run-wide, with ids of `PhysicsMaterialId`, a byte. The first
match seals it, and a later `Register` throws. At most `PhysicsMaterials.MaxMaterials` materials exist.

- Material 0 is `default`, with friction 1 and no bounce. Every body and static has it unless given another.
- A body takes a material at `AddBody` (`PhysicsBodySettings.Material`) or later (`SetMaterial`, `MaterialOf`). A static
  takes one at `AddStatic`. A mesh collider takes one at `Add` (`PhysicsBodySettings`; `SetSettings`, `SettingsOf`).
- `physics materials` lists them.
- A voxel material takes one by `PhysicsMaterials.MapVoxel`, before the first match (`OfVoxel`). The voxels have at most
  `MostVoxelMaterials` among them, with the default.
- `VoxelColliders` builds a cell's voxels of each physics material into a static of their own, in one walk of the cell
  each (`PartCount`). A cell of one material is one static. The other materials and the passable ones are left out of each
  walk.
- A cell of several materials counts as that many towards `MaxRebuiltCells`.
- A ball made from nothing takes its voxel material's physics material (`LooseBodies.TryCreateBall`). A lifted piece takes
  the default.
- The narrow phase (`NarrowPhaseCallbacks`) reads both sides' records through `BodyKinds.ContactOf`, a table of
  `ContactMaterial` that the match copies at its start. Friction is sqrt(a·b) of the two. Every pair has the engine's
  springs, so a bouncy body sinks no further than any other.

Bepu has no coefficient of restitution, so bounces are made after the step:

1. The narrow phase records a pair with a bouncy side. The pair's bounciness is the larger of the two, and stand-ins are
   left out. It records the pair when it touches, or will by the step's end at its speeds with the match's gravity.
2. A record holds the deepest contact and the speed the two meet at there. The speed is their speed before the step plus what
   gravity adds or takes, by each side's share, over the gap still between them. The record goes on the worker's buffer
   (`PhysicsContacts`, shared by the workers).
3. A pair is two bodies, or a body and a static. Every voxel static counts as one, the voxel ground. A ball that rolls from
   one cell's static into the next has not begun touching anything.
4. After the step, on the main thread, the buffers are sorted by pair, so the threads' order does not count below the
   capacities. One contact is kept for a pair, the deepest.
5. A pair that was not touching the step before and came together at `MinimumBounceSpeed` or more is pushed apart at the
   contact (`PhysicsContacts.Bounce`). It separates at its bounciness times that speed, by its masses and inertias.
   Statics and kinematic bodies take none.
6. Slower pairs rest and roll as Bepu solves them. A pair that strikes while already touching is not bounced.

- Removing a body or static forgets its pairs, so one that takes its handle next begins afresh.
- The bounce does not depend on the substeps.
- `physics` prints the bounces set and the contacts dropped past the buffers.

### Impact events

A body or static marked `PhysicsMarks.ContactEvents` has its pairs followed by the same pass. Set the mark at `AddBody`,
at `AddStatic`, in a mesh collider's settings, or with `SetMarks`. Stand-ins are left out, and only pairs with a bouncy
or marked side do this work.

- A pair gives an `ImpactEvent` as it begins touching (`Began`), as it ends (`Ended`), and as `Removed` when a side was
  removed between steps. A `Removed` event is given first at the next step.
- An event holds its sides (`ImpactSide`: a body or static by handle and `Born`, or the voxels), the point where they met,
  the normal from B towards A, the speed they came together at, and both physics materials.
- The sides are in a fixed order: bodies by handle, then statics, then the voxels. The point is local and moves with the
  origin. The speed includes gravity's part over the gap.
- `PhysicsWorld.ImpactEvents` holds the last step's events for the next step's systems.
- A pair Bepu no longer meets only because its bodies sleep still touches. A ball falling asleep gives no `Ended`, and
  waking gives no `Began`.
- A body rolling over voxel cells' borders begins nothing, since the voxels count as one. A pair that strikes again while
  still touching gives no new `Began`.
- Events past the buffers are dropped and counted with the contacts.

### Layers and groups

A collision layer (`PhysicsLayerId`) is registered by name once a run, in the game's `IRunStartup` or a mod's, in
`PhysicsLayers`. The registry is run-wide, layer 0 is `default`, and the first match seals the names. At most
`PhysicsLayers.MaxLayers` layers exist.

- Which layers touch is a table of pairs, the same both ways (`PhysicsLayers.SetTouches`). Every pair touches until set
  apart.
- The characters' stand-ins are in `CharacterLayer` and the voxels' statics in `VoxelLayer`. Both are the default unless
  set.
- Each match copies the table at its start and changes its own with `PhysicsWorld.SetTouches`, which wakes every sleeping
  body. `Touches` reads it.
- A body or static takes a layer and a group at `AddBody` and `AddStatic` (`PhysicsBodySettings.Layer` and `Group`). A mesh
  collider takes them by its settings. `SetLayer` changes a layer later: it wakes a body, and a static wakes every
  sleeping body. `LayerOf` and `GroupOf` read them.
- Members of one group other than 0 never touch each other. The numbers are the game's.
- The narrow phase applies the engine's rules first (stand-ins, cut bodies, voxel levels), then the layers and groups
  (`BodyKinds.LayersAllow`). Layers only take pairs away.
- A loose body whose layer does not touch the characters' is not read by the mover (`LooseBodies.CopyPoses`) and gets no
  stand-in (`AnyPieceNear`), so characters pass through it.
- The mover reads every mesh collider whatever its layer. A gate of a layer balls do not touch still blocks players.
- `physics layers` prints each layer, the characters' and the voxels', and the layers it does not touch. It prints the
  match's table in a match, else the run's.

### Trigger zones

A trigger zone is a kinematic body that never sleeps and stops nothing (`BodyKind.Trigger`).

- `PhysicsWorld.AddTrigger(shape, position, orientation, layer)` makes one. The shape is a `TriggerShape` box, sphere, or
  capsule along its local y. The zone is local to the origin and moves with it. `MostTriggers` limits the zones.
- `MoveTrigger`, `RemoveTrigger`, `HoldsTrigger`, and `TriggerOf` manage it. A `TriggerId` holds its handle and `Born`.
- The narrow phase pairs a zone with every body its layers allow: moving or asleep, an awake kinematic body meeting the
  sleeping ones, frozen bodies, and a client's mirror. It does not pair statics, stand-ins, or other zones.
- It records a pair with a contact of depth 0 or more on its worker's buffer (`PhysicsTriggers`) and makes no
  constraint.
- After the step the overlaps are sorted by zone and body and set against the last step's.
  `PhysicsWorld.TriggerEvents` holds the result (`TriggerEvent`: `Entered`, `Left`, `Removed`, with the zone, the body,
  and its `Born`). Systems of the next step read it. `IsInside` asks about one body.
- `RemoveBody` and `RemoveTrigger` give a removed `Left` for each body that was in. Those events come first.
- Overlaps and events past the buffers are dropped and counted (`TriggerEventsDropped`, a Warning once a match). A dropped
  overlap reads as a body leaving and entering again.
- A zone sees only what overlaps it at a step's end, with no sweep. A body that moves more than the zone's depth plus its own width in one step can cross it unseen.
- A zone's layer is its record's. `SetLayer` on its body changes it for bodies and characters alike.
- Characters are not Bepu bodies. `TriggerCharacterSystem` (`.claude/rules/systems.md`) tests each entity with a
  `TriggerSensor` as an upright capsule (`TriggerOverlap`) and writes the match's `CharacterTriggers`
  (`CharacterTriggerEvent`, with the entity).
- `physics` prints the zones and the overlaps dropped.
