"""A model's clips as glTF data on the model's own nodes, read back from an armature imported from the model.

Run inside Blender. Blender's glTF import gives each bone a rest of its own, which differs from its node's by a fixed
turn; `corrections` finds that turn, so a posed bone gives its node's transform. `sample` reads a clip that way,
`settle` makes a sampled cycle a loop played in place facing the model's front, and `write` writes clips into a .glb on
the model's nodes, which merge_clips.py adds to the model. retarget.py puts them together for motion capture.

glTF's axes are y up, the model's front +z and its left +x; Blender's are z up, front -y, left +x.
"""

from __future__ import annotations

import json
import math
import struct
from pathlib import Path

import bpy
from mathutils import Matrix, Quaternion, Vector

#: glTF's axes to Blender's, as Blender's glTF import turns them.
TO_BLENDER = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
FROM_BLENDER = TO_BLENDER.inverted()

#: Digits kept of every number written, so a build gives the same bytes on every machine.
DIGITS = 6


def read_glb(path) -> tuple[dict, bytes]:
    """The JSON document and the binary chunk of a .glb file."""
    data = Path(path).read_bytes()
    magic, version, _ = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67 or version != 2:
        raise ValueError(f"{path} is not a glTF 2.0 binary file")
    length = struct.unpack_from("<I", data, 12)[0]
    document = json.loads(data[20 : 20 + length])
    at = 20 + length
    binary = b""
    if at + 8 <= len(data):
        binary_length = struct.unpack_from("<I", data, at)[0]
        binary = data[at + 8 : at + 8 + binary_length]
    return document, binary


class Nodes:
    """A glTF document's nodes: names, parents, and rest transforms (local and in the scene), in glTF's axes."""

    def __init__(self, document: dict) -> None:
        self.document = document
        self.names = [node.get("name") for node in document["nodes"]]
        self.parent = {
            child: index for index, node in enumerate(document["nodes"]) for child in node.get("children", [])
        }
        self.rest = [_local(node) for node in document["nodes"]]
        self.rest_world = self.worlds({})

    def index(self, name: str) -> int:
        """The index of the node `name`."""
        if name not in self.names:
            raise ValueError(f"the model has no node {name}")
        return self.names.index(name)

    def worlds(self, locals_: dict) -> list:
        """Every node's transform in the scene, with `locals_` (node index: local matrix) in place of their rests."""
        worlds: list = [None] * len(self.rest)

        def world(index: int) -> Matrix:
            if worlds[index] is None:
                local = locals_.get(index, self.rest[index])
                worlds[index] = world(self.parent[index]) @ local if index in self.parent else local.copy()
            return worlds[index]

        for index in range(len(self.rest)):
            world(index)
        return worlds


def corrections(armature: bpy.types.Object, nodes: Nodes) -> dict:
    """Each bone's fixed turn from its rest, as Blender imported it, to its node's rest, both in Blender's axes."""
    return {
        bone.name: (armature.matrix_world @ bone.matrix_local).inverted()
        @ (TO_BLENDER @ nodes.rest_world[nodes.index(bone.name)] @ FROM_BLENDER)
        for bone in armature.data.bones
    }


def sample(armature: bpy.types.Object, nodes: Nodes, turns: dict, frames: list, joints: list) -> list:
    """The local transforms (glTF axes) of the nodes `joints` at each of the scene's `frames`, which may fall between
    frames: from their posed bones and their `corrections`. A joint's parent that is not a bone keeps its rest."""
    scene = bpy.context.scene
    original = (scene.frame_current, scene.frame_subframe)
    bones = {nodes.index(bone.name): bone.name for bone in armature.data.bones}
    sampled = []
    try:
        for frame in frames:
            whole = math.floor(frame)
            scene.frame_set(whole, subframe=frame - whole)
            posed = {
                index: FROM_BLENDER
                @ (armature.matrix_world @ armature.pose.bones[name].matrix @ turns[name])
                @ TO_BLENDER
                for index, name in bones.items()
            }
            locals_ = {}
            for joint in joints:
                parent = nodes.parent.get(joint)
                if parent is None:
                    parent_world = Matrix()
                else:
                    parent_world = posed[parent] if parent in posed else nodes.rest_world[parent]
                locals_[joint] = parent_world.inverted() @ posed[joint]
            sampled.append(locals_)
    finally:
        scene.frame_set(original[0], subframe=original[1])
    return sampled


