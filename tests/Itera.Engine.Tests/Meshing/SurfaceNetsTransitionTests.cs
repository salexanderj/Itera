using System.Numerics;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Meshing;

/// <summary>
/// <see cref="SurfaceNets.MeshTransition"/>: fine blocks beside coarser ones, both sampled from one smooth shape at
/// their own voxels' centres as the world's levels are, meet with at most a quarter of the open edges (edges where they
/// meet that belong to one triangle only) that plain blocks leave, along a side and at a corner, with the coarser
/// columns past the fine ones' high faces or their low faces. Plain blocks leave a
/// hundred or more along a seam (the slits the sky shows through); the levels' samples disagree in a few places on which
/// cells the surface crosses, which leaves a few holes of a voxel or two.
/// </summary>
public sealed class SurfaceNetsTransitionTests(ITestOutputHelper output)
{
    private const int Block = SurfaceNets.BlockSize;

    public static TheoryData<int> Seeds => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fine_blocks_beside_a_coarser_column_meet_it_without_gaps(int seed)
    {
        var shape = new Shape(seed);
        var plain = OpenEdges(shape, transition: false, corner: false, low: false);
        var stitched = OpenEdges(shape, transition: true, corner: false, low: false);
        output.WriteLine($"seed {seed}: {stitched} open edges stitched, {plain} plain");
        Assert.True(plain > 50, $"The plain meshes left only {plain} open edges: the test does not see the seam.");
        // Measured 2026-10-08: 5 to 27 stitched against 104 to 268 plain over the eight shapes.
        Assert.True(stitched * 4 <= plain && stitched <= 30, $"{stitched} open edges stitched, {plain} plain.");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fine_blocks_at_a_corner_of_coarser_columns_meet_them_without_gaps(int seed)
    {
        var shape = new Shape(seed);
        var stitched = OpenEdges(shape, transition: true, corner: true, low: false);
        var plain = OpenEdges(shape, transition: false, corner: true, low: false);
        output.WriteLine($"seed {seed}: {stitched} open edges stitched at the corner, {plain} plain");
        Assert.True(plain > 50, $"The plain meshes left only {plain} open edges: the test does not see the seam.");
        // Measured 2026-10-08: 8 to 29 stitched over the eight shapes.
        Assert.True(stitched * 4 <= plain && stitched <= 35, $"{stitched} open edges stitched, {plain} plain.");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fine_blocks_past_a_coarser_column_meet_it_without_gaps(int seed)
    {
        var shape = new Shape(seed);
        var plain = OpenEdges(shape, transition: false, corner: false, low: true);
        var stitched = OpenEdges(shape, transition: true, corner: false, low: true);
        output.WriteLine($"seed {seed}: {stitched} open edges stitched, {plain} plain");
        Assert.True(plain > 50, $"The plain meshes left only {plain} open edges: the test does not see the seam.");
        // Measured 2026-10-08: 11 to 40 stitched against the same 104 to 268 plain.
        Assert.True(stitched * 4 <= plain && stitched <= 45, $"{stitched} open edges stitched, {plain} plain.");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fine_blocks_past_a_corner_of_coarser_columns_meet_them_without_gaps(int seed)
    {
        var shape = new Shape(seed);
        var stitched = OpenEdges(shape, transition: true, corner: true, low: true);
        var plain = OpenEdges(shape, transition: false, corner: true, low: true);
        output.WriteLine($"seed {seed}: {stitched} open edges stitched at the corner, {plain} plain");
        Assert.True(plain > 50, $"The plain meshes left only {plain} open edges: the test does not see the seam.");
        // Measured 2026-10-08: 10 to 34 stitched against 183 to 290 plain.
        Assert.True(stitched * 4 <= plain && stitched <= 40, $"{stitched} open edges stitched, {plain} plain.");
    }

    /// <summary>
    /// Noise in both blocks, at each of the eight places a block takes in the coarser one and with every side and corner
    /// beside a coarser column: every cell the snapping reaches for lies where the coarser block's samples can make its
    /// vertex (the mesher throws for any other; the fallback once looked one cell past the coarser block's last).
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Noise_at_every_place_and_side_meshes_without_fault(int seed)
    {
        var random = new Random(seed);
        var mesher = new SurfaceNets();
        var buffers = new MeshBuffers();
        var fine = Noise(random);
        var coarse = Noise(random);
        const TransitionSides All = (TransitionSides)0xFF;
        for (var place = 0; place < 8; place++)
        {
            var offset = new VoxelCoord((place & 1) * 16, ((place >> 1) & 1) * 16, (place >> 2) * 16);
            var triangles = mesher.MeshTransition(
                fine.Samples,
                fine.Densities,
                coarse.Samples,
                coarse.Densities,
                offset,
                All,
                buffers
            );
            Assert.True(triangles > 0);
        }
    }

    /// <summary>Samples of air and one material at random, and densities on their sides of one half.</summary>
    private static (byte[] Samples, byte[] Densities) Noise(Random random)
    {
        var samples = new byte[SurfaceNets.SampleCount];
        var densities = new byte[SurfaceNets.SampleCount];
        for (var i = 0; i < samples.Length; i++)
        {
            var solid = random.Next(3) == 0;
            samples[i] = solid ? (byte)1 : (byte)0;
            densities[i] = solid ? (byte)random.Next(128, 256) : (byte)random.Next(0, 128);
        }

        return (samples, densities);
    }

    /// <summary>
    /// Meshes the fine half of a coarse column region (fine voxels, x 32 to 64) beside the coarser column past x = 64,
    /// and with <paramref name="corner"/> also past z = 64 and at the corner, and counts the edges near x = 64 (and
    /// z = 64) used by one triangle only. With <paramref name="low"/> the region is mirrored: the fine blocks are those
    /// of x 64 to 96 (and z 64 to 96) and the coarser columns lie before x = 64 (and z = 64). Positions are in fine
    /// voxels.
    /// </summary>
    private static int OpenEdges(Shape shape, bool transition, bool corner, bool low)
    {
        var triangles = new List<(Vector3, Vector3, Vector3)>();
        var mesher = new SurfaceNets();
        var buffers = new MeshBuffers();
        // The coarse block that holds the fine blocks, at the origin or, mirrored, at x and z 64.
        var parent = low ? new Vector3(2 * Block, 0, corner ? 2 * Block : 0) : Vector3.Zero;
        var coarse = Read(shape, parent, 2);
        var coarseDensities = coarse.Densities;
        // The fine blocks: those of the coarse block's half at the seam, and with a corner the other half's blocks at
        // the second seam.
        for (var bz = 0; bz < 2; bz++)
        {
            for (var by = 0; by < 2; by++)
            {
                for (var bx = 0; bx < 2; bx++)
                {
                    var origin = parent + new Vector3(Block * bx, Block * by, Block * bz);
                    var atX = low ? bx == 0 : bx == 1;
                    var atZ = low ? bz == 0 : bz == 1;
                    var sides = TransitionSides.None;
                    if (atX)
                    {
                        sides |= low ? TransitionSides.LowX : TransitionSides.HighX;
                    }

                    if (corner && atZ)
                    {
                        sides |= low ? TransitionSides.LowZ : TransitionSides.HighZ;
                        if (atX)
                        {
                            sides |= low ? TransitionSides.LowXLowZ : TransitionSides.HighXHighZ;
                        }
                    }

                    if (sides == TransitionSides.None)
                    {
                        continue;
                    }

                    var fine = Read(shape, origin, 1);
                    if (transition)
                    {
                        mesher.MeshTransition(
                            fine.Samples,
                            fine.Densities,
                            coarse.Samples,
                            coarseDensities,
                            new VoxelCoord(16 * bx, 16 * by, 16 * bz),
                            sides,
                            buffers
                        );
                    }
                    else
                    {
                        mesher.Mesh(fine.Samples, fine.Densities, buffers);
                    }

                    Collect(buffers, origin, 1, triangles);
                }
            }
        }

        // The coarser columns: past x = 64, and with a corner past z = 64 and at the corner (mirrored, before them).
        Vector3[] coarseOrigins = (corner, low) switch
        {
            (false, false) => [new(2 * Block, 0, 0)],
            (true, false) => [new(2 * Block, 0, 0), new(0, 0, 2 * Block), new(2 * Block, 0, 2 * Block)],
            (false, true) => [Vector3.Zero],
            (true, true) => [new(0, 0, 2 * Block), new(2 * Block, 0, 0), Vector3.Zero],
        };
        foreach (var origin in coarseOrigins)
        {
            var block = Read(shape, origin, 2);
            mesher.Mesh(block.Samples, block.Densities, buffers);
            Collect(buffers, origin, 2, triangles);
        }

        return CountOpen(triangles, corner, low);
    }

    /// <summary>Edges used by one triangle only, near the seam and away from the meshed region's outer bounds.</summary>
    private static int CountOpen(List<(Vector3 A, Vector3 B, Vector3 C)> triangles, bool corner, bool low)
    {
        var uses = new Dictionary<(Key, Key), int>();
        foreach (var (a, b, c) in triangles)
        {
            Use(uses, a, b);
            Use(uses, b, c);
            Use(uses, c, a);
        }

        var open = 0;
        foreach (var ((p, q), count) in uses)
        {
            if (count == 1 && Near(p, corner, low) && Near(q, corner, low))
            {
                open++;
            }
        }

        return open;
    }

    private static bool Near(Key key, bool corner, bool low)
    {
        var p = key.Position;
        var inside = p.Y > 8 && p.Y < 56;
        if (!corner)
        {
            return inside && p.X > 56 && p.X < 72 && p.Z > 8 && p.Z < 56;
        }

        if (low)
        {
            // Mirrored: round the corner at (64, 64) and along both seams past it.
            return inside
                && ((p.X > 56 && p.X < 72 && p.Z > 56 && p.Z < 88) || (p.Z > 56 && p.Z < 72 && p.X > 56 && p.X < 120));
        }

        // Round the corner at (64, 64), where the four columns meet, and along both seams near it.
        return inside
            && ((p.X > 56 && p.X < 72 && p.Z > 40 && p.Z < 72) || (p.Z > 56 && p.Z < 72 && p.X > 8 && p.X < 72));
    }

    private static void Use(Dictionary<(Key, Key), int> uses, Vector3 a, Vector3 b)
    {
        Key ka = new(a),
            kb = new(b);
        var edge = ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka);
        uses[edge] = uses.GetValueOrDefault(edge) + 1;
    }

    private static void Collect(
        MeshBuffers buffers,
        Vector3 origin,
        float voxel,
        List<(Vector3, Vector3, Vector3)> triangles
    )
    {
        for (var i = 0; i < buffers.IndexCount; i += 3)
        {
            triangles.Add(
                (
                    origin + MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[i]]) * voxel,
                    origin + MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[i + 1]]) * voxel,
                    origin + MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[i + 2]]) * voxel
                )
            );
        }
    }

    /// <summary>A block's samples and densities, its voxels of <paramref name="voxel"/> fine voxels from <paramref name="origin"/>.</summary>
    private static (byte[] Samples, byte[] Densities) Read(Shape shape, Vector3 origin, float voxel)
    {
        var samples = new byte[SurfaceNets.SampleCount];
        var densities = new byte[SurfaceNets.SampleCount];
        for (var z = 0; z < SurfaceNets.SampleSide; z++)
        {
            for (var y = 0; y < SurfaceNets.SampleSide; y++)
            {
                for (var x = 0; x < SurfaceNets.SampleSide; x++)
                {
                    var centre = origin + (new Vector3(x, y, z) - new Vector3(SurfaceNets.Apron - 0.5f)) * voxel;
                    var share = Math.Clamp(0.5f - shape.Distance(centre) / voxel, 0f, 1f);
                    var density = (byte)MathF.Round(share * 255);
                    var i = SurfaceNets.SampleIndex(x, y, z);
                    densities[i] = density;
                    samples[i] = density >= 128 ? (byte)1 : (byte)0;
                }
            }
        }

        return (samples, densities);
    }

    /// <summary>A position rounded to a sixty-fourth of a fine voxel, for telling vertices alike.</summary>
    private readonly record struct Key(int X, int Y, int Z) : IComparable<Key>
    {
        public Key(Vector3 p)
            : this((int)MathF.Round(p.X * 64), (int)MathF.Round(p.Y * 64), (int)MathF.Round(p.Z * 64)) { }

        public Vector3 Position => new Vector3(X, Y, Z) / 64;

        public int CompareTo(Key other) =>
            X != other.X ? X.CompareTo(other.X)
            : Y != other.Y ? Y.CompareTo(other.Y)
            : Z.CompareTo(other.Z);
    }

    /// <summary>Rolling ground with blobs on it and hollows in it, in fine voxels: negative inside.</summary>
    private sealed class Shape
    {
        private readonly float _a;
        private readonly float _b;
        private readonly (Vector3 Centre, float Radius)[] _blobs;
        private readonly (Vector3 Centre, float Radius)[] _hollows;

        public Shape(int seed)
        {
            var random = new Random(seed);
            _a = random.NextSingle() * 6;
            _b = random.NextSingle() * 6;
            _blobs = new (Vector3, float)[6];
            for (var i = 0; i < _blobs.Length; i++)
            {
                _blobs[i] = (
                    new Vector3(
                        48 + random.NextSingle() * 32,
                        22 + random.NextSingle() * 20,
                        8 + random.NextSingle() * 64
                    ),
                    2 + random.NextSingle() * 7
                );
            }

            _hollows = new (Vector3, float)[4];
            for (var i = 0; i < _hollows.Length; i++)
            {
                _hollows[i] = (
                    new Vector3(
                        48 + random.NextSingle() * 32,
                        18 + random.NextSingle() * 16,
                        8 + random.NextSingle() * 64
                    ),
                    2 + random.NextSingle() * 6
                );
            }
        }

        public float Distance(Vector3 p)
        {
            var ground =
                p.Y
                - (
                    28
                    + 6 * MathF.Sin(p.X / 7.3f + _a)
                    + 5 * MathF.Cos(p.Z / 5.1f + _b)
                    + 3 * MathF.Sin((p.X + p.Z) / 3.7f)
                );
            var d = ground;
            foreach (var (centre, radius) in _blobs)
            {
                d = MathF.Min(d, Vector3.Distance(p, centre) - radius);
            }

            foreach (var (centre, radius) in _hollows)
            {
                d = MathF.Max(d, radius - Vector3.Distance(p, centre));
            }

            return d;
        }
    }
}
