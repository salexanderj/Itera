"""Pictures of one model drawn by the engine itself, from set angles, for an agent or a person to judge.

Two places to draw it:

- the neutral stage (tools/ModelView): a grey ground with a 10 cm grid, the engine's own sky and look, and nothing
  else, the same for every game, so the model is judged on its own;
- the game's own world (--world PLACE): the game started offscreen through tools/control/game.py, the model put on
  the ground at one of the game's tp places, in the game's light and fog.

Both read the model through the engine's Engine:Assets:Extra option, so nothing is rebuilt, and both draw the same set:
front, back, left, and right (orthographic), top (orthographic), and a three-quarter view (perspective), plus a
three-quarter view of each clip pose asked for. On the stage the model stands at the world's middle, unturned (its
front, glTF's +z, towards +z). In a game's world it stands where meshes place puts it, in front of the player and facing
the player, and the views turn with it, so the front view stands on the player's side. Its perspective view and poses
are drawn from the player's own eye, since a set place round the model can be inside a rock or a wall. Standard
library only. A development tool, not game code.
"""

from __future__ import annotations

import json
import math
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
MODEL_VIEW = REPO / "tools" / "ModelView"
GAME_PY = REPO / "tools" / "control" / "game.py"

# The engine's default field of view gives about 59 degrees vertically; a square picture sees as much across.
VERTICAL_FOV_DEGREES = 59.0
# The sun's direction for every set: from the front left, 40 degrees up, so faces read apart.
SUN = "sun 135 40"
# Frames a new view is given before its screenshot, for its light and shadows to settle.
SETTLE_FRAMES = 5


@dataclass(frozen=True)
class View:
    """One picture: its file name, the viewpoint's text (x,y,z,yaw,pitch in metres and degrees), an orthographic
    lens's height in metres (None for perspective), and how deep an orthographic view reaches from the viewpoint, in
    metres. The depth starts at the viewpoint: a lens's default box also reaches behind it, where a game's hills would
    hide the model."""

    name: str
    viewpoint: str
    ortho_height: float | None
    depth: float = 0.0


@dataclass(frozen=True)
class Bounds:
    """The model's box in metres, as the engine reads it (ModelCheck's boundsMin and boundsMax)."""

    low: tuple[float, float, float]
    high: tuple[float, float, float]

    @property
    def centre(self) -> tuple[float, float, float]:
        return tuple((a + b) / 2 for a, b in zip(self.low, self.high))

    @property
    def size(self) -> tuple[float, float, float]:
        return tuple(b - a for a, b in zip(self.low, self.high))

    @property
    def radius(self) -> float:
        return max(0.05, math.sqrt(sum(s * s for s in self.size)) / 2)


def views(
    bounds: Bounds,
    at: tuple[float, float, float] = (0.0, 0.0, 0.0),
    turn: float = 0.0,
    eye: tuple[float, float, float] | None = None,
) -> list[View]:
    """The set of views of a model placed at `at` (world metres) and turned `turn` degrees about up: four sides and the
    top orthographic, framed on the box with a little room, and a three-quarter view in perspective, far enough that
    the whole box fits. The views turn with the model, so the front view always faces its front. With `eye` (a place
    known to be clear, such as the player's eye in a game's world), the perspective view is "from-player": from the
    eye, aimed at the model's middle, since a set place round the model can be inside a rock or a wall."""
    cx, cy, cz = bounds.centre
    sx, sy, sz = bounds.size
    away = bounds.radius + 2.0
    room = 1.15
    cos_turn, sin_turn = math.cos(math.radians(turn)), math.sin(math.radians(turn))

    def point(x: float, y: float, z: float, yaw: float, pitch: float) -> str:
        # From the model's own space to the world's: turned about up as a camera's yaw turns, then moved to `at`.
        wx = at[0] + x * cos_turn + z * sin_turn
        wz = at[2] - x * sin_turn + z * cos_turn
        return f"{wx:.3f},{at[1] + y:.3f},{wz:.3f},{yaw + turn:.1f},{pitch:.1f}"

    half_fov = math.radians(VERTICAL_FOV_DEGREES) / 2
    distance = bounds.radius / math.sin(half_fov) * 1.5
    direction = (1.0, 0.6, 1.0)
    length = math.sqrt(sum(d * d for d in direction))
    dx, dy, dz = (d / length * distance for d in direction)
    pitch = -math.degrees(math.atan2(dy, math.hypot(dx, dz)))
    depth = 2 * away
    if eye is not None:
        mx = at[0] + cx * cos_turn + cz * sin_turn
        mz = at[2] - cx * sin_turn + cz * cos_turn
        dx, dy, dz = mx - eye[0], at[1] + cy - eye[1], mz - eye[2]
        aim_yaw = math.degrees(math.atan2(-dx, -dz))
        aim_pitch = math.degrees(math.atan2(dy, math.hypot(dx, dz)))
        perspective = View("from-player", f"{eye[0]:.3f},{eye[1]:.3f},{eye[2]:.3f},{aim_yaw:.1f},{aim_pitch:.1f}", None)
    else:
        perspective = View("three-quarter", point(cx + dx, cy + dy, cz + dz, 45, pitch), None)
    return [
        View("front", point(cx, cy, cz + away, 0, 0), max(sx, sy) * room, depth),
        View("back", point(cx, cy, cz - away, 180, 0), max(sx, sy) * room, depth),
        View("left", point(cx + away, cy, cz, 90, 0), max(sz, sy) * room, depth),
        View("right", point(cx - away, cy, cz, -90, 0), max(sz, sy) * room, depth),
        View("top", point(cx, cy + away, cz, 0, -90), max(sx, sz) * room, depth),
        perspective,
    ]


