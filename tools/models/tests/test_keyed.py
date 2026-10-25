"""Tests for itera_models.keyed (clips keyed by poses, posed by IK) and itera_models.strips (their pictures)."""

import os
import tempfile
import unittest

import blender_test_utils as utils
import bpy
from itera_models import clips, keyed, strips
from itera_models.keyed import Body, Foot, Hand, Key, Turn, to_blender
from mathutils import Vector


def _side(side: str, sign: float) -> list:
    """One side's collarbone, arm, hand, fingers, thumb, and leg, its arm out along x in a T-pose, its elbow and knee
    bent a little so IK knows which way they go."""
    x = sign
    bones = [
        (f"clavicle_{side}", (0.0, 0.0, 1.42), (0.15 * x, 0.0, 1.42), 0.0, "spine_03"),
        (f"upperarm_{side}", (0.15 * x, 0.0, 1.42), (0.43 * x, 0.02, 1.42), 0.0, f"clavicle_{side}"),
        (f"lowerarm_{side}", (0.43 * x, 0.02, 1.42), (0.71 * x, 0.0, 1.42), 0.0, f"upperarm_{side}"),
        (f"hand_{side}", (0.71 * x, 0.0, 1.42), (0.79 * x, 0.0, 1.42), 0.0, f"lowerarm_{side}"),
    ]
    for finger, y in (("index", -0.03), ("middle", -0.01), ("ring", 0.01), ("pinky", 0.03)):
        bones += [
            (f"{finger}_01_{side}", (0.79 * x, y, 1.42), (0.82 * x, y, 1.42), 0.0, f"hand_{side}"),
            (f"{finger}_02_{side}", (0.82 * x, y, 1.42), (0.84 * x, y, 1.42), 0.0, f"{finger}_01_{side}"),
            (f"{finger}_03_{side}", (0.84 * x, y, 1.42), (0.86 * x, y, 1.42), 0.0, f"{finger}_02_{side}"),
        ]
    bones += [
        (f"thumb_01_{side}", (0.73 * x, -0.03, 1.41), (0.75 * x, -0.05, 1.40), 0.0, f"hand_{side}"),
        (f"thumb_02_{side}", (0.75 * x, -0.05, 1.40), (0.77 * x, -0.06, 1.39), 0.0, f"thumb_01_{side}"),
        (f"thumb_03_{side}", (0.77 * x, -0.06, 1.39), (0.79 * x, -0.07, 1.38), 0.0, f"thumb_02_{side}"),
        (f"thigh_{side}", (0.1 * x, 0.0, 0.9), (0.1 * x, -0.02, 0.5), 0.0, "pelvis"),
        (f"calf_{side}", (0.1 * x, -0.02, 0.5), (0.1 * x, 0.0, 0.1), 0.0, f"thigh_{side}"),
        (f"foot_{side}", (0.1 * x, 0.0, 0.1), (0.1 * x, -0.15, 0.02), 0.0, f"calf_{side}"),
        (f"ball_{side}", (0.1 * x, -0.15, 0.02), (0.1 * x, -0.22, 0.02), 0.0, f"foot_{side}"),
    ]
    return bones


#: A small rig with the mannequin's joints (keyed.MANNEQUIN), its front towards -Y, standing in a T-pose.
_BONES = [
    ("pelvis", (0.0, 0.0, 0.9), (0.0, 0.0, 1.0), 0.0, None),
    ("spine_01", (0.0, 0.0, 1.0), (0.0, 0.0, 1.15), 0.0, "pelvis"),
    ("spine_02", (0.0, 0.0, 1.15), (0.0, 0.0, 1.3), 0.0, "spine_01"),
    ("spine_03", (0.0, 0.0, 1.3), (0.0, 0.0, 1.45), 0.0, "spine_02"),
    ("neck_01", (0.0, 0.0, 1.45), (0.0, 0.0, 1.55), 0.0, "spine_03"),
    ("Head", (0.0, 0.0, 1.55), (0.0, 0.0, 1.75), 0.0, "neck_01"),
    *_side("l", 1.0),
    *_side("r", -1.0),
]


