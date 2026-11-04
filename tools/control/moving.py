#!/usr/bin/env python3
"""Pictures of the player's figure moving, for judging its motion: a sheet of frames through its stride and an animated
GIF, from a fixed orthographic camera set beside the figure at each frame while the game is paused.

Drives a running offline game (tools/control/game.py start) through its control port: puts the player at a start,
pauses, holds the keys given, steps a run-up, then steps a few ticks at a time; at each stop it puts the camera
(`viewpoint`, `lens ortho`) on the figure's right, left, front, or back, or above it, and takes a picture (`shot`).
Each pass may first run console commands (`--pass "LABEL=COMMANDS"`, such as tunables to compare), and the sheet stacks
the passes in rows, labelled. `--setup` runs console commands after each pass's start, before its run-up. Times count
from the start of the run-up: a key may be pressed later and held (`--press KEY@SECONDS`), as Ctrl into a slide at
speed, or let go (`--release KEY@SECONDS`); a mouse button pressed and held (`--button BUTTON@SECONDS`); and the look
turned by the mouse (`--turn DEGREES@SECONDS/OVER`, left positive, spread over OVER seconds); and console commands run
(`--command "COMMANDS@SECONDS"`), as a hit on the figure. Needs Pillow for the sheet and the GIF. See README.md next to
this file.

    moving.py W --view right --out DIR
    moving.py ShiftLeft+W+A --view front --pass "before=tune x 1" --pass "after=tune x 0.4" --out DIR
    moving.py ShiftLeft+W --press ControlLeft@1.2 --run-up 1.3 --view right --out DIR
    moving.py W --button right@0 --turn 90@1.5/1 --view top --out DIR
    moving.py W --command "hurt chest 15@1.6" --view right --out DIR
"""

import argparse
import json
import math
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import game

try:
    from PIL import Image, ImageDraw
except ImportError:  # pragma: no cover - reported at run time
    Image = None

TICKS_A_SECOND = 60
MODEL_HEIGHT = 1.7  # the figure's height at scale 1, in metres, for framing


class Driver:
    """One connection to the game, kept for every request of a run."""

    def __init__(self, port):
        self.connection = game.Connection(port, 120)

    def run(self, line, script=False):
        request = {"op": "run", "line": line}
        if script:
            request.update(script=True, timeout=110)
        reply = self.connection.ask(request)
        if not reply.get("ok"):
            raise RuntimeError(f"'{line}' failed: {reply.get('failures') or reply.get('error')}")
        return reply

    def ask(self, request):
        reply = self.connection.ask(request)
        if not reply.get("ok"):
            raise RuntimeError(f"{request['op']} failed: {reply.get('failures') or reply.get('error')}")
        return reply

    def figure(self):
        """The player's figure: its place (absolute, metres), its front across the ground (a unit x, z), and its height."""
        origin = self.ask({"op": "status"})["origin"]
        components = self.ask({"op": "query", "entity": "player", "most": 1})["components"]
        placement = components["MeshPlacement"]
        x, y, z = (placement["Position"][i] + origin[i] for i in range(3))
        qx, qy, qz, qw = placement["Orientation"]
        # The model's front (+z) turned by the placement's orientation.
        front_x = 2 * (qx * qz + qw * qy)
        front_z = 1 - 2 * (qx * qx + qy * qy)
        length = math.hypot(front_x, front_z) or 1
        return (x, y, z), (front_x / length, front_z / length), placement["Scale"] * MODEL_HEIGHT

    def close(self):
        self.connection.close()


def camera(place, front, height, view, distance):
    """The viewpoint (x,y,z,yaw,pitch in degrees) that looks at the figure from `view`, `distance` metres off."""
    x, y, z = place
    fx, fz = front
    rx, rz = -fz, fx  # its right
    offsets = {"right": (rx, rz), "left": (-rx, -rz), "front": (fx, fz), "back": (-fx, -fz)}
    if view == "top":
        return f"{x:.3f},{y + distance:.3f},{z:.3f},{math.degrees(math.atan2(-fx, -fz)):.2f},-89.9"
    ox, oz = offsets[view]
    cx, cz = x + ox * distance, z + oz * distance
    # Yaw 0 looks along -z and turns left: a look along (dx, dz) is atan2(-dx, -dz).
    yaw = math.degrees(math.atan2(-(x - cx), -(z - cz)))
    return f"{cx:.3f},{y + height * 0.55:.3f},{cz:.3f},{yaw:.2f},0"


def degrees_a_pixel(driver):
    """The mouse's turn for each pixel it moves, in degrees: the settings' sensitivity."""
    answers = " ".join(driver.run("settings").get("answers", []))
    found = re.search(r"sensitivity ([0-9.]+)", answers)
    if not found or float(found.group(1)) <= 0:
        raise RuntimeError(f"no sensitivity in the settings: {answers}")
    return float(found.group(1))


