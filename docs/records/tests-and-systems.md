# Records from cleaning the tests, systems, engine, animation, and spatial rules

Written 2026-10-19. These passages left the rules files `.claude/rules/tests.md`, `systems.md`, `engine.md`,
`animation.md`, and `spatial.md` when they were cleaned to the docs rules (`.claude/skills/docs/SKILL.md`). They are the
old text, unchanged. They are not kept current.

## `tests.md`: "Where tests live"

A list of test classes and what each asserted. Test names say what they check, and each area's rules file names its own
test classes.

Where tests live:

- `Itera.Engine.Tests/Meshing/`: `SurfaceNetsTests` (closed and consistently wound meshes, blocks side by side meeting
  exactly, an independent reference of the vertex rules), `SurfaceNetsTransitionTests` (fine blocks beside coarser ones
  on random shapes sampled per level: open edges at a seam, sides and corners, high and low; noise at every place and
  side), and `MeshVertexTests`.
- `Itera.Engine.Tests/Voxels/`: `VoxelBlocksTests` (blocks read for meshing against voxel-by-voxel reads), storage, edits, workers, and `VoxelWorld` on small test generators (`SlopeGenerator`,
  `DomeGenerator`, `BoxesGenerator`: a flat floor with boxes of given materials); `VoxelGridTests`, `TallWorldTests` (a world 25,000 layers tall
  storing what one 12 tall does; an edit 100 km up, its two runs of surface layers, rays to it and down 100 km), and
  `BaseTenWorldTests`, worlds on a 10 cm grid (`GridShapesGenerator`: shapes whose faces lie off the 10 cm lines)
  checked against the shapes at each voxel's sample point; pinned content hashes; rays and
  collision queries against voxel-by-voxel walks; row spans against the point test (`VoxelEditingTests`); large edits
  spread over steps (`DeferredEditTests`); resets (`VoxelResetTests`); pieces and save formats (`VoxelPieceTests`);
  models (`ModelEditingTests`); `EditLogTests` (with a game's sections after the pieces, damaged sections, and a frozen
  log writing only the edits it had).
- `Itera.Engine.Tests/Systems/`: each engine system's behaviour and allocation, over `PhysicsTestHarness`,
  `FlatGenerator`, `FlatTestGenerator`, `RampGenerator`, `FakeTickInput`, and `FakeFrameInput`; the play area
  (`PlayAreaTests`); `LooseBodySystem` without voxel terrain, with a game's rest handler (`LooseBodyRestTests`, over
  `MeshGroundHarness`); `ClimbingMode` over mesh walls through `PlayerMovementSystem` (`ClimbingModeTests`).
