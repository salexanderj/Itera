"""Tests for itera_models.motion: loops that close, and feet that stay put on the ground."""

import math
import types
import unittest

import blender_test_utils as utils
import bpy
from bpy_extras import anim_utils
from mathutils import Vector

import run_recipe
from itera_models import motion
from itera_models.context import Context

# A rig 1.8 m tall: a root at the ground, a leg whose tail is the foot's sole, and a still upper body.
BONES = [
    ("root", (0, 0, 0), (0, 0, 0.2), 0.0, None),
    ("leg", (0, 0, 0.5), (0, 0, 0), 0.0, "root"),
    ("body", (0, 0, 0.5), (0, 0, 1.8), 0.0, "root"),
]
FPS = 24
# The leg's tail, frame by frame, as (y, z) in metres: back along the ground from frame 1 to 13 (0.7 m in 0.5 s, a walk
# in place at 1.4 m/s), straight up, forward through the air, straight down, and at frame 25 where it began.
STEP = {1: (-0.35, 0.0), 13: (0.35, 0.0), 14: (0.35, 0.1), 19: (0.0, 0.2), 24: (-0.35, 0.1), 25: (-0.35, 0.0)}
WALK_SPEED = 1.4


def key_tail(armature, clip: str, places: dict) -> bpy.types.Action:
    """A clip that moves the leg's tail through `places` (frame: (y, z)), with straight lines between its keys."""
    action = bpy.data.actions.new(clip)
    action.use_fake_user = True
    slot = action.slots.new(id_type="OBJECT", name=armature.name)
    armature.animation_data_create()
    armature.animation_data.action = action
    armature.animation_data.action_slot = slot
    bone = armature.data.bones["leg"]
    pose_bone = armature.pose.bones["leg"]
    to_bone = bone.matrix_local.to_3x3().inverted()
    for frame, (y, z) in places.items():
        pose_bone.location = to_bone @ Vector((0.0, y, z))
        pose_bone.keyframe_insert("location", frame=frame)
    for curve in anim_utils.action_get_channelbag_for_slot(action, slot).fcurves:
        for key in curve.keyframe_points:
            key.interpolation = "LINEAR"
    pose_bone.location = (0.0, 0.0, 0.0)
    return action


class MotionCheckTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        bpy.context.scene.render.fps = FPS
        self.armature = utils.make_armature("Rig", BONES)

    def test_the_rig_height_is_from_its_lowest_bone_end_to_its_highest(self):
        self.assertAlmostEqual(motion.rest_height(self.armature), 1.8, places=4)

    def test_a_foot_moving_back_at_the_walks_speed_does_not_slide_and_its_loop_closes(self):
        key_tail(self.armature, "Walk", STEP)

        result = motion.check(self.armature, ["Walk"], ["leg"], {"Walk": WALK_SPEED})

        self.assertEqual(result["problems"], [])
        walk = result["clips"]["Walk"]
        self.assertEqual(walk["frames"], 25)
        self.assertAlmostEqual(walk["seconds"], 1.0, places=4)
        self.assertLess(walk["feet"]["leg"]["slide"], 1e-3)
        self.assertEqual(walk["feet"]["leg"]["frames_on_ground"], 14)  # frames 1 to 13, and 25
        self.assertLess(walk["loop"]["gap"], 1e-4)

    def test_the_same_foot_in_a_clip_said_to_stand_still_slides_its_whole_step(self):
        key_tail(self.armature, "Walk", STEP)

        result = motion.check(self.armature, feet=["leg"], planted={"Walk": 0.0})

        self.assertAlmostEqual(result["clips"]["Walk"]["feet"]["leg"]["slide"], 0.7, places=3)
        self.assertEqual(len(result["problems"]), 1)
        self.assertIn("Walk: leg slides 70.0 cm", result["problems"][0])

    def test_a_loop_whose_last_frame_is_not_its_first_pose_is_a_problem(self):
        key_tail(self.armature, "Reach", {1: (0.0, 0.0), 9: (0.2, 0.0)})

        result = motion.check(self.armature, ["Reach"])

        self.assertAlmostEqual(result["clips"]["Reach"]["loop"]["gap"], 0.2, places=3)
        self.assertTrue(any(problem.startswith("Reach: its last frame is 20.0 cm") for problem in result["problems"]))

    def test_the_check_puts_back_the_action_and_the_frame(self):
        key_tail(self.armature, "Walk", STEP)
        other = key_tail(self.armature, "Other", {1: (0.0, 0.0), 5: (0.0, 0.1)})
        bpy.context.scene.frame_set(3)

        motion.check(self.armature, ["Walk"])

        self.assertIs(self.armature.animation_data.action, other)
        self.assertEqual(bpy.context.scene.frame_current, 3)

    def test_names_that_are_not_there_are_problems(self):
        problems = motion.check(self.armature, ["Swim"], ["paw"], {"Swim": 1.0})["problems"]

        no_feet = motion.check(self.armature, planted={"Walk": 0})["problems"]
        no_clips = motion.check(self.armature, feet=["leg"])["problems"]

        self.assertIn("Rig has no bone paw, which FEET names", problems)
        self.assertIn("Swim: there is no clip of that name", problems)
        self.assertIn("PLANTED names clips, but FEET names no foot to check in them", no_feet)
        self.assertIn("FEET names feet, but PLANTED names no clip to check them in", no_clips)

    def test_a_recipes_loops_feet_and_planted_reach_the_check(self):
        key_tail(self.armature, "Walk", STEP)
        context = Context("Rig", ".", "Rig.glb", ".", ".")
        recipe = types.SimpleNamespace(LOOPS=["Walk"], FEET=["leg"], PLANTED={"Walk": 0.0})

        result = run_recipe.check_motion(recipe, context)

        self.assertIn("Walk", result["clips"])
        self.assertEqual(len(result["problems"]), 1)
        self.assertIsNone(run_recipe.check_motion(types.SimpleNamespace(), context))