def settle(sampled: list, nodes: Nodes, hips: int, left: int, right: int, seconds: float) -> dict:
    """Makes a sampled cycle (its last sample the pose its first came back to) a loop played in place.

    The cycle is turned about the model's up to face its front: the hips' line from the right joint to the left one,
    on average, lies along the rest's. Its travel along the ground is taken out of the hips, which keep their place
    over the rest's on average, so the game moves the body. What is left at the seam, in every node's turn and the
    hips' place, is spread back over the cycle, so the last sample is the first. Returns the travel taken out, in the
    model's metres and axes, its speed, and its way: degrees round from the front to the left.
    """
    count = len(sampled) - 1
    if count < 1:
        raise ValueError("a loop needs two samples or more")
    worlds = [nodes.worlds(locals_) for locals_ in sampled]

    across = Vector((0.0, 0.0))
    for world in worlds[:-1]:
        side = world[left].translation - world[right].translation
        across += Vector((side.x, side.z))
    rest_side = nodes.rest_world[left].translation - nodes.rest_world[right].translation
    # A turn about +y by a takes the direction atan2(x, z) to atan2(x, z) + a.
    turn = math.atan2(rest_side.x, rest_side.z) - math.atan2(across.x, across.y)
    facing = Matrix.Rotation(turn, 4, "Y")

    placed = [facing @ world[hips] for world in worlds]
    start, end = placed[0].translation, placed[count].translation
    travel = Vector((end.x - start.x, 0.0, end.z - start.z))
    middle = Vector((0.0, 0.0, 0.0))
    for k, matrix in enumerate(placed[:-1]):
        middle += matrix.translation - travel * (k / count)
    middle /= count
    rest_hips = nodes.rest_world[hips].translation
    shift = Vector((rest_hips.x - middle.x, 0.0, rest_hips.z - middle.z))
    parent = nodes.parent.get(hips)
    for k, (locals_, world, matrix) in enumerate(zip(sampled, worlds, placed)):
        matrix.translation = matrix.translation - travel * (k / count) + shift
        parent_world = world[parent] if parent is not None else Matrix()
        locals_[hips] = parent_world.inverted() @ matrix

    for node in sampled[0]:
        first, last = sampled[0][node], sampled[count][node]
        residual = last.to_quaternion() @ first.to_quaternion().inverted()
        if residual.w < 0:
            residual.negate()
        gap = last.translation - first.translation if node == hips else Vector()
        for k, locals_ in enumerate(sampled):
            share = k / count
            location, rotation, scale = locals_[node].decompose()
            rotation = Quaternion().slerp(residual.inverted(), share) @ rotation
            locals_[node] = Matrix.LocRotScale(location - gap * share, rotation, scale)
        sampled[count][node] = sampled[0][node].copy()

    way = math.degrees(math.atan2(travel.x, travel.z)) if travel.length > 1e-6 else 0.0
    return {
        "travel": [round(value, 4) for value in travel],
        "speed": round(travel.length / seconds, 4) if seconds > 0 else 0.0,
        "way": round(way, 1),
        "turned": round(math.degrees(turn), 1),
    }


def write(path, document: dict, clips: list) -> None:
    """Writes `clips` into a .glb on `document`'s nodes, so merge_clips.py takes them over: each clip a dict of `name`,
    `times` (seconds), `rotations` (node index: one local turn a time), and `translations` (node index: one local
    place a time), keyed linearly. The nodes are the document's, without their meshes, skins, and cameras; the file
    holds nothing else. Numbers keep `DIGITS` digits, so the bytes are the same on every build."""
    binary = bytearray()
    views: list = []
    accessors: list = []

    def add(values: list, kind: str) -> int:
        while len(binary) % 4:
            binary.append(0)
        offset = len(binary)
        width = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[kind]
        for value in values:
            items = (value,) if width == 1 else tuple(value)
            binary.extend(struct.pack(f"<{width}f", *(round(float(item), DIGITS) for item in items)))
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(binary) - offset})
        accessor = {"bufferView": len(views) - 1, "componentType": 5126, "count": len(values), "type": kind}
        if kind == "SCALAR":
            accessor["min"] = [round(float(min(values)), DIGITS)]
            accessor["max"] = [round(float(max(values)), DIGITS)]
        accessors.append(accessor)
        return len(accessors) - 1

    animations = []
    for clip in clips:
        times = add(clip["times"], "SCALAR")
        samplers, channels = [], []
        for path_name, kind, key, values in [
            *(("rotation", "VEC4", node, values) for node, values in sorted(clip["rotations"].items())),
            *(("translation", "VEC3", node, values) for node, values in sorted(clip.get("translations", {}).items())),
        ]:
            if path_name == "rotation":
                values = [(q.x, q.y, q.z, q.w) for q in _continuous(values)]
            samplers.append({"input": times, "output": add(values, kind), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": key, "path": path_name}})
        animations.append({"name": clip["name"], "channels": channels, "samplers": samplers})

    nodes = [{key: value for key, value in node.items() if key not in ("mesh", "skin", "camera")}
             for node in document["nodes"]]
    out = {
        "asset": {"version": "2.0", "generator": "Itera tools/models retarget"},
        "scene": document.get("scene", 0),
        "scenes": document.get("scenes", []),
        "nodes": nodes,
        "animations": animations,
        "accessors": accessors,
        "bufferViews": views,
        "buffers": [{"byteLength": len(binary)}],
    }
    text = json.dumps(out, separators=(",", ":")).encode("utf-8")
    text += b" " * (-len(text) % 4)
    binary.extend(b"\0" * (-len(binary) % 4))
    total = 12 + 8 + len(text) + 8 + len(binary)
    with open(path, "wb") as handle:
        handle.write(struct.pack("<III", 0x46546C67, 2, total))
        handle.write(struct.pack("<II", len(text), 0x4E4F534A) + text)
        handle.write(struct.pack("<II", len(binary), 0x004E4942) + binary)


def _local(node: dict) -> Matrix:
    if "matrix" in node:
        values = node["matrix"]
        return Matrix([values[0:4], values[4:8], values[8:12], values[12:16]]).transposed()
    x, y, z, w = node.get("rotation", (0.0, 0.0, 0.0, 1.0))
    return Matrix.LocRotScale(
        Vector(node.get("translation", (0.0, 0.0, 0.0))),
        Quaternion((w, x, y, z)),
        Vector(node.get("scale", (1.0, 1.0, 1.0))),
    )


def _continuous(rotations: list) -> list:
    """The turns with each on the same side as the one before, so no key flips to its negative."""
    out = []
    for rotation in rotations:
        rotation = rotation.copy()
        if out and out[-1].dot(rotation) < 0:
            rotation.negate()
        out.append(rotation)
    return out
