using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// The voxels a game builds an object kind from (<see cref="VoxelObjectShape"/>): each fill's densities put the surface
/// where the shape's is, half a voxel's density at its distance, so a voxel is solid exactly when its centre lies inside;
/// shapes join by the larger density; and the box's rules.
/// </summary>
public sealed class VoxelObjectShapeTests
{
    private const byte Bark = 30;
    private const byte Leaves = 31;

    /// <summary>
    /// A ball of 40 cm in 5 cm voxels: a voxel is solid exactly when its centre lies within the radius, its density
    /// falls from full a voxel inside the surface to empty a voxel outside it, and a voxel whose centre is on the surface
    /// is at one half.
    /// </summary>
    [Fact]
    public void A_balls_voxels_are_solid_inside_it_and_its_densities_cross_one_half_on_its_surface()
    {
        var shape = new VoxelObjectShape(5, new VoxelCoord(20, 20, 20), new VoxelCoord(50, 0, 50));
        shape.FillEllipsoid(50, 50, 50, 40, 40, 40, Leaves);

        for (var z = 0; z < 20; z++)
        {
            for (var y = 0; y < 20; y++)
            {
                for (var x = 0; x < 20; x++)
                {
                    double dx = (x + 0.5) * 5 - 50,
                        dy = (y + 0.5) * 5 - 50,
                        dz = (z + 0.5) * 5 - 50;
                    var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz) - 40;
                    var density = shape.DensityAt(x, y, z);
                    var expected = Math.Clamp(0.5 - distance / 5, 0, 1) * VoxelDensity.Full;
                    Assert.InRange(density, expected - 1, expected + 1);
                    Assert.Equal(
                        density >= VoxelDensity.Solid ? Leaves : VoxelConstants.Air,
                        shape.MaterialAt(x, y, z)
                    );
                }
            }
        }
    }

    /// <summary>
    /// A trunk and a crown joined: where both reach, the voxel keeps the larger density and the material of the shape that
    /// fills it more, so the trunk's inside stays bark under the crown's edge.
    /// </summary>
    [Fact]
    public void Shapes_join_by_the_larger_density()
    {
        var shape = new VoxelObjectShape(4, new VoxelCoord(50, 100, 50), new VoxelCoord(100, 0, 100));
        shape.FillCapsule(100, 0, 100, 100, 300, 100, 20, Bark);
        shape.FillEllipsoid(100, 320, 100, 90, 70, 90, Leaves);

        // The trunk's axis at 1 m: bark, full.
        Assert.Equal(Bark, shape.MaterialAt(25, 25, 25));
        Assert.Equal(VoxelDensity.Full, shape.DensityAt(25, 25, 25));
        // Inside the crown, off the trunk: leaves, full.
        Assert.Equal(Leaves, shape.MaterialAt(35, 80, 25));
        // Where the trunk's top meets the crown's underside (y = 250 to 300 cm), the trunk's axis is full bark.
        Assert.Equal(Bark, shape.MaterialAt(25, 68, 25));
        // Every voxel's density is the larger of the two shapes' alone.
        var trunk = new VoxelObjectShape(4, new VoxelCoord(50, 100, 50), new VoxelCoord(100, 0, 100));
        trunk.FillCapsule(100, 0, 100, 100, 300, 100, 20, Bark);
        var crown = new VoxelObjectShape(4, new VoxelCoord(50, 100, 50), new VoxelCoord(100, 0, 100));
        crown.FillEllipsoid(100, 320, 100, 90, 70, 90, Leaves);
        for (var z = 0; z < 50; z += 3)
        {
            for (var y = 0; y < 100; y += 3)
            {
                for (var x = 0; x < 50; x += 3)
                {
                    Assert.Equal(
                        Math.Max(trunk.DensityAt(x, y, z), crown.DensityAt(x, y, z)),
                        shape.DensityAt(x, y, z)
                    );
                }
            }
        }
    }

    /// <summary>A box's voxels are full inside it, empty more than half a voxel outside it, and half on its faces.</summary>
    [Fact]
    public void A_boxs_faces_are_where_its_densities_cross_one_half()
    {
        var shape = new VoxelObjectShape(2, new VoxelCoord(30, 30, 30), default);
        shape.FillBox(10, 10, 10, 41, 50, 30, Bark);

        Assert.Equal(VoxelDensity.Full, shape.DensityAt(10, 10, 10));
        Assert.Equal(VoxelDensity.Empty, shape.DensityAt(3, 10, 10));
        // Voxel 5's centre is at 11 cm, 1 cm inside the face at 10 cm: 0.5 + 1/2 of the way.
        Assert.Equal(VoxelDensity.Full, shape.DensityAt(5, 10, 10));
        // Voxel 4's centre is at 9 cm, 1 cm outside it: 0.5 - 1/2, empty.
        Assert.Equal(VoxelDensity.Empty, shape.DensityAt(4, 10, 10));
        // The far x face at 41 cm runs through voxel 20's centre: one half.
        Assert.InRange(shape.DensityAt(20, 10, 10), 127, 128);
        Assert.Equal(VoxelConstants.Air, shape.MaterialAt(25, 10, 10));
    }

    /// <summary>A voxel set or cleared takes the material and a density fitted to it; outside the box reads air.</summary>
    [Fact]
    public void Set_and_clear_fit_the_density_to_the_material()
    {
        var shape = new VoxelObjectShape(5, new VoxelCoord(4, 4, 4), default);
        shape.Set(1, 2, 3, Bark, 40);
        Assert.Equal(Bark, shape.MaterialAt(1, 2, 3));
        Assert.Equal(VoxelDensity.Solid, shape.DensityAt(1, 2, 3));
        shape.Clear(1, 2, 3);
        Assert.Equal(VoxelConstants.Air, shape.MaterialAt(1, 2, 3));
        Assert.Equal(VoxelDensity.Empty, shape.DensityAt(1, 2, 3));
        Assert.Equal(VoxelConstants.Air, shape.MaterialAt(-1, 0, 0));
        Assert.Equal(VoxelDensity.Empty, shape.DensityAt(4, 0, 0));
    }

    [Fact]
    public void The_box_and_the_fills_refuse_what_cannot_be()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoxelObjectShape(0, new VoxelCoord(1, 1, 1), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoxelObjectShape(101, new VoxelCoord(1, 1, 1), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoxelObjectShape(5, new VoxelCoord(0, 1, 1), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoxelObjectShape(5, new VoxelCoord(1025, 1, 1), default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VoxelObjectShape(5, new VoxelCoord(1024, 1024, 128), default)
        );

        var shape = new VoxelObjectShape(5, new VoxelCoord(4, 4, 4), default);
        Assert.Throws<ArgumentOutOfRangeException>(() => shape.Set(4, 0, 0, Bark, 255));
        Assert.Throws<ArgumentException>(() => shape.FillBox(0, 0, 0, 5, 5, 5, VoxelConstants.Air));
        Assert.Throws<ArgumentException>(() => shape.FillBox(5, 0, 0, 5, 5, 5, Bark));
        Assert.Throws<ArgumentOutOfRangeException>(() => shape.FillEllipsoid(0, 0, 0, 0, 5, 5, Bark));
        Assert.Throws<ArgumentOutOfRangeException>(() => shape.FillCapsule(0, 0, 0, 0, 5, 0, 0, Bark));
    }
}
