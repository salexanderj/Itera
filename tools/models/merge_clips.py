#!/usr/bin/env python3
"""Builds a skinned glTF model (.glb) from a base file and clips chosen from it and from other files on the same rig.

The base file gives the model: its nodes, meshes, skins, and materials, unchanged. Its animations are dropped but those
named, and the named animations of each added file are taken over. Every added file must have the base's nodes, in the
same order and rest pose, so an animation's channels keep their node indices. The binary buffer is rebuilt with only the
views the model and the chosen clips read, each aligned to 4 bytes.

The clip list is a JSON file:

    {
      "base": "PATH.glb",
      "clips": ["Idle_Loop", ...],
      "add": [ { "file": "PATH.glb", "clips": ["Sword_Block", ...] } ],
      "out": "PATH.glb",
      "motion": { "from": [ { "file": "PATH_RM.glb", "clips": ["Sword_Dash", ...] } ], "out": "PATH.motion.json" },
      "joints": [ { "name": "lowerarm_twist_01_l", "parent": "lowerarm_l", "toward": "hand_l", "share": 0.5,
                    "weights": [0.2, 0.9] } ]
    }

The optional "joints" adds joints the base lacks to its one skin, each after its nodes, so every clip keeps its node
indices and holds each new joint at its rest: a twist joint "share" of the way from its "parent" to the parent's child
"toward", turned as the parent is. Each vertex the parent weighs gives the new joint a share of that weight, rising
smoothly from none to all as the vertex lies from the first to the second of "weights" along the way from the parent's
bound place to the child's; each vertex keeps its four largest weights, summing to one. An added file may have the base's
nodes, or the base's with the joints added (a clip made on the model this builds).

The optional "motion" takes root motion from files of the same clips with it baked in (on the same rig): for each clip
each file names, which the model must hold, the root joint's translation keys, less the first, in the model's metres (+z
its front, +x its left), and where the root turns about the up axis, its turn from its first key in radians (to the left
above 0), written as JSON beside the model. The model keeps its clips in place; the game moves and turns the body by
these keys. A clip whose root rises or tilts is refused. A file of "from" with "place": true is one the model's
clips came from with their root motion on (a clip retargeted with "rootMotion"): the model's copies of the clips it
names lose their root's keys, so they play in place.

Paths are from the list's folder. Usage: tools/models/merge_clips.py LIST.json
"""

import json
import math
import struct
import sys
from pathlib import Path

GLB_MAGIC = 0x46546C67
JSON_CHUNK = 0x4E4F534A
BIN_CHUNK = 0x004E4942


def read_glb(path):
    data = path.read_bytes()
    magic, version, _ = struct.unpack_from("<III", data, 0)
    if magic != GLB_MAGIC or version != 2:
        raise SystemExit(f"{path}: not a glTF 2.0 binary file")
    offset = 12
    document, binary = None, b""
    while offset < len(data):
        length, kind = struct.unpack_from("<II", data, offset)
        chunk = data[offset + 8 : offset + 8 + length]
        if kind == JSON_CHUNK:
            document = json.loads(chunk)
        elif kind == BIN_CHUNK:
            binary = chunk
        offset += 8 + length
    if document is None:
        raise SystemExit(f"{path}: no JSON chunk")
    return document, binary


def rig_of(document):
    return [
        (node.get("name"), node.get("children"), node.get("translation"), node.get("rotation"), node.get("scale"))
        for node in document["nodes"]
    ]


def matrix(values):
    """A glTF matrix (16 values, column by column) as rows."""
    return [[values[column * 4 + row] for column in range(4)] for row in range(4)]


def values_of(rows):
    """Rows as a glTF matrix's 16 values, column by column."""
    return [rows[row][column] for column in range(4) for row in range(4)]


def product(a, b):
    return [[sum(a[row][k] * b[k][column] for k in range(4)) for column in range(4)] for row in range(4)]


def inverse(rows):
    """A 4x4 matrix's inverse, by Gauss-Jordan elimination with partial pivoting."""
    work = [list(row) + [1.0 if row_index == column else 0.0 for column in range(4)] for row_index, row in enumerate(rows)]
    for column in range(4):
        pivot = max(range(column, 4), key=lambda row: abs(work[row][column]))
        if abs(work[pivot][column]) < 1e-12:
            raise SystemExit("an inverse bind matrix cannot be inverted")
        work[column], work[pivot] = work[pivot], work[column]
        scale = work[column][column]
        work[column] = [value / scale for value in work[column]]
        for row in range(4):
            if row != column and work[row][column] != 0.0:
                factor = work[row][column]
                work[row] = [value - factor * pivoted for value, pivoted in zip(work[row], work[column])]
    return [row[4:] for row in work]


