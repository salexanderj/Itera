#!/usr/bin/env python3
"""Builds game models from Python recipes run by Blender with no window. Development tool, not game code.

    tools/models/build.py build RECIPE.py [--out DIR] [--work DIR] [--no-check] [--no-textures]
    tools/models/build.py docs [--force]
    tools/models/build.py verify [RECIPE.py ...] [--against DIR]
    tools/models/build.py selftest

Exit codes: 0 the model is ready, 1 a problem was found, 2 a wrong command line, a missing Blender, or a Blender whose
version is not the pinned one (blender.json). README.md has the details. Standard library only; run it with python3.
"""

from __future__ import annotations

import argparse
import ast
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path

MODELS_DIR = Path(__file__).resolve().parent
REPO = MODELS_DIR.parents[1]
PIN_FILE = MODELS_DIR / "blender.json"
RUN_RECIPE = MODELS_DIR / "run_recipe.py"
TESTS_RUN = MODELS_DIR / "tests" / "run.py"
TEST_RECIPE = MODELS_DIR / "tests" / "recipes" / "TestCrate" / "TestCrate.py"
BLENDER_FLAGS = ("-b", "--factory-startup", "--python-exit-code", "1")
BLENDER_REPOSITORY = "https://github.com/blender/blender.git"
DOC_FOLDERS = ("doc/python_api", "source/blender/bmesh/intern", "scripts/templates_py")
LOG_TAIL_LINES = 30


class ToolError(Exception):
    """A failure that ends the command with a message and an exit code."""

    def __init__(self, message: str, code: int = 1) -> None:
        super().__init__(message)
        self.code = code


# ---- Blender -------------------------------------------------------------------------------------------------------


def pinned_version() -> str:
    """The Blender version the toolkit is pinned to (blender.json)."""
    return json.loads(PIN_FILE.read_text(encoding="utf-8"))["version"]


def find_blender() -> str | None:
    """The Blender executable: $ITERA_BLENDER, or blender on the PATH. None when there is none."""
    configured = os.environ.get("ITERA_BLENDER")
    if configured:
        return configured if Path(configured).is_file() or shutil.which(configured) else None
    return shutil.which("blender")


def parse_blender_version(output: str) -> str | None:
    """The version number in the first line of `blender --version` ("Blender 5.2.2 LTS" gives "5.2.2")."""
    for line in output.splitlines():
        match = re.match(r"\s*Blender (\d+\.\d+\.\d+)\b", line)
        if match:
            return match.group(1)
    return None


def check_blender() -> str:
    """Returns the Blender executable when it is the pinned version. Otherwise raises a ToolError with code 2."""
    executable = find_blender()
    if executable is None:
        raise ToolError("Blender was not found. Install it, put it on the PATH, or set ITERA_BLENDER.", 2)
    try:
        result = subprocess.run([executable, "--version"], capture_output=True, text=True, timeout=120, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ToolError(f"Blender could not be run ({executable}): {error}", 2) from error
    found = parse_blender_version(result.stdout)
    pinned = pinned_version()
    if found != pinned:
        shown = found or "an unknown version"
        raise ToolError(
            f"{executable} is Blender {shown}, but the toolkit is pinned to {pinned} (tools/models/blender.json). "
            "Raise the pin on purpose: install the new version, run `build.py selftest` and `build.py verify`, "
            "and change blender.json only when both pass.",
            2,
        )
    return executable


def run_blender(executable: str, script: Path, script_arguments: list, log_path: Path | None) -> int:
    """Runs a Blender script with no window. Writes Blender's output to log_path. Returns the exit code."""
    command = [executable, *BLENDER_FLAGS, "--python", str(script), "--", *script_arguments]
    if log_path is None:
        return subprocess.run(command, check=False).returncode
    log_path.parent.mkdir(parents=True, exist_ok=True)
    with log_path.open("wb") as log:
        return subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=False).returncode


