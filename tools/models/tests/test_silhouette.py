"""Tests for itera_models.silhouette."""

import math
import os
import tempfile
import unittest

import blender_test_utils as utils
import bpy
import numpy as np
from itera_models import rig, silhouette


def _cube(size, location, scale=(1, 1, 1), name="Cube"):
    bpy.ops.mesh.primitive_cube_add(size=size, location=location, scale=scale)
    obj = bpy.context.object
    obj.name = name
    return obj


def _lopsided_model():
    """A tall block with a small block on its +X side near the top and a small block toward +Y at the base."""
    body = _cube(1.0, (0, 0, 1.0), scale=(1, 1, 2), name="Body")
    arm = _cube(1.0, (1.0, 0, 1.8), scale=(1.0, 0.4, 0.3), name="Arm")
    foot = _cube(1.0, (0, 0.9, 0.2), scale=(0.5, 1.0, 0.4), name="Foot")
    return [body, arm, foot]


def _render_reference(objects, path, size=300, background=(0.0, 0.0, 0.0)):
    """Renders white objects on a plain background from the front, with a camera of the test's own."""
    scene = bpy.data.scenes.new("reference")
    camera_data = bpy.data.cameras.new("reference")
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = 6.0
    camera = bpy.data.objects.new("reference", camera_data)
    camera.location = (0.3, -10, 1.5)
    camera.rotation_euler = (math.pi / 2, 0, 0)
    scene.collection.objects.link(camera)
    scene.camera = camera
    for obj in objects:
        scene.collection.objects.link(obj)
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = size
    scene.render.resolution_y = size
    scene.render.film_transparent = False
    scene.display.shading.light = "FLAT"
    scene.display.shading.color_type = "SINGLE"
    scene.display.shading.single_color = (1, 1, 1)
    scene.view_settings.view_transform = "Standard"
    scene.world = bpy.data.worlds.new("reference")
    scene.world.color = background
    scene.render.image_settings.media_type = "IMAGE"
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True, scene=scene.name)
    for obj in objects:
        scene.collection.objects.unlink(obj)
    bpy.data.scenes.remove(scene)


class MaskTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()

    def test_returns_a_boolean_square_with_the_model_filling_it(self):
        objects = _lopsided_model()
        found = silhouette.mask(objects, "front", 128)
        self.assertEqual(found.shape, (128, 128))
        self.assertEqual(found.dtype, bool)
        rows = np.nonzero(found)[0]
        self.assertLess(rows.min(), 8)
        self.assertGreater(rows.max(), 119)  # the model is the larger of width and height, so it fills one side
        self.assertGreater(found.mean(), 0.1)

    def test_views_follow_the_documented_directions(self):
        # The arm is at +X and high; the foot is at +Y and low (see _lopsided_model).
        objects = _lopsided_model()
        front = silhouette.mask(objects, "front", 128)
        back = silhouette.mask(objects, "back", 128)
        side = silhouette.mask(objects, "side", 128)
        top = silhouette.mask(objects, "top", 128)

        def edge(found, side):  # the occupied pixels within 3 columns of the shape's left or right end
            occupied = np.flatnonzero(found.any(axis=0))
            end = occupied[0] if side == "left" else occupied[-1]
            return found[:, max(end - 2, 0) : end + 3]

        def rows(found):  # mean row of the occupied pixels, from 0 (top) to 1 (bottom)
            return np.nonzero(found)[0].mean() / found.shape[0]

        def columns(found):
            return np.nonzero(found)[1].mean() / found.shape[1]

        self.assertLess(rows(edge(front, "right")), 0.35)  # front: +X is on the right, the arm is high
        self.assertLess(rows(edge(back, "left")), 0.35)  # back: +X is on the left
        self.assertGreater(rows(edge(side, "right")), 0.75)  # side: +Y is on the right, the foot is low
        first = np.flatnonzero(top.any(axis=1))[0]
        self.assertLess(columns(top[first : first + 3]), 0.5)  # top: +Y is up, the foot is left of the arm
        self.assertGreater(rows(edge(top, "right")), 0.5)  # top: +X is on the right, the arm is below the foot

    def test_an_unknown_view_and_an_empty_list_are_refused(self):
        with self.assertRaises(ValueError):
            silhouette.mask(_lopsided_model(), "left")
        with self.assertRaises(ValueError):
            silhouette.mask([], "front")

    def test_leaves_no_scene_camera_or_objects_behind(self):
        objects = _lopsided_model()
        scenes, cameras, object_count = len(bpy.data.scenes), len(bpy.data.cameras), len(bpy.data.objects)
        silhouette.mask(objects, "front", 64)
        self.assertEqual(
            (len(bpy.data.scenes), len(bpy.data.cameras), len(bpy.data.objects)), (scenes, cameras, object_count)
        )
        self.assertEqual([i.name for i in bpy.data.images if i.name != "Render Result"], [])

    def test_a_posed_skinned_mesh_draws_in_its_pose(self):
        armature = utils.make_armature(
            "Rig", [("b0", (0, 0, 0), (0, 0, 1), 0.0, None), ("b1", (0, 0, 1), (0, 0, 2), 0.0, "b0")]
        )
        upper = _cube(1.0, (0, 0, 1.5), scale=(0.3, 0.3, 1.0), name="Upper")
        lower = _cube(1.0, (0, 0, 0.5), scale=(0.3, 0.3, 1.0), name="Lower")
        skinned = rig.rigid_skin({lower: "b0", upper: "b1"}, armature)
        straight = silhouette.mask([skinned], "side", 128)
        armature.pose.bones["b1"].rotation_mode = "XYZ"
        armature.pose.bones["b1"].rotation_euler = (math.pi / 2, 0, 0)
        bpy.context.view_layer.update()
        bent = silhouette.mask([skinned], "side", 128)
        self.assertLess(silhouette.iou(straight, bent), 0.7)


class ImageMaskTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = self._tmp.name

    def tearDown(self):
        self._tmp.cleanup()

    def _picture(self, name, shape_value, background, alpha=1.0, width=60, height=40):
        pixels = np.zeros((height, width, 4), dtype=np.float32)
        pixels[...] = (*background, 1.0)
        pixels[10:30, 20:50] = (*shape_value, alpha)
        path = os.path.join(self.dir, name)
        utils.save_png(path, pixels)
        return path

    def test_uses_alpha_when_the_image_has_it(self):
        path = os.path.join(self.dir, "alpha.png")
        pixels = np.zeros((40, 60, 4), dtype=np.float32)
        pixels[10:30, 20:50] = (0.5, 0.5, 0.5, 1.0)
        utils.save_png(path, pixels)
        found = silhouette.image_mask(path, 60)
        self.assertEqual(found.shape, (40, 60))
        self.assertEqual(int(found.sum()), 20 * 30)
        self.assertTrue(found[10:30, 20:50].all())

    def test_finds_a_light_or_a_dark_shape_on_the_corner_colour(self):
        dark_on_light = silhouette.image_mask(self._picture("a.png", (0.1, 0.1, 0.1), (0.9, 0.9, 0.9)), 60)
        light_on_dark = silhouette.image_mask(self._picture("b.png", (0.9, 0.9, 0.9), (0.1, 0.1, 0.1)), 60)
        self.assertEqual(int(dark_on_light.sum()), 20 * 30)
        self.assertTrue(np.array_equal(dark_on_light, light_on_dark))

    def test_a_faint_difference_falls_under_the_threshold(self):
        path = self._picture("faint.png", (0.54, 0.54, 0.54), (0.5, 0.5, 0.5))
        self.assertEqual(int(silhouette.image_mask(path, 60).sum()), 0)
        self.assertGreater(int(silhouette.image_mask(path, 60, threshold=0.01).sum()), 0)

    def test_size_scales_the_longer_side_and_flip_mirrors(self):
        path = self._picture("c.png", (0.1, 0.1, 0.1), (0.9, 0.9, 0.9), width=60, height=40)
        small = silhouette.image_mask(path, 30)
        self.assertEqual(small.shape, (20, 30))
        normal = silhouette.image_mask(path, 60)
        flipped = silhouette.image_mask(path, 60, flip=True)
        self.assertTrue(np.array_equal(flipped, normal[:, ::-1]))
        self.assertFalse(np.array_equal(flipped, normal))

    def test_row_zero_is_the_top_of_the_picture(self):
        pixels = np.zeros((20, 20, 4), dtype=np.float32)
        pixels[..., 3] = 0.0
        pixels[0:5, 0:20] = (1, 1, 1, 1)  # the top five rows (save_png takes row 0 as the top)
        path = os.path.join(self.dir, "top.png")
        utils.save_png(path, pixels)
        found = silhouette.image_mask(path, 20)
        self.assertTrue(found[0:5].all())
        self.assertFalse(found[5:].any())


