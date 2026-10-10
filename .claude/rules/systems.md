---
paths:
  - "src/Itera.Engine/Systems/**"
  - "src/Itera.Engine/Components/**"
  - "tests/Itera.Engine.Tests/Systems/**"
---

## The engine's components and systems

`src/Itera.Engine/Components/` holds the ECS components the engine's systems share (namespace
`Itera.Engine.Components`): cameras (`CameraPose`, `CameraLens`, `FlyCamera`, `ActiveCamera`), characters
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
  C; Third person, Tab; the wheel moves the shoulder camera), and the look from the mouse while the pointer is captured;
  `fly` and `camera` are its commands. A game's own buttons go in `PlayerCommand.Actions`, set by the game's input
  system after it. While `PlayerInputBlock.Blocked`, the input reads as nothing pressed; a `Bodiless` player only looks
  about.
- `PlayerMovementSystem` (fixed) moves every entity with a command and `MovementStats` through the voxels with
  `VoxelCharacterMover`: steering faster on walkable ground than in the air, jumping, gravity, sliding on steep ground,
  pushes from loose bodies, the eye eased after a step (`EyeSmoothing.Offset`) and to a new eye height at 2 m/s
  (`EyeSmoothing.Height`). The local player crouches while Crouch is held (0.77 of its height and eye, 0.4 of its
  speed, no jump or sprint; it stands once the standing body fits) and moves backwards at 0.7 of its speed without
  sprint. While flying, its feet follow the fly camera. A game's `IMovementMode` (registered in the match's scope, given
  to the system) is offered each walking player's step first, with the player's entity and world for a component of
  its own (no structural changes in it): it may take the step with its own velocity and step height (0: no stepping
  up), and the move, contact (`CharacterContact.WallNormal` among it), and eye then go on as for any step; the
  engine's `MovementProbes` find a wall ahead, its ledge's top, and room to stand (`.claude/rules/physics.md`). The
  engine's `ClimbingMode` is a ready-made one, registered with the surface tags it climbs, for players with a `Climber`:
  Space with such a wall within 0.3 m ahead grabs it instead of jumping; on it W and S climb at 1.5 m/s, A and D move
  along it at 2 m/s, nothing held hangs, Space jumps off (4 m/s away, 5 up), Ctrl lets go (0.3 s before a new grab);
  climbing past its top pulls up onto it where the body has room (up, then over the edge, through the mover); a wall
  that ends lets go. `pos` and `tp` are its commands (`tp` takes x y z [yaw pitch],
  the form `pos` prints, `CameraPoseText`, or a game's named places with their eye, yaw, and pitch: the starter's are its
  benchmark views; all absolute, metres from the map's middle whatever the match's origin, `WorldPoint`).
- `PlayerCameraSystem` (frame) puts the active camera at the eye between the last two steps, looking where the command
  looks plus the mouse since the step, or behind the right shoulder (`ThirdPersonCamera`: 2.5 m back, 1.5 to 5 m by the
  wheel, pulled in by voxels); it makes the camera entity from a start pose (`CameraRules.CreateCamera`).
  `DebugFlyCameraSystem` flies it while the player flies (`flyspeed`; with `Engine:Voxels:DebugWalkSpeed` above 0 it
  flies a 100 m circle round the map's middle `Engine:Camera:CircleHeightMetres` up, which must clear the game's hills),
  kept to the play area (`PlayArea`: the world and 1 km round it, absolute: `Contains`, `HoldsEye`, and `ClampEye` take
  `WorldPoint`s); it too reads nothing while `PlayerInputBlock.Blocked`. `CameraExtractSystem` hands
  the camera to the renderer with its lens (`CameraLens`: perspective, drawn at the player's field of view, or
  orthographic, `CameraLens.Orthographic`, drawn as it is; `.claude/rules/rendering.md`), `AtmosphereSystem` the sky and the look (`RenderLook`) from a game's
  `IAtmosphereSource`, asked at the camera's absolute place (`WorldPoint`).
- `ToolAim` (static) gives where the local player aims from and along, and the first voxel a tool's ray meets past
  passable voxels.

The world and bodies: `StreamingCentreSystem` (fixed, last) centres streaming on the local player's eye (or the active
camera while flying), round remote players on a host, and round loose bodies; `MapResetSystem` (fixed, first) runs
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
blends from it to the new one, rotations along the shorter way, by `FadeWeight` at the frame's `Alpha`).
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
