using System.Numerics;
using Itera.Engine.Maths;

namespace Itera.Engine.Tests.Maths;

public sealed class RotationTests
{
    private const double Tolerance = 1e-12;

    [Fact]
    public void Turns_about_each_axis_take_one_axis_to_the_next()
    {
        AssertNear((0, 0, 1), Rotation.AboutX(0.25).Apply(0, 1, 0));
        AssertNear((1, 0, 0), Rotation.AboutY(0.25).Apply(0, 0, 1));
        AssertNear((0, 1, 0), Rotation.AboutZ(0.25).Apply(1, 0, 0));
    }

    [Fact]
    public void Facing_turns_the_front_towards_the_direction()
    {
        AssertNear((0.6, 0, 0.8), Rotation.Facing(3, 4).Apply(0, 0, -1));
        AssertNear((0, 0, -1), Rotation.Facing(0, -2).Apply(0, 0, -1));
    }

    [Fact]
    public void Frame_puts_x_along_the_first_vector_and_z_towards_the_second()
    {
        var frame = Rotation.Frame(0, 2, 0, 1, 1, 0);
        AssertNear((0, 1, 0), frame.Apply(1, 0, 0));
        AssertNear((1, 0, 0), frame.Apply(0, 0, 1));
        AssertNear((0, 0, 1), frame.Apply(0, 1, 0));
    }

    [Fact]
    public void Times_applies_the_first_rotation_first_and_the_transpose_undoes_it()
    {
        var first = Rotation.AboutX(0.1);
        var second = Rotation.AboutY(0.3);
        var both = second.Times(first);
        var (x, y, z) = first.Apply(0.2, -0.5, 0.7);
        AssertNear(second.Apply(x, y, z), both.Apply(0.2, -0.5, 0.7));
        AssertNear((0.2, -0.5, 0.7), both.Transposed().Times(both).Apply(0.2, -0.5, 0.7));
    }

    [Fact]
    public void Mirrored_keeps_the_swing_in_the_mirror_plane_and_reverses_the_rest()
    {
        AssertNear(Rotation.AboutX(0.1).Apply(0, -1, 0), Rotation.AboutX(0.1).Mirrored().Apply(0, -1, 0));
        AssertNear(Rotation.AboutZ(-0.1).Apply(0, -1, 0), Rotation.AboutZ(0.1).Mirrored().Apply(0, -1, 0));
    }

    [Fact]
    public void Blend_meets_both_ends_and_takes_the_shorter_way()
    {
        var from = Rotation.AboutY(0.1);
        var to = Rotation.AboutY(0.3);
        AssertNear(from.Apply(1, 0, 0), Rotation.Blend(from, to, 0).Apply(1, 0, 0));
        AssertNear(to.Apply(1, 0, 0), Rotation.Blend(from, to, 1).Apply(1, 0, 0));
        AssertNear(Rotation.AboutY(0.2).Apply(1, 0, 0), Rotation.Blend(from, to, 0.5).Apply(1, 0, 0));
        AssertNear(
            Rotation.AboutY(0).Apply(1, 0, 0),
            Rotation.Blend(Rotation.AboutY(0.95), Rotation.AboutY(0.05), 0.5).Apply(1, 0, 0)
        );
    }

    [Fact]
    public void The_quaternion_turns_a_vector_as_the_matrix_does()
    {
        var rotation = Rotation.AboutZ(0.07).Times(Rotation.AboutX(-0.2)).Times(Rotation.AboutY(0.4));
        var turned = Vector3.Transform(new Vector3(0.3f, -0.6f, 0.9f), rotation.ToQuaternion());
        var (x, y, z) = rotation.Apply(0.3, -0.6, 0.9);
        Assert.Equal(x, turned.X, 1e-5);
        Assert.Equal(y, turned.Y, 1e-5);
        Assert.Equal(z, turned.Z, 1e-5);
    }

    [Fact]
    public void A_rigid_transform_and_its_inverse_cancel()
    {
        var transform = new RigidTransform(Rotation.AboutY(0.15), 10, -4, 2.5);
        var (x, y, z) = transform.Apply(1, 2, 3);
        AssertNear((1, 2, 3), transform.Inverse().Apply(x, y, z));
        var moved = RigidTransform.Move(5, 0, 0).Times(transform);
        AssertNear((x + 5, y, z), moved.Apply(1, 2, 3));
    }

    private static void AssertNear((double X, double Y, double Z) expected, (double X, double Y, double Z) actual)
    {
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Y, actual.Y, Tolerance);
        Assert.Equal(expected.Z, actual.Z, Tolerance);
    }
}
