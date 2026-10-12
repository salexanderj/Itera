# Physics materials, collision groups, and gravity set by the game

Order of work item 6 (`docs/framework-limits.md`), built 2026-10-11 in twelve phases. Before it, limit 5 read: "Physics
settings are fixed. One gravity vector, one contact material (`NarrowPhaseCallbacks`), no collision groups or
triggers, fixed solver settings." This file records what a game can now set, the choices made and why, what the
reviews and tests found on the way, and what was left for later. The rules a change must keep are in
`.claude/rules/physics.md`; the steps for a game are in the `scaffold` skill.

## What was there

One Bepu simulation per match (`PhysicsWorld`, scoped): gravity a constant (0, -9.81, 0) for every body; one material
for every pair (friction 1, contact springs of 30 Hz critically damped, recovery at most 2 m/s); contact rules only by
`BodyKinds` (stand-ins, cut bodies, voxel levels); `SolveDescription(8, 1)` and damping 0.03 fixed. Nothing read
contacts; there were no triggers or events. Characters are not Bepu bodies (Itera's mover; a stand-in near loose
pieces); their gravity is their `MovementStats.Gravity`. Particles fell by their own 9.81.

## Lucy's decisions (2026-10-11)

- Gravity: one vector for the match, set by an option and during play (which wakes the sleepers), and a share of it for
  each body.
- Characters fall by the match's gravity's share of the Earth's, and keep their jump's launch speed, so under less
  gravity they jump higher (half gravity: twice the height and the time). Particles follow the match's gravity, its
  direction too. Down stays -Y for characters, the mover, the terrain, and navigation.
- Materials by name (friction, bounciness) on bodies, mesh colliders, and voxel materials, the voxel cells split by
  physics material; friction combines as the square root of the product, bounciness as the larger; today's material is
  the default.
- Layers (32, a table of which touch) and groups (members never touch); the engine's own rules stay first.
- Triggers (box, sphere, capsule; bodies and characters) and impact events for marked bodies.
- Characters feel the ground's material.
- The solver's settings become options at today's values, changeable in a match.
- Gravity is each peer's own (nothing on the wire). Triggers see a client's mirrors.
- A demo in the mesh sample, which Lucy tried before its commit.
- The bounce: "an exact bounce pass", chosen after the first approach fell short (below).

## The records by handle

The engine keeps one record a Bepu body and one a static (`BodyKinds`: kind, voxel level and base level, ragdoll,
marks, material, layer, group, gravity share, and `Born`, which of the bodies that took the handle it is). Before this
item a removal never reset its record, so a body that took a removed body's handle inherited its kind, level, and
ragdoll: a game's body could become `Cut` and touch only the voxels, and a game's static could read a coarse voxel level
and let level-0 bodies fall through. Every removal now goes through `PhysicsWorld.RemoveBody` and `RemoveStatic`, which
reset the record; a Debug check after each step finds a recorded body removed past them. A game adds its own bodies and
statics with `AddBody` and `AddStatic` and tells a reused handle apart with `Born`.

## Solver and gravity

`Engine:Physics:Substeps`, `VelocityIterations`, `LinearDamping`, `AngularDamping`, and `Gravity` (X, Y, Z), at the
old values; `SetSolver`, `SetDamping`, `SetGravity`, and `SetGravityScale` during a match. The integrator reads a shared
settings object; while every body's share is 1 it reads no shares (the old arithmetic exactly), and otherwise each
active lane's from its record. With more than one substep Bepu integrates a body with constraints once a substep and
one without once a step, so the integrator uses each lane's own duration; damping is applied as e^(t ln(1 - share)).
Bepu 2.5.0-beta.29 swaps the two values of the `(int, int)` tuple to `SolveDescription`, so the engine never passes one.

Particles take the match's gravity as a vector on the GPU (each recipe's multiple of it) and rest on surfaces facing
against it (`dot(normal, up) > 0.7`), checked by `validate.sh` and a shot with sideways gravity.

## Materials and the bounce

A material is friction (0 to 4) and bounciness (0 to 1), registered by name once a run (at most 64; material 0 is the
default, friction 1 and no bounce). The narrow phase sets a pair's friction to the product of the two sides' square
roots, so two default materials give exactly the old friction of 1, and every pair keeps the old springs and recovery.

