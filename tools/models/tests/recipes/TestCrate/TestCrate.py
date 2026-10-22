"""A tiny recipe for the toolkit's own end-to-end test: a skinned crate with one clip and one named point."""

import bpy
from itera_models import points

NAME = "TestCrate"
CLASS = "prop"
UNIT = 1.0


def build(ctx):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, 0.5))
    crate = bpy.context.object
    crate.name = "Crate"
    wood = bpy.data.materials.new("Wood")
    crate.data.materials.append(wood)
    # A small checker texture, made in place, so the build also exercises images and the texture compression.
    image = bpy.data.images.new("WoodColour", 8, 8)
    image.pixels = [0.6 if (i // 4 + i // 32) % 2 else 0.3 for i in range(8 * 8 * 4)]
    image.pack()
    texture = wood.node_tree.nodes.new("ShaderNodeTexImage")
    texture.image = image
    wood.node_tree.links.new(texture.outputs["Color"], wood.node_tree.nodes["Principled BSDF"].inputs["Base Color"])

    rig_data = bpy.data.armatures.new("Rig")
    rig = bpy.data.objects.new("Rig", rig_data)
    bpy.context.scene.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    root = rig_data.edit_bones.new("root")
    root.head, root.tail = (0, 0, 0), (0, 0, 0.5)
    lid = rig_data.edit_bones.new("lid")
    lid.head, lid.tail = (0, 0, 0.5), (0, 0, 1.0)
    lid.parent = root
    lid.use_connect = True
    bpy.ops.object.mode_set(mode="OBJECT")

    crate.parent = rig
    crate.modifiers.new("Armature", "ARMATURE").object = rig
    for index, vertex in enumerate(crate.data.vertices):
        group = crate.vertex_groups.get("lid" if vertex.co.z > 0.0 else "root")
        group = group or crate.vertex_groups.new(name="lid" if vertex.co.z > 0.0 else "root")
        group.add([index], 1.0, "REPLACE")

    rig.animation_data_create()
    action = bpy.data.actions.new("Open")
    rig.animation_data.action = action
    pose_bone = rig.pose.bones["lid"]
    pose_bone.rotation_mode = "XYZ"
    pose_bone.keyframe_insert("rotation_euler", frame=1)
    pose_bone.rotation_euler = (0.8, 0.0, 0.0)
    pose_bone.keyframe_insert("rotation_euler", frame=13)

    points.add_point("latch", (0.0, -0.3, 0.1), parent=rig, bone="lid")
    ctx.export(clips=["Open"])
    ctx.note("built a crate")
