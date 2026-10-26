"""Helpers for texture recipes: a square tile that repeats, measured in metres, with noise, cells, and the maps made
from a height.

Every operation wraps at the tile's edges, so a set made with them repeats without a seam. Arrays are indexed
[row, column]: a column runs along u and a row along v. On a wall v is the world's up, so row 0 is the bottom of the
tile, which is how the engine reads a set (the image's first row at the bottom of a wall's tile). Sizes are in metres,
so a recipe looks the same at any number of texels. numpy and Pillow are all it needs.
"""

import hashlib
import os
from dataclasses import dataclass

import numpy as np
from PIL import Image

F32 = np.float32


@dataclass
class Maps:
    """What a recipe makes: the colour in linear RGB (rows, columns, 3), the height in metres, and the occlusion from 0
    (shut in) to 1 (open), or None to make it from the height."""

    colour: np.ndarray
    height: np.ndarray
    occlusion: np.ndarray | None = None


@dataclass
class Cells:
    """A tile parted into cells. For each texel: the cell (index, 0 to count - 1), the distance in metres to the nearest
    border with another cell (edge), and its place in metres from the cell's middle (du along u, dv along v). The
    cells' middles are middle_u and middle_v."""

    index: np.ndarray
    edge: np.ndarray
    du: np.ndarray
    dv: np.ndarray
    count: int
    middle_u: np.ndarray
    middle_v: np.ndarray


