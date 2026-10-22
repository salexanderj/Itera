"""Renders a model from six sides, shaded and as a wireframe, into one labelled PNG for a person or a model to look at.

Run inside Blender. The sheet has a title row, then a row of shaded views and a row of wireframe views. Each row shows
the model from its front, back, left, right, from a three-quarter angle, and from above.

- Front is the side a model faces in the engine: glTF +Z, which is Blender -Y. Left and right are the model's own
  left and right (left is Blender +X), seen from outside, as when a person stands there and looks at the model.
- The top view puts the model's front at the bottom of the image.
- The side views and the top view are orthographic, so sizes compare across views. The three-quarter view is a
  perspective view from the front, the model's left, and above.
- The wireframe row comes from a temporary copy of each mesh with a Wireframe modifier and a dark material. Viewport
  overlays do not render in background mode, so the copy is how a wireframe is drawn.

Workbench with studio lighting draws the shaded row. Each render takes well under a second for a small model. Everything
the sheet adds to the scene (cameras, copies, a world, a label scene) is removed again, and the scene's own settings are
put back.
"""

from __future__ import annotations

import math
import tempfile
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Vector

from . import selection

COLUMNS = 6
LABEL_HEIGHT = 28
MARGIN = 1.15
BACKGROUND = (0.32, 0.32, 0.32)
LABEL_BACKGROUND = (0.02, 0.02, 0.02)
THREE_QUARTER_AZIMUTH = math.radians(35.0)
THREE_QUARTER_ELEVATION = math.radians(25.0)
FIELD_OF_VIEW_LENS = 50.0

# (label, direction from the model to the camera, the camera's up direction, orthographic or not), in Blender axes.
_THREE_QUARTER = Vector(
    (
        math.sin(THREE_QUARTER_AZIMUTH) * math.cos(THREE_QUARTER_ELEVATION),
        -math.cos(THREE_QUARTER_AZIMUTH) * math.cos(THREE_QUARTER_ELEVATION),
        math.sin(THREE_QUARTER_ELEVATION),
    )
)
VIEWS = (
    ("front", Vector((0, -1, 0)), Vector((0, 0, 1)), True),
    ("back", Vector((0, 1, 0)), Vector((0, 0, 1)), True),
    ("left", Vector((1, 0, 0)), Vector((0, 0, 1)), True),
    ("right", Vector((-1, 0, 0)), Vector((0, 0, 1)), True),
    ("3/4", _THREE_QUARTER, Vector((0, 0, 1)), False),
    ("top", Vector((0, 0, 1)), Vector((0, 1, 0)), True),
)


def sheet_size(size: int = 384) -> tuple[int, int]:
    """The width and height in pixels of a sheet whose views are `size` pixels square."""
    return COLUMNS * size, LABEL_HEIGHT + 2 * (size + LABEL_HEIGHT)


def sheet(path, objects=None, size: int = 384, engine: str = "BLENDER_WORKBENCH") -> Path:
    """Renders the sheet of the model (default: the scene's visible meshes) to a PNG at path. Returns the path."""
    meshes = selection.meshes_of(objects)
    if not meshes:
        raise ValueError("sheet: there are no meshes to draw")
    title = ", ".join(sorted(mesh.name for mesh in meshes))
    return _render_sheet(Path(path), meshes, size, engine, title)


