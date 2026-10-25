---
paths:
  - "src/Itera.Engine/Objects/ObjectRays.cs"
  - "src/Itera.Engine/Physics/{ObjectColumns,ObjectColumnReader,VoxelColliders.Objects}.cs"
  - "tests/Itera.Engine.Tests/Objects/ObjectRaysTests.cs"
  - "tests/Itera.Engine.Tests/Physics/{ObjectCollidersTests,ObjectColumnsTests,ObjectColumnReaderTests}.cs"
---

## Voxel objects: how characters, rays, bodies, and navigation meet them

Kinds, placement, and play are in `.claude/rules/objects.md`. Damage is in `.claude/rules/objects-damage.md`.

### Characters meet objects as ground

`ObjectColumns` (internal) is the mover's `Objects`, set where the world has terrain. It works in the mover's cells.

- A cell is blocked where its centre, turned into an object, lands on a solid voxel of the object that is not passable
  (`IVoxelPassability`: a tree's leaves where the game says so).
- Each run of blocked cells keeps its object's surface over the column's centre, and the material of the highest solid
  voxel there. The mover reads them as the ground's (`PhysicsMaterials.OfVoxel`, `IVoxelSurfaceTags`).
- The surface lies where the density crosses one half between that voxel's centre and the one above, as the world's
  ground does. So a smooth shape, such as a ramp, reads as its slope, not as steps a voxel high.
- A blocking voxel above gives that voxel's top, and a passable one counts as empty. The surface stays between the top
  cell's bottom and half a cell over its top.
- It answers the voxel world's and the mesh colliders' queries, made 32 by 32 columns at a time. It keeps a chunk until
  `ObjectWorld.Version` changes, or `ObjectDamage.ReadChanges` names an object under it. It keeps at most `ObjectColumns.MostChunks` chunks.
- A move waits where the areas round it within the map are not filled (`Known`, `ObjectWorld.IsKnown`).
- An object's column (`ObjectColumnReader`) takes its cells from its kind's columns' runs (`ObjectColumnRuns`). An object
  as its kind is turns each run of blocking voxels into the cells whose centres land in it. Runs whose cells meet are
  joined. A voxel or two are read only for the top's surface and material.
- A damaged object reads each cell through its damage, only over its kind's solid runs. Passable runs count too, because
  a paint may make one blocking.

### Rays meet objects

`ObjectRays` (scoped with terrain) finds the first solid voxel of any object along a ray. Passable voxels count, as in the
world's ray. The result is an `ObjectHit`: the object, its kind, its voxel's index in the kind, the face entered turned
into the world's frame, the material, and where the ray enters and leaves that voxel. The face is zero from inside.

- A ray that only touches an object's box, or starts on its side going away, meets nothing.
- The ray is walked in stretches. Each stretch asks `ObjectWorld.Near` for the objects whose reach meets it, turns the ray
  into each object's voxels, and steps through them in the object's box. Objects in unfilled areas are not met. A longest ray limits the walk.
- `CollisionSources` answers from it too (`SurfaceSource.VoxelObject`, `SurfaceHit.ObjectId`). So the movement probes and
  climbing (`ClimbingMode`) meet objects by their voxels' tags.
- `ToolAim.Raycast(CollisionSources, ...)` goes past objects' passable voxels round the eye, as past the terrain's.
- The third-person camera is pulled in by objects where given them: `ThirdPersonCamera`'s `objects`, `PlayerCameraSystem`'s,
  and `ToolAim.Look`'s.

### Bodies meet objects

`VoxelColliders.Objects.cs` gives bodies the objects' statics.

- Each cell of statics near a body finds, at its first build, the objects whose kind's reach meets it and whose height
  range does (`ObjectWorld.Near`).
- Each such object gets a static for each of its kind's parts and each physics material of the part's voxels. The static is
  the kind's `BigCompound` of boxes over the part's own voxels that are not passable (`VoxelBoxes.Build(kind, part,
  damaged, excluded)`, in the kind's unit, `PhysicsWorld.UnitBox`).
- The compound is shared by every object of the kind and made at the match's "objects" step
  (`VoxelColliders.MakeObjectShapes`). `CompoundsMade` counts them. A damaged object has compounds of its own
  (`.claude/rules/objects-damage.md`).
- The static is posed at the object's anchor's local place and turned by `ObjectTurns.Rotation`. It is at
  `BodyKinds.AnyLevel`, so bodies of every level touch it, cut bodies too. It is in the voxels' layer.
- Each object static is a static of its own to the bounces and impact events, not the voxel ground. A ball rolling from
  the ground onto a rock begins a new pair.
- An object's statics stay while a cell reaches it, counted, and go with the last cell. `VoxelColliders.ObjectCount` counts
  them.
- A change of the objects (`ObjectWorld.Version`) makes every cell find its objects again. The new are taken before the
  old let go.
- A body round which the areas within the map are not filled (`ObjectWorld.IsKnown`) freezes as a body over columns not
  resident does.
- The statics take no densities: their voxels' boxes are whole. At 5 cm voxels a box lies up to 2.5 cm off the drawn
  surface.

### Navigation meets objects

`.claude/rules/navigation.md` has the navigation side.

- The objects as generated are stamped onto the cells a source fills, on the navigation thread (`NavObjects`). A placer is
  read with `ObjectColumnReader`, the mover's column rule, from the kinds' columns' runs made before the thread starts.
- Reads take the objects from the mover's `ObjectColumns`, once known.
- A part's removal and each change of an object's copy read its cells again.
