"""Animation clips for recipes: new actions, posed keys, baking, and retargeting between rigs.

Blender 5 stores an action's curves per slot, and an action assigned to an object needs a slot. `new_clip` makes
both. The glTF exporter writes one animation per action, named as the action, so name each clip as the engine
should know it.
"""

from __future__ import annotations

import math

import bpy
from bpy_extras import anim_utils
from mathutils import Matrix


def new_clip(
    armature: bpy.types.Object,
    name: str,
    frame_start: int = 1,
    *,
    frame_end: int,
    fps: int | None = None,
) -> bpy.types.Action:
    """Makes an action named `name`, with a slot for `armature`, assigns it, and returns it.

    The frame range is fixed to `frame_start` to `frame_end`, so the export samples exactly that. `fps` sets the
    scene's frame rate (the engine plays a clip at the rate its keys were made for); None leaves it. The action
    keeps a fake user, so an earlier clip is not lost when the next one is assigned, and the exporter writes
    every action of the armature.
    """
    if name in bpy.data.actions:
        raise ValueError(f"An action named '{name}' exists already")
    if frame_end < frame_start:
        raise ValueError(f"frame_end {frame_end} is before frame_start {frame_start}")
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    slot = action.slots.new(id_type="OBJECT", name=armature.name)
    armature.animation_data_create()
    armature.animation_data.action = action
    armature.animation_data.action_slot = slot
    action.use_frame_range = True
    action.frame_start = frame_start
    action.frame_end = frame_end
    if fps is not None:
        bpy.context.scene.render.fps = fps
    return action


def key_pose(armature: bpy.types.Object, frame: float, pose: dict[str, dict]) -> None:
    """Keys bones at `frame`. `pose` maps a bone name to a dict with any of `location`, `rotation`, `scale`.

    Values are in the bone's own pose space (the bone's rest pose is zero movement). `rotation` is a quaternion
    (w, x, y, z). Only the channels given are keyed; bones not named are not touched. The armature needs an
    assigned action, as `new_clip` makes.
    """
    if armature.animation_data is None or armature.animation_data.action is None:
        raise ValueError(f"{armature.name} has no action to key; call new_clip first")
    for bone_name, channels in pose.items():
        bone = armature.pose.bones.get(bone_name)
        if bone is None:
            raise ValueError(f"{armature.name} has no bone '{bone_name}'")
        unknown = set(channels) - {"location", "rotation", "scale"}
        if unknown:
            raise ValueError(f"Unknown channels {sorted(unknown)} for bone '{bone_name}'")
        group = bone_name
        if "location" in channels:
            bone.location = channels["location"]
            bone.keyframe_insert("location", frame=frame, group=group)
        if "rotation" in channels:
            bone.rotation_mode = "QUATERNION"
            bone.rotation_quaternion = channels["rotation"]
            bone.keyframe_insert("rotation_quaternion", frame=frame, group=group)
        if "scale" in channels:
            bone.scale = channels["scale"]
            bone.keyframe_insert("scale", frame=frame, group=group)


def bake(armature: bpy.types.Object, frame_start: int, frame_end: int, step: int = 1) -> bpy.types.Action:
    """Bakes the armature's constraints and IK into plain keys on every bone, and returns its action.

    Visual keying records each bone's final pose at every `step` frames; the constraints are then removed. The
    glTF export has no constraints, so bake before exporting a rig that uses them. The current action is
    overwritten, or a new one is made when the armature has none.
    """
    previous_active = bpy.context.view_layer.objects.active
    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set(mode="POSE")
    try:
        bpy.ops.pose.select_all(action="SELECT")
        has_action = armature.animation_data is not None and armature.animation_data.action is not None
        bpy.ops.nla.bake(
            frame_start=frame_start,
            frame_end=frame_end,
            step=step,
            only_selected=True,
            visual_keying=True,
            clear_constraints=True,
            use_current_action=has_action,
            bake_types={"POSE"},
        )
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")
        if previous_active is not None:
            bpy.context.view_layer.objects.active = previous_active
    return armature.animation_data.action


