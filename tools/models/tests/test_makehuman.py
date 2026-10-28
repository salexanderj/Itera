"""Tests for itera_models.makehuman, and rig.move_joints and rig.bind_weighted, on small made-up MakeHuman files."""

import gzip
import json
import math
import tempfile
import unittest
from pathlib import Path

import blender_test_utils as utils
import bpy
import numpy as np
from itera_models import makehuman, rig
from mathutils import Vector

# MakeHuman's macro sliders, as its macrodetails/macro.json has them.
MACRO_CONFIG = {
    "macrotargets": {
        "gender": {"parts": [{"lowest": -0.01, "highest": 1.01, "low": "female", "high": "male"}]},
        "age": {
            "parts": [
                {"lowest": -0.01, "highest": 0.1874998, "low": "baby", "high": "child"},
                {"lowest": 0.1874999, "highest": 0.49998, "low": "child", "high": "young"},
                {"lowest": 0.49999, "highest": 1.01, "low": "young", "high": "old"},
            ]
        },
        "muscle": {
            "parts": [
                {"lowest": -0.01, "highest": 0.49998, "low": "minmuscle", "high": "averagemuscle"},
                {"lowest": 0.49999, "highest": 1.01, "low": "averagemuscle", "high": "maxmuscle"},
            ]
        },
        "weight": {
            "parts": [
                {"lowest": -0.01, "highest": 0.49998, "low": "minweight", "high": "averageweight"},
                {"lowest": 0.49999, "highest": 1.01, "low": "averageweight", "high": "maxweight"},
            ]
        },
        "proportions": {
            "parts": [
                {"lowest": -0.01, "highest": 0.4999, "low": "uncommonproportions", "high": ""},
                {"lowest": 0.50, "highest": 1.01, "low": "", "high": "idealproportions"},
            ]
        },
        "height": {
            "parts": [
                {"lowest": -0.01, "highest": 0.49, "low": "minheight", "high": ""},
                {"lowest": 0.51, "highest": 1.01, "low": "", "high": "maxheight"},
            ]
        },
        "cupsize": {
            "parts": [
                {"lowest": -0.01, "highest": 0.49998, "low": "mincup", "high": "averagecup"},
                {"lowest": 0.49999, "highest": 1.01, "low": "averagecup", "high": "maxcup"},
            ]
        },
        "firmness": {
            "parts": [
                {"lowest": -0.01, "highest": 0.4998, "low": "minfirmness", "high": "averagefirmness"},
                {"lowest": 0.4999, "highest": 1.01, "low": "averagefirmness", "high": "maxfirmness"},
            ]
        },
    }
}

# A body of three vertices along an arm hanging 45 degrees down from a shoulder (MakeHuman's axes and decimetres), its
# joint cubes three vertices each, an eye of one triangle, and a ground cube.
OBJ = """\
v 2.5 7.5 0
v 7.5 2.5 0
v 2.5 7.5 1
v -0.1 10 0
v 0.1 10 0
v 0 10 0.3
v 4.9 5 0
v 5.1 5 0
v 5 5 0.3
v 9.9 0 0
v 10.1 0 0
v 10 0 0.3
v -0.1 -1 0
v 0.1 -1 0
v 0 -1 0.3
v 0 9 1
v 0.1 9 1
v 0 9.1 1
vt 0 0
vt 1 0
vt 0 1
g body
f 1/1 2/2 3/3
g joint-a
f 4/1 5/2 6/3
g joint-b
f 7/1 8/2 9/3
g joint-c
f 10/1 11/2 12/3
g joint-ground
f 13/1 14/2 15/3
g helper-eye
f 16/1 17/2 18/3
"""

RIG = {
    "Root": {
        "head": {"strategy": "CUBE", "cube_name": "joint-ground"},
        "tail": {"strategy": "CUBE", "cube_name": "joint-a"},
        "parent": "",
    },
    "upper": {
        "head": {"strategy": "CUBE", "cube_name": "joint-a"},
        "tail": {"strategy": "CUBE", "cube_name": "joint-b"},
        "parent": "Root",
    },
    "lower": {
        "head": {"strategy": "CUBE", "cube_name": "joint-b"},
        "tail": {"strategy": "CUBE", "cube_name": "joint-c"},
        "parent": "upper",
    },
}

WEIGHTS = {"weights": {"Root": [], "upper": [[0, 1.0], [2, 1.0], [15, 1.0], [16, 1.0], [17, 1.0]], "lower": [[1, 1.0]]}}


