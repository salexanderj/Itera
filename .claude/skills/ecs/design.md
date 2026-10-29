# ECS design for Itera

How the Arch world fits the rest of the game. Read `ARCHITECTURE.md` first; this file does not repeat it.
Where a point below is a design choice that has not been made yet, it says so. Ask the user before
making it.

## What is an entity and what is not

Entities are the game objects that exist in numbers and share behaviour: players, enemies, towers,
projectiles, pickups, spawners, effects, and base structures. Their state lives in components and
their behaviour in systems.

These are not entities, and their data does not go in components:

- Voxel data. The sparse voxel hierarchy (ARCHITECTURE.md section 8) is its own service,
  `Itera.Engine.Voxels.VoxelWorld`. About 4 x 10^13 voxels cannot be entities, and neither can chunk or
  brick payloads. Systems read voxels with `VoxelWorld.TryGetMaterial`, change them with
  `VoxelWorld.Apply` (from a fixed system fed by the player's command, so edits can be sequenced by the host), move characters through them with `VoxelCharacterMover`, and set streaming centres
  with `VoxelWorld.SetCenters` (`StreamingCentreSystem`, a fixed system, so streaming follows the player even while no
  frames are built). One debug exception to "frame systems never change simulation state" remains: while the player
  flies the debug camera (the console's `fly`), a frame system moves the camera and the fixed systems read its pose, so the player's
  feet, the tools, and the streaming centre follow it.
- Spatial indexes (a grid or tree for "what is near this point"). Arch queries select by component
  type, not by position. A system keeps a separate spatial structure up to date and other systems ask
  it: the engine's `SpatialGrid` (`.claude/rules/spatial.md`), which a game fills.
- The navigation grid and flow fields, the Bepu simulation, Vulkan objects, UI state, and configuration.

An entity refers to data owned by a service with a small value: an index, a Bepu `BodyHandle`, or a
chunk coordinate. The service owns the data and its lifetime.

## Components

- Component types go in a `Components/` folder next to `Systems/` (namespace `Itera.Engine.Components` for the
  engine's, a game's own namespace, such as `Game.Components`, for a game's).
- Plain structs with public fields and no reference-type fields. Units are metres and seconds; voxel
  coordinates are integers (CLAUDE.md).
- One purpose per component, split by how systems use the data. Separate `Position` and `Velocity`
  rather than one `Transform` with everything; a system that reads only `Position` then loads only
  positions.
- Shared, rarely changing data (tower stats, enemy archetype stats) lives once in a service or a
  static table. The component holds an index into it.
- Tags are empty structs. Use a tag for a fact that rarely changes (`Enemy`, `Tower`).
  For state that changes every few ticks (stunned, targeting), use a field in an existing component:
  each `Add` or `Remove` copies the entity to another archetype.
- Every distinct set of component types is an archetype with at least one chunk of at least 16 KB.
  Avoid designs that give many entities unique sets, such as a generic `Upgrade<T>` component per
  upgrade type. Use one component with a type field instead.
- A reference to another entity is an `Entity` field, set to `Entity.Null` when empty. Check it with
  `World.IsAlive` before use; the other entity may have been destroyed.

## Systems and the two groups

`EcsWorld` owns a `Group<FixedStepTime>` and a `Group<FrameTime>`. `GameLoop` runs, each main-loop
iteration (at least once per fixed step):

1. window events and input;
2. the due fixed steps (at most eight). Each step runs the ECS fixed group, then `PhysicsWorld.Step`,
   then the mods' `OnFixedUpdate`;
3. only when the render thread has asked for a frame: the ECS frame group, the mods' `OnUpdate`, and the
   UI, then the frame is handed to the render thread. While presentation blocks (a hidden window), no
   frame is asked for, so frame systems pause and fixed systems keep running.

- Fixed systems (`IFixedUpdateSystem`, `BaseSystem<World, FixedStepTime>`) own the simulation: input
  commands, movement, AI, targeting, damage, spawning, economy, win and loss. The step is always 1/60 s,
  which keeps the simulation independent of frame rate. `FixedStepTime.Tick` counts steps; use it, not
  wall-clock time, for timers and anything that must agree between peers. Under host-authoritative
  networking, the host's fixed-step state is what gets replicated.
- Frame systems (`IFrameUpdateSystem`, `BaseSystem<World, FrameTime>`) only present: interpolation,
  camera, preparing render and UI data. They never change simulation state. `FrameTime.Alpha` is the
  fraction of a step since the last fixed step, for blending between the last two steps.
- Within a group, systems run in registration order. A typical fixed order: apply input, AI and
  targeting, movement, write physics targets, (physics step runs after the group), then on the next
  tick read physics results, then damage and death, then cleanup and destroy.
- One responsibility per system. A system that grows several unrelated queries should be split.
- Deferred destruction: an entity that must go gets a tag (for example `Dead`). One cleanup system
  near the end of the fixed group releases what the entity owns outside the ECS, then removes all
  tagged entities at once with `World.Destroy(in description)`. This keeps every other system's
  handles valid until the end of the step. The exact tag and system are an open design choice.

## Physics (Bepu)

- Characters do not move with Bepu. A fixed system hands each character's move to `VoxelCharacterMover` (the engine's
  own controller against the level-0 voxels and the loose bodies, read as voxels) and keeps the result in components:
  `CharacterPosition`, `CharacterVelocity`, `CharacterContact` (`PlayerMovementSystem`). Near a loose body a character
  has a Bepu stand-in (`CharacterStandIn`, a field-driven body: `StandInSystem` adds, places, and removes it without
  structural changes), which pushes bodies and is pushed by them; `PhysicsReadSystem` hands the push to
  `CharacterVelocity.Push`.
- Loose bodies are entities with `LooseBody` (the `BodyHandle`, its birth stamp, and a ball's radius), `BodyPose`
  (pivot and turn after the last two steps, for drawing; the component, not `Animation.BodyPose`), and `BodyBox`.
  `LooseBodies` (engine) owns the Bepu bodies and what they are made of. Whatever makes a body makes its entity
  outside its queries (`DebugBallSystem`); `LooseBodySystem` removes the Bepu body first and destroys the entity
  through its `CommandBuffer` after its chunk loop. Engine services
  (`VoxelColliders`, `LooseBodyVoxels`) read the bodies straight from Bepu or `LooseBodies`, not through the ECS.

- An entity with a body holds its `BodyHandle` (or `StaticHandle`) in a component. `PhysicsWorld` owns
  the simulation; the entity does not.
- The physics step runs after the fixed group. A system early in the group reads poses from the
  previous step into components; systems later in the group write velocities or targets that the
  next physics step uses.
- Remove the body from the simulation before destroying the entity. Arch's destroy events do not fire
  in the `Arch` package.
- A match's steps run physics before ECS, and shutdown runs in reverse (`MatchServices`), so a
  system may use the simulation in `Initialize` and release its bodies in `Dispose`.

## Networking (host-authoritative)

- `Entity` ids are local to one process and recycled. Replicated entities carry a stable id: for players, the session
  slot the host assigns (`PlayerIds`); for bodies and characters a host sends, `NetEntity { uint Id }`, which the host
  hands out. `NetEntities` maps ids to entities in each game, with a kind a game defines, and checks `IsAlive` on every
  lookup; an id whose entity was destroyed is found afterwards by the whole `Entity` (`NetEntities.TryFindId`), never by
  a `Get` through the dead handle. The engine's replication (`ReplicationService`, `.claude/rules/replication.md`) marks
  an entity it sends or shows with `Replicated` (id and kind) and keeps what each client holds of it in the service's
  own tables, by the entity's place in them, not in components: a client's view changes with every acknowledgement.
- The session's game messages are stored by handlers registered in DI (`ISessionGameHandler`), which exist before the
  ECS; systems read the stores in the fixed group. A handler never touches the ECS world.
- Query order is not stable (back-to-front iteration, swap-remove, archetype creation order). Anything
  sent over the network or compared between peers must not depend on it. Sort by the stable id when
  order matters.
- Voxel edits replicate as edits (ARCHITECTURE.md), not through the ECS. `SessionService` sends the host's edits after
  each fixed step; on a client `VoxelWorld.FollowsHost` is true and a system that edits the world must do nothing there
  but send the host a request (a game's message).

## World generation

Generation is seeded and deterministic (ARCHITECTURE.md). Create entities from generation output in a
fixed order taken from the generator, never from query order. Use `World.EnsureCapacity` or bulk
`World.Create` when creating many entities of one kind.

## Rendering and UI

The extract step and UI read component data on the main thread after the frame group; the render thread
never reads the world (the `ecs-vulkan` skill, "Frame order"). Components never
hold Vulkan handles. GPU-side data lives in renderer-owned buffers indexed by values in components
(see the `vulkan` skill for the GPU side). Copying component data into GPU memory, and structs that
shaders read, follow the `ecs-vulkan` skill.

Loose bodies and a game's bodies are drawn as models (meshes). `LooseBodyModelSystem` (frame group, after
`CameraExtractSystem`) reads the loose bodies' `LooseBody`, `BodyPose`, and `BodyBox`, and adds each body's model to
the engine's `ModelInstances` (a CPU array, cleared by `GameLoop` before the frame group); a game's own model system
adds its bodies the same way, after it. `WorldMeshes.Upload` in `GameLoop` turns that into the slot's draws after the
group, and `MeshPass` draws them. A model's voxels, posing, and parts live in services (`VoxelModels`, a game's), not in
components; an entity holds an index into them. A body that never goes back into the grid is a loose-body entity tagged
`CutPiece`; `LooseBodySystem` removes it 30 s after it rests, releasing the Bepu body and its owned models before the
entity is destroyed.

## Mods

- Mods reference only `Itera.Modding`, so they cannot use Arch types. Do not expose Arch types
  through `Itera.Modding`; a narrower mod API is open work (ARCHITECTURE.md section 9).
- Component types defined in a mod assembly stay in Arch's static registry and keep the mod loaded
  after unload (ARCHITECTURE.md section 6).

## Threading

All ECS work runs on the main thread, like every other engine service except drawing, which runs on the
render thread and gets its data only through `FrameExchange`, and voxel generation, which runs on
`VoxelWorkers` threads that hand finished columns to the main thread. Arch's parallel queries need a
static, process-wide `JobScheduler`, split work only across chunks of the same archetype, and would
compete with Bepu's worker threads. Adding them is a decision for the user, taken with measurements.
