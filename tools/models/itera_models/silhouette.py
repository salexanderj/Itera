"""Silhouette checks for recipes: render a model's outline, read a reference image's, and compare them.

The comparison ignores where the shape sits in the image and how large the image is, so a reference sketch and a
model can be compared as they are. It does not see inside the outline.

Views use Blender's own names and directions. In the model's space (Blender: +Z up, front faces -Y):

- `front` looks from -Y towards +Y. Image right is +X.
- `back` looks from +Y towards -Y. Image right is -X.
- `side` looks from +X towards -X (Blender's Right view). Image right is +Y, so the model's front is on the left.
- `top` looks down from +Z. Image up is +Y and image right is +X.
"""

from __future__ import annotations

import os
import tempfile

import bpy
import numpy as np
from mathutils import Matrix, Vector

#: For each view: the direction from the model to the camera, and the direction that is up in the image.
_VIEWS = {
    "front": (Vector((0, -1, 0)), Vector((0, 0, 1))),
    "back": (Vector((0, 1, 0)), Vector((0, 0, 1))),
    "side": (Vector((1, 0, 0)), Vector((0, 0, 1))),
    "top": (Vector((0, 0, 1)), Vector((0, 1, 0))),
}

_GEOMETRY_TYPES = {"MESH", "CURVE", "SURFACE", "FONT", "META"}
_MARGIN = 1.08


def mask(objects, view: str = "front", size: int = 256) -> np.ndarray:
    """Renders the objects' outline from `view` and returns a boolean (size, size) array. Row 0 is the top.

    The camera is orthographic and frames the objects with a small margin. Modifiers and poses count as in the
    evaluated scene. Colours and lighting play no part: the render is flat white where the objects are.
    """
    if view not in _VIEWS:
        raise ValueError(f"Unknown view '{view}'. Choose from {', '.join(_VIEWS)}")
    objects = list(objects)
    shapes = [obj for obj in objects if obj.type in _GEOMETRY_TYPES]
    if not shapes:
        raise ValueError("No mesh objects to draw")

    toward_camera, up = _VIEWS[view]
    forward = -toward_camera
    right = forward.cross(up)
    up = right.cross(forward)

    corners = _world_corners(shapes)
    right_range = _range(corners, right)
    up_range = _range(corners, up)
    forward_range = _range(corners, forward)
    width = right_range[1] - right_range[0]
    height = up_range[1] - up_range[0]
    depth = forward_range[1] - forward_range[0]
    center = (right * sum(right_range) + up * sum(up_range) + forward * sum(forward_range)) / 2.0
    distance = depth / 2.0 + 1.0

    scene = bpy.data.scenes.new("itera_silhouette")
    camera_data = bpy.data.cameras.new("itera_silhouette")
    camera = bpy.data.objects.new("itera_silhouette", camera_data)
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = max(width, height, 1e-3) * _MARGIN
    camera_data.clip_start = 0.01
    camera_data.clip_end = distance + depth + 1.0
    camera.matrix_world = Matrix.LocRotScale(
        center + toward_camera * distance,
        Matrix(((right.x, up.x, -forward.x), (right.y, up.y, -forward.y), (right.z, up.z, -forward.z))),
        None,
    )
    scene.collection.objects.link(camera)
    scene.camera = camera
    for obj in _with_dependencies(objects):
        scene.collection.objects.link(obj)

    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = size
    scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = True
    scene.render.image_settings.media_type = "IMAGE"
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.display.shading.light = "FLAT"
    scene.display.shading.color_type = "SINGLE"
    scene.display.shading.single_color = (1.0, 1.0, 1.0)
    scene.view_settings.view_transform = "Standard"

    handle, path = tempfile.mkstemp(suffix=".png", prefix="itera_silhouette_")
    os.close(handle)
    try:
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True, scene=scene.name)
        alpha = _read_image(path)[..., 3]
    finally:
        os.remove(path)
        for obj in _with_dependencies(objects):
            scene.collection.objects.unlink(obj)
        bpy.data.scenes.remove(scene)
        bpy.data.objects.remove(camera)
        bpy.data.cameras.remove(camera_data)
    return alpha > 0.5


