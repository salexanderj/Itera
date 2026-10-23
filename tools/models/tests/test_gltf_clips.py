"""Tests for itera_models.gltf_clips: a posed imported rig read back onto its file's nodes, a cycle settled into a loop,
and clips written on a model's nodes."""

import math
import tempfile
import unittest
from pathlib import Path

import blender_test_utils as utils
import bpy
import merge_clips
from itera_models import clips, export, gltf_clips, rig
from mathutils import Matrix, Quaternion, Vector

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

    def test_a_cycle_kept_facing_goes_nowhere_and_closes_still_turned(self):
        nodes, sampled = self._cycle()

        settled = gltf_clips.settle(sampled, nodes, 1, 2, 3, 1.0, face=False)

        self.assertEqual(settled["turned"], 0.0)
        self.assertAlmostEqual(settled["way"], 90.0, places=3)  # it went the way it faces, to its left
        difference = sampled[-1][1] - sampled[0][1]
        self.assertLess(max(abs(value) for row in difference for value in row), 1e-6)
        left = nodes.worlds(sampled[0])[2].translation - nodes.worlds(sampled[0])[3].translation
        self.assertLess(left.normalized().z, -0.99)  # its hips' left still turned a quarter round

    def test_a_clip_played_once_is_left_as_it_is(self):
        nodes, sampled = self._cycle()
        before = [{node: matrix.copy() for node, matrix in locals_.items()} for locals_ in sampled]

        settled = gltf_clips.settle(sampled, nodes, 1, 2, 3, 1.0, loop=False)

        self.assertAlmostEqual(settled["speed"], 0.5, places=4)
        self.assertEqual((settled["turned"], settled["lowered"]), (0.0, 0.0))
        for was, now in zip(before, sampled):
            for node in was:
                difference = now[node] - was[node]
                self.assertEqual(max(abs(value) for row in difference for value in row), 0.0, node)



class MirrorTests(unittest.TestCase):
    """mirror on a body made by hand: hips 1 m up, a leg each side 0.1 m out, each with a foot 0.5 m below it."""

    def setUp(self):
        self.nodes = gltf_clips.Nodes(
            {
                "nodes": [
                    {"name": "root", "children": [1]},
                    {"name": "hips", "translation": [0, 1, 0], "children": [2, 3]},
                    {"name": "thigh_l", "translation": [0.1, 0, 0], "children": [4]},
                    {"name": "thigh_r", "translation": [-0.1, 0, 0], "children": [5]},
                    {"name": "foot_l", "translation": [0, -0.5, 0]},
                    {"name": "foot_r", "translation": [0, -0.5, 0]},
                ]
            }
        )

    def _posed(self, hips=None, left=None):
        rest = self.nodes.rest
        return [{1: hips or rest[1].copy(), 2: left or rest[2].copy(), 3: rest[3].copy()}]

    def test_partners_are_the_other_sides_joint_or_the_joint_itself(self):
        self.assertEqual(gltf_clips.partner("thigh_l"), "thigh_r")
        self.assertEqual(gltf_clips.partner("B-hand.R"), "B-hand.L")
        self.assertEqual(gltf_clips.partner("pelvis"), "pelvis")

    def test_a_leg_swung_forward_is_the_other_leg_swung_forward(self):
        swung = self.nodes.rest[2] @ Matrix.Rotation(-0.4, 4, "X")
        (mirrored,) = gltf_clips.mirror(self._posed(left=swung), self.nodes)

        worlds = self.nodes.worlds(mirrored)
        right_foot, left_foot = worlds[5].translation, worlds[4].translation
        posed_foot = self.nodes.worlds(self._posed(left=swung)[0])[4].translation
        self.assertLess((right_foot - Vector((-posed_foot.x, posed_foot.y, posed_foot.z))).length, 1e-6)
        self.assertGreater(right_foot.z, 0.1)  # forward
        self.assertLess((left_foot - self.nodes.rest_world[4].translation).length, 1e-6)  # the other at rest

    def test_a_turn_about_up_turns_the_other_way_and_a_step_left_goes_right(self):
        hips = Matrix.Translation((0.2, 1.0, 0.0)) @ Matrix.Rotation(0.3, 4, "Y")
        (mirrored,) = gltf_clips.mirror(self._posed(hips=hips), self.nodes)

        location, rotation, _ = mirrored[1].decompose()
        self.assertLess((location - Vector((-0.2, 1.0, 0.0))).length, 1e-6)
        self.assertGreater(abs(rotation.dot(Quaternion((0, 1, 0), -0.3))), 1 - 1e-9)


class RepairAndResampleTests(unittest.TestCase):
    """repair and resample on a joint's turns made by hand, a sample a tenth of a second."""

    def setUp(self):
        self.nodes = gltf_clips.Nodes({"nodes": [{"name": "root", "children": [1]}, {"name": "foot"}]})

    @staticmethod
    def _cycle(angles):
        return [{1: Matrix.Rotation(math.radians(angle), 4, "X")} for angle in angles]

    @staticmethod
    def _angles(sampled):
        return [round(math.degrees(locals_[1].to_euler().x), 3) for locals_ in sampled]

    def test_a_spike_and_a_stretch_held_too_far_are_mended_from_the_good_turns_either_side(self):
        sampled = self._cycle([0, 10, 20, 170, 30, 40, 50, 120, 120, 80])

        (mended,) = gltf_clips.repair(sampled, self.nodes, [1], 0.1, 600, 100)

        # 20 to 170 and back, and 50 to 120, turn faster than 600 degrees a second; 170 and 120 lie past 100.
        self.assertEqual(mended, [2, 3, 4, 6, 7, 8])
        self.assertEqual(self._angles(sampled), [0, 10, 17.5, 25, 32.5, 40, 50, 60, 70, 80])

    def test_nothing_broken_stays_as_it_is(self):
        sampled = self._cycle([0, 10, 20, 30])

        self.assertEqual(gltf_clips.repair(sampled, self.nodes, [1], 0.1, 600, 100), [[]])
        self.assertEqual(self._angles(sampled), [0, 10, 20, 30])

    def test_resampling_spreads_keys_evenly_between_the_samples(self):
        sampled = self._cycle([0, 40, 80])
        sampled[2][1].translation = Vector((0, 2, 0))

        out = gltf_clips.resample(sampled, 4)

        self.assertEqual(self._angles(out), [0, 20, 40, 60, 80])
        self.assertAlmostEqual(out[3][1].translation.y, 1.0, places=6)


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
