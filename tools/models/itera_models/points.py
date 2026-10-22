"""Named points on a model: a muzzle, a grip, a seat.

Run inside Blender. The engine reads each named leaf empty of a glTF file as a point. add_point() makes that empty, and
may hang it under a bone, so the point follows the bone when the model is posed.
"""

from __future__ import annotations

import math

import bpy
from mathutils import Euler, Matrix


def add_point(name: str, location, rotation=None, parent=None, bone: str | None = None, size: float = 0.05):
    """Adds an empty named `name` (plain axes, `size` metres) and returns it.

    location: (x, y, z) in metres. rotation: (x, y, z) Euler angles in radians, or None for no turn.
    parent: an object to hang the point under, or None for a point in the scene's own space.
    bone: the name of a bone of the parent armature. The point is then parented to that bone, and `location` is in the
    bone's own axes, from the bone's head (its joint): the same numbers the file holds as the point's translation under
    the joint. Blender itself would measure from the bone's tail, so a parent inverse corrects that. Blender's exporter
    also turns a bone's children by 90 degrees about X, so the rotation is corrected too: `rotation` then means what it
    means for any other point, a turn from the parent's own axes, and no rotation exports as no rotation.
    """
    if bone is not None:
        if parent is None or parent.type != "ARMATURE":
            raise ValueError(f"add_point {name}: bone {bone} needs an armature as the parent")
        if bone not in parent.data.bones:
            raise ValueError(f"add_point {name}: the armature {parent.name} has no bone {bone}")

    point = bpy.data.objects.new(name, None)
    point.empty_display_type = "PLAIN_AXES"
    point.empty_display_size = size
    bpy.context.scene.collection.objects.link(point)
    if parent is not None:
        point.parent = parent
        if bone is not None:
            point.parent_type = "BONE"
            point.parent_bone = bone
            point.matrix_parent_inverse = Matrix.Translation((0.0, -parent.data.bones[bone].length, 0.0))
    point.location = tuple(location)
    turn = Euler(tuple(rotation)) if rotation is not None else Euler((0.0, 0.0, 0.0))
    if bone is not None:
        turn = (Matrix.Rotation(-math.pi / 2.0, 3, "X") @ turn.to_matrix()).to_euler()
    point.rotation_mode = "XYZ"
    point.rotation_euler = turn
    return point
