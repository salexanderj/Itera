using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Maths;

/// <summary>
/// <see cref="WorldOrigin"/> and <see cref="WorldPoint"/>: the origin lies on whole metres; a local float turned
/// absolute and back is the same float anywhere on a ±160 km map; whole centimetres read from a local position at any
/// origin are those read at the map's middle moved by the origin (for positions 3 cm or more from it, where a float's
/// last bit times 100 is coarser than a double's rounding there); and absolute points keep their centimetres exactly.
/// </summary>
public sealed class WorldOriginTests
{
    private static readonly VoxelCoord[] s_origins =
    [
        new(0, 0, 0),
        new(30_000, -20_000, 51_200),
        new(15_000_000, 102_400, -15_000_000),
        new(-16_000_000, -16_000_000, 16_000_000),
    ];

    [Fact]
    public void The_origin_starts_at_the_middle_and_moves_only_to_whole_metres()
    {
        var origin = new WorldOrigin();
        Assert.Equal(default, origin.Centimetres);
        Assert.Equal(0, origin.Moves);

        origin.MoveTo(new VoxelCoord(15_000_000, -300, 100));
        Assert.Equal(new WorldPoint(150_000, -3, 1), origin.Metres);
        Assert.Equal(1, origin.Moves);

        Assert.Throws<ArgumentException>(() => origin.MoveTo(new VoxelCoord(150, 0, 0)));
        Assert.Throws<ArgumentException>(() => origin.MoveTo(new VoxelCoord(0, -1, 0)));
        Assert.Equal(1, origin.Moves);
    }

    [Fact]
    public void A_local_float_turned_absolute_and_back_is_the_same_float_anywhere()
    {
        var random = new Random(7);
        foreach (var at in s_origins)
        {
            var origin = At(at);
            for (var i = 0; i < 10_000; i++)
            {
                var local = RandomLocal(random);
                Assert.Equal(local, origin.ToLocal(origin.ToWorld(local)));
            }
        }
    }

    [Fact]
    public void Centimetres_from_a_local_position_are_the_middles_moved_by_the_origin()
    {
        var middle = new WorldOrigin();
        var random = new Random(11);
        foreach (var at in s_origins)
        {
            var origin = At(at);
            for (var i = 0; i < 10_000; i++)
            {
                var local = RandomLocal(random);
                Assert.Equal(middle.ToCentimetres(local) + at, origin.ToCentimetres(local));
            }
        }
    }

    [Fact]
    public void A_whole_centimetre_turns_local_as_the_same_float_from_any_origin()
    {
        var middle = new WorldOrigin();
        var random = new Random(13);
        foreach (var at in s_origins)
        {
            var origin = At(at);
            for (var i = 0; i < 10_000; i++)
            {
                var offset = new VoxelCoord(
                    random.Next(-60_000, 60_000),
                    random.Next(-60_000, 60_000),
                    random.Next(-60_000, 60_000)
                );
                Assert.Equal(middle.ToLocal(offset), origin.ToLocal(at + offset));
                Assert.Equal(
                    middle.ToLocal(WorldPoint.FromCentimetres(offset)),
                    origin.ToLocal(WorldPoint.FromCentimetres(at + offset))
                );
            }
        }
    }

    [Fact]
    public void A_centimetres_middle_kept_as_a_local_float_reads_back_as_that_centimetre_150_km_out()
    {
        var at = new VoxelCoord(15_000_000, 102_400, -15_000_000);
        var origin = At(at);
        var random = new Random(17);
        for (var i = 0; i < 10_000; i++)
        {
            // Anywhere within 512 m of the origin, where it moves from, a float keeps a point to about 30 µm.
            var centimetre =
                at
                + new VoxelCoord(
                    random.Next(-51_200, 51_200),
                    random.Next(-51_200, 51_200),
                    random.Next(-51_200, 51_200)
                );
            var middle = WorldPoint.FromCentimetres(centimetre).Plus(0.005, 0.005, 0.005);

            Assert.Equal(centimetre, origin.ToCentimetres(origin.ToLocal(middle)));
        }
    }

    [Fact]
    public void An_absolute_point_keeps_its_centimetres_to_the_edit_limit()
    {
        foreach (var centimetres in new[] { 16_777_215, 16_000_000, 15_052_801, 1, 0, -1, -15_052_801, -16_777_216 })
        {
            var coord = new VoxelCoord(centimetres, -centimetres, centimetres);
            var point = WorldPoint.FromCentimetres(coord);

            Assert.Equal(coord, point.ToCentimetres());
            Assert.Equal(coord, point.Plus(0.0099, 0.0099, 0.0099).ToCentimetres());
            Assert.Equal(coord - new VoxelCoord(1, 1, 1), point.Plus(-0.0001, -0.0001, -0.0001).ToCentimetres());
        }

        // Every whole centimetre written in metres reads back as itself (1.13 m is 112.99999999999999 cm in binary).
        for (var centimetres = -2_000_000; centimetres <= 2_000_000; centimetres += 7)
        {
            var coord = new VoxelCoord(centimetres, centimetres + 1, centimetres + 2);
            Assert.Equal(coord, WorldPoint.FromCentimetres(coord).ToCentimetres());
        }

        Assert.Equal(new VoxelCoord(113, -1, -250), new WorldPoint(1.13, -0.004, -2.5).ToCentimetres());
        Assert.Equal(new VoxelCoord(150, -1, -250), new WorldPoint(1.505, -0.004, -2.5).ToCentimetres());
        Assert.Equal(new WorldPoint(1.5, 0, -2.5), WorldPoint.FromCentimetres(new VoxelCoord(150, 0, -250)));
    }

    [Fact]
    public void The_axes_absolute_values_are_the_origins_whole_metres_plus_the_local_float()
    {
        var origin = At(new VoxelCoord(15_000_000, 102_400, -15_000_000));

        Assert.Equal(150_000.25, origin.WorldX(0.25f));
        Assert.Equal(1_024 - 0.5, origin.WorldY(-0.5f));
        Assert.Equal(-150_000 + (double)0.1f, origin.WorldZ(0.1f));
        Assert.Equal(
            new WorldPoint(150_000.25, 1_023.5, -150_000 + (double)0.1f),
            origin.ToWorld(new(0.25f, -0.5f, 0.1f))
        );
        Assert.Equal(new Vector3(-0.5f, 1, 2.25f), origin.ToLocal(new WorldPoint(149_999.5, 1_025, -149_997.75)));
    }

    /// <summary>A local position within 600 m of the origin and at least 3 cm from it on each axis.</summary>
    private static Vector3 RandomLocal(Random random)
    {
        return new(Axis(), Axis(), Axis());

        float Axis()
        {
            var value = (float)(random.NextDouble() * 599.97 + 0.03);
            return random.Next(2) == 0 ? value : -value;
        }
    }

    private static WorldOrigin At(VoxelCoord centimetres)
    {
        var origin = new WorldOrigin();
        origin.MoveTo(centimetres);
        return origin;
    }
}