def log_tail(log_path: Path, lines: int = LOG_TAIL_LINES) -> str:
    """The last lines of a log file."""
    try:
        text = log_path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return "(no log)"
    return "\n".join(text.splitlines()[-lines:])


# ---- Recipes -------------------------------------------------------------------------------------------------------


@dataclass
class Recipe:
    """What build.py reads from a recipe file without running it."""

    path: Path
    name: str
    budget_class: str | None = None
    budgets: Path | None = None
    unit: float = 1.0
    references: dict = field(default_factory=dict)


def read_recipe(path) -> Recipe:
    """Reads the recipe's constants from its text with ast, so that no Blender is needed. Raises ToolError (code 2)."""
    path = Path(path).resolve()
    if not path.is_file():
        raise ToolError(f"{path}: no such recipe file", 2)
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    except SyntaxError as error:
        raise ToolError(f"{path}: not valid Python: {error}", 2) from error
    constants: dict = {}
    has_build = False
    for node in tree.body:
        if isinstance(node, ast.FunctionDef) and node.name == "build":
            has_build = True
        if isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name):
            key = node.targets[0].id
            if key in ("NAME", "CLASS", "BUDGETS", "UNIT", "REFERENCES"):
                try:
                    constants[key] = ast.literal_eval(node.value)
                except ValueError as error:
                    raise ToolError(f"{path}: {key} must be a plain literal", 2) from error
    name = constants.get("NAME")
    if not isinstance(name, str) or not name:
        raise ToolError(f'{path}: the recipe needs NAME = "..."', 2)
    if not has_build:
        raise ToolError(f"{path}: the recipe needs def build(ctx)", 2)
    budgets = None
    if "BUDGETS" in constants:
        budgets = (path.parent / constants["BUDGETS"]).resolve()
    elif (path.parent.parent / "budgets.json").is_file():
        budgets = (path.parent.parent / "budgets.json").resolve()
    return Recipe(
        path=path,
        name=name,
        budget_class=constants.get("CLASS"),
        budgets=budgets,
        unit=float(constants.get("UNIT", 1.0)),
        references=dict(constants.get("REFERENCES", {})),
    )


def default_recipes() -> list:
    """Every src/Game/ModelRecipes/*/*.py that defines NAME, in path order."""
    found = []
    for path in sorted((REPO / "src" / "Game" / "ModelRecipes").glob("*/*.py")):
        try:
            found.append(read_recipe(path))
        except ToolError:
            continue
    return found


# ---- Folders -------------------------------------------------------------------------------------------------------


def cache_root() -> Path:
    """The cache folder: $ITERA_CACHE, or ~/.cache/itera."""
    return Path(os.environ.get("ITERA_CACHE") or Path.home() / ".cache" / "itera")


def agent_data_folder() -> Path:
    """The data folder of the runs Claude starts: ~/.local/share/<DataFolder>-agent (tools/benchmark/data_folder.py)."""
    module_path = REPO / "tools" / "benchmark" / "data_folder.py"
    spec = importlib.util.spec_from_file_location("itera_data_folder", module_path)
    if spec is None or spec.loader is None or not module_path.is_file():
        return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / "Itera-agent"
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.data_folder(agent=True)


def default_out_dir(name: str) -> Path:
    """The model's folder in the game: src/Game/Models/<NAME>."""
    return REPO / "src" / "Game" / "Models" / name


def default_work_dir(name: str) -> Path:
    """The work folder: <agent data folder>/ModelBuilds/<NAME>."""
    return agent_data_folder() / "ModelBuilds" / name


# ---- build ---------------------------------------------------------------------------------------------------------


def run_tool(command: list, label: str) -> subprocess.CompletedProcess:
    """Runs a dotnet tool from the repository root and returns the result. Raises ToolError when dotnet is missing."""
    try:
        return subprocess.run(command, cwd=REPO, capture_output=True, text=True, check=False)
    except OSError as error:
        raise ToolError(f"{label}: could not run {command[0]}: {error}", 1) from error


