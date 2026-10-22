"""Tests of itera_models.views, run inside Blender (tests/run.py)."""

import struct
import tempfile
import time
import unittest
from pathlib import Path

import bpy
import numpy as np
import scene_kit
from itera_models import views

SIZE = 192
MOST_SECONDS_FOR_A_SHEET = 6.0


def png_size(path) -> tuple[int, int]:
    """The width and height in the header of a PNG file."""
    data = Path(path).read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a PNG file"
    return struct.unpack(">II", data[16:24])


def counts() -> dict:
    """How many of each kind of datablock the file has, to see that a render leaves none behind."""
    result = {
        name: len(getattr(bpy.data, name))
        for name in ("objects", "meshes", "materials", "cameras", "lights", "curves", "worlds", "scenes")
    }
    # Blender keeps its own "Render Result" image after any render.
    result["images"] = len([image for image in bpy.data.images if image.name != "Render Result"])
    return result


class ViewTests(unittest.TestCase):
    def setUp(self):
        scene_kit.reset()
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / "sheet.png"

    def make_rigged_cube(self):
        cube = scene_kit.add_cube()
        rig = scene_kit.add_armature()
        scene_kit.skin_to(cube, rig, {"root": {i: 1.0 for i in range(8)}})
        scene_kit.key_clip(rig, "Wave", angle=1.0, frames=(1, 25))
        return cube, rig

    def test_sheet_is_a_png_of_the_expected_size(self):
        scene_kit.add_cube()
        started = time.perf_counter()
        result = views.sheet(self.path, size=SIZE)
        seconds = time.perf_counter() - started
        self.assertEqual(self.path, result)
        self.assertEqual(views.sheet_size(SIZE), png_size(self.path))
        self.assertLess(seconds, MOST_SECONDS_FOR_A_SHEET)

    def test_sheet_shows_the_model_and_leaves_the_scene_as_it_was(self):
        cube = scene_kit.add_cube()
        scene = bpy.context.scene
        engine, camera, frame = scene.render.engine, scene.camera, scene.frame_current
        before = counts()
        views.sheet(self.path, size=SIZE)
        self.assertEqual(before, counts())
        self.assertEqual((engine, camera, frame), (scene.render.engine, scene.camera, scene.frame_current))
        self.assertFalse(cube.hide_render)
        self.assertEqual([], [obj.name for obj in scene.objects if obj is not cube])
        image = bpy.data.images.load(str(self.path))
        try:
            pixels = np.array(image.pixels[:], dtype=np.float32).reshape(image.size[1], image.size[0], 4)
        finally:
            bpy.data.images.remove(image)
        # The sheet is not one flat colour: it has a model, wireframes, and text in it.
        self.assertGreater(float(pixels[:, :, :3].std()), 0.05)
        self.assertGreater(len(np.unique(pixels[:, :, 0].round(2))), 8)

    def test_a_wide_model_is_framed_in_every_view(self):
        cube = scene_kit.add_cube()
        cube.dimensions = (4.0, 0.5, 0.5)
        bpy.context.view_layer.update()
        views.sheet(self.path, size=SIZE)
        self.assertEqual(views.sheet_size(SIZE), png_size(self.path))

    def test_posed_sheet_sets_the_action_and_puts_it_back(self):
        _, rig = self.make_rigged_cube()
        rig.animation_data.action = None
        scene = bpy.context.scene
        frame = scene.frame_current
        started = time.perf_counter()
        views.posed(self.path, rig, "Wave", 0.5, size=SIZE)
        seconds = time.perf_counter() - started
        self.assertEqual(views.sheet_size(SIZE), png_size(self.path))
        self.assertIsNone(rig.animation_data.action)
        self.assertEqual(frame, scene.frame_current)
        self.assertLess(seconds, MOST_SECONDS_FOR_A_SHEET)

    def test_unknown_clip_is_refused(self):
        _, rig = self.make_rigged_cube()
        with self.assertRaises(ValueError):
            views.posed(self.path, rig, "Nope", 0.0, size=SIZE)

    def test_eevee_sheet_is_made_too(self):
        scene_kit.add_cube()
        views.sheet(self.path, size=SIZE, engine="BLENDER_EEVEE")
        self.assertEqual(views.sheet_size(SIZE), png_size(self.path))

    def test_no_meshes_is_refused(self):
        scene_kit.add_armature()
        with self.assertRaises(ValueError):
            views.sheet(self.path, size=SIZE)


if __name__ == "__main__":
    unittest.main()
