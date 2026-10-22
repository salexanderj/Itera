"""Tests for itera_models.rig."""

import os
import tempfile
import unittest

import blender_test_utils as utils
import bmesh
import bpy
import mathutils
from itera_models import rig

BONES = [
    ("b0", (0, 0, 0), (0, 0, 1), 0.0, None),
    ("b1", (0, 0, 1), (0, 0, 2), 0.0, "b0"),
    ("b2", (0, 0, 2), (0, 0, 3), 0.0, "b1"),
]


def _box(name, size, location, cuts=0):
    bpy.ops.mesh.primitive_cube_add(size=size, location=location)
    obj = bpy.context.object
    obj.name = name
    if cuts:
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.subdivide(number_cuts=cuts)
        bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def _weights_of(obj, vertex_index):
    return {obj.vertex_groups[g.group].name: g.weight for g in obj.data.vertices[vertex_index].groups}


class TransferWeightsTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self.armature = utils.make_armature("Rig", BONES)
        # The reference: a tall box skinned along three bones, with six influences on every vertex.
        self.source = _box("Reference", 1.0, (0, 0, 1.5), cuts=3)
        self.source.scale = (1, 1, 3)
        bpy.context.view_layer.update()
        for name in ("b0", "b1", "b2", "x0", "x1", "x2"):
            self.source.vertex_groups.new(name=name)
        for vertex in self.source.data.vertices:
            height = self.source.matrix_world @ vertex.co
            z = height.z
            weights = {
                "b0": max(0.0, 1.0 - abs(z - 0.5)),
                "b1": max(0.0, 1.0 - abs(z - 1.5)),
                "b2": max(0.0, 1.0 - abs(z - 2.5)),
                "x0": 0.01 + 0.002 * vertex.index % 3,
                "x1": 0.02,
                "x2": 0.03,
            }
            for group_name, weight in weights.items():
                if weight > 0:
                    self.source.vertex_groups[group_name].add([vertex.index], weight, "REPLACE")
        modifier = self.source.modifiers.new("Armature", "ARMATURE")
        modifier.object = self.armature
        # A larger, denser body around the same place.
        self.target = _box("Body", 1.4, (0, 0, 1.5), cuts=5)
        self.target.scale = (1.2, 1.2, 2.8)

    def test_no_vertex_is_left_unweighted(self):
        weighted, unweighted = rig.transfer_weights(self.target, self.source, self.armature)
        self.assertEqual(unweighted, 0)
        self.assertEqual(weighted, len(self.target.data.vertices))
        self.assertEqual(utils_mode(), "OBJECT")

    def test_limits_influences_and_normalises(self):
        rig.transfer_weights(self.target, self.source, self.armature, max_influences=2)
        stats = rig.weight_stats(self.target)
        self.assertLessEqual(stats["max_influences"], 2)
        for vertex in self.target.data.vertices:
            self.assertAlmostEqual(sum(g.weight for g in vertex.groups), 1.0, places=4)

    def test_drops_groups_that_are_not_bones_and_follows_the_height(self):
        rig.transfer_weights(self.target, self.source, self.armature)
        self.assertEqual({g.name for g in self.target.vertex_groups}, {"b0", "b1", "b2"})
        lowest = min(self.target.data.vertices, key=lambda v: v.co.z)
        highest = max(self.target.data.vertices, key=lambda v: v.co.z)
        low, high = _weights_of(self.target, lowest.index), _weights_of(self.target, highest.index)
        self.assertGreater(low.get("b0", 0), low.get("b2", 0))
        self.assertGreater(high.get("b2", 0), high.get("b0", 0))

    def test_binds_to_the_armature_without_moving_the_mesh(self):
        before = [v.co.copy() for v in self.target.data.vertices]
        rig.transfer_weights(self.target, self.source, self.armature)
        modifiers = [m for m in self.target.modifiers if m.type == "ARMATURE"]
        self.assertEqual(len(modifiers), 1)
        self.assertIs(modifiers[0].object, self.armature)
        self.assertIs(self.target.parent, self.armature)
        self.assertEqual(len(self.target.modifiers), 1, "the transfer modifier must be applied away")
        after = [v.co for v in self.target.data.vertices]
        self.assertEqual(len(before), len(after))
        self.assertLess(max((a - b).length for a, b in zip(before, after)), 1e-5)
        self.assertLess((self.target.matrix_world.translation - mathutils.Vector((0, 0, 1.5))).length, 1e-5)

    def test_the_source_rig_pose_is_restored(self):
        self.armature.data.pose_position = "POSE"
        rig.transfer_weights(self.target, self.source, self.armature)
        self.assertEqual(self.armature.data.pose_position, "POSE")


class AutoWeightsTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self.armature = utils.make_armature("Rig", BONES)

    def _pillar(self):
        bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=0.3, depth=3.0, location=(0, 0, 1.5))
        obj = bpy.context.object
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.subdivide(number_cuts=5)
        bpy.ops.object.mode_set(mode="OBJECT")
        return obj

    def test_a_clean_mesh_gets_weights_everywhere(self):
        pillar = self._pillar()
        unweighted = rig.auto_weights(pillar, self.armature)
        self.assertEqual(unweighted, 0)
        self.assertEqual(rig.weight_stats(pillar)["vertices"], len(pillar.data.vertices))
        self.assertLessEqual(rig.weight_stats(pillar)["max_influences"], 4)
        self.assertIs(pillar.parent, self.armature)

    def test_a_detached_island_is_reported(self):
        pillar = self._pillar()
        bm = bmesh.new()
        bm.from_mesh(pillar.data)
        bmesh.ops.create_cube(bm, size=0.1, matrix=mathutils.Matrix.Translation((20.0, 0, 1.5)))
        bm.to_mesh(pillar.data)
        bm.free()
        unweighted = rig.auto_weights(pillar, self.armature)
        self.assertGreater(unweighted, 0)
        self.assertLessEqual(unweighted, 8)


class RigidSkinTests(unittest.TestCase):
    def setUp(self):
        utils.reset_scene()
        self._tmp = tempfile.TemporaryDirectory()
        self.armature = utils.make_armature("Rig", BONES)
        self.lower = _box("Lower", 0.5, (0, 0, 0.5))
        self.middle = _box("Middle", 0.5, (0, 0, 1.5))
        self.upper = _box("Upper", 0.5, (0, 0, 2.5))
        self.parts = {self.lower: "b0", self.middle: "b1", self.upper: "b2"}

    def tearDown(self):
        self._tmp.cleanup()

    def test_joins_parts_and_weights_each_to_its_bone(self):
        joined = rig.rigid_skin(self.parts, self.armature, name="Skinned")
        self.assertEqual(joined.name, "Skinned")
        self.assertEqual(len(joined.data.vertices), 24)
        self.assertEqual(len([o for o in bpy.data.objects if o.type == "MESH"]), 1)
        for vertex in joined.data.vertices:
            groups = _weights_of(joined, vertex.index)
            self.assertEqual(groups, {_band((joined.matrix_world @ vertex.co).z): 1.0})
        self.assertEqual(rig.weight_stats(joined)["unweighted"], 0)
        self.assertIs(joined.parent, self.armature)

    def test_a_single_part_works(self):
        joined = rig.rigid_skin({self.lower: "b0"}, self.armature)
        self.assertIs(joined, self.lower)
        self.assertEqual(rig.weight_stats(joined)["max_influences"], 1)

    def test_rejects_an_unknown_bone(self):
        with self.assertRaises(ValueError) as caught:
            rig.rigid_skin({self.lower: "tail"}, self.armature)
        self.assertIn("tail", str(caught.exception))
        self.assertEqual(len([o for o in bpy.data.objects if o.type == "MESH"]), 3)

    def test_exports_as_one_skin_with_all_the_joints(self):
        joined = rig.rigid_skin(self.parts, self.armature)
        glb = utils.export_glb(os.path.join(self._tmp.name, "rigid.glb"), [joined, self.armature])
        self.assertEqual(len(glb["skins"]), 1)
        self.assertEqual({glb["nodes"][j]["name"] for j in glb["skins"][0]["joints"]}, {"b0", "b1", "b2"})
        attributes = glb["meshes"][0]["primitives"][0]["attributes"]
        self.assertIn("JOINTS_0", attributes)
        self.assertIn("WEIGHTS_0", attributes)

    def test_import_reference_returns_the_armature_and_meshes(self):
        joined = rig.rigid_skin(self.parts, self.armature, name="Skinned")
        path = os.path.join(self._tmp.name, "ref.glb")
        utils.export_glb(path, [joined, self.armature])
        utils.reset_scene()
        armature, meshes = rig.import_reference(path)
        self.assertEqual(armature.type, "ARMATURE")
        self.assertEqual({b.name for b in armature.data.bones}, {"b0", "b1", "b2"})
        self.assertEqual([m.type for m in meshes], ["MESH"])
        self.assertEqual({g.name for g in meshes[0].vertex_groups}, {"b0", "b1", "b2"})


def _band(z: float) -> str:
    return "b0" if z < 1.0 else "b1" if z < 2.0 else "b2"


def utils_mode() -> str:
    active = bpy.context.view_layer.objects.active
    return active.mode if active is not None else "OBJECT"


if __name__ == "__main__":
    unittest.main()