def extract_json_list(text: str):
    """The first JSON list in a text that may have other lines before it."""
    decoder = json.JSONDecoder()
    for match in re.finditer(r"^\[", text, re.MULTILINE):
        try:
            value, _ = decoder.raw_decode(text[match.start() :])
        except ValueError:
            continue
        return value
    return None


def model_check(recipe: Recipe, glb: Path) -> dict:
    """Runs tools/ModelCheck on the model and returns its report. Raises ToolError when it cannot run."""
    command = ["dotnet", "run", "-c", "Release", "--project", "tools/ModelCheck", "--", "--json"]
    if recipe.unit != 1.0:
        command += ["--unit", repr(recipe.unit)]
    if recipe.budget_class and recipe.budgets and recipe.budgets.is_file():
        command += ["--budget", str(recipe.budgets), "--class", recipe.budget_class]
    command.append(str(glb))
    result = run_tool(command, "ModelCheck")
    reports = extract_json_list(result.stdout)
    if result.returncode not in (0, 1) or not reports:
        raise ToolError(f"ModelCheck failed (exit {result.returncode}):\n{(result.stderr or result.stdout)[-2000:]}")
    return reports[0]


def compress_textures(glb: Path) -> list:
    """Runs tools/Textures' model mode. Returns a list of problem sentences (empty when it worked)."""
    command = ["dotnet", "run", "-c", "Release", "--project", "tools/Textures", "--", "model", str(glb)]
    result = run_tool(command, "Textures")
    if result.returncode != 0:
        detail = (result.stderr or result.stdout)[-500:].strip()
        return [f"texture compression failed (exit {result.returncode}): {detail}"]
    return []


@dataclass
class BuildResult:
    """What a build produced."""

    recipe: Recipe
    out_path: Path
    work_dir: Path
    problems: list = field(default_factory=list)
    warnings: list = field(default_factory=list)
    notes: list = field(default_factory=list)
    report: dict = field(default_factory=dict)
    check: dict | None = None
    textures_made: bool = False

    @property
    def ready(self) -> bool:
        return not self.problems


def build_recipe(
    recipe: Recipe,
    executable: str,
    out_dir: Path,
    work_dir: Path,
    check: bool = True,
    textures: bool = True,
) -> BuildResult:
    """Runs the recipe in Blender, then the checker and the texture tool as asked. Never raises for a model problem."""
    out_path = out_dir / f"{recipe.name}.glb"
    result = BuildResult(recipe, out_path, work_dir)
    work_dir.mkdir(parents=True, exist_ok=True)
    for stale in (work_dir / "report.json", work_dir / f"{recipe.name}-sheet.png"):
        stale.unlink(missing_ok=True)
    out_dir.mkdir(parents=True, exist_ok=True)

    log_path = work_dir / "blender.log"
    code = run_blender(executable, RUN_RECIPE, [str(recipe.path), str(out_path), str(work_dir)], log_path)
    report_path = work_dir / "report.json"
    if code != 0 or not report_path.is_file():
        result.problems.append(f"Blender failed (exit {code}); the end of {log_path}:\n{log_tail(log_path)}")
        return result
    result.report = json.loads(report_path.read_text(encoding="utf-8"))
    result.problems += result.report.get("problems", [])
    result.warnings += result.report.get("warnings", [])
    result.notes += result.report.get("notes", [])

    if check:
        try:
            result.check = model_check(recipe, out_path)
        except ToolError as error:
            result.problems.append(str(error))
        else:
            result.problems += [f"[{item['kind']}] {item['message']}" for item in result.check.get("problems", [])]
            result.notes += result.check.get("notes", [])
            if textures and result.check.get("textures"):
                try:
                    texture_problems = compress_textures(out_path)
                except ToolError as error:
                    texture_problems = [str(error)]
                result.problems += texture_problems
                result.textures_made = not texture_problems
    return result


