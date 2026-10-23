"""Checks of the Blender scene that Blender itself does not report.

Run inside Blender. scene() looks at the objects a model is made of and returns plain numbers and two lists of
sentences: `problems` (the build fails) and `warnings` (the build goes on). Blender reports a failed automatic weighting
only as a warning, so the check for vertices that weigh no bone is the one that catches it.
"""

from __future__ import annotations

import bmesh
import bpy

from . import selection

SCALE_TOLERANCE = 1e-4
WEIGHT_EPSILON = 1e-6
WEIGHT_SUM_TOLERANCE = 1e-3
ZERO_AREA = 1e-9
MOST_INFLUENCES = 4
# Material node types that read a UV map.
UV_NODE_TYPES = ("TEX_IMAGE", "NORMAL_MAP", "TANGENT", "DISPLACEMENT", "VECTOR_DISPLACEMENT")


def scene(objects=None) -> dict:
    """Checks the given objects (default: the scene's visible meshes, armatures, and empties).

    The result has the totals (`triangles`, `vertices`), one entry for each of `meshes`, `armatures`, and `actions`, and
    the lists `problems` and `warnings`. Everything in it is plain data that json can write.
    """
    chosen = selection.resolve(objects)
    problems: list[str] = []
    warnings: list[str] = []
    depsgraph = bpy.context.evaluated_depsgraph_get()

    meshes = [_mesh(obj, depsgraph, problems, warnings) for obj in chosen if obj.type == "MESH"]
    armatures = [_armature(obj) for obj in chosen if obj.type == "ARMATURE"]
    for obj in chosen:
        if obj.type == "ARMATURE":
            _check_scale(obj, problems)
    if len(armatures) > 1:
        names = ", ".join(entry["name"] for entry in armatures)
        problems.append(f"the model has {len(armatures)} armatures ({names}); the engine reads one skin")
    actions = [_action(action, warnings) for action in bpy.data.actions]

    return {
        "triangles": sum(entry["triangles"] for entry in meshes),
        "vertices": sum(entry["vertices"] for entry in meshes),
        "meshes": meshes,
        "armatures": armatures,
        "actions": actions,
        "problems": problems,
        "warnings": warnings,
    }


def _mesh(obj, depsgraph, problems: list, warnings: list) -> dict:
    """The numbers and findings for one mesh object, taken from its evaluated mesh (modifiers applied)."""
    entry: dict = {"name": obj.name}
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        entry["triangles"] = len(mesh.loop_triangles)
        entry["vertices"] = len(mesh.vertices)
        builder = bmesh.new()
        try:
            builder.from_mesh(mesh)
            # Edges with more than two faces are a broken mesh; open edges (one face) are a hole, or a card such as a
            # leaf or a banner, which a game may want.
            entry["non_manifold_edges"] = sum(1 for edge in builder.edges if len(edge.link_faces) > 2)
            entry["open_edges"] = sum(1 for edge in builder.edges if edge.is_boundary)
            entry["loose_vertices"] = sum(1 for vertex in builder.verts if not vertex.link_edges)
            entry["loose_edges"] = sum(1 for edge in builder.edges if edge.is_wire)
            entry["zero_area_faces"] = sum(1 for face in builder.faces if face.calc_area() < ZERO_AREA)
        finally:
            builder.free()
    finally:
        evaluated.to_mesh_clear()

    entry["scale"] = [round(value, 6) for value in obj.scale]
    entry["uv_maps"] = len(obj.data.uv_layers)
    entry["materials"] = [slot.material.name if slot.material else None for slot in obj.material_slots]
    name = obj.name
    if entry["non_manifold_edges"]:
        count = entry["non_manifold_edges"]
        problems.append(f"{name}: {count} non-manifold edges (shared by more than 2 faces)")
    if entry["open_edges"]:
        warnings.append(f"{name}: {entry['open_edges']} open edges (a hole, or a card such as a leaf or a banner)")
    if entry["loose_vertices"]:
        problems.append(f"{name}: {entry['loose_vertices']} loose vertices")
    if entry["loose_edges"]:
        problems.append(f"{name}: {entry['loose_edges']} loose edges (edges with no face)")
    if entry["zero_area_faces"]:
        problems.append(f"{name}: {entry['zero_area_faces']} faces with no area")
    _check_scale(obj, problems)
    _check_materials(obj, entry, problems, warnings)

    armature = _armature_of(obj)
    entry["skinned"] = armature is not None
    if armature is not None:
        entry.update(_check_weights(obj, armature, problems, warnings))
    return entry


