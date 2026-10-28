"""MakeHuman's human body for recipes: its base mesh, its targets, and its game-engine rig's joints and weights.

Run inside Blender. The data is MakeHuman's (CC0), read straight from the MPFB2 repository at a pinned commit through
the recipe's fetch, so no MPFB2 code runs. The base mesh (hm08) holds the body and helper geometry: the eyes, the
joint cubes that place the rig's joints, and others. A target is a list of offsets of some of its vertices; a body is
the base mesh with its targets added, each by a weight. The macro sliders (gender, age, muscle, weight, height,
proportions, breasts) each blend a few of MakeHuman's own targets (`macro_stack`).

A recipe shapes a `Body`, finds its joints (`Body.joints`), works out the turns that pose it into its game's rig's
rest (`fit`), moves the body with them (`posed`), and builds its mesh (`Body.mesh`). It moves the rig's joints to the
body's own places with `rig.move_joints`, keeping their turns, so the engine plays the rig's clips on it.

MakeHuman's axes: +Y up, the body's front +Z, its left +X, in decimetres. Blender's: +Z up, front -Y, left +X, metres.
"""

from __future__ import annotations

import gzip
import json
import math

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

#: The commit of the MPFB2 repository the files are read from.
COMMIT = "6ff1eb358f6c1959ced77e79e6dd2406d9a71f49"
#: Where its data files lie, by commit.
DATA = "https://raw.githubusercontent.com/makehumancommunity/mpfb2/{commit}/src/mpfb/data/"
#: Metres a unit of MakeHuman's.
METRES = 0.1
#: The macro sliders and their middle values, MakeHuman's average body.
MACROS = {
    "gender": 0.5,
    "age": 0.5,
    "muscle": 0.5,
    "weight": 0.5,
    "proportions": 0.5,
    "height": 0.5,
    "cupsize": 0.5,
    "firmness": 0.5,
    "race": {"african": 1 / 3, "asian": 1 / 3, "caucasian": 1 / 3},
}


def fetch(ctx, path: str, hashes=None, commit: str = COMMIT):
    """Fetches one of MPFB2's data files (`path` under its data folder) into the cache and returns its path. `hashes`
    pins files by their paths; an unpinned one prints its hash to pin."""
    return ctx.fetch(
        DATA.format(commit=commit) + path,
        sha256=(hashes or {}).get(path),
        name="mpfb-" + path.replace("/", "__"),
    )


def read_obj(path) -> dict:
    """MakeHuman's base mesh: `coords` (an array of every vertex, helpers too, as the targets number them), `uvs`,
    `faces` (each a list of (vertex, uv) corners), and each group's `group_faces` and `group_vertices`."""
    coords, uvs, faces = [], [], []
    group = None
    group_faces, group_vertices = {}, {}
    with open(path, encoding="utf-8") as file:
        for line in file:
            if line.startswith("v "):
                coords.append([float(value) for value in line.split()[1:4]])
            elif line.startswith("vt "):
                uvs.append([float(value) for value in line.split()[1:3]])
            elif line.startswith("g "):
                group = line[2:].strip()
            elif line.startswith("f "):
                corners = [tuple(int(part) - 1 for part in token.split("/")[:2]) for token in line.split()[1:]]
                group_faces.setdefault(group, []).append(len(faces))
                group_vertices.setdefault(group, set()).update(corner[0] for corner in corners)
                faces.append(corners)
    return {
        "coords": np.array(coords, dtype=np.float64),
        "uvs": uvs,
        "faces": faces,
        "group_faces": group_faces,
        "group_vertices": group_vertices,
    }


def read_target(path) -> tuple[np.ndarray, np.ndarray]:
    """A target file (gzipped or not): the vertices it moves and their offsets. An empty one moves none."""
    opener = gzip.open if str(path).endswith(".gz") else open
    indices, offsets = [], []
    with opener(path, "rt", encoding="utf-8") as file:
        for line in file:
            if not line.strip() or line.startswith("#"):
                continue
            parts = line.split()
            indices.append(int(parts[0]))
            offsets.append([float(value) for value in parts[1:4]])
    return np.array(indices, dtype=np.int64), np.array(offsets, dtype=np.float64).reshape(-1, 3)


