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

The game's skeleton keeps its joints, their order, their parents, and their turns at rest, or its clips stop fitting.
A new body may move the joints to its own places: the engine rebases the clips onto them (`mesh-assets.md`).

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
- A body of other proportions moves the joints to its own places with `rig.move_joints`, which keeps each bone's turn
  and roll. The clips still turn each joint as they do on the rig's model, so hands and feet land a little elsewhere.

## A human body from MakeHuman

MakeHuman's body (CC0) is a far better start for a human than one modelled in code: a real face, hands, and feet, and
joints that bend cleanly. `tools/models/itera_models/makehuman.py` reads it without MPFB2's code.

```python
from itera_models import makehuman, rig

body = makehuman.Body(ctx, HASHES)                   # base mesh, rig, and weights; HASHES pins every file fetched
body.shape(MACROS, TARGETS)                          # macro sliders, then targets: {"cheek/l-cheek-volume-decr": 1.0}
coords = body.blender_coords()
coords *= HEIGHT / body.height(coords)
heads, tails = (rename(joints) for joints in body.joints(coords))   # MakeHuman's joint names as the rig's
transforms, places = makehuman.fit(heads, tails, parents, rest, AIMS, LEAVES, STILL, TWISTS, SHARES)
posed = makehuman.posed(coords, body.weights, transforms, RENAME)
rig.move_joints(armature, places)                    # the rig's joints at the body's own places, their turns kept
skin = body.mesh("body", posed, ("body", "helper-l-eye", "helper-r-eye"), RENAME, bone_names)
```

- `rest` is each of the rig's joints' place at rest. It says only which way each bone points. A bone of `AIMS` points
  at a joint on both, one of `LEAVES` at its own tail and the rig's leaf joint. The trunk (`STILL`) keeps MakeHuman's
  places.
- A hand twists about its own line to lay its knuckles as the rig's (`TWISTS`). A thumb turned all the way folds the
  skin of its web, so it takes half its turn (`SHARES`).
- The base mesh is about 27,000 triangles. Reduce it with Decimate (Collapse, symmetric on x), then look for faces
  folded over and smooth them. MakeHuman's UV map has no overlaps; its eyes' UVs may lie on the body's, so move them.
- Run the build once with an empty `HASHES`: each fetched file's note gives its hash to pin.

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

## Attacks and other moves keyed by poses, on a model that has its rig

A humanoid's moves that no pack or capture has (a giant's blows) go in a clip recipe, made by
`tools/models/build.py clips` (the toolkit's README), not in a model's recipe. It writes the clips on the model's own
nodes, and the model's `merge_clips.py` list adds them.

```python
from itera_models.keyed import Body, Foot, Hand, Key, Turn

MODEL = "../../Models/Human/Human.glb"
OUT = "Moves.glb"
STAND = "Idle_Loop"

def build(ctx):
    ctx.clip("Smash_R", [
        Key(0.0, Body(), foot_l=Foot(), foot_r=Foot()),
        Key(2.4, Body(spine=Turn(bend=-12, twist=-25)), hand_r=Hand((-0.22, 1.82, -0.06)), ease="in"),
        Key(2.7, Body(hips=(0, -0.08, 0.06), hips_turn=Turn(bend=14)), hand_r=Hand(reach=(0, -0.4, 1))),
        Key(4.4, Body(), hand_r=Hand()),
    ], planted=(("foot_l", 0, 4.4), ("foot_r", 0, 4.4)), impacts=(("fist", ("hand_r",), 2.7),), mirror="Smash_L")
```

- Key whole poses at a few times, each on a frame, at the speed the game plays them: a slow raise, a blow eased `in`
  to its impact, a short hold, and a heavy return to the stand.
- Hinge at the hips (`hips_turn`) to bring the shoulders low: the spine's joints are close together, so its bend curls
  the back but barely lowers them. Reach low with `reach` from the shoulder, and read a miss's report of where the
  shoulder was before moving a place.
- A stand is seldom square. Place a foot that moves far from its own stand from the body's middle (`at`), so its mirror
  lands as far; square the shoulders with a twist and a lean worked out from the stand's for a blow made with both hands.
- A foot that kneels on its toes takes its ankle from `ctx.ankle_for(side, toes, turn)`, and a key half way down, so
  the toes stay where they stood.
- Name in `ground` the joints that touch the ground (a hand's fingers come with it), in `planted` those that must not
  slide, and in `impacts` the joints and times the game's hit windows come from.
- A cycle that does not start at the stand (a climb) is a `loop`: its first key names every part, and its last key the
  same pose. A loop the game poses by how far the body has gone, not by time, keeps its parts in step with the world.
- A body bound on the model's rig with joints of its own places (a body from MakeHuman) plays the model's clips with
  its hands and feet a few centimetres off. Name it in `BODY`, and the clips are keyed for it.

## Motion capture onto the game's rig

A looping clip from motion capture goes through `tools/models/build.py retarget SPEC.json` (its README), not a
recipe: it writes the clips on the model's own nodes, and the model's `merge_clips.py` list adds them.

- The spec's `map` takes each source joint to a game joint; unmapped joints keep their rest pose. Map one source joint
  to each game joint; a source with more spine joints than the game's skips the extra ones.
- `scale` turns the BVH's units into metres. A source whose skeleton does not rest in the game rig's T-pose names a
  frame that holds one (`restFrame`).
- An animation pack bought or downloaded for a game is read from its own `.blend` file (a clip's `blend` and `action`,
  the source's `armature` and `fps`), not its FBX files, whose unit and axis conversions land on the root bone. Its
  files stay in the game's ignored `Sources/`.
- Captured feet are the least reliable part. Aim them (`aim`), mend their glitches (`repair`), and leave the toes out
  of the map, so the model keeps its own foot shape. Name the source's ground joints in `feet`.
- Find one cycle (`from` to `to`) in a straight, steady stretch: the frame a stride's pose comes round again. A clip
  played once (`"loop": false`) keeps its frames as they are; a pack's loop turned to face its way keeps its turn with
  `"face": false`. Where one side of a pack's pair is worse, make it from the other with `"mirror": true`.
- Read the report's slide and the seam, then look at the poses with `views MODEL.glb --clip NAME@SECONDS`.
- Inside a recipe, `clips.retarget` does the same for an armature the recipe exports itself.

## Materials

- `ctx.polyhaven(asset, "1k", maps=..., hashes=...)` (OpenGL normals) or `ctx.ambientcg(asset, "1K", sha256=...)`
  (DirectX normals, flipped for you), then the materials helper's maps material. Both libraries are CC0.
- A flat colour needs no texture: a Principled BSDF's base colour, with a name.
- Grime and procedural nodes show only in Cycles, so they show in the bake and in the engine, not in the view sheet.
- Emission is drawn by the engine. Metalness and roughness are read but not drawn yet, so do not judge metal by eye.