class Tile:
    """A square tile metres wide, texels across, whose random numbers come from seed."""

    def __init__(self, metres, texels, seed=1):
        self.metres = float(metres)
        self.texels = int(texels)
        self.seed = int(seed)
        self.texel = self.metres / self.texels
        centres = ((np.arange(self.texels) + 0.5) * self.texel).astype(F32)
        self.u = centres[None, :]
        self.v = centres[:, None]
        self._fv = np.fft.fftfreq(self.texels, d=self.texel)[:, None]
        self._fu = np.fft.rfftfreq(self.texels, d=self.texel)[None, :]
        self._f2 = self._fu * self._fu + self._fv * self._fv

    def rng(self, name):
        """A random number generator of its own for name, so adding a feature to a recipe leaves the others alone."""
        digest = hashlib.sha256(f"{self.seed}:{name}".encode()).digest()
        return np.random.default_rng(int.from_bytes(digest[:8], "little"))

    def zeros(self):
        return np.zeros((self.texels, self.texels), F32)

    def wrap(self, d):
        """A difference in metres brought to the nearest copy of the tile: between minus and plus half its width."""
        return (d + self.metres / 2) % self.metres - self.metres / 2

    def blur(self, a, sigma):
        """A Gaussian blur of sigma metres."""
        if sigma <= 0:
            return a.astype(F32)
        kernel = np.exp(-2 * np.pi**2 * sigma**2 * self._f2)
        return np.fft.irfft2(np.fft.rfft2(a) * kernel, s=a.shape).astype(F32)

    def noise(self, wavelength, name, width=0.5, stretch=1.0, angle=0.0):
        """Noise with a standard deviation of 1 and features about wavelength metres across, stretch times longer along
        the direction angle radians from u towards v. width is the band's spread in octaves."""
        white = self.rng(name).standard_normal((self.texels, self.texels)).astype(F32)
        along = self._fu * np.cos(angle) + self._fv * np.sin(angle)
        across = self._fv * np.cos(angle) - self._fu * np.sin(angle)
        frequency = np.sqrt((along * stretch) ** 2 + across**2)
        with np.errstate(divide="ignore"):
            octave = np.log2(np.maximum(frequency, 1e-9) * wavelength)
        weight = np.exp(-0.5 * (octave / width) ** 2)
        weight[0, 0] = 0
        out = np.fft.irfft2(np.fft.rfft2(white) * weight, s=white.shape)
        std = out.std()
        return (out / std if std > 0 else out).astype(F32)

    def fractal(self, wavelength, name, octaves=4, gain=0.5, stretch=1.0):
        """Octaves of noise from wavelength down by halves, each gain times the one before, scaled to a standard
        deviation of 1."""
        total = self.zeros()
        amplitude = 1.0
        for octave in range(octaves):
            total += amplitude * self.noise(wavelength / 2**octave, f"{name}/{octave}", stretch=stretch)
            amplitude *= gain
        return (total / total.std()).astype(F32)

    def gradient(self, h):
        """The slope of h (metres over metres) along u and up v."""
        du = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) / (2 * self.texel)
        dv = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) / (2 * self.texel)
        return du.astype(F32), dv.astype(F32)

    def cavity(self, h, sigma):
        """How far h lies under its blur of sigma metres: deep in hollows, 0 on tops."""
        return np.maximum(self.blur(h, sigma) - h, 0).astype(F32)

    def convexity(self, h, sigma):
        """How far h stands over its blur of sigma metres: high on edges and ridges, 0 in hollows."""
        return np.maximum(h - self.blur(h, sigma), 0).astype(F32)

    def occlusion(self, h, near=0.01, far=0.05, near_strength=4.0, far_strength=1.5, floor=0.25):
        """Occlusion from how deep each texel lies in hollows near and far, in metres, from floor to 1."""
        shut = near_strength * self.cavity(h, near) + far_strength * self.cavity(h, far)
        return np.clip(1 - shut, floor, 1).astype(F32)

    def grid_cells(self, columns, rows, name, jitter=0.8, merge=0.0, row_jitter=None, warp=None):
        """Voronoi cells round one point in each of columns by rows boxes, each moved up to jitter of its box from the
        box's middle (row_jitter for v, jitter if None). Distances are measured in boxes, so cells are as long along u
        as the boxes are. With merge, that share of the cells joins the cell to its right into one (and chains). warp,
        a pair of arrays in metres (along u and v), moves each texel before it is placed, so the borders wander."""
        rng = self.rng(name)
        row_jitter = jitter if row_jitter is None else row_jitter
        width, height = self.metres / columns, self.metres / rows
        jitter_u = (rng.random((rows, columns)) - 0.5) * jitter
        jitter_v = (rng.random((rows, columns)) - 0.5) * row_jitter
        parent = list(range(rows * columns))

        def find(a):
            while parent[a] != a:
                parent[a] = parent[parent[a]]
                a = parent[a]
            return a

        joins = rng.random((rows, columns)) < merge
        for j in range(rows):
            for i in range(columns):
                if joins[j, i]:
                    parent[find(j * columns + i)] = find(j * columns + (i + 1) % columns)
        roots = np.array([find(k) for k in range(rows * columns)])
        _, group = np.unique(roots, return_inverse=True)
        group = group.reshape(rows, columns)

        # The texel's place in boxes, and its box.
        pu = np.broadcast_to(self.u, (self.texels, self.texels))
        pv = np.broadcast_to(self.v, (self.texels, self.texels))
        if warp is not None:
            pu = (pu + warp[0]) % self.metres
            pv = (pv + warp[1]) % self.metres
        bu = pu / width
        bv = pv / height
        iu = np.floor(bu).astype(np.int32)
        iv = np.floor(bv).astype(np.int32)
        offsets = [(a, b) for a in range(-2, 3) for b in range(-2, 3)]

        def candidate(a, b):
            cu = iu + a
            cv = iv + b
            wu = cu % columns
            wv = cv % rows
            pu = cu + 0.5 + jitter_u[wv, wu]
            pv = cv + 0.5 + jitter_v[wv, wu]
            return pu - bu, pv - bv, group[wv, wu], wu, wv, cu, cv

        best = np.full((self.texels, self.texels), np.inf, F32)
        near_u = np.zeros_like(best)
        near_v = np.zeros_like(best)
        near_group = np.zeros(best.shape, np.int64)
        for a, b in offsets:
            du, dv, g, _, _, _, _ = candidate(a, b)
            d2 = (du * du + dv * dv).astype(F32)
            closer = d2 < best
            best = np.where(closer, d2, best)
            near_u = np.where(closer, du, near_u)
            near_v = np.where(closer, dv, near_v)
            near_group = np.where(closer, g, near_group)

        edge = np.full_like(best, np.inf)
        for a, b in offsets:
            du, dv, g, _, _, _, _ = candidate(a, b)
            eu, ev = du - near_u, dv - near_v
            length = np.maximum(np.sqrt(eu * eu + ev * ev), 1e-6)
            across = (du * du + dv * dv - best) / (2 * length)
            # From boxes to metres along the border's normal.
            metres = across / np.maximum(np.sqrt((eu / length / width) ** 2 + (ev / length / height) ** 2), 1e-9)
            edge = np.where(g != near_group, np.minimum(edge, metres), edge)

        count = int(group.max()) + 1
        middle_u, middle_v = self._group_middles(group, jitter_u, jitter_v, width, height)
        index = near_group.astype(np.int32)
        du = self.wrap(pu - middle_u[index])
        dv = self.wrap(pv - middle_v[index])
        return Cells(index, edge.astype(F32), du.astype(F32), dv.astype(F32), count, middle_u, middle_v)

    def _group_middles(self, group, jitter_u, jitter_v, width, height):
        rows, columns = group.shape
        count = int(group.max()) + 1
        middle_u = np.zeros(count, F32)
        middle_v = np.zeros(count, F32)
        for g in range(count):
            members = np.argwhere(group == g)
            us = (members[:, 1] + 0.5 + jitter_u[members[:, 0], members[:, 1]]) * width
            vs = (members[:, 0] + 0.5 + jitter_v[members[:, 0], members[:, 1]]) * height
            middle_u[g] = (us[0] + self.wrap(us - us[0]).mean()) % self.metres
            middle_v[g] = (vs[0] + self.wrap(vs - vs[0]).mean()) % self.metres
        return middle_u, middle_v

    def rect_cells(self, rects):
        """Cells that are rectangles (u, v, width, height) in metres, which may run over the tile's edge and wrap. Each
        texel belongs to the last rectangle that covers it; the rectangles should cover the tile."""
        index = np.full((self.texels, self.texels), -1, np.int32)
        edge = self.zeros()
        du = self.zeros()
        dv = self.zeros()
        u = self.u[0]
        v = self.v[:, 0]
        for k, (ru, rv, rw, rh) in enumerate(rects):
            lu = (u - ru) % self.metres
            lv = (v - rv) % self.metres
            columns = lu < rw
            rows = np.nonzero(lv < rh)[0]
            if rows.size == 0 or not columns.any():
                continue
            block = np.ix_(rows, np.nonzero(columns)[0])
            bu = lu[columns][None, :]
            bv = lv[rows][:, None]
            index[block] = k
            edge[block] = np.minimum(np.minimum(bu, rw - bu), np.minimum(bv, rh - bv))
            du[block] = bu - rw / 2
            dv[block] = bv - rh / 2
        if (index < 0).any():
            raise ValueError("the rectangles leave part of the tile uncovered")
        middles = np.array([((ru + rw / 2) % self.metres, (rv + rh / 2) % self.metres) for ru, rv, rw, rh in rects])
        return Cells(index, edge, du, dv, len(rects), middles[:, 0].astype(F32), middles[:, 1].astype(F32))

    def split(self, name, least, most, snap=0.0, near_square=1.5):
        """The tile cut again and again into rectangles at least least metres on a side, until none is wider or taller
        than most. Each cut falls on a multiple of snap metres (none if 0) and goes across the longer side, or either
        way when the sides are within near_square of each other."""
        rng = self.rng(name)
        done = []
        todo = [(0.0, 0.0, self.metres, self.metres)]
        while todo:
            ru, rv, rw, rh = todo.pop()
            if rw < 2 * least and rh < 2 * least or (rw <= most and rh <= most and rng.random() < 0.35):
                done.append((ru, rv, rw, rh))
                continue
            along_u = rw >= rh if max(rw, rh) / min(rw, rh) > near_square else rng.random() < 0.5
            if along_u and rw < 2 * least:
                along_u = False
            if not along_u and rh < 2 * least:
                along_u = True
            side = rw if along_u else rh
            cut = rng.uniform(least, side - least)
            if snap > 0:
                cut = min(max(round(cut / snap) * snap, least), side - least)
            if along_u:
                todo += [(ru, rv, cut, rh), (ru + cut, rv, rw - cut, rh)]
            else:
                todo += [(ru, rv, rw, cut), (ru, rv + cut, rw, rh - cut)]
        return done

    def courses(self, name, least_height, most_height, least_length, most_length):
        """Rows of blocks, as rectangles: courses of random heights filling the tile up v, each of blocks of random
        lengths filling it along u, starting at a random place so the joints of one course miss the next's."""
        rng = self.rng(name)
        heights = _fill(rng, self.metres, least_height, most_height)
        rects = []
        v = 0.0
        for h in heights:
            start = rng.uniform(0, self.metres)
            u = start
            for length in _fill(rng, self.metres, least_length, most_length):
                rects.append((u % self.metres, v, length, h))
                u += length
            v += h
        return rects


