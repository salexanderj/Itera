"""Tests for itera_models.materials."""

import os
import tempfile
import unittest

import blender_test_utils as utils
import bpy
import numpy as np
from itera_models import materials


def _textured_cube(location=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=location)
    return bpy.context.object


def _image_names(glb: dict, material_index: int = 0) -> dict:
    """Which glTF texture slots the material uses."""
    material = glb["materials"][material_index]
    pbr = material.get("pbrMetallicRoughness", {})
    return {
        "baseColor": "baseColorTexture" in pbr,
        "metallicRoughness": "metallicRoughnessTexture" in pbr,
        "normal": "normalTexture" in material,
        "occlusion": "occlusionTexture" in material,
    }


class PbrTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = self._tmp.name
        self.paths = {}
        for key, color in {
            "color": (0.8, 0.3, 0.2, 1),
            "normal": (0.5, 0.2, 1.0, 1),
            "rough": (0.6, 0.6, 0.6, 1),
            "ao": (0.7, 0.7, 0.7, 1),
            "metal": (0.0, 0.0, 0.0, 1),
        }.items():
            path = os.path.join(self.dir, f"{key}.png")
            utils.solid_png(path, color)
            self.paths[key] = path

    def tearDown(self):
        self._tmp.cleanup()

    def _node_of(self, material, image_key):
        for node in material.node_tree.nodes:
            if node.bl_idname == "ShaderNodeTexImage" and node.image.name.startswith(image_key):
                return node
        self.fail(f"no image node for {image_key}")

    def test_wires_each_map_into_the_principled_bsdf(self):
        material = materials.pbr(
            "Test",
            color=self.paths["color"],
            normal=self.paths["normal"],
            roughness=self.paths["rough"],
            ao=self.paths["ao"],
            metal=self.paths["metal"],
        )
        bsdf = next(n for n in material.node_tree.nodes if n.bl_idname == "ShaderNodeBsdfPrincipled")
        self.assertTrue(bsdf.inputs["Base Color"].is_linked)
        self.assertTrue(bsdf.inputs["Roughness"].is_linked)
        self.assertTrue(bsdf.inputs["Metallic"].is_linked)
        normal_map = bsdf.inputs["Normal"].links[0].from_node
        self.assertEqual(normal_map.bl_idname, "ShaderNodeNormalMap")
        self.assertEqual(self._node_of(material, "color").image.colorspace_settings.name, "sRGB")
        for key in ("normal", "rough", "ao", "metal"):
            self.assertEqual(self._node_of(material, key).image.colorspace_settings.name, "Non-Color", key)

    def test_directx_normal_gets_its_green_channel_flipped(self):
        material = materials.pbr("Test", normal=self.paths["normal"], normal_directx=True)
        normal_map = next(n for n in material.node_tree.nodes if n.bl_idname == "ShaderNodeNormalMap")
        image = normal_map.inputs["Color"].links[0].from_node.image
        pixels = np.empty(image.size[0] * image.size[1] * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
        self.assertAlmostEqual(float(pixels[1::4].mean()), 0.8, places=2)  # green was 0.2
        self.assertAlmostEqual(float(pixels[0::4].mean()), 0.5, places=2)  # red is untouched

    def test_tiling_scales_the_texture_coordinates(self):
        material = materials.pbr("Test", color=self.paths["color"], tiling=4.0)
        mapping = next(n for n in material.node_tree.nodes if n.bl_idname == "ShaderNodeMapping")
        self.assertEqual(tuple(mapping.inputs["Scale"].default_value), (4.0, 4.0, 4.0))
        node = self._node_of(material, "color")
        self.assertTrue(node.inputs["Vector"].is_linked)

    def test_no_tiling_adds_no_mapping(self):
        material = materials.pbr("Test", color=self.paths["color"])
        self.assertFalse(any(n.bl_idname == "ShaderNodeMapping" for n in material.node_tree.nodes))

    def test_exports_every_texture_slot(self):
        cube = _textured_cube()
        material = materials.pbr(
            "Test",
            color=self.paths["color"],
            normal=self.paths["normal"],
            roughness=self.paths["rough"],
            ao=self.paths["ao"],
            metal=self.paths["metal"],
        )
        cube.data.materials.append(material)
        glb = utils.export_glb(os.path.join(self.dir, "pbr.glb"), [cube])
        self.assertEqual(
            _image_names(glb),
            {"baseColor": True, "metallicRoughness": True, "normal": True, "occlusion": True},
        )

    def test_exports_a_flipped_directx_normal(self):
        cube = _textured_cube()
        cube.data.materials.append(
            materials.pbr("Test", color=self.paths["color"], normal=self.paths["normal"], normal_directx=True)
        )
        glb = utils.export_glb(os.path.join(self.dir, "dx.glb"), [cube])
        self.assertTrue(_image_names(glb)["normal"])
        self.assertEqual(len(glb["images"]), 2)

    def test_tiling_exports_as_a_texture_transform(self):
        cube = _textured_cube()
        cube.data.materials.append(materials.pbr("Test", color=self.paths["color"], tiling=3.0))
        glb = utils.export_glb(os.path.join(self.dir, "tiled.glb"), [cube])
        self.assertIn("KHR_texture_transform", glb.get("extensionsUsed", []))

    def test_pbr_from_maps_reads_both_libraries_keys(self):
        polyhaven = materials.pbr_from_maps(
            "A", {"diff": self.paths["color"], "nor_gl": self.paths["normal"], "rough": self.paths["rough"]}
        )
        ambientcg = materials.pbr_from_maps(
            "B", {"color": self.paths["color"], "normal_dx": self.paths["normal"], "roughness": self.paths["rough"]}
        )
        for material in (polyhaven, ambientcg):
            bsdf = next(n for n in material.node_tree.nodes if n.bl_idname == "ShaderNodeBsdfPrincipled")
            self.assertTrue(bsdf.inputs["Base Color"].is_linked)
            self.assertTrue(bsdf.inputs["Normal"].is_linked)
            self.assertTrue(bsdf.inputs["Roughness"].is_linked)
        # Only the DirectX one is flipped, into a new image.
        names = [n.image.name for n in ambientcg.node_tree.nodes if n.bl_idname == "ShaderNodeTexImage"]
        self.assertTrue(any(name.endswith("_normal_gl") for name in names))


class GrimeTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self._tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self._tmp.cleanup()

    def _trough(self):
        """Two boxes side by side with a deep slot between them: a crevice."""
        bpy.ops.mesh.primitive_cube_add(size=1.0, location=(-0.55, 0, 0))
        bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0.55, 0, 0))
        bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, -0.5), scale=(1.4, 1.0, 1.0))
        for obj in bpy.context.scene.objects:
            obj.select_set(True)
        bpy.ops.object.join()
        return bpy.context.object

    def _mean_colour(self, obj, name):
        out = os.path.join(self._tmp.name, name)
        materials.bake(obj, size=64, maps=("color",), out_dir=out, margin=2, samples=2)
        pixels = utils.read_png(os.path.join(out, f"{obj.name}_color.png"))
        return float(pixels[..., :3].mean())

    def test_grime_inserts_a_mix_between_the_colour_and_the_bsdf(self):
        material = bpy.data.materials.new("M")
        bsdf = material.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Base Color"].default_value = (0.7, 0.7, 0.7, 1)
        materials.grime(material, amount=0.8)
        feeder = bsdf.inputs["Base Color"].links[0].from_node
        self.assertEqual(feeder.bl_idname, "ShaderNodeMix")
        self.assertEqual(feeder.data_type, "RGBA")
        kinds = {n.bl_idname for n in material.node_tree.nodes}
        self.assertTrue({"ShaderNodeAmbientOcclusion", "ShaderNodeNewGeometry", "ShaderNodeTexNoise"} <= kinds)

    def test_grime_keeps_an_existing_colour_link(self):
        material = bpy.data.materials.new("M")
        nodes = material.node_tree.nodes
        checker = nodes.new("ShaderNodeTexChecker")
        material.node_tree.links.new(checker.outputs["Color"], nodes["Principled BSDF"].inputs["Base Color"])
        materials.grime(material)
        mix = nodes["Principled BSDF"].inputs["Base Color"].links[0].from_node
        sources = [link.from_node for socket in mix.inputs for link in socket.links]
        self.assertIn(checker, sources)

    def test_grime_darkens_a_baked_colour_in_a_crevice(self):
        plain = self._trough()
        plain.data.materials.append(bpy.data.materials.new("Plain"))
        plain.data.materials[0].node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (
            0.7,
            0.7,
            0.7,
            1,
        )
        clean = self._mean_colour(plain, "clean")

        bpy.ops.object.delete()
        dirty_obj = self._trough()
        material = bpy.data.materials.new("Dirty")
        material.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.7, 0.7, 0.7, 1)
        materials.grime(material, amount=1.0, distance=0.4)
        dirty_obj.data.materials.append(material)
        dirty = self._mean_colour(dirty_obj, "dirty")
        self.assertLess(dirty, clean - 0.01)


class BakeTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = self._tmp.name

    def tearDown(self):
        self._tmp.cleanup()

    def _cube_with_colour(self, colour=(0.9, 0.2, 0.1, 1)):
        cube = _textured_cube()
        material = bpy.data.materials.new("Source")
        material.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = colour
        material.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.3
        cube.data.materials.append(material)
        return cube

    def test_bake_writes_pngs_of_the_asked_size_and_exports_textures(self):
        cube = self._cube_with_colour()
        # The primitive has UVs; remove them so bake has to unwrap.
        while cube.data.uv_layers:
            cube.data.uv_layers.remove(cube.data.uv_layers[0])
        out = os.path.join(self.dir, "bake")
        material = materials.bake(cube, size=128, out_dir=out, margin=4)

        self.assertTrue(len(cube.data.uv_layers) >= 1, "bake should unwrap an object with no UVs")
        for name in ("color", "normal", "roughness", "ao"):
            path = os.path.join(out, f"{cube.name}_{name}.png")
            self.assertTrue(os.path.isfile(path), path)
            image = bpy.data.images.load(path)
            self.assertEqual(tuple(image.size), (128, 128))
            bpy.data.images.remove(image)
        self.assertEqual([m.name for m in cube.data.materials], [material.name])
        self.assertIn("total", materials.last_bake_seconds)

        glb = utils.export_glb(os.path.join(self.dir, "baked.glb"), [cube])
        self.assertEqual(len(glb["materials"]), 1)
        names = _image_names(glb)
        self.assertTrue(names["baseColor"] and names["normal"], names)
        self.assertTrue(names["occlusion"], names)
        self.assertTrue(names["metallicRoughness"], names)

    def test_baked_colour_matches_the_source_colour(self):
        cube = self._cube_with_colour((0.9, 0.2, 0.1, 1))
        out = os.path.join(self.dir, "bake")
        materials.bake(cube, size=64, maps=("color",), out_dir=out)
        pixels = utils.read_png(os.path.join(out, f"{cube.name}_color.png")).reshape(-1, 4)

        def srgb(linear):  # the file holds sRGB-encoded values
            return 12.92 * linear if linear <= 0.0031308 else 1.055 * linear ** (1 / 2.4) - 0.055

        median = np.median(pixels[:, :3], axis=0)
        for channel, linear in enumerate((0.9, 0.2, 0.1)):
            self.assertAlmostEqual(float(median[channel]), srgb(linear), delta=0.02)

    def test_bake_with_only_some_maps_writes_only_those(self):
        cube = self._cube_with_colour()
        out = os.path.join(self.dir, "bake")
        materials.bake(cube, size=32, maps=("color", "roughness"), out_dir=out)
        self.assertEqual(sorted(os.listdir(out)), [f"{cube.name}_color.png", f"{cube.name}_roughness.png"])

    def test_bake_merges_several_materials_into_one(self):
        cube = _textured_cube()
        for colour in ((1.0, 0.0, 0.0, 1), (0.0, 0.0, 1.0, 1)):
            material = bpy.data.materials.new("Part")
            material.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = colour
            cube.data.materials.append(material)
        indices = np.array([0, 0, 0, 1, 1, 1], dtype=np.int32)
        cube.data.polygons.foreach_set("material_index", indices)
        out = os.path.join(self.dir, "bake")
        baked = materials.bake(cube, size=64, maps=("color",), out_dir=out)
        self.assertEqual([m.name for m in cube.data.materials], [baked.name])
        self.assertEqual({p.material_index for p in cube.data.polygons}, {0})
        pixels = utils.read_png(os.path.join(out, f"{cube.name}_color.png")).reshape(-1, 4)
        self.assertGreater(int(np.count_nonzero((pixels[:, 0] > 0.8) & (pixels[:, 2] < 0.2))), 200)  # red
        self.assertGreater(int(np.count_nonzero((pixels[:, 2] > 0.8) & (pixels[:, 0] < 0.2))), 200)  # blue
        # The source materials are left as they were, without the bake's image nodes.
        for material in bpy.data.materials:
            if material.name.startswith("Part"):
                self.assertFalse(any(n.bl_idname == "ShaderNodeTexImage" for n in material.node_tree.nodes))

    def test_bake_rejects_an_unknown_map(self):
        cube = self._cube_with_colour()
        with self.assertRaises(ValueError):
            materials.bake(cube, size=32, maps=("metal",), out_dir=self.dir)

    def test_bake_includes_the_materials_own_normal_map(self):
        # A flat tangent-space normal is (0.5, 0.5, 1). A tilted one must show in the baked normal.
        tilted = os.path.join(self.dir, "tilted.png")
        utils.solid_png(tilted, (0.8, 0.5, 0.8, 1))

        flat = self._cube_with_colour()
        flat_out = os.path.join(self.dir, "flat")
        materials.bake(flat, size=32, maps=("normal",), out_dir=flat_out)
        flat_pixels = utils.read_png(os.path.join(flat_out, f"{flat.name}_normal.png"))

        bpy.ops.object.delete()
        bumped = self._cube_with_colour()
        material = bumped.data.materials[0]
        nodes = material.node_tree.nodes
        texture = nodes.new("ShaderNodeTexImage")
        texture.image = bpy.data.images.load(tilted)
        texture.image.colorspace_settings.name = "Non-Color"
        normal_map = nodes.new("ShaderNodeNormalMap")
        material.node_tree.links.new(texture.outputs["Color"], normal_map.inputs["Color"])
        material.node_tree.links.new(normal_map.outputs["Normal"], nodes["Principled BSDF"].inputs["Normal"])
        bumped_out = os.path.join(self.dir, "bumped")
        materials.bake(bumped, size=32, maps=("normal",), out_dir=bumped_out)
        bumped_pixels = utils.read_png(os.path.join(bumped_out, f"{bumped.name}_normal.png"))

        flat_mean = flat_pixels[..., :3].reshape(-1, 3).mean(axis=0)
        bumped_mean = bumped_pixels[..., :3].reshape(-1, 3).mean(axis=0)
        # Flat is (0.5, 0.5, 1); the tilt leans +X, so red rises, green holds, blue falls.
        self.assertTrue(np.allclose(flat_mean, (0.5, 0.5, 1.0), atol=0.02), flat_mean)
        self.assertGreater(float(bumped_mean[0]), 0.65)
        self.assertAlmostEqual(float(bumped_mean[1]), 0.5, delta=0.03)
        self.assertLess(float(bumped_mean[2]), 0.9)


if __name__ == "__main__":
    unittest.main()