def shots(name: str, views_: list[View], out_dir: Path, clips: list[tuple[str, float]], at: str) -> list[str]:
    """Console commands that take every view's screenshot into out_dir, then each clip pose's three-quarter view."""
    commands: list[str] = []

    def take(view: View, file: str) -> None:
        commands.append(f"lens ortho {view.ortho_height:.3f} 0 {view.depth:.3f}" if view.ortho_height else "lens off")
        commands.append(f"viewpoint {view.viewpoint}")
        commands.append(f"wait frames {SETTLE_FRAMES}")
        commands.append(f'screenshot noui "{out_dir / file}"')

    for view in views_:
        take(view, f"{view.name}.png")
    three_quarter = views_[-1]
    for clip, seconds in clips:
        commands.append("meshes clear")
        commands.append(f"meshes place {name} at {at} 1 {clip} {seconds:g}")
        take(three_quarter, f"pose-{_file_part(clip)}-{seconds:g}.png")
    commands.append("lens off")
    commands.append("viewpoint off")
    return commands


def model_report(glb: Path, unit: float) -> dict:
    """ModelCheck's report on the file: its bounds, clips, and problems, as the engine reads it."""
    result = subprocess.run(
        [
            "dotnet",
            "run",
            "-c",
            "Release",
            "--project",
            str(REPO / "tools" / "ModelCheck"),
            "--",
            "--json",
            "--unit",
            f"{unit:g}",
            str(glb),
        ],
        capture_output=True,
        text=True,
        cwd=REPO,
        check=False,
    )
    if result.returncode not in (0, 1):
        raise RuntimeError(f"ModelCheck failed:\n{result.stderr or result.stdout}")
    return json.loads(result.stdout)[0]