class _Ctx:
    """A recipe's context that fetches from a folder by the URL's path under MPFB2's data folder; a target it lacks is
    empty."""

    def __init__(self, folder: Path):
        self.folder = folder
        self.fetched = []

    def fetch(self, url, sha256=None, name=None):
        path = url.split("/src/mpfb/data/", 1)[1]
        self.fetched.append(path)
        local = self.folder / path
        if not local.exists():
            local.parent.mkdir(parents=True, exist_ok=True)
            with gzip.open(local, "wt", encoding="utf-8") as file:
                file.write("")
        return local


def _write_data(folder: Path):
    (folder / "3dobjs").mkdir(parents=True)
    (folder / "3dobjs" / "base.obj").write_text(OBJ, encoding="utf-8")
    (folder / "targets" / "macrodetails").mkdir(parents=True)
    (folder / "targets" / "macrodetails" / "macro.json").write_text(json.dumps(MACRO_CONFIG), encoding="utf-8")
    (folder / "rigs" / "standard").mkdir(parents=True)
    (folder / "rigs" / "standard" / "rig.game_engine.json").write_text(json.dumps(RIG), encoding="utf-8")
    (folder / "rigs" / "standard" / "weights.game_engine.json").write_text(json.dumps(WEIGHTS), encoding="utf-8")
    (folder / "targets" / "test").mkdir(parents=True)
    with gzip.open(folder / "targets" / "test" / "lift.target.gz", "wt", encoding="utf-8") as file:
        file.write("# a made-up target\n0 0 1 0\n")


class MacroStackTests(unittest.TestCase):
    def test_a_man_of_average_build_blends_his_race_and_universal_targets(self):
        macros = {**makehuman.MACROS, "gender": 1.0, "race": {"caucasian": 1.0}}
        stack = dict(makehuman.macro_stack(MACRO_CONFIG, macros))
        # The gender's female share (under 1 %) and the height and proportions in their middles add nothing.
        self.assertEqual(
            {"macrodetails/caucasian-male-young", "macrodetails/universal-male-young-averagemuscle-averageweight"},
            set(stack),
        )
        self.assertAlmostEqual(0.9902, stack["macrodetails/caucasian-male-young"], places=3)

    def test_a_womans_breasts_blend_by_cup_and_firmness_without_her_gender(self):
        macros = {**makehuman.MACROS, "gender": 0.0, "cupsize": 1.0, "race": {"african": 1.0}}
        stack = dict(makehuman.macro_stack(MACRO_CONFIG, macros))
        name = "breast/female-young-averagemuscle-averageweight-maxcup-averagefirmness"
        self.assertIn(name, stack)
        self.assertNotIn("breast/female-young-averagemuscle-averageweight-averagecup-averagefirmness", stack)
        # Its cup's share (1 - 0.49999) / 0.51001 and its firmness's nearly 1, with no share for her gender.
        self.assertAlmostEqual(0.9802, stack[name], places=3)


class BodyTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self.folder = Path(tempfile.mkdtemp())
        _write_data(self.folder)
        self.ctx = _Ctx(self.folder)

    def test_a_target_moves_its_vertices_and_the_body_turns_into_blenders_axes_on_the_ground(self):
        body = makehuman.Body(self.ctx)
        before = body.coords[0].copy()

        body.shape({}, {"test/lift": 0.5})

        self.assertTrue(np.allclose(body.coords[0], before + [0, 0.5, 0]))
        self.assertIn("targets/test/lift.target.gz", self.ctx.fetched)
        coords = body.blender_coords()
        # MakeHuman's (2.5, 8.0, 0) decimetres, its z the depth; the body's lowest vertex (y 2.5) on the ground.
        self.assertTrue(np.allclose(coords[0], [0.25, 0.0, 0.55]))
        self.assertAlmostEqual(0.55, body.height(coords), places=6)

    def test_its_joints_stand_in_their_cubes_middles_and_its_mesh_keeps_its_groups_uvs_and_weights(self):
        body = makehuman.Body(self.ctx)
        coords = makehuman.to_blender(body.coords)

        heads, tails = body.joints(coords)
        obj = body.mesh(
            "made", coords, ("body", "helper-eye"), {"upper": "arm"}, bones={"arm", "lower"}, marks=("helper-eye",)
        )

        self.assertTrue(np.allclose(heads["upper"], [0.0, -0.01, 1.0]))
        self.assertTrue(np.allclose(tails["lower"], [1.0, -0.01, 0.0]))
        self.assertEqual(6, len(obj.data.vertices))
        self.assertEqual({"arm", "lower"}, {group.name for group in obj.vertex_groups})
        self.assertEqual("UVMap", obj.data.uv_layers.active.name)
        marked = [0.0] * 6
        obj.data.attributes["helper-eye"].data.foreach_get("value", marked)
        self.assertEqual([0, 0, 0, 1, 1, 1], marked)


class FitTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        # The body's arm hangs 45 degrees down from its shoulder at (0, 0, 1); the rig's points level along +x.
        self.heads = {"Root": Vector((0, 0, 0)), "upper": Vector((0, 0, 1)), "lower": Vector((0.5, 0, 0.5))}
        self.tails = {"Root": Vector((0, 0, 1)), "upper": Vector((0.5, 0, 0.5)), "lower": Vector((1, 0, 0))}
        self.parents = {"Root": None, "upper": "Root", "lower": "upper"}
        self.rest = {
            "Root": Vector((0, 0, 0)),
            "upper": Vector((0, 0, 1.2)),
            "lower": Vector((0.4, 0, 1.2)),
            "leaf": Vector((0.9, 0, 1.2)),
        }

    def test_each_bone_lies_along_the_rigs_and_keeps_its_own_length_from_where_its_parent_put_it(self):
        transforms, places = makehuman.fit(
            self.heads, self.tails, self.parents, self.rest, {"upper": "lower"}, {"lower": "leaf"}
        )

        arm = math.sqrt(0.5)
        self.assertTrue((places["upper"] - Vector((0, 0, 1))).length < 1e-6)
        self.assertTrue((places["lower"] - Vector((arm, 0, 1))).length < 1e-6)
        self.assertTrue((places["leaf"] - Vector((2 * arm, 0, 1))).length < 1e-6)
        moved = makehuman.posed(np.array([[0.25, 0, 0.75]]), {"upper": [(0, 1.0)]}, transforms)
        self.assertTrue(np.allclose(moved[0], [arm / 2, 0, 1]))

    def test_a_bone_of_shares_takes_only_that_share_of_its_turn(self):
        _, places = makehuman.fit(
            self.heads,
            self.tails,
            self.parents,
            self.rest,
            {"upper": "lower"},
            {"lower": "leaf"},
            shares={"upper": 0.5},
        )

        way = (places["lower"] - places["upper"]).normalized()
        self.assertAlmostEqual(-math.sin(math.radians(22.5)), way.z, places=5)


class MoveJointsTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self.armature = utils.make_armature(
            "Rig",
            [
                ("hip", (0, 0, 1), (0, 0, 1.3), 0.4, None),
                ("knee", (0, 0, 1.3), (0, 0.2, 1.5), 0.1, "hip"),
            ],
        )

    def test_joints_move_to_their_places_keeping_each_bones_turn(self):
        turns = {bone.name: bone.matrix_local.to_quaternion() for bone in self.armature.data.bones}

        unmoved = rig.move_joints(self.armature, {"knee": Vector((0.1, 0, 1.4))})

        self.assertEqual(["hip"], unmoved)
        knee = self.armature.data.bones["knee"]
        self.assertTrue((knee.head_local - Vector((0.1, 0, 1.4))).length < 1e-6)
        for bone in self.armature.data.bones:
            self.assertGreater(abs(bone.matrix_local.to_quaternion().dot(turns[bone.name])), 1 - 1e-6)

    def test_a_weighted_mesh_binds_with_its_groups_on_bones_alone_four_at_most(self):
        bpy.ops.mesh.primitive_plane_add(size=0.2, location=(0, 0, 1.2))
        mesh = bpy.context.object
        for name in ("hip", "knee", "x", "y", "z"):
            group = mesh.vertex_groups.new(name=name)
            group.add([0, 1, 2, 3], 0.2, "REPLACE")

        stats = rig.bind_weighted(mesh, self.armature)

        self.assertEqual({"hip", "knee"}, {group.name for group in mesh.vertex_groups})
        self.assertEqual(0, stats["unweighted"])
        self.assertEqual(self.armature, mesh.parent)
        self.assertTrue(any(modifier.type == "ARMATURE" for modifier in mesh.modifiers))


if __name__ == "__main__":
    unittest.main()
