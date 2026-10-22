"""Exports the scene to a .glb the engine reads.

Run inside Blender. glb() sets every option the engine needs (no compression, applied modifiers, at most four joint
influences, no morph targets, metres, +Y up) so a recipe cannot export by mistake with Blender's defaults.

Which clips are exported: Blender's exporter picks actions that are active on an object or stashed on an NLA track, and
with "Export all Armature Actions" on, every action of the file. glb() turns that option off and, for the export only,
stashes exactly the asked-for actions on NLA tracks. It puts everything back afterwards.
"""

from __future__ import annotations

import contextlib
from pathlib import Path

import bpy

from . import selection


def glb(path, objects=None, clips=None, vertex_colors: bool = False) -> dict:
    """Exports objects (default: the scene's visible meshes, armatures, and empties) to path as a binary glTF file.

    clips: the names of the actions to export as glTF animations. The default (None or empty) exports no animation at
    all, so a static model gets none, and a skinned model with no clips is fine.
    vertex_colors: export the active colour attribute as COLOR_0. Off by default: no vertex colours are exported.

    Returns the settings dict passed to Blender's exporter, for the build report.
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    exported = selection.resolve(objects)
    if not exported:
        raise ValueError("export: there are no objects to export")
    for obj in exported:
        if obj.type not in selection.MODEL_TYPES:
            raise ValueError(f"export: {obj.name} is a {obj.type}; only meshes, armatures, and empties are exported")
        if not obj.visible_get():
            raise ValueError(f"export: {obj.name} is hidden, and Blender exports only visible objects")
    clip_names = list(clips or [])

    settings = {
        "filepath": str(path),
        "export_format": "GLB",
        "use_selection": True,
        # Modifiers are applied, so what the file holds is what the checks and the sheet saw.
        "export_apply": True,
        "export_yup": True,
        "export_cameras": False,
        "export_lights": False,
        # The engine reads no compressed meshes and no gltfpack output, and no KTX2 or Basis images.
        "export_draco_mesh_compression_enable": False,
        "export_meshopt_compression_enable": False,
        "export_use_gltfpack": False,
        "export_image_format": "AUTO",
        "export_image_add_webp": False,
        "export_keep_originals": False,
        # Skinning: one skin, at most four joint influences a vertex.
        "export_skins": True,
        "export_influence_nb": 4,
        "export_all_influences": False,
        "export_morph": False,
        # Blender's default ("MATERIAL") exports colour attributes nobody asked for, so they are off unless requested.
        "export_vertex_color": "ACTIVE" if vertex_colors else "NONE",
        "export_tangents": _uses_normal_map(exported),
        "export_animations": bool(clip_names),
        "export_animation_mode": "ACTIONS",
        # A clip starts at time 0, not at the time of its first frame (a clip keyed from frame 1 would start at 1/24 s).
        "export_anim_slide_to_zero": True,
        # With this on, Blender exports every action of the file, not only the asked-for ones.
        "export_anim_single_armature": False,
        "export_action_filter": False,
    }

    with _selected(exported), _only_clips(exported, clip_names):
        result = bpy.ops.export_scene.gltf(**settings)
    if "FINISHED" not in result:
        raise RuntimeError(f"export: Blender's glTF exporter returned {result}")
    return settings


def _uses_normal_map(objects) -> bool:
    """True when a material of the exported meshes has a Normal Map node that feeds a shader."""
    for obj in objects:
        if obj.type != "MESH":
            continue
        for slot in obj.material_slots:
            tree = slot.material.node_tree if slot.material else None
            if tree is None:
                continue
            for node in tree.nodes:
                if node.type == "NORMAL_MAP" and any(output.is_linked for output in node.outputs):
                    return True
    return False


@contextlib.contextmanager
def _selected(objects):
    """Selects exactly the given objects for the export, then puts the old selection back."""
    view_layer = bpy.context.view_layer
    old_selection = [obj for obj in view_layer.objects if obj.select_get()]
    old_active = view_layer.objects.active
    try:
        for obj in view_layer.objects:
            obj.select_set(False)
        for obj in objects:
            obj.select_set(True)
        view_layer.objects.active = objects[0]
        yield
    finally:
        for obj in view_layer.objects:
            obj.select_set(obj in old_selection)
        view_layer.objects.active = old_active


@contextlib.contextmanager
def _only_clips(objects, clip_names):
    """Makes the exporter see exactly the named actions, then restores the scene's animation state.

    Active actions are cleared and existing NLA strips muted, so nothing else is exported. Each named action is stashed
    on a new NLA track of the object it animates.
    """
    saved_actions = []
    saved_strips = []
    new_tracks = []
    try:
        if clip_names:
            for obj in objects:
                data = obj.animation_data
                if data is None:
                    continue
                saved_actions.append((obj, data.action, data.action_slot))
                data.action = None
                for track in data.nla_tracks:
                    for strip in track.strips:
                        saved_strips.append((strip, strip.mute))
                        strip.mute = True
            for clip_name in clip_names:
                action = bpy.data.actions.get(clip_name)
                if action is None:
                    raise ValueError(f"export: there is no action named {clip_name}")
                targets = _targets_of(action, objects, saved_actions)
                if not targets:
                    raise ValueError(
                        f"export: the action {clip_name} animates none of the exported objects "
                        "(it needs an armature among them, or an object it is assigned to)"
                    )
                for obj in targets:
                    new_tracks.append((obj, _stash(obj, action)))
        yield
    finally:
        for obj, track in new_tracks:
            obj.animation_data.nla_tracks.remove(track)
        for strip, muted in saved_strips:
            strip.mute = muted
        for obj, action, slot in saved_actions:
            obj.animation_data.action = action
            if action is not None:
                obj.animation_data.action_slot = slot


def _targets_of(action, objects, saved_actions) -> list:
    """The objects an action animates: the exported armatures, and any object it was assigned to."""
    assigned = {id(obj) for obj, old_action, _ in saved_actions if old_action is action}
    return [obj for obj in objects if obj.type == "ARMATURE" or id(obj) in assigned]


def _stash(obj, action):
    """Adds an NLA track with one strip of the action to the object. Returns the track."""
    slot = _slot_for(obj, action)
    if obj.animation_data is None:
        obj.animation_data_create()
    track = obj.animation_data.nla_tracks.new()
    track.name = action.name
    strip = track.strips.new(action.name, int(action.frame_range[0]), action)
    strip.action_slot = slot
    return track


def _slot_for(obj, action):
    """The action's slot for an object: the one named for it, else the first slot that animates objects."""
    object_slots = [slot for slot in action.slots if slot.target_id_type == "OBJECT"]
    if not object_slots:
        raise ValueError(f"export: the action {action.name} has no slot that animates objects")
    for slot in object_slots:
        if slot.identifier == "OB" + obj.name:
            return slot
    return object_slots[0]
