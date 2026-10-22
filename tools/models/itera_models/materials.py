"""Materials for recipes: PBR from image files, procedural grime, and baking to export-ready textures.

The glTF exporter writes only node patterns it recognises, so a material that goes into a model should be
`pbr(...)` (image textures into a Principled BSDF) or the result of `bake(...)`. Procedural nodes, such as
`grime`, do not export; bake them first.

What the engine draws: base colour, normal (OpenGL convention, +Y green), occlusion and emission. It reads
metalness and roughness but does not draw them yet.
"""

from __future__ import annotations

import math
import os
import time

import bpy
import numpy as np

_SRGB = "sRGB"
_DATA = "Non-Color"
_GLTF_OUTPUT_GROUP = "glTF Material Output"

#: Seconds each map of the last `bake` took, by map name, plus "unwrap" and "total".
last_bake_seconds: dict[str, float] = {}


def pbr(
    name: str,
    color=None,
    normal=None,
    roughness=None,
    ao=None,
    metal=None,
    normal_directx: bool = False,
    tiling: float = 1.0,
) -> bpy.types.Material:
    """Makes a Principled BSDF material from image files.

    Each argument is a file path, or None to leave that input alone. Base colour is read as sRGB, the others as
    data. With `normal_directx` the normal map's green channel is flipped, because glTF wants OpenGL normals
    (ambientCG ships DirectX ones). Occlusion goes into the "glTF Material Output" group, which is where the
    glTF exporter looks for it. `tiling` repeats every texture that many times across the UV square.
    """
    images = {}
    if color is not None:
        images["color"] = _load(color, _SRGB)
    if normal is not None:
        image = _load(normal, _DATA)
        images["normal"] = _flip_green(image, f"{name}_normal_gl") if normal_directx else image
    if roughness is not None:
        images["roughness"] = _load(roughness, _DATA)
    if ao is not None:
        images["ao"] = _load(ao, _DATA)
    if metal is not None:
        images["metal"] = _load(metal, _DATA)
    return _build_material(name, images, tiling)


def pbr_from_maps(name: str, maps: dict, tiling: float = 1.0) -> bpy.types.Material:
    """Makes a `pbr` material from what `fetch.polyhaven_texture` or `fetch.ambientcg_material` returned.

    Accepts the keys of both libraries: `diff` or `color`, `nor_gl` or `normal_gl`, `nor_dx` or `normal_dx`,
    `rough` or `roughness`, `ao`, `metal`. Other maps, such as displacement, are ignored.
    """

    def first(*keys):
        for key in keys:
            if key in maps:
                return maps[key]
        return None

    normal_gl = first("nor_gl", "normal_gl")
    normal_dx = first("nor_dx", "normal_dx")
    return pbr(
        name,
        color=first("diff", "color"),
        normal=normal_gl if normal_gl is not None else normal_dx,
        normal_directx=normal_gl is None and normal_dx is not None,
        roughness=first("rough", "roughness"),
        ao=first("ao"),
        metal=first("metal"),
        tiling=tiling,
    )