class LoopClosureTests(unittest.TestCase):
    """loop_closure on paths of one point, made by hand."""

    @staticmethod
    def _path(heights):
        return [[(0.0, 0.0, height)] for height in heights]

    def test_a_smooth_cycle_closes_and_runs_on_across_its_seam(self):
        steps = 24
        closure = motion.loop_closure(self._path([math.sin(2 * math.pi * k / steps) for k in range(steps + 1)]))

        self.assertLess(closure["gap"], 1e-9)
        self.assertLessEqual(closure["seam"], 2 * closure["inside"])

    def test_a_cycle_that_turns_back_at_its_seam_jolts(self):
        steps = 24
        closure = motion.loop_closure(self._path([math.sin(math.pi * k / steps) for k in range(steps + 1)]))

        self.assertLess(closure["gap"], 1e-9)
        self.assertGreater(closure["seam"], 10 * closure["inside"])

    def test_the_roots_travel_along_the_ground_is_not_a_gap(self):
        frames = [[(0.0, -0.1 * k, 0.0), (0.0, -0.1 * k, 1.0 if k % 2 else 0.0)] for k in range(5)]

        self.assertAlmostEqual(motion.loop_closure(frames, root=0)["gap"], 0.0)
        self.assertAlmostEqual(motion.loop_closure(frames)["gap"], 0.4)


class FootSlideTests(unittest.TestCase):
    """foot_slide on paths made by hand, at 10 frames a second."""

    def test_only_moves_between_two_frames_on_the_ground_count(self):
        path = [(0.0, 0.0, 0.0), (0.0, 0.05, 0.0), (0.0, 0.5, 0.3), (0.0, 0.0, 0.0), (0.0, 0.02, 0.0)]

        found = motion.foot_slide(path, 0.1, 0.0, 0.02)

        self.assertAlmostEqual(found["slide"], 0.05)  # the longer of the two stretches; the swing does not count
        self.assertEqual(found["frames"], 4)

    def test_a_foot_moving_back_with_the_ground_does_not_slide(self):
        path = [(0.0, 0.1 * k, 0.0) for k in range(6)]

        self.assertAlmostEqual(motion.foot_slide(path, 0.1, 1.0, 0.02)["slide"], 0.0)
        self.assertAlmostEqual(motion.foot_slide(path, 0.1, 0.0, 0.02)["slide"], 0.5)


if __name__ == "__main__":
    unittest.main()
