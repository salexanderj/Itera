"""Checks of a model's clips: a looping clip ends as it starts, and a foot on the ground stays put.

Run inside Blender. check() samples each clip it names at every frame and returns plain numbers and a list of
sentences, `problems` (the build fails), as checks.scene does. A recipe names the clips in LOOPS, FEET,
and PLANTED (run_recipe.py), or calls check() itself with other limits. Each limit is a share of the rig's height at
rest (rest_height), so one rule suits a 0.3 m creature and a 6 m boss.

- A loop's last frame must hold its first pose, after the root's travel along the ground (a clip that moves the root).
  Its motion must also run on across the seam: no bone end may change its motion between the last and the first frame
  by more than SEAM_JOLT times the most it changes between two frames inside the clip.
- A foot is on the ground at a frame when the tail of its bone (the ball, for a foot bone; the tip, for a toe) is within
  CONTACT of its lowest height in the clip. Between two frames on the ground, its move along the ground is slide. The
  ground moves under it at the clip's own speed: the speed over the ground a body moves at while the clip plays at its
  own pace (forward is the engine's +z, Blender's -Y). A foot of a walk in place must move back at that speed. A game
  plays a stepping clip at its body's speed over this one, so the body's speed can be tuned without feet that slide.
- The clip's own speed is the recipe's number, or, given None, the speed its feet show on the ground (ground_speed),
  which the result gives either way.
"""

from __future__ import annotations

import math

import bpy

# Shares of the rig's height at rest.
LOOP_GAP = 0.005  # the farthest a bone end may be at a loop's last frame from where it was at its first
SEAM_FLOOR = 0.002  # a change of a bone end's motion across a loop's seam, a frame, that never counts as a jolt
CONTACT = 0.02  # a foot this near its lowest point in a clip is on the ground
SLIDE = 0.01  # the most a foot may slide in one stretch on the ground
SEAM_JOLT = 2.0  # times the most a bone end's motion changes between two frames inside a loop
FORWARD = (0.0, -1.0, 0.0)  # the model's front in Blender: the engine's +z


def check(
    armature,
    loops=(),
    feet=(),
    planted=None,
    *,
    loop_gap: float = LOOP_GAP,
    seam_jolt: float = SEAM_JOLT,
    contact: float = CONTACT,
    slide: float = SLIDE,
) -> dict:
    """Checks the clips in `loops` for closure, and the bones in `feet` for slide in each clip of `planted`.

    `planted` maps a clip's name to its own speed over the ground, in metres a second (0 for a clip in place, such as an
    idle), or to None for the speed its feet show. The limits are shares of the rig's height at rest. The result has
    the height, one entry for each clip (its frames and seconds, its loop's gap and seam, the speed its feet show, and
    each foot's slide), and the list `problems`. The armature's action and the scene's frame are put back afterwards.
    """
    planted = dict(planted or {})
    problems: list[str] = []
    height = rest_height(armature)
    result: dict = {"height": round(height, 4), "clips": {}, "problems": problems}

    missing = [name for name in feet if name not in armature.pose.bones]
    if missing:
        problems.append(f"{armature.name} has no bone {', '.join(missing)}, which FEET names")
    if planted and not feet:
        problems.append("PLANTED names clips, but FEET names no foot to check in them")
    if feet and not planted:
        problems.append("FEET names feet, but PLANTED names no clip to check them in")
    for name, speed in planted.items():
        if speed is not None and not isinstance(speed, (int, float)):
            problems.append(f"{name}: PLANTED gives it {speed!r}, not a speed in m/s or None")

    for name in dict.fromkeys([*loops, *planted]):
        action = bpy.data.actions.get(name)
        if action is None:
            problems.append(f"{name}: there is no clip of that name")
            continue
        points, frames, fps = sample(armature, action)
        if frames is None:
            problems.append(f"{name}: the clip has no slot that animates objects")
            continue
        entry: dict = {"frames": len(frames), "seconds": round((len(frames) - 1) / fps, 4)}
        if name in loops:
            entry["loop"] = _check_loop(name, armature, points, frames, height, loop_gap, seam_jolt, problems)
        speed = planted.get(name)
        if name in planted and feet and not missing and (speed is None or isinstance(speed, (int, float))):
            paths = {foot: [places[points.index(f"{foot} tail")] for places in frames] for foot in feet}
            shown = ground_speed(list(paths.values()), 1.0 / fps, contact * height)
            entry["speed"] = round(shown, 3)
            against = shown if speed is None else speed
            entry["feet"] = _check_feet(name, paths, fps, against, height, contact, slide, problems)
        result["clips"][name] = entry
    return result


