using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// The run's object kinds (<see cref="VoxelObjectKinds"/>): the registry's rules; a kind split into parts of at most 128
/// voxels a side, empty ones left out; each part's coarser levels the mean of the voxels they cover; sealing for a world
/// whose voxels are coarser than every kind's; and the fingerprint peers compare.
/// </summary>
public sealed class VoxelObjectKindsTests
{
    private const byte Bark = 30;
    private const byte Leaves = 31;

    [Fact]
    public void Kinds_are_numbered_in_order_found_by_name_and_refused_twice_empty_or_once_sealed()
    {
        var kinds = new VoxelObjectKinds();
        var rock = kinds.Register("rock", Ball(5, 10));
        var bush = kinds.Register("bush", Ball(4, 12));

        Assert.Equal(0, rock.Value);
        Assert.Equal(1, bush.Value);
        Assert.Equal(2, kinds.Count);
        Assert.True(kinds.TryFind("bush", out var found));
        Assert.Equal(bush, found);
        Assert.False(kinds.TryFind("tree", out _));
        Assert.Equal("rock", kinds[rock].Name);
        Assert.Equal(5, kinds[rock].UnitCentimetres);
        Assert.Throws<InvalidOperationException>(() => kinds.Register("rock", Ball(5, 10)));
        Assert.Throws<ArgumentException>(() =>
            kinds.Register("nothing", new VoxelObjectShape(5, new VoxelCoord(8, 8, 8), default))
        );
        // A ball a fifth of a voxel across, a voxel's centre 1 cm from its surface: some density, no solid voxel.
        var faint = new VoxelObjectShape(5, new VoxelCoord(8, 8, 8), default);
        faint.FillEllipsoid(19, 17.5, 17.5, 0.5, 0.5, 0.5, Leaves);
        Assert.InRange(faint.DensityAt(3, 3, 3), 1, VoxelDensity.Solid - 1);
        Assert.Throws<ArgumentException>(() => kinds.Register("faint", faint));

        kinds.Seal(new VoxelGrid(10));
        Assert.True(kinds.IsSealed);
        Assert.Throws<InvalidOperationException>(() => kinds.Register("tree", Ball(5, 10)));
    }

