"""A rig's skin at its joints' limits: each joint turned alone to each limit a recipe gives, and what the skin does
there. Run inside Blender.

For each pose it measures, over all the meshes together:

- stretch: the longest edge's length over its length at rest, and squash, the shortest's (a skin folding in on itself
  shortens its edges towards nothing);
- volume: how much the closed whole's volume changes, as a share of its volume at rest (parts that join at sockets
  close each other's open loops, so together they enclose one volume);
- gap: how far apart the two sides of any socket come (`sockets`); none while both sides weigh the same bones alike.

The poses go on a temporary action, which is removed afterwards; the armature's own action, each turned bone's rotation
mode and turn, and the scene's frame are put back. A sheet of the worst poses, from the side and from three quarters in
front, is drawn with `strips.sheet`, each pose numbered as its result's `sheet`.
"""

from __future__ import annotations

import math

import bpy
from mathutils import Quaternion, Vector

from . import clips, sockets, strips

ACTION = "itera-stress"


def poses(limits: dict[str, list[tuple[str, float]]]) -> list[tuple[str, str, str, float]]:
    """The poses for `limits` ({bone: [(axis, degrees), ...]}, about the bone's own X, Y, or Z): (label, bone, axis,
    degrees), bones in name order."""
    return [
        (f"{bone} {axis}{degrees:+g}", bone, axis, degrees) for bone in sorted(limits) for axis, degrees in limits[bone]
    ]


def check(
    armature: bpy.types.Object,
    meshes: list[bpy.types.Object],
    limits: dict[str, list[tuple[str, float]]],
    sheet=None,
    worst: int = 8,
    most_stretch: float = 1.8,
    least_squash: float = 0.35,
    most_volume: float = 0.08,
    most_gap: float = 1e-4,
) -> dict:
    """Turns each bone of `limits` alone to each of its limits, measures the skin of `meshes`, and draws the `worst`
    poses into `sheet` (a PNG path) if given, the worst by how far an edge stretches or shrinks.

    Returns {"poses": [...], "problems": [...]}. A pose's result has `pose` (its label), `frame`, `stretch` and
    `stretch_at` (the mesh), `squash` and `squash_at`, `volume` (the change as a share), `gap` (metres), and `sheet`
    (its number on the sheet) if drawn. A pose past a limit given here is a problem: an edge stretched past
    `most_stretch` times its length or shrunk below `least_squash` of it, a volume change past `most_volume`, or a
    socket open past `most_gap` metres."""
    missing = sorted(bone for bone in limits if bone not in armature.pose.bones)
    if missing:
        raise ValueError(f"stress: the armature has no bones {missing}")
    listed = poses(limits)
    scene = bpy.context.scene
    data = armature.animation_data
    old_action = (data.action, data.action_slot) if data is not None else (None, None)
    old_frame = scene.frame_current
    old_bones = {
        bone: (pose.rotation_mode, tuple(pose.rotation_quaternion), tuple(pose.rotation_euler))
        for bone, pose in ((bone, armature.pose.bones[bone]) for bone in limits)
    }
    pairs = _socket_pairs(meshes)
    try:
        clips.new_clip(armature, ACTION, 1, frame_end=len(listed) + 1)
        for frame in range(1, len(listed) + 2):
            pose = {bone: {"rotation": (1.0, 0.0, 0.0, 0.0)} for bone in limits}
            if frame > 1:
                _, bone, axis, degrees = listed[frame - 2]
                pose[bone] = {"rotation": tuple(_turn(axis, degrees))}
            clips.key_pose(armature, frame, pose)
        scene.frame_set(1)
        rest = _measure(meshes)
        results = []
        for frame, (label, _, _, _) in enumerate(listed, start=2):
            scene.frame_set(frame)
            results.append(_compare(label, frame, rest, _measure(meshes), pairs))
        problems = [
            problem
            for result in results
            for problem in _problems(result, most_stretch, least_squash, most_volume, most_gap)
        ]
        if sheet is not None and results:
            chosen = sorted(results, key=lambda r: (-max(r["stretch"], 1 / max(r["squash"], 1e-6)), r["pose"]))
            chosen = sorted(chosen[:worst], key=lambda r: r["frame"])
            for number, result in enumerate(chosen, start=1):
                result["sheet"] = number
            frames = [result["frame"] for result in chosen]
            strips.sheet(sheet, armature, meshes, frames, 1, labels=[str(n) for n in range(1, len(chosen) + 1)])
        return {"poses": results, "problems": problems}
    finally:
        if ACTION in bpy.data.actions:
            clips.remove_clip(ACTION)
        if data is not None and old_action[0] is not None:
            data.action, data.action_slot = old_action
        for bone, (mode, quaternion, euler) in old_bones.items():
            pose = armature.pose.bones[bone]
            # The mode first: setting it converts the turn there is into the new mode's values.
            pose.rotation_mode = mode
            pose.rotation_quaternion = quaternion
            pose.rotation_euler = euler
        scene.frame_set(old_frame)


