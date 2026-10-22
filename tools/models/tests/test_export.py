"""Tests of itera_models.export, run inside Blender (tests/run.py)."""

import tempfile
import unittest
from pathlib import Path

import bpy
import scene_kit
from itera_models import export

FORBIDDEN_EXTENSIONS = (
    "KHR_draco_mesh_compression",
    "EXT_meshopt_compression",
    "KHR_meshopt_compression",
    "KHR_texture_basisu",
    "KHR_lights_punctual",
    "EXT_texture_webp",
)


class ExportTests(unittest.TestCase):
    def setUp(self):
        scene_kit.reset()
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / "model.glb"

    def export_and_read(self, **arguments):
        settings = export.glb(self.path, **arguments)
        document, binary = scene_kit.read_glb(self.path)
        return settings, document, binary

    def test_cube_has_no_compression_no_cameras_and_a_named_material(self):
        scene_kit.add_cube(material_name="Stone")
        bpy.ops.object.camera_add()
        bpy.ops.object.light_add(type="POINT")
        settings, document, _ = self.export_and_read()
        extensions = set(document.get("extensionsUsed", [])) | set(document.get("extensionsRequired", []))
        for extension in FORBIDDEN_EXTENSIONS:
            self.assertNotIn(extension, extensions)
        self.assertNotIn("cameras", document)
        self.assertEqual(["Stone"], [material["name"] for material in document["materials"]])
        self.assertEqual(1, len(document["meshes"]))
        self.assertNotIn("animations", document)
        self.assertTrue(settings["export_apply"])
        self.assertEqual(4, settings["export_influence_nb"])
        self.assertFalse(settings["export_morph"])

    def test_modifiers_are_applied(self):
        cube = scene_kit.add_cube()
        cube.modifiers.new("Array", "ARRAY").count = 3
        _, document, _ = self.export_and_read()
        position = document["accessors"][document["meshes"][0]["primitives"][0]["attributes"]["POSITION"]]
        # A cube has 24 vertices once split at its hard edges; three of them make 72.
        self.assertEqual(72, position["count"])

    def test_normal_mapped_material_exports_tangents(self):
        cube = scene_kit.add_cube(material_name="Bumpy")
        material = cube.data.materials[0]
        image = bpy.data.images.new("bump", 4, 4)
        image.pixels = [0.5, 0.5, 1.0, 1.0] * 16
        image.pack()
        tree = material.node_tree
        texture = tree.nodes.new("ShaderNodeTexImage")
        texture.image = image
        texture.image.colorspace_settings.name = "Non-Color"
        normal_map = tree.nodes.new("ShaderNodeNormalMap")
        tree.links.new(texture.outputs["Color"], normal_map.inputs["Color"])
        tree.links.new(normal_map.outputs["Normal"], tree.nodes["Principled BSDF"].inputs["Normal"])
        settings, document, _ = self.export_and_read()
        self.assertTrue(settings["export_tangents"])
        attributes = document["meshes"][0]["primitives"][0]["attributes"]
        self.assertIn("TANGENT", attributes)
        self.assertEqual(["image/png"], [entry["mimeType"] for entry in document["images"]])

    def test_plain_material_exports_no_tangents(self):
        scene_kit.add_cube()
        settings, document, _ = self.export_and_read()
        self.assertFalse(settings["export_tangents"])
        self.assertNotIn("TANGENT", document["meshes"][0]["primitives"][0]["attributes"])

    def test_static_model_exports_no_animations_even_with_an_active_action(self):
        cube = scene_kit.add_cube()
        armature = scene_kit.add_armature()
        scene_kit.skin_to(cube, armature, {"root": {i: 1.0 for i in range(8)}})
        scene_kit.key_clip(armature, "Wave")
        _, document, _ = self.export_and_read()
        self.assertNotIn("animations", document)
        self.assertEqual(1, len(document["skins"]))

    def test_only_the_asked_for_clip_is_exported_by_name(self):
        cube = scene_kit.add_cube()
        armature = scene_kit.add_armature()
        scene_kit.skin_to(cube, armature, {"root": {i: 1.0 for i in range(8)}})
        scene_kit.key_clip(armature, "Wave")
        nod = scene_kit.key_clip(armature, "Nod")
        self.assertIs(nod, armature.animation_data.action)
        _, document, _ = self.export_and_read(clips=["Nod"])
        self.assertEqual(["Nod"], [clip["name"] for clip in document["animations"]])
        _, document, _ = self.export_and_read(clips=["Wave", "Nod"])
        self.assertEqual(["Nod", "Wave"], sorted(clip["name"] for clip in document["animations"]))

    def test_a_clip_starts_at_time_zero(self):
        cube = scene_kit.add_cube()
        armature = scene_kit.add_armature()
        scene_kit.skin_to(cube, armature, {"root": {i: 1.0 for i in range(8)}})
        scene_kit.key_clip(armature, "Wave", frames=(1, 25))
        _, document, _ = self.export_and_read(clips=["Wave"])
        sampler = document["animations"][0]["samplers"][0]
        times = document["accessors"][sampler["input"]]
        self.assertAlmostEqual(0.0, times["min"][0], places=4)
        self.assertAlmostEqual(1.0, times["max"][0], places=4)

    def test_export_leaves_the_animation_state_as_it_was(self):
        cube = scene_kit.add_cube()
        armature = scene_kit.add_armature()
        scene_kit.skin_to(cube, armature, {"root": {i: 1.0 for i in range(8)}})
        scene_kit.key_clip(armature, "Wave")
        nod = scene_kit.key_clip(armature, "Nod")
        self.export_and_read(clips=["Wave"])
        self.assertIs(nod, armature.animation_data.action)
        self.assertEqual(0, len(armature.animation_data.nla_tracks))

    def test_unknown_clip_is_refused(self):
        scene_kit.add_cube()
        with self.assertRaises(ValueError):
            export.glb(self.path, clips=["Nope"])

    def test_vertex_colours_are_exported_only_on_request(self):
        cube = scene_kit.add_cube()
        cube.data.color_attributes.new("Col", "BYTE_COLOR", "CORNER")
        _, document, _ = self.export_and_read()
        self.assertNotIn("COLOR_0", document["meshes"][0]["primitives"][0]["attributes"])
        settings, document, _ = self.export_and_read(vertex_colors=True)
        self.assertEqual("ACTIVE", settings["export_vertex_color"])
        self.assertIn("COLOR_0", document["meshes"][0]["primitives"][0]["attributes"])

    def test_selection_is_restored(self):
        cube = scene_kit.add_cube()
        other = scene_kit.add_cube(name="Other", location=(3, 0, 0.5))
        bpy.ops.object.select_all(action="DESELECT")
        other.select_set(True)
        export.glb(self.path, objects=[cube])
        self.assertFalse(cube.select_get())
        self.assertTrue(other.select_get())
        document, _ = scene_kit.read_glb(self.path)
        self.assertEqual(["Cube"], [node["name"] for node in document["nodes"]])


if __name__ == "__main__":
    unittest.main()