def posed(
    path,
    armature,
    clip: str,
    seconds: float,
    size: int = 384,
    objects=None,
    engine: str = "BLENDER_WORKBENCH",
) -> Path:
    """Renders the same sheet with the action `clip` on the armature, `seconds` into the clip. Returns the path.

    The armature's own action and the scene's current frame are put back afterwards.
    """
    action = bpy.data.actions.get(clip)
    if action is None:
        raise ValueError(f"posed: there is no action named {clip}")
    slots = [slot for slot in action.slots if slot.target_id_type == "OBJECT"]
    if not slots:
        raise ValueError(f"posed: the action {clip} has no slot that animates objects")
    slot = next((candidate for candidate in slots if candidate.identifier == "OB" + armature.name), slots[0])
    meshes = selection.meshes_of(objects)
    if not meshes:
        raise ValueError("posed: there are no meshes to draw")

    scene = bpy.context.scene
    if armature.animation_data is None:
        armature.animation_data_create()
    data = armature.animation_data
    old_action, old_slot = data.action, data.action_slot
    old_frame, old_subframe = scene.frame_current, scene.frame_subframe
    try:
        data.action = action
        data.action_slot = slot
        frame = action.frame_range[0] + seconds * scene.render.fps / scene.render.fps_base
        scene.frame_set(math.floor(frame), subframe=frame - math.floor(frame))
        title = f"{clip} at {seconds:.2f} s"
        return _render_sheet(Path(path), meshes, size, engine, title)
    finally:
        data.action = old_action
        if old_action is not None:
            data.action_slot = old_slot
        scene.frame_set(old_frame, subframe=old_subframe)


class _Temporaries:
    """Datablocks made for the render, removed together when it ends."""

    def __init__(self) -> None:
        self.objects: list = []
        self.meshes: list = []
        self.materials: list = []
        self.cameras: list = []
        self.lights: list = []
        self.curves: list = []
        self.worlds: list = []
        self.images: list = []
        self.scenes: list = []

    def remove_all(self) -> None:
        for obj in self.objects:
            bpy.data.objects.remove(obj, do_unlink=True)
        for scene in self.scenes:
            bpy.data.scenes.remove(scene)
        for collection, items in (
            (bpy.data.meshes, self.meshes),
            (bpy.data.materials, self.materials),
            (bpy.data.cameras, self.cameras),
            (bpy.data.lights, self.lights),
            (bpy.data.curves, self.curves),
            (bpy.data.worlds, self.worlds),
            (bpy.data.images, self.images),
        ):
            for item in items:
                collection.remove(item)


class _SceneSettings:
    """Remembers the scene settings the render changes, and puts them back."""

    def __init__(self, scene) -> None:
        self.scene = scene
        render = scene.render
        self.saved = {
            "camera": scene.camera,
            "world": scene.world,
            "engine": render.engine,
            "resolution_x": render.resolution_x,
            "resolution_y": render.resolution_y,
            "resolution_percentage": render.resolution_percentage,
            "filepath": render.filepath,
            "film_transparent": render.film_transparent,
            "media_type": render.image_settings.media_type,
            "file_format": render.image_settings.file_format,
            "color_mode": render.image_settings.color_mode,
            "view_transform": scene.view_settings.view_transform,
            "render_aa": scene.display.render_aa,
            "light": scene.display.shading.light,
            "color_type": scene.display.shading.color_type,
            "single_color": tuple(scene.display.shading.single_color),
            "show_object_outline": scene.display.shading.show_object_outline,
            "show_cavity": scene.display.shading.show_cavity,
        }
        self.hide_render = [(obj, obj.hide_render) for obj in scene.objects]

    def restore(self) -> None:
        scene = self.scene
        render = scene.render
        saved = self.saved
        render.engine = saved["engine"]
        render.resolution_x = saved["resolution_x"]
        render.resolution_y = saved["resolution_y"]
        render.resolution_percentage = saved["resolution_percentage"]
        render.filepath = saved["filepath"]
        render.film_transparent = saved["film_transparent"]
        render.image_settings.media_type = saved["media_type"]
        render.image_settings.file_format = saved["file_format"]
        render.image_settings.color_mode = saved["color_mode"]
        scene.view_settings.view_transform = saved["view_transform"]
        scene.display.render_aa = saved["render_aa"]
        shading = scene.display.shading
        shading.light = saved["light"]
        shading.color_type = saved["color_type"]
        shading.single_color = saved["single_color"]
        shading.show_object_outline = saved["show_object_outline"]
        shading.show_cavity = saved["show_cavity"]
        scene.camera = saved["camera"]
        scene.world = saved["world"]
        for obj, hidden in self.hide_render:
            obj.hide_render = hidden