Bepu's contacts have no coefficient of restitution: its soft contacts stop a pair, and only a spring under-damped and
stiff enough to overshoot pushes it back. That was tried first, as the plan had it (bounciness mapped to the springs'
damping and frequency, calibrated by a drop test): at one substep the springs gave back at most about 6 % of the
approach speed, and softer springs that bounced more let a falling ball sink 10 to 20 cm into the ground first. Lucy
then chose an exact bounce pass:

- The narrow phase records each pair with a bouncy side (or a marked one, below) that touches or will by the step's end
  at its speeds with the match's gravity (within 1 mm), with its deepest contact and the speed the two come together at
  there: their speed before the step, and what gravity adds or takes, by each side's share, over the gap still between
  them (Bepu's speculative contacts stop a pair before its depth reaches 0).
- After the step, on the main thread, the records are sorted by pair (the threads' order does not count), one kept a
  pair (the deepest), and set against the last step's. A pair that began touching at 0.5 m/s or more is pushed apart at
  the contact until it separates at its bounciness times that speed, by the pair's masses and inertias (the effective
  mass along the normal, `1/m + n·((I⁻¹(r×n))×r)` for each side that moves). The bounce does not depend on the substeps.
  Slower pairs rest and roll as Bepu solves them.
- Every voxel static counts as one side, the voxel ground. Without that, a bouncy ball rolling over flat voxel ground at
  4 m/s was thrown 40 cm up at a cell's border: the next cell's static was a new pair, and its edge's contact (a
  "ghost" contact, its normal tilted towards the roller) came with an approach of several metres a second. A plain
  ball rolling there hops about 2 cm at each border, Bepu's own ghost contact, whatever its material.
- Removing a body or static forgets its pairs: a reviewer found that a body taking a removed body's handle did not bounce
  where the old one had rested, the pair looking old.
- A pair Bepu stops meeting only because its bodies sleep (it does not test sleeping bodies against statics or each
  other) still touches, so a ball falling asleep ends nothing and waking begins nothing.

Measured in tests: a ball dropped 1 m onto bounciness 0.5 and 0.8 leaves at 0.97 to 1.01 of that share of its impact
speed, at one substep and at four, and rises a step's rise short of the curve (at 60 Hz about 3 cm for 0.8), less what
damping takes; two balls of bounciness 1 meeting head on trade their speeds.

## Voxel materials

A voxel material maps to a physics material (`MapVoxel`; the voxels have at most 8, the default among them). Each 32-voxel
cell becomes one static for each physics material its voxels have: one walk of the cell each, the other materials left
out with the passable ones, so a cell of one material, and every cell of a world that maps nothing, is built as before.
A cell of several counts as that many against the 8 cells a step may rebuild. A ball made from nothing takes its voxel
material's physics material; a lifted piece keeps the default. Cost, measured in Debug round a 1 m ball over a block of
a random material in each voxel (the worst case): one physics material 20 ms for 32 statics of 50,124 boxes; three
materials 42 ms for 80 statics of 92,504 boxes (the merges of boxes stop at material borders).

## Characters and the ground

A grounded move reports what the body stands on: the highest voxel or mesh triangle within the grounded reach under the
feet's centre, else the highest under four points half the radius out, with its physics material and surface tags (a
mesh collider's material read as it is now; a loose body's, the default). The engine's walking turns on the ground by
its acceleration times the ground's friction, at most 1 (rough ground does not make a character turn faster than its
own tuning): on ice of 0.05 a person takes 1.8 s to reach walking speed, and as long to stop.

## Layers and groups

Up to 32 layers, registered by name, with a table of which touch, the same both ways; the characters' stand-ins and the
voxels' statics each in a layer the game names. Each match copies the run's table and changes its own during play (which
wakes the sleepers). Bodies and statics of one group other than 0 never touch each other. The narrow phase applies the
engine's rules first (stand-ins, cut bodies, voxel levels), then the layers and groups, which only take pairs away. A
loose body whose layer does not touch the characters' is not read by the mover and gets no stand-in. The mover reads
every mesh collider whatever its layer: a gate of a layer balls do not touch still stops players (a mover filtered by
layer was left for later).

## Triggers

