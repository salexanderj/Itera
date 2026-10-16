using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Tests.Voxels.Generation;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Physics;

/// <summary>Rolling hills on 1 m voxels (<see cref="HillsGenerator"/> without its boulders and caves), loaded once.</summary>
public sealed class CoarseHillsFixture : IDisposable
{
    /// <summary>Where the tests stand: the middle of what is loaded.</summary>
    public static readonly Vector3 Centre = new(0, 6, 0);

    public CoarseHillsFixture()
    {
        World = TestWorlds.Load(new HillsGenerator(new VoxelGrid(100), features: false), Centre, radiusMetres: 60);
        Coarse = new VoxelCharacterMover(World, new NothingPassable());
        Fine = new VoxelCharacterMover(World, 10, new NothingPassable());
    }

    public VoxelWorld World { get; }

    /// <summary>The mover in the world's 1 m cells.</summary>
    public VoxelCharacterMover Coarse { get; }

    /// <summary>The mover in 10 cm cells over the same voxels.</summary>
    public VoxelCharacterMover Fine { get; }

    public void Dispose() => World.Dispose();
}

/// <summary>
/// Smooth hills on 1 m voxels, as a hand-made map's (slopes up to about 35 degrees, rising 6 m and falling 4 m over tens
/// of metres), loaded once round the middle.
/// </summary>
public sealed class SmoothHillsFixture : IDisposable
{
    /// <summary>Where the tests stand: the middle of what is loaded.</summary>
    public static readonly Vector3 Centre = new(0, 2, 0);

    public SmoothHillsFixture()
    {
        World = TestWorlds.Load(new SmoothHillsGenerator(), Centre, radiusMetres: 60);
        Coarse = new VoxelCharacterMover(World, new NothingPassable());
        Fine = new VoxelCharacterMover(World, 10, new NothingPassable());
    }

    public VoxelWorld World { get; }

    /// <summary>The mover in the world's 1 m cells.</summary>
    public VoxelCharacterMover Coarse { get; }

    /// <summary>The mover in 10 cm cells over the same voxels.</summary>
    public VoxelCharacterMover Fine { get; }

    public void Dispose() => World.Dispose();
}

/// <summary>Smooth hills on 1 m voxels, of grass over stone (<see cref="SmoothHillsFixture"/>).</summary>
internal sealed class SmoothHillsGenerator()
    : HeightfieldGenerator(new VoxelGrid(100), new GroundLayers(100, [], HillsGenerator.Stone))
{
    public override string Id => "test.smooth-hills";

    public override int Version => 1;

    /// <summary>The ground's height at (x, z) in centimetres.</summary>
    protected override double HeightAt(int x, int z, ulong seed) =>
        300 * Math.Sin(x / 1100.0) * Math.Cos(z / 1300.0) + 300 * Math.Sin((x + z) / 1700.0) + 100;

    protected override byte SurfaceAt(int x, int z, ulong seed, double height) => HillsGenerator.Grass;
}