def describe_motion(motion: dict) -> str:
    """One line of the clip checks: each clip's loop gap, its feet's largest slide, and the speed its feet show."""
    parts = []
    for name, entry in motion.get("clips", {}).items():
        found = []
        if "loop" in entry:
            found.append(f"loop gap {entry['loop']['gap'] * 100:.1f} cm")
        if entry.get("feet"):
            most = max(foot["slide"] for foot in entry["feet"].values())
            found.append(f"slide {most * 100:.1f} cm at {entry['speed']:.2f} m/s shown")
        parts.append(f"{name} ({', '.join(found)})" if found else name)
    return "; ".join(parts) or "no clips"


def print_build(result: BuildResult) -> None:
    """Prints the short summary of a build."""
    status = "ready" if result.ready else "has problems"
    print(f"{result.recipe.name}: {status}")
    if result.report and result.out_path.is_file():
        print(f"  output:    {result.out_path}")
    if result.check is not None:
        check = result.check
        clips = ", ".join(clip["name"] for clip in check.get("clips", [])) or "none"
        points = ", ".join(point["name"] for point in check.get("points", [])) or "none"
        joints = len(check.get("joints", []))
        print(f"  triangles: {check.get('triangles')}   vertices: {check.get('vertices')}   joints: {joints}")
        print(f"  clips:     {clips}")
        print(f"  points:    {points}")
        if result.textures_made:
            print("  textures:  compressed (NAME.textures/ beside the model)")
    elif result.report:
        scene = result.report.get("scene", {})
        print(f"  triangles: {scene.get('triangles')}   vertices: {scene.get('vertices')}   (checker skipped)")
    for label, items in (("problem", result.problems), ("warning", result.warnings), ("note", result.notes)):
        for item in items:
            print(f"  {label}: {item}")
    if not result.problems:
        print("  problems:  none")
    if result.report and result.report.get("motion"):
        print(f"  motion:    {describe_motion(result.report['motion'])}")
    if result.report and result.report.get("references"):
        scores = ", ".join(f"{view} {score:.2f}" for view, score in result.report["references"].items())
        print(f"  silhouette match: {scores}")
    if result.report:
        print(f"  sheet:     {result.report.get('sheet')}")
        print(f"  report:    {result.work_dir / 'report.json'}")
    print(f"  log:       {result.work_dir / 'blender.log'}")


def command_build(arguments) -> int:
    recipe = read_recipe(arguments.recipe)
    executable = check_blender()
    out_dir = Path(arguments.out).resolve() if arguments.out else default_out_dir(recipe.name)
    work_dir = Path(arguments.work).resolve() if arguments.work else default_work_dir(recipe.name)
    result = build_recipe(recipe, executable, out_dir, work_dir, not arguments.no_check, not arguments.no_textures)
    print_build(result)
    return 0 if result.ready else 1


# ---- docs ----------------------------------------------------------------------------------------------------------


def docs_folder(version: str) -> Path:
    """Where the API reference of a Blender version is kept: <cache>/blender-<version>/api."""
    return cache_root() / f"blender-{version}" / "api"


def run_checked(command: list, label: str) -> None:
    """Runs a command and raises ToolError when it fails."""
    try:
        result = subprocess.run(command, capture_output=True, text=True, check=False)
    except OSError as error:
        raise ToolError(f"{label}: could not run {command[0]}: {error}") from error
    if result.returncode != 0:
        raise ToolError(f"{label} failed (exit {result.returncode}):\n{(result.stderr or result.stdout)[-2000:]}")


