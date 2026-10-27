namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The clouds' noise as a tileable texture (binding 19 of the mesh set, <c>mesh-data.glsl</c>'s <c>cloudNoise</c>; read
/// by <c>clouds.glsl</c>): RGBA8, <see cref="Texels"/> a side, with every mip level, made on the CPU when the pass starts.
/// Red is gradient noise, three octaves at 4, 8, and 16 cells a tile, for the clouds' soft shape. Green, blue, and alpha
/// are cellular (Worley) noise turned inside out, high at each cell's point and low between, each three octaves, from 4,
/// 8, and 16 cells a tile: puffs that rounded clouds are made of, and the finer ones eat their edges. Every octave's
/// lattice repeats at the tile, so the texture tiles.
/// </summary>
internal static class CloudNoise
{
    /// <summary>Texels along each side of the largest level.</summary>
    public const int Texels = 256;

    /// <summary>The mip levels, down to one texel.</summary>
    public const int Mips = 9;

    /// <summary>Bytes of every level, four a texel, level after level.</summary>
    public static int Bytes { get; } = BytesOf(Mips);

    /// <summary>The byte offset of level <paramref name="mip"/> in what <see cref="Write"/> writes.</summary>
    public static int OffsetOf(int mip) => BytesOf(mip);

    /// <summary>The side of level <paramref name="mip"/> in texels.</summary>
    public static int SideOf(int mip) => Math.Max(1, Texels >> mip);

    /// <summary>
    /// Writes every level into <paramref name="texels"/> (<see cref="Bytes"/> long): the largest made from the noise, row
    /// after row, each texel red, green, blue, alpha; each smaller the mean of the four texels under it.
    /// </summary>
    public static void Write(Span<byte> texels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(texels.Length, Bytes);
        var level = new float[Texels * Texels * 4];
        Parallel.For(0, Texels, z => WriteRow(level, z));
        for (var mip = 0; mip < Mips; mip++)
        {
            var side = SideOf(mip);
            var into = texels.Slice(OffsetOf(mip), side * side * 4);
            for (var i = 0; i < into.Length; i++)
            {
                into[i] = (byte)Math.Round(Math.Clamp(level[i], 0, 1) * 255);
            }

            if (mip + 1 < Mips)
            {
                level = Halve(level, side);
            }
        }
    }

    /// <summary>The largest level's row <paramref name="z"/>, each texel's four channels, 0 to 1.</summary>
    private static void WriteRow(float[] level, int z)
    {
        Span<float> cells = stackalloc float[5];
        for (var x = 0; x < Texels; x++)
        {
            var u = (x + 0.5) / Texels;
            var v = (z + 0.5) / Texels;
            for (var octave = 0; octave < cells.Length; octave++)
            {
                cells[octave] = (float)(1 - Worley(u, v, 4 << octave, 101 + (uint)octave));
            }

            var gradient = 0.5 * Gradient(u, v, 4, 11) + 0.25 * Gradient(u, v, 8, 12) + 0.125 * Gradient(u, v, 16, 13);
            var at = (z * Texels + x) * 4;
            // Gradient noise peaks near 0.7 either way: three octaves summed to 0.875 at most, stretched to 0 to 1.
            level[at] = (float)(0.5 + gradient / 0.875 * 0.75);
            level[at + 1] = (float)(0.625 * cells[0] + 0.25 * cells[1] + 0.125 * cells[2]);
            level[at + 2] = (float)(0.625 * cells[1] + 0.25 * cells[2] + 0.125 * cells[3]);
            level[at + 3] = (float)(0.625 * cells[2] + 0.25 * cells[3] + 0.125 * cells[4]);
        }
    }

    /// <summary>The level half as wide as <paramref name="level"/> (<paramref name="side"/> texels a side): each texel the mean of four.</summary>
    private static float[] Halve(float[] level, int side)
    {
        var half = Math.Max(1, side / 2);
        var smaller = new float[half * half * 4];
        for (var z = 0; z < half; z++)
        {
            for (var x = 0; x < half; x++)
            {
                for (var channel = 0; channel < 4; channel++)
                {
                    var a = level[((2 * z) * side + 2 * x) * 4 + channel];
                    var b = level[((2 * z) * side + 2 * x + 1) * 4 + channel];
                    var c = level[((2 * z + 1) * side + 2 * x) * 4 + channel];
                    var d = level[((2 * z + 1) * side + 2 * x + 1) * 4 + channel];
                    smaller[(z * half + x) * 4 + channel] = (a + b + c + d) * 0.25f;
                }
            }
        }

        return smaller;
    }

    /// <summary>
    /// The distance from (<paramref name="u"/>, <paramref name="v"/>) in the tile to the nearest of one point a cell, the
    /// cells <paramref name="cells"/> a side and wrapping at the tile, in cells, at most 1.
    /// </summary>
    private static double Worley(double u, double v, int cells, uint salt)
    {
        var px = u * cells;
        var pz = v * cells;
        var cx = (int)Math.Floor(px);
        var cz = (int)Math.Floor(pz);
        var nearest = double.MaxValue;
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var x = cx + dx;
                var z = cz + dz;
                var wx = Wrap(x, cells);
                var wz = Wrap(z, cells);
                var fx = x + NoiseTables.LatticeHash(wx, 0, wz, salt) - px;
                var fz = z + NoiseTables.LatticeHash(wx, 1, wz, salt) - pz;
                nearest = Math.Min(nearest, fx * fx + fz * fz);
            }
        }

        return Math.Min(Math.Sqrt(nearest), 1);
    }

    /// <summary>
    /// Gradient noise at (<paramref name="u"/>, <paramref name="v"/>) in the tile, on a lattice <paramref name="cells"/> a
    /// side that wraps at the tile, from about -0.7 to 0.7: each corner's gradient a hash's angle, blended by a quintic.
    /// </summary>
    private static double Gradient(double u, double v, int cells, uint salt)
    {
        var px = u * cells;
        var pz = v * cells;
        var cx = (int)Math.Floor(px);
        var cz = (int)Math.Floor(pz);
        var fx = px - cx;
        var fz = pz - cz;
        var a = Corner(cx, cz, fx, fz, cells, salt);
        var b = Corner(cx + 1, cz, fx - 1, fz, cells, salt);
        var c = Corner(cx, cz + 1, fx, fz - 1, cells, salt);
        var d = Corner(cx + 1, cz + 1, fx - 1, fz - 1, cells, salt);
        var sx = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
        var sz = fz * fz * fz * (fz * (fz * 6 - 15) + 10);
        return a + (b - a) * sx + (c - a) * sz + (a - b - c + d) * sx * sz;
    }

    // The gradient of corner (x, z), wrapped at the tile, dotted with the offset (fx, fz) from it.
    private static double Corner(int x, int z, double fx, double fz, int cells, uint salt)
    {
        var angle = NoiseTables.LatticeHash(Wrap(x, cells), 0, Wrap(z, cells), salt) * (2 * Math.PI);
        return Math.Cos(angle) * fx + Math.Sin(angle) * fz;
    }

    private static int Wrap(int cell, int cells) => ((cell % cells) + cells) % cells;

    private static int BytesOf(int mips)
    {
        var bytes = 0;
        for (var mip = 0; mip < mips; mip++)
        {
            var side = SideOf(mip);
            bytes += side * side * 4;
        }

        return bytes;
    }
}
