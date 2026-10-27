---
paths:
  - "src/Itera.Engine/Systems/**"
  - "src/Itera.Engine/Components/**"
  - "tests/Itera.Engine.Tests/Systems/**"
---

## The engine's components and systems

`docs/reference/game-systems.md` lists the match's systems in run order with what each does. This file holds the rules
for changing them and where their parts are. Test classes: one `<System>Tests` class for each system, in
`tests/Itera.Engine.Tests/Systems/`, plus `PlayAreaTests`, `ThirdPersonCameraTests`, `ToolAimTests`,
`CharacterStepperTests`, `ClimbingModeTests`, and `LooseBodyRestTests`.

### Components

`src/Itera.Engine/Components/` holds the components the engine's systems share (namespace `Itera.Engine.Components`):

- Cameras: `CameraPose`, `CameraLens`, `FlyCamera`, `ActiveCamera`, `LookKick`.
- Characters: `LocalPlayer`, `RemotePlayer`, `Bodiless`, `PlayerCommand` (with `PlayerButtons` and a game's own
  `Actions` bits), `PlayerMode` (`Flying`, `Crouching`), `ThirdPersonView`, `CharacterSize` (`ForHeight`),
  `CharacterPosition`, `CharacterVelocity`, `CharacterContact`, `EyeSmoothing`, `MovementStats` (`Default`),
  `TriggerSensor`.
- Mesh assets: `MeshPlacement` (`At`), `DrawnMesh` (`Of`), `MeshAnimator` (`Playing`), `ShapedPose`, `SmoothedPose`,
  `MeshAttachment`, `ConsoleMesh`.