def _components(config: dict, macro: str, value: float) -> list[tuple[str, float]]:
    """The targets one macro slider blends at `value`, each with its share (MPFB2's
    TargetService._interpolate_macro_components)."""
    components = []
    for part in config["macrotargets"][macro]["parts"]:
        lowest, highest = part["lowest"], part["highest"]
        if lowest < value < highest:
            share = (value - lowest) / (highest - lowest)
            if part["low"]:
                components.append((part["low"], round(1 - share, 4)))
            if part["high"]:
                components.append((part["high"], round(share, 4)))
    return components


def macro_stack(config: dict, macros: dict, cutoff: float = 0.01) -> list[tuple[str, float]]:
    """The targets the macro sliders blend, each a path under the targets folder (no extension) with its weight, as
    MPFB2's TargetService.calculate_target_stack_from_macro_info_dict has them. `config` is macrodetails/macro.json;
    `macros` gives each slider of `MACROS`. Targets of a weight at or under `cutoff` are left out."""
    names = ("gender", "age", "muscle", "weight", "proportions", "height", "cupsize", "firmness")
    c = {name: _components(config, name, macros[name]) for name in names}
    stack = []
    for race, share in macros["race"].items():
        if share > 0.0001:
            for age, age_share in c["age"]:
                for gender, gender_share in c["gender"]:
                    weight = share * gender_share * age_share
                    if weight > cutoff:
                        stack.append((f"macrodetails/{race}-{gender}-{age}", weight))
    for gender, gender_share in c["gender"]:
        for age, age_share in c["age"]:
            for muscle, muscle_share in c["muscle"]:
                for mass, mass_share in c["weight"]:
                    base = gender_share * age_share * muscle_share * mass_share
                    if base > cutoff:
                        stack.append((f"macrodetails/universal-{gender}-{age}-{muscle}-{mass}", base))
    for gender, gender_share in c["gender"]:
        for age, age_share in c["age"]:
            for muscle, muscle_share in c["muscle"]:
                for mass, mass_share in c["weight"]:
                    base = gender_share * age_share * muscle_share * mass_share
                    for height, height_share in c["height"]:
                        if base * height_share > cutoff:
                            stack.append(
                                (f"macrodetails/height/{gender}-{age}-{muscle}-{mass}-{height}", base * height_share)
                            )
    # The breasts': a female body's alone, with no share for the gender, and none for an average cup and firmness.
    for gender, _ in c["gender"]:
        if gender != "female":
            continue
        for age, age_share in c["age"]:
            for muscle, muscle_share in c["muscle"]:
                for mass, mass_share in c["weight"]:
                    for cup, cup_share in c["cupsize"]:
                        for firmness, firmness_share in c["firmness"]:
                            weight = age_share * muscle_share * mass_share * cup_share * firmness_share
                            name = f"breast/{gender}-{age}-{muscle}-{mass}-{cup}-{firmness}"
                            if weight > cutoff and "averagecup-averagefirmness" not in name and age != "baby":
                                stack.append((name, weight))
    for gender, gender_share in c["gender"]:
        for age, age_share in c["age"]:
            for muscle, muscle_share in c["muscle"]:
                for mass, mass_share in c["weight"]:
                    base = gender_share * age_share * muscle_share * mass_share
                    for proportions, proportions_share in c["proportions"]:
                        if base * proportions_share > cutoff and age != "baby":
                            stack.append(
                                (
                                    f"macrodetails/proportions/{gender}-{age}-{muscle}-{mass}-{proportions}",
                                    base * proportions_share,
                                )
                            )
    return stack


def to_blender(coords: np.ndarray) -> np.ndarray:
    """MakeHuman's coordinates in Blender's axes and metres."""
    return np.stack([coords[:, 0], -coords[:, 2], coords[:, 1]], axis=1) * METRES


