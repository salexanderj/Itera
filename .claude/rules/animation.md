---
paths:
  - "src/Itera.Engine/Animation/**"
  - "src/Itera.Engine/Maths/**"
  - "tests/Itera.Engine.Tests/Animation/**"
  - "tests/Itera.Engine.Tests/Maths/**"
---

## Poses, clips, and foot IK

`src/Itera.Engine/Animation/` (namespace `Itera.Engine.Animation`) poses jointed bodies; no engine system uses it yet,
so a game drives it. A `Skeleton` is up to 32 bones (`MaxBones`), each with a parent, a rest turn, a joint axis and its
swing, a share of the look (`LookShare`), and its legs (`LegChain`: hip, knee, ankle, foot). A `BodyPose` (this namespace's, not the
`Components.BodyPose` of a loose body) is a turn per bone, mirrored and blended; an `AnimationClip` is keyframes that add onto a pose, mirrored, with legs it claims. `Aim`
spreads a look at a point over the skeleton's look bones; `LimbPose` turns a bone towards a world direction and reaches
two bones to a point in a plane. `FootPlacement` puts any number of feet on the voxel ground with two-bone IK (probing
the ground under each foot through `VoxelCharacterMover`), tilts the trunk, and softens the reach; `SegmentPlacement`
lays a legless body along the ground; `SupportPoints` gives a part's lowest corners for resting on; `StanceGround`
rests a body on its support points with the slope's tilt (given its skeleton, its support points, and a standing lift
for when none is left). `CharacterPoses` stores each body's current and previous pose, lift, sway, and yaw, and blends
between steps.

`src/Itera.Engine/Maths/` holds what every peer must compute alike: `DeterministicMath` (SplitMix hashing, seeded
draws, gradient noise, exact integer roots, `Turns` and `CosSin` from their series; integer operations and + - * / and
sqrt only) and `Rotation` with `RigidTransform` (3x3 doubles built through `CosSin`, blended through quaternions), so a
body posed the same way fills the same voxels on every machine. Their pinned hashes (`DeterministicMathTests`) guard
every generator built on them.