def smooth(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3 - 2 * x)


def floats(document, binary, index):
    """An accessor's float values, as tuples of its components."""
    accessor = document["accessors"][index]
    if "sparse" in accessor or accessor.get("componentType") != 5126:
        raise SystemExit("only dense float accessors are read for root motion")
    view = document["bufferViews"][accessor["bufferView"]]
    width = {"SCALAR": 1, "VEC3": 3, "VEC4": 4, "MAT4": 16}[accessor["type"]]
    stride = view.get("byteStride", 4 * width)
    start = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    return [struct.unpack_from(f"<{width}f", binary, start + i * stride) for i in range(accessor["count"])]


def root_motion(folder, spec, rigs, chosen):
    """The "motion" part: each named clip's root translation keys, less the first, and its turn, written to its "out"."""
    if not isinstance(spec.get("from"), list) or "out" not in spec:
        raise SystemExit('"motion" takes "from", a list of files and their clips, and "out"')
    clips = {}
    for source in spec["from"]:
        for name, keys in clip_motions(folder, source, rigs, chosen).items():
            if name in clips:
                raise SystemExit(f"the root motion of {name} is taken twice")
            clips[name] = keys
    sources = json.dumps([source["file"] for source in spec["from"]])
    lines = [f"  {json.dumps(name)}: {json.dumps(keys)}" for name, keys in clips.items()]
    text = "{\n" + f' "sources": {sources},\n "clips": {{\n' + ",\n".join(lines) + "\n }\n}\n"
    target = folder / spec["out"]
    target.write_text(text)
    print(f"{target}: root motion of {len(clips)} clips")


def clip_motions(folder, source, rigs, chosen):
    """One file's named clips' root motion: each one's seconds, side and forward metres, and turn where its root turns."""
    document, binary = read_glb(folder / source["file"])
    if rig_of(document) not in rigs:
        raise SystemExit(f"{source['file']}: its nodes are not the base's")
    roots = [index for index, node in enumerate(document["nodes"]) if node.get("name") == "root"]
    if len(roots) != 1:
        raise SystemExit(f"{source['file']}: no one joint named root")
    root = roots[0]
    named = {clip.get("name"): clip for clip in document.get("animations", [])}
    clips = {}
    for name in source["clips"]:
        if name not in named:
            raise SystemExit(f"{source['file']} has no clip {name}")
        if name not in chosen:
            raise SystemExit(f"the model has no clip {name} for its root motion")
        clip = named[name]
        keys, turns = None, None
        for channel in clip["channels"]:
            if channel["target"]["node"] != root:
                continue
            sampler = clip["samplers"][channel["sampler"]]
            if sampler.get("interpolation", "LINEAR") != "LINEAR":
                raise SystemExit(f"{name}: its root is not keyed linearly")
            values = floats(document, binary, sampler["output"])
            times = [time for (time,) in floats(document, binary, sampler["input"])]
            if channel["target"]["path"] == "rotation":
                turns = (times, [turn_about_up(name, value, values[0]) for value in values])
            elif channel["target"]["path"] == "translation":
                keys = (times, values)
        if keys is None:
            raise SystemExit(f"{name}: its root has no translation")
        times, values = keys
        if any(abs(value[1] - values[0][1]) > 1e-3 for value in values):
            raise SystemExit(f"{name}: its root rises")
        first = values[0]
        clips[name] = {
            "seconds": [round(time, 4) for time in times],
            "side": [round(value[0] - first[0], 4) for value in values],
            "forward": [round(value[2] - first[2], 4) for value in values],
        }
        if turns is not None and any(abs(turn) > 1e-4 for turn in turns[1]):
            if turns[0] != times:
                raise SystemExit(f"{name}: its root's turn and its translation are keyed at other times")
            clips[name]["turn"] = [round(turn, 4) for turn in unwrapped(turns[1])]
    return clips


