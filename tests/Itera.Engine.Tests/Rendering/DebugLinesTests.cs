using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// <see cref="DebugLines"/> and the line shader: the struct and push constants match <c>debug-lines.vert</c>, boxes and
/// cylinders give their edges, the list stops at its limit, and the shader's projection puts points where the voxel
/// shader's rays go.
/// </summary>
public sealed class DebugLinesTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;
    private readonly DebugLines _lines = new(NullLogger<DebugLines>.Instance);

    public DebugLinesTests()
    {
        _compiler = new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, _cache);
        _compiler.Initialize();
    }

    public void Dispose()
    {
        _compiler.Dispose();
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public void The_line_shader_reads_the_vertex_struct_and_the_view_constants()
    {
        var spirv = _compiler.Compile("debug-lines.vert");

        Assert.Empty(GpuLayout.ArrayElementDifferences<DebugLineVertex>(spirv, 0, 0));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(spirv));
        Assert.NotEmpty(_compiler.Compile("debug-lines.frag"));
    }

    [Fact]
    public void A_box_gives_its_12_edges_turned_about_its_origin()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

        _lines.AddBox(new Vector3(10, 0, 0), turn, Vector3.Zero, new Vector3(1, 2, 3), 7);

        var vertices = _lines.Vertices;
        Assert.Equal(24, vertices.Length);
        var lengths = new List<float>();
        for (var i = 0; i < vertices.Length; i += 2)
        {
            lengths.Add(MathF.Round(Vector3.Distance(vertices[i].Position, vertices[i + 1].Position), 4));
            Assert.Equal(7u, vertices[i].Colour);
        }

        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3], lengths.Order());
        // A quarter turn about y takes the box's +z corner (0, 0, 3) to (3, 0, 0).
        Assert.Contains(vertices.ToArray(), v => Vector3.Distance(v.Position, new Vector3(13, 0, 0)) < 1e-4f);
    }

    [Fact]
    public void A_cylinder_gives_two_circles_and_four_sides()
    {
        _lines.AddCylinder(Vector3.Zero, 0.5f, 2, 1);

        Assert.Equal(2 * (16 + 16 + 4), _lines.Vertices.Length);
    }

    [Fact]
    public void Lines_past_the_limit_are_dropped_and_clearing_starts_again()
    {
        for (var i = 0; i < DebugLines.MaxLines + 10; i++)
        {
            _lines.Add(Vector3.Zero, Vector3.One, 1);
        }

        var full = _lines.Vertices.Length;
        _lines.Clear();

        Assert.Equal(2 * DebugLines.MaxLines, full);
        Assert.Equal(0, _lines.Vertices.Length);
    }

    [Fact]
    public void The_shader_puts_a_point_on_the_view_ray_through_it_at_the_worlds_depth()
    {
        var camera = CameraView.Default with
        {
            Position = new Vector3(1, 2, 3),
            Orientation = Quaternion.CreateFromYawPitchRoll(0.4f, -0.3f, 0),
        };
        var view = ViewConstants.Create(
            in camera,
            1920,
            1080,
            0,
            Vector3.UnitY,
            Atmosphere.Clear(Vector3.One),
            100,
            true
        );

        // The point 7 m along the ray through image point (0.3, -0.6) (viewRay in view.glsl).
        var ray = Vector3.Normalize(view.Forward + 0.3f * view.Right - 0.6f * view.Up);
        var point = view.Position + ray * 7;

        // debug-lines.vert projects as every pass does (view.glsl's viewClip, which ViewConstants.Clip mirrors).
        var clip = view.Clip(point - view.Position);

        Assert.Equal(0.3f, clip.X / clip.W, 4);
        Assert.Equal(-0.6f, clip.Y / clip.W, 4);
        // Its depth (clip z over w) is the world's reverse depth for a surface 7 m along that ray (mesh.vert): the lens's
        // near distance over its distance along the view direction.
        var lineDepth = clip.Z / clip.W;
        var worldDepth = view.Near / (7 * Vector3.Dot(ray, view.Forward));
        Assert.Equal(worldDepth, lineDepth, 6);
        Assert.Equal(camera.NearMetres, view.Near);
        Assert.InRange(lineDepth, 0.0025f, 0.05f);
    }

    [Fact]
    public void Adding_lines_allocates_nothing()
    {
        _lines.AddBox(Vector3.Zero, Quaternion.Identity, Vector3.Zero, Vector3.One, 1);
        _lines.Clear();

        var before = SystemAllocation.StartMeasurement();
        _lines.AddBox(Vector3.Zero, Quaternion.Identity, Vector3.Zero, Vector3.One, 1);
        _lines.AddCylinder(Vector3.Zero, 0.3f, 1.7f, 2);
        _lines.Clear();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