def command_docs(arguments) -> int:
    version = pinned_version()
    api = docs_folder(version)
    rst = api / "sphinx-in"
    if rst.is_dir() and not arguments.force:
        print(f"The API reference is already built: {rst}\n(use --force to build it again)")
        return 0
    executable = check_blender()
    base = api.parent
    source = base / "source"
    if arguments.force:
        shutil.rmtree(base, ignore_errors=True)
    if not (source / "doc" / "python_api" / "sphinx_doc_gen.py").is_file():
        shutil.rmtree(source, ignore_errors=True)
        base.mkdir(parents=True, exist_ok=True)
        run_checked(
            [
                "git", "clone", "--depth", "1", "--filter=blob:none", "--sparse",
                "--branch", f"v{version}", BLENDER_REPOSITORY, str(source),
            ],
            "git clone",
        )  # fmt: skip
        run_checked(["git", "-C", str(source), "sparse-checkout", "set", *DOC_FOLDERS], "git sparse-checkout")
    run_checked(
        [
            executable, "-b", "--factory-startup", "--python-exit-code", "1",
            "--python", str(source / "doc" / "python_api" / "sphinx_doc_gen.py"),
            "--", f"--output={api}",
        ],
        "Blender's API documentation generator",
    )  # fmt: skip
    if not rst.is_dir():
        raise ToolError(f"the generator finished but {rst} does not exist")
    print(rst)
    return 0


# ---- verify --------------------------------------------------------------------------------------------------------


def command_verify(arguments) -> int:
    recipes = [read_recipe(path) for path in arguments.recipes] if arguments.recipes else default_recipes()
    if not recipes:
        print("No recipes to verify (none given, and none in src/Game/ModelRecipes).")
        return 0
    executable = check_blender()
    models = Path(arguments.against).resolve() if arguments.against else REPO / "src" / "Game" / "Models"
    different = 0
    with tempfile.TemporaryDirectory(prefix="itera-verify-") as folder:
        for recipe in recipes:
            committed = models / recipe.name / f"{recipe.name}.glb"
            work = Path(folder) / recipe.name / "work"
            result = build_recipe(recipe, executable, Path(folder) / recipe.name, work, check=False, textures=False)
            if result.report == {}:
                print(f"{recipe.name}: DIFFERS (the rebuild failed)\n{result.problems[0]}")
                different += 1
            elif not committed.is_file():
                print(f"{recipe.name}: DIFFERS (no committed file at {committed})")
                different += 1
            elif committed.read_bytes() == result.out_path.read_bytes():
                print(f"{recipe.name}: matches")
            else:
                print(f"{recipe.name}: DIFFERS ({describe_difference(committed, result.out_path)})")
                different += 1
    return 1 if different else 0


def describe_difference(first: Path, second: Path) -> str:
    """A short account of how two .glb files differ: sizes, and whether the JSON chunks match."""
    first_bytes, second_bytes = first.read_bytes(), second.read_bytes()
    first_json, second_json = glb_json_chunk(first_bytes), glb_json_chunk(second_bytes)
    if first_json == second_json:
        where = "the JSON chunks are the same, so the binary data differs"
    else:
        where = "the JSON chunks differ"
    return f"{len(first_bytes)} and {len(second_bytes)} bytes; {where}"


def glb_json_chunk(data: bytes) -> bytes:
    """The JSON chunk of a .glb file, or b"" when there is none."""
    offset = 12
    while offset + 8 <= len(data):
        length = int.from_bytes(data[offset : offset + 4], "little")
        kind = data[offset + 4 : offset + 8]
        if kind == b"JSON":
            return data[offset + 8 : offset + 8 + length]
        offset += 8 + length
    return b""


# ---- selftest ------------------------------------------------------------------------------------------------------


