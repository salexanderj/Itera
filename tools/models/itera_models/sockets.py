"""Parts that join at sockets: rings, lofts through them, and the check that each part's open edge meets its
neighbour's. Run inside Blender.

A socket is where two parts of a body join: a closed loop of vertices that both parts end on, at the same places and
with the same bone weights, so the join stays shut in every pose. A part is built as a loft: a tube through rings, each
ring a loop of points round a station on the part's path. A part that joins another starts from the other's loop, so
both share it by construction; `check` then finds every open loop of the parts and reports one with no partner, or a
partner that differs in places or weights.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import bmesh
import bpy
from mathutils import Vector
from mathutils.kdtree import KDTree


@dataclass(frozen=True)
class Station:
    """A ring on a part's path: its middle, the way the path goes there, and its size across.

    `wide` is the half-width along the ring's side, `top` and `bottom` the reaches along its up and against it. `up` is
    used for the first station alone: later stations carry the frame along the path (`frames`).
    """

    middle: tuple
    way: tuple
    wide: float
    top: float
    bottom: float
    up: tuple | None = None


def frames(stations: list[Station], first_up: tuple) -> list[tuple[Vector, Vector, Vector]]:
    """Each station's (way, up, side), the up turned with the path from one station to the next (a frame that turns
    no more than the path does), and side = up x way, so a ring's points go round counter-clockwise looking back along
    the way."""
    result = []
    up = Vector(first_up)
    previous = None
    for station in stations:
        way = Vector(station.way).normalized()
        if previous is not None:
            up = previous.rotation_difference(way) @ up
        up = (up - way * up.dot(way)).normalized()
        result.append((way, up, up.cross(way)))
        previous = way
    return result


def ring(station: Station, frame: tuple, count: int) -> list[Vector]:
    """The ring's `count` points, from the side round over the top: an ellipse whose upper half reaches `top` and
    lower half `bottom`."""
    way, up, side = frame
    middle = Vector(station.middle)
    points = []
    for index in range(count):
        angle = 2 * math.pi * index / count
        cos, sin = math.cos(angle), math.sin(angle)
        reach = station.top if sin >= 0 else station.bottom
        points.append(middle + side * (station.wide * cos) + up * (reach * sin))
    return points


def align(loop: list[Vector], target: list[Vector]) -> list[int]:
    """The order of `loop`'s indices that best lies on `target`, a ring of as many points: turned and, if it runs the
    other way, reversed. A part that starts from another's loop takes it in this order, so its faces do not twist."""
    if len(loop) != len(target):
        raise ValueError(f"align: a loop of {len(loop)} points does not fit a ring of {len(target)}")
    count = len(loop)
    best, order = None, None
    for reverse in (False, True):
        base = list(reversed(range(count))) if reverse else list(range(count))
        for shift in range(count):
            candidate = base[shift:] + base[:shift]
            cost = sum((loop[candidate[i]] - target[i]).length_squared for i in range(count))
            if best is None or cost < best - 1e-12:
                best, order = cost, candidate
    return order


def loft(
    name: str,
    rings: list[list[Vector]],
    cap_start: Vector | None = None,
    cap_end: Vector | None = None,
) -> bpy.types.Object:
    """A mesh object through `rings`, each of as many points, with quads between rings whose faces look outward when
    each ring goes round counter-clockwise looking back along the path. A cap is a fan of triangles to its point; a
    side with no cap stays open, a socket. Its vertices are numbered ring by ring, then the caps' points."""
    count = len(rings[0])
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    verts = [[bm.verts.new(point) for point in points] for points in rings]
    for near, far in zip(verts, verts[1:]):
        for i in range(count):
            j = (i + 1) % count
            bm.faces.new((near[i], near[j], far[j], far[i]))
    if cap_start is not None:
        pole = bm.verts.new(cap_start)
        first = verts[0]
        for i in range(count):
            bm.faces.new((first[(i + 1) % count], first[i], pole))
    if cap_end is not None:
        pole = bm.verts.new(cap_end)
        last = verts[-1]
        for i in range(count):
            bm.faces.new((last[i], last[(i + 1) % count], pole))
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def boundary_loops(obj: bpy.types.Object) -> list[list[int]]:
    """The mesh's open edges as closed loops of vertex indices, in order. An open edge that forms no closed loop is
    left out."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    neighbours: dict[int, list[int]] = {}
    for edge in bm.edges:
        if edge.is_boundary:
            a, b = edge.verts[0].index, edge.verts[1].index
            neighbours.setdefault(a, []).append(b)
            neighbours.setdefault(b, []).append(a)
    bm.free()
    loops, seen = [], set()
    for start in sorted(neighbours):
        if start in seen or len(neighbours[start]) != 2:
            continue
        loop, previous, current = [start], None, start
        while True:
            seen.add(current)
            options = [n for n in neighbours[current] if n != previous]
            if not options or len(neighbours[current]) != 2:
                loop = None
                break
            previous, current = current, options[0]
            if current == start:
                break
            loop.append(current)
        if loop:
            loops.append(loop)
    return loops


def weights_of(obj: bpy.types.Object, index: int) -> dict[str, float]:
    """A vertex's bone weights by group name, those over nothing."""
    names = {group.index: group.name for group in obj.vertex_groups}
    return {names[g.group]: g.weight for g in obj.data.vertices[index].groups if g.weight > 0}