def turn_about_up(name, rotation, first):
    """How far the root's rotation (x, y, z, w) is turned from its first about the up axis (+y), in radians, to the left
    above 0; a turn about any other axis is refused."""
    x, y, z, w = rotation
    fx, fy, fz, fw = (-first[0], -first[1], -first[2], first[3])
    # The turn from the first: rotation times the first's inverse, as the root's parent sees it.
    turn = (
        w * fx + x * fw + y * fz - z * fy,
        w * fy - x * fz + y * fw + z * fx,
        w * fz + x * fy - y * fx + z * fw,
        w * fw - x * fx - y * fy - z * fz,
    )
    if max(abs(turn[0]), abs(turn[2])) > 1e-3:
        raise SystemExit(f"{name}: its root tilts")
    return 2 * math.atan2(turn[1], turn[3])


def unwrapped(turns):
    """Turns made continuous: each within half a round of the one before."""
    out = []
    for turn in turns:
        if out:
            turn += round((out[-1] - turn) / math.tau) * math.tau
        out.append(turn)
    return out


class Builder:
    """The output's accessors, views, and bytes, each source view copied once."""

    def data(self, raw, accessor):
        """New bytes in a view of their own, read by a new accessor like `accessor` (its view and offset set here)."""
        while len(self.binary) % 4:
            self.binary.append(0)
        self.views.append({"buffer": 0, "byteOffset": len(self.binary), "byteLength": len(raw)})
        self.binary.extend(raw)
        accessor = {key: value for key, value in accessor.items() if key not in ("bufferView", "byteOffset", "min", "max")}
        accessor["bufferView"] = len(self.views) - 1
        self.accessors.append(accessor)
        return len(self.accessors) - 1

    def __init__(self):
        self.accessors = []
        self.views = []
        self.binary = bytearray()
        self._views = {}
        self._accessors = {}

    def view(self, source, document, binary, index):
        key = (source, index)
        if key not in self._views:
            view = dict(document["bufferViews"][index])
            start = view.get("byteOffset", 0)
            data = binary[start : start + view["byteLength"]]
            while len(self.binary) % 4:
                self.binary.append(0)
            view["buffer"] = 0
            view["byteOffset"] = len(self.binary)
            self.binary.extend(data)
            self._views[key] = len(self.views)
            self.views.append(view)
        return self._views[key]

    def accessor(self, source, document, binary, index):
        key = (source, index)
        if key not in self._accessors:
            accessor = dict(document["accessors"][index])
            if "sparse" in accessor:
                raise SystemExit("sparse accessors are not supported")
            if "bufferView" in accessor:
                accessor["bufferView"] = self.view(source, document, binary, accessor["bufferView"])
            self._accessors[key] = len(self.accessors)
            self.accessors.append(accessor)
        return self._accessors[key]


