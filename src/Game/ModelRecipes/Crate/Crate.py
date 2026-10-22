"""A weathered wooden crate: the starter's sample of a model built by a recipe (tools/models/README.md).

A box 0.8 m a side inside a frame of battens along its edges. Its wood is Poly Haven's CC0 "wood_planks", projected by
position so the grain keeps one scale on every face, darker in its corners and creases, and baked into one material of
512-texel maps. A named point, "top", marks the middle of its lid, where another crate stacks.

Build it with: tools/models/build.py build src/Game/ModelRecipes/Crate/Crate.py
"""

import bmesh
import bpy
from itera_models import materials, points
from mathutils import Matrix, Vector

NAME = "Crate"
CLASS = "prop"

SIZE = 0.8  # metres a side, battens included
BATTEN = 0.07  # metres across a batten
TEXELS = 512
WOOD_METRES = 1.5  # the photographed width of wood_planks
WOOD_HASHES = {
    "diff": "2d079a0bea287e2b167eaec78360932aa822e970b4b9650c7c4f55bbdf36a91f",
    "rough": "f1eec48d61e647be0274012318137d9093ac94551d59b082414cb26c2f4da66d",
    "ao": "91657d97fc1b9bda1e95886605ac3a0b1339865c0eed92cddbd540fcf877625f",
}


def build(ctx):
    crate = _crate_mesh()
    wood = materials.pbr_from_maps(
        "wood", ctx.polyhaven("wood_planks", "1k", maps=("diff", "rough", "ao"), hashes=WOOD_HASHES)
    )
    _project_by_position(wood, WOOD_METRES)
    materials.grime(wood, amount=0.45)
    crate.data.materials.append(wood)
    materials.bake(crate, size=TEXELS, maps=("color", "roughness", "ao"), out_dir=ctx.work_dir / "bake")
    points.add_point("top", (0.0, 0.0, SIZE))


def _crate_mesh():
    """The inner box and twelve battens, each a closed box of its own, in one mesh standing on the origin."""
    builder = bmesh.new()
    half = SIZE / 2
    inner = SIZE - BATTEN
    _box(builder, Vector((0, 0, half)), Vector((inner, inner, inner)))
    edge = half - BATTEN / 2
    for a in (-edge, edge):
        for b in (-edge, edge):
            _box(builder, Vector((a, b, half)), Vector((BATTEN, BATTEN, SIZE)))  # upright
            _box(builder, Vector((a, 0, half + b)), Vector((BATTEN, SIZE, BATTEN)))  # along y
            _box(builder, Vector((0, a, half + b)), Vector((SIZE, BATTEN, BATTEN)))  # along x
    mesh = bpy.data.meshes.new("Crate")
    builder.to_mesh(mesh)
    builder.free()
    crate = bpy.data.objects.new("Crate", mesh)
    bpy.context.scene.collection.objects.link(crate)
    bevel = crate.modifiers.new("soft edges", "BEVEL")
    bevel.width = 0.006
    bevel.segments = 1
    bevel.limit_method = "ANGLE"
    return crate


def _box(builder, centre, size):
    matrix = Matrix.Translation(centre) @ Matrix.Diagonal((size.x, size.y, size.z, 1.0))
    bmesh.ops.create_cube(builder, size=1.0, matrix=matrix)


def _project_by_position(material, metres):
    """Reads every image of the material by the object's position, one copy every `metres`, from each side (box
    projection), so a batten and a face show the grain at one scale. The bake then writes it onto the UV map."""
    tree = material.node_tree
    coordinates = tree.nodes.new("ShaderNodeTexCoord")
    mapping = tree.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (1 / metres, 1 / metres, 1 / metres)
    tree.links.new(coordinates.outputs["Object"], mapping.inputs["Vector"])
    for node in tree.nodes:
        if node.type == "TEX_IMAGE":
            node.projection = "BOX"
            node.projection_blend = 0.15
            tree.links.new(mapping.outputs["Vector"], node.inputs["Vector"])
