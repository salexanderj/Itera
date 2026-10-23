"""Pictures of a clip as it plays: a sheet of its frames from the side and from in front, and a video. Run inside
Blender.

The sheet has a row for each view: from the model's right side and from three quarters in front of it on its left,
each frame labelled with its time. Both are drawn with Workbench, orthographic, the camera fixed for the whole clip so
the body's travel and height show. The video plays the clip at its speed from the three-quarter view, as H.264 in an
MP4 file, with Blender's own FFmpeg.
"""

from __future__ import annotations

import math
import tempfile
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Vector

from .views import _corners, _read_png, _save, _SceneSettings, _Temporaries

#: The views: a label and the way from the model to the camera, in Blender's axes (front -y, left +x).
VIEWS = (
    ("side", Vector((-1.0, 0.0, 0.0))),
    ("front", Vector((0.6, -1.0, 0.0))),
)

#: The background, and the margin round the clip's reach.
BACKGROUND = (0.62, 0.66, 0.70)
MARGIN = 1.08


def sheet(path, armature, meshes: list, frames: list, fps: int, size: tuple = (220, 300)) -> Path:
    """Renders `frames` (scene frames) of the armature's clip from each view into a sheet at `path`."""
    scene = bpy.context.scene
    temporaries = _Temporaries()
    settings = _SceneSettings(scene)
    try:
        bounds = _reach(armature, meshes, frames)
        camera = _setup(scene, temporaries, size)
        _hide_all_but(scene, meshes)
        rows = []
        with tempfile.TemporaryDirectory(prefix="itera-strips-") as folder:
            for label, way in VIEWS:
                _aim(camera, way, bounds, size)
                row = []
                for number, frame in enumerate(frames):
                    scene.frame_set(int(frame))
                    shot = Path(folder) / f"{label}{number}.png"
                    scene.render.filepath = str(shot)
                    bpy.ops.render.render(write_still=True)
                    row.append(_read_png(shot))
                rows.append(row)
        canvas = _compose(rows, frames, fps)
        _save(canvas, Path(path))
    finally:
        settings.restore()
        temporaries.remove_all()
    return Path(path)


def video(path, armature, meshes: list, last: int, size: tuple = (480, 640)) -> Path:
    """Renders the armature's clip, frames 0 to `last`, from the three-quarter view into an MP4 at `path`."""
    scene = bpy.context.scene
    temporaries = _Temporaries()
    settings = _SceneSettings(scene)
    start, end = scene.frame_start, scene.frame_end
    try:
        bounds = _reach(armature, meshes, list(range(0, last + 1, 3)) + [last])
        camera = _setup(scene, temporaries, size)
        _hide_all_but(scene, meshes)
        _aim(camera, VIEWS[1][1], bounds, size)
        render = scene.render
        render.image_settings.media_type = "VIDEO"
        render.image_settings.file_format = "FFMPEG"
        render.ffmpeg.format = "MPEG4"
        render.ffmpeg.codec = "H264"
        render.ffmpeg.constant_rate_factor = "HIGH"
        scene.frame_start, scene.frame_end = 0, last
        render.filepath = str(path)
        bpy.ops.render.render(animation=True)
    finally:
        scene.frame_start, scene.frame_end = start, end
        settings.restore()
        temporaries.remove_all()
    return Path(path)


def _reach(armature, meshes: list, frames: list) -> tuple:
    """The box round the meshes over the frames."""
    scene = bpy.context.scene
    low = Vector((math.inf,) * 3)
    high = Vector((-math.inf,) * 3)
    for frame in frames:
        scene.frame_set(int(frame))
        depsgraph = bpy.context.evaluated_depsgraph_get()
        for mesh in meshes:
            evaluated = mesh.evaluated_get(depsgraph)
            for corner in evaluated.bound_box:
                point = evaluated.matrix_world @ Vector(corner)
                low = Vector(map(min, low, point))
                high = Vector(map(max, high, point))
    low.z = min(low.z, 0.0)
    return low, high