def _fill(rng, total, least, most):
    """Random lengths between least and most that add up to total exactly (scaled a little to fit)."""
    lengths = []
    while sum(lengths) < total:
        lengths.append(rng.uniform(least, most))
    if len(lengths) > 1 and sum(lengths) - total > lengths[-1] / 2:
        lengths.pop()
    scale = total / sum(lengths)
    return [length * scale for length in lengths]


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return (t * t * (3 - 2 * t)).astype(F32)


def mix(a, b, t):
    """a to b by t, for plain arrays, or for colours (a or b ending in an axis of 3) by a t without that axis."""
    a = np.asarray(a, F32)
    b = np.asarray(b, F32)
    t = np.asarray(t, F32)
    if (a.shape[-1:] == (3,) or b.shape[-1:] == (3,)) and t.shape[-1:] != (3,):
        t = t[..., None]
    return (a + (b - a) * t).astype(F32)


def ramp(t, stops):
    """A colour ramp: t from 0 to 1 through stops, a list of (t, (r, g, b)) in linear RGB in rising t."""
    t = np.clip(np.asarray(t, F32), stops[0][0], stops[-1][0])
    out = np.zeros(t.shape + (3,), F32)
    for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
        inside = (t >= t0) & (t <= t1)
        share = np.clip((t - t0) / max(t1 - t0, 1e-9), 0, 1)
        out = np.where(inside[..., None], mix(c0, c1, share), out)
    return out


