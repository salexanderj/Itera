---
paths:
  - "src/Itera.Engine/Physics/{MeshColliders,CollisionSources,SurfaceHit,HitHistory,HitShapes}.cs"
  - "tests/Itera.Engine.Tests/Physics/{CollisionSourcesTests,HitHistoryTests,MeshCollidersTests,TestTriangles}.cs"
---

## Mesh colliders, the shared ray, and the hit history

### Mesh colliders

`MeshColliders` (scoped to the match) holds a game's static triangle colliders. Only registered colliders collide, and
drawing stays apart. Add colliders once the match's physics exists, from a system's `Initialize` on.

- `Add(asset, transform, tagsOf)` registers a mesh asset placed by a local transform. Each triangle is tagged by its
  material's name through `tagsOf`.
- `Add(positions, indices, transform, tags)` registers triangles built in code.
- `Add(asset, at, transform, tagsOf)` and `Add(positions, indices, at, transform, tags)` place them from an absolute
  point (`WorldPoint`), which keeps a large or far mesh precise.
- `Remove` takes one away.

Each collider keeps:

- An anchor, the absolute whole metres nearest its point, and its triangles from it.
- A grid of 4 m buckets over absolute x and z. The grid is made again at the first query after a change, which allocates.
  Other queries allocate nothing.
- A Bepu `Mesh` static posed at its anchor's local place, of its settings' physics material. Its triangles are wound
  clockwise seen from the front as Bepu asks, while glTF's are counter-clockwise. The static is one-sided in Bepu and
  at `BodyKinds.AnyLevel`, so every loose body touches it whatever its voxel level (`SetGameStatic`; `ClearStatic` on
  removal).

Surface tags are up to 32 bits a game defines. Voxel materials get theirs through the game's `IVoxelSurfaceTags`, none by
default. Mesh materials get theirs by name at registration.

### The shared ray

`CollisionSources` (scoped) is what rays meet. `Raycast` answers from four sources and the nearest wins:

- The terrain (`VoxelWorld.Raycast`).
- The voxel objects the world places (`ObjectRays`, `.claude/rules/objects-meeting.md`).
- The colliders, two-sided, nearest first through the buckets.
- The solid models (`SolidModelVoxels`, given the loose bodies' voxels and the match's origin).

The answer is a `SurfaceHit`: the point, the normal turned towards the origin, the distance, the tags, the source, and
what the source gives:

- A voxel hit gives the voxel's material.
- A collider hit gives the collider and the triangle.
- An object hit gives the object's voxel's material and `ObjectId`.
- A solid model hit gives its id in `Collider` and `SurfaceSource.SolidModel`, with no material or tags.

The voxel tools' ray over the terrain alone is `ToolAim.Raycast(VoxelWorld, ...)`. `ToolAim.Raycast(CollisionSources,
...)` meets all the sources, past the terrain's and the objects' passable voxels round the eye.

### The hit history

`HitHistory` (scoped, `Physics/HitHistory.cs`) is lag compensation's record. `HitShapes.RaycastCapsule`, the exact ray
against a capsule, is public for a game's own shapes.

- A game records each character's hit capsules once a step with `Record(entity, tick, capsules)`. A `HitCapsule` holds
  two ends, a radius, and the game's part number.
- A host judges a shot with `Raycast(from, direction, maxMetres, tick, ignore, out hit)`. It returns the nearest capsule
  of any entity but the shooter, at a tick that may fall between two steps.
- Between two steps each capsule's ends move straight by the fraction. A capsule at the same place in both lists with the
  same part is matched. Otherwise each is used where it was recorded.
- `CapsulesAt(entity, tick, into)` gives one entity's capsules as that ray takes them at the tick. A game uses it to test
  its own shapes past the first the ray meets, such as organs inside a part it hit.
- `ThirdPersonCamera.Aim` takes a tick and the aimer to leave out, so an over-the-shoulder crosshair stops at a
  character's shape. `AimPoint` gives the point itself, for a shot that leaves from a muzzle towards it. `LookPoint`
  gives the point straight along the look from the eye, for an aimer with no camera over its shoulder.
- It keeps `Steps` steps of up to `MostCapsules` capsules for up to `MostEntities` entities. `Steps` is more than
  `ReplicationService.MostShownTicksBehind`.
- A step is one absolute anchor (a `WorldPoint`) and float offsets from it. A move of the origin changes nothing, and
  the history is not a component `OriginShifts` moves.
- An entity is met from a step before its oldest record to a step after its newest. A character the game stopped
  recording is met only at ticks it was there. `Forget` drops one.
- Storage is made at an entity's first record and reused after `Forget`. Recording a known entity, asking, and forgetting
  allocate nothing.
- The game decides what the tick and the capsules are. The engine keeps no character's shape.
- `HitShapes.FromPose(globals, placed, bones, into)` gives a posed rig's capsules from its joints' places (`Rig.Globals`),
  through the mesh's placement (`MeshPoses.Placed`). Each `HitBone` is a part from one joint towards another, or a
  sphere. Its far end is `Reach` times the way between them, and its radius scales with the placement.
