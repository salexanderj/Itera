"""Reference poses for recipes: a humanoid model posed as its reference images are drawn, for the outline check and a
sheet to look at.

A reference is often drawn in a pose other than the rig's rest pose: arms hanging in an idle stance, or in the
anatomical position, while the game's rig rests in a T-pose. `held(armature, pose)` puts the armature in one of `POSES`
for as long as its `with` block lasts, and puts it back as it was:

- `rest`: the rig's own rest pose, nothing changed.
- `t-pose`: each upper arm straight out to its side, level, and the arm below it straight.
- `idle`: each arm hanging down, a little out from the body (`IDLE_SPREAD`), palms towards the thighs.
- `anatomical`: each arm hanging down close to the body (`ANATOMICAL_SPREAD`), palms forward.

Only the arms move; the legs keep the rest pose. An arm is found by its bones' names, each with its side marked
(`upperarm_l`, `upper_arm.L`, `LeftArm`): its upper arm, then the forearm and the hand below it, each straight on the
bone above it. The model faces Blender's -Y, with +Z up (the toolkit's export space), so out is along X, away from the
body's middle. `anatomical` turns each arm about itself so that the side of the hand that faces down at rest faces
forward: the palm, on a rig whose rest pose has the palms down, as the T-posed rigs this toolkit meets have.
"""

from __future__ import annotations

import math
import re
from contextlib import contextmanager

import bpy
from mathutils import Matrix, Vector

#: The poses `held` knows.
POSES = ("rest", "t-pose", "idle", "anatomical")

#: How far out from straight down an arm hangs in the idle pose, in degrees.
IDLE_SPREAD = 12.0

#: How far out from straight down an arm hangs in the anatomical position, in degrees.
ANATOMICAL_SPREAD = 6.0

# A bone's name with its side marks and rig prefixes taken off, by the arm's bone it names.
_UPPER_ARMS = {"upperarm", "uparm", "arm"}
_FOREARMS = {"lowerarm", "forearm"}
_HANDS = {"hand"}
_PREFIX = re.compile(r"^(mixamorig\d*[:_]|def[-_.]|org[-_.]|bip\d*[ _])", re.IGNORECASE)
_SIDE = re.compile(r"(^(left|right|l|r)(?=[^a-z])|(?<=[^a-z])(left|right|l|r)$|^(left|right)|(left|right)$)")

_FORWARD = Vector((0.0, -1.0, 0.0))
_DOWN = Vector((0.0, 0.0, -1.0))


def arm_chains(armature: bpy.types.Object) -> list[tuple[str, ...]]:
    """Each arm's bones found by name, the upper arm first, then the forearm and the hand where it has them.

    Raises ValueError for an armature with no arm found.
    """
    bones = armature.data.bones
    chains = []
    for bone in bones:
        if _core(bone.name) not in _UPPER_ARMS or _side(bone.name) is None:
            continue
        chain = [bone.name]
        forearm = _child(bone, _FOREARMS)
        if forearm is not None:
            chain.append(forearm.name)
            hand = _child(forearm, _HANDS)
            if hand is not None:
                chain.append(hand.name)
        chains.append(tuple(chain))
    if not chains:
        raise ValueError(f"{armature.name} has no bones named as arms (upperarm_l, upper_arm.L, LeftArm)")
    return chains


@contextmanager
def held(armature: bpy.types.Object, pose: str):
    """Holds `armature` in the reference pose `pose` (one of `POSES`) for the `with` block, then puts back its pose,
    its action, and whether it showed its pose or its rest."""
    if pose not in POSES:
        raise ValueError(f"Unknown pose '{pose}'. Choose from {', '.join(POSES)}")
    if pose == "rest":
        yield armature
        return

    data = armature.animation_data
    old_action = data.action if data is not None else None
    old_slot = data.action_slot if data is not None else None
    old_position = armature.data.pose_position
    old_bases = {bone.name: bone.matrix_basis.copy() for bone in armature.pose.bones}
    try:
        if data is not None:
            data.action = None
        armature.data.pose_position = "POSE"
        for bone in armature.pose.bones:
            bone.matrix_basis = Matrix.Identity(4)
        bpy.context.view_layer.update()
        for chain in arm_chains(armature):
            _pose_arm(armature, chain, pose)
        bpy.context.view_layer.update()
        yield armature
    finally:
        for bone in armature.pose.bones:
            bone.matrix_basis = old_bases[bone.name]
        armature.data.pose_position = old_position
        if data is not None:
            data.action = old_action
            if old_action is not None:
                data.action_slot = old_slot
        bpy.context.view_layer.update()


