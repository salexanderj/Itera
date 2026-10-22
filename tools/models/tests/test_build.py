"""Plain-Python tests of build.py (no Blender needed): `python3 tests/run.py --plain`."""

import argparse
import contextlib
import io
import os
import stat
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path
from unittest import mock

PLAIN_PYTHON = True

MODELS_DIR = Path(__file__).resolve().parents[1]
if str(MODELS_DIR) not in sys.path:
    sys.path.insert(0, str(MODELS_DIR))

import build


def write_recipe(folder: Path, text: str, name: str = "Thing") -> Path:
    """Writes a recipe file into folder/name/name.py and returns its path."""
    path = folder / name / f"{name}.py"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(textwrap.dedent(text), encoding="utf-8")
    return path


def fake_blender(folder: Path, first_line: str) -> Path:
    """A program that prints a Blender version line, for the version check. Not for Windows."""
    path = folder / "fake-blender"
    path.write_text(f"#!{sys.executable}\nprint({first_line!r})\n", encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IXUSR)
    return path


class BlenderCheckTests(unittest.TestCase):
    def test_the_pin_is_a_version_number(self):
        self.assertRegex(build.pinned_version(), r"^\d+\.\d+\.\d+$")

    def test_version_is_read_from_the_first_line(self):
        self.assertEqual("5.2.2", build.parse_blender_version("Blender 5.2.2 LTS\n\tbuild date: 2026-09-15\n"))
        self.assertEqual("4.5.14", build.parse_blender_version("Blender 4.5.14\n"))
        self.assertIsNone(build.parse_blender_version("Python 3.14.4\n"))
        self.assertIsNone(build.parse_blender_version(""))

    def test_missing_blender_is_refused_with_code_2(self):
        with mock.patch.dict(os.environ, {"ITERA_BLENDER": "/nonexistent/blender"}):
            with self.assertRaises(build.ToolError) as raised:
                build.check_blender()
        self.assertEqual(2, raised.exception.code)

    @unittest.skipIf(sys.platform == "win32", "the fake Blender is a script with a shebang line")
    def test_other_version_is_refused_and_the_pinned_one_is_accepted(self):
        with tempfile.TemporaryDirectory() as folder:
            other = fake_blender(Path(folder), "Blender 9.9.9")
            with mock.patch.dict(os.environ, {"ITERA_BLENDER": str(other)}):
                with self.assertRaises(build.ToolError) as raised:
                    build.check_blender()
            self.assertEqual(2, raised.exception.code)
            self.assertIn("pinned to " + build.pinned_version(), str(raised.exception))
            self.assertIn("selftest", str(raised.exception))
            self.assertIn("verify", str(raised.exception))
            pinned = fake_blender(Path(folder), f"Blender {build.pinned_version()} LTS")
            with mock.patch.dict(os.environ, {"ITERA_BLENDER": str(pinned)}):
                self.assertEqual(str(pinned), build.check_blender())

    @unittest.skipIf(sys.platform == "win32", "the fake Blender is a script with a shebang line")
    def test_main_returns_2_for_a_mismatch_and_for_a_bad_recipe(self):
        with tempfile.TemporaryDirectory() as folder:
            recipe = write_recipe(Path(folder), 'NAME = "Thing"\ndef build(ctx):\n    pass\n')
            other = fake_blender(Path(folder), "Blender 9.9.9")
            with mock.patch.dict(os.environ, {"ITERA_BLENDER": str(other)}), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(2, build.main(["build", str(recipe)]))
                self.assertEqual(2, build.main(["build", str(Path(folder) / "missing.py")]))


class RecipeTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)

    def test_a_recipe_is_read_without_running_it(self):
        path = write_recipe(
            self.root,
            """
            import bpy_that_does_not_exist
            NAME = "Thing"
            CLASS = "prop"
            BUDGETS = "my-budgets.json"
            UNIT = 0.01
            REFERENCES = {"front": "front.png"}

            def build(ctx):
                raise RuntimeError("never run")
            """,
        )
        recipe = build.read_recipe(path)
        self.assertEqual("Thing", recipe.name)
        self.assertEqual("prop", recipe.budget_class)
        self.assertEqual(0.01, recipe.unit)
        self.assertEqual({"front": "front.png"}, recipe.references)
        self.assertEqual((path.parent / "my-budgets.json").resolve(), recipe.budgets)

    def test_budgets_default_to_the_file_above_the_recipes_folder_when_it_exists(self):
        path = write_recipe(self.root, 'NAME = "Thing"\ndef build(ctx):\n    pass\n')
        self.assertIsNone(build.read_recipe(path).budgets)
        (self.root / "budgets.json").write_text("{}", encoding="utf-8")
        recipe = build.read_recipe(path)
        self.assertEqual((self.root / "budgets.json").resolve(), recipe.budgets)
        self.assertEqual(1.0, recipe.unit)
        self.assertIsNone(recipe.budget_class)

    def test_a_recipe_needs_a_name_and_a_build_function(self):
        for text in (
            "def build(ctx):\n    pass\n",
            'NAME = "Thing"\n',
            "NAME = make_name()\ndef build(ctx):\n    pass\n",
            'NAME = ""\ndef build(ctx):\n    pass\n',
            "NAME = (\n",
        ):
            with self.subTest(text=text):
                path = write_recipe(self.root, text)
                with self.assertRaises(build.ToolError) as raised:
                    build.read_recipe(path)
                self.assertEqual(2, raised.exception.code)

    def test_default_recipes_are_the_ones_that_define_a_name(self):
        recipes = self.root / "src" / "Game" / "ModelRecipes"
        write_recipe(recipes, 'NAME = "C"\ndef build(ctx):\n    pass\n', name="C")
        write_recipe(recipes, "y = 1\n", name="D")
        with mock.patch.object(build, "REPO", self.root):
            names = [recipe.name for recipe in build.default_recipes()]
        self.assertEqual(["C"], names)


class PathTests(unittest.TestCase):
    def test_default_output_is_the_games_models_folder(self):
        self.assertEqual(build.REPO / "src" / "Game" / "Models" / "Crate", build.default_out_dir("Crate"))

    def test_default_work_folder_is_in_the_agent_data_folder(self):
        with tempfile.TemporaryDirectory() as data, mock.patch.dict(os.environ, {"XDG_DATA_HOME": data}):
            work = build.default_work_dir("Crate")
        self.assertEqual(Path(data), work.parents[2])
        self.assertTrue(work.parents[1].name.endswith("-agent"), work)
        self.assertEqual(("ModelBuilds", "Crate"), (work.parent.name, work.name))

    def test_cache_is_itera_cache_or_the_home_cache(self):
        with mock.patch.dict(os.environ, {"ITERA_CACHE": "/somewhere/cache"}):
            self.assertEqual(Path("/somewhere/cache"), build.cache_root())
        environment = {key: value for key, value in os.environ.items() if key != "ITERA_CACHE"}
        with mock.patch.dict(os.environ, environment, clear=True):
            self.assertEqual(Path.home() / ".cache" / "itera", build.cache_root())

    def test_the_api_reference_folder_is_named_for_the_version(self):
        with mock.patch.dict(os.environ, {"ITERA_CACHE": "/c"}):
            self.assertEqual(Path("/c/blender-5.2.2/api"), build.docs_folder("5.2.2"))


class CommandTests(unittest.TestCase):
    def test_docs_skips_an_existing_reference(self):
        with tempfile.TemporaryDirectory() as cache:
            (Path(cache) / f"blender-{build.pinned_version()}" / "api" / "sphinx-in").mkdir(parents=True)
            output = io.StringIO()
            with mock.patch.dict(os.environ, {"ITERA_CACHE": cache, "ITERA_BLENDER": "/nonexistent"}):
                with contextlib.redirect_stdout(output):
                    code = build.main(["docs"])
        self.assertEqual(0, code)
        self.assertIn("sphinx-in", output.getvalue())

    def test_verify_with_no_recipes_says_so_and_succeeds(self):
        output = io.StringIO()
        with mock.patch.object(build, "default_recipes", return_value=[]), contextlib.redirect_stdout(output):
            code = build.command_verify(argparse.Namespace(recipes=[], against=None))
        self.assertEqual(0, code)
        self.assertIn("No recipes", output.getvalue())

    def test_a_wrong_command_line_exits_with_2(self):
        with contextlib.redirect_stderr(io.StringIO()):
            for argv in ([], ["build"], ["nonsense"], ["build", "a.py", "--nonsense"]):
                with self.subTest(argv=argv), self.assertRaises(SystemExit) as raised:
                    build.main(argv)
                self.assertEqual(2, raised.exception.code)

    def test_json_list_is_found_after_other_lines(self):
        text = 'Build succeeded.\n[\n  {"ok": true}\n]\n'
        self.assertEqual([{"ok": True}], build.extract_json_list(text))
        self.assertIsNone(build.extract_json_list("no json here\n"))


if __name__ == "__main__":
    unittest.main()