def grime(
    material: bpy.types.Material,
    amount: float = 0.5,
    color=(0.08, 0.06, 0.05),
    distance: float = 0.15,
) -> None:
    """Darkens the material's base colour in crevices and inside corners, for a weathered look.

    The mask is the larger of two things: ambient occlusion within `distance` metres, and concave pointiness. A
    noise texture breaks it up. `amount` (0 to 1) is how strongly the grime colour replaces the base colour in
    the darkest places. It is procedural, so it does not export; `bake` it. Ambient occlusion and pointiness
    render in Cycles only, so an EEVEE preview shows none of it, and neither does a Workbench one.
    """
    tree = material.node_tree
    nodes, links = tree.nodes, tree.links
    bsdf = _principled(material)
    base = bsdf.inputs["Base Color"]
    origin = base.links[0].from_socket if base.is_linked else None
    original_color = tuple(base.default_value)

    ao_node = nodes.new("ShaderNodeAmbientOcclusion")
    ao_node.inputs["Distance"].default_value = distance
    ao_node.samples = 8
    crease = _math(nodes, "SUBTRACT", 1.0, None)
    links.new(ao_node.outputs["AO"], crease.inputs[1])

    geometry = nodes.new("ShaderNodeNewGeometry")
    concave = nodes.new("ShaderNodeValToRGB")
    concave.color_ramp.elements[0].position = 0.38
    concave.color_ramp.elements[0].color = (1, 1, 1, 1)
    concave.color_ramp.elements[1].position = 0.5
    concave.color_ramp.elements[1].color = (0, 0, 0, 1)
    links.new(geometry.outputs["Pointiness"], concave.inputs["Fac"])

    both = _math(nodes, "MAXIMUM", None, None)
    links.new(crease.outputs["Value"], both.inputs[0])
    links.new(concave.outputs["Color"], both.inputs[1])

    noise = nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 6.0
    breakup = _math(nodes, "MULTIPLY_ADD", None, 0.4)
    breakup.inputs[2].default_value = 0.6
    links.new(noise.outputs["Fac"], breakup.inputs[0])

    masked = _math(nodes, "MULTIPLY", None, None)
    links.new(both.outputs["Value"], masked.inputs[0])
    links.new(breakup.outputs["Value"], masked.inputs[1])
    strength = _math(nodes, "MULTIPLY", None, amount)
    strength.use_clamp = True
    links.new(masked.outputs["Value"], strength.inputs[0])

    mix = nodes.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.blend_type = "MIX"
    links.new(strength.outputs["Value"], _socket(mix.inputs, "Factor_Float"))
    if origin is not None:
        links.new(origin, _socket(mix.inputs, "A_Color"))
    else:
        _socket(mix.inputs, "A_Color").default_value = original_color
    _socket(mix.inputs, "B_Color").default_value = (*color, 1.0)
    links.new(_socket(mix.outputs, "Result_Color"), base)


def bake(
    obj: bpy.types.Object,
    size: int = 1024,
    maps: tuple[str, ...] = ("color", "normal", "roughness", "ao"),
    *,
    out_dir: str | os.PathLike,
    margin: int = 8,
    samples: int = 4,
    ao_samples: int = 16,
    ao_distance: float | None = None,
) -> bpy.types.Material:
    """Bakes the object's materials into PNG textures and gives it one plain image material.

    Cycles bakes on the CPU, one pass per map, into `size` by `size` images saved in `out_dir` as
    `<object>_<map>.png`:

    - `color`: base colour without lighting (sRGB).
    - `normal`: tangent-space normal, +X red, +Y green, +Z blue. That is the OpenGL convention glTF wants. The
      material's own normal map and bump are included.
    - `roughness` and `ao`: grey images (data, not colour).

    An object with no UV map is unwrapped first (Smart UV Project, then pack). The object's material slots are
    replaced by one new material, `<object>_baked`, that the glTF exporter writes as base colour, normal,
    metallic-roughness and occlusion textures. The materials it had are left in the file, as they were.

    `samples` applies to the colour, normal and roughness passes (4 to 16 is plenty), `ao_samples` to occlusion.
    `ao_distance` is how far occlusion looks, in metres; by default a quarter of the object's largest dimension.
    The seconds each pass took are printed and kept in `last_bake_seconds`.
    """
    known = {"color", "normal", "roughness", "ao"}
    unknown = set(maps) - known
    if unknown:
        raise ValueError(f"Cannot bake {sorted(unknown)}; choose from {sorted(known)}")
    if obj.type != "MESH":
        raise ValueError(f"{obj.name} is not a mesh")

    out_dir = os.fspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    last_bake_seconds.clear()
    started = time.perf_counter()

    _ensure_mode_object()
    if not obj.data.uv_layers:
        step = time.perf_counter()
        _unwrap(obj, size, margin)
        last_bake_seconds["unwrap"] = time.perf_counter() - step
    if not obj.data.materials:
        obj.data.materials.append(bpy.data.materials.new(f"{obj.name}_source"))
    source_materials = [m for m in obj.data.materials if m is not None]

    scene = bpy.context.scene
    saved = (scene.render.engine, scene.cycles.device, scene.cycles.samples, scene.cycles.use_denoising, scene.world)
    made_world = None
    if scene.world is None:
        made_world = bpy.data.worlds.new("bake_world")
        scene.world = made_world
    saved_distance = scene.world.light_settings.distance
    scene.world.light_settings.distance = ao_distance if ao_distance is not None else max(obj.dimensions) * 0.25
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.use_denoising = False

    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    baked_paths: dict[str, str] = {}
    try:
        for map_name in maps:
            step = time.perf_counter()
            image = bpy.data.images.new(f"{obj.name}_{map_name}", size, size, alpha=False, is_data=map_name != "color")
            image.colorspace_settings.name = _SRGB if map_name == "color" else _DATA
            scene.cycles.samples = ao_samples if map_name == "ao" else samples
            added = _add_target_nodes(source_materials, image)
            try:
                _run_bake(map_name, margin)
            finally:
                for material, node in added:
                    material.node_tree.nodes.remove(node)
            path = os.path.join(out_dir, f"{obj.name}_{map_name}.png")
            image.filepath_raw = path
            image.file_format = "PNG"
            image.save()
            bpy.data.images.remove(image)
            baked_paths[map_name] = path
            last_bake_seconds[map_name] = time.perf_counter() - step
    finally:
        scene.world.light_settings.distance = saved_distance
        scene.render.engine, scene.cycles.device, scene.cycles.samples, scene.cycles.use_denoising, scene.world = saved
        if made_world is not None:
            bpy.data.worlds.remove(made_world)

    images = {}
    for map_name, path in baked_paths.items():
        images[map_name] = _load(path, _SRGB if map_name == "color" else _DATA)
    material = _build_material(f"{obj.name}_baked", images, 1.0)

    obj.data.materials.clear()
    obj.data.materials.append(material)
    obj.data.polygons.foreach_set("material_index", np.zeros(len(obj.data.polygons), dtype=np.int32))
    obj.data.update()

    last_bake_seconds["total"] = time.perf_counter() - started
    report = ", ".join(f"{key} {value:.2f}s" for key, value in last_bake_seconds.items())
    print(f"bake {obj.name} at {size}px: {report}")
    return material