def retarget(
    source: bpy.types.Object,
    target: bpy.types.Object,
    joint_map: dict[str, str],
    frame_start: int,
    frame_end: int,
    hips: tuple[str, str] | None = None,
    name: str | None = None,
    rest_frame: int | None = None,
    feet: tuple[str, ...] | None = None,
) -> bpy.types.Action:
    """Copies the source armature's animation onto the target armature, and returns the new action on the target.

    `joint_map` maps source bone names to target bone names. For each mapped joint and frame, the source joint's
    rotation change from its own rest pose, measured in world space, is applied to the target joint's rest pose
    and converted into the target parent's local space. Rest poses and bone rolls may differ. Target bones not
    in the map keep their rest pose.

    `hips` is a `(source bone, target bone)` pair. That target bone also gets the source bone's movement from
    its rest position, scaled by the ratio of the two bones' rest heights, so a short body does not slide on
    a tall body's strides. The pair should be in `joint_map` too, so the hips rotate. Without `hips` no bone
    moves, only turns.

    The action is named `name`, or `<source action>_retarget`. Frames are sampled one by one from the scene. The
    source's own action stays in the file, and the glTF exporter writes every action that has bone curves, so
    remove it with `remove_clip` (or delete the source rig and its actions) before exporting the target.

    Heights for the hips ratio are measured from each armature object's origin, which should be at the feet.

    The source's rest pose must match the target's (a T-pose to a T-pose). `rest_frame` takes the source's pose at that
    frame as its rest instead of its skeleton's own: for a source whose skeleton rests in another pose but whose
    animation holds a T-pose, as the CMU set's BVH conversion does in its first frame.

    `feet` names the source's bones that stand on the ground. With them, heights are measured from the ground, not the
    armature objects' origins, for a source whose capture puts its floor anywhere: each rig's hips height is taken over
    its rest's lowest joint, the source's ground is its feet's lowest point over the frames retargeted, and the target's
    hips stand as high over its rest's lowest joint as the source's over that ground, by the ratio.
    """
    if source.animation_data is None or source.animation_data.action is None:
        raise ValueError(f"{source.name} has no animation to retarget")
    for source_bone, target_bone in joint_map.items():
        if source_bone not in source.data.bones:
            raise ValueError(f"{source.name} has no bone '{source_bone}'")
        if target_bone not in target.data.bones:
            raise ValueError(f"{target.name} has no bone '{target_bone}'")
    hips_source, hips_target = hips if hips is not None else (None, None)
    if hips is not None and (hips_source not in source.data.bones or hips_target not in target.data.bones):
        raise ValueError(f"hips {hips} are not bones of the source and target")

    clip_name = name or f"{source.animation_data.action.name}_retarget"
    action = new_clip(target, clip_name, frame_start, frame_end=frame_end)
    for bone in target.pose.bones:
        bone.rotation_mode = "QUATERNION"

    source_world = source.matrix_world
    target_world = target.matrix_world
    target_world_rotation = target_world.to_3x3().normalized()

    # Rest data, read once. Armature-space rest matrices of every bone.
    scene = bpy.context.scene
    original_frame = scene.frame_current
    if rest_frame is None:
        source_rest = {n: source_world @ source.data.bones[n].matrix_local for n in joint_map}
    else:
        scene.frame_set(rest_frame)
        source_rest = {n: source_world @ source.pose.bones[n].matrix for n in joint_map}
        if hips is not None and hips_source not in source_rest:
            source_rest[hips_source] = source_world @ source.pose.bones[hips_source].matrix
    target_rest = {bone.name: bone.matrix_local.copy() for bone in target.data.bones}
    order = _parent_first(target)
    hip_ratio = 1.0
    hips_source_rest_position = hips_target_rest_position = None
    if hips is not None:
        hips_source_rest_position = (
            source_rest[hips_source] if hips_source in source_rest
            else source_world @ source.data.bones[hips_source].matrix_local
        ).translation.copy()
        hips_target_rest_position = target_world @ target_rest[hips_target].translation
        source_floor, target_floor = source_world.translation.z, target_world.translation.z
        if feet:
            source_ends = (
                [end for bone in source.pose.bones for end in (bone.head, bone.tail)]
                if rest_frame is not None
                else [end for bone in source.data.bones for end in (bone.head_local, bone.tail_local)]
            )
            source_floor = min((source_world @ end).z for end in source_ends)
            target_floor = min(
                (target_world @ end).z for bone in target.data.bones for end in (bone.head_local, bone.tail_local)
            )
        source_height = hips_source_rest_position.z - source_floor
        target_height = hips_target_rest_position.z - target_floor
        if abs(source_height) < 1e-6:
            raise ValueError(f"Source bone '{hips_source}' rests at floor height, so its movement cannot be scaled")
        hip_ratio = target_height / source_height
        if feet:
            # The source's ground: its feet's lowest point over the frames retargeted.
            ground = math.inf
            for frame in range(frame_start, frame_end + 1):
                scene.frame_set(frame)
                for foot in feet:
                    bone = source.pose.bones[foot]
                    ground = min(ground, (source_world @ bone.head).z, (source_world @ bone.tail).z)

    reverse = {target_bone: source_bone for source_bone, target_bone in joint_map.items()}
    try:
        for frame in range(frame_start, frame_end + 1):
            scene.frame_set(frame)
            deltas = {}
            for source_bone in joint_map:
                world = source_world @ source.pose.bones[source_bone].matrix
                deltas[source_bone] = (
                    world.to_3x3().normalized() @ source_rest[source_bone].to_3x3().normalized().inverted()
                )
            hips_offset = None
            if hips is not None:
                pose_position = (source_world @ source.pose.bones[hips_source].matrix).translation
                hips_offset = (pose_position - hips_source_rest_position) * hip_ratio  # world space
                if feet:
                    hips_offset.z = (
                        target_floor + (pose_position.z - ground) * hip_ratio - hips_target_rest_position.z
                    )

            pose_matrices: dict[str, Matrix] = {}
            for bone_name in order:
                bone = target.data.bones[bone_name]
                parent_name = bone.parent.name if bone.parent else None
                if parent_name is None:
                    rest_relative = target_rest[bone_name]
                    parent_pose = Matrix.Identity(4)
                else:
                    rest_relative = target_rest[parent_name].inverted() @ target_rest[bone_name]
                    parent_pose = pose_matrices[parent_name]
                unposed = parent_pose @ rest_relative
                source_bone = reverse.get(bone_name)
                if source_bone is None:
                    pose_matrices[bone_name] = unposed
                    continue

                rotation_world = (
                    deltas[source_bone] @ target_world_rotation @ target_rest[bone_name].to_3x3().normalized()
                )
                rotation = target_world_rotation.inverted() @ rotation_world
                pose = rotation.to_4x4()
                pose.translation = unposed.translation
                if bone_name == hips_target:
                    pose.translation = unposed.translation + target_world.inverted().to_3x3() @ hips_offset
                pose_matrices[bone_name] = pose

                basis = unposed.inverted() @ pose
                pose_bone = target.pose.bones[bone_name]
                pose_bone.rotation_quaternion = basis.to_quaternion()
                pose_bone.keyframe_insert("rotation_quaternion", frame=frame, group=bone_name)
                if bone_name == hips_target:
                    pose_bone.location = basis.to_translation()
                    pose_bone.keyframe_insert("location", frame=frame, group=bone_name)
    finally:
        scene.frame_set(original_frame)
    return action