def command_selftest(arguments) -> int:
    results: list = []

    def step(name: str, action) -> None:
        started = time.perf_counter()
        ok = action()
        results.append((name, ok, time.perf_counter() - started))

    step(
        "plain Python tests",
        lambda: subprocess.run([sys.executable, str(TESTS_RUN), "--plain"], check=False).returncode == 0,
    )
    executable = check_blender()
    with tempfile.TemporaryDirectory(prefix="itera-selftest-") as folder:
        root = Path(folder)
        step("Blender tests", lambda: selftest_blender_tests(executable, root / "blender-tests.log"))
        step("end-to-end build and ModelCheck", lambda: selftest_build(executable, root))
        step("second build is byte-identical", lambda: selftest_verify(executable, root))
    print("\nselftest:")
    for name, ok, seconds in results:
        print(f"  {'ok  ' if ok else 'FAIL'} {name} ({seconds:.1f} s)")
    return 0 if all(ok for _, ok, _ in results) else 1


def selftest_blender_tests(executable: str, log_path: Path) -> bool:
    """Runs tests/run.py in Blender. Prints the summary line, or the end of the log when a test failed."""
    code = run_blender(executable, TESTS_RUN, [], log_path)
    lines = log_path.read_text(encoding="utf-8", errors="replace").splitlines()
    summary = [line for line in lines if re.match(r"^(Ran \d+ tests|OK|FAILED)", line)]
    if code == 0:
        print("Blender tests: " + "; ".join(summary))
        return True
    print(log_tail(log_path, 80))
    return False


def selftest_build(executable: str, root: Path) -> bool:
    """Builds the test recipe with the checker and checks the result."""
    recipe = read_recipe(TEST_RECIPE)
    result = build_recipe(recipe, executable, root / "models" / recipe.name, root / "work", check=True, textures=True)
    print_build(result)
    checked = result.check or {}
    expected = (
        result.ready
        and result.out_path.is_file()
        and [clip["name"] for clip in checked.get("clips", [])] == ["Open"]
        and [point["name"] for point in checked.get("points", [])] == ["latch"]
        and len(checked.get("joints", [])) == 2
        and len(checked.get("textures", [])) == 1
        and result.textures_made
        and (result.out_path.parent / f"{recipe.name}.textures").is_dir()
    )
    if result.ready and not expected:
        print("  the model is ready but not what the test recipe makes")
    return expected


def selftest_verify(executable: str, root: Path) -> bool:
    """Rebuilds the test recipe and compares it with the first build."""
    arguments = argparse.Namespace(recipes=[str(TEST_RECIPE)], against=str(root / "models"))
    return command_verify(arguments) == 0


# ---- command line --------------------------------------------------------------------------------------------------


# ---- views ---------------------------------------------------------------------------------------------------------


def parse_clip(text: str) -> tuple[str, float]:
    """CLIP@SECONDS, for a pose to draw."""
    clip, _, seconds = text.rpartition("@")
    try:
        return clip, float(seconds)
    except ValueError:
        raise argparse.ArgumentTypeError(f"{text}: a pose is CLIP@SECONDS") from None