class Body:
    """MakeHuman's base mesh, its rig's joints and weights, and the targets added to it.

    `rig` names one of MPFB2's standard rigs (`game_engine`: the Unreal mannequin's joint names). `hashes` pins the
    fetched files by their paths under MPFB2's data folder.
    """

    def __init__(self, ctx, hashes=None, rig: str = "game_engine", commit: str = COMMIT):
        self._ctx = ctx
        self._hashes = hashes or {}
        self._commit = commit
        self.mesh_data = read_obj(self._fetch("3dobjs/base.obj"))
        self.macro_config = json.loads(self._fetch("targets/macrodetails/macro.json").read_text(encoding="utf-8"))
        self.rig = json.loads(self._fetch(f"rigs/standard/rig.{rig}.json").read_text(encoding="utf-8"))
        weights = json.loads(self._fetch(f"rigs/standard/weights.{rig}.json").read_text(encoding="utf-8"))
        #: Each bone's weights, as (vertex, weight) pairs.
        self.weights = {bone: [(int(v), float(w)) for v, w in pairs] for bone, pairs in weights["weights"].items()}
        #: Each bone's parent (None for the root).
        self.parents = {bone: info["parent"] or None for bone, info in self.rig.items()}
        #: Every vertex in MakeHuman's axes and units, with the targets added so far.
        self.coords = self.mesh_data["coords"].copy()

    def _fetch(self, path: str):
        return fetch(self._ctx, path, self._hashes, self._commit)

    def shape(self, macros: dict, targets: dict | None = None) -> int:
        """Adds the targets the macro sliders blend (`macro_stack`; a slider not given takes `MACROS`') and `targets` (a
        path under the targets folder, no extension, to its weight). Returns how many targets were added."""
        stack = macro_stack(self.macro_config, {**MACROS, **macros})
        stack += list((targets or {}).items())
        for name, weight in stack:
            indices, offsets = read_target(self._fetch(f"targets/{name}.target.gz"))
            if len(indices):
                self.coords[indices] += weight * offsets
        return len(stack)

    def blender_coords(self) -> np.ndarray:
        """Every vertex in Blender's axes and metres, the body's lowest vertex on the ground (z = 0)."""
        coords = to_blender(self.coords)
        coords[:, 2] -= coords[sorted(self.mesh_data["group_vertices"]["body"]), 2].min()
        return coords

    def height(self, coords: np.ndarray) -> float:
        """The body's height in `coords` (Blender's), from its lowest vertex to its highest."""
        body = coords[sorted(self.mesh_data["group_vertices"]["body"])]
        return float(body[:, 2].max() - body[:, 2].min())

    def joints(self, coords: np.ndarray) -> tuple[dict, dict]:
        """Each bone's head and tail in `coords`: the middle of the joint cube (or the vertices) the rig names."""
        groups = self.mesh_data["group_vertices"]

        def place(end):
            vertices = sorted(groups[end["cube_name"]]) if end["strategy"] == "CUBE" else list(end["vertex_indices"])
            return Vector(coords[vertices].mean(axis=0))

        heads = {bone: place(info["head"]) for bone, info in self.rig.items()}
        tails = {bone: place(info["tail"]) for bone, info in self.rig.items()}
        return heads, tails

    def mesh(
        self,
        name: str,
        coords: np.ndarray,
        groups=("body",),
        rename=None,
        bones=None,
        marks=(),
    ) -> bpy.types.Object:
        """A mesh object of the faces of `groups` at `coords`, with MakeHuman's UV map and each bone's weights as a
        vertex group (named by `rename`, and only those in `bones` when given). Each group of `marks` is a point float
        attribute of its name, 1 on its vertices. Smooth shaded, at the scene's origin."""
        data = self.mesh_data
        faces = [f for group in groups for f in data["group_faces"][group]]
        used = sorted({corner[0] for f in faces for corner in data["faces"][f]})
        index = {old: new for new, old in enumerate(used)}
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(
            [tuple(coords[v]) for v in used], [], [[index[corner[0]] for corner in data["faces"][f]] for f in faces]
        )
        uv = mesh.uv_layers.new(name="UVMap")
        loop = 0
        for f in faces:
            for corner in data["faces"][f]:
                uv.data[loop].uv = data["uvs"][corner[1]]
                loop += 1
        for group in marks:
            attribute = mesh.attributes.new(group, "FLOAT", "POINT")
            marked = data["group_vertices"][group]
            attribute.data.foreach_set("value", [1.0 if v in marked else 0.0 for v in used])
        mesh.validate()
        for polygon in mesh.polygons:
            polygon.use_smooth = True
        obj = bpy.data.objects.new(name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        rename = rename or {}
        for bone, pairs in self.weights.items():
            named = rename.get(bone, bone)
            kept = [(index[v], w) for v, w in pairs if v in index]
            if not kept or (bones is not None and named not in bones):
                continue
            group = obj.vertex_groups.new(name=named)
            for vertex, weight in kept:
                group.add([vertex], weight, "REPLACE")
        return obj


def fit(
    heads: dict,
    tails: dict,
    parents: dict,
    rest: dict,
    aims: dict,
    leaves: dict,
    still=(),
    twists=None,
    shares=None,
    lift: Vector | None = None,
) -> tuple[dict, dict]:
    """The turns that pose a body into a rig's rest, and where the body's joints then stand.

    `heads`, `tails`, and `parents` are the body's (`Body.joints`, `Body.parents`), renamed to the rig's joint names.
    `rest` gives each of the rig's joints' places at rest, which say only which way each bone points. A bone of `aims`
    points at the joint named there, on both; one of `leaves` points at its own tail, the rig's at the leaf joint named
    there. The root and each bone in `still` keep their places, moved by `lift` (the trunk: its spine and collarbones).
    Each other bone turns by its parent's turn, then by the least turn that lays it along the rig's, about its own joint
    where its parent put it. A bone below a still one starts afresh. A bone of `shares` takes only that share of its
    least turn: a thumb turned all the way folds the skin between it and the hand. A bone of `twists` then turns about
    its own line, to lay the line between its two named joints (a hand's first knuckles) along the rig's.

    Returns each bone's transform (a 4x4 matrix from the body's place to its posed one) and each joint's posed place,
    the leaves' too.
    """
    lift = lift if lift is not None else Vector()
    twists = twists or {}
    shares = shares or {}
    lifted = Matrix.Translation(lift)
    turns, transforms, places = {}, {}, {}

    def solve(name):
        if name in transforms:
            return
        parent = parents[name]
        if name in still or parent is None:
            transforms[name], turns[name] = lifted, Quaternion()
            places[name] = heads[name] + lift
            return
        solve(parent)
        parent_turn = Quaternion() if parent in still or parents[parent] is None else turns[parent]
        start = transforms[parent] @ heads[name]
        turn = parent_turn
        if name in aims or name in leaves:
            end = heads[aims[name]] if name in aims else tails[name]
            target = rest[aims[name]] if name in aims else rest[leaves[name]]
            least = (parent_turn @ (end - heads[name])).rotation_difference(target - rest[name])
            turn = Quaternion().slerp(least, shares.get(name, 1.0)) @ parent_turn
            if name in twists:
                turn = _twist(turn, rest[aims[name]] - rest[name], twists[name], heads, rest) @ turn
        turns[name] = turn
        linear = turn.to_matrix()
        transform = linear.to_4x4()
        transform.translation = start - linear @ heads[name]
        transforms[name] = transform
        places[name] = start
        if name in leaves:
            places[leaves[name]] = transform @ tails[name]

    for name in parents:
        solve(name)
    return transforms, places


def _twist(turn: Quaternion, line: Vector, pair: tuple, heads: dict, rest: dict) -> Quaternion:
    """The turn about `line` that lays the body's line between the joints of `pair`, turned by `turn`, along the
    rig's."""
    axis = line.normalized()
    first, second = pair
    ours = turn @ (heads[first] - heads[second])
    theirs = rest[first] - rest[second]
    ours = (ours - axis * ours.dot(axis)).normalized()
    theirs = (theirs - axis * theirs.dot(axis)).normalized()
    return Quaternion(axis, math.atan2(axis.dot(ours.cross(theirs)), ours.dot(theirs)))


def posed(coords: np.ndarray, weights: dict, transforms: dict, rename=None) -> np.ndarray:
    """`coords` moved by each bone's transform (`fit`), blended by the body's `weights`. A vertex that no bone weighs
    stays."""
    rename = rename or {}
    moved = np.zeros_like(coords)
    total = np.zeros(len(coords))
    for bone, pairs in weights.items():
        if not pairs:
            continue
        matrix = np.array(transforms[rename.get(bone, bone)])
        index = np.array([v for v, _ in pairs], dtype=np.int64)
        weight = np.array([w for _, w in pairs])
        np.add.at(moved, index, (coords[index] @ matrix[:3, :3].T + matrix[:3, 3]) * weight[:, None])
        np.add.at(total, index, weight)
    weighted = total > 0
    moved[weighted] /= total[weighted, None]
    moved[~weighted] = coords[~weighted]
    return moved
