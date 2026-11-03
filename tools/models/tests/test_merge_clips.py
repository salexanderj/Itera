"""Plain-Python test of merge_clips.py: tiny .glb files made here, their clips chosen, and a twist joint added."""

import json
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

PLAIN_PYTHON = True

MODELS_DIR = Path(__file__).resolve().parents[1]
MERGE_CLIPS = MODELS_DIR / "merge_clips.py"


def write_glb(path: Path, clips: dict) -> None:
    """A .glb with one node named root, no meshes, and one translation clip for each name in clips.

    clips maps a clip name to the x position it ends at.
    """
    binary = bytearray()
    views, accessors, animations = [], [], []
    for name, end_x in clips.items():
        times = struct.pack("<2f", 0.0, 1.0)
        values = struct.pack("<6f", 0.0, 0.0, 0.0, end_x, 0.0, 0.0)
        first = len(accessors)
        for data, kind in ((times, "SCALAR"), (values, "VEC3")):
            views.append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(data)})
            binary.extend(data)
            accessors.append({"bufferView": len(views) - 1, "componentType": 5126, "count": 2, "type": kind})
        animations.append(
            {
                "name": name,
                "samplers": [{"input": first, "output": first + 1, "interpolation": "LINEAR"}],
                "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}],
            }
        )
    document = {
        "asset": {"version": "2.0"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "root"}],
        "skins": [],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": views,
        "accessors": accessors,
        "animations": animations,
    }
    text = json.dumps(document).encode()
    text += b" " * (-len(text) % 4)
    total = 12 + 8 + len(text) + 8 + len(binary)
    path.write_bytes(
        struct.pack("<III", 0x46546C67, 2, total)
        + struct.pack("<II", len(text), 0x4E4F534A)
        + text
        + struct.pack("<II", len(binary), 0x004E4942)
        + bytes(binary)
    )


def write_skinned_glb(path: Path) -> None:
    """A .glb with an arm joint, a hand joint 1 m along its +y, and a skin of three vertices the arm wholly weighs, at
    0.1, 0.5, and 0.95 m along it; and one clip turning the hand."""
    binary = bytearray()
    views, accessors = [], []

    def add(data: bytes, component: int, count: int, kind: str) -> int:
        while len(binary) % 4:
            binary.append(0)
        views.append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(data)})
        binary.extend(data)
        accessors.append({"bufferView": len(views) - 1, "componentType": component, "count": count, "type": kind})
        return len(accessors) - 1

    identity = [1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0]
    hand_bind = [1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0, 0, -1.0, 0, 1.0]
    binds = add(struct.pack("<32f", *identity, *hand_bind), 5126, 2, "MAT4")
    places = add(struct.pack("<9f", 0, 0.1, 0, 0, 0.5, 0, 0, 0.95, 0), 5126, 3, "VEC3")
    joints = add(struct.pack("<12B", *([0, 0, 0, 0] * 3)), 5121, 3, "VEC4")
    weights = add(struct.pack("<12f", *([1.0, 0, 0, 0] * 3)), 5126, 3, "VEC4")
    times = add(struct.pack("<2f", 0.0, 1.0), 5126, 2, "SCALAR")
    turns = add(struct.pack("<8f", 0, 0, 0, 1, 0, 0.7071, 0, 0.7071), 5126, 2, "VEC4")
    document = {
        "asset": {"version": "2.0"},
        "scene": 0,
        "scenes": [{"nodes": [0, 2]}],
        "nodes": [
            {"name": "arm", "children": [1]},
            {"name": "hand", "translation": [0, 1.0, 0]},
            {"name": "body", "mesh": 0, "skin": 0},
        ],
        "meshes": [{"primitives": [{"attributes": {"POSITION": places, "JOINTS_0": joints, "WEIGHTS_0": weights}}]}],
        "skins": [{"joints": [0, 1], "inverseBindMatrices": binds}],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": views,
        "accessors": accessors,
        "animations": [
            {
                "name": "Turn",
                "samplers": [{"input": times, "output": turns, "interpolation": "LINEAR"}],
                "channels": [{"sampler": 0, "target": {"node": 1, "path": "rotation"}}],
            }
        ],
    }
    text = json.dumps(document).encode()
    text += b" " * (-len(text) % 4)
    total = 12 + 8 + len(text) + 8 + len(binary)
    path.write_bytes(
        struct.pack("<III", 0x46546C67, 2, total)
        + struct.pack("<II", len(text), 0x4E4F534A)
        + text
        + struct.pack("<II", len(binary), 0x004E4942)
        + bytes(binary)
    )


def accessor_values(document: dict, binary: bytes, index: int, form: str) -> list:
    """An accessor's values, each as a tuple, read by `form` (a struct letter and count, as "4f")."""
    accessor = document["accessors"][index]
    view = document["bufferViews"][accessor["bufferView"]]
    size = struct.calcsize("<" + form)
    start = view["byteOffset"] + accessor.get("byteOffset", 0)
    return [struct.unpack_from("<" + form, binary, start + i * size) for i in range(accessor["count"])]


def read_glb(path: Path) -> tuple:
    """The JSON document and the binary chunk of a .glb."""
    data = path.read_bytes()
    json_length = struct.unpack_from("<I", data, 12)[0]
    document = json.loads(data[20 : 20 + json_length])
    binary_start = 20 + json_length + 8
    return document, data[binary_start:]


