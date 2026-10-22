using Itera.Engine.Maths;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary>
/// The shapes that fill rows from a range of x worked out per row, checked against a test of each point on its own: for
/// random capsules, cut ellipsoids, and prisms, at the sample points of levels 0 to 4, a point lies in its row's range
/// exactly when it lies in the shape. Points within a millionth of a centimetre of the surface are skipped, where
/// rounding may go either way. Ellipsoid fills and row fills are checked voxel by voxel.
/// </summary>
public sealed class ShapeSpanTests
{
    private const double Edge = 1e-6;

    [Fact]
    public void A_capsules_rows_hold_exactly_its_points()
    {
        var random = new Random(24);
        var shapes = new List<Capsule>
        {
            new(0, 0, 0, 0, 0, 0, 17), // a sphere
            new(-20, 3, 5, 25, 3, 5, 9), // along x
            new(5, -25, 7, 5, 30, 7, 12), // upright
            new(2, 4, -30, 2, 4, 15, 6.5), // along z
        };
        for (var i = 0; i < 10; i++)
        {
            shapes.Add(
                new Capsule(
                    random.Next(-30, 30) + random.NextDouble(),
                    random.Next(-30, 30) + random.NextDouble(),
                    random.Next(-30, 30) + random.NextDouble(),
                    random.Next(-30, 30) + random.NextDouble(),
                    random.Next(-30, 30) + random.NextDouble(),
                    random.Next(-30, 30) + random.NextDouble(),
                    2 + 14 * random.NextDouble()
                )
            );
        }

        var inside = 0;
        foreach (var capsule in shapes)
        {
            for (var level = 0; level <= 4; level++)
            {
                foreach (var z in Samples(capsule.MinZ, capsule.MaxZ, level))
                {
                    var planeHeld = capsule.Heights(z, capsule.Radius, out var lowest, out var highest);
                    foreach (var y in Samples(capsule.MinY, capsule.MaxY, level))
                    {
                        var row = capsule.Span(y, z, capsule.Radius, out var min, out var max);
                        var reach = Math.Max(Math.Abs(capsule.AX), Math.Abs(capsule.BX)) + capsule.Radius;
                        foreach (var x in Samples(-reach, reach, level))
                        {
                            var distance = Math.Sqrt(SegmentDistanceSquared(capsule, x, y, z));
                            if (Math.Abs(distance - capsule.Radius) < Edge)
                            {
                                continue;
                            }

                            var expected = distance < capsule.Radius;
                            if (expected != (row && x >= min && x <= max))
                            {
                                Assert.Fail(
                                    $"{capsule} level {level} at ({x}, {y}, {z}): distance {distance}, row {row} [{min}, {max}]"
                                );
                            }

                            if (expected)
                            {
                                inside++;
                                if (!(planeHeld && y >= lowest && y <= highest))
                                {
                                    Assert.Fail($"{capsule} heights at z {z}");
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.True(inside > 5_000, $"{inside} points inside");
    }

    [Fact]
    public void A_cut_ellipsoids_rows_hold_exactly_its_points()
    {
        var random = new Random(31);
        var planes = new List<ShapePlane>();
        var parts = new List<CutEllipsoid>();
        for (var i = 0; i < 12; i++)
        {
            var first = planes.Count;
            var count = random.Next(0, 5);
            for (var p = 0; p < count; p++)
            {
                // A unit normal, some with no x (the row test's own case), at 0.2 to 0.9 of the part's size.
                var (nx, ny, nz) = (
                    p == 0 ? 0 : random.NextDouble() * 2 - 1,
                    random.NextDouble() * 2 - 1,
                    random.NextDouble() * 2 - 1
                );
                var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                planes.Add(
                    new ShapePlane(nx / length, ny / length, nz / length, (0.2 + 0.7 * random.NextDouble()) * 15)
                );
            }

            var radiusY = 6 + 20 * random.NextDouble();
            var y = random.Next(-20, 20) + random.NextDouble();
            parts.Add(
                new CutEllipsoid(
                    random.Next(-20, 20) + random.NextDouble(),
                    y,
                    random.Next(-20, 20) + random.NextDouble(),
                    6 + 20 * random.NextDouble(),
                    radiusY,
                    6 + 20 * random.NextDouble(),
                    random.NextDouble() - 0.5,
                    random.NextDouble() - 0.5,
                    y - radiusY * random.NextDouble(),
                    first,
                    count
                )
            );
        }

        var all = planes.ToArray();
        var inside = 0;
        foreach (var part in parts)
        {
            for (var level = 0; level <= 4; level++)
            {
                foreach (var z in Samples(part.Z - part.ExtentZ, part.Z + part.ExtentZ, level))
                {
                    foreach (var y in Samples(part.MinY - 2, part.MaxY, level))
                    {
                        var row = part.Span(y, z, all, out var min, out var max);
                        foreach (var x in Samples(part.X - part.ExtentX, part.X + part.ExtentX, level))
                        {
                            var margin = Margin(part, all, x, y, z);
                            if (Math.Abs(margin) < Edge)
                            {
                                continue;
                            }

                            var expected = margin > 0;
                            inside += expected ? 1 : 0;
                            if (expected != (row && x >= min && x <= max))
                            {
                                Assert.Fail(
                                    $"{part} level {level} at ({x}, {y}, {z}): margin {margin}, row {row} [{min}, {max}]"
                                );
                            }
                        }
                    }
                }
            }
        }

        Assert.True(inside > 5_000, $"{inside} points inside");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void An_ellipsoid_fills_exactly_the_voxels_whose_sample_points_lie_inside(int baseCentimetres)
    {
        var random = new Random(61);
        var grid = new VoxelGrid(baseCentimetres);
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        var first = new VoxelCoord(-64, -64, -64);
        var filled = 0;
        for (var shape = 0; shape < 3; shape++)
        {
            var x = random.Next(-20, 20) * baseCentimetres;
            var y = random.Next(-20, 20) * baseCentimetres;
            var z = random.Next(-20, 20) * baseCentimetres;
            var rx = random.Next(5, 30) * baseCentimetres;
            var ry = random.Next(5, 30) * baseCentimetres;
            var rz = random.Next(5, 30) * baseCentimetres;
            long xx = (long)rx * rx;
            long yy = (long)ry * ry;
            long zz = (long)rz * rz;
            for (var level = 0; level <= 3; level++)
            {
                var gridLevel = new GridLevel(grid, level);
                Array.Clear(voxels);
                Ellipsoid.Fill(x, y, z, rx, ry, rz, 9, first, gridLevel, voxels);
                for (var vz = 0; vz < VoxelConstants.ChunkSize; vz++)
                {
                    long dz = gridLevel.SampleAt(first.Z + vz) - z;
                    for (var vy = 0; vy < VoxelConstants.ChunkSize; vy++)
                    {
                        long dy = gridLevel.SampleAt(first.Y + vy) - y;
                        for (var vx = 0; vx < VoxelConstants.ChunkSize; vx++)
                        {
                            long dx = gridLevel.SampleAt(first.X + vx) - x;
                            var inside = dx * dx * yy * zz + dy * dy * xx * zz + dz * dz * xx * yy <= xx * yy * zz;
                            var material = voxels[(vz * VoxelConstants.ChunkSize + vy) * VoxelConstants.ChunkSize + vx];
                            filled += inside ? 1 : 0;
                            if ((inside ? 9 : 0) != material)
                            {
                                Assert.Fail(
                                    $"base {baseCentimetres} level {level} voxel ({vx}, {vy}, {vz}): {material}, inside {inside}"
                                );
                            }
                        }
                    }
                }
            }
        }

        Assert.True(filled > 1_000, $"{filled} voxels filled");
    }

    [Fact]
    public void A_prism_boxs_rows_hold_exactly_its_points()
    {
        var random = new Random(53);
        var builder = new PrismBuilder();
        var boxes =
            new List<(
                Prism Prism,
                double X,
                double Y,
                double Z,
                double AlongX,
                double AlongZ,
                double Along,
                double Across,
                double Up
            )>();
        for (var i = 0; i < 10; i++)
        {
            var (alongX, alongZ) = DeterministicMath.CosSin(random.NextDouble());
            var x = random.Next(-20, 20) + random.NextDouble();
            var y = random.Next(-20, 20) + random.NextDouble();
            var z = random.Next(-20, 20) + random.NextDouble();
            var along = 4 + 16 * random.NextDouble();
            var across = 4 + 16 * random.NextDouble();
            var up = 4 + 16 * random.NextDouble();
            boxes.Add(
                (builder.Box((x, y, z), alongX, alongZ, along, across, up), x, y, z, alongX, alongZ, along, across, up)
            );
        }

        var planes = builder.Planes();
        var inside = 0;
        foreach (var (prism, cx, cy, cz, alongX, alongZ, halfAlong, halfAcross, halfUp) in boxes)
        {
            for (var level = 0; level <= 4; level++)
            {
                foreach (var z in Samples(prism.MinZ - 1, prism.MaxZ + 1, level))
                {
                    foreach (var y in Samples(prism.MinY - 1, prism.MaxY + 1, level))
                    {
                        var row = prism.Span(y, z, planes, 0, out var min, out var max);
                        foreach (var x in Samples(prism.MinX - 1, prism.MaxX + 1, level))
                        {
                            var along = (x - cx) * alongX + (z - cz) * alongZ;
                            var across = (z - cz) * alongX - (x - cx) * alongZ;
                            var margin = Math.Min(
                                halfAlong - Math.Abs(along),
                                Math.Min(halfAcross - Math.Abs(across), halfUp - Math.Abs(y - cy))
                            );
                            if (Math.Abs(margin) < Edge)
                            {
                                continue;
                            }

                            var expected = margin > 0;
                            if (expected != (row && x >= min && x <= max))
                            {
                                Assert.Fail(
                                    $"{prism} level {level} at ({x}, {y}, {z}): margin {margin}, row {row} [{min}, {max}]"
                                );
                            }

                            if (expected != prism.Contains(x, y, z, planes, 0))
                            {
                                Assert.Fail($"{prism} level {level} at ({x}, {y}, {z}): Contains is not {expected}");
                            }

                            if (expected)
                            {
                                inside++;
                                if (!(x >= prism.MinX && x <= prism.MaxX && z >= prism.MinZ && z <= prism.MaxZ))
                                {
                                    Assert.Fail(
                                        $"{prism} level {level}: ({x}, {y}, {z}) lies outside its x and z bounds"
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.True(inside > 5_000, $"{inside} points inside");
    }

    [Fact]
    public void A_prism_encloses_a_box_only_when_the_whole_box_lies_inside()
    {
        var builder = new PrismBuilder();
        var prism = builder.Box((10, 20, 30), 0.6, 0.8, 8, 5, 4);
        var planes = builder.Planes();

        // The sphere of radius 3.5 about the centre fits in the box, whose smallest half side is 4.
        Assert.True(prism.Encloses(6.5, 13.5, 16.5, 23.5, 26.5, 33.5, planes));
        Assert.False(prism.Encloses(-30, 50, 0, 40, 0, 60, planes));
        Assert.False(prism.Encloses(10, 10, 20, 20, 30, 130, planes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void A_row_fill_takes_the_voxels_whose_sample_points_lie_in_the_range(int baseCentimetres)
    {
        var random = new Random(17);
        var grid = new VoxelGrid(baseCentimetres);
        for (var level = 0; level <= 3; level++)
        {
            var gridLevel = new GridLevel(grid, level);
            for (var i = 0; i < 20; i++)
            {
                var firstX = random.Next(-100, 100);
                var min = random.Next(-2000, 2000) + random.NextDouble();
                var max = min + random.Next(0, 3000) * random.NextDouble();
                var row = new byte[VoxelConstants.ChunkSize];
                VoxelRows.FillRange(row, min, max, firstX, gridLevel, 7);
                var air = new byte[VoxelConstants.ChunkSize];
                Array.Fill(air, (byte)5);
                air[3] = VoxelConstants.Air;
                VoxelRows.ReplaceAirInRange(air, min, max, firstX, gridLevel, 7);
                var (start, end) = VoxelRows.Rows(min, max, firstX, gridLevel);
                for (var x = 0; x < row.Length; x++)
                {
                    var sample = gridLevel.SampleAt(firstX + x);
                    var inside = sample >= min && sample <= max;
                    Assert.Equal(inside ? 7 : 0, row[x]);
                    Assert.Equal(x == 3 ? (inside ? 7 : VoxelConstants.Air) : 5, air[x]);
                    Assert.Equal(inside, x >= start && x < end);
                }
            }
        }
    }

    /// <summary>The sample points of <paramref name="level"/> from <paramref name="min"/> to <paramref name="max"/>, with a voxel either side.</summary>
    private static IEnumerable<int> Samples(double min, double max, int level)
    {
        var step = 1 << level;
        for (
            var i = DeterministicMath.FloorDivide((int)Math.Floor(min), step) - 1;
            VoxelConstants.SampleAt(i, level) <= max + step;
            i++
        )
        {
            yield return VoxelConstants.SampleAt(i, level);
        }
    }

    private static double SegmentDistanceSquared(in Capsule c, double x, double y, double z)
    {
        var (dx, dy, dz) = (c.BX - c.AX, c.BY - c.AY, c.BZ - c.AZ);
        var length = dx * dx + dy * dy + dz * dz;
        var t = length > 0 ? Math.Clamp(((x - c.AX) * dx + (y - c.AY) * dy + (z - c.AZ) * dz) / length, 0, 1) : 0;
        var (px, py, pz) = (c.AX + dx * t - x, c.AY + dy * t - y, c.AZ + dz * t - z);
        return px * px + py * py + pz * pz;
    }

    /// <summary>
    /// How far inside the part the point is, by its nearest test: the floor, the ellipsoid (as 1 less its sum), and each
    /// plane; positive inside, negative outside.
    /// </summary>
    private static double Margin(in CutEllipsoid part, ShapePlane[] planes, double x, double y, double z)
    {
        var v = y - part.Y;
        var u = x - part.X - part.LeanX * v;
        var w = z - part.Z - part.LeanZ * v;
        if (y < part.Floor)
        {
            return part.Floor - y < Edge ? 0 : -1;
        }

        var margin = (
            1
            - u * u / (part.RadiusX * part.RadiusX)
            - v * v / (part.RadiusY * part.RadiusY)
            - w * w / (part.RadiusZ * part.RadiusZ)
        );
        for (var i = part.FirstPlane; i < part.FirstPlane + part.PlaneCount; i++)
        {
            var plane = planes[i];
            margin = Math.Min(margin, plane.Distance - (plane.X * u + plane.Y * v + plane.Z * w));
        }

        return margin;
    }
}