def add_joints(base, binary, joints, builder):
    """The base's nodes, skin, and weights with `joints` added (the list's "joints"): the new nodes and skin, and a map
    from each skinned primitive's weight accessors to the new ones."""
    if len(base.get("skins", [])) != 1:
        raise SystemExit("joints are added to a base with one skin")
    nodes = [dict(node) for node in base["nodes"]]
    names = {node.get("name"): index for index, node in enumerate(nodes)}
    skin = dict(base["skins"][0])
    slots = list(skin["joints"])
    binds = [matrix(values) for values in floats(base, binary, skin["inverseBindMatrices"])]
    added = []
    for joint in joints:
        if joint["name"] in names:
            raise SystemExit(f"the base already has a node {joint['name']}")
        parent, toward = names.get(joint["parent"]), names.get(joint["toward"])
        if parent not in slots or toward not in slots or toward not in nodes[parent].get("children", []):
            raise SystemExit(f"{joint['name']}: {joint['toward']} is no joint under the joint {joint['parent']}")
        share = float(joint["share"])
        place = [share * value for value in nodes[toward].get("translation", [0.0, 0.0, 0.0])]
        nodes.append({"name": joint["name"], "translation": place})
        nodes[parent]["children"] = list(nodes[parent]["children"]) + [len(nodes) - 1]
        names[joint["name"]] = len(nodes) - 1
        # Its inverse bind: its own place taken off, then the parent's.
        moved = [[1.0 if row == column else 0.0 for column in range(4)] for row in range(4)]
        for axis in range(3):
            moved[axis][3] = -place[axis]
        parent_bind = binds[slots.index(parent)]
        binds.append(product(moved, parent_bind))
        slots.append(len(nodes) - 1)
        start = [row[3] for row in inverse(parent_bind)][:3]
        end = [row[3] for row in inverse(binds[slots.index(toward)])][:3]
        low, high = joint["weights"]
        added.append((slots.index(parent), len(slots) - 1, start, end, float(low), float(high)))
    skin["joints"] = slots
    bind_bytes = b"".join(struct.pack("<16f", *values_of(rows)) for rows in binds)
    skin["inverseBindMatrices"] = builder.data(
        bind_bytes, dict(base["accessors"][base["skins"][0]["inverseBindMatrices"]], count=len(binds))
    )

    # Each skinned vertex's weights, a share of the parent's moved to each new joint.
    moved_weights = {}
    for node in base["nodes"]:
        if "mesh" not in node or node.get("skin") != 0:
            continue
        for primitive in base["meshes"][node["mesh"]]["primitives"]:
            attributes = primitive["attributes"]
            if "JOINTS_1" in attributes or "WEIGHTS_1" in attributes:
                raise SystemExit("joints are added to a skin of four weights a vertex")
            key = (attributes["JOINTS_0"], attributes["WEIGHTS_0"])
            if key in moved_weights:
                continue
            joint_accessor = base["accessors"][attributes["JOINTS_0"]]
            weight_accessor = base["accessors"][attributes["WEIGHTS_0"]]
            if weight_accessor.get("componentType") != 5126:
                raise SystemExit("joints are added to a skin whose weights are floats")
            places = floats(base, binary, attributes["POSITION"])
            weights = floats(base, binary, attributes["WEIGHTS_0"])
            joint_slots = whole(base, binary, attributes["JOINTS_0"])
            new_slots, new_weights = [], []
            for place, vertex_slots, vertex_weights in zip(places, joint_slots, weights):
                held = {}
                for slot, weight in zip(vertex_slots, vertex_weights):
                    if weight > 0:
                        held[slot] = held.get(slot, 0.0) + weight
                for parent_slot, new_slot, start, end, low, high in added:
                    weight = held.get(parent_slot, 0.0)
                    if weight <= 0:
                        continue
                    way = [b - a for a, b in zip(start, end)]
                    length = sum(value * value for value in way)
                    along = sum((p - a) * w for p, a, w in zip(place, start, way)) / length if length > 0 else 0.0
                    given = weight * smooth((along - low) / (high - low) if high > low else float(along >= high))
                    if given > 0:
                        held[parent_slot] = weight - given
                        held[new_slot] = given
                kept = sorted(held.items(), key=lambda item: (-item[1], item[0]))[:4]
                total = sum(weight for _, weight in kept)
                kept = [(slot, weight / total) for slot, weight in kept if weight > 0] if total > 0 else []
                kept += [(0, 0.0)] * (4 - len(kept))
                new_slots.append([slot for slot, _ in kept])
                new_weights.append([weight for _, weight in kept])
            wide = len(slots) > 256 or joint_accessor.get("componentType") == 5123
            joint_bytes = b"".join(struct.pack("<4H" if wide else "<4B", *vertex) for vertex in new_slots)
            weight_bytes = b"".join(struct.pack("<4f", *vertex) for vertex in new_weights)
            moved_weights[key] = (
                builder.data(joint_bytes, dict(joint_accessor, componentType=5123 if wide else 5121)),
                builder.data(weight_bytes, weight_accessor),
            )
    return nodes, skin, moved_weights


def whole(document, binary, index):
    """An accessor's unsigned whole values (bytes or shorts), as tuples of its components."""
    accessor = document["accessors"][index]
    kind = {5121: ("B", 1), 5123: ("H", 2)}.get(accessor.get("componentType"))
    if "sparse" in accessor or kind is None:
        raise SystemExit("only dense unsigned byte and short joint accessors are read")
    view = document["bufferViews"][accessor["bufferView"]]
    width = {"SCALAR": 1, "VEC4": 4}[accessor["type"]]
    stride = view.get("byteStride", kind[1] * width)
    start = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    return [struct.unpack_from(f"<{width}{kind[0]}", binary, start + i * stride) for i in range(accessor["count"])]


def in_place(document, clip):
    """The clip without its root joint's keys, its other samplers renumbered: played in place, its root at rest."""
    roots = {index for index, node in enumerate(document["nodes"]) if node.get("name") == "root"}
    channels = [channel for channel in clip["channels"] if channel["target"]["node"] not in roots]
    used = sorted({channel["sampler"] for channel in channels})
    number = {sampler: index for index, sampler in enumerate(used)}
    return dict(
        clip,
        channels=[dict(channel, sampler=number[channel["sampler"]]) for channel in channels],
        samplers=[clip["samplers"][sampler] for sampler in used],
    )


