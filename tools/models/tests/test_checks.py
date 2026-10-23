"""Tests of itera_models.checks, run inside Blender (tests/run.py)."""

import unittest

import bmesh
import bpy
import scene_kit
from itera_models import checks
from mathutils import Matrix


def found(sentences, text):
    """The sentences that contain the text."""
    return [sentence for sentence in sentences if text in sentence]


def stacked_bones(names, length: float) -> tuple:
    """Bones that stand one on top of the next, each `length` metres long."""
    return tuple((name, (0, 0, i * length), (0, 0, (i + 1) * length)) for i, name in enumerate(names))


class CheckTests(unittest.TestCase):
    def setUp(self):
        scene_kit.reset()

    def test_clean_cube_has_no_problems(self):
        scene_kit.add_cube()
        result = checks.scene()
        self.assertEqual([], result["problems"])
        self.assertEqual(12, result["triangles"])
        self.assertEqual(8, result["vertices"])
        self.assertEqual(0, result["meshes"][0]["non_manifold_edges"])

    def test_triangles_are_counted_after_modifiers(self):
        cube = scene_kit.add_cube()
        cube.modifiers.new("Array", "ARRAY").count = 3
        self.assertEqual(36, checks.scene()["triangles"])

    def test_unapplied_scale_is_a_problem(self):
        cube = scene_kit.add_cube()
        cube.scale = (2.0, 1.0, 1.0)
        self.assertTrue(found(checks.scene()["problems"], "scale"))

    def test_open_edges_warn_and_a_loose_vertex_and_empty_slot_are_problems(self):
        bpy.ops.mesh.primitive_plane_add()
        plane = bpy.context.object
        builder = bmesh.new()
        builder.from_mesh(plane.data)
        builder.verts.new((3.0, 3.0, 0.0))
        builder.to_mesh(plane.data)
        builder.free()
        plane.data.materials.append(None)
        result = checks.scene()
        problems = result["problems"]
        self.assertTrue(found(result["warnings"], "open edges"))
        self.assertFalse(found(problems, "non-manifold"))
        self.assertTrue(found(problems, "loose vertices"))
        self.assertTrue(found(problems, "material slot 0 is empty"))

    def test_an_edge_of_three_faces_is_a_problem(self):
        bpy.ops.mesh.primitive_plane_add()
        plane = bpy.context.object
        builder = bmesh.new()
        builder.from_mesh(plane.data)
        builder.edges.ensure_lookup_table()
        edge = builder.edges[0]
        a, b = edge.verts
        # The plane's face and two fins on one edge: three faces meet there.
        builder.faces.new((a, b, builder.verts.new((0.0, 0.0, 1.0))))
        builder.faces.new((a, b, builder.verts.new((0.0, 0.0, -1.0))))
        builder.to_mesh(plane.data)
        builder.free()
        self.assertTrue(found(checks.scene()["problems"], "non-manifold"))

    def test_textured_material_without_uv_map_is_a_problem_but_a_flat_one_a_warning(self):
        cube = scene_kit.add_cube()
        cube.data.uv_layers.remove(cube.data.uv_layers[0])
        result = checks.scene()
        self.assertEqual([], result["problems"])
        self.assertTrue(found(result["warnings"], "no UV map"))
        texture = cube.data.materials[0].node_tree.nodes.new("ShaderNodeTexImage")
        texture.image = bpy.data.images.new("t", 2, 2)
        self.assertTrue(found(checks.scene()["problems"], "no UV map"))

    def test_detached_island_is_found_unweighted(self):
        bpy.ops.mesh.primitive_cylinder_add(vertices=24, depth=2.0, radius=0.3, location=(0, 0, 1))
        body = bpy.context.object
        body.data.materials.append(bpy.data.materials.new("Skin"))
        builder = bmesh.new()
        builder.from_mesh(body.data)
        bmesh.ops.create_cube(builder, size=0.1, matrix=Matrix.Translation((2.0, 0.0, 0.0)))
        builder.to_mesh(body.data)
        builder.free()
        rig = scene_kit.add_armature(bones=(("lower", (0, 0, 0), (0, 0, 1)), ("upper", (0, 0, 1), (0, 0, 2))))
        bpy.ops.object.select_all(action="DESELECT")
        body.select_set(True)
        rig.select_set(True)
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.parent_set(type="ARMATURE_AUTO")
        result = checks.scene()
        message = found(result["problems"], "weigh no bone")
        self.assertTrue(message, result["problems"])
        entry = next(entry for entry in result["meshes"] if entry["name"] == body.name)
        self.assertEqual(8, entry["unweighted_vertices"])

    def test_five_influences_are_a_problem(self):
        cube = scene_kit.add_cube()
        names = [f"bone{i}" for i in range(5)]
        rig = scene_kit.add_armature(bones=stacked_bones(names, 0.2))
        scene_kit.skin_to(cube, rig, {n: {i: 0.2 for i in range(8)} for n in names})
        result = checks.scene()
        self.assertTrue(found(result["problems"], "more than 4"))
        self.assertEqual([], found(result["problems"], "weigh no bone"))

    def test_four_influences_summing_to_one_are_clean(self):
        cube = scene_kit.add_cube()
        names = [f"bone{i}" for i in range(4)]
        rig = scene_kit.add_armature(bones=stacked_bones(names, 0.25))
        scene_kit.skin_to(cube, rig, {n: {i: 0.25 for i in range(8)} for n in names})
        result = checks.scene()
        self.assertEqual([], result["problems"])
        self.assertEqual([], result["warnings"])
        self.assertEqual(4, result["armatures"][0]["bones"])
        self.assertEqual(4, result["armatures"][0]["deform_bones"])

    def test_weight_on_a_group_that_names_no_bone_is_a_problem(self):
        cube = scene_kit.add_cube()
        rig = scene_kit.add_armature()
        scene_kit.skin_to(cube, rig, {"root": {i: 0.5 for i in range(8)}, "typo": {i: 0.5 for i in range(8)}})
        result = checks.scene()
        self.assertTrue(found(result["problems"], "name no bone"))
        self.assertTrue(found(result["warnings"], "do not sum to 1"))

    def test_actions_report_their_slot_and_frame_range(self):
        cube = scene_kit.add_cube()
        rig = scene_kit.add_armature()
        scene_kit.skin_to(cube, rig, {"root": {i: 1.0 for i in range(8)}})
        scene_kit.key_clip(rig, "Wave", frames=(3, 20))
        bpy.data.actions.new("Empty")
        result = checks.scene()
        by_name = {entry["name"]: entry for entry in result["actions"]}
        self.assertTrue(by_name["Wave"]["has_slot"])
        self.assertEqual((3.0, 20.0), (by_name["Wave"]["frame_start"], by_name["Wave"]["frame_end"]))
        self.assertFalse(by_name["Empty"]["has_slot"])
        self.assertTrue(found(result["warnings"], "action Empty"))

    def test_a_mesh_parented_to_a_bone_is_a_rigid_part_that_needs_no_weights(self):
        rig = scene_kit.add_armature()
        cube = scene_kit.add_cube(location=(0.0, 0.0, 1.2))
        cube.parent = rig
        cube.parent_type = "BONE"
        cube.parent_bone = "top"
        result = checks.scene()
        entry = next(entry for entry in result["meshes"] if entry["name"] == cube.name)
        self.assertFalse(entry["skinned"])
        self.assertFalse(found(result["problems"], "weigh no bone"))

    def test_two_armatures_are_a_problem(self):
        scene_kit.add_armature(name="A")
        scene_kit.add_armature(name="B")
        self.assertTrue(found(checks.scene()["problems"], "2 armatures"))


if __name__ == "__main__":
    unittest.main()