def srgb(linear):
    linear = np.clip(linear, 0, 1)
    return np.where(linear <= 0.0031308, linear * 12.92, 1.055 * linear ** (1 / 2.4) - 0.055)


def normal_map(tile, h):
    """The normal map of h in OpenGL's convention as the engine reads it: red along u, green along the image's upward
    direction, which is the rows' falling one, so green is the slope up the rows."""
    du, dv = tile.gradient(h)
    n = np.stack([-du, dv, np.ones_like(du)], -1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True)


def write(folder, tile, maps):
    """Writes colour.png, normal.png, occlusion.png, height.png, and metres.txt, as tools/Textures reads a bake. The
    height is written about its mean, its span stretched to the 8 bits (a few extreme texels clipped)."""
    os.makedirs(folder, exist_ok=True)
    h = maps.height.astype(F32)
    occlusion = tile.occlusion(h) if maps.occlusion is None else maps.occlusion

    def grey(values):
        b = (np.clip(values, 0, 1) * 255 + 0.5).astype(np.uint8)
        return np.stack([b, b, b], -1)

    Image.fromarray((srgb(maps.colour) * 255 + 0.5).astype(np.uint8)).save(os.path.join(folder, "colour.png"))
    n = normal_map(tile, h)
    Image.fromarray(np.clip(n * 127.5 + 127.5 + 0.5, 0, 255).astype(np.uint8)).save(os.path.join(folder, "normal.png"))
    Image.fromarray(grey(occlusion)).save(os.path.join(folder, "occlusion.png"))
    centred = h - h.mean()
    span = max(float(np.percentile(np.abs(centred), 99.5)), 1e-6)
    Image.fromarray(grey(0.5 + centred / (2 * span))).save(os.path.join(folder, "height.png"))
    with open(os.path.join(folder, "metres.txt"), "w", encoding="utf-8") as f:
        f.write(f"{tile.metres:g}")