def axes(armature: bpy.types.Object, bones) -> dict[str, tuple]:
    """Each bone's own X axis at rest, in the armature's space: the axis its turns about X go round, to check that a
    limit bends the joint the way meant."""
    return {
        bone: tuple(round(value, 3) for value in Vector(armature.data.bones[bone].matrix_local.to_3x3().col[0]))
        for bone in bones
    }


def _problems(result: dict, most_stretch, least_squash, most_volume, most_gap) -> list[str]:
    pose = result["pose"]
    found = []
    if result["stretch"] > most_stretch:
        found.append(f"stress: {pose}: an edge of {result['stretch_at']} stretches to {result['stretch']:.2f} times")
    if result["squash"] < least_squash:
        found.append(f"stress: {pose}: an edge of {result['squash_at']} shrinks to {result['squash']:.2f} of its length")
    if abs(result["volume"]) > most_volume:
        found.append(f"stress: {pose}: the volume changes by {result['volume'] * 100:+.1f} %")
    if result["gap"] > most_gap:
        found.append(f"stress: {pose}: a socket opens by {result['gap'] * 1000:.1f} mm")
    return found


def _turn(axis: str, degrees: float) -> Quaternion:
    vector = {"X": (1, 0, 0), "Y": (0, 1, 0), "Z": (0, 0, 1)}[axis]
    return Quaternion(vector, math.radians(degrees))


def _socket_pairs(meshes) -> list[tuple[str, int, str, int]]:
    found, _ = sockets.joins(meshes)
    return [
        (obj.name, index, other.name, match)
        for obj, loop, other, matches in found
        if obj.name < other.name
        for index, match in zip(loop, matches)
    ]


def _measure(meshes) -> dict:
    """Each mesh's evaluated vertices (world), edges, and triangles."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    measured = {}
    for obj in meshes:
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        world = evaluated.matrix_world
        points = [world @ vertex.co for vertex in mesh.vertices]
        edges = [tuple(edge.vertices) for edge in mesh.edges]
        mesh.calc_loop_triangles()
        triangles = [tuple(triangle.vertices) for triangle in mesh.loop_triangles]
        evaluated.to_mesh_clear()
        measured[obj.name] = (points, edges, triangles)
    return measured


def _volume(measured) -> float:
    total = 0.0
    for points, _, triangles in measured.values():
        for a, b, c in triangles:
            total += points[a].dot(points[b].cross(points[c]))
    return total / 6.0


def _compare(label, frame, rest, posed, pairs) -> dict:
    stretch, stretch_at, squash, squash_at = 1.0, "", 1.0, ""
    for name, (points, edges, _) in posed.items():
        rest_points = rest[name][0]
        for a, b in edges:
            before = (rest_points[a] - rest_points[b]).length
            if before < 1e-9:
                continue
            ratio = (points[a] - points[b]).length / before
            if ratio > stretch:
                stretch, stretch_at = ratio, name
            if ratio < squash:
                squash, squash_at = ratio, name
    gap = max(
        ((posed[name][0][index] - posed[other][0][match]).length for name, index, other, match in pairs), default=0.0
    )
    volume_rest = _volume(rest)
    volume = _volume(posed) / volume_rest - 1.0 if volume_rest else 0.0
    return {
        "pose": label,
        "frame": frame,
        "stretch": round(stretch, 4),
        "stretch_at": stretch_at,
        "squash": round(squash, 4),
        "squash_at": squash_at,
        "volume": round(volume, 5),
        "gap": round(gap, 7),
    }
