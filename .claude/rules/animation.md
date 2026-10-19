---
paths:
  - "src/Itera.Engine/Animation/**"
  - "src/Itera.Engine/Maths/**"
  - "tests/Itera.Engine.Tests/Animation/**"
  - "tests/Itera.Engine.Tests/Maths/**"
---

## Poses, clips, and foot IK

`src/Itera.Engine/Animation/` (namespace `Itera.Engine.Animation`) poses jointed bodies for a game to drive. No engine
system uses it. A glTF model's skeleton and clips are another thing: `Assets/`'s `Rig` and `RigClip`, in floats
and for drawing only (`.claude/rules/mesh-assets.md`).

- `Skeleton`: bones (`MaxBones`), each with a parent, a rest turn, a joint axis and its swing, a share of the look
  (`LookShare`), and its legs (`LegChain`: hip, knee, ankle, foot).
- `BodyPose`: a turn per bone, mirrored and blended. It is this namespace's, not the `Components.BodyPose` of a loose
  body.
- `AnimationClip`: keyframes that add onto a pose, mirrored, with the legs it claims.
- `Aim` spreads a look at a point over the skeleton's look bones.
- `LimbPose` turns a bone towards a world direction and reaches two bones to a point in a plane.
- `FootPlacement` puts any number of feet on the voxel ground with two-bone IK, probing the ground through
  `VoxelCharacterMover`, tilts the trunk, and softens the reach.
- `SegmentPlacement` lays a legless body along the ground.
- `SupportPoints` gives a part's lowest corners, and `StanceGround` rests a body on them with the slope's tilt.
- `CharacterPoses` stores each body's current and previous pose, lift, sway, and yaw, and blends between steps.

`src/Itera.Engine/Maths/` holds what every peer must compute alike:

- `DeterministicMath`: SplitMix hashing, seeded draws, gradient noise, exact integer roots, and `Turns` and `CosSin`
  from their series. It uses integer operations and + - * / and sqrt only.
- `Rotation` with `RigidTransform`: 3x3 doubles built through `CosSin`, blended through quaternions.

A body posed the same way must fill the same voxels on every machine. Never use `Math.Sin` or `System.Numerics` helpers
in code that peers must agree on. The pinned hashes in `DeterministicMathTests` guard every generator built on this.
`WorldPoint`, `WorldOrigin`, and `OriginShift` are in `.claude/rules/engine.md`, and `Octahedral` is in
`.claude/rules/mesh-assets.md`.

Tests: `tests/Itera.Engine.Tests/Animation/` (over `HumanoidTestSkeleton`) and `tests/Itera.Engine.Tests/Maths/`
(`DeterministicMathTests`, `RotationTests`, `WorldOriginTests`, `OctahedralTests`).