def _render_sheet(path: Path, meshes: list, size: int, engine: str, title: str) -> Path:
    """Renders the shaded and wireframe rows for the meshes in their current pose and writes the sheet."""
    scene = bpy.context.scene
    temporaries = _Temporaries()
    settings = _SceneSettings(scene)
    try:
        with tempfile.TemporaryDirectory(prefix="itera-views-") as folder:
            renderer = _Renderer(scene, size, engine, Path(folder), temporaries)
            renderer.hide_all_but(meshes)
            bounds = _bounds(meshes)
            shaded = [renderer.render(view, bounds) for view in VIEWS]
            copies = renderer.wire_copies(meshes, bounds)
            renderer.hide_all_but(copies, extra_visible=())
            renderer.use_wire_look()
            wire = [renderer.render(view, bounds) for view in VIEWS]
            labels = _Labels(Path(folder), temporaries)
            canvas = _compose(shaded, wire, labels, title, size)
        _save(canvas, path)
    finally:
        settings.restore()
        temporaries.remove_all()
    return path


class _Renderer:
    """Sets up the scene for a Workbench or EEVEE render and renders views of the model."""

    def __init__(self, scene, size: int, engine: str, folder: Path, temporaries: _Temporaries) -> None:
        self.scene = scene
        self.size = size
        self.folder = folder
        self.temporaries = temporaries
        self.count = 0

        render = scene.render
        render.engine = engine
        render.resolution_x = size
        render.resolution_y = size
        render.resolution_percentage = 100
        render.film_transparent = False
        render.image_settings.media_type = "IMAGE"
        render.image_settings.file_format = "PNG"
        render.image_settings.color_mode = "RGB"
        scene.view_settings.view_transform = "Standard"
        if engine == "BLENDER_EEVEE":
            scene.eevee.taa_render_samples = 8
        else:
            scene.display.render_aa = "8"
        shading = scene.display.shading
        shading.light = "STUDIO"
        shading.color_type = "MATERIAL"
        shading.show_object_outline = True
        shading.show_cavity = False

        world = bpy.data.worlds.new("itera_views_world")
        world.color = BACKGROUND
        temporaries.worlds.append(world)
        scene.world = world

        camera_data = bpy.data.cameras.new("itera_views_camera")
        temporaries.cameras.append(camera_data)
        self.camera = bpy.data.objects.new("itera_views_camera", camera_data)
        scene.collection.objects.link(self.camera)
        temporaries.objects.append(self.camera)
        scene.camera = self.camera
        if engine != "BLENDER_WORKBENCH":
            light_data = bpy.data.lights.new("itera_views_light", "SUN")
            light_data.energy = 3.0
            temporaries.lights.append(light_data)
            light = bpy.data.objects.new("itera_views_light", light_data)
            light.rotation_euler = (math.radians(50), 0.0, math.radians(30))
            scene.collection.objects.link(light)
            temporaries.objects.append(light)

    def hide_all_but(self, visible: list, extra_visible=()) -> None:
        """Hides every object from the render except the given ones (and the camera and lights, which draw nothing)."""
        keep = {id(obj) for obj in list(visible) + list(extra_visible)}
        for obj in self.scene.objects:
            obj.hide_render = id(obj) not in keep

    def use_wire_look(self) -> None:
        """Flat light for the wireframe row, so the dark lines stand out against a light body."""
        self.scene.display.shading.light = "FLAT"

    def wire_copies(self, meshes: list, bounds) -> list:
        """A temporary copy of each mesh, in its current pose, with a light body and a dark Wireframe modifier."""
        depsgraph = bpy.context.evaluated_depsgraph_get()
        radius = max((bounds[1] - bounds[0]).length / 2.0, 1e-6)
        body = bpy.data.materials.new("itera_views_body")
        body.diffuse_color = (0.78, 0.78, 0.78, 1.0)
        lines = bpy.data.materials.new("itera_views_lines")
        lines.diffuse_color = (0.03, 0.03, 0.03, 1.0)
        self.temporaries.materials.extend([body, lines])
        copies = []
        for mesh_object in meshes:
            evaluated = mesh_object.evaluated_get(depsgraph)
            data = bpy.data.meshes.new_from_object(evaluated, preserve_all_data_layers=False, depsgraph=depsgraph)
            self.temporaries.meshes.append(data)
            data.materials.clear()
            data.materials.append(body)
            data.materials.append(lines)
            if len(data.polygons):
                data.polygons.foreach_set("material_index", np.zeros(len(data.polygons), dtype=np.int32))
            copy = bpy.data.objects.new(mesh_object.name + "_wire", data)
            copy.matrix_world = mesh_object.matrix_world.copy()
            self.scene.collection.objects.link(copy)
            self.temporaries.objects.append(copy)
            modifier = copy.modifiers.new("itera_views_wire", "WIREFRAME")
            modifier.thickness = radius * 0.008
            modifier.use_replace = False
            modifier.use_even_offset = True
            modifier.material_offset = 1
            copies.append(copy)
        return copies

    def render(self, view, bounds) -> np.ndarray:
        """Renders one view and returns its pixels as a top-down (size, size, 4) float array."""
        _label, direction, up, orthographic = view
        self._place_camera(direction, up, orthographic, bounds)
        self.count += 1
        path = self.folder / f"view{self.count}.png"
        self.scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True)
        return _read_png(path)

    def _place_camera(self, direction: Vector, up: Vector, orthographic: bool, bounds) -> None:
        """Aims the camera at the middle of the bounds from the given direction, framing the whole model."""
        low, high = bounds
        centre = (low + high) / 2.0
        radius = max((high - low).length / 2.0, 1e-6)
        back = direction.normalized()
        right = up.cross(back).normalized()
        true_up = back.cross(right).normalized()
        camera = self.camera.data
        if orthographic:
            corners = _corners(low, high)
            width = max(abs((corner - centre).dot(right)) for corner in corners) * 2.0
            height = max(abs((corner - centre).dot(true_up)) for corner in corners) * 2.0
            camera.type = "ORTHO"
            camera.ortho_scale = max(width, height, 1e-6) * MARGIN
            distance = radius * 3.0
        else:
            camera.type = "PERSP"
            camera.lens = FIELD_OF_VIEW_LENS
            camera.sensor_width = 36.0
            camera.sensor_fit = "AUTO"
            distance = radius / math.sin(camera.angle / 2.0) * 1.05
        camera.clip_start = max(distance - radius * 2.0, radius * 0.01)
        camera.clip_end = distance + radius * 2.0
        rotation = Matrix((right, true_up, back)).transposed()
        self.camera.matrix_world = Matrix.Translation(centre + back * distance) @ rotation.to_4x4()