def armatures_of(objects) -> list[bpy.types.Object]:
    """The armatures among the objects and those that deform them, each once."""
    found: dict[str, bpy.types.Object] = {}
    for obj in objects:
        if obj.type == "ARMATURE":
            found[obj.name] = obj
        for modifier in getattr(obj, "modifiers", []):
            target = getattr(modifier, "object", None)
            if modifier.type == "ARMATURE" and target is not None:
                found[target.name] = target
    return list(found.values())


@contextmanager
def held_all(objects, pose: str):
    """Holds every armature of `objects` (`armatures_of`) in `pose` for the `with` block. With `rest`, or no armature,
    nothing changes."""
    armatures = armatures_of(objects) if pose != "rest" else []
    if not armatures:
        yield
        return
    with held(armatures[0], pose):
        if len(armatures) == 1:
            yield
        else:
            with held_all(armatures[1:], pose):
                yield


def _pose_arm(armature: bpy.types.Object, chain: tuple[str, ...], pose: str) -> None:
    """Aims the chain's upper arm as `pose` holds it, turning it about itself for the anatomical position."""
    upper = armature.pose.bones[chain[0]]
    rest = upper.bone
    to_armature = armature.matrix_world.inverted().to_3x3()
    head_world = armature.matrix_world @ rest.head_local
    middle = armature.matrix_world.translation
    out = Vector((1.0 if head_world.x >= middle.x else -1.0, 0.0, 0.0))
    if pose == "t-pose":
        way = out
    else:
        spread = math.radians(IDLE_SPREAD if pose == "idle" else ANATOMICAL_SPREAD)
        way = _DOWN * math.cos(spread) + out * math.sin(spread)
    way = (to_armature @ way).normalized()
    along = (rest.tail_local - rest.head_local).normalized()
    turn = along.rotation_difference(way)
    if pose == "anatomical":
        # The side that faced down at rest turned to face forward, about the arm's own way.
        down = _across(turn @ (to_armature @ _DOWN).normalized(), way)
        forward = _across((to_armature @ _FORWARD).normalized(), way)
        if down is not None and forward is not None:
            turn = down.rotation_difference(forward) @ turn
    head = rest.head_local
    upper.matrix = (
        Matrix.Translation(head) @ turn.to_matrix().to_4x4() @ Matrix.Translation(-head) @ rest.matrix_local
    )


def _across(vector: Vector, axis: Vector) -> Vector | None:
    """The part of `vector` across `axis`, normalised, or None where it lies along it."""
    across = vector - axis * vector.dot(axis)
    return across.normalized() if across.length > 1e-6 else None


def _child(bone: bpy.types.Bone, cores: set) -> bpy.types.Bone | None:
    """The first child of `bone` whose name, sides and prefixes taken off, is one of `cores`."""
    return next((child for child in bone.children if _core(child.name) in cores), None)


def _side(name: str) -> str | None:
    """`left` or `right` where the bone's name marks its side, else None."""
    match = _SIDE.search(_strip_prefix(name).lower())
    if match is None:
        return None
    mark = next(group for group in match.groups()[1:] if group)
    return "left" if mark.startswith("l") else "right"


def _core(name: str) -> str:
    """The bone's name in lower case, its rig prefix and side mark taken off and its letters alone kept."""
    stripped = _SIDE.sub("", _strip_prefix(name).lower(), count=1)
    return re.sub(r"[^a-z]", "", stripped)


def _strip_prefix(name: str) -> str:
    return _PREFIX.sub("", name)
