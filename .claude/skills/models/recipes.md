# Patterns for recipes

Each pattern below is a starting point for one kind of model. The helpers are in `tools/models/itera_models/`, and
their docstrings give every argument. Blender's axes: +Z up, and a model faces -Y, which is +z in the engine.

## A prop

The crate of Itera's starter game (its recipe is ModelRecipes/Crate/Crate.py there) is the pattern: closed boxes built with bmesh, a bevel
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
- A new body on the game's rig needs none of its clips: the game declares it bound on the rig's model
  (`Declare(..., rigOf: NAME)`, `mesh-assets.md`), which takes that model's joint order and clips. Export no clips.

## Parts on a body's bones (plates that break off, a helm, a lamp)

A part a game attaches to a joint, or drops, is a mesh of its own parented to one bone. It needs no weights. The
exporter writes it under the joint's node, and the game declares it as a part by its object's name
(`Declare(..., node: NAME)`), which the engine reads in that joint's space. The whole file leaves it out.

```python
helm = make_helm()                             # modelled round the body at rest, where it sits
world = helm.matrix_world.copy()
helm.parent = armature
helm.parent_type = "BONE"
helm.parent_bone = "Head"
bpy.context.view_layer.update()
helm.matrix_world = world                      # kept where it was modelled: a bone's parenting moves it
ctx.export(objects=[armature, body, helm])
```

- Name each part as the game names it: the object's name is the node's.
- A part is drawn whole and rigid, so give a part that spans a bending joint (a back plate over the spine) to the bone
  it moves with most.
- The parts of one file share its textures: an image their materials use is held once. To texture many parts, bake
  them together into one image set (join them for the bake in one UV layout, then split them again).

## A creature

1. Block the body out with the Skin modifier on a skeleton of vertices and edges, or with metaballs, against the
   reference outlines. Check `MATCH` before adding detail.
2. Make its armature from the same skeleton (`bpy.ops.object.skin_armature_create`), or build one with bones in edit
   mode, then apply the modifiers.
3. Weight it: automatic weights on one closed, joined proxy, then copy them to the real mesh with the rig helper's
   weight transfer. Rigid parts, each on one bone, suit plated or insect-like bodies.
4. Key its gait in code (below), with its feet in `FEET` and its gaits in `PLANTED`, so the build checks that each
   foot stays still while it touches the ground.

## Clips keyed in code

```python
from itera_models import clips

walk = clips.new_clip(armature, "Walk", 1, frame_end=25)      # an action with its slot, as Blender 5 needs
clips.key_pose(armature, 1, {"thigh_l": {"rotation": (0.97, 0.26, 0.0, 0.0)}})
...
clips.bake(armature, 1, 25)                                   # constraints and IK to plain keys
ctx.export(clips=["Walk", "Idle"])                            # only these, each from time 0
```

- Make a loop's last key equal its first, and name the clip in `LOOPS`. The build fails a loop whose last frame is off
  its first pose, or whose motion jolts at the seam.
- Name the bones whose tails touch the ground in `FEET`, and each clip they stand or walk in in `PLANTED`:
  `{"Idle": 0, "Walk": None}`. `None` takes the clip's own speed from its feet, and the build prints it. A walk in place
  moves its planted foot back at that speed. The toolkit's README gives the limits.
- The game plays a stepping clip at its body's speed over the clip's own. It measures that speed from the model as it
  loads (the engine's `ClipGround`), or keeps it in one place that a test compares with the model.
- Key whole poses at a few frames, and let the curves fill between them.
- Use IK constraints for feet and hands, then bake them to keys before the export.

## Motion capture onto the game's rig

A looping clip from motion capture goes through `tools/models/build.py retarget SPEC.json` (its README), not a
recipe: it writes the clips on the model's own nodes, and the model's `merge_clips.py` list adds them.

- The spec's `map` takes each source joint to a game joint; unmapped joints keep their rest pose. Map one source joint
  to each game joint; a source with more spine joints than the game's skips the extra ones.
- `scale` turns the BVH's units into metres. A source whose skeleton does not rest in the game rig's T-pose names a
  frame that holds one (`restFrame`).
- Captured feet are the least reliable part. Aim them (`aim`), mend their glitches (`repair`), and leave the toes out
  of the map, so the model keeps its own foot shape. Name the source's ground joints in `feet`.
- Find one cycle (`from` to `to`) in a straight, steady stretch: the frame a stride's pose comes round again.
- Read the report's slide and the seam, then look at the poses with `views MODEL.glb --clip NAME@SECONDS`.
- Inside a recipe, `clips.retarget` does the same for an armature the recipe exports itself.

## Materials

- `ctx.polyhaven(asset, "1k", maps=..., hashes=...)` (OpenGL normals) or `ctx.ambientcg(asset, "1K", sha256=...)`
  (DirectX normals, flipped for you), then the materials helper's maps material. Both libraries are CC0.
- A flat colour needs no texture: a Principled BSDF's base colour, with a name.
- Grime and procedural nodes show only in Cycles, so they show in the bake and in the engine, not in the view sheet.
- Emission is drawn by the engine. Metalness and roughness are read but not drawn yet, so do not judge metal by eye.