def _corners(low: Vector, high: Vector) -> list:
    """The eight corners of a box."""
    return [Vector((x, y, z)) for x in (low.x, high.x) for y in (low.y, high.y) for z in (low.z, high.z)]


def _bounds(meshes: list) -> tuple[Vector, Vector]:
    """The lowest and highest world corner of the meshes' evaluated shapes."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    points = []
    for mesh_object in meshes:
        evaluated = mesh_object.evaluated_get(depsgraph)
        matrix = evaluated.matrix_world
        points.extend(matrix @ Vector(corner) for corner in evaluated.bound_box)
    low = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    high = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    return low, high


def _read_png(path: Path) -> np.ndarray:
    """A PNG's pixels as a top-down float array of shape (height, width, 4), exactly as stored."""
    image = bpy.data.images.load(str(path))
    try:
        # The numbers pass through unchanged: no colour space conversion on the way in or out.
        image.colorspace_settings.name = "Non-Color"
        width, height = image.size
        pixels = np.empty(width * height * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
    finally:
        bpy.data.images.remove(image)
    return pixels.reshape(height, width, 4)[::-1]


class _Labels:
    """Draws short texts as images with Blender's own font, in a scene of their own."""

    def __init__(self, folder: Path, temporaries: _Temporaries) -> None:
        self.folder = folder
        self.temporaries = temporaries
        self.cache: dict = {}
        self.count = 0
        self.scene = bpy.data.scenes.new("itera_views_labels")
        temporaries.scenes.append(self.scene)
        scene = self.scene
        scene.render.engine = "BLENDER_WORKBENCH"
        scene.render.resolution_percentage = 100
        scene.render.image_settings.media_type = "IMAGE"
        scene.render.image_settings.file_format = "PNG"
        scene.render.image_settings.color_mode = "RGB"
        scene.view_settings.view_transform = "Standard"
        scene.display.render_aa = "8"
        scene.display.shading.light = "FLAT"
        scene.display.shading.color_type = "SINGLE"
        scene.display.shading.single_color = (1.0, 1.0, 1.0)
        world = bpy.data.worlds.new("itera_labels_world")
        world.color = LABEL_BACKGROUND
        temporaries.worlds.append(world)
        scene.world = world
        self.font = bpy.data.curves.new("itera_labels_text", "FONT")
        self.font.align_x = "LEFT"
        self.font.align_y = "CENTER"
        self.font.size = 0.55
        temporaries.curves.append(self.font)
        text_object = bpy.data.objects.new("itera_labels_text", self.font)
        scene.collection.objects.link(text_object)
        temporaries.objects.append(text_object)
        self.text_object = text_object
        camera_data = bpy.data.cameras.new("itera_labels_camera")
        camera_data.type = "ORTHO"
        temporaries.cameras.append(camera_data)
        camera = bpy.data.objects.new("itera_labels_camera", camera_data)
        camera.location = (0.0, 0.0, 5.0)
        scene.collection.objects.link(camera)
        temporaries.objects.append(camera)
        self.camera_data = camera_data
        scene.camera = camera

    def image(self, text: str, width: int) -> np.ndarray:
        """The text as a top-down (LABEL_HEIGHT, width, 4) array: white on a dark strip."""
        key = (text, width)
        if key not in self.cache:
            self.cache[key] = self._draw(text, width)
        return self.cache[key]

    def _draw(self, text: str, width: int) -> np.ndarray:
        scene = self.scene
        scene.render.resolution_x = width
        scene.render.resolution_y = LABEL_HEIGHT
        # One world unit is LABEL_HEIGHT pixels tall, so the text size above is a fraction of the strip's height.
        self.camera_data.ortho_scale = width / LABEL_HEIGHT
        self.font.body = text
        self.text_object.location = (-width / LABEL_HEIGHT / 2.0 + 0.3, 0.0, 0.0)
        self.count += 1
        path = self.folder / f"label{self.count}.png"
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True, scene=scene.name)
        return _read_png(path)


