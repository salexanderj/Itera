"""Tests for itera_models.stress."""

import os
import tempfile
import unittest

import bpy
from itera_models import stress
from test_sockets import TwoTubes

LIMITS = {"b1": [("X", -30), ("X", 30)]}


class StressTests(unittest.TestCase):
    def test_a_socket_both_sides_weigh_alike_stays_shut_as_its_joint_turns(self):
        tubes = TwoTubes().make()
        result = stress.check(tubes.armature, [tubes.lower, tubes.upper], LIMITS)
        self.assertEqual([r["pose"] for r in result["poses"]], ["b1 X-30", "b1 X+30"])
        for pose in result["poses"]:
            self.assertLess(pose["gap"], 1e-6, pose)
            self.assertGreater(pose["stretch"], 1.0, pose)
            self.assertLess(abs(pose["volume"]), 0.08, pose)

    def test_a_socket_whose_sides_weigh_different_bones_opens(self):
        tubes = TwoTubes().make(socket_on_b1_alone=True)
        result = stress.check(tubes.armature, [tubes.lower, tubes.upper], LIMITS)
        self.assertTrue(all(pose["gap"] > 0.01 for pose in result["poses"]), result["poses"])
        self.assertTrue(any("a socket opens" in problem for problem in result["problems"]), result["problems"])

    def test_a_turn_past_what_the_skin_bears_is_a_problem(self):
        tubes = TwoTubes().make()
        result = stress.check(tubes.armature, [tubes.lower, tubes.upper], {"b1": [("X", 150)]})
        self.assertTrue(result["problems"], result["poses"])

    def test_the_armature_and_the_scene_are_put_back(self):
        tubes = TwoTubes().make()
        bone = tubes.armature.pose.bones["b1"]
        bone.rotation_mode = "XYZ"
        bone.rotation_euler = (0.1, 0.0, 0.0)
        bpy.context.scene.frame_set(7)
        stress.check(tubes.armature, [tubes.lower, tubes.upper], LIMITS)
        self.assertNotIn(stress.ACTION, bpy.data.actions)
        self.assertEqual(bone.rotation_mode, "XYZ")
        self.assertAlmostEqual(bone.rotation_euler[0], 0.1)
        self.assertEqual(bpy.context.scene.frame_current, 7)

    def test_the_sheet_numbers_the_poses_it_draws(self):
        tubes = TwoTubes().make()
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "stress.png")
            result = stress.check(tubes.armature, [tubes.lower, tubes.upper], LIMITS, sheet=path, worst=1)
            self.assertTrue(os.path.exists(path))
        drawn = [pose for pose in result["poses"] if "sheet" in pose]
        self.assertEqual(len(drawn), 1)
        self.assertEqual(drawn[0]["sheet"], 1)

    def test_a_limit_on_a_bone_the_rig_lacks_is_refused(self):
        tubes = TwoTubes().make()
        with self.assertRaises(ValueError):
            stress.check(tubes.armature, [tubes.lower], {"b9": [("X", 10)]})


if __name__ == "__main__":
    unittest.main()
