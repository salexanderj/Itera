using System.Globalization;
using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// One shader compiler for <see cref="ViewConstantsTests"/>, each shader compiled once: every test made its own compiler
/// with an empty cache, so one shader was compiled three times.
/// </summary>
public sealed class CompiledShaders : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;
    private readonly Dictionary<string, byte[]> _compiled = new(StringComparer.Ordinal);

    public CompiledShaders()
    {
        // The default source directory is Shaders/ next to the test assembly, where the engine's shader files are copied.
        _compiler = new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, _cache);
        _compiler.Initialize();
    }

    /// <summary>The SPIR-V of <paramref name="file"/>, compiled on first use.</summary>
    public byte[] Compile(string file)
    {
        if (!_compiled.TryGetValue(file, out var spirv))
        {
            spirv = _compiler.Compile(file);
            _compiled[file] = spirv;
        }

        return spirv;
    }

    public void Dispose()
    {
        _compiler.Dispose();
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }
}

public sealed class ViewConstantsTests(CompiledShaders compiler) : IClassFixture<CompiledShaders>
{
    private readonly CompiledShaders _compiler = compiler;

    /// <summary>Every shader that reads the view's push constants.</summary>
    [Theory]
    [InlineData("mesh.vert")]
    [InlineData("mesh.frag")]
    [InlineData("world-sky.frag")]
    [InlineData("debug-lines.vert")]
    [InlineData("particle-cubes.vert")]
    [InlineData("particle-cubes.frag")]
    [InlineData("particle-sprites.vert")]
    public void The_push_constant_block_in_the_shaders_matches_the_struct(string shader)
    {
        var spirv = _compiler.Compile(shader);

        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(spirv));
    }

    [Fact]
    public void The_fullscreen_vertex_shader_compiles()
    {
        Assert.NotEmpty(_compiler.Compile("fullscreen.vert"));
    }

    [Fact]
    public void Image_edges_are_the_field_of_view_and_the_aspect_ratio()
    {
        var camera = CameraView.Default with { VerticalFovRadians = MathF.PI / 2 };

        var atmosphere = new Atmosphere(new Vector3(0.5f, 0.6f, 0.7f), new Vector3(0.9f, 0.4f, 0.3f), 0.2f, 1f, 0.6f);

        var view = ViewConstants.Create(in camera, 1920, 1080, 1, Vector3.UnitY, in atmosphere, 17.28f, true, default);

        // Default orientation looks along -Z. A 90 degree field of view puts the top edge at 45 degrees up.
        Assert.Equal(-Vector3.UnitZ, view.Forward);
        AssertClose(Vector3.UnitY, view.Up);
        AssertClose(Vector3.UnitX * (1920f / 1080f), view.Right);
        var topEdge = Vector3.Normalize(view.Forward + view.Up);
        Assert.Equal(45f, MathF.Acos(Vector3.Dot(topEdge, view.Forward)) * 180 / MathF.PI, 3);
        Assert.Equal(1u, view.Slot);
        Assert.Equal(17.28f, view.MaxDistance);
        Assert.Equal(ViewSwitches.Shadows, view.Switches);
        Assert.Equal(atmosphere.SkyHorizon, view.SkyHorizon);
        Assert.Equal(atmosphere.SunColour, view.SunColour);
        Assert.Equal(atmosphere.PackAir(), view.Air);
    }

    [Fact]
    public void A_render_scale_draws_a_smaller_image_with_the_screen_s_aspect_ratio()
    {
        var atmosphere = Atmosphere.Clear(Vector3.One);

        var full = ViewConstants.Create(
            CameraView.Default,
            2560,
            1440,
            0,
            Vector3.UnitY,
            in atmosphere,
            10,
            true,
            default
        );
        var scaled = ViewConstants.Create(
            CameraView.Default,
            2560,
            1440,
            0,
            Vector3.UnitY,
            in atmosphere,
            10,
            false,
            default,
            0.67f
        );

        Assert.Equal((2560u, 1440u), (full.Width, full.Height));
        Assert.Equal((1715u, 965u), (scaled.Width, scaled.Height));
        Assert.False(scaled.Has(ViewSwitches.Shadows));
        // The rays span the same view: 965 rows would round the aspect ratio otherwise.
        Assert.Equal(full.Right, scaled.Right);
        Assert.Equal(full.Up, scaled.Up);
    }

    [Fact]
    public void A_perspective_lens_draws_from_its_own_near_plane_with_no_far_one()
    {
        var camera = CameraView.Default with { NearMetres = 0.2f };

        var view = ViewConstants.Create(
            in camera,
            1600,
            900,
            0,
            Vector3.UnitY,
            Atmosphere.Clear(Vector3.One),
            256,
            true,
            default
        );

        Assert.False(view.Orthographic);
        Assert.Equal((0.2f, 0f), (view.Near, view.Far));
        // view.glsl's viewClip and viewDepthOf: the near distance over the depth, and back.
        var clip = view.Clip(new Vector3(1, 2, -8));
        Assert.Equal(8f, clip.W, 5);
        Assert.Equal(0.2f / 8, clip.Z / clip.W, 6);
        Assert.Equal(8f, view.DepthOf(clip.Z / clip.W), 4);
    }

    [Fact]
    public void An_orthographic_lens_gives_half_its_size_in_metres_and_depth_falling_evenly_from_near_to_far()
    {
        var camera = CameraView.Default with
        {
            Position = new Vector3(5, 20, -3),
            Projection = CameraProjection.Orthographic,
            HeightMetres = 64,
            NearMetres = -100,
            FarMetres = 150,
        };

        var view = ViewConstants.Create(
            in camera,
            1600,
            900,
            0,
            Vector3.UnitY,
            Atmosphere.Clear(Vector3.One),
            256,
            true,
            default
        );

        Assert.True(view.Orthographic);
        Assert.Equal(ViewSwitches.Shadows | ViewSwitches.Orthographic, view.Switches);
        AssertClose(Vector3.UnitY * 32, view.Up);
        AssertClose(Vector3.UnitX * (32 * 1600f / 900f), view.Right);
        Assert.Equal((-100f, 150f), (view.Near, view.Far));
        // The image's top-right corner at any depth is (1, 1); the depth stored is 1 at the near end, 0 at the far one,
        // and 0.8 at 50 m behind the middle.
        var corner = view.Clip(view.Right + view.Up - 50 * view.Forward);
        Assert.Equal(1f, corner.W);
        Assert.Equal(1f, corner.X, 5);
        Assert.Equal(1f, corner.Y, 5);
        Assert.Equal(0.8f, corner.Z, 6);
        Assert.Equal(-50f, view.DepthOf(corner.Z), 3);
        Assert.Equal(1f, view.Clip(-100 * view.Forward).Z, 6);
        Assert.Equal(0f, view.Clip(150 * view.Forward).Z, 6);
    }

    [Fact]
    public void A_lens_that_cannot_be_drawn_gives_way()
    {
        var air = Atmosphere.Clear(Vector3.One);
        var perspective = CameraView.Default with { NearMetres = 0 };
        var orthographic = CameraView.Default with
        {
            Projection = CameraProjection.Orthographic,
            HeightMetres = 0,
            NearMetres = float.NaN,
            FarMetres = float.NaN,
        };
        var inverted = orthographic with { HeightMetres = 10, NearMetres = 10, FarMetres = 5 };

        var near = ViewConstants.Create(in perspective, 100, 100, 0, Vector3.UnitY, in air, 256, false, default);
        var box = ViewConstants.Create(in orthographic, 100, 100, 0, Vector3.UnitY, in air, 256, false, default);
        var turned = ViewConstants.Create(in inverted, 100, 100, 0, Vector3.UnitY, in air, 256, false, default);

        Assert.Equal(ViewConstants.DefaultNearMetres, near.Near);
        Assert.Equal(0.5f, box.Up.Length(), 5);
        Assert.Equal((-256f, 256f), (box.Near, box.Far));
        Assert.Equal((10f, 266f), (turned.Near, turned.Far));

        // Too far or too large for a float's depths: kept within MostLensMetres, the far still past the near.
        var huge = orthographic with
        {
            HeightMetres = 1e38f,
            NearMetres = 3e9f,
            FarMetres = float.NaN,
        };
        var bounded = ViewConstants.Create(in huge, 100, 100, 0, Vector3.UnitY, in air, 256, false, default);
        Assert.Equal(ViewConstants.MostLensMetres / 2, bounded.Up.Length(), 0);
        Assert.Equal((ViewConstants.MostLensMetres - 1, ViewConstants.MostLensMetres), (bounded.Near, bounded.Far));
        Assert.True(float.IsFinite(bounded.Clip(Vector3.Zero).Z));

        // A field of view that cannot be drawn is 90 degrees.
        foreach (var field in (float[])[0, -1, MathF.PI, float.NaN])
        {
            var wide = ViewConstants.Create(
                CameraView.Default with
                {
                    VerticalFovRadians = field,
                },
                100,
                100,
                0,
                Vector3.UnitY,
                in air,
                256,
                false,
                default
            );
            Assert.Equal(1f, wide.Up.Length(), 5);
        }
    }

    /// <summary>
    /// The shaders project and read depth back as <see cref="ViewConstants.Clip"/> and <see cref="ViewConstants.DepthOf"/>
    /// do (view.glsl, and particle-simulate.comp's own copy over its push constants): the tests of those two stand for
    /// the shaders only while their formulas are the same.
    /// </summary>
    [Fact]
    public void The_shaders_project_and_read_depth_as_the_constants_do()
    {
        var view = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "view.glsl"));
        var particles = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "particle-simulate.comp"));

        Assert.Contains(
            "return vec4(x, y, (view.far - depth) / (view.far - view.near), 1.0);",
            view,
            StringComparison.Ordinal
        );
        Assert.Contains("return vec4(x, y, view.near, depth);", view, StringComparison.Ordinal);
        Assert.Contains(
            "return viewOrthographic() ? view.far - stored * (view.far - view.near) : view.near / stored;",
            view,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "return orthographic() ? push.far - stored * (push.far - push.near) : push.near / stored;",
            particles,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void The_shaders_name_the_same_switch_bits()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "view.glsl"));
        var named = System
            .Text.RegularExpressions.Regex.Matches(source, @"const uint View(\w+) = (\d+)u;")
            .ToDictionary(
                match => match.Groups[1].Value,
                match => uint.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
            );

        foreach (var value in Enum.GetValues<ViewSwitches>().Where(value => value != ViewSwitches.None))
        {
            Assert.Equal((uint)value, named[value.ToString()]);
        }

        Assert.Equal(Enum.GetValues<ViewSwitches>().Length - 1, named.Count);
    }

    [Theory]
    [InlineData(2560u, 1f, 2560u)]
    [InlineData(2560u, 0.75f, 1920u)]
    [InlineData(1440u, 0.5f, 720u)]
    [InlineData(1u, 0.5f, 1u)]
    [InlineData(0u, 0.5f, 0u)]
    [InlineData(100u, 2f, 100u)]
    // Never below the least scale.
    [InlineData(100u, 0.1f, 50u)]
    public void The_render_scale_rounds_to_whole_pixels_within_its_range(uint size, float scale, uint expected)
    {
        Assert.Equal(expected, RenderScale.Apply(size, scale));
    }

    [Fact]
    public void Air_packs_haze_overcast_and_grade_a_byte_each_from_the_lowest()
    {
        var atmosphere = new Atmosphere(Vector3.One, Vector3.One, 0.2f, 1f, 0.6f);

        // 0.2 * 255 = 51, 0.6 * 255 = 153; values outside 0 to 1 are clamped.
        Assert.Equal(51u | 255u << 8 | 153u << 16, atmosphere.PackAir());
        Assert.Equal(255u, (atmosphere with { Haze = 3f, Overcast = -1f, Grade = 0f }).PackAir());
    }

    [Fact]
    public void The_vectors_follow_the_camera_orientation()
    {
        // Turned 90 degrees left: forward becomes -X and right becomes -Z.
        var camera = CameraView.Default with
        {
            Orientation = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0),
        };

        var atmosphere = Atmosphere.Clear(Vector3.One);

        var view = ViewConstants.Create(in camera, 100, 100, 0, Vector3.UnitY, in atmosphere, 10, false, default);

        AssertClose(-Vector3.UnitX, view.Forward);
        AssertClose(-Vector3.UnitZ, view.Right);
        AssertClose(Vector3.UnitY, view.Up);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"Expected {expected}, got {actual}.");
}