def _check_scale(obj, problems: list) -> None:
    """Reports an object scale other than 1: the engine's tools expect it applied."""
    if any(abs(value - 1.0) > SCALE_TOLERANCE for value in obj.scale):
        values = ", ".join(f"{value:.4g}" for value in obj.scale)
        problems.append(f"{obj.name}: its scale is ({values}), not 1; apply the scale")


def _check_materials(obj, entry: dict, problems: list, warnings: list) -> None:
    """Reports empty and unnamed material slots, and a material with no UV map to put its textures on."""
    name = obj.name
    for index, slot in enumerate(obj.material_slots):
        if slot.material is None:
            problems.append(f"{name}: material slot {index} is empty")
        elif not slot.material.name:
            problems.append(f"{name}: material slot {index} has no name")
    materials = [slot.material for slot in obj.material_slots if slot.material is not None]
    if materials and entry["uv_maps"] == 0:
        if any(_reads_uvs(material) for material in materials):
            problems.append(f"{name}: a material has a texture but the mesh has no UV map")
        else:
            warnings.append(f"{name}: it has a material but no UV map")


def _reads_uvs(material) -> bool:
    """True when the material has a node that needs UV coordinates (an image or a normal map)."""
    tree = material.node_tree
    return tree is not None and any(node.type in UV_NODE_TYPES for node in tree.nodes)


def _armature_of(obj):
    """The armature object that deforms this mesh (an Armature modifier or a parent), or None.

    A mesh parented to one bone is a rigid part that the bone carries, not skinned: it needs no weights, and the
    engine reads it as a part of its own (a model declared by its node).
    """
    for modifier in obj.modifiers:
        if modifier.type == "ARMATURE" and modifier.object is not None:
            return modifier.object
    if obj.parent is not None and obj.parent.type == "ARMATURE" and obj.parent_type != "BONE":
        return obj.parent
    return None


def _check_weights(obj, armature, problems: list, warnings: list) -> dict:
    """Checks a skinned mesh's vertex weights against the bones of its armature. Returns the counts."""
    name = obj.name
    bone_names = {bone.name for bone in armature.data.bones}
    group_names = {group.index: group.name for group in obj.vertex_groups}
    bone_group_indices = {index for index, group_name in group_names.items() if group_name in bone_names}

    unweighted = 0
    too_many = 0
    worst_influences = 0
    sum_off = 0
    used_stray_groups: set = set()
    for vertex in obj.data.vertices:
        total = 0.0
        influences = 0
        for assignment in vertex.groups:
            if assignment.weight <= WEIGHT_EPSILON:
                continue
            if assignment.group in bone_group_indices:
                total += assignment.weight
                influences += 1
            else:
                used_stray_groups.add(group_names.get(assignment.group, f"#{assignment.group}"))
        if total <= WEIGHT_EPSILON:
            unweighted += 1
            continue
        worst_influences = max(worst_influences, influences)
        if influences > MOST_INFLUENCES:
            too_many += 1
        if abs(total - 1.0) > WEIGHT_SUM_TOLERANCE:
            sum_off += 1

    if unweighted:
        problems.append(f"{name}: {unweighted} vertices weigh no bone of {armature.name}")
    if too_many:
        problems.append(f"{name}: {too_many} vertices have more than {MOST_INFLUENCES} bone influences")
    if used_stray_groups:
        listed = ", ".join(sorted(used_stray_groups))
        problems.append(f"{name}: vertex groups that name no bone of {armature.name} carry weight: {listed}")
    unused = sorted(group_name for index, group_name in group_names.items() if index not in bone_group_indices)
    unused = [group_name for group_name in unused if group_name not in used_stray_groups]
    if unused:
        warnings.append(f"{name}: vertex groups that name no bone of {armature.name} (unused): {', '.join(unused)}")
    if sum_off:
        warnings.append(f"{name}: {sum_off} vertices whose bone weights do not sum to 1 (the export normalises them)")
    return {
        "armature": armature.name,
        "unweighted_vertices": unweighted,
        "vertices_over_four_influences": too_many,
        "most_influences": worst_influences,
    }


def _armature(obj) -> dict:
    """Bone counts of an armature object."""
    bones = obj.data.bones
    return {
        "name": obj.name,
        "bones": len(bones),
        "deform_bones": sum(1 for bone in bones if bone.use_deform),
    }


def _action(action, warnings: list) -> dict:
    """The slot count and frame range of an action. An action with no slot animates nothing."""
    has_slot = len(action.slots) > 0
    if not has_slot:
        warnings.append(f"action {action.name}: it has no slot, so it animates nothing")
    start, end = action.frame_range
    return {
        "name": action.name,
        "has_slot": has_slot,
        "slots": [slot.identifier for slot in action.slots],
        "frame_start": start,
        "frame_end": end,
    }
