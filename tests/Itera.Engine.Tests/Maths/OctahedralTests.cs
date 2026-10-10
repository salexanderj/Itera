using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Tests.Maths;

public sealed class OctahedralTests
{
    private const int Count = 1000;

    /// <summary>Points spread over the sphere by the golden angle, so every region and both hemispheres are covered.</summary>
    private static Vector3[] FibonacciDirections()
    {
        var result = new Vector3[Count];
        var golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var i = 0; i < Count; i++)
        {
            var z = 1f - 2f * (i + 0.5f) / Count;
            var radius = MathF.Sqrt(1f - z * z);
            var angle = golden * i;
            result[i] = new Vector3(radius * MathF.Cos(angle), radius * MathF.Sin(angle), z);
        }

        return result;
    }

    private static Vector3[] RandomDirections()
    {
        var random = new Random(12345);
        var result = new Vector3[Count];
        for (var i = 0; i < Count; i++)
        {
            Vector3 v;
            do
            {
                v = new Vector3(
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1)
                );
            } while (v.LengthSquared() < 0.01f);

            result[i] = Vector3.Normalize(v);
        }

        return result;
    }

    private static Vector3[] Directions(int which) => which == 0 ? FibonacciDirections() : RandomDirections();

    private static float AngleBetween(Vector3 a, Vector3 b) =>
        MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));

    public static TheoryData<int> Sets => new() { 0, 1 };

    [Theory]
    [MemberData(nameof(Sets))]
    public void Snorm16_round_trips_within_a_fraction_of_a_milliradian(int which)
    {
        foreach (var direction in Directions(which))
        {
            var back = Octahedral.UnpackSnorm16(Octahedral.PackSnorm16(direction));
            Assert.True(AngleBetween(direction, back) < 2e-4f, $"{direction} came back as {back}");
        }
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Tangent_round_trips_within_a_fraction_of_a_milliradian(int which)
    {
        foreach (var direction in Directions(which))
        {
            var back = Octahedral.UnpackTangent(Octahedral.PackTangent(direction, 1f), out _);
            Assert.True(AngleBetween(direction, back) < 4e-4f, $"{direction} came back as {back}");
        }
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Unquantised_encoding_round_trips_and_stays_in_the_square(int which)
    {
        foreach (var direction in Directions(which))
        {
            var encoded = Octahedral.Encode(direction);
            Assert.InRange(encoded.X, -1f, 1f);
            Assert.InRange(encoded.Y, -1f, 1f);
            Assert.True(AngleBetween(direction, Octahedral.Decode(encoded)) < 1e-5f);
        }
    }

    [Fact]
    public void The_six_axes_round_trip_exactly()
    {
        Vector3[] axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        foreach (var axis in axes)
        {
            Assert.Equal(axis, Octahedral.Decode(Octahedral.Encode(axis)));
            Assert.Equal(axis, Octahedral.UnpackSnorm16(Octahedral.PackSnorm16(axis)));
            Assert.Equal(axis, Octahedral.UnpackTangent(Octahedral.PackTangent(axis, 1f), out _));
        }
    }

    [Fact]
    public void PackSnorm16_equals_the_normal_word_a_mesh_vertex_stores()
    {
        foreach (var direction in FibonacciDirections().Concat(RandomDirections()))
        {
            var vertex = MeshVertex.Pack(Vector3.Zero, direction, 0, 255);
            Assert.Equal(vertex.Normal, Octahedral.PackSnorm16(direction));
            Assert.Equal(MeshVertex.DecodeNormal(vertex), Octahedral.UnpackSnorm16(vertex.Normal));
        }

        var unnormalised = new Vector3(3f, -4f, -12f);
        Assert.Equal(MeshVertex.Pack(Vector3.Zero, unnormalised, 0, 255).Normal, Octahedral.PackSnorm16(unnormalised));
    }

    [Fact]
    public void A_zero_direction_decodes_to_plus_z()
    {
        Assert.Equal(Vector2.Zero, Octahedral.Encode(Vector3.Zero));
        Assert.Equal(Vector3.UnitZ, Octahedral.Decode(Octahedral.Encode(Vector3.Zero)));
        Assert.Equal(0u, Octahedral.PackSnorm16(Vector3.Zero));
        Assert.Equal(Vector3.UnitZ, Octahedral.UnpackSnorm16(0u));
        Assert.Equal(Vector3.UnitZ, Octahedral.UnpackTangent(Octahedral.PackTangent(Vector3.Zero, 1f), out _));
        Assert.Equal(Vector2.Zero, Octahedral.Encode(new Vector3(float.NaN, 1f, 0f)));
    }

    [Fact]
    public void Handedness_is_the_top_bit_and_reads_back()
    {
        var direction = Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f));
        var positive = Octahedral.PackTangent(direction, 1f);
        var negative = Octahedral.PackTangent(direction, -1f);

        Assert.Equal(0u, positive >> 31);
        Assert.Equal(1u, negative >> 31);
        Assert.Equal(positive | 0x8000_0000u, negative);

        var a = Octahedral.UnpackTangent(positive, out var handedPositive);
        var b = Octahedral.UnpackTangent(negative, out var handedNegative);
        Assert.Equal(1f, handedPositive);
        Assert.Equal(-1f, handedNegative);
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_second_tangent_component_is_a_signed_fifteen_bit_value()
    {
        // The encoded v of -y is -1, which stores as 0x4001 in 15 bits.
        var packed = Octahedral.PackTangent(-Vector3.UnitY, 1f);
        Assert.Equal(0x4001u, packed >> 16);
        Assert.Equal(0u, packed & 0xFFFFu);
    }
}