def image_mask(path: str | os.PathLike, size: int = 256, threshold: float = 0.1, flip: bool = False) -> np.ndarray:
    """Reads a reference image's silhouette as a boolean array whose longer side is `size` pixels. Row 0 is the top.

    An image with transparency gives its opaque pixels. Otherwise the background is the colour of the image's
    corners (their median), and a pixel belongs to the shape when any of its channels differs from that by more
    than `threshold` (0 to 1), whether darker or lighter. `flip` mirrors the result left to right, for a
    reference drawn facing the other way.
    """
    pixels = _read_image(os.fspath(path))
    alpha = pixels[..., 3]
    if np.any(alpha < 0.99):
        found = alpha > 0.5
    else:
        corners = np.array([pixels[0, 0], pixels[0, -1], pixels[-1, 0], pixels[-1, -1]])[:, :3]
        background = np.median(corners, axis=0)
        found = np.abs(pixels[..., :3] - background).max(axis=-1) > threshold
    height, width = found.shape
    scale = size / max(height, width)
    found = _resize_nearest(found, max(1, round(height * scale)), max(1, round(width * scale)))
    return np.fliplr(found) if flip else found


def iou(model_mask: np.ndarray, reference_mask: np.ndarray, size: int = 256, keep_aspect: bool = True) -> float:
    """Returns the intersection over union of two masks, from 0 (nothing shared) to 1 (the same outline).

    Each mask is cropped to its own bounding box, so position and image size do not matter. By default both are
    then scaled to the same height, so a model twice as wide as its reference scores low: proportions are what a
    reference sets. With `keep_aspect=False` both are stretched to the same `size` by `size` square, and only the
    outline's shape within its box counts. An empty mask scores 0.
    """
    model = _crop(model_mask)
    reference = _crop(reference_mask)
    if model is None or reference is None:
        return 0.0
    if keep_aspect:
        widths = [max(1, round(m.shape[1] * size / m.shape[0])) for m in (model, reference)]
        model = _resize_nearest(model, size, widths[0])
        reference = _resize_nearest(reference, size, widths[1])
        common = max(widths)
        model = _pad_center(model, common)
        reference = _pad_center(reference, common)
    else:
        model = _resize_nearest(model, size, size)
        reference = _resize_nearest(reference, size, size)
    union = np.count_nonzero(model | reference)
    return float(np.count_nonzero(model & reference) / union)


def compare(
    objects,
    references: dict[str, str | os.PathLike],
    size: int = 256,
    keep_aspect: bool = True,
    flip: tuple[str, ...] = (),
) -> dict[str, float]:
    """Compares the objects' outline with reference images and returns `{view: iou}`.

    `references` maps a view (`front`, `side`, `back`, `top`) to an image path. Views named in `flip` mirror the
    reference left to right first, for a reference that faces the other way than the `side` view above.
    """
    objects = list(objects)
    result = {}
    for view, path in references.items():
        model = mask(objects, view, size)
        reference = image_mask(path, size, flip=view in flip)
        result[view] = iou(model, reference, size, keep_aspect)
    return result


def _world_corners(shapes) -> list[Vector]:
    depsgraph = bpy.context.evaluated_depsgraph_get()
    corners = []
    for obj in shapes:
        evaluated = obj.evaluated_get(depsgraph)
        corners.extend(evaluated.matrix_world @ Vector(corner) for corner in evaluated.bound_box)
    return corners


def _range(points, axis: Vector) -> tuple[float, float]:
    """The lowest and highest position of the points along an axis."""
    positions = [point.dot(axis) for point in points]
    return min(positions), max(positions)


def _with_dependencies(objects) -> list[bpy.types.Object]:
    """The objects plus the armatures that deform them and the parents above them, so the scene poses them."""
    found: dict[str, bpy.types.Object] = {}

    def add(obj):
        if obj is None or obj.name in found:
            return
        found[obj.name] = obj
        add(obj.parent)
        for modifier in obj.modifiers:
            add(getattr(modifier, "object", None))

    for obj in objects:
        add(obj)
    return list(found.values())


def _read_image(path: str) -> np.ndarray:
    image = bpy.data.images.load(path, check_existing=False)
    try:
        image.colorspace_settings.name = "Non-Color"
        width, height = image.size
        pixels = np.empty(width * height * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
    finally:
        bpy.data.images.remove(image)
    return np.flipud(pixels.reshape(height, width, 4))


def _crop(found: np.ndarray) -> np.ndarray | None:
    rows = np.flatnonzero(found.any(axis=1))
    if rows.size == 0:
        return None
    columns = np.flatnonzero(found.any(axis=0))
    return found[rows[0] : rows[-1] + 1, columns[0] : columns[-1] + 1]


def _resize_nearest(found: np.ndarray, height: int, width: int) -> np.ndarray:
    rows = np.minimum(((np.arange(height) + 0.5) * found.shape[0] / height).astype(int), found.shape[0] - 1)
    columns = np.minimum(((np.arange(width) + 0.5) * found.shape[1] / width).astype(int), found.shape[1] - 1)
    return found[np.ix_(rows, columns)]


def _pad_center(found: np.ndarray, width: int) -> np.ndarray:
    extra = width - found.shape[1]
    left = extra // 2
    return np.pad(found, ((0, 0), (left, extra - left)))
