"""Skinning helpers for recipes: weights from a reference body, automatic weights, rigid skinning, and moving a rig's
joints to a body of other proportions.

The engine reads at most four influences per vertex, one skin, and joints by name. Every function here leaves
the scene in Object mode and ends with weights that follow those rules.
"""

from __future__ import annotations

import os

import bpy
import numpy as np
from mathutils import Vector

#: Weights below this are dropped before limiting, so they do not take one of the influence slots.
_CLEAN_THRESHOLD = 1e-4
#: A vertex whose weights add up to less than this counts as unweighted.
_UNWEIGHTED_SUM = 1e-6


def import_reference(path: str | os.PathLike) -> tuple[bpy.types.Object, list[bpy.types.Object]]:
    """Imports a .glb and returns its armature and its meshes.

    A file with several armatures gives the one with the most bones. Only objects the import made are returned,
    and bones get no display-shape meshes.
    """
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=os.fspath(path), disable_bone_shape=True)
    made = [obj for obj in bpy.data.objects if obj not in before]
    armatures = [obj for obj in made if obj.type == "ARMATURE"]
    if not armatures:
        raise ValueError(f"{path} has no armature")
    armature = max(armatures, key=lambda obj: len(obj.data.bones))
    meshes = [obj for obj in made if obj.type == "MESH"]
    return armature, meshes


def transfer_weights(
    target: bpy.types.Object,
    source: bpy.types.Object,
    armature: bpy.types.Object,
    max_influences: int = 4,
) -> tuple[int, int]:
    """Copies vertex weights from a skinned `source` mesh to `target`, and binds `target` to `armature`.

    The source's vertex groups are copied by name (each target vertex takes the interpolated weights of its
    nearest source face), so source and target must sit in the same place and pose in world space. Groups that
    are not bones of `armature` are dropped. Then the weights are cleaned, limited to `max_influences`,
    and normalised; an Armature modifier is added and `target` is parented to `armature`.

    Returns `(weighted, unweighted)`: the number of target vertices that got weight, and that did not.
    """
    _ensure_object_mode()
    # The source's rig may hold a pose from an imported animation; the weights belong to the rest shape.
    rigs = {m.object for m in source.modifiers if m.type == "ARMATURE" and m.object is not None}
    saved_positions = {rig: rig.data.pose_position for rig in rigs}
    for rig in rigs:
        rig.data.pose_position = "REST"
    try:
        _remove_binding(target)
        target.vertex_groups.clear()
        modifier = target.modifiers.new("ItTransferWeights", "DATA_TRANSFER")
        modifier.object = source
        modifier.use_vert_data = True
        modifier.data_types_verts = {"VGROUP_WEIGHTS"}
        modifier.vert_mapping = "POLYINTERP_NEAREST"
        modifier.layers_vgroup_select_src = "ALL"
        modifier.layers_vgroup_select_dst = "NAME"
        _activate(target)
        bpy.ops.object.modifier_move_to_index(modifier=modifier.name, index=0)
        bpy.ops.object.datalayout_transfer(modifier=modifier.name)
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    finally:
        for rig, position in saved_positions.items():
            rig.data.pose_position = position

    bone_names = {bone.name for bone in armature.data.bones}
    for group in list(target.vertex_groups):
        if group.name not in bone_names:
            target.vertex_groups.remove(group)
    limit_and_normalise(target, max_influences)
    _bind(target, armature)
    stats = weight_stats(target)
    return stats["weighted"], stats["unweighted"]


def auto_weights(obj: bpy.types.Object, armature: bpy.types.Object, max_influences: int = 4) -> int:
    """Parents `obj` to `armature` with automatic weights (bone heat), and returns how many vertices got none.

    Blender only prints a warning when bone heat fails, for example on a part far from every bone or a mesh
    in pieces, so a zero here is the check that it worked. Existing vertex groups and bindings are replaced.
    """
    _ensure_object_mode()
    _remove_binding(obj)
    obj.vertex_groups.clear()
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    _activate(armature)
    armature.select_set(True)
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")
    limit_and_normalise(obj, max_influences)
    return weight_stats(obj)["unweighted"]