- `Itera.Engine.Tests/Voxels/Generation/`: the shapes' row spans against point tests, fractal noise, the jittered grid,
  the scalar field, the field tint map with `VoxelTintSampler`, and the heightfield generator (every description against
  its filled voxels, a column's range and its layers standing for every layer, fills against a voxel-by-voxel reference, `HillsGenerator`).
- `Itera.Engine.Tests/Objects/`: voxel objects over `PlacingGenerator` (flat stone placing a test's objects): kinds'
  shapes and registry, models, `ObjectWorld`'s areas, far copies (`ObjectCopiesTests`: the shares summing to the
  object's volume at four headings and voxels wholly inside solid, a thin trunk kept a voxel wide, a kind's share grids
  those of its voxels, a column job's voxels the rule's; a pinned hash), turns, and rays (`ObjectRaysTests`:
  random rays against every solid voxel's box, a ray across stretches 40 m out, the shared ray, a tool's ray through a
  bush, the third-person camera pulled in by a trunk, the movement probes on a trunk, no allocation), and damage
  (`ObjectDamageTests`: a turned box against the cubes it holds wholly or misses, damage made live against damage
  replayed when the area fills bit for bit (also across a reset), a dig past the map's edge changing nothing, the damage
  pinned (with and without a part's removal), a part removed whole and falling only live, a removal bound by its box and
  never changing the world, the labeller's pieces by each root rule, a cut on the host logging the removal it cuts
  off (a client following labels nothing; a cut replayed when its area fills too; an answer for a copy a reset dropped
  changing nothing, the labeller run on the test's thread, `ObjectLabeller.Manual`), its far copies cleared where only the piece filled them (kept through a
  reload and on a client), hostile part removals and clears of copies, a saved log with a removal loading the same damage, a client following its host, a reset, the cap dropping the farthest copy and replaying it near, a mover far behind the changes, an area's
  copies going and coming back, a clear's count of what it takes for `IVoxelRemovals`, paint and fill, damaged parts'
  models against the kind's rule over their voxels across a part's border, the mover, rays, and statics meeting a dug
  object, a bush painted to rock stopping a character, a damaged part's unmarked blocks reading what its kind's do (so
  sharing their meshes is right), a dig making the bodies' compounds again only for the wall's part it reaches (a ball
  asleep on the other part, damaged before, stays asleep), a second dig allocating nothing; `ObjectDamageMatchTests`: a body on a rock in a match of the engine's real
  composition, falling when the rock is dug away; a trunk's top cut off falling as a body onto its stump; a cut alone
  dropping the top).
- `Itera.Engine.Tests/Spatial/`: `SpatialGridTests` (points, points on the ground, and lines against every entry worked
  out by hand over random entries narrow and wide in cells of two widths, an edge reaching the next cell, tags, moves
  and removals, the dead passed over, the nearest with ties by entity, a move of the origin, a grid refilled each step
  and one kept across 40 moves of the origin against the distances in doubles, growth, no allocation).
- `Itera.Engine.Tests/Replication/`: `ReplicatedStateCodecTests` (fields read back within their steps, changes from a
  baseline, damaged records refused or read within range, the blend, schemas and kinds) and `ReplicationServiceTests`
  over `ReplicationHarness` (a host and a client in one process without terrain, test movers and players, the clock
  stepped after the join; `SimulatedNetwork` for delay, jitter, and loss): following a little behind, through 100 ms
  each way, removals, reach, the budget, refused kinds, damaged snapshots, a bad reply; the replication systems'
  behaviour and allocation in `Systems/`.
- `Itera.Engine.Tests/Maths/` and `Animation/`: the deterministic maths and rotations, the octahedral encoding, the
  origin's conversions at up to ±160 km (`WorldOriginTests`), and skeletons, clips, aiming, and foot IK over a humanoid
  test skeleton (`HumanoidTestSkeleton`).
- `Itera.Engine.Tests/Assets/`: glTF files written in the test (`GltfBuilder`: accessors over a data-URI buffer or a
  .glb's BIN chunk, PNG images, skins, joints, and animations) read by `GltfImportTests` and `RigImportTests` (a rig
  posed against SharpGLTF's own world matrices); `RigClipTests` (step, linear, and cubic sampling); `RigShapingTests` (a
  joint by name, the globals, a mask and a blend, a turn in the asset's space under a turned parent, two-bone IK reaching
  and stretching, a joint's chain of globals the same bits as the pass over every joint, turning, moving, and reaching
  shaped exactly as with every joint's globals worked out, on the test chain and on a rig of two branches ordered by
  depth, no allocation); `MeshAssetsTests`,
  `TextureDecodeTests`, `MipChainTests`, `MeshTangentsTests`; a model's compressed textures (`CompressedTexturesTests`:
  read where they match, each mismatch decoded with a warning, the image's hash wherever the model keeps it).
- `Itera.Engine.Tests/Physics/`: a game's bodies and the records by handle (`PhysicsBodiesTests`: a reused handle
  starts afresh, freezing and forgetting, `Holds`), physics materials (`PhysicsMaterialsTests`: the registry's rules,
  the default pair exactly the engine's contact, drops at bounciness 0, 0.5, and 0.8 at 1 and 4 substeps, small drops
  landing at every place within a step, a body bouncing where the removed body that had its handle rested, two balls of
  bounciness 1 trading speeds, a bouncing ball coming to rest, a mesh collider's material, no allocation; in
  `VoxelCollidersTests` an ice box sliding down the 30 degree slope and a bouncy ball rolling over cells' borders no
  higher than a plain one; voxel materials' in `VoxelPhysicsMaterialsTests`: a static for each physics material of a
  cell, each with its own voxels' boxes, none split with nothing mapped, a box sliding down a slope of ice voxels, a
  cleared material's static removed, a ball of its voxel material's, the mapping's rules, the build timed for one and
  three materials, no allocation; the ground a move reports in `GroundMaterialTests`: a mesh floor's collider material
  read as it is now and its tags, past the floor's edge, voxels' by their voxel material, none in the air, no
  allocation; layers and groups in `PhysicsLayersTests`: the registry and its table both ways, the engine's rules
  first, a ball through a wall of a layer it does not touch, a group falling through itself, a match's table change
  waking a sleeper, the mover blocked by a gate of any layer and reading no body that does not touch characters, no
  allocation; trigger zones in `PhysicsTriggersTests`: a ball through a zone entering and leaving once and falling as
  without it, one asleep inside staying in, removals giving a removed leave, a zone moved onto a resting ball and off,
  layers, a kinematic mirror seen, each shape, a move of the origin, one thread against four, drops, no allocation; the
  characters' in `Systems/TriggerCharacterSystemTests`; impact events in `ImpactEventsTests`: drops from 1 and 4 m
  reporting their speed, place, normal, and materials, a ball rolling to rest asleep giving nothing more, two marked
  balls meeting and parting, a marked static, a removal, the origin, drops, no allocation), mesh colliders (`MeshCollidersTests`: two-sided rays across buckets, tags, a ball that
  lands on a collider's Bepu static at any voxel level) and the shared ray (`CollisionSourcesTests`); the colliders as
  the mover's cells (`MeshColumnsTests`); the voxel objects as its cells (`ObjectColumnsTests`: cells against a
  cell-by-cell reference, a trunk, a rock's top, material, and tags, a low stone, a bush, waiting for unfilled areas, the
  map's edge, chunks made again when the objects change, no allocation; `ObjectColumnReaderTests`: a kind's columns'
  runs against its shape's voxels across its parts, one object's spans against a cell-by-cell reference for cells as
  wide as its voxels, wider, and narrower, at four headings, below its anchor, with passable voxels, and damaged, and the
  runs made once a set of passable materials) and as statics near bodies (`ObjectCollidersTests`,
  made before the objects' world is initialised as a match makes them: a ball on a rock's top, a ball rolled at a trunk,
  a plank turned from its anchor, passable leaves, two physics materials, a body frozen until the objects are known,
  statics going with the cells and kept while the objects change, a cut body on a trunk, a kind's boxes against its
  voxels, the kinds' compounds made at the start and none when a body first comes near, a change's box reaching the
  parts whose voxels it covers at any heading against a voxel-by-voxel check, no allocation); the mover through test features (walls, 1 cm steps, a pit, a ceiling, leaves)
  and 10,000 random moves that never leave it inside a voxel, the same test bodies run on voxels and on triangles built
  from the same features (`MoverFeatureTests`: `VoxelCharacterMoverTests` and `MeshMoverFeatureTests` over a world of
  air; `DensityGroundTests` at 10 cm: `DensityMoverTests` and `MeshDensityGroundTests` with no world, both from
  `TestTriangles`), the mover in 1 m cells under a lintel (`CoarseCeilingMoverTests`, over `OpeningGenerator`: a body
  rising under it falls back and is never grounded in the air, one on its top stands there, a wall beside the opening
  stops one in the air, and one walks through), the mover over colliders alone (`MeshMoverTests`: a thin wall, a slope along no axis, a collider
  added and removed, the map's edge), and over voxels and colliders in the same cells (`MixedGroundMoverTests`); loose
  bodies without voxel terrain (`LooseBodiesWithoutTerrainTests`, over `Systems/MeshGroundHarness`: mesh ground, models
  in their own memory, the mover reading the bodies); the movement modes' probes over meshes and voxels
  (`MovementProbesTests`, `MovementProbesVoxelTests`); wide bodies (`WideBodyMoverTests`); statics of voxel boxes,
  coarse colliders (`CoarseCollidersTests`), ragdolls (`RagdollTests`), thrown and settled pieces, the mover among
  loose bodies (`MoverWithBodiesTests`), and solid models (`SolidModelVoxelsTests`); far from the middle
  (`FarFromTheMiddleTests`: a world moved 150 km out by `Voxels/TranslatedGenerator`, the origin there; the density
  ground's tests on its voxels and on mesh colliders placed there, `DensityMoverFarTests` and
  `MeshDensityGroundFarTests`; rays, a ball, and mesh rays against the middle's bit for bit, a walk within 0.1 mm);
  the origin moved under Bepu (`PhysicsShiftTests`: sleeping, kinematic, and moving bodies and statics) and under loose
  bodies on voxels (`ShiftedBodiesTests`: a resting ball, and a move's cost with 384 bodies, logged); lag compensation's hit history
  (`HitHistoryTests`: the ray against a capsule by hand and against a march, an entity's capsules read at a tick as the
  ray takes them, a character met where it was at a fractional
  tick and not where it is, the shooter ignored, the nearer entity and the part, an entity met only near its records, the
  ring's 32 steps, a move of the origin between records, `Forget` and `Clear`, the limits, no allocation).
- `Itera.Engine.Tests/Rendering/`: `Materials/MaterialTextureSetsTests` (DDS files read back, sets loaded and left out,
  a material packed with its set), `RenderLookTests` (the engine's look pinned to the shaders' former constants, every
  value copied and tunable), `SunPathTests` (daylight and night round the horizon, the test day, compass angles),
  `ViewConstantsTests` (the push constants against the shaders, the lens: a perspective near plane, an orthographic box
  and its depth), `ShadowCascadesTests` (slices and orthographic boxes inside their cascades, a fit local to a moved
  origin mapping points as one at the middle), `RenderViewTests` (the
  frame's view: its atmosphere, and what a match's end puts back), `Meshes/` (`MeshPassTests` (the shaders' structs; the fading shaders, and fade shares that meet
  showing each threshold once), `MeshAssetDrawsTests`
  (the asset lists, posed instances, and the blended list sorted back to front), `MeshViewTests` (the frame's frustum,
  cascades, and lights), `LevelBalanceTests` (random trees against
  marking every finest column, a level-9 column), `MeshColumnTests`, `WorldMeshesTests` over a draw target and staging
  in memory: the seams' stitched meshes, a full heap, the edits' latency, an image view's lists over `FakeViewLists`,
  the same blocks drawn local to a moved origin, a column cut by the map's edge or wider than the view still split, a
  ball set far over a heightfield's ground widening its column and drawn from blocks for its runs alone, no fine blocks
  under a camera high over the ground, a model sharing a base's meshes meshing only its marked block; objects drawn as models under split columns and within
  the hand-over's fade, their levels fading into each other by distance with complementary shares and out over
  their copies past the hand-over, the fading draws written last, a level-K column
  splitting only once its objects are known),
  `RenderViewsTests` (declaring, the cadence, panels), `Memory/GpuRangeHeapTests`, `GpuLayoutTests` (of
  `tests/Shared/GpuLayout`, also `DescriptorSets`), `Memory/GpuMemoryAllocatorTests`, `DeviceChoiceTests`,
  `RenderThreadTests` (a fake renderer), `RenderPassesTests` (a game's pass at every stage in `EngineComposition`, none
  made or written without graphics; too many, or names empty or used twice, refused; no picture read unless a pass says so), the passes' shaders against their
  push constants (`UiPassTests`, `UpscalePassTests`, `LoadingPassTests`, `ParticlePassTests`, with the pool's origin), `ScreenshotTests`,
  `ShaderReloadTests`.
- Elsewhere in `Itera.Engine.Tests`: `Configuration/` (`EngineOptionsValidatorTests`), `Networking/` (`SessionServiceTests`, `SessionTuningTests` (a host's match tunables reaching a joining client and
each change, local ones staying, a client's range kept, defaults back at the end), `LoopbackTests`, `SteamTransportTests`, `SimulatedNetworkTests` (delay, jitter, and loss over `FakeTransport` with a hand-stepped clock),
  `SteamFriendListTests`, `SessionGameMessageTests`), `Saves/SaveServiceTests`, `SaveSectionsTests` (a game's sections by name and version, a newer or damaged one
stopping the start, the backup for a damaged save and not for a newer build's, a deleted save starting afresh, an
autosave on a background thread, a section that throws when captured, a capture holding its moment, no allocation until
a save is due), `SaveMatchTests` (the last save captured while the ECS world lives, restored before the first step), and
`SaveFilesTests` (a temporary folder), `Matches/JoinRequestsTests`, `Matches/MatchControlStatesTests` (the hooks for a game's menu tests against a real match), `Navigation/`
  (flow fields on small grids, paths on them (`NavPathFinderTests`: a flow field's cost, round walls, no corner cut, a
  ramp before a step, a sealed goal and the budget ending nearest, searches in turn on one finder finding what finders
  of their own find, no allocation), the scanner with a floor 409 m down,
  and the service; the voxel objects,
  `NavigationObjectsTests`: as generated stamped onto the fill and read alike, a trunk cut through opening once its top
  is removed and blocked again after a reset, a cell waiting until its objects are known and a removal logged before
  then waiting as an edit, a moved window stamping the objects in its strips, no allocation; and the service's moving
  window,
  `NavigationWindowTests`: moves by tiles, the map's edge, a moved window against one placed there at the start, an edit
  past it, a reset during a move, a walker 2 km to its goal, no allocation), `Audio/` (`AudioMixerTests` with the one-shots across a move of the origin, `AudioServiceTests`,
  `SynthesisTests`, `SoundCacheTests`; the files' readers, `WavReaderTests` over `WavBuilder`, `VorbisDecoderTests` and
  `SoundFilesTests` over the Ogg fixtures `tools/sounds/make_sounds.py fixtures` made; `FileSoundTests` and
  `FileLoopTests` through the mixer on `FakeAudioDevice`; `MusicTests` over `MusicHarness`, the music's parts pumped
  round by round on in-memory files, `FakeDecoder`, a loop's join checked frame for frame; `OpenAlLoopbackTests` on
  OpenAL Soft's loopback device), `Particles/`, `Commands/` (`MatchCommandsTests` too), `UI/` (`AimDotLayerTests`, `TextBlobsTests`,
  `DevConsoleTests`, `MenuLayerTests` and `MenuJoinPageTests` (the menu worked by what its elements read),
  `MenuPicturesTests` (every menu page drawn at 1280x720 and 2560x1440; `ITERA_UI_PICTURES` keeps them, for a look test),
  `EngineLayerBoundsTests` and `UiFontsTests` (the sizing rule), `RadialMenuTests`, `TextLineTests`; `Toolkit/`:
  `UiLayoutTests` pinning rectangles with `FixedTextMeasurer`, `UiInputTests`, `UiLayerTests` through `UISurface` and
  `LayerPicture`), `Input/`
  (`MenuInputTests`, `KeyBindingsTests`, `KeyboardFocusTests`, `InputInjectionTests`, `TextFocusTests`), `Settings/` (with `TunablesTests`: names, ranges, sealing, scopes, and `tune`), `Loading/`,
  `Control/`, `EngineStepsTests` (the start-up steps), `ModLoaderIntegrationTests` (loads the real sample mod and
  checks that it unloads), and `ModDispatcherTests` (a throwing mod isolated, callbacks timed).

## `systems.md`: values the rules no longer give

The code holds these values (`MovementStats`, `ClimbingMode`, `ThirdPersonCamera`, `SmoothedPose`,
`CharacterTriggers`). The rules file now names the part and not the number.

### Movement

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

### The camera

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

### The origin

The origin: `OriginShiftSystem` (fixed, first, so the whole step runs in one origin) moves the match's origin to the
active camera's whole metres once the camera is `Engine:Origin:ShiftMetres` (512) or more from it on any axis, or to
the point a jump asked for (`tp`; then the camera is not read that step), through `OriginShifts`
(`.claude/rules/engine.md`); its `Initialize` moves the origin a metre and back, so the first move in play does not
compile the code; registered anywhere but first, it throws at the first step. A game that leaves it out keeps its
first origin.


### Trigger zones

Trigger zones and characters: `TriggerCharacterSystem` (fixed, after the characters move and their stand-ins) tests
each entity with a `TriggerSensor` (and `CharacterPosition`, `CharacterSize`; not `Bodiless`) as an upright capsule of
its size at its feet against every zone of the match's physics whose layer touches the characters', and writes the
match's `CharacterTriggers` (scoped, the engine's): `Events` (`CharacterTriggerEvent`: entered or left, the zone, the
entity, and removed when the entity, its sensor, or the zone went; a character that turns bodiless leaves plainly),
sorted by entity and zone, read by the systems after it that step and the others at the next, and `IsInside`; at most
1,024 pairs, the rest dropped and counted.


### Pose blending and smoothing

A second clip may be blended in (`MeshAnimator.SetBlend(clip, weight, shift)`, set by a fixed system each step: posed at the
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

## `engine.md`: the origin's section, with its values and the starter's names

The match's origin moves through `OriginShifts` (`Matches/`, scoped): `ShiftTo(point)` puts it on the whole metres
nearest the point (within the edit format's ±167.77 km) and moves everything kept in local positions the other way, so
nothing moves in the world: Bepu's bodies and statics (`PhysicsWorld.Shift`), the engine's components
(`OriginShifts.ShiftedComponents`: the camera's, the characters' feet now and before, a climber's target, mesh
placements and loose bodies' poses now and before), the world's streaming centres, the retired models, the point
lights' flashes, the particles' queued bursts, attractors, and stress place, the sounds queued, held, waiting, and
playing, the frame view's camera (`RenderView.Camera`, which the listener and some commands read before the next frame
sets it), and then the game's
`IOriginShiftHandler`s in their registration order (the starter's `FoxRouteShift`). It moves only between steps and
frames, or in the first fixed system's update (`EcsWorld.OriginMayMove`; a move asked anywhere else throws, and asking
for the place it is at returns false wherever it is asked), so every system of a step or a frame sees one origin; it allocates nothing but its Debug line. A position far from the new origin rounds to its
floats there. Mods are not told. A match's first origin is where the game's match starts: its `IOriginStart` (the
starter's `StartPose`, its eye), read at the match's first step, "origin", once the session has settled the seed; with
none, the map's middle. Then `OriginShiftSystem` (`.claude/rules/systems.md`) follows the active camera:
`OriginShifts.Follow` moves the origin to the camera's whole metres once it is `Engine:Origin:ShiftMetres` (512; 8 to
4096) out on any axis, and a jump the next step makes (`tp`) asks first (`RequestFollow`), so the origin goes to the
jump's end, not back to the camera still where it was. The console's run-only `origin move DX DY DZ` (`EngineCommands`,
with `origin` printing it and the camera's place) moves it too. The renderer, the particles' GPU pool, and the sound
follow it (`.claude/rules/rendering.md`, "Under a moved origin"); positions in messages are absolute
(`.claude/rules/sessions.md`).


## `spatial.md`: a value the rules no longer give

`Along` finds the line's nearest point by a golden-section search (the distance to a convex shape only falls and then
rises along a line), to 2e-7 of the line's length.

## `tests.md`: the old opening and run-time paragraphs

The old text counted the test classes that ran at once ("eight at a time in the engine and game test projects",
`xunit.runner.json`) and gave a reason for the Debug-only checks and tiered compilation: "an allocation test never
measures what unoptimised library code allocates (Assert.InRange's comparer once failed one); the suite takes a little
longer (ARCHITECTURE.md)".
