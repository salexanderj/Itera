#!/usr/bin/env python3
"""Runs the toolkit's tests with unittest and exits non-zero when one fails.

In Blender (the Blender tests, in one process):

    blender -b --factory-startup --python-exit-code 1 --python tests/run.py -- [PATTERN]

With python3 (the plain-Python tests, which do not need Blender):

    python3 tests/run.py --plain [PATTERN]

A test file is a plain-Python test when it has the line `PLAIN_PYTHON = True`. Every other tests/test_*.py file is a
Blender test. PATTERN limits the run to test files whose name contains it (for example `export`).
"""

import sys
import unittest
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
MODELS_DIR = TESTS_DIR.parent
PLAIN_MARKER = "PLAIN_PYTHON = True"


def is_plain(path: Path) -> bool:
    """True when the test file is marked as a plain-Python test."""
    return PLAIN_MARKER in path.read_text(encoding="utf-8").splitlines()


def test_files(plain: bool, pattern: str = "") -> list:
    """The test files to run: the plain ones or the Blender ones, in name order."""
    paths = sorted(TESTS_DIR.glob("test_*.py"))
    return [path for path in paths if is_plain(path) == plain and pattern in path.name]


def main(arguments: list) -> int:
    plain = "--plain" in arguments
    patterns = [argument for argument in arguments if not argument.startswith("--")]
    for folder in (str(TESTS_DIR), str(MODELS_DIR)):
        if folder not in sys.path:
            sys.path.insert(0, folder)
    sys.dont_write_bytecode = True
    loader = unittest.TestLoader()
    suite = unittest.TestSuite()
    for path in test_files(plain, patterns[0] if patterns else ""):
        suite.addTests(loader.loadTestsFromName(path.stem))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    return 0 if result.wasSuccessful() else 1


def in_blender() -> bool:
    """True when this script runs inside Blender (where the bpy module exists)."""
    try:
        import bpy  # noqa: F401
    except ImportError:
        return False
    return True


if __name__ == "__main__":
    if in_blender():
        # Everything after the double dash is ours.
        extra = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
        code = main(extra)
        sys.stdout.flush()
        sys.stderr.flush()
        if code != 0:
            sys.exit(code)
    else:
        sys.exit(main(sys.argv[1:]))