def timed_inputs(args, degrees_per_pixel):
    """The input actions after the run-up starts, as (tick, action) in order of their ticks."""
    timed = [(round(seconds * TICKS_A_SECOND), f"down {key}") for key, seconds in args.presses]
    timed += [(round(seconds * TICKS_A_SECOND), f"up {key}") for key, seconds in args.releases]
    timed += [(round(seconds * TICKS_A_SECOND), f"press {button}") for button, seconds in args.buttons]
    # Console commands are kept apart from the input actions as (commands,), run as a script at their tick.
    timed += [(round(seconds * TICKS_A_SECOND), (commands,)) for commands, seconds in args.commands]
    for degrees, seconds, over in args.turns:
        # Spread over the ticks, a share each; the mouse moving right turns right.
        ticks = max(1, round(over * TICKS_A_SECOND))
        pixels = -degrees / ticks / degrees_per_pixel
        timed += [(round(seconds * TICKS_A_SECOND) + i, f"look {pixels:.4f} 0") for i in range(ticks)]
    return sorted(timed, key=lambda pair: pair[0])


def one_pass(driver, args, label, commands, out):
    """Runs one pass and returns its frames, cropped round the figure."""
    driver.run("resume; viewpoint off; lens off", script=True)
    if commands:
        driver.run(commands, script=True)
    driver.run(f"tp {args.start}; wait ticks 60; pause", script=True)
    if args.setup:
        driver.run(args.setup, script=True)
    capture = bool(args.turns or args.buttons)
    if capture:
        driver.ask({"op": "input", "actions": ["capture on"], "timeout": 30})
    driver.ask({"op": "input", "actions": [f"down {key}" for key in args.keys], "timeout": 30})
    pending = timed_inputs(args, degrees_a_pixel(driver) if args.turns else 1)
    stepped = 0

    def step(ticks):
        # Steps the game, sending each later input at its tick on the way.
        nonlocal stepped
        end = stepped + ticks
        while pending and pending[0][0] <= end:
            at = pending[0][0]
            if at > stepped:
                driver.run(f"step {at - stepped}; wait ticks {at - stepped}", script=True)
                stepped = at
            actions, runs = [], []
            while pending and pending[0][0] == at:
                action = pending.pop(0)[1]
                (runs if isinstance(action, tuple) else actions).append(action)
            if actions:
                driver.ask({"op": "input", "actions": actions, "timeout": 30})
            for (commands,) in runs:
                driver.run(commands, script=True)
        if end > stepped:
            driver.run(f"step {end - stepped}; wait ticks {end - stepped}", script=True)
            stepped = end

    frames = []
    try:
        step(round(args.run_up * TICKS_A_SECOND))
        for index in range(args.frames):
            step(args.every)
            place, front, height = driver.figure()
            if index == 0:
                # A range of its own: the lens's default, the view distance each way, draws only the haze
                # (salexanderj/Itera#55).
                driver.run(f"lens ortho {height * args.zoom:.3f} -1 {args.distance + 400:.0f}")
            view = camera(place, front, height, args.view, args.distance)
            driver.run(f"viewpoint {view}; wait frames 3", script=True)
            path = out / f"{label}-{index:03d}.png"
            driver.ask({"op": "shot", "path": str(path.resolve()), "scale": args.scale, "ui": False})
            picture = Image.open(path).convert("RGB")
            width, tall = picture.size
            keep = round(tall * args.width)
            frames.append(picture.crop(((width - keep) // 2, 0, (width + keep) // 2, tall)))
    finally:
        ups = [f"up {key}" for key in args.keys + [key for key, _ in args.presses]]
        ups += [f"release {button}" for button, _ in args.buttons] + (["capture off"] if capture else [])
        driver.ask({"op": "input", "actions": ups, "timeout": 30})
        driver.run("viewpoint off; lens off; resume", script=True)
    return frames


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("keys", help="keys held, joined by '+' (Silk.NET names: W, A, S, D, ShiftLeft, ControlLeft)")
    parser.add_argument("--view", choices=["right", "left", "front", "back", "top"], default="right")
    parser.add_argument("--out", required=True, help="the folder for the frames, sheet.png, and moving.gif")
    parser.add_argument("--start", default="110 1.7 110 0 0", help="tp's place for each pass (x y z yaw pitch)")
    parser.add_argument("--run-up", type=float, default=1.5, help="seconds of the game moving before the first frame")
    parser.add_argument("--frames", type=int, default=30, help="frames taken")
    parser.add_argument("--every", type=int, default=2, help="ticks (1/60 s) between frames")
    parser.add_argument("--sheet", type=int, default=8, help="frames on the sheet's row, spread through those taken")
    parser.add_argument("--distance", type=float, default=4, help="metres from the figure to the camera")
    parser.add_argument("--zoom", type=float, default=1.35, help="the picture's height as a share of the figure's")
    parser.add_argument("--width", type=float, default=0.6, help="each frame's width kept, as a share of its height")
    parser.add_argument("--scale", type=float, default=0.5, help="the pictures' size as a share of the screen's")
    parser.add_argument("--slow", type=float, default=1, help="how many times slower the GIF plays than the game")
    parser.add_argument("--pass", dest="passes", action="append", default=[],
                        help="LABEL=COMMANDS: a pass run after those console commands (repeatable; one plain pass "
                             "without it)")
    parser.add_argument("--press", dest="presses", action="append", default=[],
                        help="KEY@SECONDS: a key pressed and held from that many seconds into the run-up (repeatable)")
    parser.add_argument("--release", dest="releases", action="append", default=[],
                        help="KEY@SECONDS: a key let go that many seconds into the run-up (repeatable)")
    parser.add_argument("--button", dest="buttons", action="append", default=[],
                        help="BUTTON@SECONDS: a mouse button (left, right, middle) pressed and held from that many "
                             "seconds into the run-up (repeatable)")
    parser.add_argument("--turn", dest="turns", action="append", default=[],
                        help="DEGREES@SECONDS/OVER: the look turned DEGREES to the left (negative: right) by the mouse, "
                             "from that many seconds into the run-up, spread over OVER seconds (repeatable)")
    parser.add_argument("--command", dest="commands", action="append", default=[],
                        help="COMMANDS@SECONDS: console commands run that many seconds into the run-up, the game "
                             "paused (repeatable)")
    parser.add_argument("--setup", default="",
                        help="console commands run after each pass's start, before its run-up")
    parser.add_argument("--port", type=int, default=None)
    args = parser.parse_args()
    if Image is None:
        print("moving.py needs Pillow (python3 -m pip install pillow)", file=sys.stderr)
        return 2
    args.keys = [key for key in args.keys.split("+") if key]
    try:
        args.presses = [(key, float(seconds)) for key, seconds in (text.split("@", 1) for text in args.presses)]
        args.releases = [(key, float(seconds)) for key, seconds in (text.split("@", 1) for text in args.releases)]
        args.buttons = [(button, float(seconds)) for button, seconds in (text.split("@", 1) for text in args.buttons)]
        args.commands = [(commands, float(seconds)) for commands, seconds in (text.rsplit("@", 1) for text in args.commands)]
    except ValueError:
        print("--press, --release, --button, and --command take NAME@SECONDS", file=sys.stderr)
        return 2
    try:
        args.turns = [
            (float(degrees), float(at), float(over))
            for degrees, at, over in (re.fullmatch(r"(.+)@(.+)/(.+)", text).groups() for text in args.turns)
        ]
    except (AttributeError, ValueError):
        print("--turn takes DEGREES@SECONDS/OVER", file=sys.stderr)
        return 2
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    passes = [tuple(text.split("=", 1)) if "=" in text else (text, "") for text in args.passes] or [("moving", "")]
    driver = Driver(game.port_of(args))
    try:
        rows = [(label, one_pass(driver, args, label, commands, out)) for label, commands in passes]
    finally:
        driver.close()

    cell_width, cell_height = rows[0][1][0].size
    count = max(1, min(args.sheet, args.frames))
    picks = [round(i * (args.frames - 1) / max(1, count - 1)) for i in range(count)]
    label_height = 22
    sheet = Image.new("RGB", (cell_width * count, (cell_height + label_height) * len(rows)), (24, 24, 24))
    draw = ImageDraw.Draw(sheet)
    for row, (label, frames) in enumerate(rows):
        top = row * (cell_height + label_height)
        draw.text((6, top + 5), f"{label}: {' + '.join(args.keys)}, from the {args.view}", fill=(235, 235, 235))
        for column, pick in enumerate(picks):
            sheet.paste(frames[pick], (column * cell_width, top + label_height))
    sheet.save(out / "sheet.png")

    # The GIF shows the passes side by side, in step.
    gif = []
    for index in range(args.frames):
        frame = Image.new("RGB", (cell_width * len(rows), cell_height + label_height), (24, 24, 24))
        draw = ImageDraw.Draw(frame)
        for column, (label, frames) in enumerate(rows):
            draw.text((column * cell_width + 6, 5), label, fill=(235, 235, 235))
            frame.paste(frames[index], (column * cell_width, label_height))
        gif.append(frame.quantize(colors=128))
    duration = round(1000 * args.every / TICKS_A_SECOND * args.slow)
    gif[0].save(out / "moving.gif", save_all=True, append_images=gif[1:], duration=duration, loop=0)
    print(json.dumps({"sheet": str(out / "sheet.png"), "gif": str(out / "moving.gif")}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
