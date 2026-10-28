"""Makes a model's clips from a clip recipe: key poses posed by IK (itera_models/keyed.py). build.py starts it.

    blender -b --factory-startup --python-exit-code 1 --python clip_recipe.py -- RECIPE.py REPORT.json WORK
        [--video] [--out PATH]

A clip recipe is a Python file with these names; its paths are from its own folder:

    MODEL = "PATH.glb"      # the model whose rig the clips are for (its nodes, its rest, its stand clip)
    OUT = "PATH.glb"        # the clips on the model's nodes, which merge_clips.py adds to the model
    STAND = "Idle_Loop"     # the model's clip whose first pose each clip but a loop starts and ends in
    FPS = 30                # keys a second
    BODY = "PATH.glb"       # optional: a body bound on MODEL's rig with joints of its own places, the clips keyed for it
    def build(ctx): ...     # makes the clips with ctx.clip

`ctx.clip(name, keys, ground=(), planted=(), impacts=(), mirror=None, loop=False)` makes one clip from `keys`
(keyed.Key), checks it (keyed.check: `ground` names the joints that touch the ground, `planted` the joints that stand
still, as (joint, from, to) in seconds), reports where each of its `impacts`, as (label, joints, seconds), puts the
joints, and draws its sheet and, with --video, its video. `mirror` names the same clip made for the other side. A
`loop` (a climb's cycle) need not start at the stand: it ends in the pose it starts in. `ctx.keyer` stands the model,
`ctx.stand(joint)` gives where a joint stands, in the model's axes, and `ctx.ankle_for(side, toes, turn)` where an ankle
goes for its toes to rest at a place with the foot turned.

With a BODY, the rig's joints move to the body's places, its stand clip rebased on them as the engine rebases it, and
the body is drawn. The clips are still written on MODEL's nodes, the hips' translation less the body's shift of its
rest, which the engine adds back as it binds the body.
`--out` writes the clips there in place of the recipe's OUT (build.py verify compares the two).

The report lists, for each clip, its length, its lowest joints, its knees' and elbows' most bend, how far its ends lie
from the stand and from each other (its seam), its impacts, and its pictures; and every problem the checks found.
"""

import importlib.util
import json
import sys
import traceback
from pathlib import Path

import bpy
from mathutils import Vector

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))
sys.dont_write_bytecode = True

from itera_models import gltf_clips, keyed, rig, strips


def swapped(name: str) -> str:
    """A joint's name on the other side."""
    if name.endswith("_l"):
        return name[:-2] + "_r"
    if name.endswith("_r"):
        return name[:-2] + "_l"
    return name


