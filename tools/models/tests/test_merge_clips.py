"""Plain-Python test of merge_clips.py: a tiny .glb made here, two clips in it, one chosen."""

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

    def test_a_missing_clip_is_refused(self):
        listing = self.root / "list.json"
        listing.write_text(json.dumps({"base": "base.glb", "clips": ["Nope"], "out": "out.glb"}), encoding="utf-8")
        result = run(listing)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Nope", result.stderr)


if __name__ == "__main__":
    unittest.main()
