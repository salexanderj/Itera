using System.Numerics;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Shaderc;

namespace Itera.Engine.Tests.Rendering;

public sealed class GpuLayoutTests : IDisposable
{
    private const string Head = """
        #version 450
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 64) in;
        struct Bounds { vec3 low; vec3 high; };

        """;

    private const string Body = """
        layout(scalar, set = 0, binding = 3) writeonly buffer Output { float result[]; };
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            Instance it = instances[i];
            result[i] = (it.model * vec4(it.position, 1.0)).x + it.radius + float(it.colour) + float(it.kind)
                + it.box.low.x + it.box.high.y;
        }
        """;

    private const string ScalarInstances = $$"""
        {{Head}}
        struct Instance { mat4 model; vec3 position; float radius; uint colour; int kind; Bounds box; };
        layout(scalar, set = 0, binding = 2) readonly buffer Instances { Instance instances[]; };
        {{Body}}
        """;

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public GpuLayoutTests()
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
    public void Matching_scalar_array_element_has_no_differences()
    {
        var spirv = Compile(ScalarInstances);

        Assert.Empty(GpuLayout.ArrayElementDifferences<TestInstance>(spirv, 0, 2));
    }

    [Fact]
    public void Reports_std430_offsets_and_stride()
    {
        // std430 aligns vec3 and structs holding one to 16 bytes, which moves Box and Box.High and rounds the stride up.
        var spirv = Compile(
            ScalarInstances.Replace(
                "scalar, set = 0, binding = 2",
                "std430, set = 0, binding = 2",
                StringComparison.Ordinal
            )
        );
        string[] expected =
        [
            "TestInstance: array stride is 128 in SPIR-V, size is 112 in C#",
            "TestInstance.Box: offset 88 in C#, 96 in SPIR-V",
            "TestInstance.Box.High: offset 12 in C#, 16 in SPIR-V",
        ];

        Assert.Equal(expected, GpuLayout.ArrayElementDifferences<TestInstance>(spirv, 0, 2));
    }

    [Fact]
    public void Reports_a_row_major_matrix()
    {
        // GLSL allows row_major on a block, not on a plain struct member; structs inside the block inherit it.
        var spirv = Compile(
            ScalarInstances.Replace(
                "scalar, set = 0, binding = 2",
                "scalar, row_major, set = 0, binding = 2",
                StringComparison.Ordinal
            )
        );

        var difference = Assert.Single(GpuLayout.ArrayElementDifferences<TestInstance>(spirv, 0, 2));
        Assert.StartsWith("TestInstance.Model: row-major", difference, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_a_type_mismatch()
    {
        var spirv = Compile(
            ScalarInstances
                .Replace("vec3 position", "vec4 position", StringComparison.Ordinal)
                .Replace("vec4(it.position, 1.0)", "it.position", StringComparison.Ordinal)
        );

        var differences = GpuLayout.ArrayElementDifferences<TestInstance>(spirv, 0, 2);

        Assert.Contains("TestInstance.Position: vec3 in C#, vec4 in SPIR-V", differences);
    }

    [Fact]
    public void Reports_a_bool_field_and_a_missing_member()
    {
        var spirv = Compile(ScalarInstances);

        var differences = GpuLayout.ArrayElementDifferences<WithBool>(spirv, 0, 2);

        Assert.Contains("WithBool.Visible: type Boolean is not allowed in a GPU-shared struct", differences);
        Assert.Contains("WithBool: 2 fields in C#, 6 members in SPIR-V", differences);
    }

    [Fact]
    public void Reports_a_missing_binding()
    {
        var spirv = Compile(ScalarInstances);

        var difference = Assert.Single(GpuLayout.ArrayElementDifferences<TestInstance>(spirv, 0, 7));
        Assert.Equal("no block at set 0 binding 7", difference);
    }

    [Fact]
    public void Matching_scalar_push_constants_have_no_differences()
    {
        var spirv = Compile(
            $$"""
            {{Head}}
            layout(push_constant, scalar) uniform Push { vec4 tint; vec3 offset; uint index; } push;
            layout(scalar, set = 0, binding = 0) writeonly buffer Output { vec4 result[]; };
            void main() { result[push.index] = push.tint + vec4(push.offset, 0.0); }
            """
        );

        Assert.Empty(GpuLayout.PushConstantDifferences<TestPush>(spirv));
    }

    private byte[] Compile(string source) =>
        _compiler.CompileSource(source, ShaderKind.ComputeShader, "layout-test.comp");

    // The structs below are only measured, never written.
#pragma warning disable CS0649
    private struct Bounds
    {
        public Vector3 Low;
        public Vector3 High;
    }

    private struct TestInstance
    {
        public Matrix4x4 Model;
        public Vector3 Position;
        public float Radius;
        public uint Colour;
        public int Kind;
        public Bounds Box;
    }

    private struct WithBool
    {
        public Matrix4x4 Model;
        public bool Visible;
    }

    private struct TestPush
    {
        public Vector4 Tint;
        public Vector3 Offset;
        public uint Index;
    }
#pragma warning restore CS0649
}
