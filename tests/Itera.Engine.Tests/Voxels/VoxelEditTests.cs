using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed class VoxelEditTests
{
    [Fact]
    public void A_reset_is_an_empty_box_of_air_at_the_origin_that_covers_and_changes_nothing()
    {
        var reset = VoxelEdit.ResetAll() with { Sequence = 5 };
        reset.GetBounds(out var min, out var end);

        Assert.Null(reset.Validate());
        Assert.Equal(min, end);
        Assert.False(reset.Contains(0, 0, 0));
        Assert.Equal(7, reset.Apply(7));
        Assert.True(reset.LeavesUniform(3));
        Assert.NotNull((reset with { Extent = new VoxelCoord(1, 1, 1) }).Validate());
        Assert.NotNull((reset with { Shape = VoxelEditShape.Sphere }).Validate());
        Assert.NotNull((reset with { Material = 3 }).Validate());
        Assert.NotNull((reset with { Origin = new VoxelCoord(0, 1, 0) }).Validate());
        Assert.NotNull((reset with { Operation = (VoxelEditOperation)7 }).Validate());
    }

    [Fact]
    public void A_sphere_covers_points_at_exactly_its_radius()
    {
        var edit = VoxelEdit.Sphere(new VoxelCoord(10, -20, 30), 5, VoxelEditOperation.Clear);

        Assert.True(edit.Contains(15, -20, 30));
        Assert.True(edit.Contains(13, -16, 30));
        Assert.False(edit.Contains(16, -20, 30));
        Assert.False(edit.Contains(14, -16, 30));
    }

    [Fact]
    public void Sphere_bounds_hold_every_covered_point()
    {
        var edit = VoxelEdit.Sphere(new VoxelCoord(-3, 0, 7), 4, VoxelEditOperation.Clear);

        edit.GetBounds(out var min, out var end);

        Assert.Equal(new VoxelCoord(-7, -4, 3), min);
        Assert.Equal(new VoxelCoord(2, 5, 12), end);
        for (var z = min.Z - 1; z <= end.Z; z++)
        {
            for (var y = min.Y - 1; y <= end.Y; y++)
            {
                for (var x = min.X - 1; x <= end.X; x++)
                {
                    var inside = x >= min.X && x < end.X && y >= min.Y && y < end.Y && z >= min.Z && z < end.Z;
                    Assert.False(!inside && edit.Contains(x, y, z));
                }
            }
        }
    }

    [Fact]
    public void A_capsule_covers_points_within_its_radius_of_the_segment()
    {
        var edit = VoxelEdit.Capsule(new VoxelCoord(0, 0, 0), new VoxelCoord(10, 0, 0), 3, VoxelEditOperation.Clear);

        Assert.Equal(3, edit.Radius);
        Assert.Equal(new VoxelCoord(10, 0, 0), edit.CapsuleEnd);
        Assert.True(edit.Contains(5, 3, 0));
        Assert.False(edit.Contains(5, 4, 0));
        Assert.True(edit.Contains(-3, 0, 0));
        Assert.False(edit.Contains(-3, 1, 0));
        Assert.True(edit.Contains(13, 0, 0));
        Assert.False(edit.Contains(14, 0, 0));

        // A diagonal one: (6, 4) lies 1.41 from the line x = y, (7, 3) 2.83.
        var diagonal = VoxelEdit.Capsule(default, new VoxelCoord(10, 10, 0), 2, VoxelEditOperation.Clear);
        Assert.True(diagonal.Contains(5, 5, 0));
        Assert.True(diagonal.Contains(6, 4, 0));
        Assert.False(diagonal.Contains(7, 3, 0));

        // Both ends at one voxel: a sphere.
        var point = VoxelEdit.Capsule(new VoxelCoord(4, 4, 4), new VoxelCoord(4, 4, 4), 5, VoxelEditOperation.Clear);
        var sphere = VoxelEdit.Sphere(new VoxelCoord(4, 4, 4), 5, VoxelEditOperation.Clear);
        for (var z = -3; z <= 11; z++)
        {
            for (var y = -3; y <= 11; y++)
            {
                for (var x = -3; x <= 11; x++)
                {
                    Assert.Equal(sphere.Contains(x, y, z), point.Contains(x, y, z));
                }
            }
        }
    }

    [Fact]
    public void Capsule_bounds_hold_every_covered_point_and_its_test_matches_the_distance_to_its_segment()
    {
        var random = new Random(11);
        for (var trial = 0; trial < 300; trial++)
        {
            var start = new VoxelCoord(random.Next(-50, 50), random.Next(-50, 50), random.Next(-50, 50));
            var end = new VoxelCoord(
                start.X + random.Next(-12, 13),
                start.Y + random.Next(-12, 13),
                start.Z + random.Next(-12, 13)
            );
            var radius = random.Next(0, 6);
            var edit = VoxelEdit.Capsule(start, end, radius, VoxelEditOperation.Clear);
            Assert.Null(edit.Validate());
            edit.GetBounds(out var min, out var bound);
            for (var z = min.Z - 1; z <= bound.Z; z++)
            {
                for (var y = min.Y - 1; y <= bound.Y; y++)
                {
                    for (var x = min.X - 1; x <= bound.X; x++)
                    {
                        var inside =
                            x >= min.X && x < bound.X && y >= min.Y && y < bound.Y && z >= min.Z && z < bound.Z;
                        var contains = edit.Contains(x, y, z);
                        Assert.False(!inside && contains);

                        // Away from the surface the integer test agrees with the distance in doubles.
                        var distance = DistanceToSegment(x, y, z, start, end);
                        if (Math.Abs(distance - radius) > 1e-6)
                        {
                            Assert.Equal(distance < radius, contains);
                        }
                    }
                }
            }
        }
    }

    private static double DistanceToSegment(int x, int y, int z, VoxelCoord a, VoxelCoord b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double dz = b.Z - a.Z;
        var dd = dx * dx + dy * dy + dz * dz;
        var t = dd > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy + (z - a.Z) * dz) / dd, 0, 1) : 0;
        var px = x - (a.X + t * dx);
        var py = y - (a.Y + t * dy);
        var pz = z - (a.Z + t * dz);
        return Math.Sqrt(px * px + py * py + pz * pz);
    }

    [Fact]
    public void A_box_covers_its_min_and_excludes_its_end()
    {
        var edit = VoxelEdit.Box(new VoxelCoord(-2, 0, 4), new VoxelCoord(3, 1, 2), VoxelEditOperation.Set, 7);

        Assert.True(edit.Contains(-2, 0, 4));
        Assert.True(edit.Contains(0, 0, 5));
        Assert.False(edit.Contains(1, 0, 4));
        Assert.False(edit.Contains(0, 1, 4));
        Assert.False(edit.Contains(0, 0, 6));
    }

    [Theory]
    [InlineData(VoxelEditOperation.Set, 0, 9)]
    [InlineData(VoxelEditOperation.Set, 3, 9)]
    [InlineData(VoxelEditOperation.Clear, 3, 0)]
    [InlineData(VoxelEditOperation.Paint, 0, 0)]
    [InlineData(VoxelEditOperation.Paint, 3, 9)]
    public void Operations_change_voxels_as_documented(VoxelEditOperation operation, byte current, byte expected)
    {
        var edit = VoxelEdit.Sphere(default, 1, operation, 9);

        Assert.Equal(expected, edit.Apply(current));
    }

    [Theory]
    [InlineData(VoxelEditOperation.Set, 9, true)]
    [InlineData(VoxelEditOperation.Set, 0, false)]
    [InlineData(VoxelEditOperation.Clear, 0, true)]
    [InlineData(VoxelEditOperation.Clear, 9, false)]
    [InlineData(VoxelEditOperation.Paint, 0, true)]
    [InlineData(VoxelEditOperation.Paint, 9, true)]
    [InlineData(VoxelEditOperation.Paint, 4, false)]
    public void Uniform_chunks_that_an_edit_cannot_change_are_recognised(
        VoxelEditOperation operation,
        byte uniform,
        bool unchanged
    )
    {
        var edit = VoxelEdit.Sphere(default, 1, operation, 9);

        Assert.Equal(unchanged, edit.LeavesUniform(uniform));
    }

    [Fact]
    public void A_clear_of_some_materials_takes_only_those_and_is_a_box_of_air_that_names_at_least_one()
    {
        var edit = VoxelEdit.MaterialsBox(new VoxelCoord(-5, 2, 3), new VoxelCoord(10, 10, 10), [2, 63, 64, 127]);

        Assert.Null(edit.Validate());
        Assert.Equal(VoxelEditOperation.ClearMaterials, edit.Operation);
        Assert.Equal(VoxelConstants.Air, edit.Apply(2));
        Assert.Equal(VoxelConstants.Air, edit.Apply(63));
        Assert.Equal(VoxelConstants.Air, edit.Apply(64));
        Assert.Equal(VoxelConstants.Air, edit.Apply(127));
        Assert.Equal(3, edit.Apply(3));
        Assert.Equal(128, edit.Apply(128));
        Assert.Equal(VoxelConstants.Air, edit.Apply(VoxelConstants.Air));
        Assert.True(edit.LeavesUniform(VoxelConstants.Air));
        Assert.True(edit.LeavesUniform(3));
        Assert.False(edit.LeavesUniform(64));

        Assert.NotNull((edit with { Shape = VoxelEditShape.Sphere }).Validate());
        Assert.NotNull((edit with { Material = 2 }).Validate());
        Assert.NotNull((edit with { Piece = 0, Turn = 0 }).Validate());
        Assert.NotNull((edit with { Piece = edit.Piece | 1 }).Validate());
        Assert.NotNull((edit with { Operation = VoxelEditOperation.Clear }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VoxelEdit.MaterialsBox(default, new VoxelCoord(1, 1, 1), [VoxelEdit.MaterialSetSize])
        );
    }

    [Fact]
    public void Validation_allocates_nothing_even_right_after_a_collection()
    {
        var edit = VoxelEdit.Box(default, new VoxelCoord(4, 4, 4), VoxelEditOperation.Paint, 3);
        Assert.Null(edit.Validate());

        // StartMeasurement collects first. Enum.IsDefined keeps a cache that a collection drops, and rebuilding it
        // allocated about 480 bytes, which this check would catch every time.
        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            edit.Validate();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Invalid_edits_are_rejected_with_a_reason()
    {
        Assert.Null(VoxelEdit.Sphere(default, 100, VoxelEditOperation.Clear).Validate());
        Assert.NotNull(VoxelEdit.Sphere(default, -1, VoxelEditOperation.Clear).Validate());
        Assert.NotNull(
            VoxelEdit.Sphere(default, VoxelConstants.MaxEditExtent + 1, VoxelEditOperation.Clear).Validate()
        );
        Assert.NotNull(VoxelEdit.Box(default, new VoxelCoord(1, -1, 1), VoxelEditOperation.Clear).Validate());
        Assert.NotNull(VoxelEdit.Box(new VoxelCoord(int.MinValue, 0, 0), default, VoxelEditOperation.Clear).Validate());
        Assert.NotNull(
            (VoxelEdit.Sphere(default, 1, VoxelEditOperation.Clear) with { Shape = (VoxelEditShape)9 }).Validate()
        );

        var capsule = VoxelEdit.Capsule(default, new VoxelCoord(-20, 5, 9), 4, VoxelEditOperation.Clear);
        Assert.Null(capsule.Validate());
        Assert.Null((capsule with { Piece = 0 }).Validate());
        Assert.NotNull((capsule with { Piece = -1 }).Validate());
        Assert.NotNull((capsule with { Piece = VoxelConstants.MaxEditExtent + 1L }).Validate());
        Assert.NotNull((capsule with { Piece = long.MaxValue }).Validate());
        Assert.NotNull((capsule with { Turn = 1 }).Validate());
        Assert.NotNull((capsule with { Extent = new VoxelCoord(VoxelEdit.MaxCapsuleOffset + 1, 0, 0) }).Validate());
        Assert.NotNull((capsule with { Extent = new VoxelCoord(0, int.MinValue, 0) }).Validate());
        Assert.NotNull((capsule with { Operation = VoxelEditOperation.Lift }).Validate());

        // A lift's box with an empty side lifts a piece no save can hold, and a piece edit reaches no farther than the
        // largest piece.
        Assert.Null(VoxelEdit.Box(default, new VoxelCoord(4, 4, 4), VoxelEditOperation.Lift).Validate());
        Assert.NotNull(VoxelEdit.Box(default, new VoxelCoord(4, 0, 4), VoxelEditOperation.Lift).Validate());
        Assert.NotNull(VoxelEdit.Box(default, new VoxelCoord(4, 4, 0), VoxelEditOperation.Lift).Validate());
        Assert.Null(VoxelEdit.PieceAt(1, VoxelPiece.MaxRadius, default, VoxelEdit.NoTurn).Validate());
        Assert.NotNull(VoxelEdit.PieceAt(1, VoxelPiece.MaxRadius + 1, default, VoxelEdit.NoTurn).Validate());
        Assert.NotNull(VoxelEdit.PieceAt(1, 10_000, default, VoxelEdit.NoTurn).Validate());
        Assert.Null(
            (
                capsule with
                {
                    Extent = new VoxelCoord(-VoxelEdit.MaxCapsuleOffset, VoxelEdit.MaxCapsuleOffset, 0),
                }
            ).Validate()
        );
    }
}
