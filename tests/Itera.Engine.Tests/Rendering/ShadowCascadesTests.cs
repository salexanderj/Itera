using System.Numerics;
using Itera.Engine.Rendering;

namespace Itera.Engine.Tests.Rendering;

public sealed class ShadowCascadesTests
{
    private const int Map = 2048;
    private const float Near = 12f;
    private const float Far = 60f;
    private const float Caster = 80f;
    private const float Epsilon = 1e-4f;

    private static readonly float TanHalf = MathF.Tan(MathF.PI / 6);
    private const float Aspect = 16f / 9f;

    private static float Angle(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;

    private static float Distance(Matrix4x4 a, Matrix4x4 b)
    {
        var most = 0f;
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                most = MathF.Max(most, MathF.Abs(a[row, column] - b[row, column]));
            }
        }

        return most;
    }

    private static Vector3 Sun(float elevationDegrees, float azimuthDegrees)
    {
        var e = elevationDegrees * MathF.PI / 180;
        var a = azimuthDegrees * MathF.PI / 180;
        return new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Sin(e), MathF.Cos(e) * MathF.Sin(a));
    }

    private static void Camera(
        float yawDegrees,
        float pitchDegrees,
        out Vector3 forward,
        out Vector3 right,
        out Vector3 up
    )
    {
        var yaw = yawDegrees * MathF.PI / 180;
        var pitch = pitchDegrees * MathF.PI / 180;
        forward = new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Sin(yaw));
        var r = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var u = Vector3.Cross(r, forward);
        right = r * (TanHalf * Aspect);
        up = u * TanHalf;
    }

    private static CascadeFit Fit(Vector3 position, float yaw, float pitch, Vector3 sun)
    {
        Camera(yaw, pitch, out var f, out var r, out var u);
        return ShadowCascades.Fit(position, f, r, u, Near, Far, sun, Map, Caster);
    }

    private static Vector3 Ndc(in CascadeFit fit, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), fit.ViewProjection);
        Assert.Equal(1f, c.W, 1e-6f);
        return new Vector3(c.X, c.Y, c.Z);
    }

    private static Vector3[] SliceCorners(Vector3 position, float yaw, float pitch)
    {
        Camera(yaw, pitch, out var f, out var r, out var u);
        var corners = new Vector3[8];
        var n = 0;
        foreach (var d in new[] { Near, Far })
        {
            foreach (var sr in new[] { -1f, 1f })
            {
                foreach (var su in new[] { -1f, 1f })
                {
                    corners[n++] = position + (d * (f + (sr * r) + (su * u)));
                }
            }
        }

        return corners;
    }

    private static Vector3 Centre(Vector3[] corners)
    {
        var sum = Vector3.Zero;
        foreach (var c in corners)
        {
            sum += c;
        }

        return sum / corners.Length;
    }

    private static void AssertCornersInside(Vector3 position, float yaw, float pitch, Vector3 sun)
    {
        var fit = Fit(position, yaw, pitch, sun);
        foreach (var c in SliceCorners(position, yaw, pitch))
        {
            var n = Ndc(in fit, c);
            Assert.True(float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z));
            Assert.InRange(n.X, -1 - Epsilon, 1 + Epsilon);
            Assert.InRange(n.Y, -1 - Epsilon, 1 + Epsilon);
            Assert.InRange(n.Z, -Epsilon, 1 + Epsilon);
        }
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(37f, 20f)]
    [InlineData(200f, -45f)]
    [InlineData(95f, 80f)]
    [InlineData(-120f, -80f)]
    public void Every_slice_corner_maps_inside_the_cascade(float yaw, float pitch)
    {
        AssertCornersInside(new Vector3(120.3f, 14.7f, -80.1f), yaw, pitch, Sun(50, 30));
    }

    /// <summary>
    /// An orthographic view's slice is a box, its sides as far apart at every depth, and its depths may lie behind the
    /// position (the view's middle).
    /// </summary>
    [Theory]
    [InlineData(0f, -89f)]
    [InlineData(45f, -35f)]
    [InlineData(200f, 10f)]
    public void Every_corner_of_an_orthographic_box_maps_inside_the_cascade(float yaw, float pitch)
    {
        Camera(yaw, pitch, out var f, out var r, out var u);
        var right = Vector3.Normalize(r) * 40;
        var up = Vector3.Normalize(u) * 22.5f;
        var position = new Vector3(10.3f, 30.1f, -5.7f);

        var fit = ShadowCascades.Fit(position, f, right, up, -30, 30, Sun(50, 30), Map, Caster, default, true);

        foreach (var d in (float[])[-30, 30])
        {
            foreach (var sr in (float[])[-1, 1])
            {
                foreach (var su in (float[])[-1, 1])
                {
                    var n = Ndc(in fit, position + d * f + sr * right + su * up);
                    Assert.InRange(n.X, -1 - Epsilon, 1 + Epsilon);
                    Assert.InRange(n.Y, -1 - Epsilon, 1 + Epsilon);
                    Assert.InRange(n.Z, -Epsilon, 1 + Epsilon);
                }
            }
        }

        // The square is the box's bounding sphere (radius 54.83 m, grown by a texel's share and rounded up to a sixteenth),
        // not a frustum's at those depths.
        Assert.Equal(54.875f, fit.TexelMetres * Map / 2, 4);
    }

    [Fact]
    public void Casters_towards_the_sun_and_the_far_side_stay_in_the_depth_range()
    {
        var position = new Vector3(5, 2, 9);
        var sun = Vector3.Normalize(new Vector3(0.3f, 0.7f, -0.4f));
        var fit = Fit(position, 40, -10, sun);
        var corners = SliceCorners(position, 40, -10);
        var centre = Centre(corners);
        var radius = 0f;
        foreach (var c in corners)
        {
            radius = MathF.Max(radius, Vector3.Distance(c, centre));
        }

        var towards = Ndc(in fit, centre + (sun * Caster));
        Assert.InRange(towards.Z, 0f, 1f);
        var far = Ndc(in fit, centre - (sun * radius));
        Assert.InRange(far.Z, 0f, 1f + Epsilon);
        // The box ends where z leaves [0, 1]: a little past the sphere's sun side plus the casters, and past its far side.
        Assert.True(Ndc(in fit, centre + (sun * (Caster + radius + 1))).Z < 0f);
        Assert.True(Ndc(in fit, centre - (sun * (radius + 1))).Z > 1f);
    }

    [Fact]
    public void Turning_the_camera_in_place_keeps_the_texel_size()
    {
        var position = new Vector3(33.3f, 5.5f, -12.2f);
        var sun = Sun(40, 100);
        var first = Fit(position, 0, 0, sun).TexelMetres;
        Assert.True(first > 0);
        foreach (var (yaw, pitch) in new[] { (15f, 0f), (90f, 30f), (180f, -60f), (271f, 75f), (-33f, -10f) })
        {
            Assert.Equal(first, Fit(position, yaw, pitch, sun).TexelMetres);
        }
    }

    [Fact]
    public void The_radius_is_a_multiple_of_a_sixteenth_of_a_metre()
    {
        var fit = Fit(new Vector3(1, 2, 3), 10, 5, Sun(45, 0));
        var radius = fit.TexelMetres * Map / 2;
        Assert.Equal(0.0, (radius * 16) - Math.Round(radius * 16), 1e-3);
    }

    [Fact]
    public void Moving_the_camera_shifts_the_map_by_whole_texels_only()
    {
        var sun = Sun(35, 70);
        var world = new Vector3(21.37f, 1.9f, 16.81f);
        var position = new Vector3(3, 2, 4);
        var steps = new[]
        {
            new Vector3(0.013f, 0, 0.007f),
            new Vector3(0.31f, 0.02f, -0.11f),
            new Vector3(-0.0071f, 0, 0.5f),
            new Vector3(0.9f, -0.04f, 0.23f),
            new Vector3(0.02f, 0, -0.77f),
            new Vector3(1.7f, 0, 1.3f),
        };

        float? firstX = null;
        float? firstY = null;
        foreach (var step in steps)
        {
            position += step;
            var fit = Fit(position, 25, -5, sun);
            var n = Ndc(in fit, world);
            var x = n.X * Map / 2;
            var y = n.Y * Map / 2;
            firstX ??= x;
            firstY ??= y;
            var dx = x - firstX.Value;
            var dy = y - firstY.Value;
            Assert.True(MathF.Abs(dx - MathF.Round(dx)) < 1e-3f, $"x fraction moved by {dx}");
            Assert.True(MathF.Abs(dy - MathF.Round(dy)) < 1e-3f, $"y fraction moved by {dy}");
        }
    }

    [Theory]
    [InlineData(90f, 0f)]
    [InlineData(10f, 0f)]
    [InlineData(10f, 135f)]
    [InlineData(89.5f, 0f)]
    [InlineData(89.5f, 270f)]
    [InlineData(-90f, 0f)]
    [InlineData(-89.5f, 45f)]
    public void Steep_and_flat_suns_give_finite_fits_with_every_corner_inside(float elevation, float azimuth)
    {
        var sun = Sun(elevation, azimuth);
        foreach (var (yaw, pitch) in new[] { (0f, 0f), (123f, -30f), (300f, 60f) })
        {
            AssertCornersInside(new Vector3(-40.5f, 8.2f, 63.9f), yaw, pitch, sun);
        }

        var fit = Fit(new Vector3(1, 1, 1), 0, 0, sun);
        Assert.True(float.IsFinite(fit.TexelMetres));
        Assert.True(float.IsFinite(fit.ViewProjection.M11) && float.IsFinite(fit.ViewProjection.M43));
    }

    [Fact]
    public void Points_nearer_the_sun_get_a_smaller_z()
    {
        var position = new Vector3(10, 3, -4);
        foreach (var sun in new[] { Sun(50, 20), Sun(90, 0), Sun(5, 200) })
        {
            var fit = Fit(position, 60, 0, sun);
            var p = Centre(SliceCorners(position, 60, 0));
            var previous = float.MaxValue;
            for (var k = -20; k <= 20; k += 4)
            {
                var z = Ndc(in fit, p + (sun * k)).Z;
                Assert.True(z < previous, $"z {z} not below {previous} at k {k}");
                previous = z;
            }

            previous = float.MinValue;
            for (var k = 20; k >= -20; k -= 4)
            {
                var z = Ndc(in fit, p + (sun * k)).Z;
                Assert.True(z > previous);
                previous = z;
            }
        }
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        Camera(0, 0, out var f, out var r, out var u);
        var sun = Sun(45, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowCascades.Fit(default, f, r, u, 1, 10, sun, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowCascades.Fit(default, f, r, u, -1, 10, sun, Map, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowCascades.Fit(default, f, r, u, 10, 10, sun, Map, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowCascades.Fit(default, f, r, u, 10, 5, sun, Map, 10));
        // An orthographic box may start behind its position, but must still end past where it starts.
        Assert.True(
            float.IsFinite(ShadowCascades.Fit(default, f, r, u, -1, 10, sun, Map, 10, default, true).TexelMetres)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShadowCascades.Fit(default, f, r, u, 10, -1, sun, Map, 10, default, true)
        );
    }

    /// <summary>
    /// A sun that passes straight overhead turns the maps' image axis with it, a little each step, when the axis is
    /// carried from step to step; the fit's own axis (world up's cross with the sun) turns half round as it passes.
    /// </summary>
    [Fact]
    public void A_sun_passing_overhead_turns_a_carried_image_axis_smoothly()
    {
        var axis = ShadowCascades.ImageAxis(Sun(80, 0), Vector3.Zero);
        var own = axis;
        var mostTurn = 0f;
        var mostOwnTurn = 0f;
        for (var elevation = 80.05f; elevation <= 100; elevation += 0.05f)
        {
            // Past 90 degrees the sun goes on over to the other side.
            var sun = Sun(elevation, 0);
            var next = ShadowCascades.ImageAxis(sun, axis);
            var nextOwn = ShadowCascades.ImageAxis(sun, Vector3.Zero);
            Assert.Equal(0, Vector3.Dot(next, sun), 5);
            Assert.Equal(1, next.Length(), 5);
            mostTurn = MathF.Max(mostTurn, Angle(axis, next));
            mostOwnTurn = MathF.Max(mostOwnTurn, Angle(own, nextOwn));
            axis = next;
            own = nextOwn;
        }

        Assert.True(mostTurn < 0.1f, $"turned {mostTurn} degrees in a step");
        Assert.True(mostOwnTurn > 45, $"the fit's own axis turned at most {mostOwnTurn} degrees");
    }

    [Fact]
    public void An_image_axis_square_to_the_sun_gives_the_fits_own_matrix_when_it_is_the_fits_own()
    {
        Camera(30, -10, out var forward, out var right, out var up);
        var sun = Sun(40, 120);
        var own = ShadowCascades.Fit(new Vector3(5, 2, -3), forward, right, up, Near, Far, sun, Map, Caster);

        var given = ShadowCascades.Fit(
            new Vector3(5, 2, -3),
            forward,
            right,
            up,
            Near,
            Far,
            sun,
            Map,
            Caster,
            ShadowCascades.ImageAxis(sun, Vector3.Zero)
        );

        Assert.True(Distance(own.ViewProjection, given.ViewProjection) < 1e-5f);
        Assert.Equal(own.TexelMetres, given.TexelMetres);
    }

    [Fact]
    public void Fit_allocates_nothing()
    {
        Camera(30, -10, out var f, out var r, out var u);
        var sun = Sun(45, 10);
        var position = new Vector3(4, 2, 7);
        var texels = 0f;
        texels += ShadowCascades.Fit(position, f, r, u, Near, Far, sun, Map, Caster).TexelMetres;

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            texels += ShadowCascades
                .Fit(position + (Vector3.UnitX * i), f, r, u, Near, Far, sun, Map, Caster)
                .TexelMetres;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(texels > 0);
    }
}