def rest_height(armature) -> float:
    """The rig's height at rest: from its lowest bone end to its highest, in metres (1 for a flat rig)."""
    heights = []
    for bone in armature.data.bones:
        heights.append((armature.matrix_world @ bone.head_local).z)
        heights.append((armature.matrix_world @ bone.tail_local).z)
    span = max(heights) - min(heights) if heights else 0.0
    return span if span > 1e-6 else 1.0


def sample(armature, action) -> tuple[list[str], list[list[tuple]] | None, float]:
    """Every bone's head and tail in world space at each frame of `action`, and the scene's frame rate.

    The points are named "BONE head" and "BONE tail", in the armature's bone order. The frames are None when the
    action has no slot that animates objects.
    """
    scene = bpy.context.scene
    fps = scene.render.fps / scene.render.fps_base
    slots = [slot for slot in action.slots if slot.target_id_type == "OBJECT"]
    names = [f"{bone.name} {end}" for bone in armature.pose.bones for end in ("head", "tail")]
    if not slots:
        return names, None, fps
    slot = next((candidate for candidate in slots if candidate.identifier == "OB" + armature.name), slots[0])

    if armature.animation_data is None:
        armature.animation_data_create()
    data = armature.animation_data
    old_action, old_slot = data.action, data.action_slot
    old_frame, old_subframe = scene.frame_current, scene.frame_subframe
    frames: list[list[tuple]] = []
    try:
        data.action = action
        data.action_slot = slot
        start, end = (round(value) for value in action.frame_range)
        world = armature.matrix_world
        for frame in range(start, end + 1):
            scene.frame_set(frame)
            places = []
            for bone in armature.pose.bones:
                places.append(tuple(world @ bone.head))
                places.append(tuple(world @ bone.tail))
            frames.append(places)
    finally:
        data.action = old_action
        if old_action is not None:
            data.action_slot = old_slot
        scene.frame_set(old_frame, subframe=old_subframe)
    return names, frames, fps


def loop_closure(frames: list[list[tuple]], root: int | None = None) -> dict:
    """How far a loop is from closing, from the places of its points frame by frame (the first frame to the last).

    `gap` is the farthest any point is at the last frame from its place at the first, after the travel along the ground
    (x and y) of the point `root`, when one is given. For each point, `seam` is the change of its motion from the last
    two frames to the first two, and `inside` the most its motion changes between two frames inside the clip; the
    entry is the point whose seam is the largest share of its inside (`point`).
    """
    first, last = frames[0], frames[-1]
    travel = (0.0, 0.0, 0.0)
    if root is not None:
        travel = (last[root][0] - first[root][0], last[root][1] - first[root][1], 0.0)
    gaps = [math.dist(_minus(end, travel), start) for start, end in zip(first, last)]
    gap_point = max(range(len(gaps)), key=gaps.__getitem__)
    result = {"gap": gaps[gap_point], "gap_point": gap_point, "seam": 0.0, "inside": 0.0, "point": None}
    if len(frames) < 3:
        return result

    worst = -1.0
    for point in range(len(first)):
        path = [places[point] for places in frames]
        seam = math.dist(_minus(path[1], path[0]), _minus(path[-1], path[-2]))
        inside = max(
            math.dist(_minus(path[k + 1], path[k]), _minus(path[k], path[k - 1])) for k in range(1, len(path) - 1)
        )
        share = seam / inside if inside > 0 else (math.inf if seam > 0 else 0.0)
        if share > worst:
            worst = share
            result.update(seam=seam, inside=inside, point=point)
    return result


