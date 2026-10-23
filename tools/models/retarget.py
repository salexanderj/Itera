"""Retargets motion capture clips onto a model's rig, as loops played in place. build.py starts it, not a person.

    blender -b --factory-startup --python-exit-code 1 --python retarget.py -- SPEC.json REPORT.json

The spec is a JSON file. Its paths are from its own folder:

    {
      "model": "PATH.glb",         # the model whose rig the clips are for (its nodes, its rest)
      "out": "PATH.glb",           # the clips on the model's nodes, which merge_clips.py adds to the model
      "hips": "pelvis",            # the model's hips, which the clip's hips move
      "left": "thigh_l", "right": "thigh_r",   # the model's hip joints, which set the way it faces
      "feet": ["ball_l", "ball_r"],            # the model's joints that stand on the ground, for the checks
      "fps": 30,                   # keys a second written
      "sources": {
        "NAME": {
          "scale": 0.01,           # metres a unit of the source's BVH files
          "restFrame": 1,          # optional: a frame holding the T-pose, when the skeleton's own rest is not one
          "aim": ["LeftFoot"],     # optional: joints whose direction alone is copied (clips.retarget's aim)
          "feet": ["LeftToeBase"], # optional: the source's joints on the ground (default: those mapped to "feet")
          "repair": [{"joints": ["foot_l"], "mostSpeed": 1500, "mostAngle": 90}],   # optional: model joints whose
                                   # capture errors are mended (gltf_clips.repair), in degrees a second and degrees
          "hips": "Hips",          # the source's hips
          "map": {"Hips": "pelvis", "LeftUpLeg": "thigh_l", ...}   # source joint: model joint
        }
      },
      "clips": [
        {"name": "Crouch_Walk_Loop", "source": "NAME", "zip": "URL", "member": "PATH/IN/ZIP.bvh",
         "sha256": "HEX", "from": 500, "to": 655},
        {"name": "Run_Loop", "source": "PACK", "blend": "PATH.blend", "action": "ACTION", "sha256": "HEX",
         "from": 0, "to": 18},
        {"name": "Death", "source": "PACK", "blend": "PATH.blend", "action": "ACTION", "from": 1, "to": 82,
         "loop": false},
        {"name": "Strafe_Right", "source": "PACK", "blend": "PATH.blend", "action": "STRAFE LEFT", "from": 1,
         "to": 24, "mirror": true}
      ]
    }

A clip with `"loop": false` is played once: its frames are kept as the source has them, not turned, moved, or closed.
With `"place": true` as well, its travel along the ground is taken out of its hips, so the game moves the body.
A loop with `"face": false` (or its source's `"face": false`) keeps the way it faces, not turned to face the front.
A clip with `"mirror": true` is its action played on the other side (gltf_clips.mirror): a strafe to the left becomes
one to the right, for a source whose own other side is worse or missing.

Each clip's BVH is one member of a zip, fetched by range requests into the cache and pinned by its SHA-256 (fetch.py).
Or a clip is an action of an armature in a local `.blend` file (`blend`, pinned by its SHA-256 when given), as an
animation pack ships one: its source names the armature (`"armature": "Rig"`) and its frames a second (`"fps": 30`),
and its scale is the file's own. The armature's constraints (a control rig) are worked out at each frame, so the bones
read are those the pack's clips pose. Frames `from` to `to`, as Blender numbers a BVH's frames (its first is 1) or as
the action keys them, are one cycle: `to`'s pose is `from`'s come round again. The clip is retargeted onto the model (clips.retarget), read back onto the model's nodes at every source
frame, mended where the capture breaks, turned to face the model's front, its travel taken out, closed at the seam, and
spread over the keys written (gltf_clips.py). The report gives, for each clip, the travel taken out (the speed and the
way it went), and the checks of motion.py on the result: the speed its feet show, their largest slide, and the seam's
jolt.
"""

import json
import math
import os
import sys
import traceback
from pathlib import Path

import bpy

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))
sys.dont_write_bytecode = True

from itera_models import clips, fetch, gltf_clips, motion, rig


def cache_root() -> Path:
    """The cache folder: $ITERA_CACHE, or ~/.cache/itera."""
    return Path(os.environ.get("ITERA_CACHE") or Path.home() / ".cache" / "itera")


