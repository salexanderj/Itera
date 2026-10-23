"""Clips keyed in code by poses, for a humanoid rig. Run inside Blender.

At a few key times a recipe places the hips, bends the spine and the neck, and puts each hand and foot where it should
be; the arms and legs reach there by IK, each elbow and knee pointing the way the key gives. The fingers keep the
stand's pose throughout. The keys are eased as each one asks for the time to the next, baked to plain keys on every
bone, and checked (`check`): what goes under the ground, a hand or foot that cannot reach its place, a knee or elbow
bent past a person's, a foot that slides where it should stand, and how far the first and last poses lie from the
stand.

Places are the model's metres and axes, from its origin on the ground: +left, +up, +front (Blender's +x, +z, -y). Turns
are degrees from how the part stands: `bend` forward, `lean` to its left, `twist` to its left about the up axis. Every
clip starts from the stand, a clip of the model's own at its first frame; a part moves between the keys that name it
and holds after the last. A key's `ease` shapes the time from it to the next: "both" eases out and in, "in" speeds up
into the next key (a blow), "out" slows into it, "linear" keeps one speed, and "hold" stays until the next.

`mirrored` gives a clip's keys for the other side: left and right swapped, the left axis and the turns about up and
front reversed, so a one-handed blow is authored once.
"""

from __future__ import annotations

import itertools
import math
from dataclasses import dataclass, field, replace

import bpy
from bpy_extras import anim_utils
from mathutils import Matrix, Quaternion, Vector

from . import clips

#: The eases a key may ask for, as Blender's interpolation and easing.
EASES = {
    "both": ("BEZIER", "AUTO"),
    "in": ("CUBIC", "EASE_IN"),
    "out": ("CUBIC", "EASE_OUT"),
    "linear": ("LINEAR", "AUTO"),
    "hold": ("CONSTANT", "AUTO"),
}

#: How far from the wrist or ankle the pole that aims an elbow or a knee stands, in the model's metres.
POLE_METRES = 0.5


@dataclass(frozen=True)
class Turn:
    """A turn from how a part stands, in degrees: forward, to its left, and about the up axis to its left."""

    bend: float = 0.0
    lean: float = 0.0
    twist: float = 0.0

    def matrix(self) -> Matrix:
        """The turn as a 3x3 matrix in Blender's axes."""
        return (
            Matrix.Rotation(math.radians(self.twist), 3, "Z")
            @ Matrix.Rotation(math.radians(self.bend), 3, "X")
            @ Matrix.Rotation(math.radians(self.lean), 3, "Y")
        )

    def mirrored(self) -> Turn:
        return Turn(self.bend, -self.lean, -self.twist)

    def scaled(self, share: float) -> Turn:
        return Turn(self.bend * share, self.lean * share, self.twist * share)


@dataclass(frozen=True)
class Hand:
    """Where a hand is: its wrist `at`; or moved `by` metres from where it stands, on its own side; or reaching the way
    `reach` from its shoulder as the key leaves it, `extend` of the arm's length (none of them: where it stands). Then
    the way its elbow points, the way its knuckles point and its palm faces (None for as it stands), and its
    collarbone's turn. Its fingers stay as they stand."""

    at: tuple | None = None
    elbow: tuple | None = None
    knuckles: tuple | None = None
    palm: tuple | None = None
    shoulder: Turn = Turn()
    by: tuple | None = None
    reach: tuple | None = None
    extend: float = 0.95

    def mirrored(self) -> Hand:
        return replace(
            self,
            at=_mirror(self.at),
            by=_mirror(self.by),
            reach=_mirror(self.reach),
            elbow=_mirror(self.elbow),
            knuckles=_mirror(self.knuckles),
            palm=_mirror(self.palm),
            shoulder=self.shoulder.mirrored(),
        )


@dataclass(frozen=True)
class Foot:
    """Where a foot is: its ankle `at`, or moved `by` metres from where it stands, on its own side (neither: where it
    stands), the way its knee points (None for as it stands), and its turn from as it stands (`bend` brings its toes
    down)."""

    at: tuple | None = None
    knee: tuple | None = None
    turn: Turn = Turn()
    by: tuple | None = None

    def mirrored(self) -> Foot:
        return replace(
            self, at=_mirror(self.at), by=_mirror(self.by), knee=_mirror(self.knee), turn=self.turn.mirrored()
        )