def _load(path, colorspace: str) -> bpy.types.Image:
    image = bpy.data.images.load(os.fspath(path), check_existing=True)
    image.colorspace_settings.name = colorspace
    return image


def _flip_green(image: bpy.types.Image, name: str) -> bpy.types.Image:
    """Returns a packed copy of a normal map with its green channel inverted (DirectX to OpenGL)."""
    width, height = image.size
    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    pixels[1::4] = 1.0 - pixels[1::4]
    flipped = bpy.data.images.new(name, width, height, alpha=False, is_data=True)
    flipped.pixels.foreach_set(pixels)
    flipped.pack()
    return flipped


def _build_material(name: str, images: dict, tiling: float) -> bpy.types.Material:
    """Wires image textures into a fresh Principled BSDF the glTF exporter understands."""
    material = bpy.data.materials.new(name)
    tree = material.node_tree
    nodes, links = tree.nodes, tree.links
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (400, 0)
    bsdf = nodes.new("ShaderNodeBsdfPrincipled")
    links.new(bsdf.outputs["BSDF"], output.inputs["Surface"])

    vector = None
    if not math.isclose(tiling, 1.0):
        coordinates = nodes.new("ShaderNodeTexCoord")
        mapping = nodes.new("ShaderNodeMapping")
        mapping.inputs["Scale"].default_value = (tiling, tiling, tiling)
        links.new(coordinates.outputs["UV"], mapping.inputs["Vector"])
        vector = mapping.outputs["Vector"]

    def texture(image):
        node = nodes.new("ShaderNodeTexImage")
        node.image = image
        node.interpolation = "Linear"
        if vector is not None:
            links.new(vector, node.inputs["Vector"])
        return node

    if "color" in images:
        links.new(texture(images["color"]).outputs["Color"], bsdf.inputs["Base Color"])
    if "normal" in images:
        normal_map = nodes.new("ShaderNodeNormalMap")
        normal_map.space = "TANGENT"
        links.new(texture(images["normal"]).outputs["Color"], normal_map.inputs["Color"])
        links.new(normal_map.outputs["Normal"], bsdf.inputs["Normal"])
    if "roughness" in images:
        links.new(texture(images["roughness"]).outputs["Color"], bsdf.inputs["Roughness"])
    if "metal" in images:
        links.new(texture(images["metal"]).outputs["Color"], bsdf.inputs["Metallic"])
    if "ao" in images:
        group_node = nodes.new("ShaderNodeGroup")
        group_node.node_tree = _gltf_output_group()
        links.new(texture(images["ao"]).outputs["Color"], group_node.inputs["Occlusion"])

    _arrange(nodes)
    return material


