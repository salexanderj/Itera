"""Which objects of the scene a model is made of.

Run inside Blender. The export, the scene checks, and the view sheet all take the same default list, so they agree on
what "the model" is.
"""

import bpy

# The only object types the engine reads from a glTF file: cameras and lights are left out.
MODEL_TYPES = ("MESH", "ARMATURE", "EMPTY")


def default_objects() -> list:
    """The scene's visible meshes, armatures, and empties, in name order."""
    objects = [obj for obj in bpy.context.scene.objects if obj.type in MODEL_TYPES and obj.visible_get()]
    return sorted(objects, key=lambda obj: obj.name)


def resolve(objects) -> list:
    """The given objects as a list, or the default list when none are given."""
    if objects is None:
        return default_objects()
    return list(objects)


def meshes_of(objects) -> list:
    """The mesh objects among the given objects (or among the default list)."""
    return [obj for obj in resolve(objects) if obj.type == "MESH"]