@dataclass(frozen=True)
class Body:
    """The trunk: the hips moved from where they stand (left, up, front) and turned, the spine's turn shared over its
    joints, and the neck's shared over the neck and the head."""

    hips: tuple = (0.0, 0.0, 0.0)
    hips_turn: Turn = Turn()
    spine: Turn = Turn()
    neck: Turn = Turn()

    def mirrored(self) -> Body:
        return Body(_mirror(self.hips), self.hips_turn.mirrored(), self.spine.mirrored(), self.neck.mirrored())


@dataclass(frozen=True)
class Key:
    """One key pose at `seconds`: the parts it names, and the ease from it to the next key. A part a key leaves out
    (None) moves on between the keys that name it; the first key leaves out only what stands."""

    seconds: float
    body: Body | None = None
    hand_l: Hand | None = None
    hand_r: Hand | None = None
    foot_l: Foot | None = None
    foot_r: Foot | None = None
    ease: str = "both"

    def mirrored(self) -> Key:
        def flip(part):
            return None if part is None else part.mirrored()

        return Key(
            self.seconds,
            body=flip(self.body),
            hand_l=flip(self.hand_r),
            hand_r=flip(self.hand_l),
            foot_l=flip(self.foot_r),
            foot_r=flip(self.foot_l),
            ease=self.ease,
        )


def mirrored(keys: list) -> list:
    """The keys of a clip on the other side."""
    return [key.mirrored() for key in keys]


@dataclass(frozen=True)
class Rig:
    """A humanoid rig's joints by what they are: the hips, the spine and the neck (lowest first, the head last), and on
    each side ("l", "r") the collarbone, the arm (upper, lower, hand), the fingers (each a chain, knuckle out), the
    thumb, the leg (thigh, calf, foot), and the toes."""

    hips: str
    spine: tuple
    neck: tuple
    collarbones: dict
    arms: dict
    fingers: dict
    thumbs: dict
    legs: dict
    toes: dict


def _side(side: str, names: tuple) -> tuple:
    return tuple(f"{name}_{side}" for name in names)


#: The joints of the Unreal-style mannequin (Quaternius's human, the game's human model).
MANNEQUIN = Rig(
    hips="pelvis",
    spine=("spine_01", "spine_02", "spine_03"),
    neck=("neck_01", "Head"),
    collarbones={side: f"clavicle_{side}" for side in "lr"},
    arms={side: _side(side, ("upperarm", "lowerarm", "hand")) for side in "lr"},
    fingers={
        side: tuple(
            _side(side, (f"{finger}_01", f"{finger}_02", f"{finger}_03"))
            for finger in ("index", "middle", "ring", "pinky")
        )
        for side in "lr"
    },
    thumbs={side: _side(side, ("thumb_01", "thumb_02", "thumb_03")) for side in "lr"},
    legs={side: _side(side, ("thigh", "calf", "foot")) for side in "lr"},
    toes={side: f"ball_{side}" for side in "lr"},
)


def to_blender(place) -> Vector:
    """A place or a way in the model's axes (left, up, front) in Blender's."""
    left, up, front = place
    return Vector((left, -front, up))


def to_model(vector) -> tuple:
    """A Blender vector in the model's axes (left, up, front)."""
    return (vector.x, vector.z, -vector.y)


def _mirror(place):
    return None if place is None else (-place[0], place[1], place[2])


@dataclass
class _Limb:
    """An arm or a leg reaching by IK: its joints, its target and pole, and where it stood."""

    side: str
    kind: str
    joints: tuple
    target: bpy.types.Object = None
    pole: bpy.types.Object = None
    stand_turn: Quaternion = None
    stand_bend: Vector = None


@dataclass
class Made:
    """A clip made: its action, its length in frames, and the keys' places a hand or foot could not reach."""

    name: str
    action: bpy.types.Action
    frames: int
    misses: list = field(default_factory=list)


