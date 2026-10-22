# Patterns for recipes

Each pattern below is a starting point for one kind of model. The helpers are in `tools/models/itera_models/`, and
their docstrings give every argument. Blender's axes: +Z up, and a model faces -Y, which is +z in the engine.

## A prop

The starter's crate (`src/Game/ModelRecipes/Crate/Crate.py`) is the pattern: closed boxes built with bmesh, a bevel
modifier for soft edges, a CC0 texture projected by position, grime in the creases, and one bake.

```python
import bmesh, bpy
from itera_models import materials, points

NAME = "Crate"
CLASS = "prop"

def build(ctx):
    crate = make_mesh()                       # bmesh.ops.create_cube for each part, into one mesh
    crate.modifiers.new("soft edges", "BEVEL").width = 0.006
    maps = ctx.polyhaven("wood_planks", "1k", maps=("diff", "rough", "ao"), hashes={...})
    wood = materials.pbr_from_maps("wood", maps)
    materials.grime(wood, amount=0.45)
    crate.data.materials.append(wood)
    materials.bake(crate, size=512, maps=("color", "roughness", "ao"), out_dir=ctx.work_dir / "bake")
    points.add_point("top", (0.0, 0.0, 0.8))
```

- Pin each download: an unpinned one prints its hash as a note in the build's summary.
- Bake whenever the material uses projection, tiling, grime, or procedural nodes. The bake unwraps a mesh that has no
  UV map; remove UV maps that overlap before it.
- Size the bake to the class's texture budget: 512 for a prop, more only for a hero model.

## A part with points (a weapon, a tower gun)

- Build the parts as separate closed meshes, bevelled. Use booleans (solver `"EXACT"` or `"FLOAT"`) for cuts, and
  apply them before the bake.
- Put each part that moves (a barrel, a lid) on its own bone, with the rig helper's rigid skin. The engine moves only
  bones, never a plain node.
- Mark each place a game reads with a point: muzzle, grip, support, magazine, ejection, tip. A point under a bone moves
  with it.

```python
from itera_models import points, rig

gun = rig.rigid_skin({stock: "body", barrel: "barrel"}, armature, name="Repeater")
points.add_point("muzzle", (0.0, 0.0, 0.42), parent=armature, bone="barrel")   # along the bone, from its joint
points.add_point("grip", (0.0, 0.0, 0.0), parent=armature, bone="body")
```

## A humanoid variant on the game's rig (armour, clothing, a new body)

The game's skeleton must stay exactly as it is: the same joints, in the same order, in the same rest pose, or its clips
stop fitting.

```python
from itera_models import rig

armature, bodies = rig.import_reference(ctx.recipe_dir / "../../Models/Human/Human.glb")
plate = make_armour_around(bodies[0])          # modelled over the body in its rest pose
weighted, unweighted = rig.transfer_weights(plate, bodies[0], armature)
if unweighted:
    ctx.problem(f"{unweighted} vertices took no weight from the body")
for body in bodies:                            # the reference body is not part of this model
    bpy.data.objects.remove(body)
ctx.export(objects=[armature, plate])          # no clips: the game's clips come from its own clip files
```

- Fit the new mesh to the rest pose, a little outside the body, so the copied weights follow it.
- Keep the game's material names when a game recolours them by name.
- To add clips from another file on the same rig, the toolkit's `tools/models/merge_clips.py` merges them.

## A creature

1. Block the body out with the Skin modifier on a skeleton of vertices and edges, or with metaballs, against the
   reference outlines. Check `MATCH` before adding detail.
2. Make its armature from the same skeleton (`bpy.ops.object.skin_armature_create`), or build one with bones in edit
   mode, then apply the modifiers.
3. Weight it: automatic weights on one closed, joined proxy, then copy them to the real mesh with the rig helper's
   weight transfer. Rigid parts, each on one bone, suit plated or insect-like bodies.
4. Key its gait in code (below), and test that each foot stays still while it touches the ground.

## Clips keyed in code

```python
from itera_models import clips

walk = clips.new_clip(armature, "Walk", 1, frame_end=25)      # an action with its slot, as Blender 5 needs
clips.key_pose(armature, 1, {"thigh_l": {"rotation": (0.97, 0.26, 0.0, 0.0)}})
...
clips.bake(armature, 1, 25)                                   # constraints and IK to plain keys
ctx.export(clips=["Walk", "Idle"])                            # only these, each from time 0
```

- Make a loop's last key equal its first.
- Key whole poses at a few frames, and let the curves fill between them.
- Use IK constraints for feet and hands, then bake them to keys before the export.

## Motion capture onto the game's rig

```python
bpy.ops.import_anim.bvh(filepath=str(path), global_scale=0.01, axis_forward="-Z", axis_up="Y")
source = bpy.context.object
moved = clips.retarget(source, armature, JOINT_MAP, 1, 120, hips=("Hips", "pelvis"), name="Climb")
clips.remove_clip(source.animation_data.action.name)          # else the exporter writes the source's clip too
bpy.data.objects.remove(source)
```

- `JOINT_MAP` maps each source joint to a game joint. Unmapped joints keep their rest pose.
- Check the clip in the engine with `views --clip Climb@1.5`. Look for feet that slide and limbs that cross.

## Materials

- `ctx.polyhaven(asset, "1k", maps=..., hashes=...)` (OpenGL normals) or `ctx.ambientcg(asset, "1K", sha256=...)`
  (DirectX normals, flipped for you), then the materials helper's maps material. Both libraries are CC0.
- A flat colour needs no texture: a Principled BSDF's base colour, with a name.
- Grime and procedural nodes show only in Cycles, so they show in the bake and in the engine, not in the view sheet.
- Emission is drawn by the engine. Metalness and roughness are read but not drawn yet, so do not judge metal by eye.