def _compose(shaded: list, wire: list, labels: _Labels, title: str, size: int) -> np.ndarray:
    """Lays the title, the labels, and the two rows of views out in one top-down float array."""
    width, height = sheet_size(size)
    canvas = np.zeros((height, width, 4), dtype=np.float32)
    canvas[:, :, 3] = 1.0
    canvas[0:LABEL_HEIGHT, :, :] = labels.image(title, width)
    top = LABEL_HEIGHT
    for suffix, row in ((" shaded", shaded), (" wire", wire)):
        for column, ((name, _d, _u, _o), pixels) in enumerate(zip(VIEWS, row)):
            left = column * size
            canvas[top : top + LABEL_HEIGHT, left : left + size, :] = labels.image(name + suffix, size)
            canvas[top + LABEL_HEIGHT : top + LABEL_HEIGHT + size, left : left + size, :] = pixels
        top += LABEL_HEIGHT + size
    return canvas


def _save(canvas: np.ndarray, path: Path) -> None:
    """Writes a top-down float array as a PNG, with the numbers unchanged."""
    height, width, _ = canvas.shape
    path.parent.mkdir(parents=True, exist_ok=True)
    image = bpy.data.images.new("itera_views_sheet", width, height, alpha=False)
    try:
        image.colorspace_settings.name = "Non-Color"
        image.pixels.foreach_set(np.ascontiguousarray(canvas[::-1]).ravel())
        image.filepath_raw = str(path)
        image.file_format = "PNG"
        image.save()
    finally:
        bpy.data.images.remove(image)
