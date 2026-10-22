"""The Context a recipe's build(ctx) receives.

Run inside Blender. A Context holds where the recipe lives and where its output goes, collects the problems and notes
the recipe reports, and gives the recipe the export and the fetch helpers.
"""

from __future__ import annotations

from pathlib import Path


class Context:
    """What a recipe knows about its run, and what it can ask the toolkit to do."""

    def __init__(
        self,
        name: str,
        recipe_dir: str | Path,
        out_path: str | Path,
        work_dir: str | Path,
        cache_dir: str | Path,
    ) -> None:
        self.name = name
        self.recipe_dir = Path(recipe_dir)
        self.out_path = Path(out_path)
        self.work_dir = Path(work_dir)
        self.cache_dir = Path(cache_dir)
        self.problems: list[str] = []
        self.notes: list[str] = []
        # Set by export(): the settings of the last export, and the objects it was given (None: the default list).
        self.export_settings: dict | None = None
        self.export_objects = None

    def problem(self, text: str) -> None:
        """Reports a problem. The build ends with exit code 1 when any problem is reported."""
        self.problems.append(str(text))

    def note(self, text: str) -> None:
        """Reports a note. Notes are printed and kept in report.json; they do not fail the build."""
        self.notes.append(str(text))

    def fetch(self, url: str, sha256: str | None = None, name: str | None = None) -> Path:
        """Downloads a file into the cache's inputs folder (once) and returns its path. See fetch.py."""
        from .fetch import fetch

        path, digest = fetch(url, self.cache_dir, sha256=sha256, name=name)
        if sha256 is None:
            self.note(f"unpinned download {path.name}: pin it with sha256=\"{digest}\"")
        return path

    def polyhaven(self, asset: str, resolution: str = "1k", maps=("diff", "nor_gl", "rough", "ao"), hashes=None):
        """A Poly Haven texture's maps in the cache, as `{map: path}`. See fetch.polyhaven_texture."""
        from .fetch import polyhaven_texture, sha256_of

        found = polyhaven_texture(asset, resolution, tuple(maps), cache_dir=self.cache_dir, hashes=hashes)
        for code, path in found.items():
            if not (hashes or {}).get(code):
                self.note(f"unpinned download {path.name}: pin it in hashes as \"{code}\": \"{sha256_of(path)}\"")
        return found

    def ambientcg(self, asset: str, resolution: str = "1K", sha256: str | None = None):
        """An ambientCG material's maps in the cache, as `{map: path}`. See fetch.ambientcg_material."""
        from .fetch import ambientcg_material, sha256_of

        found = ambientcg_material(asset, resolution, cache_dir=self.cache_dir, sha256=sha256)
        if sha256 is None:
            archive = self.cache_dir / f"{asset}_{resolution}-PNG.zip"
            self.note(f"unpinned download {archive.name}: pin it with sha256=\"{sha256_of(archive)}\"")
        return found

    def export(self, objects=None, clips=None, vertex_colors: bool = False) -> dict:
        """Exports the model to out_path as a .glb. See export.glb for the arguments. Returns the settings used."""
        from . import export

        settings = export.glb(self.out_path, objects=objects, clips=clips, vertex_colors=vertex_colors)
        self.export_settings = settings
        self.export_objects = objects
        return settings