/// <summary>
/// The 1 m voxels read in 10 cm cells (<see cref="FineGround"/>): over the hills each cell column's top is where its
/// centre's surface lies, the voxels' columns' surfaces blended with no seam between columns that join, and its surface
/// that surface; a wall of 1 m voxels blocks exactly the cells inside it and an opening through it none; and
/// its queries allocate nothing.
/// </summary>
public sealed class FineGroundTests(CoarseHillsFixture hills, CoarseCeilingFixture opening)
    : IClassFixture<CoarseHillsFixture>,
        IClassFixture<CoarseCeilingFixture>
{
    private const int Factor = 10;

    [Fact]
    public void Over_the_hills_each_cell_columns_top_and_surface_are_the_seamless_blend_at_its_centre()
    {
        var world = hills.World;
        var fine = new FineGround(world, 100 / Factor);
        var passable = default(MaterialSet);
        fine.Begin(1, in passable);
        // A square of 12 by 12 m of 10 cm cell columns, and the 1 m columns round it.
        const int X0 = -60,
            Z0 = -60,
            Side = 120;
        var tops = new int[Side * Side];
        fine.ColumnTops(X0, Z0, Side, Side, -300, 300, tops);
        var surfaces = CoarseSurfaces(world, -7, -7, 14, -30, 30);
        var checkedColumns = 0;
        for (var z = 0; z < Side; z++)
        {
            for (var x = 0; x < Side; x++)
            {
                var expected = Blended(surfaces, -7, -7, 14, (X0 + x + 0.5) / Factor, (Z0 + z + 0.5) / Factor);
                var top = (int)Math.Floor(expected * Factor + 0.5);
                Assert.Equal(top, tops[x + Side * z]);
                var got = fine.SurfaceOver(X0 + x, top, Z0 + z);
                if (Math.Abs(got - expected * Factor) > 1e-9)
                {
                    var vx = (int)Math.Floor((X0 + x + 0.5) / Factor);
                    var vz = (int)Math.Floor((Z0 + z + 0.5) / Factor);
                    var around = string.Join(
                        " ",
                        Enumerable
                            .Range(-1, 3)
                            .SelectMany(dz => Enumerable.Range(-1, 3).Select(dx => (dx, dz)))
                            .Select(p => surfaces[vx + p.dx + 7 + 14 * (vz + p.dz + 7)].ToString("F3"))
                    );
                    Assert.Fail(
                        $"Cell ({X0 + x}, {Z0 + z}) in column ({vx}, {vz}): {got} against {expected * Factor}; round it {around}"
                    );
                }

                checkedColumns++;
            }
        }

        Assert.Equal(Side * Side, checkedColumns);
    }

    [Fact]
    public void A_wall_of_metre_voxels_blocks_the_cells_inside_it_and_its_opening_none()
    {
        // The wall: x -4 to 4 m, z 0 to 1 m, 4 m tall; its opening x -1 to 2 m, 3 m tall.
        var fine = new FineGround(opening.World, 100 / Factor);
        var passable = default(MaterialSet);
        fine.Begin(1, in passable);
        var rows = new UInt128[30];
        // 0.5 to 1.5 m up, over x -6 to 6 m and z -1 to 2 m.
        fine.BlockedCells(-60, -10, 120, 5, 15, rows);
        for (var z = 0; z < rows.Length; z++)
        {
            for (var x = 0; x < 120; x++)
            {
                var cellX = -60 + x;
                var cellZ = -10 + z;
                var inWall = cellX >= -40 && cellX < 40 && cellZ >= 0 && cellZ < 10;
                var inOpening = cellX >= -10 && cellX < 20;
                var blocked = ((rows[z] >> x) & UInt128.One) != UInt128.Zero;
                Assert.True(blocked == (inWall && !inOpening), $"Cell ({cellX}, {cellZ}) blocked {blocked}.");
            }
        }

        // Over the opening the lintel blocks from 3 m up, and the ground under it lies at 0.
        Span<int> tops = stackalloc int[1];
        fine.ColumnTops(5, 5, 1, 1, -20, 25, tops);
        Assert.Equal(0, tops[0]);
        fine.ColumnTops(5, 5, 1, 1, -20, 35, tops);
        Assert.Equal(36, tops[0]);
    }

    [Fact]
    public void Its_queries_allocate_nothing()
    {
        var fine = new FineGround(hills.World, 100 / Factor);
        var passable = default(MaterialSet);
        var rows = new UInt128[64];
        var tops = new int[64 * 64];
        var footprint = new CircleFootprint(5.5, 7.5, 3);
        void Query()
        {
            fine.Begin(1, in passable);
            fine.BlockedCells(-30, -30, 64, 50, 80, rows);
            fine.ColumnTops(-30, -30, 64, 64, 0, 200, tops);
            fine.HighestTop(in footprint, 0, 200);
            fine.LowestBottom(in footprint, 0, 200);
            fine.SurfaceOver(5, tops[35 + 64 * 37], 7);
            fine.TopAt(5, 7, 0, 200, out _, out _);
        }

        Query();
        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 20; i++)
        {
            Query();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>
    /// The surface of each 1 m column of a square (its highest ground from y0 to y1, the density's surface over it), as
    /// the 1 m mover reads them; NaN where none lies there.
    /// </summary>
    private static double[] CoarseSurfaces(VoxelWorld world, int x0, int z0, int side, int y0, int y1)
    {
        var tops = new int[side * side];
        var passable = default(MaterialSet);
        world.ColumnTops(x0, z0, side, side, y0, y1, in passable, tops);
        var surfaces = new double[side * side];
        for (var i = 0; i < tops.Length; i++)
        {
            surfaces[i] =
                tops[i] == int.MinValue
                    ? double.NaN
                    : world.SurfaceOver(x0 + i % side, tops[i], z0 + i / side, in passable);
        }

        return surfaces;
    }

    /// <summary>
    /// The seamless blend at (x, z) in metres over a square of columns' surfaces, for a 45 degree ramp: bilinear over the
    /// quarter of the column, between its centre's surface, its sides' middles (the mean with a joined neighbour), and
    /// its corner (the mean of the joined group of the four columns round it).
    /// </summary>
    private static double Blended(double[] surfaces, int x0, int z0, int side, double x, double z)
    {
        double At(int cx, int cz) =>
            (uint)(cx - x0) < (uint)side && (uint)(cz - z0) < (uint)side
                ? surfaces[cx - x0 + side * (cz - z0)]
                : double.NaN;
        var vx = (int)Math.Floor(x);
        var vz = (int)Math.Floor(z);
        var own = At(vx, vz);
        var dx = x - vx >= 0.5 ? 1 : -1;
        var dz = z - vz >= 0.5 ? 1 : -1;
        double Middle(double other) => Math.Abs(other - own) < 1 ? (own + other) / 2 : own;
        var group = new List<double> { own };
        var others = new List<double> { At(vx + dx, vz), At(vx, vz + dz), At(vx + dx, vz + dz) };
        for (var grew = true; grew; )
        {
            grew = false;
            foreach (var other in others.ToArray())
            {
                if (group.Any(member => Math.Abs(member - other) < 1))
                {
                    group.Add(other);
                    others.Remove(other);
                    grew = true;
                }
            }
        }

        double tx = Math.Abs(x - vx - 0.5) * 2,
            tz = Math.Abs(z - vz - 0.5) * 2;
        return own * (1 - tx) * (1 - tz)
            + Middle(At(vx + dx, vz)) * tx * (1 - tz)
            + Middle(At(vx, vz + dz)) * (1 - tx) * tz
            + group.Average() * tx * tz;
    }
}