def _setup(scene, temporaries: _Temporaries, size: tuple):
    render = scene.render
    render.engine = "BLENDER_WORKBENCH"
    render.resolution_x, render.resolution_y = size
    render.resolution_percentage = 100
    render.film_transparent = False
    render.image_settings.media_type = "IMAGE"
    render.image_settings.file_format = "PNG"
    render.image_settings.color_mode = "RGB"
    scene.view_settings.view_transform = "Standard"
    scene.display.render_aa = "8"
    shading = scene.display.shading
    shading.light = "STUDIO"
    shading.color_type = "MATERIAL"
    shading.show_object_outline = True
    shading.show_cavity = False
    world = bpy.data.worlds.new("itera_strips_world")
    world.color = BACKGROUND
    temporaries.worlds.append(world)
    scene.world = world
    data = bpy.data.cameras.new("itera_strips_camera")
    temporaries.cameras.append(data)
    camera = bpy.data.objects.new("itera_strips_camera", data)
    scene.collection.objects.link(camera)
    temporaries.objects.append(camera)
    scene.camera = camera
    return camera


def _hide_all_but(scene, visible: list) -> None:
    keep = {id(obj) for obj in visible}
    for obj in scene.objects:
        obj.hide_render = id(obj) not in keep


def _aim(camera, way: Vector, bounds: tuple, size: tuple) -> None:
    """An orthographic camera looking from `way`, level, framing the box."""
    low, high = bounds
    centre = (low + high) / 2
    back = Vector((way.x, way.y, 0.0)).normalized()
    up = Vector((0.0, 0.0, 1.0))
    right = up.cross(back).normalized()
    corners = _corners(low, high)
    width = max(abs((corner - centre).dot(right)) for corner in corners) * 2
    height = max(abs((corner - centre).dot(up)) for corner in corners) * 2
    aspect = size[0] / size[1]
    data = camera.data
    data.type = "ORTHO"
    data.sensor_fit = "AUTO"
    # The scale spans the picture's longer side.
    data.ortho_scale = (max(height, width / aspect) if aspect < 1 else max(width, height * aspect)) * MARGIN
    radius = (high - low).length
    data.clip_start = 0.01
    data.clip_end = radius * 4
    rotation = Matrix((right, up, back)).transposed()
    camera.matrix_world = Matrix.Translation(centre + back * radius * 2) @ rotation.to_4x4()


def _compose(rows: list, frames: list, fps: int) -> np.ndarray:
    """The rows side by side, each frame with its time written across its top in small block digits."""
    height, width = rows[0][0].shape[:2]
    canvas = np.ones((height * len(rows), width * len(frames), 4), dtype=np.float32)
    for r, row in enumerate(rows):
        for c, image in enumerate(row):
            tile = image.copy()
            _write(tile, f"{frames[c] / fps:.2f}")
            canvas[r * height : (r + 1) * height, c * width : (c + 1) * width] = tile
            canvas[r * height : (r + 1) * height, c * width, :3] = 0.25
    return canvas


#: Block digits, 3 by 5, for the frames' times.
_DIGITS = {
    "0": "111101101101111",
    "1": "010110010010111",
    "2": "111001111100111",
    "3": "111001111001111",
    "4": "101101111001001",
    "5": "111100111001111",
    "6": "111100111101111",
    "7": "111001001001001",
    "8": "111101111101111",
    "9": "111101111001111",
    ".": "000000000000010",
}


def _write(image: np.ndarray, text: str, scale: int = 3) -> None:
    x = 4
    for char in text:
        bits = _DIGITS.get(char)
        if bits is None:
            continue
        for row in range(5):
            for column in range(3):
                if bits[row * 3 + column] == "1":
                    y0, x0 = 4 + row * scale, x + column * scale
                    image[y0 : y0 + scale, x0 : x0 + scale, :3] = 0.05
        x += 4 * scale
