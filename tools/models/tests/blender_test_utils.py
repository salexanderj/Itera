"""Helpers the tests of the model toolkit share. They run inside Blender."""

import json
import struct

import bpy
import numpy as np


def reset_scene() -> None:
    """Empties Blender's data so a test starts clean."""
    bpy.ops.wm.read_factory_settings(use_empty=True)


def save_png(path: str, pixels_rgba: np.ndarray) -> None:
    """Writes a (height, width, 4) float array in 0..1 as a PNG, values as given. Row 0 is the image's top."""
    height, width = pixels_rgba.shape[:2]
    image = bpy.data.images.new("tmp_png", width, height, alpha=True, is_data=True)
    image.pixels.foreach_set(np.ascontiguousarray(np.flipud(pixels_rgba), dtype=np.float32).ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)


def read_png(path: str) -> np.ndarray:
    """Reads an image file's raw values into a (height, width, 4) float array. Row 0 is the image's bottom."""
    image = bpy.data.images.load(path, check_existing=False)
    image.colorspace_settings.name = "Non-Color"
    width, height = image.size
    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    bpy.data.images.remove(image)
    return pixels.reshape(height, width, 4)


def solid_png(path: str, color: tuple[float, float, float, float], size: int = 8) -> None:
    """Writes a PNG of one colour."""
    save_png(path, np.tile(np.array(color, dtype=np.float32), (size, size, 1)))


def export_glb(path: str, objects=None, animations: bool = True) -> dict:
    """Exports to GLB the way the engine wants it and returns the JSON chunk."""
    bpy.ops.object.select_all(action="DESELECT")
    if objects is not None:
        for obj in objects:
            obj.select_set(True)
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        use_selection=objects is not None,
        export_apply=True,
        export_influence_nb=4,
        export_morph=False,
        export_animations=animations,
    )
    return read_glb_json(path)


def read_glb_json(path: str) -> dict:
    """Returns the JSON chunk of a GLB file."""
    with open(path, "rb") as handle:
        data = handle.read()
    length = struct.unpack("<I", data[12:16])[0]
    return json.loads(data[20 : 20 + length])


def object_mode() -> None:
    """Leaves any edit or pose mode."""
    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")


def make_armature(name: str, bones, location=(0.0, 0.0, 0.0)):
    """Creates an armature. `bones` is a list of (name, head, tail, roll, parent_name_or_None).

    Heads and tails are in the armature's own space; a child's head sits on its parent's tail when connected.
    """
    data = bpy.data.armatures.new(name)
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = location
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    made = {}
    for bone_name, head, tail, roll, parent in bones:
        bone = data.edit_bones.new(bone_name)
        bone.head = head
        bone.tail = tail
        bone.roll = roll
        if parent is not None:
            bone.parent = made[parent]
        made[bone_name] = bone
    bpy.ops.object.mode_set(mode="OBJECT")
    return obj