def animation(builder, source, document, binary, clip):
    copied = dict(clip)
    copied["samplers"] = [
        dict(
            sampler,
            input=builder.accessor(source, document, binary, sampler["input"]),
            output=builder.accessor(source, document, binary, sampler["output"]),
        )
        for sampler in clip["samplers"]
    ]
    return copied


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    listing = Path(sys.argv[1]).resolve()
    folder = listing.parent
    spec = json.loads(listing.read_text())
    base, base_binary = read_glb(folder / spec["base"])
    builder = Builder()
    out = dict(base)
    rigs = [rig_of(base)]
    moved_weights = {}
    if spec.get("joints"):
        out["nodes"], joined_skin, moved_weights = add_joints(base, base_binary, spec["joints"], builder)
        rigs.append(rig_of(out))

    meshes = []
    for mesh in base.get("meshes", []):
        primitives = []
        for primitive in mesh["primitives"]:
            primitive = dict(primitive)
            # Weights a joint was added to are the new ones; the base's are not copied.
            weights = moved_weights.get((primitive["attributes"].get("JOINTS_0"), primitive["attributes"].get("WEIGHTS_0")))
            primitive["attributes"] = {
                name: weights[0]
                if weights is not None and name == "JOINTS_0"
                else weights[1]
                if weights is not None and name == "WEIGHTS_0"
                else builder.accessor("base", base, base_binary, index)
                for name, index in primitive["attributes"].items()
            }
            if "indices" in primitive:
                primitive["indices"] = builder.accessor("base", base, base_binary, primitive["indices"])
            if "targets" in primitive:
                primitive["targets"] = [
                    {name: builder.accessor("base", base, base_binary, index) for name, index in target.items()}
                    for target in primitive["targets"]
                ]
            primitives.append(primitive)
        meshes.append(dict(mesh, primitives=primitives))
    out["meshes"] = meshes
    out["skins"] = (
        [joined_skin]
        if moved_weights or spec.get("joints")
        else [
            dict(skin, inverseBindMatrices=builder.accessor("base", base, base_binary, skin["inverseBindMatrices"]))
            if "inverseBindMatrices" in skin
            else skin
            for skin in base.get("skins", [])
        ]
    )
    if base.get("images"):
        raise SystemExit("images are not supported")

    clips = []
    by_name = {clip.get("name"): clip for clip in base.get("animations", [])}
    for name in spec["clips"]:
        if name not in by_name:
            raise SystemExit(f"{spec['base']} has no clip {name}")
        clips.append(animation(builder, "base", base, base_binary, by_name[name]))
    placed = {
        name for source in spec.get("motion", {}).get("from", []) if source.get("place") for name in source["clips"]
    }
    for number, added in enumerate(spec.get("add", [])):
        document, binary = read_glb(folder / added["file"])
        if rig_of(document) not in rigs:
            raise SystemExit(f"{added['file']}: its nodes are not the base's")
        source = f"add{number}"
        named = {clip.get("name"): clip for clip in document.get("animations", [])}
        for name in added["clips"]:
            if name not in named:
                raise SystemExit(f"{added['file']} has no clip {name}")
            if any(clip.get("name") == name for clip in clips):
                raise SystemExit(f"the clip {name} is taken twice")
            clip = in_place(document, named[name]) if name in placed else named[name]
            clips.append(animation(builder, source, document, binary, clip))
    out["animations"] = clips
    out["accessors"] = builder.accessors
    out["bufferViews"] = builder.views
    while len(builder.binary) % 4:
        builder.binary.append(0)
    out["buffers"] = [{"byteLength": len(builder.binary)}]

    text = json.dumps(out, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    total = 12 + 8 + len(text) + 8 + len(builder.binary)
    target = folder / spec["out"]
    with target.open("wb") as file:
        file.write(struct.pack("<III", GLB_MAGIC, 2, total))
        file.write(struct.pack("<II", len(text), JSON_CHUNK))
        file.write(text)
        file.write(struct.pack("<II", len(builder.binary), BIN_CHUNK))
        file.write(builder.binary)
    print(f"{target}: {len(clips)} clips, {total / 1e6:.1f} MB")
    if "motion" in spec:
        root_motion(folder, spec["motion"], rigs, {clip.get("name") for clip in clips})


if __name__ == "__main__":
    main()
