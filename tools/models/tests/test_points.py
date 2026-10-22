"""Tests of itera_models.points, run inside Blender (tests/run.py)."""

import tempfile
import unittest
from pathlib import Path

import bpy
import scene_kit
from itera_models import export, points


class PointTests(unittest.TestCase):
    def setUp(self):
        scene_kit.reset()
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / "model.glb"

    def export_and_read(self):
        bpy.context.view_layer.update()
        export.glb(self.path)
        document, _ = scene_kit.read_glb(self.path)
        return document

    def test_point_under_a_bone_is_a_child_of_the_bones_node(self):
        cube = scene_kit.add_cube()
        rig = scene_kit.add_armature()
        scene_kit.skin_to(cube, rig, {"top": {i: 1.0 for i in range(8)}})
        point = points.add_point("muzzle", (0.0, 0.0, 0.0), parent=rig, bone="top", size=0.1)
        self.assertEqual("EMPTY", point.type)
        self.assertEqual("PLAIN_AXES", point.empty_display_type)
        bpy.context.view_layer.update()
        # With no offset the point sits at the bone's head, 0.5 m up, not at its tail.
        self.assertAlmostEqual(0.5, point.matrix_world.translation.z, places=5)
        document = self.export_and_read()
        nodes = document["nodes"]
        by_name = {node["name"]: index for index, node in enumerate(nodes)}
        self.assertIn("muzzle", by_name)
        self.assertIn(by_name["muzzle"], nodes[by_name["top"]].get("children", []))
        translation = nodes[by_name["muzzle"]].get("translation", [0.0, 0.0, 0.0])
        for value in translation:
            self.assertAlmostEqual(0.0, value, places=4)
        self.assertNotIn("mesh", nodes[by_name["muzzle"]])
        self.assertNotIn("children", nodes[by_name["muzzle"]])

    def test_location_and_rotation_under_a_bone_export_as_given(self):
        cube = scene_kit.add_cube()
        # A bone that points sideways, so its axes differ from the armature's.
        rig = scene_kit.add_armature(
            bones=(
                ("root", (0, 0, 0), (0, 0, 0.5)),
                ("arm", (0, 0, 0.5), (1.0, 0, 0.5)),
                ("hand", (1, 0, 0.5), (1, 0.5, 0.5)),
            )
        )
        scene_kit.skin_to(cube, rig, {"arm": {i: 1.0 for i in range(8)}})
        points.add_point("flat", (0.1, 0.2, 0.3), parent=rig, bone="hand")
        points.add_point("turned", (0.0, 0.0, 0.0), rotation=(0.0, 0.3, 0.0), parent=rig, bone="hand")
        points.add_point("free_turned", (0.0, 0.0, 0.0), rotation=(0.0, 0.3, 0.0))
        document = self.export_and_read()
        nodes = {node["name"]: node for node in document["nodes"]}
        for expected, actual in zip((0.1, 0.2, 0.3), nodes["flat"]["translation"]):
            self.assertAlmostEqual(expected, actual, places=4)
        # No rotation exports as no rotation, and a turn exports as it does for a point outside a bone.
        for expected, actual in zip((0, 0, 0, 1), nodes["flat"].get("rotation", [0, 0, 0, 1])):
            self.assertAlmostEqual(expected, actual, places=4)
        for expected, actual in zip(nodes["free_turned"]["rotation"], nodes["turned"]["rotation"]):
            self.assertAlmostEqual(expected, actual, places=4)

    def test_point_under_an_object_and_a_free_point(self):
        cube = scene_kit.add_cube()
        points.add_point("seat", (0.0, 0.0, 1.0), parent=cube)
        points.add_point("origin", (1.0, 2.0, 3.0), rotation=(0.0, 0.0, 1.0))
        bpy.context.view_layer.update()
        self.assertAlmostEqual(1.5, bpy.data.objects["seat"].matrix_world.translation.z, places=5)
        document = self.export_and_read()
        names = [node["name"] for node in document["nodes"]]
        self.assertIn("seat", names)
        self.assertIn("origin", names)

    def test_a_bone_needs_an_armature_that_has_it(self):
        cube = scene_kit.add_cube()
        rig = scene_kit.add_armature()
        with self.assertRaises(ValueError):
            points.add_point("a", (0, 0, 0), parent=cube, bone="top")
        with self.assertRaises(ValueError):
            points.add_point("b", (0, 0, 0), parent=rig, bone="nope")


if __name__ == "__main__":
    unittest.main()