def run(listing: Path) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(MERGE_CLIPS), str(listing)], capture_output=True, text=True, check=False)


class MergeClipsTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        write_glb(self.root / "base.glb", {"Walk": 1.0, "Run": 2.0})
        write_glb(self.root / "other.glb", {"Wave": 3.0, "Walk": 9.0})

    def test_one_of_two_clips_is_chosen(self):
        listing = self.root / "list.json"
        listing.write_text(json.dumps({"base": "base.glb", "clips": ["Run"], "out": "out.glb"}), encoding="utf-8")
        result = run(listing)
        self.assertEqual(0, result.returncode, result.stderr)
        document, binary = read_glb(self.root / "out.glb")
        self.assertEqual(["Run"], [clip["name"] for clip in document["animations"]])
        # The kept clip's accessors read the right numbers from the rebuilt buffer.
        sampler = document["animations"][0]["samplers"][0]
        view = document["bufferViews"][document["accessors"][sampler["output"]]["bufferView"]]
        values = struct.unpack_from("<6f", binary, view["byteOffset"])
        self.assertEqual(2.0, values[3])
        self.assertEqual(len(binary), document["buffers"][0]["byteLength"])

    def test_clips_of_another_file_on_the_same_rig_are_added(self):
        listing = self.root / "list.json"
        listing.write_text(
            json.dumps(
                {
                    "base": "base.glb",
                    "clips": ["Walk"],
                    "add": [{"file": "other.glb", "clips": ["Wave"]}],
                    "out": "out.glb",
                }
            ),
            encoding="utf-8",
        )
        self.assertEqual(0, run(listing).returncode)
        document, _ = read_glb(self.root / "out.glb")
        self.assertEqual(["Walk", "Wave"], [clip["name"] for clip in document["animations"]])

    def test_a_twist_joint_is_added_after_the_nodes_and_takes_the_parents_weight_along_the_way(self):
        write_skinned_glb(self.root / "skinned.glb")
        listing = self.root / "list.json"
        twist = {"name": "twist", "parent": "arm", "toward": "hand", "share": 0.5, "weights": [0.2, 0.9]}
        listing.write_text(
            json.dumps(
                {
                    "base": "skinned.glb",
                    "clips": ["Turn"],
                    "add": [{"file": "skinned.glb", "clips": []}],
                    "joints": [twist],
                    "out": "out.glb",
                }
            ),
            encoding="utf-8",
        )
        result = run(listing)
        self.assertEqual(0, result.returncode, result.stderr)
        document, binary = read_glb(self.root / "out.glb")

        # After the nodes, half way to the hand, under the arm; the clip still turns the hand.
        self.assertEqual({"name": "twist", "translation": [0, 0.5, 0]}, document["nodes"][3])
        self.assertEqual([1, 3], document["nodes"][0]["children"])
        self.assertEqual(1, document["animations"][0]["channels"][0]["target"]["node"])
        skin = document["skins"][0]
        self.assertEqual([0, 1, 3], skin["joints"])
        binds = accessor_values(document, binary, skin["inverseBindMatrices"], "16f")
        self.assertAlmostEqual(-0.5, binds[2][13], places=6)
        # None of the arm's weight near the arm, all of it near the hand, and a smooth share between.
        primitive = document["meshes"][0]["primitives"][0]["attributes"]
        slots = accessor_values(document, binary, primitive["JOINTS_0"], "4B")
        weights = accessor_values(document, binary, primitive["WEIGHTS_0"], "4f")
        held = [dict((slot, weight) for slot, weight in zip(s, w) if weight > 0) for s, w in zip(slots, weights)]
        self.assertEqual({0: 1.0}, held[0])
        between = (0.3 / 0.7) ** 2 * (3 - 2 * 0.3 / 0.7)
        self.assertAlmostEqual(between, held[1][2], places=5)
        self.assertAlmostEqual(1 - between, held[1][0], places=5)
        self.assertEqual({2: 1.0}, held[2])

    def test_a_file_with_the_joints_added_is_taken_as_on_the_same_rig(self):
        write_skinned_glb(self.root / "skinned.glb")
        twist = {"name": "twist", "parent": "arm", "toward": "hand", "share": 0.5, "weights": [0.2, 0.9]}
        first = self.root / "first.json"
        first.write_text(
            json.dumps({"base": "skinned.glb", "clips": ["Turn"], "joints": [twist], "out": "joined.glb"}),
            encoding="utf-8",
        )
        self.assertEqual(0, run(first).returncode)
        second = self.root / "second.json"
        second.write_text(
            json.dumps(
                {
                    "base": "skinned.glb",
                    "clips": [],
                    "add": [{"file": "joined.glb", "clips": ["Turn"]}],
                    "joints": [twist],
                    "out": "out.glb",
                }
            ),
            encoding="utf-8",
        )
        result = run(second)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_a_missing_clip_is_refused(self):
        listing = self.root / "list.json"
        listing.write_text(json.dumps({"base": "base.glb", "clips": ["Nope"], "out": "out.glb"}), encoding="utf-8")
        result = run(listing)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Nope", result.stderr)


if __name__ == "__main__":
    unittest.main()