def command_views(arguments) -> int:
    """Draws the model with the engine itself: on ModelView's neutral stage, or in the game's world at a tp place."""
    import engine_views

    model = Path(arguments.model)
    if model.suffix == ".py":
        recipe = read_recipe(model)
        glb = default_out_dir(recipe.name) / f"{recipe.name}.glb"
        stem, unit = recipe.name, recipe.unit
    else:
        glb, stem, unit = model.resolve(), model.stem, arguments.unit
    if not glb.is_file():
        raise ToolError(f"{glb}: no such model (build the recipe first)", 2)
    name = arguments.name or stem.lower()
    out_dir = (
        Path(arguments.out).resolve()
        if arguments.out
        else default_work_dir(stem) / ("world-views" if arguments.world else "views")
    )
    out_dir.mkdir(parents=True, exist_ok=True)
    for old in out_dir.glob("*.png"):
        old.unlink()
    try:
        if arguments.world:
            pictures = engine_views.in_world(
                glb, name, unit, arguments.world, out_dir, arguments.clip, arguments.size, arguments.release
            )
        else:
            pictures = engine_views.on_stage(
                glb, name, unit, out_dir, arguments.clip, arguments.size, arguments.release
            )
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"{name}: the engine did not draw it: {error}", file=sys.stderr)
        return 1

    order = [view.name for view in engine_views.views(engine_views.Bounds((0, 0, 0), (1, 1, 1)))] + ["from-player"]
    pictures.sort(key=lambda path: (order.index(path.stem) if path.stem in order else len(order), path.name))
    sheet = out_dir / "sheet.png"
    executable = check_blender()
    command = [
        executable,
        *BLENDER_FLAGS,
        "--python",
        str(MODELS_DIR / "tile_pictures.py"),
        "--",
        str(sheet),
        "3",
        "384",
    ]
    tiled = subprocess.run(command + [str(path) for path in pictures], capture_output=True, text=True, check=False)
    print(f"{name}: drawn by the engine {'at ' + arguments.world if arguments.world else 'on the stage'}")
    for path in pictures:
        print(f"  {path}")
    if tiled.returncode == 0:
        print(f"  sheet: {sheet}")
    else:
        print(f"  (no sheet: {tiled.stdout[-500:]})")
    return 0


def make_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Builds game models from Python recipes run by Blender.")
    commands = parser.add_subparsers(dest="command", required=True)

    build = commands.add_parser("build", help="build a recipe into a .glb, check it, and compress its textures")
    build.add_argument("recipe", help="the recipe file (RECIPE.py)")
    build.add_argument("--out", help="the folder for NAME.glb (default: src/Game/Models/NAME)")
    build.add_argument("--work", help="the work folder (default: <agent data folder>/ModelBuilds/NAME)")
    build.add_argument("--no-check", action="store_true", help="skip tools/ModelCheck")
    build.add_argument("--no-textures", action="store_true", help="skip tools/Textures")
    build.set_defaults(handler=command_build)

    docs = commands.add_parser("docs", help="build the local Blender API reference for the pinned version")
    docs.add_argument("--force", action="store_true", help="build it again even when it exists")
    docs.set_defaults(handler=command_docs)

    verify = commands.add_parser("verify", help="rebuild recipes and compare with the committed .glb files")
    verify.add_argument("recipes", nargs="*", help="recipe files (default: every src/Game/ModelRecipes/*/*.py)")
    verify.add_argument("--against", help="compare with DIR/NAME/NAME.glb instead of src/Game/Models/NAME/NAME.glb")
    verify.set_defaults(handler=command_verify)

    views = commands.add_parser("views", help="draw a model with the engine itself, from set angles")
    views.add_argument("model", help="a .glb or .gltf file, or a recipe (its built model)")
    views.add_argument("--world", metavar="PLACE", help="in the game's world at its tp place PLACE, not on the stage")
    views.add_argument(
        "--clip",
        action="append",
        default=[],
        type=parse_clip,
        metavar="CLIP@SECONDS",
        help="also draw the model held at SECONDS of CLIP (repeat for more)",
    )
    views.add_argument("--name", help="the name the run reads it by (default: the file's name, lower case)")
    views.add_argument("--unit", type=float, default=1.0, help="metres a unit of a .glb given directly (default 1)")
    views.add_argument("--out", help="the folder for the pictures (default: the work folder's views/)")
    views.add_argument("--size", type=int, default=768, help="each picture's side in pixels (default 768)")
    views.add_argument("--release", action="store_true", help="use the Release build")
    views.set_defaults(handler=command_views)

    selftest = commands.add_parser("selftest", help="run the toolkit's own tests")
    selftest.set_defaults(handler=command_selftest)
    return parser


def main(argv: list | None = None) -> int:
    arguments = make_parser().parse_args(argv)
    try:
        return arguments.handler(arguments)
    except ToolError as error:
        print(str(error), file=sys.stderr)
        return error.code


if __name__ == "__main__":
    sys.exit(main())
