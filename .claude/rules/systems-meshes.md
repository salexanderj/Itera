---
paths:
  - "src/Itera.Engine/Systems/{MeshDrawSystem,MeshAnimatorSystem,MeshPoses,PoseSmoothing}.cs"
  - "src/Itera.Engine/Components/MeshComponents.cs"
  - "tests/Itera.Engine.Tests/Systems/{MeshDrawSystem,MeshAnimatorSystem}Tests.cs"
---

## The systems that pose and draw mesh assets

The assets themselves are in `mesh-assets.md`, and how they are drawn in `rendering-mesh-assets.md`. The rules every
engine system follows are in `systems.md`. Test classes: `MeshDrawSystemTests` and `MeshAnimatorSystemTests`.

- `MeshDrawSystem` (frame) hands every entity with a `MeshPlacement` and a `DrawnMesh` to `MeshInstances`, its pose
  blended by the frame's `Alpha`. A `MeshPlacement` holds the pose at the last two fixed steps and a uniform scale. A
  fixed system that moves a placement copies the pose into the previous one first. A `DrawnMesh` holds the asset's index
  from `MeshAssets.Find`, a tint, and a `ModelShadow`. A `Hidden` one is not posed or drawn, nor is a mesh attached to
  it, so a game hides a figure for a while without a structural change.
- A `MeshAnimator` poses the entity by a clip of the asset's rig (`MeshInstances.AddPosed`, `Rig.WritePalette`).
  A clip index of -1 shows the rest pose, a speed of 0 holds, and `Once` stops at the end. `MeshAnimator.FadeTo` keeps
  the old clip running while each joint blends to the new one by `FadeWeight`. `MeshAnimator.SetBlend` blends in a
  second clip, shifted by `BlendShift`, set by a fixed system each step. It leaves the main clip's own times unchanged
  (`BlendShare` is the weight), and a game's footfalls read them.
- An entity with a `ShapedPose` is then shaped by the game's `IMeshPoseShaper` (scoped, given to the system). The
  shaper makes no structural change and no allocation, and is told why it poses (`MeshPosePurpose`: `Drawing`, or
  `HitShapes` where a game poses a body for its hit shapes). What a game keeps of its own recent frames is for drawing
  alone, so a body is never hit where it is not drawn.
- An entity with a `SmoothedPose` has its changes smoothed as it is drawn, after the shaping (`PoseSmoothing`). A change
  is each `FadeTo`, each `SetBlend` of another clip, and each `MeshAnimator.MarkChange` of a game. The smoothing is for
  drawing only.
- An entity with a `MeshAttachment` and a `DrawnMesh`, with no placement, is drawn at its holder's joint as the holder
  is posed, shaped, and smoothed this frame, and is not drawn once the holder is gone. A model's named point under a
  joint (`MeshPoint`) gives the attachment's joint, offset, and turn as it is.
- `MeshPoses` (`Placed`, `Sample`, `JointInWorld`) poses a mesh as the system draws it, for a game that poses a body it
  does not draw. Posing for hit shapes (`MeshPosePurpose.HitShapes`) is never culled.
- `MeshAnimatorSystem` moves each clip's time on by its speed, so clips follow the match's time and stop while it is
  paused. It wraps both times of a looping clip together, moves the old clip and the fade on during a fade, and ends
  the fade a step after it is whole.

### Posing only what a list may draw

Given a `MeshPoseCulling`, `MeshDrawSystem` poses an animated mesh only when some list of the frame may draw it: the
camera's view, a cascade it casts into as its `ModelShadow` allows, or an image view due this frame.

- It tests a sphere round every pose of the asset (`MeshBounds.EveryPose`), grown by `MeshDrawSystem.BoundShare`, scaled
  by the placement, and `CullMarginMetres` wider. Without a `MeshPoseCulling` it makes no such spheres.
- Unshaped meshes, such as a swarm's, are culled. A mesh with a `ShapedPose` is always posed, unless the game's shaper
  says it keeps that entity near its clips (`IMeshPoseShaper.KeepsNearClips`, false by default). A shaper may move the
  joints anywhere: a ragdoll or a toppling body posed from its physics bodies while its `MeshPlacement` stays where it
  died. A game's shaper says true for a living figure whose shaping stays within `BoundShare` of its clips.
- A mesh left out is neither sampled nor added to `MeshInstances`. `LastPosed` and `LastCulled` count them.
- An attached mesh follows its holder: it is left out only when its holder is. A holder that may be culled is posed while
  a mesh attached to it may be drawn: the holder's sphere grown by the attachment's offset and the mesh's own reach.
- A smoothed mesh left out keeps its slot (`PoseSmoothing.Hold`). A change marked meanwhile starts from the pose last
  drawn, and a change under way goes on, so it is drawn again as far through the change as posing every frame would
  show. It starts still, with no speed from the poses before.
- With no picture (a run without graphics) or no camera set this frame, every mesh is posed.

### The console's `meshes`

- `meshes` is `MeshDrawSystem`'s command, with the subcommands `place`, `test`, and `clear`. `clear` removes the
  entities it placed (`ConsoleMesh`). A game places its own.
- `meshes place NAME at X Y Z YAW` puts a model at a place in the world, turned, and every `place` prints where in the
  world it put the model. It reads the world through the `OriginShifts` a game's composition passes the system; without
  them it refuses `at`. With `viewpoint` (`rendering.md`) a script shows a model from set places, as `tools/ModelView`
  does.
