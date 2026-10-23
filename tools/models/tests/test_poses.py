"""Tests for itera_models.poses, and the outline check of a reference drawn in a pose of its own."""

import math
import os
import tempfile
import unittest

import blender_test_utils as utils
import bpy
import numpy as np
from itera_models import poses, rig, silhouette
from mathutils import Matrix, Vector

# A small humanoid rig resting in a T-pose, its front towards -Y: a spine, and each arm out along X in three bones,
# with a finger under each hand that is no part of the arm.
_T_POSE = [
    ("spine", (0, 0, 1.0), (0, 0, 1.5), 0.0, None),
    ("upperarm_l", (0.2, 0, 1.45), (0.5, 0, 1.45), 0.0, "spine"),
    ("lowerarm_l", (0.5, 0, 1.45), (0.8, 0, 1.45), 0.0, "upperarm_l"),
    ("hand_l", (0.8, 0, 1.45), (0.9, 0, 1.45), 0.0, "lowerarm_l"),
    ("index_01_l", (0.9, 0, 1.45), (0.95, 0, 1.45), 0.0, "hand_l"),
    ("upperarm_r", (-0.2, 0, 1.45), (-0.5, 0, 1.45), 0.0, "spine"),
    ("lowerarm_r", (-0.5, 0, 1.45), (-0.8, 0, 1.45), 0.0, "upperarm_r"),
    ("hand_r", (-0.8, 0, 1.45), (-0.9, 0, 1.45), 0.0, "lowerarm_r"),
]


def _world_way(armature, name):
    """The posed bone's way from its head to its tail, in the world."""
    bone = armature.pose.bones[name]
    head = armature.matrix_world @ bone.head
    tail = armature.matrix_world @ bone.tail
    return (tail - head).normalized()


def _turned(armature, name, vector):
    """`vector` (world) as the posed bone turns it from its rest."""
    bone = armature.pose.bones[name]
    turn = (bone.matrix @ bone.bone.matrix_local.inverted()).to_3x3()
    return (armature.matrix_world.to_3x3() @ turn @ armature.matrix_world.to_3x3().inverted() @ vector).normalized()


def _degrees(a, b):
    return math.degrees(a.angle(b))


class ArmChainsTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()

    def test_each_rigs_arms_are_found_by_their_names_and_fingers_left_out(self):
        rigs = {
            "game": ["upperarm_l", "lowerarm_l", "hand_l", "index_01_l"],
            "rigify": ["DEF-upper_arm.L", "DEF-forearm.L", "DEF-hand.L", "DEF-f_index.01.L"],
            "mixamo": ["mixamorig:LeftArm", "mixamorig:LeftForeArm", "mixamorig:LeftHand", "mixamorig:LeftHandIndex1"],
        }
        for style, names in rigs.items():
            bones = [("spine", (0, 0, 1), (0, 0, 1.5), 0.0, None)]
            parent = "spine"
            for index, name in enumerate(names):
                bones.append((name, (0.2 + 0.3 * index, 0, 1.45), (0.5 + 0.3 * index, 0, 1.45), 0.0, parent))
                parent = name
            armature = utils.make_armature(style, bones)
            with self.subTest(style):
                self.assertEqual(poses.arm_chains(armature), [tuple(names[:3])])

    def test_a_rig_with_no_arms_is_refused(self):
        armature = utils.make_armature("Stick", [("spine", (0, 0, 0), (0, 0, 1), 0.0, None)])
        with self.assertRaises(ValueError):
            poses.arm_chains(armature)


class HeldTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self.armature = utils.make_armature("Rig", _T_POSE)

    def test_idle_hangs_each_arm_down_a_little_out_with_the_arm_below_it_straight(self):
        with poses.held(self.armature, "idle"):
            left = _world_way(self.armature, "upperarm_l")
            right = _world_way(self.armature, "upperarm_r")
            forearm = _world_way(self.armature, "lowerarm_l")
            hand = self.armature.matrix_world @ self.armature.pose.bones["hand_l"].head

        down = Vector((0, 0, -1))
        self.assertAlmostEqual(_degrees(left, down), poses.IDLE_SPREAD, delta=0.5)
        self.assertAlmostEqual(_degrees(right, down), poses.IDLE_SPREAD, delta=0.5)
        self.assertGreater(left.x, 0)
        self.assertLess(right.x, 0)
        self.assertLess(_degrees(forearm, left), 0.5)
        self.assertLess(hand.z, 1.0)

    def test_the_anatomical_position_hangs_the_arms_close_with_the_palms_forward(self):
        with poses.held(self.armature, "anatomical"):
            way = _world_way(self.armature, "upperarm_l")
            palm = _turned(self.armature, "hand_l", Vector((0, 0, -1)))
        with poses.held(self.armature, "idle"):
            idle_palm = _turned(self.armature, "hand_l", Vector((0, 0, -1)))

        self.assertAlmostEqual(_degrees(way, Vector((0, 0, -1))), poses.ANATOMICAL_SPREAD, delta=0.5)
        # The palm faced down at rest: forward (-Y) in the anatomical position, towards the body (-X) when idle.
        self.assertLess(_degrees(palm, Vector((0, -1, 0))), 7)
        self.assertLess(_degrees(idle_palm, Vector((-1, 0, 0))), 13)

    def test_a_rig_resting_with_its_arms_down_is_held_level_in_a_t_pose(self):
        utils.reset_scene()
        hanging = [
            ("spine", (0, 0, 1.0), (0, 0, 1.5), 0.0, None),
            ("upperarm_l", (0.2, 0, 1.45), (0.4, 0, 1.25), 0.0, "spine"),
            ("lowerarm_l", (0.4, 0, 1.25), (0.6, 0, 1.05), 0.0, "upperarm_l"),
        ]
        armature = utils.make_armature("A", hanging, location=(3, 0, 0))
        with poses.held(armature, "t-pose"):
            upper = _world_way(armature, "upperarm_l")
            lower = _world_way(armature, "lowerarm_l")

        self.assertLess(_degrees(upper, Vector((1, 0, 0))), 0.5)
        self.assertLess(_degrees(lower, Vector((1, 0, 0))), 0.5)

    def test_the_pose_and_the_action_are_put_back_and_rest_changes_nothing(self):
        armature = self.armature
        armature.pose.bones["upperarm_l"].rotation_mode = "XYZ"
        armature.pose.bones["upperarm_l"].rotation_euler = (0.3, 0, 0)
        action = bpy.data.actions.new("Wave")
        armature.animation_data_create().action = action
        before = armature.pose.bones["upperarm_l"].matrix_basis.copy()

        with poses.held(armature, "rest"):
            resting = armature.pose.bones["upperarm_l"].matrix_basis.copy()
        with poses.held(armature, "idle"):
            pass

        self.assertEqual(resting, before)
        self.assertEqual(armature.pose.bones["upperarm_l"].matrix_basis, before)
        self.assertIs(armature.animation_data.action, action)

    def test_an_unknown_pose_is_refused(self):
        with self.assertRaises(ValueError), poses.held(self.armature, "kneeling"):
            pass


class PosedReferenceTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()

    def test_a_reference_drawn_idle_matches_the_model_held_idle_and_not_at_rest(self):
        armature = utils.make_armature("Rig", _T_POSE)
        parts = {}
        for name, location, scale in (
            ("Body", (0, 0, 1.0), (0.4, 0.3, 1.0)),
            ("ArmL", (0.5, 0, 1.45), (0.6, 0.12, 0.12)),
            ("ArmR", (-0.5, 0, 1.45), (0.6, 0.12, 0.12)),
        ):
            bpy.ops.mesh.primitive_cube_add(size=1.0, location=location, scale=scale)
            bpy.context.object.name = name
            bpy.ops.object.transform_apply(scale=True)
            parts[bpy.context.object] = {"Body": "spine", "ArmL": "upperarm_l", "ArmR": "upperarm_r"}[name]
        model = rig.rigid_skin(parts, armature, "Model")
        with poses.held(armature, "idle"):
            drawn = silhouette.mask([model], "front", 256)
        folder = tempfile.mkdtemp()
        path = os.path.join(folder, "idle.png")
        pixels = np.zeros((256, 256, 4), dtype=np.float32)
        pixels[..., 3] = 1
        pixels[drawn] = (1, 1, 1, 1)
        utils.save_png(path, pixels)

        rest = silhouette.compare([model], {"front": path})["front"]
        idle = silhouette.compare([model], {"front": path}, poses={"front": "idle"})["front"]

        self.assertGreater(idle, 0.97)
        self.assertLess(rest, idle - 0.3)
        self.assertEqual(armature.pose.bones["upperarm_l"].matrix_basis, Matrix.Identity(4))


if __name__ == "__main__":
    unittest.main()
