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

        var view = ViewConstants.Create(in camera, 1920, 1080, 1, Vector3.UnitY, in atmosphere, 17.28f, true);

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

        var full = ViewConstants.Create(CameraView.Default, 2560, 1440, 0, Vector3.UnitY, in atmosphere, 10, true);
        var scaled = ViewConstants.Create(
            CameraView.Default,
            2560,
            1440,
            0,
            Vector3.UnitY,
            in atmosphere,
            10,
            false,
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

        var view = ViewConstants.Create(in camera, 100, 100, 0, Vector3.UnitY, in atmosphere, 10, false);

        AssertClose(-Vector3.UnitX, view.Forward);
        AssertClose(-Vector3.UnitZ, view.Right);
        AssertClose(Vector3.UnitY, view.Up);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"Expected {expected}, got {actual}.");
}
