"""Tests for itera_models.fetch. They use `file://` URLs only, so they need no network."""

import contextlib
import hashlib
import io
import json
import os
import tempfile
import unittest
import zipfile
from pathlib import Path

from itera_models import fetch


def _file_url(path) -> str:
    return Path(path).resolve().as_uri()


class ZipMemberTests(unittest.TestCase):
    """zip_member on a zip served from a `file://` URL: one stored and one deflated member, each read alone."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.cache = self.root / "cache"
        self.zip = self.root / "set.zip"
        self.walk = b"MOTION walk " * 500
        self.run = b"MOTION run " * 500
        with zipfile.ZipFile(self.zip, "w") as archive:
            archive.writestr("set/Walk/walk.bvh", self.walk, compress_type=zipfile.ZIP_DEFLATED)
            archive.writestr("set/Run/run.bvh", self.run, compress_type=zipfile.ZIP_STORED)
            archive.writestr("set/notes.txt", b"notes")

    def tearDown(self):
        self._tmp.cleanup()

    def test_reads_a_deflated_and_a_stored_member_by_name_into_the_cache(self):
        with contextlib.redirect_stdout(io.StringIO()):
            walk, walk_digest = fetch.zip_member(_file_url(self.zip), "set/Walk/walk.bvh", self.cache)
            run, _ = fetch.zip_member(_file_url(self.zip), "set/Run/run.bvh", self.cache, name="fast.bvh")

        self.assertEqual(walk.read_bytes(), self.walk)
        self.assertEqual(walk, self.cache / "walk.bvh")
        self.assertEqual(walk_digest, hashlib.sha256(self.walk).hexdigest())
        self.assertEqual(run.read_bytes(), self.run)
        self.assertEqual(run.name, "fast.bvh")

    def test_a_pinned_member_comes_from_the_cache_and_a_wrong_pin_drops_it(self):
        pin = hashlib.sha256(self.walk).hexdigest()
        fetch.zip_member(_file_url(self.zip), "set/Walk/walk.bvh", self.cache, sha256=pin)
        self.zip.unlink()
        path, _ = fetch.zip_member(_file_url(self.zip), "set/Walk/walk.bvh", self.cache, sha256=pin)
        self.assertEqual(path.read_bytes(), self.walk)

        with self.assertRaises(ValueError) as raised:
            fetch.zip_member(_file_url(self.zip), "set/Walk/walk.bvh", self.cache, sha256="0" * 64)
        self.assertIn(pin, str(raised.exception))
        self.assertFalse(path.exists())

    def test_a_member_the_zip_lacks_is_named(self):
        with self.assertRaises(ValueError) as raised:
            fetch.zip_member(_file_url(self.zip), "set/Swim/swim.bvh", self.cache)
        self.assertIn("set/Swim/swim.bvh", str(raised.exception))
        self.assertEqual(list(self.cache.iterdir()), [])


class FetchTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.cache = self.root / "cache"
        self.source = self.root / "source.bin"
        self.source.write_bytes(b"itera model bytes")
        self.digest = hashlib.sha256(b"itera model bytes").hexdigest()

    def tearDown(self):
        self._tmp.cleanup()

    def test_downloads_once_and_reads_the_cache(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            path, digest = fetch.fetch(_file_url(self.source), self.cache)
        self.assertEqual(digest, self.digest)
        self.assertEqual(path.read_bytes(), b"itera model bytes")
        self.assertEqual(path.parent, self.cache)

        # The source is gone; the second call must come from the cache.
        self.source.unlink()
        with contextlib.redirect_stdout(io.StringIO()):
            again, again_digest = fetch.fetch(_file_url(self.source), self.cache)
        self.assertEqual(again, path)
        self.assertEqual(again_digest, self.digest)

    def test_without_a_pin_prints_the_hash(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            fetch.fetch(_file_url(self.source), self.cache, name="renamed.bin")
        self.assertIn(self.digest, out.getvalue())
        self.assertIn("pin", out.getvalue())
        self.assertTrue((self.cache / "renamed.bin").exists())

    def test_with_the_right_pin_prints_nothing(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            fetch.fetch(_file_url(self.source), self.cache, sha256=self.digest)
        self.assertEqual(out.getvalue(), "")

    def test_a_wrong_pin_raises_with_both_hashes_and_drops_the_file(self):
        wrong = "0" * 64
        with self.assertRaises(ValueError) as caught:
            fetch.fetch(_file_url(self.source), self.cache, sha256=wrong)
        message = str(caught.exception)
        self.assertIn(self.digest, message)
        self.assertIn(wrong, message)
        self.assertFalse((self.cache / "source.bin").exists())

    def test_a_missing_file_raises_and_leaves_no_partial_file(self):
        with self.assertRaises(OSError):
            fetch.fetch(_file_url(self.root / "absent.bin"), self.cache)
        self.assertEqual([p.name for p in self.cache.iterdir()], [])


class TextureLibraryTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.cache = self.root / "cache"
        self._old_api = fetch.POLYHAVEN_API
        self._old_download = fetch.AMBIENTCG_DOWNLOAD

    def tearDown(self):
        fetch.POLYHAVEN_API = self._old_api
        fetch.AMBIENTCG_DOWNLOAD = self._old_download
        self._tmp.cleanup()

    def _make_polyhaven(self, resolution="1k"):
        api = self.root / "api"
        api.mkdir()
        files = self.root / "files"
        files.mkdir()
        listing = {}
        for key, code in (("Diffuse", "diff"), ("nor_gl", "nor_gl"), ("Rough", "rough"), ("AO", "ao")):
            file = files / f"rock_{code}.png"
            file.write_bytes(f"png {code}".encode())
            listing[key] = {resolution: {"png": {"url": _file_url(file), "md5": "unused"}}}
        (api / "rock").write_text(json.dumps(listing), encoding="utf-8")
        fetch.POLYHAVEN_API = _file_url(api) + "/{asset}"
        return files

    def test_polyhaven_returns_the_requested_maps(self):
        self._make_polyhaven()
        with contextlib.redirect_stdout(io.StringIO()):
            paths = fetch.polyhaven_texture("rock", "1k", cache_dir=self.cache)
        self.assertEqual(set(paths), {"diff", "nor_gl", "rough", "ao"})
        self.assertEqual(paths["nor_gl"].read_bytes(), b"png nor_gl")
        self.assertEqual(paths["diff"].parent, self.cache)

    def test_polyhaven_checks_the_pins(self):
        self._make_polyhaven()
        good = hashlib.sha256(b"png diff").hexdigest()
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            fetch.polyhaven_texture("rock", "1k", maps=("diff",), cache_dir=self.cache, hashes={"diff": good})
        self.assertEqual(out.getvalue(), "")
        with self.assertRaises(ValueError):
            fetch.polyhaven_texture("rock", "1k", maps=("diff",), cache_dir=self.cache / "b", hashes={"diff": "1" * 64})

    def test_polyhaven_names_a_missing_map_or_resolution(self):
        self._make_polyhaven()
        with self.assertRaises(ValueError) as caught:
            fetch.polyhaven_texture("rock", "4k", maps=("diff",), cache_dir=self.cache)
        self.assertIn("4k", str(caught.exception))
        with self.assertRaises(ValueError):
            fetch.polyhaven_texture("rock", "1k", maps=("nonsense",), cache_dir=self.cache)

    def _make_ambientcg(self, names):
        folder = self.root / "acg"
        folder.mkdir()
        with zipfile.ZipFile(folder / "Rock030_1K-PNG.zip", "w") as archive:
            for name in names:
                archive.writestr(name, name.encode())
            archive.writestr("../escape.txt", b"must not be written")
        fetch.AMBIENTCG_DOWNLOAD = _file_url(folder) + "/"

    def test_ambientcg_finds_maps_by_suffix_and_prefers_the_gl_normal(self):
        self._make_ambientcg(
            [
                "Rock030_1K-PNG_Color.png",
                "Rock030_1K-PNG_NormalDX.png",
                "Rock030_1K-PNG_NormalGL.png",
                "Rock030_1K-PNG_Roughness.png",
                "Rock030_1K-PNG_AmbientOcclusion.png",
                "Rock030_1K-PNG_Displacement.png",
            ]
        )
        with contextlib.redirect_stdout(io.StringIO()):
            paths = fetch.ambientcg_material("Rock030", "1K", cache_dir=self.cache)
        self.assertEqual(set(paths), {"color", "normal_gl", "roughness", "ao"})
        self.assertEqual(paths["normal_gl"].read_bytes(), b"Rock030_1K-PNG_NormalGL.png")
        self.assertFalse((self.root / "escape.txt").exists())
        self.assertFalse((self.cache / "escape.txt").exists())

    def test_ambientcg_falls_back_to_the_directx_normal(self):
        self._make_ambientcg(["Rock030_1K-PNG_Color.png", "Rock030_1K-PNG_NormalDX.png"])
        with contextlib.redirect_stdout(io.StringIO()):
            paths = fetch.ambientcg_material("Rock030", "1K", cache_dir=self.cache)
        self.assertEqual(set(paths), {"color", "normal_dx"})
        for path in paths.values():
            self.assertTrue(os.path.isfile(path))


if __name__ == "__main__":
    unittest.main()