def _rig():
    """The rig, with a one-frame clip `Stand` at its rest to stand in, and a box to draw."""
    utils.reset_scene()
    armature = utils.make_armature("Rig", _BONES)
    clips.new_clip(armature, "Stand", 0, frame_end=1)
    clips.key_pose(armature, 0, {"pelvis": {"rotation": (1.0, 0.0, 0.0, 0.0)}})
    bpy.ops.mesh.primitive_cube_add(size=0.4, location=(0.0, 0.0, 1.0))
    box = bpy.context.object
    box.parent = armature
    return armature, [box]


def _near(first, second, within: float) -> bool:
    return (Vector(first) - Vector(second)).length <= within


STAND = Key(0.0, Body(), foot_l=Foot(), foot_r=Foot())


class TurnsAndMirrorsTests(unittest.TestCase):
    def test_a_bend_turns_up_to_the_front_a_lean_to_the_left_and_a_twist_the_front_to_the_left(self):
        up, front, left = to_blender((0, 1, 0)), to_blender((0, 0, 1)), to_blender((1, 0, 0))
        self.assertTrue(_near(Turn(bend=90).matrix() @ up, front, 1e-6))
        self.assertTrue(_near(Turn(lean=90).matrix() @ up, left, 1e-6))
        self.assertTrue(_near(Turn(twist=90).matrix() @ front, left, 1e-6))

    def test_a_mirrored_key_swaps_its_sides_and_reverses_the_left_axis_and_the_turns_about_up_and_front(self):
        key = Key(
            0.5,
            Body(hips=(0.1, -0.2, 0.3), spine=Turn(10, 5, 20)),
            hand_r=Hand((-0.3, 1.2, 0.3), elbow=(-1.0, 0.0, 0.0), shoulder=Turn(lean=10)),
            foot_l=Foot(by=(0.05, 0.1, 0.2), turn=Turn(bend=30, twist=10)),
            ease="in",
        )

        mirrored = key.mirrored()

        self.assertEqual((0.5, "in"), (mirrored.seconds, mirrored.ease))
        self.assertEqual(((-0.1, -0.2, 0.3), Turn(10, -5, -20)), (mirrored.body.hips, mirrored.body.spine))
        self.assertIsNone(mirrored.hand_r)
        self.assertEqual((0.3, 1.2, 0.3), mirrored.hand_l.at)
        self.assertEqual((1.0, 0.0, 0.0), mirrored.hand_l.elbow)
        self.assertEqual(Turn(lean=-10), mirrored.hand_l.shoulder)
        self.assertIsNone(mirrored.foot_l)
        self.assertEqual(((-0.05, 0.1, 0.2), Turn(bend=30, twist=-10)), (mirrored.foot_r.by, mirrored.foot_r.turn))