def on_stage(
    glb: Path,
    name: str,
    unit: float,
    out_dir: Path,
    clips: list[tuple[str, float]],
    size: int = 768,
    release: bool = False,
) -> list[Path]:
    """Draws the model on ModelView's neutral stage and returns the pictures' paths."""
    report = model_report(glb, unit)
    if not report.get("boundsMin"):
        raise RuntimeError(f"the engine cannot read {glb}: {report.get('problems')}")
    bounds = Bounds(tuple(report["boundsMin"]), tuple(report["boundsMax"]))
    out_dir.mkdir(parents=True, exist_ok=True)
    config = "Release" if release else "Debug"
    build = subprocess.run(
        ["dotnet", "build", "-c", config, str(MODEL_VIEW)], capture_output=True, text=True, check=False
    )
    if build.returncode != 0:
        raise RuntimeError(f"ModelView did not build:\n{build.stdout[-2000:]}")

    place = "0 0 0 0"
    script = ["wait loaded", SUN, f"meshes place {name} at {place}"]
    script += shots(name, views(bounds), out_dir, clips, place)
    script.append("quit")
    executable = MODEL_VIEW / "bin" / config / "net10.0" / ("ModelView.exe" if os.name == "nt" else "ModelView")
    run = subprocess.run(
        [
            str(executable),
            "--Engine:Application:DataFolder=IteraModelView-agent",
            "--Engine:Console:ExitOnFailure=true",
            f"--Engine:Window:Width={size}",
            f"--Engine:Window:Height={size}",
            f"--Engine:Assets:Extra:0:Name={name}",
            f"--Engine:Assets:Extra:0:Path={glb.resolve()}",
            f"--Engine:Assets:Extra:0:MetresPerUnit={unit:g}",
            "--Engine:Console:Run=" + "; ".join(script),
        ],
        capture_output=True,
        text=True,
        timeout=300,
        check=False,
    )
    if run.returncode != 0:
        raise RuntimeError(f"ModelView ended with {run.returncode}:\n{run.stdout[-3000:]}")
    return _pictures(out_dir)


def in_world(
    glb: Path,
    name: str,
    unit: float,
    place: str,
    out_dir: Path,
    clips: list[tuple[str, float]],
    size: int = 768,
    release: bool = False,
) -> list[Path]:
    """Draws the model in the game's own world at its tp place PLACE (on the ground there), through game.py, and
    returns the pictures' paths. Refuses while another game started by game.py runs."""
    report = model_report(glb, unit)
    if not report.get("boundsMin"):
        raise RuntimeError(f"the engine cannot read {glb}: {report.get('problems')}")
    bounds = Bounds(tuple(report["boundsMin"]), tuple(report["boundsMax"]))
    out_dir.mkdir(parents=True, exist_ok=True)
    start = (
        [sys.executable, str(GAME_PY), "start", "--offscreen"]
        + (["--release"] if release else [])
        + [
            "--",
            f"--Engine:Window:Width={size}",
            f"--Engine:Window:Height={size}",
            "--Engine:Renderer:DynamicRenderScaling=false",
            "--Engine:Ui:Stats=false",
            "--Engine:Camera:StartFlying=true",
            f"--Engine:Assets:Extra:0:Name={name}",
            f"--Engine:Assets:Extra:0:Path={glb.resolve()}",
            f"--Engine:Assets:Extra:0:MetresPerUnit={unit:g}",
        ]
    )
    started = subprocess.run(start, capture_output=True, text=True, check=False)
    if started.returncode != 0:
        raise RuntimeError(f"the game did not start:\n{started.stdout}{started.stderr}")
    try:
        # meshes place puts it on the ground in front of the player, facing the player. The views turn with it, and the
        # perspective view is drawn from the player's eye, the one place known to be clear.
        placed = _game(f"wait ticks 2; tp {place}; wait view; {SUN}; pos; meshes place {name}")
        match = re.search(rf"{re.escape(name)} placed at (\S+) (\S+) (\S+) in the world", placed)
        pose = re.search(r"(-?[\d.]+),(-?[\d.]+),(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)", placed)
        if not match or not pose:
            raise RuntimeError(f"pos and meshes place gave no place in the world:\n{placed}")
        at = tuple(float(v) for v in match.groups())
        eye = (float(pose.group(1)), float(pose.group(2)), float(pose.group(3)))
        turn = float(pose.group(4))
        place_text = f"{at[0]:.3f} {at[1]:.3f} {at[2]:.3f} {turn:g}"
        _game("; ".join(shots(name, views(bounds, at, turn, eye), out_dir, clips, place_text)))
    finally:
        subprocess.run([sys.executable, str(GAME_PY), "stop"], capture_output=True, text=True, check=False)
    return _pictures(out_dir)


def _game(line: str) -> str:
    """Runs console commands in the game started by game.py, as a script; their answers."""
    result = subprocess.run(
        [sys.executable, str(GAME_PY), "run", "-q", "-s", line],
        capture_output=True,
        text=True,
        timeout=600,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(f"the game refused '{line}':\n{result.stdout}{result.stderr}")
    return result.stdout


def _pictures(out_dir: Path) -> list[Path]:
    return sorted(out_dir.glob("*.png"))


def _file_part(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]", "_", text)
