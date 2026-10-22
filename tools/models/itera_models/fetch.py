"""Downloads files for recipes into a cache, and reads texture sets from Poly Haven and ambientCG.

A recipe pins each download by its SHA-256 so that a model builds the same from the same files. A download
without a pin prints one line with the hash to copy into the recipe.

Both texture libraries are CC0. Poly Haven gives OpenGL normals (`nor_gl`), which glTF wants. ambientCG gives
DirectX normals unless the zip also holds `NormalGL`. `materials.pbr_from_maps` takes what these functions return
and flips a DirectX normal itself.

Nothing here needs Blender, so these functions also run in plain Python.
"""

from __future__ import annotations

import hashlib
import json
import os
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path

#: Poly Haven's file list for one asset. Tests point this at a `file://` folder.
POLYHAVEN_API = "https://api.polyhaven.com/files/{asset}"

#: ambientCG's download address; the file name follows it. Tests point this at a `file://` folder.
AMBIENTCG_DOWNLOAD = "https://ambientcg.com/get?file="

#: Poly Haven names its maps differently in the file list than in the file names. Keys are what recipes ask for.
POLYHAVEN_MAP_KEYS = {
    "diff": "Diffuse",
    "rough": "Rough",
    "ao": "AO",
    "disp": "Displacement",
    "nor_gl": "nor_gl",
    "nor_dx": "nor_dx",
    "arm": "arm",
    "metal": "Metal",
}

#: The file name ends of ambientCG's maps. A map's first suffix found in the zip is used.
AMBIENTCG_SUFFIXES = {
    "color": ("_Color.png",),
    "normal_gl": ("_NormalGL.png",),
    "normal_dx": ("_NormalDX.png",),
    "roughness": ("_Roughness.png",),
    "ao": ("_AmbientOcclusion.png",),
    "metal": ("_Metalness.png",),
}

_USER_AGENT = "itera-models/1.0 (+https://github.com/salexanderj/itera)"
_TIMEOUT_SECONDS = 120
_CHUNK = 1 << 20


def sha256_of(path: str | os.PathLike) -> str:
    """Returns the SHA-256 of a file as lowercase hex."""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        while chunk := handle.read(_CHUNK):
            digest.update(chunk)
    return digest.hexdigest()


def fetch(
    url: str,
    cache_dir: str | os.PathLike,
    sha256: str | None = None,
    name: str | None = None,
) -> tuple[Path, str]:
    """Downloads `url` into `cache_dir` once and returns `(path, sha256)`.

    A later call finds the file in the cache and does not touch the network. With `sha256` given, the file must
    match it: a file that does not is deleted and an error names both hashes. Without it, the hash is computed and
    printed so the recipe author can pin it.

    `name` is the file name in the cache. It defaults to the last part of the URL's path. Give a name when two
    URLs end in the same name. `file://` URLs work, which tests use.
    """
    cache = Path(cache_dir)
    cache.mkdir(parents=True, exist_ok=True)
    file_name = name or _name_from_url(url)
    target = cache / file_name

    if not target.exists():
        _download(url, target)

    digest = sha256_of(target)
    if sha256 is not None and digest != sha256.lower():
        target.unlink()
        raise ValueError(
            f"{file_name} from {url} has SHA-256 {digest}, but the recipe pins {sha256.lower()}. "
            "The file was removed from the cache."
        )
    if sha256 is None:
        print(f'fetch: {file_name} has SHA-256 {digest}; pin it with fetch(..., sha256="{digest}")')
    return target, digest


def polyhaven_texture(
    asset: str,
    resolution: str = "1k",
    maps: tuple[str, ...] = ("diff", "nor_gl", "rough", "ao"),
    *,
    cache_dir: str | os.PathLike,
    hashes: dict[str, str] | None = None,
) -> dict[str, Path]:
    """Downloads the PNG maps of a Poly Haven texture at one resolution and returns `{map: path}`.

    `maps` uses Poly Haven's file-name codes: `diff`, `nor_gl`, `nor_dx`, `rough`, `ao`, `disp`, `arm`, `metal`.
    `hashes` maps a map code to its pinned SHA-256; a map without a pin prints a line with its hash.
    """
    hashes = hashes or {}
    cache = Path(cache_dir)
    cache.mkdir(parents=True, exist_ok=True)
    listing_path = cache / f"{asset}_files.json"
    if not listing_path.exists():
        _download(POLYHAVEN_API.format(asset=asset), listing_path)
    listing = json.loads(listing_path.read_text(encoding="utf-8"))

    result: dict[str, Path] = {}
    for code in maps:
        key = POLYHAVEN_MAP_KEYS.get(code)
        if key is None:
            raise ValueError(f"Unknown Poly Haven map '{code}'. Known: {', '.join(sorted(POLYHAVEN_MAP_KEYS))}")
        try:
            url = listing[key][resolution]["png"]["url"]
        except KeyError:
            raise ValueError(f"Poly Haven asset '{asset}' has no PNG '{code}' map at {resolution}") from None
        path, _ = fetch(url, cache, sha256=hashes.get(code), name=f"{asset}_{resolution}_{code}.png")
        result[code] = path
    return result


def ambientcg_material(
    asset: str,
    resolution: str = "1K",
    *,
    cache_dir: str | os.PathLike,
    sha256: str | None = None,
) -> dict[str, Path]:
    """Downloads an ambientCG material's PNG zip, unpacks the maps, and returns `{map: path}`.

    Keys are `color`, `roughness`, `ao`, `metal` (each only when the zip has it), and the normal map as
    `normal_gl` when the zip has `NormalGL`, otherwise as `normal_dx`. Give `normal_directx=True` to
    `materials.pbr` when only `normal_dx` is present. `sha256` pins the zip.
    """
    cache = Path(cache_dir)
    zip_name = f"{asset}_{resolution}-PNG.zip"
    zip_path, _ = fetch(AMBIENTCG_DOWNLOAD + zip_name, cache, sha256=sha256, name=zip_name)
    folder = cache / f"{asset}_{resolution}"
    folder.mkdir(parents=True, exist_ok=True)

    found: dict[str, Path] = {}
    with zipfile.ZipFile(zip_path) as archive:
        for member in archive.namelist():
            base = os.path.basename(member)
            for code, suffixes in AMBIENTCG_SUFFIXES.items():
                if code not in found and base.endswith(suffixes):
                    found[code] = _extract(archive, member, folder / base)

    if "normal_gl" in found:
        found.pop("normal_dx", None)
    return found


def _name_from_url(url: str) -> str:
    name = os.path.basename(urllib.parse.urlparse(url).path)
    if not name:
        name = hashlib.sha256(url.encode("utf-8")).hexdigest()[:16]
    return name


def _download(url: str, target: Path) -> None:
    """Writes the URL's body to `target` through a temporary file, so a failed download is never kept."""
    request = urllib.request.Request(url, headers={"User-Agent": _USER_AGENT})
    partial = target.with_name(target.name + ".part")
    try:
        with urllib.request.urlopen(request, timeout=_TIMEOUT_SECONDS) as response, open(partial, "wb") as out:
            while chunk := response.read(_CHUNK):
                out.write(chunk)
        os.replace(partial, target)
    finally:
        if partial.exists():
            partial.unlink()


def _extract(archive: zipfile.ZipFile, member: str, target: Path) -> Path:
    """Writes one zip member to `target`. Only the member's content is used, never its path."""
    with archive.open(member) as source, open(target, "wb") as out:
        while chunk := source.read(_CHUNK):
            out.write(chunk)
    return target