class KeyerTests(unittest.TestCase):
    def setUp(self):
        self.armature, self.meshes = _rig()
        self.keyer = keyed.Keyer(self.armature, "Stand", fps=30)

    def test_a_hand_reaches_its_place_at_its_key_and_the_clip_starts_and_ends_at_the_stand(self):
        keys = [STAND, Key(0.5, hand_r=Hand((-0.45, 1.1, 0.3))), Key(1.0, hand_r=Hand())]

        made = self.keyer.clip("Reach", keys)
        report, problems = keyed.check(self.armature, self.keyer, made)

        self.assertEqual([], problems)
        self.assertTrue(_near(keyed.place_at(self.armature, "hand_r", 0.5, 30), (-0.45, 1.1, 0.3), 0.01))
        self.assertLess(max(report["start_off"], report["end_off"]), 1e-3)
        self.assertEqual(31, report["frames"])

    def test_a_loop_ends_in_the_pose_it_starts_in_off_the_stand_and_one_that_does_not_is_a_problem(self):
        raised = Hand((-0.45, 1.1, 0.3))
        start = Key(0.0, Body(hips=(0.0, -0.1, 0.0)), hand_l=Hand(), hand_r=raised, foot_l=Foot(), foot_r=Foot())
        loop = [start, Key(0.5, hand_r=Hand((-0.45, 1.3, 0.3))), Key(1.0, Body(hips=(0.0, -0.1, 0.0)), hand_r=raised)]
        open_ = [start, Key(0.5, hand_r=Hand((-0.45, 1.3, 0.3))), Key(1.0, Body(), hand_r=Hand())]

        looped, loop_problems = keyed.check(self.armature, self.keyer, self.keyer.clip("Loop", loop), loop=True)
        _, open_problems = keyed.check(self.armature, self.keyer, self.keyer.clip("Open", open_), loop=True)
        _, stand_problems = keyed.check(self.armature, self.keyer, self.keyer.clip("Not a loop", loop))

        self.assertEqual([], loop_problems)
        self.assertLess(looped["seam"], 1e-3)
        self.assertGreater(looped["start_off"], 0.05)
        self.assertIn("a loop that ends", "\n".join(open_problems))
        self.assertIn("from the stand", "\n".join(stand_problems))

    def test_a_hand_reaching_from_its_shoulder_goes_that_way_its_share_of_the_arms_length(self):
        keys = [STAND, Key(0.5, hand_l=Hand(reach=(0.0, 0.0, 1.0), extend=0.9)), Key(1.0, hand_l=Hand())]

        made = self.keyer.clip("Forward", keys)
        _, problems = keyed.check(self.armature, self.keyer, made)

        self.assertEqual([], problems)
        shoulder = keyed.place_at(self.armature, "upperarm_l", 0.5, 30)
        wrist = keyed.place_at(self.armature, "hand_l", 0.5, 30)
        way = Vector(wrist) - Vector(shoulder)
        self.assertAlmostEqual(0.9 * (0.28 + 0.28), way.length, delta=0.01)
        self.assertGreater(way.normalized().z, 0.99)

    def test_a_part_a_key_leaves_out_moves_between_the_keys_that_name_it(self):
        keys = [STAND, Key(0.5, hand_r=Hand(by=(0.0, 0.1, 0.0))), Key(1.0, Body(hips=(0.0, -0.2, 0.0)))]

        self.keyer.clip("Between", keys)
        hips = keyed.place_at(self.armature, "pelvis", 0.5, 30)

        self.assertLess(hips[1], 0.9 - 0.03)
        self.assertGreater(hips[1], 0.9 - 0.17)

    def test_a_key_between_frames_or_out_of_time_is_refused(self):
        with self.assertRaises(ValueError):
            self.keyer.clip("Between frames", [STAND, Key(0.25, hand_r=Hand())])
        with self.assertRaises(ValueError):
            self.keyer.clip("Out of time", [STAND, Key(0.5, hand_r=Hand()), Key(0.3, hand_r=Hand())])

    def test_the_checks_find_what_goes_under_the_ground_a_foot_that_slides_and_a_place_out_of_reach(self):
        keys = [
            STAND,
            Key(0.5, Body(hips=(0.0, -0.95, 0.0)), hand_r=Hand((-2.0, 1.4, 0.0)), foot_l=Foot(by=(0.0, 0.0, 0.2))),
            Key(1.0, Body(), hand_r=Hand(), foot_l=Foot()),
        ]

        made = self.keyer.clip("Wrong", keys)
        _, problems = keyed.check(self.armature, self.keyer, made, planted=(("foot_l", 0.0, 1.0),))

        text = "\n".join(problems)
        self.assertIn("pelvis goes", text)
        self.assertIn("foot_l slides", text)
        self.assertIn("hand_r falls", text)

    def test_the_fingers_keep_the_stands_pose(self):
        keys = [STAND, Key(0.5, hand_r=Hand((-0.45, 1.1, 0.3), knuckles=(0.0, 0.0, 1.0))), Key(1.0, hand_r=Hand())]

        self.keyer.clip("Fingers", keys)
        bpy.context.scene.frame_set(15)

        for name in ("index_02_r", "thumb_03_r", "pinky_01_l"):
            basis = self.armature.pose.bones[name].matrix_basis
            self.assertTrue(_near(basis.to_quaternion(), self.keyer.stand_basis[name].to_quaternion(), 1e-5), name)

    def test_the_sheet_has_a_frame_for_each_time_from_each_view(self):
        self.keyer.clip("Sheet", [STAND, Key(0.5, hand_r=Hand(by=(0.0, 0.1, 0.0)))])

        with tempfile.TemporaryDirectory() as folder:
            path = strips.sheet(os.path.join(folder, "sheet.png"), self.armature, self.meshes, [0, 15], 30, (40, 60))
            pixels = utils.read_png(str(path))

        self.assertEqual((2 * 60, 2 * 40), pixels.shape[:2])


if __name__ == "__main__":
    unittest.main()
