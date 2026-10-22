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
      "motion": { "file": "PATH_RM.glb", "clips": ["Sword_Dash", ...], "out": "PATH.motion.json" }
    }

The optional "motion" takes root motion from a file of the same clips with it baked in (on the same rig): for each clip
it names, which the model must hold, the root joint's translation keys, less the first, in the model's metres (+z its
front, +x its left), written as JSON beside the model. The model keeps its clips in place; the game moves the body by
these keys. A clip whose root turns or rises is refused.

Paths are from the list's folder. Usage: tools/models/merge_clips.py LIST.json
"""

import json
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


def floats(document, binary, index):
    """An accessor's float values, as tuples of its components."""
    accessor = document["accessors"][index]
    if "sparse" in accessor or accessor.get("componentType") != 5126:
        raise SystemExit("only dense float accessors are read for root motion")
    view = document["bufferViews"][accessor["bufferView"]]
    width = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[accessor["type"]]
    stride = view.get("byteStride", 4 * width)
    start = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    return [struct.unpack_from(f"<{width}f", binary, start + i * stride) for i in range(accessor["count"])]


def root_motion(folder, spec, base, chosen):
    """The "motion" part: each named clip's root translation keys, less the first, written to its "out"."""
    document, binary = read_glb(folder / spec["file"])
    if rig_of(document) != rig_of(base):
        raise SystemExit(f"{spec['file']}: its nodes are not the base's")
    roots = [index for index, node in enumerate(document["nodes"]) if node.get("name") == "root"]
    if len(roots) != 1:
        raise SystemExit(f"{spec['file']}: no one joint named root")
    root = roots[0]
    named = {clip.get("name"): clip for clip in document.get("animations", [])}
    clips = {}
    for name in spec["clips"]:
        if name not in named:
            raise SystemExit(f"{spec['file']} has no clip {name}")
        if name not in chosen:
            raise SystemExit(f"the model has no clip {name} for its root motion")
        clip = named[name]
        keys = None
        for channel in clip["channels"]:
            if channel["target"]["node"] != root:
                continue
            sampler = clip["samplers"][channel["sampler"]]
            if sampler.get("interpolation", "LINEAR") != "LINEAR":
                raise SystemExit(f"{name}: its root is not keyed linearly")
            values = floats(document, binary, sampler["output"])
            if channel["target"]["path"] == "rotation":
                if any(max(abs(a - b) for a, b in zip(value, values[0])) > 1e-4 for value in values):
                    raise SystemExit(f"{name}: its root turns")
            elif channel["target"]["path"] == "translation":
                times = [time for (time,) in floats(document, binary, sampler["input"])]
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
    lines = [f"  {json.dumps(name)}: {json.dumps(keys)}" for name, keys in clips.items()]
    text = "{\n" + f' "source": {json.dumps(spec["file"])},\n "clips": {{\n' + ",\n".join(lines) + "\n }\n}\n"
    target = folder / spec["out"]
    target.write_text(text)
    print(f"{target}: root motion of {len(clips)} clips")


class Builder:
    """The output's accessors, views, and bytes, each source view copied once."""

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

    meshes = []
    for mesh in base.get("meshes", []):
        primitives = []
        for primitive in mesh["primitives"]:
            primitive = dict(primitive)
            primitive["attributes"] = {
                name: builder.accessor("base", base, base_binary, index)
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
    out["skins"] = [
        dict(skin, inverseBindMatrices=builder.accessor("base", base, base_binary, skin["inverseBindMatrices"]))
        if "inverseBindMatrices" in skin
        else skin
        for skin in base.get("skins", [])
    ]
    if base.get("images"):
        raise SystemExit("images are not supported")

    clips = []
    by_name = {clip.get("name"): clip for clip in base.get("animations", [])}
    for name in spec["clips"]:
        if name not in by_name:
            raise SystemExit(f"{spec['base']} has no clip {name}")
        clips.append(animation(builder, "base", base, base_binary, by_name[name]))
    for number, added in enumerate(spec.get("add", [])):
        document, binary = read_glb(folder / added["file"])
        if rig_of(document) != rig_of(base):
            raise SystemExit(f"{added['file']}: its nodes are not the base's")
        source = f"add{number}"
        named = {clip.get("name"): clip for clip in document.get("animations", [])}
        for name in added["clips"]:
            if name not in named:
                raise SystemExit(f"{added['file']} has no clip {name}")
            if any(clip.get("name") == name for clip in clips):
                raise SystemExit(f"the clip {name} is taken twice")
            clips.append(animation(builder, source, document, binary, named[name]))
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
        root_motion(folder, spec["motion"], base, {clip.get("name") for clip in clips})


if __name__ == "__main__":
    main()
