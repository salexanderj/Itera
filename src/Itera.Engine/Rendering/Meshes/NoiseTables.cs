namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The plane value noise's lattices that every world pixel reads, as tables: a layer each of an R16 array
/// <see cref="Cells"/> texels a side, binding 18 of the mesh set (<c>mesh-data.glsl</c>'s <c>noiseTables</c>). Texel
/// (x, z) holds <c>surface-noise.glsl</c>'s <c>latticeHash</c> of the lattice cell whose low bits those are, the cells
/// from -<see cref="Cells"/>/2 to <see cref="Cells"/>/2 - 1 on each axis, so <c>mesh-surface.glsl</c>'s
/// <c>planeNoise</c> gathers a cell's four corners in one read where <c>valueNoise2</c> hashed four times, and gives the
/// same noise over those cells (at 20 m cells, 10.5 km each way from the map's middle); past them the lattice repeats.
/// </summary>
internal static class NoiseTables
{
    /// <summary>Texels along each side of a layer.</summary>
    public const int Cells = 1024;

    /// <summary>The salt of each layer, in order (<c>mesh-data.glsl</c>'s <c>Noise</c> constants).</summary>
    public static ReadOnlySpan<uint> Salts => s_salts;

    private static readonly uint[] s_salts =
    [
        // The surfaces' patches over about 10 m (surfaceFinish).
        1,
        // The strata's wander over about 20 m (strataBands).
        4,
        // The look's hue drift over about 20 m.
        41,
    ];

    /// <summary>Bytes of every layer, two a texel.</summary>
    public const int Bytes = 2 * Cells * Cells * 3;

    /// <summary>
    /// Writes every layer into <paramref name="texels"/> (<see cref="Bytes"/> long), layer after layer, rows of x along
    /// z, each texel its hash in 16 bits.
    /// </summary>
    public static void Write(Span<byte> texels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(texels.Length, Bytes);
        var at = 0;
        foreach (var salt in s_salts)
        {
            for (var z = 0; z < Cells; z++)
            {
                for (var x = 0; x < Cells; x++)
                {
                    var value = (ushort)Math.Round(LatticeHash(CellOf(x), 0, CellOf(z), salt) * ushort.MaxValue);
                    texels[at++] = (byte)value;
                    texels[at++] = (byte)(value >> 8);
                }
            }
        }
    }

    /// <summary>The lattice cell texel <paramref name="texel"/> holds: its low bits as a signed cell round 0.</summary>
    public static int CellOf(int texel) => texel < Cells / 2 ? texel : texel - Cells;

    /// <summary>
    /// <c>surface-noise.glsl</c>'s <c>latticeHash</c>: a hash of a lattice point and a salt, 0 to 1, from its top 24 bits.
    /// </summary>
    public static float LatticeHash(int x, int y, int z, uint salt)
    {
        var h = unchecked(((uint)x * 0x8DA6B343u) ^ ((uint)y * 0xD8163841u) ^ ((uint)z * 0xCB1AB31Fu) ^ salt);
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        return (h >> 8) * (1f / 16777216f);
    }
}