class IouTests(unittest.TestCase):
    def _block(self, rows, columns, shape=(100, 100)):
        found = np.zeros(shape, dtype=bool)
        found[rows[0] : rows[1], columns[0] : columns[1]] = True
        return found

    def test_the_same_shape_anywhere_at_any_size_scores_one(self):
        small = self._block((10, 20), (30, 60), shape=(50, 100))
        large = self._block((200, 400), (100, 700), shape=(600, 900))
        self.assertEqual(silhouette.iou(small, large), 1.0)

    def test_different_shapes_score_low(self):
        square = self._block((0, 40), (0, 40))
        ring = square.copy()
        ring[5:35, 5:35] = False
        self.assertLess(silhouette.iou(square, ring), 0.5)

    def test_an_empty_mask_scores_zero(self):
        self.assertEqual(silhouette.iou(np.zeros((10, 10), dtype=bool), self._block((0, 5), (0, 5))), 0.0)
        self.assertEqual(silhouette.iou(self._block((0, 5), (0, 5)), np.zeros((10, 10), dtype=bool)), 0.0)

    def test_keep_aspect_tells_a_wide_shape_from_a_tall_one(self):
        wide = self._block((0, 20), (0, 80))
        tall = self._block((0, 80), (0, 20))
        self.assertEqual(silhouette.iou(wide, tall, keep_aspect=False), 1.0)  # stretched to the same square
        self.assertLess(silhouette.iou(wide, tall), 0.2)
        self.assertEqual(silhouette.iou(wide, wide.copy()), 1.0)


class CompareTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = self._tmp.name

    def tearDown(self):
        self._tmp.cleanup()

    def test_a_model_matches_a_render_of_itself_made_another_way(self):
        objects = _lopsided_model()
        reference = os.path.join(self.dir, "reference.png")
        _render_reference(objects, reference, size=300)
        score = silhouette.iou(silhouette.mask(objects, "front", 256), silhouette.image_mask(reference, 300))
        self.assertGreater(score, 0.95)

    def test_a_model_matches_its_own_alpha_render(self):
        objects = _lopsided_model()
        path = os.path.join(self.dir, "alpha.png")
        found = silhouette.mask(objects, "front", 200)
        pixels = np.zeros((200, 200, 4), dtype=np.float32)
        pixels[found] = (1, 1, 1, 1)
        utils.save_png(path, pixels)
        self.assertEqual(silhouette.iou(found, silhouette.image_mask(path, 200)), 1.0)

    def test_a_different_shape_scores_clearly_lower(self):
        objects = _lopsided_model()
        reference = os.path.join(self.dir, "reference.png")
        _render_reference(objects, reference, size=300)
        bpy.ops.mesh.primitive_uv_sphere_add(radius=1.0, location=(0, 0, 1.0))
        sphere = bpy.context.object
        same = silhouette.iou(silhouette.mask(objects, "front", 256), silhouette.image_mask(reference, 300))
        different = silhouette.iou(silhouette.mask([sphere], "front", 256), silhouette.image_mask(reference, 300))
        self.assertGreater(same, 0.95)
        self.assertLess(different, 0.75)
        self.assertGreater(same - different, 0.2)

    def test_compare_scores_each_view_and_can_flip_a_reference(self):
        objects = _lopsided_model()
        references = {}
        for view in ("front", "side", "top"):
            path = os.path.join(self.dir, f"{view}.png")
            found = silhouette.mask(objects, view, 160)
            pixels = np.zeros((160, 160, 4), dtype=np.float32)
            pixels[found] = (1, 1, 1, 1)
            utils.save_png(path, pixels)
            references[view] = path
        scores = silhouette.compare(objects, references)
        self.assertEqual(set(scores), {"front", "side", "top"})
        for view, score in scores.items():
            self.assertGreater(score, 0.97, view)

        flipped = silhouette.compare(objects, {"side": references["side"]}, flip=("side",))
        self.assertLess(flipped["side"], scores["side"])


if __name__ == "__main__":
    unittest.main()