def retarget_clip(spec: dict, entry: dict, target, nodes, turns) -> tuple[dict, dict]:
    """One clip: its keys on the model's nodes, and its report."""
    source_spec = spec["sources"][entry["source"]]
    source, source_action, digest = _load(spec, source_spec, entry)
    scene = bpy.context.scene
    source_fps = scene.render.fps / scene.render.fps_base
    joint_map = source_spec["map"]
    reverse = {model_joint: source_joint for source_joint, model_joint in joint_map.items()}

    # Turn the source about the up axis so its rest faces as the model's does.
    rest_frame = source_spec.get("restFrame")
    if rest_frame is not None:
        scene.frame_set(rest_frame)
    source_side = _point(source, reverse[spec["left"]], rest_frame) - _point(source, reverse[spec["right"]], rest_frame)
    left, right = (nodes.rest_world[nodes.index(spec[side])].translation for side in ("left", "right"))
    model_side = gltf_clips.TO_BLENDER @ (left - right)
    source.rotation_euler.z += math.atan2(model_side.y, model_side.x) - math.atan2(source_side.y, source_side.x)
    bpy.context.view_layer.update()

    first, last = entry["from"], entry["to"]
    action = clips.retarget(
        source,
        target,
        joint_map,
        first,
        last,
        hips=(source_spec["hips"], spec["hips"]),
        name=entry["name"],
        rest_frame=rest_frame,
        feet=tuple(source_spec.get("feet") or (reverse[foot] for foot in spec["feet"])),
        aim=tuple(source_spec.get("aim", ())),
    )
    for obj in [source, *source.children_recursive]:
        bpy.data.objects.remove(obj)
    bpy.data.actions.remove(source_action)

    seconds = (last - first) / source_fps
    joints = sorted({nodes.index(joint) for joint in joint_map.values()} | {nodes.index(spec["hips"])})
    target.animation_data.action = action
    # Every source frame of the cycle: mended, settled, then spread over the keys written.
    sampled = gltf_clips.sample(target, nodes, turns, list(range(first, last + 1)), joints)
    mended = {}
    for rule in source_spec.get("repair", []):
        found = gltf_clips.repair(
            sampled,
            nodes,
            [nodes.index(joint) for joint in rule["joints"]],
            1.0 / source_fps,
            rule["mostSpeed"],
            rule.get("mostAngle"),
        )
        mended.update({joint: len(frames) for joint, frames in zip(rule["joints"], found) if frames})
    settled = gltf_clips.settle(
        sampled,
        nodes,
        nodes.index(spec["hips"]),
        nodes.index(spec["left"]),
        nodes.index(spec["right"]),
        seconds,
        [nodes.index(foot) for foot in spec.get("feet", [])],
        face=entry.get("face", source_spec.get("face", True)),
        loop=entry.get("loop", True),
        place=entry.get("place", False),
    )
    target.animation_data.action = None
    bpy.data.actions.remove(action)
    count = max(1, round(seconds * spec.get("fps", 30)))
    sampled = gltf_clips.resample(sampled, count)
    if entry.get("mirror", False):
        sampled = gltf_clips.mirror(sampled, nodes)
        settled["travel"][0] = -settled["travel"][0]
        settled["way"] = -settled["way"]

    hips = nodes.index(spec["hips"])
    keys = {
        "name": entry["name"],
        "times": [seconds * k / count for k in range(count + 1)],
        "rotations": {joint: [locals_[joint].to_quaternion() for locals_ in sampled] for joint in joints},
        "translations": {hips: [locals_[hips].translation.copy() for locals_ in sampled]},
    }
    report = {"name": entry["name"], "member": entry.get("member") or entry["action"], "sha256": digest,
              "frames": count + 1,
              "seconds": round(seconds, 4), "mended": mended, **settled,
              **_checks(spec, nodes, sampled, seconds / count)}
    return keys, report