    /// <summary>A world must have coarser voxels than every kind's: a kind of 10 cm voxels in a 10 cm world is refused.</summary>
    [Fact]
    public void Sealing_refuses_a_kind_not_finer_than_the_world()
    {
        var kinds = new VoxelObjectKinds();
        kinds.Register("rock", Ball(5, 10));
        kinds.Seal(new VoxelGrid(10));

        var coarse = new VoxelObjectKinds();
        coarse.Register("boulder", Ball(10, 10));
        var error = Assert.Throws<InvalidOperationException>(() => coarse.Seal(new VoxelGrid(10)));
        Assert.Contains("boulder", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A shape 200 x 300 x 60 voxels is cut into parts that own 112 voxels a side from its corner's multiples of 112, each
    /// with a border of its neighbours' 8 voxels round it (its model's box); a part owning nothing is left out, and each
    /// side with no part past it is open.
    /// </summary>
    [Fact]
    public void A_kind_is_split_into_parts_of_at_most_128_voxels_without_the_empty_ones()
    {
        var shape = new VoxelObjectShape(2, new VoxelCoord(200, 300, 60), new VoxelCoord(200, 0, 60));
        // A trunk up through the parts of x 0, and a crown across the top ones.
        shape.FillCapsule(100, 0, 60, 100, 500, 60, 30, Bark);
        shape.FillEllipsoid(200, 520, 60, 180, 60, 50, Leaves);
        var kinds = new VoxelObjectKinds();
        var kind = kinds[kinds.Register("tree", shape)];

        // Parts at x 0 and 112, y 0, 112, and 224, z 0: the trunk (x 35 to 65 voxels) is in x 0's parts only, the crown
        // (y 230 to 290 voxels, x 10 to 190) in y 224's alone.
        var offsets = kind.Parts.Select(part => part.Offset).ToHashSet();
        Assert.Equal(
            new[]
            {
                new VoxelCoord(0, 0, 0),
                new VoxelCoord(0, 112, 0),
                new VoxelCoord(0, 224, 0),
                new VoxelCoord(112, 224, 0),
            },
            offsets.OrderBy(o => o.Y).ThenBy(o => o.X)
        );
        Assert.Equal(4, kind.PartCount);
        var corner = kind.Parts.Single(part => part.Offset == new VoxelCoord(112, 224, 0));
        Assert.Equal(new VoxelCoord(88, 76, 60), corner.OwnSize);
        Assert.Equal(new VoxelCoord(104, 92, 76), corner.Size);
        Assert.Equal(new VoxelCoord(52, 46, 38), corner.SizeAt(1));
        Assert.Equal(new VoxelCoord(26, 23, 19), corner.SizeAt(2));
        // Beside it at x 0 lies the part at (0, 224, 0); nothing below it (the part at (112, 112, 0) owns nothing) or in
        // front of it.
        Assert.Equal(new VoxelCoord(0, 1, 1), corner.OpenLow);
        Assert.Equal(new VoxelCoord(1, 1, 1), kind.Parts.Single(part => part.Offset == default).OpenLow);
    }

    /// <summary>
    /// A part's level-0 voxels are the shape's from 8 before its corner (its border); a level-1 or level-2 voxel holds the
    /// mean density of the 8 or 64 it covers, solid from one half, of the material at its sample point where that is solid.
    /// </summary>
    [Fact]
    public void A_parts_coarser_levels_hold_the_mean_density_of_what_they_cover()
    {
        var shape = Ball(4, 40);
        var kinds = new VoxelObjectKinds();
        var kind = kinds[kinds.Register("rock", shape)];
        var part = Assert.Single(kind.Parts);
        var materials = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        for (var level = 0; level < VoxelModels.MaxLevels; level++)
        {
            part.Decode(level, materials, densities);
            var step = 1 << level;
            var size = part.SizeAt(level);
            for (var z = 0; z < size.Z; z++)
            {
                for (var y = 0; y < size.Y; y++)
                {
                    for (var x = 0; x < size.X; x++)
                    {
                        var sum = 0;
                        for (var k = 0; k < step * step * step; k++)
                        {
                            sum += shape.DensityAt(
                                x * step + k % step - 8,
                                y * step + k / step % step - 8,
                                z * step + k / (step * step) - 8
                            );
                        }

                        var cells = step * step * step;
                        var mean = (sum + cells / 2) / cells;
                        var i = x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z);
                        Assert.Equal(mean, densities[i]);
                        Assert.Equal(mean >= VoxelDensity.Solid ? Leaves : VoxelConstants.Air, materials[i]);
                    }
                }
            }
        }
    }

    /// <summary>Peers whose kinds hold the same voxels in the same order agree; one voxel or the order apart, they do not.</summary>
    [Fact]
    public void The_fingerprint_stands_for_every_kinds_voxels_and_their_order()
    {
        static VoxelObjectKinds Make(bool swap, bool dent)
        {
            var kinds = new VoxelObjectKinds();
            var rock = Ball(5, 10);
            if (dent)
            {
                rock.Clear(5, 5, 5);
            }

            if (swap)
            {
                kinds.Register("bush", Ball(4, 12));
                kinds.Register("rock", rock);
            }
            else
            {
                kinds.Register("rock", rock);
                kinds.Register("bush", Ball(4, 12));
            }

            return kinds;
        }

        var first = Make(swap: false, dent: false).Fingerprint;
        Assert.Equal(first, Make(swap: false, dent: false).Fingerprint);
        Assert.NotEqual(first, Make(swap: true, dent: false).Fingerprint);
        Assert.NotEqual(first, Make(swap: false, dent: true).Fingerprint);
        Assert.Equal(0UL, new VoxelObjectKinds().Fingerprint);
    }

    /// <summary>
    /// A kind is copied far away up to half its box's widest side unless it says how far; a negative reach is refused;
    /// and the fingerprint stands for how far a kind is copied too.
    /// </summary>
    [Fact]
    public void A_kinds_copies_reach_half_its_widest_side_unless_it_says_and_count_in_the_fingerprint()
    {
        var kinds = new VoxelObjectKinds();
        var rock = kinds.Register("rock", Ball(5, 10));
        var far = kinds.Register("far rock", Ball(5, 10), copiesUpToCentimetres: 200);

        Assert.Equal(25, kinds[rock].CopiesUpToCentimetres);
        Assert.Equal(200, kinds[far].CopiesUpToCentimetres);
        Assert.Throws<ArgumentOutOfRangeException>(() => kinds.Register("bad", Ball(5, 10), -1));

        var near = new VoxelObjectKinds();
        near.Register("rock", Ball(5, 10));
        var farther = new VoxelObjectKinds();
        farther.Register("rock", Ball(5, 10), copiesUpToCentimetres: 300);
        Assert.NotEqual(near.Fingerprint, farther.Fingerprint);
    }

    /// <summary>A ball of leaves <paramref name="voxels"/> voxels across in voxels of <paramref name="unit"/>, standing on its bottom.</summary>
    private static VoxelObjectShape Ball(int unit, int voxels)
    {
        var half = voxels * unit / 2.0;
        var shape = new VoxelObjectShape(
            unit,
            new VoxelCoord(voxels, voxels, voxels),
            new VoxelCoord((int)half, 0, (int)half)
        );
        shape.FillEllipsoid(half, half, half, half - unit, half - unit, half - unit, Leaves);
        return shape;
    }
}
