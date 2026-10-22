"""Small scene builders and a glTF reader for the toolkit's Blender tests. Run inside Blender."""

from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

import bpy

MODELS_DIR = Path(__file__).resolve().parents[1]
if str(MODELS_DIR) not in sys.path:
    sys.path.insert(0, str(MODELS_DIR))


def reset() -> None:
    """Empties the scene: no cube, camera, or light."""
    bpy.ops.wm.read_factory_settings(use_empty=True)


def add_cube(name: str = "Cube", material_name: str | None = "Stone", location=(0.0, 0.0, 0.5)):
    """A 1 m cube with a UV map and, unless material_name is None, a named material."""
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.data.name = name
    if material_name is not None:
        material = bpy.data.materials.new(material_name)
        obj.data.materials.append(material)
    return obj


def add_armature(name: str = "Rig", bones=(("root", (0, 0, 0), (0, 0, 0.5)), ("top", (0, 0, 0.5), (0, 0, 1.0)))):
    """An armature object with the given (name, head, tail) bones, each parented to the one before it."""
    data = bpy.data.armatures.new(name)
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    previous = None
    for bone_name, head, tail in bones:
        bone = data.edit_bones.new(bone_name)
        bone.head = head
        bone.tail = tail
        if previous is not None:
            bone.parent = previous
            bone.use_connect = True
        previous = bone
    bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def skin_to(mesh_obj, armature_obj, weights: dict) -> None:
    """Parents the mesh to the armature and weights vertices: weights maps a bone name to {vertex index: weight}."""
    mesh_obj.parent = armature_obj
    modifier = mesh_obj.modifiers.new("Armature", "ARMATURE")
    modifier.object = armature_obj
    for bone_name, vertices in weights.items():
        group = mesh_obj.vertex_groups.get(bone_name) or mesh_obj.vertex_groups.new(name=bone_name)
        for index, weight in vertices.items():
            group.add([index], weight, "REPLACE")


def key_clip(armature_obj, name: str, bone: str = "root", angle: float = 0.5, frames=(1, 10)):
    """Makes an action with two rotation keys on a bone. It is left assigned to the armature."""
    if armature_obj.animation_data is None:
        armature_obj.animation_data_create()
    action = bpy.data.actions.new(name)
    armature_obj.animation_data.action = action
    pose_bone = armature_obj.pose.bones[bone]
    pose_bone.rotation_mode = "XYZ"
    pose_bone.rotation_euler = (0.0, 0.0, 0.0)
    pose_bone.keyframe_insert("rotation_euler", frame=frames[0])
    pose_bone.rotation_euler = (angle, 0.0, 0.0)
    pose_bone.keyframe_insert("rotation_euler", frame=frames[1])
    pose_bone.rotation_euler = (0.0, 0.0, 0.0)
    return action


def read_glb(path) -> tuple[dict, bytes]:
    """The JSON document and the binary chunk of a .glb file."""
    data = Path(path).read_bytes()
    magic, version, _ = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67 or version != 2:
        raise ValueError(f"{path} is not a glTF 2.0 binary file")
    offset = 12
    document, binary = None, b""
    while offset < len(data):
        length, kind = struct.unpack_from("<II", data, offset)
        chunk = data[offset + 8 : offset + 8 + length]
        if kind == 0x4E4F534A:
            document = json.loads(chunk)
        elif kind == 0x004E4942:
            binary = bytes(chunk)
        offset += 8 + length
    return document, binary