def remove_clip(name: str) -> None:
    """Deletes the action `name` from the file, unassigning it from any armature first.

    The glTF exporter writes every action with bone curves that it finds, so a clip that must not be in the
    model, such as the source rig's animation after a retarget, has to go before the export.
    """
    action = bpy.data.actions.get(name)
    if action is None:
        raise ValueError(f"No action named '{name}'")
    for obj in bpy.data.objects:
        animation = obj.animation_data
        if animation is not None and animation.action is action:
            animation.action = None
    bpy.data.actions.remove(action)


def clip_keys(armature: bpy.types.Object, action: bpy.types.Action | None = None) -> dict[str, int]:
    """Returns the key count of each F-curve in an action, by data path and index (`pose.bones["a"].location[0]`)."""
    action = action or armature.animation_data.action
    channelbag = anim_utils.action_get_channelbag_for_slot(action, armature.animation_data.action_slot)
    if channelbag is None:
        return {}
    return {f"{curve.data_path}[{curve.array_index}]": len(curve.keyframe_points) for curve in channelbag.fcurves}


def _parent_first(armature: bpy.types.Object) -> list[str]:
    """Returns the armature's bone names with every bone after its parent."""
    names: list[str] = []

    def visit(bone):
        names.append(bone.name)
        for child in bone.children:
            visit(child)

    for bone in armature.data.bones:
        if bone.parent is None:
            visit(bone)
    return names