class Keyer:
    """Makes clips on `armature` from key poses, each starting from the stand: the clip `stand` at its first frame."""

    def __init__(self, armature: bpy.types.Object, stand: str, rig: Rig = MANNEQUIN, fps: int = 30) -> None:
        self.armature = armature
        self.rig = rig
        self.fps = fps
        bpy.context.scene.render.fps = fps
        bpy.context.scene.render.fps_base = 1.0
        action = bpy.data.actions.get(stand)
        if action is None:
            raise ValueError(f"the model has no clip {stand} to stand in")
        armature.animation_data_create()
        data = armature.animation_data
        data.action = action
        data.action_slot = action.slots[0]
        bpy.context.scene.frame_set(int(action.frame_range[0]))
        bpy.context.view_layer.update()
        bones = armature.pose.bones
        self.stand_basis = {bone.name: bone.matrix_basis.copy() for bone in bones}
        self.stand = {bone.name: (armature.matrix_world @ bone.matrix).copy() for bone in bones}
        data.action = None
        for bone in bones:
            bone.matrix_basis = self.stand_basis[bone.name]
        bpy.context.view_layer.update()

    # The stand.

    def stand_place(self, joint: str) -> tuple:
        """Where a joint's head stands, in the model's axes."""
        return to_model(self.stand[joint].translation)

    def stand_head(self, joint: str) -> Vector:
        return self.stand[joint].translation.copy()

    def ankle_for(self, side: str, toes: tuple, turn: Turn | None = None) -> tuple:
        """Where a foot's ankle goes so its toes' joint lies at `toes` with the foot turned by `turn` (None: as it
        stands) from as it stands, in the model's axes: for a foot on its toes, kneeling."""
        ankle, ball = self.stand_head(self.rig.legs[side][2]), self.stand_head(self.rig.toes[side])
        turned = (turn or Turn()).matrix()
        return to_model(to_blender(toes) - turned @ (ball - ankle))

    # Clips.

    def clip(self, name: str, keys: list) -> Made:
        """Makes the clip `name` from `keys` (by time, the first at 0), baked to plain keys on every bone."""
        if not keys or keys[0].seconds != 0:
            raise ValueError(f"{name}: its first key must be at 0 s")
        for before, after in itertools.pairwise(keys):
            if after.seconds <= before.seconds:
                raise ValueError(f"{name}: its keys must come in time, {after.seconds} s after {before.seconds} s")
        for key in keys:
            if key.ease not in EASES:
                raise ValueError(f"{name}: no ease '{key.ease}' (one of {', '.join(EASES)})")
            if abs(key.seconds * self.fps - round(key.seconds * self.fps)) > 1e-6:
                raise ValueError(f"{name}: its key at {key.seconds} s falls between frames at {self.fps} a second")
        last = round(keys[-1].seconds * self.fps)
        action = clips.new_clip(self.armature, name, 0, frame_end=last)
        limbs = self._limbs()
        made = Made(name, action, last)
        try:
            previous: dict = {}
            frames = []
            for number, key in enumerate(keys):
                frame = round(key.seconds * self.fps)
                frames.append((frame, key.ease))
                first = number == 0
                self._key_body(frame, key, first, previous)
                bpy.context.scene.frame_set(frame)
                bpy.context.view_layer.update()
                for limb in limbs:
                    part = getattr(key, f"{'hand' if limb.kind == 'arm' else 'foot'}_{limb.side}")
                    if part is not None or first:
                        self._key_limb(limb, frame, part, previous)
            self._ease(action, [limb.target for limb in limbs] + [limb.pole for limb in limbs], frames)
            for frame, _ in frames:
                bpy.context.scene.frame_set(frame)
                bpy.context.view_layer.update()
                for limb in limbs:
                    reached = self._reached(limb)
                    if reached > 0.01:
                        made.misses.append(self._miss(limb, frame, reached))
            clips.bake(self.armature, 0, last)
        finally:
            for limb in limbs:
                for empty in (limb.target, limb.pole):
                    if empty is not None:
                        if empty.animation_data and empty.animation_data.action:
                            bpy.data.actions.remove(empty.animation_data.action)
                        bpy.data.objects.remove(empty, do_unlink=True)
            for bone in self.armature.pose.bones:
                for constraint in list(bone.constraints):
                    bone.constraints.remove(constraint)
        made.action = self.armature.animation_data.action
        return made

    # Keys.

    def _key_body(self, frame: int, key: Key, first: bool, previous: dict) -> None:
        """Keys the hips, spine, and neck as the key's body turns them, and each collarbone as its hand's turns it,
        where the key names them; at the first key, every other bone as it stands, the fingers among them (the arms and
        legs too, which IK and the copied turns then move)."""
        rig = self.rig
        bones = self.armature.pose.bones
        if first:
            moved = set(rig.spine) | set(rig.neck) | {rig.hips} | set(rig.collarbones.values())
            for bone in bones:
                if bone.name not in moved:
                    self._key_basis(bone, self.stand_basis[bone.name], frame, previous)
        for side in "lr":
            hand = getattr(key, f"hand_{side}")
            if hand is not None or first:
                shoulder = hand.shoulder if hand is not None else Turn()
                collarbone = rig.collarbones[side]
                self._key_basis(bones[collarbone], self._turned(collarbone, shoulder.matrix()), frame, previous)
        body = key.body if key.body is not None else (Body() if first else None)
        if body is None:
            return

        # The hips: moved and turned in the model's axes, from where they stand.
        hips = bones[rig.hips]
        basis = self._turned(rig.hips, body.hips_turn.matrix())
        stand = self.stand[rig.hips]
        frame_turn = stand.to_3x3() @ self.stand_basis[rig.hips].to_3x3().inverted()
        offset = frame_turn.inverted() @ to_blender(body.hips)
        basis.translation = self.stand_basis[rig.hips].translation + offset
        self._key_basis(hips, basis, frame, previous)

        for chain, turn in ((rig.spine, body.spine), (rig.neck, body.neck)):
            share = turn.scaled(1.0 / len(chain)).matrix()
            for joint in chain:
                self._key_basis(bones[joint], self._turned(joint, share), frame, previous)

    def _turned(self, joint: str, turn: Matrix) -> Matrix:
        """A joint's basis turned by `turn` (Blender's axes) from how it stands, relative to its parent."""
        stand = self.stand[joint].to_3x3()
        basis = self.stand_basis[joint].to_3x3() @ (stand.inverted() @ turn @ stand)
        result = basis.to_4x4()
        result.translation = self.stand_basis[joint].translation
        return result

    def _palm(self, side: str) -> Vector:
        """The way the palm of a hand faces as it stands: from the knuckles' line, towards the thumb's side's palm."""
        rig = self.rig
        hand = self.stand[rig.arms[side][2]]
        index = self.stand[rig.fingers[side][0][0]].translation
        pinky = self.stand[rig.fingers[side][3][0]].translation
        along = (index + pinky) / 2 - hand.translation
        across = index - pinky
        normal = along.cross(across) if side == "l" else across.cross(along)
        thumb = self.stand[rig.thumbs[side][1]].translation - hand.translation
        # The palm faces the side the thumb's tip bends to.
        normal = normal.normalized()
        return normal if thumb.dot(normal) >= 0 else -normal

    def _key_basis(self, bone, basis: Matrix, frame: int, previous: dict) -> None:
        location, rotation, scale = basis.decompose()
        last = previous.get(bone.name)
        if last is not None and last.dot(rotation) < 0:
            rotation.negate()
        previous[bone.name] = rotation
        bone.rotation_mode = "QUATERNION"
        bone.location = location
        bone.rotation_quaternion = rotation
        bone.scale = scale
        group = bone.name
        bone.keyframe_insert("location", frame=frame, group=group)
        bone.keyframe_insert("rotation_quaternion", frame=frame, group=group)
        bone.keyframe_insert("scale", frame=frame, group=group)

    # Limbs.

    def _limbs(self) -> list:
        """An IK chain for each arm and leg, its target, pole, and turn copied, its pole's angle found at the stand."""
        limbs = []
        for kind, chains in (("arm", self.rig.arms), ("leg", self.rig.legs)):
            for side in "lr":
                limb = _Limb(side, kind, chains[side])
                _, lower, end = limb.joints
                bones = self.armature.pose.bones
                limb.target = _empty(f"keyed_{kind}_{side}_target")
                limb.pole = _empty(f"keyed_{kind}_{side}_pole")
                limb.stand_turn = self.stand[end].to_quaternion()
                start, middle, tip = (self.stand_head(joint) for joint in limb.joints)
                limb.stand_bend = _bend(start, middle, tip)
                ik = bones[lower].constraints.new("IK")
                ik.target = limb.target
                ik.pole_target = limb.pole
                ik.chain_count = 2
                ik.use_tail = True
                ik.use_stretch = False
                copy = bones[end].constraints.new("COPY_ROTATION")
                copy.target = limb.target
                copy.target_space = "WORLD"
                copy.owner_space = "WORLD"
                limb.target.location = tip
                limb.target.rotation_mode = "QUATERNION"
                limb.target.rotation_quaternion = limb.stand_turn
                limb.pole.location = tip + limb.stand_bend * POLE_METRES + (start - tip) / 2
                self._aim_pole(limb, ik, middle)
                limbs.append(limb)
        return limbs

    def _aim_pole(self, limb: _Limb, ik, middle: Vector) -> None:
        """The pole angle that keeps the elbow or knee where it stands, found by trying and narrowing."""
        bones = self.armature.pose.bones

        def miss(angle: float) -> float:
            ik.pole_angle = angle
            bpy.context.view_layer.update()
            return ((self.armature.matrix_world @ bones[limb.joints[1]].head) - middle).length

        best = min((math.radians(d) for d in range(-180, 180, 15)), key=miss)
        step = math.radians(7.5)
        while step > math.radians(0.05):
            best = min((best - step, best, best + step), key=miss)
            step /= 2
        miss(best)

    def _key_limb(self, limb: _Limb, frame: int, part, previous: dict) -> None:
        """Keys a limb's target and pole for its hand or foot (None: as it stands)."""
        start = (self.armature.matrix_world @ self.armature.pose.bones[limb.joints[0]].head).copy()
        if part is None:
            place = self.stand_head(limb.joints[2])
            turn = limb.stand_turn
            bend = limb.stand_bend
        else:
            if part.at is not None:
                place = to_blender(part.at)
            elif limb.kind == "arm" and part.reach is not None:
                place = start + to_blender(part.reach).normalized() * part.extend * self._length(limb)
            elif part.by is not None:
                place = self.stand_head(limb.joints[2]) + to_blender(part.by)
            else:
                place = self.stand_head(limb.joints[2])
            if limb.kind == "arm":
                turn = self._hand_turn(limb, part)
                bend = to_blender(part.elbow).normalized() if part.elbow is not None else limb.stand_bend
            else:
                turn = (part.turn.matrix() @ limb.stand_turn.to_matrix()).to_quaternion()
                bend = to_blender(part.knee).normalized() if part.knee is not None else limb.stand_bend
        target = limb.target
        target.location = place
        last = previous.get(target.name)
        if last is not None and last.dot(turn) < 0:
            turn = -turn
        previous[target.name] = turn
        target.rotation_quaternion = turn
        target.keyframe_insert("location", frame=frame)
        target.keyframe_insert("rotation_quaternion", frame=frame)
        limb.pole.location = (start + place) / 2 + bend * POLE_METRES
        limb.pole.keyframe_insert("location", frame=frame)

    def _hand_turn(self, limb: _Limb, hand: Hand) -> Quaternion:
        """The hand's turn: its knuckles and palm as the key gives them, else as it stands."""
        stand = limb.stand_turn.to_matrix()
        if hand.knuckles is None and hand.palm is None:
            return limb.stand_turn.copy()
        knuckles_stand = (stand @ Vector((0.0, 1.0, 0.0))).normalized()
        palm_stand = self._palm(limb.side)
        knuckles = to_blender(hand.knuckles).normalized() if hand.knuckles is not None else knuckles_stand
        palm = to_blender(hand.palm).normalized() if hand.palm is not None else palm_stand
        turn = _frame(knuckles, palm) @ _frame(knuckles_stand, palm_stand).inverted()
        return (turn @ stand).to_quaternion()

    def _miss(self, limb: _Limb, frame: int, short: float) -> dict:
        """A limb that falls short of its target: when, by how much, and where its root was, against its reach."""
        bones = self.armature.pose.bones
        root = self.armature.matrix_world @ bones[limb.joints[0]].head
        length = self._length(limb)
        target = limb.target.matrix_world.translation
        return {
            "seconds": round(frame / self.fps, 3),
            "joint": limb.joints[2],
            "short": round(short, 3),
            "root": tuple(round(v, 3) for v in to_model(root)),
            "distance": round((target - root).length, 3),
            "length": round(length, 3),
        }

    def _length(self, limb: _Limb) -> float:
        """A limb's length: its upper and lower bones'."""
        return sum((self.stand_head(a) - self.stand_head(b)).length for a, b in zip(limb.joints[1:], limb.joints))

    def _reached(self, limb: _Limb) -> float:
        """How far the limb's wrist or ankle is from its target now."""
        bone = self.armature.pose.bones[limb.joints[1]]
        return ((self.armature.matrix_world @ bone.tail) - limb.target.matrix_world.translation).length

    # Eases.

    def _ease(self, action, empties: list, frames: list) -> None:
        """Sets each key's ease on every curve the armature and the empties have keys on."""
        curves = list(_curves(self.armature))
        for empty in empties:
            curves.extend(_curves(empty))
        eases = dict(frames)
        for curve in curves:
            for point in curve.keyframe_points:
                ease = eases.get(round(point.co.x))
                if ease is None:
                    continue
                interpolation, easing = EASES[ease]
                point.interpolation = interpolation
                point.easing = easing
                if interpolation == "BEZIER":
                    point.handle_left_type = "AUTO_CLAMPED"
                    point.handle_right_type = "AUTO_CLAMPED"
            curve.update()


