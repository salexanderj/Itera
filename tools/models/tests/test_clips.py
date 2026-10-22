"""Tests for itera_models.clips."""

import math
import os
import tempfile
import unittest

import blender_test_utils as utils
import bpy
from itera_models import clips, rig
from mathutils import Euler, Matrix, Quaternion, Vector

BONES = [
    ("b0", (0, 0, 0), (0, 0, 1), 0.0, None),
    ("b1", (0, 0, 1), (0, 0, 2), 0.0, "b0"),
]


def _rest_delta(obj, bone_name) -> Matrix:
    """World rotation of a posed bone relative to its own rest rotation."""
    posed = (obj.matrix_world @ obj.pose.bones[bone_name].matrix).to_3x3().normalized()
    rest = (obj.matrix_world @ obj.data.bones[bone_name].matrix_local).to_3x3().normalized()
    return posed @ rest.inverted()


def _rotations_match(first: Matrix, second: Matrix, tolerance: float = 1e-4) -> bool:
    return all(abs(a - b) < tolerance for row_a, row_b in zip(first, second) for a, b in zip(row_a, row_b))


def _tail_world(obj, bone_name) -> Vector:
    return obj.matrix_world @ obj.pose.bones[bone_name].tail


def _head_world(obj, bone_name) -> Vector:
    return obj.matrix_world @ obj.pose.bones[bone_name].head


class NewClipTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self._tmp = tempfile.TemporaryDirectory()
        self.armature = utils.make_armature("Rig", BONES)

    def tearDown(self):
        self._tmp.cleanup()

    def _skinned_box(self):
        bpy.ops.mesh.primitive_cube_add(size=0.5, location=(0, 0, 1.0))
        box = bpy.context.object
        return rig.rigid_skin({box: "b0"}, self.armature)

    def test_assigns_an_action_with_a_slot_and_a_frame_range(self):
        action = clips.new_clip(self.armature, "Walk", 3, frame_end=20, fps=30)
        animation = self.armature.animation_data
        self.assertIs(animation.action, action)
        self.assertIsNotNone(animation.action_slot)
        self.assertEqual(animation.action_slot.target_id_type, "OBJECT")
        self.assertTrue(action.use_frame_range)
        self.assertEqual((action.frame_start, action.frame_end), (3, 20))
        self.assertTrue(action.use_fake_user)
        self.assertEqual(bpy.context.scene.render.fps, 30)

    def test_refuses_a_name_in_use_and_a_backwards_range(self):
        clips.new_clip(self.armature, "Walk", frame_end=10)
        with self.assertRaises(ValueError):
            clips.new_clip(self.armature, "Walk", frame_end=10)
        with self.assertRaises(ValueError):
            clips.new_clip(self.armature, "Other", 5, frame_end=4)

    def test_key_pose_keys_only_the_channels_given(self):
        clips.new_clip(self.armature, "Wave", frame_end=10)
        clips.key_pose(self.armature, 1, {"b1": {"rotation": (1, 0, 0, 0)}})
        clips.key_pose(
            self.armature, 10, {"b1": {"rotation": Quaternion((0, 1, 0), 1.0)}, "b0": {"location": (0, 0.2, 0)}}
        )
        keys = clips.clip_keys(self.armature)
        self.assertEqual(
            keys,
            {
                'pose.bones["b1"].rotation_quaternion[0]': 2,
                'pose.bones["b1"].rotation_quaternion[1]': 2,
                'pose.bones["b1"].rotation_quaternion[2]': 2,
                'pose.bones["b1"].rotation_quaternion[3]': 2,
                'pose.bones["b0"].location[0]': 1,
                'pose.bones["b0"].location[1]': 1,
                'pose.bones["b0"].location[2]': 1,
            },
        )

    def test_key_pose_needs_an_action_and_known_bones(self):
        with self.assertRaises(ValueError):
            clips.key_pose(self.armature, 1, {"b1": {"rotation": (1, 0, 0, 0)}})
        clips.new_clip(self.armature, "Wave", frame_end=10)
        with self.assertRaises(ValueError):
            clips.key_pose(self.armature, 1, {"nope": {"rotation": (1, 0, 0, 0)}})
        with self.assertRaises(ValueError):
            clips.key_pose(self.armature, 1, {"b1": {"twist": 1.0}})

    def test_the_exported_gltf_has_an_animation_named_as_the_action_keyed_on_the_bones(self):
        box = self._skinned_box()
        clips.new_clip(self.armature, "Wave", frame_end=13, fps=24)
        clips.key_pose(self.armature, 1, {"b1": {"rotation": (1, 0, 0, 0)}})
        clips.key_pose(self.armature, 13, {"b1": {"rotation": Quaternion((1, 0, 0), 1.0)}})
        glb = utils.export_glb(os.path.join(self._tmp.name, "wave.glb"), [box, self.armature])
        self.assertEqual([a["name"] for a in glb["animations"]], ["Wave"])
        targets = {
            (glb["nodes"][c["target"]["node"]]["name"], c["target"]["path"]) for c in glb["animations"][0]["channels"]
        }
        self.assertIn(("b1", "rotation"), targets)

    def test_two_clips_both_export_when_only_the_last_is_assigned(self):
        box = self._skinned_box()
        clips.new_clip(self.armature, "Idle", frame_end=8)
        clips.key_pose(self.armature, 1, {"b1": {"rotation": (1, 0, 0, 0)}})
        clips.key_pose(self.armature, 8, {"b1": {"rotation": Quaternion((1, 0, 0), 0.5)}})
        clips.new_clip(self.armature, "Run", frame_end=8)
        clips.key_pose(self.armature, 1, {"b1": {"rotation": (1, 0, 0, 0)}})
        clips.key_pose(self.armature, 8, {"b1": {"rotation": Quaternion((0, 1, 0), 0.5)}})
        self.assertEqual(self.armature.animation_data.action.name, "Run")
        glb = utils.export_glb(os.path.join(self._tmp.name, "two.glb"), [box, self.armature])
        self.assertEqual(sorted(a["name"] for a in glb["animations"]), ["Idle", "Run"])


class BakeTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self.armature = utils.make_armature(
            "Leg",
            [("thigh", (0, 0, 2), (0, 0.1, 1), 0.0, None), ("shin", (0, 0.1, 1), (0, 0, 0), 0.0, "thigh")],
        )
        self.target = bpy.data.objects.new("Goal", None)
        bpy.context.scene.collection.objects.link(self.target)
        self.target.location = (0, 0, 0)
        self.target.keyframe_insert("location", frame=1)
        self.target.location = (0, 0.8, 0.8)
        self.target.keyframe_insert("location", frame=20)
        constraint = self.armature.pose.bones["shin"].constraints.new("IK")
        constraint.target = self.target
        constraint.chain_count = 2

    def test_bakes_ik_into_keys_on_the_bones_and_removes_the_constraint(self):
        action = clips.bake(self.armature, 1, 20)
        self.assertEqual(len(self.armature.pose.bones["shin"].constraints), 0)
        keys = clips.clip_keys(self.armature, action)
        self.assertTrue(any("thigh" in path for path in keys))
        self.assertTrue(any("shin" in path for path in keys))
        self.assertTrue(all(count == 20 for count in keys.values()), keys)
        self.assertEqual(self.armature.mode, "OBJECT")

    def test_the_baked_pose_reaches_the_target(self):
        clips.bake(self.armature, 1, 20)
        self.target.animation_data_clear()
        self.target.location = (5, 5, 5)  # moving the goal no longer matters
        bpy.context.scene.frame_set(20)
        self.assertLess((_tail_world(self.armature, "shin") - Vector((0, 0.8, 0.8))).length, 0.02)

    def test_overwrites_the_current_action_when_there_is_one(self):
        clips.new_clip(self.armature, "Walk", frame_end=20)
        action = clips.bake(self.armature, 1, 20)
        self.assertEqual(action.name, "Walk")
        self.assertGreater(len(clips.clip_keys(self.armature, action)), 0)


class RetargetTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        # The source arm lies along +X with rolled bones.
        self.source = utils.make_armature(
            "Source",
            [("a", (0, 0, 0), (1, 0, 0), 0.0, None), ("b", (1, 0, 0), (2, 0, 0), 0.7, "a")],
            location=(-3, 0, 0),
        )
        # The target arm stands up and bends, with other rolls, an unmapped tip, and its object turned a quarter.
        self.target = utils.make_armature(
            "Target",
            [
                ("ta", (0, 0, 0), (0, 1, 0), 1.1, None),
                ("tb", (0, 1, 0), (0, 1, 1), -0.4, "ta"),
                ("tc", (0, 1, 1), (0, 1, 2), 0.3, "tb"),
            ],
            location=(4, 0, 0),
        )
        self.target.rotation_euler = (0, 0, math.pi / 2)
        bpy.context.view_layer.update()
        self.joint_map = {"a": "ta", "b": "tb"}

    def _animate_source(self):
        clips.new_clip(self.source, "Swing", frame_end=9)
        clips.key_pose(self.source, 1, {"a": {"rotation": (1, 0, 0, 0)}, "b": {"rotation": (1, 0, 0, 0)}})
        clips.key_pose(
            self.source,
            5,
            {
                "a": {"rotation": Euler((0.3, 1.0, -0.4)).to_quaternion()},
                "b": {"rotation": Euler((0.9, 0.2, 0.5)).to_quaternion()},
            },
        )
        clips.key_pose(
            self.source,
            9,
            {"a": {"rotation": (1, 0, 0, 0)}, "b": {"rotation": Euler((-0.8, 0.1, 0.2)).to_quaternion()}},
        )

    def test_every_mapped_joint_turns_by_the_same_world_rotation_as_its_source(self):
        self._animate_source()
        action = clips.retarget(self.source, self.target, self.joint_map, 1, 9)
        self.assertIs(self.target.animation_data.action, action)
        scene = bpy.context.scene
        moved = False
        for frame in range(1, 10):
            scene.frame_set(frame)
            for source_bone, target_bone in self.joint_map.items():
                expected = _rest_delta(self.source, source_bone)
                actual = _rest_delta(self.target, target_bone)
                self.assertTrue(_rotations_match(expected, actual), f"frame {frame} {target_bone}")
                moved = moved or not _rotations_match(actual, Matrix.Identity(3))
        self.assertTrue(moved, "the test pose must actually turn the bones")

    def test_a_pose_copied_across_lands_where_expected(self):
        # Turn the source's first bone a quarter turn about world Z; the whole target arm must swing the same way.
        clips.new_clip(self.source, "Quarter", frame_end=2)
        a = self.source.pose.bones["a"]
        a.rotation_mode = "QUATERNION"
        a.keyframe_insert("rotation_quaternion", frame=1)
        turn = Matrix.Rotation(math.pi / 2, 4, "Z")
        a.matrix = turn @ a.matrix
        a.keyframe_insert("rotation_quaternion", frame=2)
        self.source.pose.bones["b"].keyframe_insert("rotation_quaternion", frame=1)
        self.source.pose.bones["b"].keyframe_insert("rotation_quaternion", frame=2)

        clips.retarget(self.source, self.target, self.joint_map, 1, 2)
        bpy.context.scene.frame_set(1)
        rest_ta_tail = _tail_world(self.target, "ta")
        rest_tb_tail = _tail_world(self.target, "tb")
        rest_tc_tail = _tail_world(self.target, "tc")
        pivot = _head_world(self.target, "ta")
        bpy.context.scene.frame_set(2)

        def turned(point):
            return pivot + turn.to_3x3() @ (point - pivot)

        for bone, rest_tail in (("ta", rest_ta_tail), ("tb", rest_tb_tail), ("tc", rest_tc_tail)):
            self.assertLess((_tail_world(self.target, bone) - turned(rest_tail)).length, 1e-3, bone)

    def test_unmapped_joints_follow_their_parent(self):
        self._animate_source()
        clips.retarget(self.source, self.target, self.joint_map, 1, 9)
        bpy.context.scene.frame_set(5)
        self.assertTrue(_rotations_match(_rest_delta(self.target, "tc"), _rest_delta(self.target, "tb")))
        keyed = clips.clip_keys(self.target)
        self.assertFalse(any('"tc"' in path for path in keyed))

    def test_scales_the_hips_translation_by_the_ratio_of_the_hips_heights(self):
        source = utils.make_armature("TallSource", [("hip", (0, 0, 2), (0, 0, 2.5), 0.0, None)], location=(-3, 0, 0))
        target = utils.make_armature("ShortTarget", [("thip", (0, 0, 1), (0, 0, 1.25), 0.4, None)], location=(4, 0, 0))
        target.rotation_euler = (0, 0, math.pi / 2)
        bpy.context.view_layer.update()

        clips.new_clip(source, "Shift", frame_end=2)
        hip = source.pose.bones["hip"]
        hip.rotation_mode = "QUATERNION"
        hip.keyframe_insert("location", frame=1)
        hip.keyframe_insert("rotation_quaternion", frame=1)
        hip.matrix = Matrix.Translation((1.0, 0.5, 0.4)) @ hip.matrix
        hip.keyframe_insert("location", frame=2)
        hip.keyframe_insert("rotation_quaternion", frame=2)

        clips.retarget(source, target, {"hip": "thip"}, 1, 2, hips=("hip", "thip"))
        scene = bpy.context.scene
        scene.frame_set(1)
        start = _head_world(target, "thip")
        scene.frame_set(2)
        moved = _head_world(target, "thip") - start
        self.assertLess((moved - Vector((0.5, 0.25, 0.2))).length, 1e-3, moved)  # half the height, half the stride

    def test_the_retargeted_clip_exports_by_its_name(self):
        self._animate_source()
        clips.retarget(self.source, self.target, self.joint_map, 1, 9, name="Swing_on_target")
        with tempfile.TemporaryDirectory() as folder:
            glb = utils.export_glb(os.path.join(folder, "target.glb"), [self.target])
        self.assertIn("Swing_on_target", [a["name"] for a in glb["animations"]])

    def test_the_source_clip_leaves_the_export_once_removed(self):
        self._animate_source()
        clips.retarget(self.source, self.target, self.joint_map, 1, 9, name="Swing_on_target")
        clips.remove_clip("Swing")
        self.assertIsNone(self.source.animation_data.action)
        with tempfile.TemporaryDirectory() as folder:
            glb = utils.export_glb(os.path.join(folder, "target.glb"), [self.target])
        self.assertEqual([a["name"] for a in glb["animations"]], ["Swing_on_target"])

    def test_rejects_bones_that_do_not_exist_and_a_source_with_no_animation(self):
        with self.assertRaises(ValueError):
            clips.retarget(self.source, self.target, self.joint_map, 1, 9)
        self._animate_source()
        with self.assertRaises(ValueError):
            clips.retarget(self.source, self.target, {"a": "missing"}, 1, 9)


if __name__ == "__main__":
    unittest.main()