class Context:
    """What a clip recipe's build gets: the clips it makes are kept to be written, with their reports and pictures."""

    def __init__(self, armature, meshes, nodes, turns, keyer, work: Path, video: bool, shifts: dict) -> None:
        self.armature = armature
        self.meshes = meshes
        self.nodes = nodes
        self.turns = turns
        self.shifts = shifts
        self.keyer = keyer
        self.work = work
        self.video = video
        self.written: list = []
        self.reports: list = []
        self.problems: list = []

    def stand(self, joint: str) -> tuple:
        return self.keyer.stand_place(joint)

    def ankle_for(self, side: str, toes: tuple, turn=None) -> tuple:
        return self.keyer.ankle_for(side, toes, turn if turn is not None else keyed.Turn())

    def clip(self, name, keys, ground=(), planted=(), impacts=(), mirror=None, loop=False) -> None:
        self._one(name, keys, tuple(ground), tuple(planted), tuple(impacts), loop)
        if mirror:
            self._one(
                mirror,
                keyed.mirrored(keys),
                tuple(swapped(joint) for joint in ground),
                tuple((swapped(joint), start, end) for joint, start, end in planted),
                tuple((label, tuple(swapped(j) for j in joints), seconds) for label, joints, seconds in impacts),
                loop,
            )

    def _one(self, name, keys, ground, planted, impacts, loop) -> None:
        keyer = self.keyer
        made = keyer.clip(name, keys)
        report, problems = keyed.check(self.armature, keyer, made, ground=ground, planted=planted, loop=loop)
        report["impacts"] = [
            {
                "label": label,
                "seconds": seconds,
                "places": {joint: keyed.place_at(self.armature, joint, seconds, keyer.fps) for joint in joints},
            }
            for label, joints, seconds in impacts
        ]
        last = made.frames
        times = sorted({round(k.seconds * keyer.fps) for k in keys} | set(range(0, last + 1, max(1, keyer.fps // 2))))
        report["sheet"] = str(strips.sheet(self.work / f"{name}.png", self.armature, self.meshes, times, keyer.fps))
        if self.video:
            report["video"] = str(strips.video(self.work / f"{name}.mp4", self.armature, self.meshes, last))
        nodes = self.nodes
        joints = sorted(nodes.index(bone.name) for bone in self.armature.data.bones if bone.parent is not None)
        hips = nodes.index(keyer.rig.hips)
        sampled = gltf_clips.sample(self.armature, nodes, self.turns, list(range(last + 1)), joints)
        self.written.append(
            {
                "name": name,
                "times": [frame / keyer.fps for frame in range(last + 1)],
                "rotations": {joint: [locals_[joint].to_quaternion() for locals_ in sampled] for joint in joints},
                "translations": {
                    hips: [locals_[hips].translation - self.shifts.get(hips, Vector()) for locals_ in sampled]
                },
            }
        )
        self.reports.append(report)
        self.problems.extend(problems)


def on_body(armature, meshes: list, nodes, path: Path) -> tuple:
    """Moves the rig's joints to the body's at `path` (`gltf_clips.on_body`) and draws the body's meshes on the rig in
    place of the model's: returns them and the joints' shifts."""
    document, _ = gltf_clips.read_glb(path)
    shifts = gltf_clips.on_body(armature, nodes, gltf_clips.Nodes(document))
    body_armature, body_meshes = rig.import_reference(path)
    for mesh in body_meshes:
        rig.bind_weighted(mesh, armature)
    for obj in [body_armature, *meshes]:
        bpy.data.objects.remove(obj, do_unlink=True)
    return body_meshes, shifts


def load(path: Path):
    spec = importlib.util.spec_from_file_location("clip_recipe_module", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    for name in ("MODEL", "OUT", "STAND", "build"):
        if not hasattr(module, name):
            raise ValueError(f"{path}: a clip recipe needs {name}")
    return module


def main(arguments: list) -> None:
    video = "--video" in arguments
    arguments = [argument for argument in arguments if argument != "--video"]
    out_override = None
    if "--out" in arguments:
        at = arguments.index("--out")
        out_override = Path(arguments[at + 1]).resolve()
        del arguments[at : at + 2]
    if len(arguments) != 3:
        raise SystemExit("usage: clip_recipe.py -- RECIPE.py REPORT.json WORK [--video] [--out PATH]")
    recipe_path, report_path, work = (Path(argument).resolve() for argument in arguments)
    work.mkdir(parents=True, exist_ok=True)
    module = load(recipe_path)
    folder = recipe_path.parent
    model = (folder / module.MODEL).resolve()

    bpy.ops.wm.read_factory_settings(use_empty=True)
    document, _ = gltf_clips.read_glb(model)
    nodes = gltf_clips.Nodes(document)
    armature, meshes = rig.import_reference(model)
    turns = gltf_clips.corrections(armature, nodes)
    shifts = {}
    if hasattr(module, "BODY"):
        meshes, shifts = on_body(armature, meshes, nodes, (folder / module.BODY).resolve())
    keyer = keyed.Keyer(armature, module.STAND, getattr(module, "RIG", keyed.MANNEQUIN), getattr(module, "FPS", 30))
    for action in list(bpy.data.actions):
        bpy.data.actions.remove(action)
    ctx = Context(armature, meshes, nodes, turns, keyer, work, video, shifts)
    module.build(ctx)
    out = out_override or (folder / module.OUT).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)
    gltf_clips.write(out, document, ctx.written)
    report_path.write_text(
        json.dumps({"out": str(out), "clips": ctx.reports, "problems": ctx.problems}, indent=2) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    try:
        main(sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else [])
    except SystemExit:
        raise
    except BaseException:
        traceback.print_exc()
        sys.stderr.flush()
        sys.exit(1)