def _curves(obj):
    data = obj.animation_data
    if data is None or data.action is None or data.action_slot is None:
        return []
    channelbag = anim_utils.action_get_channelbag_for_slot(data.action, data.action_slot)
    return [] if channelbag is None else channelbag.fcurves


def _empty(name: str) -> bpy.types.Object:
    empty = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(empty)
    return empty


def _bend(start: Vector, middle: Vector, end: Vector) -> Vector:
    """The way a joint between start and end points, off the line between them."""
    line = (end - start).normalized()
    off = (middle - start) - line * (middle - start).dot(line)
    return off.normalized() if off.length > 1e-6 else Vector((0.0, -1.0, 0.0))


def _frame(along: Vector, facing: Vector) -> Matrix:
    """A turn whose columns are `along`, `facing` made square to it, and their cross."""
    y = along.normalized()
    z = (facing - y * facing.dot(y)).normalized()
    x = y.cross(z)
    return Matrix((x, y, z)).transposed()


# Checks.


def check(
    armature: bpy.types.Object,
    keyer: Keyer,
    made: Made,
    *,
    ground: tuple = (),
    planted: tuple = (),
    ends: float = 0.01,
    ground_slack: float = 0.02,
    slide: float = 0.01,
) -> tuple[dict, list]:
    """Checks a baked clip: returns its report and its problems.

    Every joint stays above the ground (`ground_slack` metres under it for the joints named in `ground`, which touch
    it, and the joints below them: a hand's fingers). Each joint named in `planted`, as (joint, from seconds, to
    seconds), moves no more than `slide` metres across the ground in that time. Knees and elbows bend no further than a
    person's, and the knees bend forwards. The first and last poses lie within `ends` metres of the stand at every
    joint.
    """
    rig = keyer.rig
    bones = armature.pose.bones
    names = [bone.name for bone in bones]
    touching = {bone.name for bone in bones if any(bone.name == joint or _below(bone, joint) for joint in ground)}
    places = []
    for frame in range(made.frames + 1):
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        places.append({name: (armature.matrix_world @ bones[name].head).copy() for name in names})
    fps = keyer.fps
    problems = []
    lowest = {}
    for name in names:
        frame, height = min(((f, place[name].z) for f, place in enumerate(places)), key=lambda item: item[1])
        lowest[name] = (round(height, 4), round(frame / fps, 3))
        slack = ground_slack if name in touching else 0.0
        # The toes' tips and the leaves are below the ankle's joint by design; check the joints that carry a body.
        if height < -slack - 1e-4 and not name.endswith("leaf_l") and not name.endswith("leaf_r"):
            problems.append(
                f"{made.name}: {name} goes {(-height) * 100:.1f} cm under the ground at {frame / fps:.2f} s"
            )
    for joint, start, end in planted:
        first, last = round(start * fps), round(end * fps)
        origin = places[first][joint]
        moved = max(
            math.hypot(place[joint].x - origin.x, place[joint].y - origin.y) for place in places[first : last + 1]
        )
        if moved > slide:
            problems.append(f"{made.name}: {joint} slides {moved * 100:.1f} cm from {start} to {end} s")
    worst = {}
    for side in "lr":
        for kind, chain, limit in (("knee", rig.legs[side], 160.0), ("elbow", rig.arms[side], 165.0)):
            upper, lower, end = chain
            most = (0.0, 0.0)
            for f, place in enumerate(places):
                bend = math.degrees((place[lower] - place[upper]).angle(place[end] - place[lower], 0.0))
                if bend > most[0]:
                    most = (bend, f / fps)
                if kind == "knee":
                    forward = _forward(place, rig)
                    middle = (place[upper] + place[end]) / 2
                    if (place[lower] - middle).dot(forward) < -0.02 and bend > 10:
                        problems.append(f"{made.name}: the {side} knee bends backwards at {f / fps:.2f} s")
                        break
            worst[f"{kind}_{side}"] = (round(most[0], 1), round(most[1], 3))
            if most[0] > limit:
                problems.append(f"{made.name}: the {side} {kind} bends {most[0]:.0f} degrees at {most[1]:.2f} s")
    stand = {name: keyer.stand[name].translation for name in names}
    start_off = max((places[0][name] - stand[name]).length for name in names)
    end_off = max((places[-1][name] - stand[name]).length for name in names)
    if start_off > ends or end_off > ends:
        problems.append(f"{made.name}: starts {start_off * 100:.1f} cm and ends {end_off * 100:.1f} cm from the stand")
    for miss in made.misses:
        problems.append(
            f"{made.name}: {miss['joint']} falls {miss['short'] * 100:.1f} cm short at {miss['seconds']} s "
            f"(its limb from {miss['root']}, {miss['length']:.3f} long, {miss['distance']:.3f} from its target)"
        )
    report = {
        "name": made.name,
        "seconds": round(made.frames / fps, 3),
        "frames": made.frames + 1,
        "lowest": {name: lowest[name] for name in sorted(lowest) if name in _watched(rig)},
        "bends": worst,
        "start_off": round(start_off, 4),
        "end_off": round(end_off, 4),
    }
    return report, problems


def place_at(armature: bpy.types.Object, joint: str, seconds: float, fps: int) -> tuple:
    """Where a joint's head is `seconds` into the armature's clip, in the model's axes."""
    frame = seconds * fps
    bpy.context.scene.frame_set(math.floor(frame), subframe=frame - math.floor(frame))
    bpy.context.view_layer.update()
    return tuple(round(v, 4) for v in to_model(armature.matrix_world @ armature.pose.bones[joint].head))


def _below(bone, joint: str) -> bool:
    """True when `joint` is one of the bone's parents."""
    parent = bone.parent
    while parent is not None:
        if parent.name == joint:
            return True
        parent = parent.parent
    return False


def _forward(place: dict, rig: Rig) -> Vector:
    """The way the hips face, from the line between the thighs."""
    left, right = place[rig.legs["l"][0]], place[rig.legs["r"][0]]
    across = left - right
    forward = across.cross(Vector((0.0, 0.0, 1.0)))
    return forward.normalized() if forward.length > 1e-6 else Vector((0.0, -1.0, 0.0))


def _watched(rig: Rig) -> set:
    watched = {rig.hips}
    for side in "lr":
        watched |= set(rig.arms[side]) | set(rig.legs[side]) | {rig.toes[side]}
    return watched