A trigger zone is a kinematic Bepu body that never sleeps, a box, sphere, or capsule (at most 256). The narrow phase
pairs it with every body its layers allow and makes no constraint; a pair with a contact of depth 0 or more is an
overlap. Bepu's source (tag 2.5.0-beta.29) confirms what this relies on: it asks the callbacks about kinematic pairs
(`DemoCallbacks`: "it will ask about kinematic-kinematic pairs"), calls `ConfigureContactManifold` before deciding on a
constraint (`NarrowPhaseConstraintUpdate.UpdateConstraintsForPair`), and tests the awake bodies against the static
tree, where the sleeping ones are, so a zone sees a ball asleep inside it without waking it. After the step the
overlaps are sorted and set against the last step's into entered and left; a body or zone removed between steps gives
a removed leave first. Characters are tested by `TriggerCharacterSystem` as upright capsules against the zones' shapes
(segment against sphere, segment against segment, and segment against box by narrowing thirds of a convex distance).
A zone sees only what overlaps it at a step's end: a body faster than the zone's depth and its own width a step can
cross a thin zone unseen.

## Impact events

A body or static marked `ContactEvents` has its pairs followed by the bounce pass: each gives an event as it begins
touching (its sides in a fixed order, the point, the normal, the speed they met at, both materials) and as it ends.
Drops from 1 and 4 m report about 4.4 and 8.9 m/s. Only pairs with a marked or bouncy side do this work. A pair that
strikes again while still touching (a ball rolling into a wall of the same static) gives no new event.

## The demo

The mesh sample's physics corner (`samples/Itera.MeshSample/README.md`): an ice ramp, a gate pane balls pass and
players do not, a trigger zone that chimes, a rubber pad whose balls clang by their impact speed, and floating and slow
balls. Trying it, Lucy saw a sharp lit notch in the soft shadow of a ball above the pad: the near cascade's blocker
search used every other point of the filter's disc, and those eight leaned to one side, a gap of 140 degrees, so a
small caster on that side was missed (fixed: eight points of its own, every 45 degrees). She also saw the see-through
cubes' bottoms flicker with the ground: a blended face in the ground's plane (fixed in the sample, by leaving it out).

## Measurements

Release, 2026-10-11, on a quiet machine (the development PC, Bepu on 4 threads): the physics step before this item
(`179e389`) and after it (`1190798`), driven by a console program on `PhysicsWorld` alone, each scene run 7 times and
the median step of each run's window taken, then the median of three passes, in milliseconds. "Plain" is one material,
no layers, no marks, in both builds: the narrow phase's new reads of the records (layers, groups, materials) and the
bounce pass that finds nothing to follow. "Everything used" (after only) puts every body in one of two layers that
touch, every fifth in a group, two thirds on a material other than the default (half of those bouncy, 0.6), and marks
every body for impact events, so every contact is recorded, sorted, and set against the last step's.

| Scene, while the bodies move (first 2 s) | Before | After, plain | After, everything used |
|---|---|---|---|
| A heap: 384 balls (hulls of 256 points, 12 cm) poured into a pit 3 m square | 0.855 | 0.866 (+1.3 %) | 0.950 (+11 %) |
| A pile: 238 boxes (30 by 20 by 40 cm) dropped on one place | 0.218 | 0.216 | 0.233 (+7 %) |
| 384 balls thrown over a floor | 0.542 | 0.552 | 0.414 (bouncier: fewer contacts) |
| The same balls rolling (the next 8 s) | 0.226 | 0.227 | 0.299 (+0.07 ms) |

The plain step costs what it did (the differences are within the passes' spread, about 2 %). Every body marked and
bouncy costs 7 to 11 % more while bodies touch; a game pays it only for the bodies it marks or makes bouncy. Asleep, every
scene steps in 0.01 to 0.02 ms, as before.

## Left for later

- Materials per triangle of a mesh collider (one a collider today), and per lifted piece (the default).
- A mover filtered by layer (it reads every mesh collider and every loose body that touches the characters).
- Impacts while a pair already touches (a ball rolling into a wall of the ground it rolls on).
- Triggers of mesh shapes, swept triggers, and rays filtered by layer.
- Gravity on the wire (each peer's own today) and gravity in other directions for characters, the mover, and the
  terrain.