def rigid_skin(
    parts: dict[bpy.types.Object, str], armature: bpy.types.Object, name: str | None = None
) -> bpy.types.Object:
    """Joins mesh parts into one skinned mesh, each part fully weighted to one bone.

    `parts` maps each mesh object to a bone name of `armature`. The parts are joined in world space into the
    first part's object, which is returned (renamed to `name` when given), bound to `armature`.
    """
    if not parts:
        raise ValueError("rigid_skin needs at least one part")
    bone_names = {bone.name for bone in armature.data.bones}
    for part, bone in parts.items():
        if part.type != "MESH":
            raise ValueError(f"{part.name} is not a mesh")
        if bone not in bone_names:
            raise ValueError(f"{armature.name} has no bone '{bone}' for {part.name}")

    _ensure_object_mode()
    for part, bone in parts.items():
        _remove_binding(part)
        part.vertex_groups.clear()
        group = part.vertex_groups.new(name=bone)
        group.add(list(range(len(part.data.vertices))), 1.0, "REPLACE")

    first = next(iter(parts))
    bpy.ops.object.select_all(action="DESELECT")
    for part in parts:
        part.select_set(True)
    _activate(first)
    if len(parts) > 1:
        bpy.ops.object.join()
    if name is not None:
        first.name = name
    _bind(first, armature)
    return first


def limit_and_normalise(obj: bpy.types.Object, max_influences: int = 4) -> None:
    """Drops tiny weights, keeps each vertex's strongest `max_influences`, and makes its weights add up to 1."""
    _ensure_object_mode()
    with bpy.context.temp_override(
        object=obj, active_object=obj, selected_objects=[obj], selected_editable_objects=[obj]
    ):
        bpy.ops.object.vertex_group_clean(group_select_mode="ALL", limit=_CLEAN_THRESHOLD, keep_single=True)
        bpy.ops.object.vertex_group_limit_total(group_select_mode="ALL", limit=max_influences)
        bpy.ops.object.vertex_group_normalize_all(group_select_mode="ALL", lock_active=False)


def bind_weighted(obj: bpy.types.Object, armature: bpy.types.Object, max_influences: int = 4) -> dict[str, int]:
    """Binds a mesh that already has its weights, one vertex group a bone, to `armature`: groups that name no bone are
    dropped, and the weights limited to `max_influences` and normalised. Returns `weight_stats`."""
    _ensure_object_mode()
    bone_names = {bone.name for bone in armature.data.bones}
    for group in list(obj.vertex_groups):
        if group.name not in bone_names:
            obj.vertex_groups.remove(group)
    _activate(obj)
    limit_and_normalise(obj, max_influences)
    _bind(obj, armature)
    return weight_stats(obj)


def move_joints(armature: bpy.types.Object, places: dict) -> list[str]:
    """Moves each joint of `armature` named in `places` to its place there (world space), keeping each bone's turn,
    roll, and length, so the clips made on the rig still turn each joint alike: a body of other proportions. Bones are
    no longer connected. Returns the names of the joints `places` leaves where they were."""
    _ensure_object_mode()
    inverse = armature.matrix_world.inverted()
    with bpy.context.temp_override(object=armature, active_object=armature, selected_objects=[armature]):
        bpy.ops.object.mode_set(mode="EDIT")
        bones = armature.data.edit_bones
        for bone in bones:
            bone.use_connect = False
        for bone in bones:
            if bone.name in places:
                head = inverse @ Vector(places[bone.name])
                along, roll = bone.tail - bone.head, bone.roll
                bone.head = head
                bone.tail = head + along
                bone.roll = roll
        bpy.ops.object.mode_set(mode="OBJECT")
    return [bone.name for bone in armature.data.bones if bone.name not in places]


def weight_stats(obj: bpy.types.Object) -> dict[str, int]:
    """Counts a mesh's weights: `vertices`, `weighted`, `unweighted`, and `max_influences` on one vertex."""
    vertices = obj.data.vertices
    weight_sums = np.array([sum(group.weight for group in vertex.groups) for vertex in vertices], dtype=np.float64)
    counts = [len(vertex.groups) for vertex in vertices]
    unweighted = int(np.count_nonzero(weight_sums < _UNWEIGHTED_SUM))
    return {
        "vertices": len(vertices),
        "weighted": len(vertices) - unweighted,
        "unweighted": unweighted,
        "max_influences": max(counts, default=0),
    }


def _ensure_object_mode() -> None:
    active = bpy.context.view_layer.objects.active
    if active is not None and active.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")


def _activate(obj: bpy.types.Object) -> None:
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)


def _remove_binding(obj: bpy.types.Object) -> None:
    """Removes Armature modifiers and the parent, keeping the object where it is."""
    for modifier in list(obj.modifiers):
        if modifier.type == "ARMATURE":
            obj.modifiers.remove(modifier)
    if obj.parent is not None:
        world = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world


def _bind(obj: bpy.types.Object, armature: bpy.types.Object) -> None:
    """Adds an Armature modifier and parents `obj` to `armature` without moving it."""
    _remove_binding(obj)
    modifier = obj.modifiers.new("Armature", "ARMATURE")
    modifier.object = armature
    modifier.use_vertex_groups = True
    world = obj.matrix_world.copy()
    obj.parent = armature
    obj.matrix_parent_inverse = armature.matrix_world.inverted()
    obj.matrix_world = world
