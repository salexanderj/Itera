---
paths:
  - "src/Itera.Engine/Systems/**"
  - "src/Itera.Engine/Components/**"
  - "tests/Itera.Engine.Tests/Systems/**"
---

## The engine's components and systems

`src/Itera.Engine/Components/` holds the ECS components the engine's systems share (namespace
`Itera.Engine.Components`): cameras (`CameraPose`, `CameraLens`, `FlyCamera`, `ActiveCamera`, `LookKick`), characters
(`LocalPlayer`, `RemotePlayer`, `Bodiless`, `PlayerCommand` with `PlayerButtons` and a game's own `Actions` bits,
`PlayerMode` with `Flying` and `Crouching`, `ThirdPersonView`, `CharacterSize` with `ForHeight`, `CharacterPosition`,
`CharacterVelocity`, `CharacterContact`, `EyeSmoothing`, `MovementStats` with a person's `Default`), mesh assets
(`MeshPlacement` with `At`, `DrawnMesh` with `Of`, `MeshAnimator` with `Playing`, `ConsoleMesh`), physics
(`LooseBody`, `BodyPose` (a loose body's pose, not `Animation.BodyPose`), `BodyBox`, `CutPiece`, `RemoteBody`,
`CharacterStandIn`), and networking (`NetEntity`).
The components' positions are local to the match's origin and move with it (`OriginShifts.ShiftedComponents`;
`.claude/rules/engine.md`); velocities, directions, and boxes about a pivot do not.
`src/Itera.Engine/Systems/` holds the systems (namespace `Itera.Engine.Systems`). The engine registers no system: a game
registers those it wants, in run order, in its composition (the starter's `GameServiceCollectionExtensions`), and the
engine registers what they share (`PlayArea` round the match's grid, or `PlayArea.WithoutTerrain` with no terrain,
`PlayerInputBlock`, `WorldResets`, `IBenchmarkHost` with terrain, `MovementKeys`).

The player (the starter's `PlayerSpawnSystem` makes it with these components):

- `PlayerInputSystem` (fixed) builds the local player's `PlayerCommand` from the step's input by the movement keys
  (`MovementKeys`: Forward, Back, Left, Right, W A S D; Jump, Space, once a press; Sprint, Shift; Crouch, Ctrl; Fly down,
  C; Third person, Tab, which a game whose camera is always over the shoulder leaves out, `new MovementKeys(bindings,
  thirdPersonKey: false)` registered after the engine's; the wheel moves the shoulder camera), and the look from the
  mouse while the pointer is captured; `fly` and `camera` are its commands. A game's own buttons go in `PlayerCommand.Actions`, set by the game's input
  system after it. While `PlayerInputBlock.Blocked`, the input reads as nothing pressed; a `Bodiless` player only looks
  about.
- `PlayerMovementSystem` (fixed) moves every entity with a command and `MovementStats` through the voxels with
  `VoxelCharacterMover`: steering faster on walkable ground than in the air (on the ground by its acceleration times the
  ground's friction, at most 1, `CharacterContact.GroundMaterial` and `PhysicsWorld.FrictionOf`: on ice of 0.05 a person
  takes 1.8 s to reach walking speed and as long to stop), jumping, gravity (its own times the match's
  share of the Earth's, given the match's `PhysicsWorld`; a jump keeps its own launch speed), sliding on steep ground,
  pushes from loose bodies, the eye eased after a step (`EyeSmoothing.Offset`) and to a new eye height at 2 m/s
  (`EyeSmoothing.Height`; a size a movement mode sets, a roll's or a slide's, is eased from the eye's height before it
  in the same step, as a crouch is). Each character crouches while Crouch is held (0.77 of its height and eye, 0.4 of its
  speed, no jump or sprint; it stands once the standing body fits) and moves backwards at 0.7 of its speed without
  sprint: the local player, a remote one a host moves from its commands, and a game's characters alike. One
  character's step is also `CharacterStepper.Step` (scoped, with the game's movement mode): the same code on one entity,
  for running a step again (a client replaying its commands after the host's correction); a flying one only stops. While flying, its feet follow the fly camera. A game's `IMovementMode` (registered in the match's scope, given
  to the system) is offered each walking player's step first, with the player's entity and world for a component of
  its own (no structural changes in it): it may take the step with its own velocity, step height (0: no stepping
  up), whether the body steps up in the air too (`MovementModeMove.StepsInAir`: a jump whose feet come within the step
  of a wall's top lands on it), and jump (`MovementModeMove.Jumped`, kept in `CharacterContact.Jumped` as the engine's
  own), and the move,
  contact (`CharacterContact.WallNormal` among it), and eye then go on as for any step. It sees the command as the
  engine's walking limits it (`MovementModeInput.Command`: crouched, without Jump or Sprint and at a crouch's pace) and
  as the player asked it (`Asked`), so a game tells a crouch pressed while sprinting (a slide) from a plain one; the
  engine's `MovementProbes` find a wall ahead, its ledge's top, and room to stand (`.claude/rules/physics.md`). The
  engine's `ClimbingMode` is a ready-made one, registered with the surface tags it climbs, for players with a `Climber`:
  Space with such a wall within 0.3 m ahead grabs it instead of jumping; on it W and S climb at 1.5 m/s, A and D move
  along it at 2 m/s, nothing held hangs, Space jumps off (4 m/s away, 5 up), Ctrl lets go (0.3 s before a new grab);
  climbing past its top pulls up onto it where the body has room (up, then over the edge, through the mover); a wall
  that ends lets go. `pos` and `tp` are its commands (`tp` takes x y z [yaw pitch],
  the form `pos` prints, `CameraPoseText`, or a game's named places with their eye, yaw, and pitch: the starter's are its
  benchmark views; all absolute, metres from the map's middle whatever the match's origin, `WorldPoint`); a `tp` to an
  eye `Engine:Origin:ShiftMetres` or more from the origin asks `OriginShiftSystem` to move the origin there at the next
  step's start (`OriginShifts.RequestFollow`), before the jump, so a jump of any length lands exactly; the jump moves
  the fly camera and the active camera with the player, so a second step before the next frame does not follow the
  camera back.
- `PlayerCameraSystem` (frame) puts the active camera at the eye between the last two steps (with a client's eased
  correction, `EyeSmoothing.Correction`, `.claude/rules/replication.md`), looking where the command looks plus the mouse
  since the step, or behind a shoulder (`ThirdPersonCamera`: 2.5 m back, 1.5 to 5 m by the
  wheel, the right shoulder unless `ThirdPersonView.Left` (1 the left, a game easing it between to swap), pulled in by
  voxels, by the voxel objects where the game gives it `ObjectRays`, and by the mesh colliders where it gives it
  `MeshColliders`; the aim the same, and stopped by a character's hit shape where the game gives it a `HitHistory` and
  a tick, the aimer's own left out, so a crosshair on a character sends a shot from the eye into it; `Aim`, `AimPoint`,
  and `LookPoint` also take the match's `CollisionSources` in place of its voxels, objects, and colliders, so the aim
  meets what rays meet, the solid models a game places among them), turned by a
  game's kick of the view (`LookKick` on the local player, radians up and to the left: a weapon's recoil, which the
  game also adds to what it aims through the camera; the command's look is left as it was); it makes the camera entity
  from a start pose (`CameraRules.CreateCamera`).
  `DebugFlyCameraSystem` flies it while the player flies (`flyspeed`; with `Engine:Voxels:DebugWalkSpeed` above 0 it
  flies a 100 m circle round the map's middle `Engine:Camera:CircleHeightMetres` up, which must clear the game's hills),
  kept to the play area (`PlayArea`: the world and 1 km round it, absolute: `Contains`, `HoldsEye`, and `ClampEye` take
  `WorldPoint`s); it too reads nothing while `PlayerInputBlock.Blocked`. `CameraExtractSystem` hands
  the camera to the renderer with its lens (`CameraLens`: perspective, drawn at the player's field of view and then
  `Zoom` times closer, a game's aim down sights, or below 1 wider, to `CameraLens.LeastZoom` (0.5), a game's view
  at speed; or orthographic, `CameraLens.Orthographic`, drawn as it is; the pose's `Roll` turns the picture about the way it
  looks, a game's tilt, set again each frame after `PlayerCameraSystem`, which writes the pose whole;
  `.claude/rules/rendering.md`), `AtmosphereSystem` the sky and the look (`RenderLook`) from a game's
  `IAtmosphereSource`, asked at the camera's absolute place (`WorldPoint`).
- `ToolAim` (static) gives where the local player aims from and along (through the third-person camera, with the voxel
  objects given `ObjectRays`), and the first voxel a tool's ray meets past passable voxels (`Raycast` over the terrain),
  or the first surface of the match (`Raycast` over `CollisionSources`: the terrain, the voxel objects, and the mesh
  colliders).

The origin: `OriginShiftSystem` (fixed, first, so the whole step runs in one origin) moves the match's origin to the
active camera's whole metres once the camera is `Engine:Origin:ShiftMetres` (512) or more from it on any axis, or to
the point a jump asked for (`tp`; then the camera is not read that step), through `OriginShifts`
(`.claude/rules/engine.md`); its `Initialize` moves the origin a metre and back, so the first move in play does not
compile the code; registered anywhere but first, it throws at the first step. A game that leaves it out keeps its
first origin.

Trigger zones and characters: `TriggerCharacterSystem` (fixed, after the characters move and their stand-ins) tests
each entity with a `TriggerSensor` (and `CharacterPosition`, `CharacterSize`; not `Bodiless`) as an upright capsule of
its size at its feet against every zone of the match's physics whose layer touches the characters', and writes the
match's `CharacterTriggers` (scoped, the engine's): `Events` (`CharacterTriggerEvent`: entered or left, the zone, the
entity, and removed when the entity, its sensor, or the zone went; a character that turns bodiless leaves plainly),
sorted by entity and zone, read by the systems after it that step and the others at the next, and `IsInside`; at most
1,024 pairs, the rest dropped and counted.

The world and bodies: `StreamingCentreSystem` (fixed, last) centres streaming on the local player's eye (or the active
camera while flying), round remote players on a host, and round loose bodies; `MapResetSystem` (fixed, after the origin and the game's spawns) runs
`cleanup` (every loose body and particle) and `reset` (the world back to the seed's, counted in `WorldResets` for a
game's systems to put their things back; without voxel terrain, the clean-up and the count only); the physics systems,
`LooseBodySystem`'s rest handler without terrain (`ILooseBodyRest`) among them, are in `.claude/rules/physics.md`;
`DebugOutlineSystem` draws bodies' boxes and stand-ins with the debug lines.

Mesh assets: `MeshDrawSystem` (frame, after `LooseBodyModelSystem`) hands every entity with a `MeshPlacement` (the
pose at the last two fixed steps and a uniform scale; a fixed system that moves it copies the pose into the previous
one first) and a `DrawnMesh` (the asset's index from `MeshAssets.Find`, a tint, a `ModelShadow`) to `MeshInstances`, its
pose blended by the frame's `Alpha`; one with a `MeshAnimator` (a clip of the asset's rig by index, `Rig.FindClip`, -1
for the rest pose; its time at the last two steps; a speed, 0 to hold; `Once` to stop at the end) is posed by the clip
at the blend of its two times, wrapped by a looping clip's length (`MeshInstances.AddPosed`, `Rig.WritePalette`). A
change of clip may fade (`MeshAnimator.FadeTo(clip, seconds)`: the old clip goes on at its own speed, and each joint
blends from it to the new one, rotations along the shorter way, by `FadeWeight` at the frame's `Alpha`). A second clip may be blended in (`MeshAnimator.SetBlend(clip, weight, shift)`, set by a fixed system each step: posed at the
same share of its length as the clip is of its own, and `shift` of its length further (`BlendShift`: clips whose steps
fall at different shares kept in step), so two gaits keep step, each joint moved towards it by the weight
between the last two steps at the frame's `Alpha`, `BlendShare`): directional movement between a forward clip and a
sideways one; the clip's own times stay the main clip's (a game's footfalls read them). One with a
`ShapedPose` is then shaped by the game's `IMeshPoseShaper` (scoped, given to the system: layers, aim, IK through the
rig's tools, reading the entity's own components; no structural changes, no allocation), told why it poses
(`MeshPosePurpose`: `Drawing` here, `HitShapes` where a game poses a body for its hit shapes; what a game keeps of its
own recent frames, such as springs or a ragdoll, is for drawing alone, so a body is never hit where it is not drawn). One with a
`SmoothedPose` has its changes smoothed as it is drawn, after the shaping (`PoseSmoothing`, dead blending, in place of a
cross-fade; drawn only): each change its animator marks (`MeshAnimator.Changes`: every `FadeTo`, a `SetBlend` of
another clip, and a game's `MarkChange` when its own layer switches at once) starts from the pose drawn the frame before,
each joint going on at the speed it had, slowing, and blends into the new pose, eased, over `SmoothedPose.Seconds` (0.2 s
by default); a change during another starts from what was drawn; at most 256 at once, each slot checked against its
entity and let go when not drawn. So a game may change clips at once and let the smoothing carry them. An entity with a
`MeshAttachment` and a `DrawnMesh` (no placement) is drawn at its holder's joint as the holder is posed (and shaped,
and smoothed) this frame, then its offset and turn in the joint's space (a rifle in a hand); nothing once the holder is
gone.
`MeshPoses` (`Placed`, `Sample`, `JointInWorld`) poses a mesh as the system draws it, for a game posing a body it does
not draw (a host judging a hit, its shaper told `MeshPosePurpose.HitShapes`).
`MeshAnimatorSystem` (fixed, after every system that chooses a clip) moves each clip's time on by its speed, so clips
follow the match's time and stop while it is paused; at a looping clip's end it wraps both times together; during a
fade it moves the old clip and the fade on too, and ends the fade a step after it is whole. `meshes` is
`MeshDrawSystem`'s command: the run's assets with their rigs' joints and clips; `place NAME [SCALE] [CLIP [SECONDS]]` 4 m
in front of the camera, facing it, dropped to the voxel ground by a ray where there is one, playing CLIP or held at
SECONDS of it; `test COUNT [NAME [CLIP]]` in rows, each from another time of the clip; `clear` removes those it placed
(`ConsoleMesh`). A game places its own (the starter's `FoxSystem`).

The benchmark (`BenchmarkSystem`, frame, after the fly camera; a game registers it when `Engine:Benchmark:Enabled`,
with its own views, `BenchmarkView`, absolute points): it puts the camera at each view, waits until it has loaded
(`EngineBenchmarkHost`), settles, measures, flies the circle, and exits; `tools/benchmark` reads its log lines, which
stay as they are.