def _gltf_output_group() -> bpy.types.NodeTree:
    """Returns the "glTF Material Output" node group, where the exporter reads occlusion from."""
    group = bpy.data.node_groups.get(_GLTF_OUTPUT_GROUP)
    if group is not None:
        return group
    try:
        from io_scene_gltf2.blender.com.material_helpers import create_settings_group

        return create_settings_group(_GLTF_OUTPUT_GROUP)
    except ImportError:
        group = bpy.data.node_groups.new(_GLTF_OUTPUT_GROUP, "ShaderNodeTree")
        group.interface.new_socket("Occlusion", socket_type="NodeSocketFloat")
        return group


def _arrange(nodes) -> None:
    """Puts image nodes in a column left of the BSDF so a saved file is readable."""
    row = 0
    for node in nodes:
        if node.bl_idname in {"ShaderNodeTexImage", "ShaderNodeNormalMap", "ShaderNodeGroup"}:
            node.location = (-500 if node.bl_idname == "ShaderNodeTexImage" else -200, -300 * row)
            row += 1


def _principled(material: bpy.types.Material):
    for node in material.node_tree.nodes:
        if node.bl_idname == "ShaderNodeBsdfPrincipled":
            return node
    raise ValueError(f"Material {material.name} has no Principled BSDF")


def _math(nodes, operation: str, first, second):
    node = nodes.new("ShaderNodeMath")
    node.operation = operation
    if first is not None:
        node.inputs[0].default_value = first
    if second is not None:
        node.inputs[1].default_value = second
    return node


def _socket(sockets, identifier: str):
    for socket in sockets:
        if socket.identifier == identifier:
            return socket
    raise KeyError(identifier)


def _ensure_mode_object() -> None:
    active = bpy.context.view_layer.objects.active
    if active is not None and active.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")


def _unwrap(obj: bpy.types.Object, size: int, margin: int) -> None:
    """Smart UV Project, then pack, on the whole mesh."""
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        bpy.ops.mesh.select_all(action="SELECT")
        island_margin = margin / size
        bpy.ops.uv.smart_project(angle_limit=math.radians(66.0), island_margin=island_margin)
        bpy.ops.uv.pack_islands(margin=island_margin)
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")


def _add_target_nodes(materials, image) -> list:
    """Adds an active image node holding `image` to each material, which is where a bake writes."""
    added = []
    for material in materials:
        node = material.node_tree.nodes.new("ShaderNodeTexImage")
        node.image = image
        material.node_tree.nodes.active = node
        node.select = True
        added.append((material, node))
    return added


def _run_bake(map_name: str, margin: int) -> None:
    if map_name == "color":
        result = bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"}, margin=margin, use_clear=True)
    elif map_name == "normal":
        result = bpy.ops.object.bake(
            type="NORMAL",
            normal_space="TANGENT",
            normal_r="POS_X",
            normal_g="POS_Y",
            normal_b="POS_Z",
            margin=margin,
            use_clear=True,
        )
    elif map_name == "roughness":
        result = bpy.ops.object.bake(type="ROUGHNESS", margin=margin, use_clear=True)
    else:
        result = bpy.ops.object.bake(type="AO", margin=margin, use_clear=True)
    if "FINISHED" not in result:
        raise RuntimeError(f"Baking {map_name} did not finish: {result}")