def foot_slide(path: list[tuple], seconds_per_frame: float, speed: float, contact: float) -> dict:
    """The most a foot slides in one stretch on the ground, from its places frame by frame.

    The foot is on the ground at a frame when it is within `contact` metres of its lowest height in the clip. A move
    between two frames on the ground counts, measured along the ground as the ground moves under the model at `speed`
    metres a second. `slide` is the longest such stretch's total, and `frames` how many frames the foot is on the
    ground.
    """
    lowest = min(place[2] for place in path)
    on_ground = [place[2] <= lowest + contact for place in path]
    ground_step = (FORWARD[0] * speed * seconds_per_frame, FORWARD[1] * speed * seconds_per_frame)
    longest = stretch = 0.0
    for k in range(1, len(path)):
        if on_ground[k - 1] and on_ground[k]:
            stretch += math.hypot(
                path[k][0] - path[k - 1][0] + ground_step[0], path[k][1] - path[k - 1][1] + ground_step[1]
            )
        else:
            longest = max(longest, stretch)
            stretch = 0.0
    return {"slide": max(longest, stretch), "frames": sum(on_ground)}


def _check_loop(name, armature, points, frames, height, loop_gap, seam_jolt, problems) -> dict:
    roots = [index for index, bone in enumerate(armature.pose.bones) if bone.parent is None]
    root = 2 * roots[0] if len(roots) == 1 else None
    closure = loop_closure(frames, root)
    if closure["gap"] > loop_gap * height:
        problems.append(
            f"{name}: its last frame is {_cm(closure['gap'])} from its first pose at {points[closure['gap_point']]}, "
            f"over the {_cm(loop_gap * height)} a loop may be off"
        )
    floor = SEAM_FLOOR * height
    if closure["point"] is not None and closure["seam"] > max(seam_jolt * closure["inside"], floor):
        problems.append(
            f"{name}: at the loop's seam, the motion of {points[closure['point']]} changes by "
            f"{_cm(closure['seam'])} a frame, over {seam_jolt:g} times the most it changes inside the clip "
            f"({_cm(closure['inside'])} a frame)"
        )
    return {
        "gap": round(closure["gap"], 4),
        "seam": round(closure["seam"], 4),
        "inside": round(closure["inside"], 4),
    }


def ground_speed(paths: list[list[tuple]], seconds_per_frame: float, contact: float) -> float:
    """The speed a clip's feet show on the ground: how fast, on average, they move back while on it, in m/s.

    `paths` holds each foot's places frame by frame. A foot is on the ground as foot_slide says, and only moves between
    two frames on the ground count. 0 when no foot is on the ground at two frames in a row.
    """
    back = 0.0
    steps = 0
    for path in paths:
        lowest = min(place[2] for place in path)
        for k in range(1, len(path)):
            if path[k - 1][2] <= lowest + contact and path[k][2] <= lowest + contact:
                back -= (path[k][0] - path[k - 1][0]) * FORWARD[0] + (path[k][1] - path[k - 1][1]) * FORWARD[1]
                steps += 1
    return back / (steps * seconds_per_frame) if steps else 0.0


def _check_feet(name, paths, fps, speed, height, contact, slide, problems) -> dict:
    found = {}
    for foot, path in paths.items():
        measured = foot_slide(path, 1.0 / fps, speed, contact * height)
        found[foot] = {"slide": round(measured["slide"], 4), "frames_on_ground": measured["frames"]}
        if measured["slide"] > slide * height:
            problems.append(
                f"{name}: {foot} slides {_cm(measured['slide'])} in one stretch on the ground at {speed:.2f} m/s, "
                f"over the {_cm(slide * height)} a foot may slide"
            )
    return found


def _minus(a: tuple, b: tuple) -> tuple:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cm(metres: float) -> str:
    return f"{metres * 100:.1f} cm"