- Physics: `LooseBody`, `BodyPose` (a loose body's pose, not `Animation.BodyPose`), `BodyBox`, `CutPiece`,
  `RemoteBody`, `CharacterStandIn`.
- Networking: `NetEntity`.

`RemoteBody` marks a loose body that mirrors a body of another peer. `LooseBodySystem`, `PhysicsReadSystem`,
`LooseBodyRoom`, and `MapResetSystem` skip it, and `LooseBodyModelSystem` draws it. Nothing in the engine or the
starter adds it today: a game that mirrors a host's bodies adds it.

A component's positions are local to the match's origin and move with it (`OriginShifts.ShiftedComponents`,
`.claude/rules/engine.md`). Velocities, directions, and boxes about a pivot do not move.

### Registration

The engine registers no system. A game registers the ones it wants, in run order, in its composition. The engine
registers what they share: `PlayArea` (round the match's grid, or `PlayArea.WithoutTerrain`), `PlayerInputBlock`,
`WorldResets`, `IBenchmarkHost` (with terrain), and `MovementKeys`.

- `OriginShiftSystem` is the first fixed system. Registered anywhere else, it throws at the first step. A game that
  leaves it out keeps its first origin.
- `ReplicationReceiveSystem` runs early and `ReplicationSendSystem` last (`.claude/rules/replication.md`).
- `MapResetSystem` runs after the origin and after the game's spawns.
- `MeshAnimatorSystem` runs after every system that chooses a clip.
- `MeshDrawSystem` (frame) runs after `LooseBodyModelSystem`. With a `MeshPoseCulling` it culls by the camera, the sun,
  and the image views set so far, so it runs after `CameraExtractSystem`, `AtmosphereSystem`, and every system that asks
  for an image view. `GameLoop` warns once a run when one of them changes after it.
- `CameraExtractSystem` (frame) runs after every system that moves the camera, including a game's roll and zoom.

### The player

- `PlayerInputSystem` builds the local player's `PlayerCommand` from the step's input by `MovementKeys`, and the look
  from the mouse while the pointer is captured. A game that keeps its camera over the shoulder registers
  `new MovementKeys(bindings, thirdPersonKey: false)` after the engine's. A game's own buttons go in
  `PlayerCommand.Actions`, set by the game's input system after it. While `PlayerInputBlock.Blocked`, the input reads as
  nothing pressed, and a `Bodiless` player only looks about. `fly` and `camera` are its commands. The Move down key (C)
  and the Third person key (Tab) are `MovementKeys` too, and the wheel moves the shoulder camera. While flying, the
  command takes the active camera's look and place (`PlayerButtons.Fly`, `PlayerCommand.Viewpoint`).
- A game that names its own command for flying registers the system with `flyCommand: false`, and asks through
  `PlayerInputSystem.RequestFlying`. `Engine:Camera:StartFlying` starts the first local player flying.
- A game's free camera away from the body sets `PlayerButtons.Viewpoint` and the camera's place in the command, in its
  input system. The engine carries the place to the host, and the feet stay where they are.
- `PlayerMovementSystem` moves every entity with a command and `MovementStats` through `VoxelCharacterMover`: the local
  player, a remote player the host moves from its commands, and a game's characters alike. Ground friction
  (`CharacterContact.GroundMaterial`, `PhysicsWorld.FrictionOf`) and the match's gravity share (`PhysicsWorld`) change
  its steering and its fall. A size a movement mode sets is eased from the eye's height, as a crouch is. A move keeps
  `MovementStats.SideShare` of its speed straight to the side and `BackShare` straight back, and between them by its
  way (`PlayerMovementSystem.DirectionShare`). A move turned further from ahead than `MovementStats.SprintDegrees` (90
  by default), or going back, does not sprint.
- A flying player's feet follow the camera its command carries, kept to the play area, so a host flies a client's
  player where that client's camera is (`.claude/rules/replication.md`).
- `CharacterStepper.Step` (scoped, with the game's movement mode) runs one character's step with the same code, for a
  client that replays its commands after the host's correction.
- A game's `IMovementMode` (registered in the match's scope, given to the system) is offered each walking player's step
  first. It gets the player's entity and world, and makes no structural change. It may take the step with its own
  velocity, step height (0 means no stepping up), `MovementModeMove.StepsInAir`, and `MovementModeMove.Jumped`. The move, the contact
  (`CharacterContact.WallNormal`), and the eye then go on as for any step.
- A movement mode sees the command as the engine's walking limits it (`MovementModeInput.Command`) and as the player
  asked it (`MovementModeInput.Asked`). `MovementProbes` find a wall ahead, a ledge's top, and room to stand
  (`.claude/rules/physics-mover.md`).
- `ClimbingMode` is a ready-made movement mode, registered with the surface tags it climbs, for players with a
  `Climber`. Jump beside such a wall grabs it. On the wall the player climbs, moves along it, or hangs. Jump leaps off,
  crouch lets go, and climbing past the top pulls up onto it where the body has room. A wall that ends lets go.
- `pos` and `tp` are the system's commands. `tp` takes x y z and optionally yaw and pitch, the form `pos` prints
  (`CameraPoseText`), or a game's named place. All are absolute (`WorldPoint`). A `tp` to an eye
  `Engine:Origin:ShiftMetres` or more from the origin calls `OriginShifts.RequestFollow`, and the jump moves the fly
  camera and the active camera with the player.

### Cameras

- `PlayerCameraSystem` (frame) puts the active camera at the eye between the last two steps, with a client's eased
  correction (`EyeSmoothing.Correction`, `.claude/rules/replication.md`). It looks where the command looks plus the mouse
  since the step, turned by the game's `LookKick`, or sits behind a shoulder (`ThirdPersonCamera`).
- `LookKick` on the local player is a weapon's recoil. The game also adds it to what it aims through the camera, and the
  command's look is left as it was.
- `ThirdPersonCamera` is pulled in by voxels, and by the voxel objects and the mesh colliders where the game gives it
  `ObjectRays` and `MeshColliders`. Given a `HitHistory` and a tick, its aim stops at a character's hit shape, leaving
  out the aimer's own. `Aim`, `AimPoint`, and `LookPoint` also take the match's `CollisionSources`.
- `CameraRules.CreateCamera` makes the camera entity from a start pose.
- `DebugFlyCameraSystem` flies the camera while the player flies (`flyspeed`), kept to the play area (`PlayArea`:
  `Contains`, `HoldsEye`, and `ClampEye` take `WorldPoint`s). With `Engine:Voxels:DebugWalkSpeed` above 0 it flies a
  circle round the map's middle at `Engine:Camera:CircleHeightMetres`. It reads nothing while `PlayerInputBlock.Blocked`.
- `CameraExtractSystem` hands the camera and its `CameraLens` to the renderer (`.claude/rules/rendering.md`). The
  pose's `Roll` is set each frame after `PlayerCameraSystem`, which writes the pose whole.
- `AtmosphereSystem` sets the sky and the look (`RenderLook`) from a game's `IAtmosphereSource`, asked at the camera's
  absolute place (`WorldPoint`).
- `ToolAim` (static) gives where the local player aims from and along, through the third-person camera, with the voxel
  objects given `ObjectRays`. `Raycast` over the terrain gives the first voxel a tool's ray meets past passable voxels.
  `Raycast` over `CollisionSources` gives the first surface of the match: the terrain, the voxel objects, and the mesh
  colliders.

### The origin

`OriginShiftSystem` moves the match's origin to the active camera's whole metres once the camera is
`Engine:Origin:ShiftMetres` or more from it on any axis. A jump asked for (`tp`) goes first, and the camera is not read
that step. The move goes through `OriginShifts` (`.claude/rules/engine.md`). Its `Initialize` moves the origin a metre
and back.

### Trigger zones and characters

`TriggerCharacterSystem` runs after the characters move and their stand-ins are placed. It tests each entity with a
`TriggerSensor`, a `CharacterPosition`, and a `CharacterSize` (not `Bodiless`) as an upright capsule against every zone
of the match's physics whose layer touches the characters'. It writes the match's `CharacterTriggers` (scoped):
`Events` (`CharacterTriggerEvent`), sorted by entity and zone, and `IsInside`. A `CharacterTriggerEvent` is entered,
left, or removed (the entity, its sensor, or the zone went; a character that turns bodiless leaves plainly). Systems
after it read the events in the same step, and the others at the next step. The pairs have a fixed limit, and the rest
are dropped and counted.

### The world and bodies

- `StreamingCentreSystem` runs after the systems that move the player, remote players, and loose bodies. It centres streaming on the local player's eye (or the active camera while flying), round remote
  players on a host, and round streamed characters and loose bodies.
- `MapResetSystem` runs the console's `cleanup` (every loose body and particle) and `reset` (the world back to the
  seed's, counted in `WorldResets` for a game's systems to put their things back). Without voxel terrain it cleans up
  and counts only.
- The physics systems, among them `LooseBodySystem`, are in `.claude/rules/physics.md`; its `ILooseBodyRest`
  in `.claude/rules/physics-voxels.md`.
- `DebugOutlineSystem` draws bodies' boxes and stand-ins with the debug lines.

### Mesh assets

The systems that pose and draw mesh assets (`MeshDrawSystem`, `MeshAnimatorSystem`, `MeshPoses`), their components,
and the console's `meshes` are in `systems-meshes.md`.

### The benchmark

`BenchmarkSystem` (frame, after the fly camera) is registered by a game when `Engine:Benchmark:Enabled`, with the
game's own views (`BenchmarkView`, absolute points). It puts the camera at each view, waits until it has loaded
(`EngineBenchmarkHost`), settles, measures, flies the circle, and exits. `tools/benchmark` reads its log lines, which
stay as they are.
