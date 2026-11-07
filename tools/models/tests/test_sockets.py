"""Tests for itera_models.sockets."""

import unittest

import blender_test_utils as utils
import bpy
from itera_models import rig, sockets
from itera_models.sockets import Station
from mathutils import Vector

BONES = [
    ("b0", (0, 0, 0), (0, 0, 1), 0.0, None),
    ("b1", (0, 0, 1), (0, 0, 2), 0.0, "b0"),
]
POINTS = 8


def _rings(heights, radius=0.2):
    stations = [Station((0, 0, z), (0, 0, 1), radius, radius, radius) for z in heights]
    return [sockets.ring(s, f, POINTS) for s, f in zip(stations, sockets.frames(stations, (0, -1, 0)))]


def _weigh(obj, weights_of_vertex):
    for vertex in obj.data.vertices:
        for name, weight in weights_of_vertex(vertex).items():
            group = obj.vertex_groups.get(name) or obj.vertex_groups.new(name=name)
            group.add([vertex.index], weight, "REPLACE")


def _socket_weights(vertex):
    return {"b0": 0.5, "b1": 0.5} if abs(vertex.co.z - 1.0) < 1e-6 else None


class TwoTubes:
    """A lower tube on b0, open at its top, and an upper one on b1 that starts from its top loop and is capped."""

    def make(self, shift=0.0, socket_on_b1_alone=False):
        utils.reset_scene()
        self.armature = utils.make_armature("Rig", BONES)
        lower_rings = _rings([0.0, 0.5, 1.0])
        self.lower = sockets.loft("lower", lower_rings, cap_start=Vector((0, 0, -0.1)))
        top = lower_rings[-1]
        upper_rings = _rings([1.0, 1.5, 2.0])
        order = sockets.align(top, upper_rings[0])
        upper_rings[0] = [top[i] + Vector((shift, 0, 0)) for i in order]
        self.upper = sockets.loft("upper", upper_rings, cap_end=Vector((0, 0, 2.1)))
        _weigh(self.lower, lambda v: _socket_weights(v) or {"b0": 1.0})
        if socket_on_b1_alone:
            _weigh(self.upper, lambda v: {"b1": 1.0})
        else:
            _weigh(self.upper, lambda v: _socket_weights(v) or {"b1": 1.0})
        for part in (self.lower, self.upper):
            rig.bind_weighted(part, self.armature)
        return self


class CheckTests(unittest.TestCase):
    def test_parts_that_share_a_loop_and_its_weights_join_with_no_problem(self):
        tubes = TwoTubes().make()
        report = sockets.check([tubes.lower, tubes.upper])
        self.assertEqual(report["problems"], [])
        self.assertEqual(report["joins"], [("lower", "upper", POINTS)])

    def test_a_loop_moved_off_its_partner_meets_no_other_part(self):
        tubes = TwoTubes().make(shift=0.01)
        problems = sockets.check([tubes.lower, tubes.upper])["problems"]
        self.assertEqual(len(problems), 2, problems)
        self.assertTrue(all("meets no other part" in problem for problem in problems), problems)

    def test_a_socket_whose_sides_weigh_different_bones_is_a_problem(self):
        tubes = TwoTubes().make(socket_on_b1_alone=True)
        problems = sockets.check([tubes.lower, tubes.upper])["problems"]
        self.assertTrue(any("weights differ by up to 0.500" in problem for problem in problems), problems)


class LoftTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()

    def test_a_loft_has_one_open_loop_for_each_end_without_a_cap(self):
        tube = sockets.loft("tube", _rings([0.0, 1.0, 2.0]), cap_end=Vector((0, 0, 2.2)))
        loops = sockets.boundary_loops(tube)
        self.assertEqual(len(loops), 1)
        self.assertEqual(sorted(loops[0]), list(range(POINTS)))

    def test_a_lofts_faces_look_outward(self):
        tube = sockets.loft("tube", _rings([0.0, 1.0]), Vector((0, 0, -0.2)), Vector((0, 0, 1.2)))
        for polygon in tube.data.polygons:
            middle = polygon.center
            outward = middle - Vector((0, 0, middle.z)) if 0.0 < middle.z < 1.0 else Vector((0, 0, middle.z - 0.5))
            self.assertGreater(polygon.normal.dot(outward), 0.0, f"face {polygon.index} looks inward")

    def test_align_takes_a_loop_that_runs_the_other_way_in_the_rings_order(self):
        ring = _rings([0.0])[0]
        loop = list(reversed(ring[3:] + ring[:3]))
        order = sockets.align(loop, ring)
        self.assertTrue(all((loop[order[i]] - ring[i]).length < 1e-9 for i in range(POINTS)))

    def test_frames_turn_the_up_with_the_path_and_keep_it_across_it(self):
        stations = [Station((0, 0, 0), (0, 0, -1), 1, 1, 1), Station((0, -1, -1), (0, -1, 0), 1, 1, 1)]
        (way0, up0, _), (way1, up1, side1) = sockets.frames(stations, (0, -1, 0))
        self.assertAlmostEqual(up0.dot(way0), 0.0)
        self.assertLess((up1 - Vector((0, 0, 1))).length, 1e-6, "a forward up turns to point up as the path turns ahead")
        self.assertLess((side1 - up1.cross(way1)).length, 1e-9)


class ShareNormalsTests(unittest.TestCase):
    def test_both_sides_of_a_socket_take_the_same_normals(self):
        tubes = TwoTubes().make()
        changed = sockets.share_normals([tubes.lower, tubes.upper])
        self.assertEqual(changed, 2 * POINTS)
        found, _ = sockets.joins([tubes.lower, tubes.upper])
        obj, loop, other, matches = found[0]
        mine = _corner_normals(obj)
        theirs = _corner_normals(other)
        for index, match in zip(loop, matches):
            self.assertLess((mine[index] - theirs[match]).length, 1e-4)


def _corner_normals(obj):
    """Each vertex's custom normal, read from the first corner that uses it."""
    normals = {}
    for loop in obj.data.loops:
        normals.setdefault(loop.vertex_index, Vector(obj.data.corner_normals[loop.index].vector))
    return normals


if __name__ == "__main__":
    unittest.main()