def joins(objects: list[bpy.types.Object], tolerance: float = 1e-5) -> tuple[list[tuple], list[str]]:
    """Pairs every open loop of `objects` with the loop of another part that lies on it: a list of joins, each
    (object, loop, other object, the other's index for each of the loop's vertices), and the problems: a loop that meets
    no other, or meets one of another length."""
    loops = [(obj, loop) for obj in objects for loop in boundary_loops(obj)]
    trees = {}
    for obj in objects:
        tree = KDTree(len(obj.data.vertices))
        for vertex in obj.data.vertices:
            tree.insert(obj.matrix_world @ vertex.co, vertex.index)
        tree.balance()
        trees[obj.name] = tree
    found, problems = [], []
    for obj, loop in loops:
        partner = None
        for other in objects:
            if other is obj:
                continue
            tree = trees[other.name]
            matches = []
            for index in loop:
                _, near, distance = tree.find(obj.matrix_world @ obj.data.vertices[index].co)
                if near is None or distance > tolerance:
                    break
                matches.append(near)
            else:
                partner = (other, matches)
                break
        middle = sum((obj.matrix_world @ obj.data.vertices[i].co for i in loop), Vector()) / len(loop)
        where = f"({middle.x:.3f}, {middle.y:.3f}, {middle.z:.3f})"
        if partner is None:
            problems.append(f"{obj.name}: an open loop of {len(loop)} vertices at {where} meets no other part")
            continue
        other, matches = partner
        other_loops = [set(other_loop) for other_loop in boundary_loops(other)]
        if set(matches) not in other_loops:
            problems.append(
                f"{obj.name}: its loop of {len(loop)} vertices at {where} lies on {other.name}, which has no open loop "
                "of the same vertices there"
            )
            continue
        found.append((obj, loop, other, matches))
    return found, problems


def check(objects: list[bpy.types.Object], tolerance: float = 1e-5, weight_tolerance: float = 1e-3) -> dict:
    """Checks every socket of the parts `objects`: each open loop meets another part's, vertex for vertex, and both
    sides weigh the same bones alike. Returns {"joins": [...], "problems": [...]}, a join as (part, other, vertices)."""
    found, problems = joins(objects, tolerance)
    listed = []
    for obj, loop, other, matches in found:
        worst, worst_at = 0.0, None
        for index, match in zip(loop, matches):
            mine, theirs = weights_of(obj, index), weights_of(other, match)
            for bone in set(mine) | set(theirs):
                difference = abs(mine.get(bone, 0.0) - theirs.get(bone, 0.0))
                if difference > worst:
                    worst, worst_at = difference, bone
        if worst > weight_tolerance:
            problems.append(
                f"{obj.name} and {other.name}: their socket's weights differ by up to {worst:.3f} on {worst_at}, "
                "so the join opens when that bone turns"
            )
        if obj.name < other.name:
            listed.append((obj.name, other.name, len(loop)))
    return {"joins": listed, "problems": problems}


def share_normals(objects: list[bpy.types.Object], tolerance: float = 1e-5) -> int:
    """Gives both sides of each socket the same normals, the sum of the two sides' vertex normals, so the join shades
    as one surface; every other vertex keeps its own. Sets the parts' custom normals. Returns the vertices changed."""
    normals = {obj.name: [vertex.normal.copy() for vertex in obj.data.vertices] for obj in objects}
    found, _ = joins(objects, tolerance)
    changed = 0
    for obj, loop, other, matches in found:
        if obj.name > other.name:
            continue
        for index, match in zip(loop, matches):
            shared = (obj.data.vertices[index].normal + other.data.vertices[match].normal).normalized()
            normals[obj.name][index] = shared
            normals[other.name][match] = shared.copy()
            changed += 2
    for obj in objects:
        obj.data.normals_split_custom_set_from_vertices([tuple(n) for n in normals[obj.name]])
    return changed
