"""Tests for itera_models.gltf_clips: a posed imported rig read back onto its file's nodes, a cycle settled into a loop,
and clips written on a model's nodes."""

import math
import tempfile
import unittest
from pathlib import Path

import blender_test_utils as utils
import bpy
from mathutils import Matrix, Quaternion, Vector

import merge_clips
from itera_models import clips, export, gltf_clips, rig

BONES = [
    ("root", (0, 0, 0), (0, 0, 0.3), 0.0, None),
    ("body", (0, 0, 0.3), (0, 0, 1.0), 0.0, "root"),
    ("arm", (0, 0, 1.0), (0.5, 0, 1.0), 0.3, "body"),
]


class SampleTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self._tmp = tempfile.TemporaryDirectory()
        self.path = Path(self._tmp.name) / "Rig.glb"
        armature = utils.make_armature("Rig", BONES)
        bpy.ops.mesh.primitive_cube_add(size=0.3, location=(0, 0, 0.6))
        rig.rigid_skin({bpy.context.object: "body"}, armature)
        clips.new_clip(armature, "Wave", 1, frame_end=3)
        for frame, angle in ((1, 0.0), (2, 0.6), (3, -0.4)):
            turn = Quaternion((0, 0, 1), angle)
            clips.key_pose(armature, frame, {"body": {"rotation": turn}, "arm": {"rotation": turn.inverted()}})
        clips.key_pose(armature, 1, {"root": {"location": (0, 0, 0)}})
        clips.key_pose(armature, 3, {"root": {"location": (0.2, 0.1, 0)}})
        export.glb(self.path, clips=["Wave"])

    def tearDown(self):
        self._tmp.cleanup()

    def test_a_posed_imported_rig_gives_its_nodes_transforms_as_the_file_keys_them(self):
        document, binary = gltf_clips.read_glb(self.path)
        nodes = gltf_clips.Nodes(document)
        utils.reset_scene()
        armature, _ = rig.import_reference(self.path)
        turns = gltf_clips.corrections(armature, nodes)
        animation = document["animations"][0]
        fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
        joints = [nodes.index(name) for name in ("root", "body", "arm")]
        armature.animation_data.action = next(action for action in bpy.data.actions if action.name.startswith("Wave"))

        checked = 0
        for channel in animation["channels"]:
            sampler = animation["samplers"][channel["sampler"]]
            times = merge_clips.floats(document, binary, sampler["input"])
            values = merge_clips.floats(document, binary, sampler["output"])
            node, path = channel["target"]["node"], channel["target"]["path"]
            sampled = gltf_clips.sample(armature, nodes, turns, [time[0] * fps for time in times], joints)
            for locals_, keyed in zip(sampled, values):
                location, rotation, _ = locals_[node].decompose()
                if path == "rotation":
                    keyed = Quaternion((keyed[3], keyed[0], keyed[1], keyed[2]))
                    self.assertGreater(abs(rotation.dot(keyed)), 1 - 1e-6, (node, keyed))
                elif path == "translation":
                    self.assertLess((location - Vector(keyed)).length, 1e-5, (node, keyed))
                checked += 1
        self.assertGreater(checked, 6)


class SettleTests(unittest.TestCase):
    """settle on a cycle made by hand: hips 1 m up with hip joints 0.1 m to each side, the body turned to face +x and
    going 0.5 m that way over a second, its hips also turning 0.1 rad about x, which the seam must take back."""

    COUNT = 10

    def _cycle(self):
        document = {
            "nodes": [
                {"name": "root", "children": [1]},
                {"name": "hips", "translation": [0, 1, 0], "children": [2, 3]},
                {"name": "left", "translation": [0.1, 0, 0]},
                {"name": "right", "translation": [-0.1, 0, 0]},
            ]
        }
        nodes = gltf_clips.Nodes(document)
        sampled = []
        for k in range(self.COUNT + 1):
            turn = Matrix.Rotation(math.pi / 2, 4, "Y") @ Matrix.Rotation(0.1 * k / self.COUNT, 4, "X")
            hips = Matrix.Translation((0.5 * k / self.COUNT + 0.3, 1.0, 0.2)) @ turn
            sampled.append({1: hips, 2: nodes.rest[2].copy(), 3: nodes.rest[3].copy()})
        return nodes, sampled

    def test_the_cycle_faces_front_goes_nowhere_and_closes(self):
        nodes, sampled = self._cycle()

        settled = gltf_clips.settle(sampled, nodes, 1, 2, 3, 1.0)

        self.assertAlmostEqual(settled["turned"], -90.0, places=3)
        self.assertAlmostEqual(settled["speed"], 0.5, places=4)
        self.assertAlmostEqual(settled["way"], 0.0, places=3)  # once turned, it went forward
        for node in sampled[0]:
            difference = sampled[-1][node] - sampled[0][node]
            self.assertLess(max(abs(value) for row in difference for value in row), 1e-6, node)
        middle = sum((locals_[1].translation for locals_ in sampled[:-1]), Vector()) / self.COUNT
        self.assertLess(Vector((middle.x, middle.z)).length, 1e-6)  # over the rest's place
        left = nodes.worlds(sampled[0])[2].translation - nodes.worlds(sampled[0])[3].translation
        self.assertGreater(left.normalized().x, 0.99)  # the hips' left is the model's left again


class WriteTests(unittest.TestCase):
    def test_the_clips_go_on_the_models_own_nodes_the_same_bytes_each_time(self):
        document = {
            "scene": 0,
            "scenes": [{"nodes": [0]}],
            "nodes": [
                {"name": "root", "children": [1, 2]},
                {"name": "hips", "translation": [0, 1, 0], "rotation": [0, 0, 0, 1]},
                {"name": "body", "mesh": 0, "skin": 0},
            ],
        }
        clip = {
            "name": "Bob_Loop",
            "times": [0.0, 0.5, 1.0],
            "rotations": {1: [Quaternion(), Quaternion((0, 1, 0), 0.2), Quaternion()]},
            "translations": {1: [Vector((0, 1, 0)), Vector((0, 1.1, 0)), Vector((0, 1, 0))]},
        }
        with tempfile.TemporaryDirectory() as folder:
            first, second = Path(folder) / "a.glb", Path(folder) / "b.glb"
            gltf_clips.write(first, document, [clip])
            gltf_clips.write(second, document, [clip])
            written, binary = gltf_clips.read_glb(first)

            self.assertEqual(first.read_bytes(), second.read_bytes())
        self.assertEqual(merge_clips.rig_of(written), merge_clips.rig_of(document))
        self.assertNotIn("mesh", written["nodes"][2])
        (animation,) = written["animations"]
        self.assertEqual(animation["name"], "Bob_Loop")
        paths = sorted((c["target"]["node"], c["target"]["path"]) for c in animation["channels"])
        self.assertEqual(paths, [(1, "rotation"), (1, "translation")])
        times = written["accessors"][animation["samplers"][0]["input"]]
        self.assertEqual((times["min"], times["max"]), ([0.0], [1.0]))
        self.assertAlmostEqual(merge_clips.floats(written, binary, animation["samplers"][1]["output"])[1][1], 1.1, 5)


if __name__ == "__main__":
    unittest.main()
