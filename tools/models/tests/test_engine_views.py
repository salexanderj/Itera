"""engine_views' views and console commands, in plain Python: the set of angles framed on a model's box, and the
script that takes each picture and each pose."""

import sys
import unittest
from pathlib import Path

PLAIN_PYTHON = True

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import engine_views


class EngineViewsTests(unittest.TestCase):
    def setUp(self):
        # A box 1 m wide, 2 m tall, and 0.5 m deep, standing on the origin.
        self.bounds = engine_views.Bounds((-0.5, 0.0, -0.25), (0.5, 2.0, 0.25))

    def test_the_set_is_four_sides_and_the_top_orthographic_and_a_three_quarter_view(self):
        views = engine_views.views(self.bounds)
        self.assertEqual(["front", "back", "left", "right", "top", "three-quarter"], [view.name for view in views])
        self.assertTrue(all(view.ortho_height for view in views[:5]))
        self.assertIsNone(views[5].ortho_height)

    def test_each_side_view_looks_at_the_box_from_its_side_with_room_round_it(self):
        views = {view.name: view for view in engine_views.views(self.bounds)}
        front = [float(value) for value in views["front"].viewpoint.split(",")]
        left = [float(value) for value in views["left"].viewpoint.split(",")]
        # The front looks along -z from +z at the box's middle height; the left along -x from +x.
        self.assertEqual((0.0, 1.0, 0.0), (front[0], front[1], front[3]))
        self.assertGreater(front[2], 0.25)
        self.assertEqual(90.0, left[3])
        self.assertGreater(left[0], 0.5)
        # The front shows the box's height (2 m) with room; its depth starts at the viewpoint and reaches past the box.
        self.assertAlmostEqual(2.0 * 1.15, views["front"].ortho_height)
        self.assertGreater(views["front"].depth, front[2] + 0.25)

    def test_the_views_follow_the_place_the_model_stands(self):
        moved = engine_views.views(self.bounds, at=(100.0, 5.0, -40.0))[0]
        x, y, z = (float(value) for value in moved.viewpoint.split(",")[:3])
        self.assertEqual((100.0, 6.0), (x, y))
        self.assertGreater(z, -40.0)

    def test_the_script_takes_each_view_then_each_pose_and_gives_the_camera_back(self):
        out = Path("/tmp/pictures")
        views = engine_views.views(self.bounds)
        commands = engine_views.shots("crate", views, out, [("Open", 0.5)], "0 0 0 0")
        screenshots = [command for command in commands if command.startswith("screenshot")]
        self.assertEqual(7, len(screenshots))
        self.assertIn(str(out / "front.png"), screenshots[0])
        self.assertIn(str(out / "pose-Open-0.5.png"), screenshots[-1])
        self.assertIn("meshes place crate at 0 0 0 0 1 Open 0.5", commands)
        self.assertTrue(commands[0].startswith("lens ortho 2.300 0 "))
        self.assertEqual(["lens off", "viewpoint off"], commands[-2:])


if __name__ == "__main__":
    unittest.main()