def _load(spec: dict, source_spec: dict, entry: dict):
    """A clip's source armature in the scene, playing the clip's action, with the action and the source's SHA-256: a
    BVH fetched from its zip, or an armature appended from a local `.blend` file with its action. The scene's frames a
    second become the source's."""
    scene = bpy.context.scene
    if "blend" in entry:
        path = (Path(spec["_folder"]) / entry["blend"]).resolve()
        digest = fetch.sha256_of(path)
        if entry.get("sha256") and entry["sha256"] != digest:
            raise ValueError(f"{path.name} has SHA-256 {digest}, not the {entry['sha256']} the spec pins")
        with bpy.data.libraries.load(str(path), link=False) as (_, data_to):
            data_to.objects = [source_spec["armature"]]
            data_to.actions = [entry["action"]]
        source = data_to.objects[0]
        if source is None or data_to.actions[0] is None:
            raise ValueError(f"{path.name} has no armature {source_spec['armature']} or no action {entry['action']}")
        scene.collection.objects.link(source)
        action = data_to.actions[0]
        if source.animation_data is None:
            source.animation_data_create()
        source.animation_data.action = action
        if action.slots:
            source.animation_data.action_slot = action.slots[0]
        scene.render.fps, scene.render.fps_base = int(source_spec["fps"]), 1.0
        bpy.context.view_layer.update()
        return source, action, digest
    path, digest = fetch.zip_member(entry["zip"], entry["member"], cache_root() / "inputs", sha256=entry.get("sha256"))
    before = set(bpy.data.objects)
    bpy.ops.import_anim.bvh(
        filepath=str(path), global_scale=source_spec["scale"], update_scene_fps=True, update_scene_duration=True
    )
    source = next(obj for obj in bpy.data.objects if obj not in before and obj.type == "ARMATURE")
    return source, source.animation_data.action, digest


def _point(armature, bone: str, rest_frame):
    """A bone's head in the scene: posed at the scene's frame, or at rest when no rest frame is given."""
    if rest_frame is None:
        return armature.matrix_world @ armature.data.bones[bone].head_local
    return armature.matrix_world @ armature.pose.bones[bone].head


def _checks(spec: dict, nodes, sampled: list, step: float) -> dict:
    """motion.py's checks on the clip as written: the feet's speed and slide, and the seam, in Blender's axes."""
    joints = sorted(sampled[0])
    worlds = [nodes.worlds(locals_) for locals_ in sampled]
    height = max(w.translation.y for w in nodes.rest_world) - min(w.translation.y for w in nodes.rest_world)
    feet = [nodes.index(foot) for foot in spec.get("feet", [])]
    paths = [[tuple(gltf_clips.TO_BLENDER @ world[foot].translation) for world in worlds] for foot in feet]
    shown = motion.ground_speed(paths, step, motion.CONTACT * height) if paths else 0.0
    slide = max((motion.foot_slide(path, step, shown, motion.CONTACT * height)["slide"] for path in paths), default=0.0)
    places = [[tuple(gltf_clips.TO_BLENDER @ world[joint].translation) for joint in joints] for world in worlds]
    seam = motion.loop_closure(places)
    return {
        "shown_speed": round(shown, 3),
        "slide": round(slide, 4),
        "slide_limit": round(motion.SLIDE * height, 4),
        "seam": round(seam["seam"], 4),
        "inside": round(seam["inside"], 4),
    }


def main(arguments: list) -> None:
    if len(arguments) != 2:
        raise SystemExit("usage: retarget.py -- SPEC.json REPORT.json")
    spec_path, report_path = (Path(argument).resolve() for argument in arguments)
    spec = json.loads(spec_path.read_text(encoding="utf-8"))
    folder = spec_path.parent
    spec["_folder"] = str(folder)
    model = (folder / spec["model"]).resolve()

    bpy.ops.wm.read_factory_settings(use_empty=True)
    document, _ = gltf_clips.read_glb(model)
    nodes = gltf_clips.Nodes(document)
    target, _ = rig.import_reference(model)
    for action in list(bpy.data.actions):
        bpy.data.actions.remove(action)
    turns = gltf_clips.corrections(target, nodes)

    written, reports = [], []
    for entry in spec["clips"]:
        keys, report = retarget_clip(spec, entry, target, nodes, turns)
        written.append(keys)
        reports.append(report)
    out = (folder / spec["out"]).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)
    gltf_clips.write(out, document, written)
    report_path.write_text(json.dumps({"out": str(out), "clips": reports}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    try:
        main(sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else [])
    except SystemExit:
        raise
    except BaseException:
        traceback.print_exc()
        sys.stderr.flush()
        sys.exit(1)
