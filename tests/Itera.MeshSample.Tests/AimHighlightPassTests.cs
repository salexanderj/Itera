using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Itera.MeshSample.Rendering;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The sample's own pass's shaders (copied beside the engine's, including its <c>view.glsl</c>): they compile, read
/// <see cref="AimHighlightData"/> at set 0 binding 0 as the CPU writes it, and the view's push constants as the engine
/// pushes them, and use no other set.
/// </summary>
public sealed class AimHighlightPassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public AimHighlightPassTests()
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

    [Theory]
    [InlineData(AimHighlightPass.VertexShader)]
    [InlineData(AimHighlightPass.FragmentShader)]
    public void Its_shaders_read_the_highlight_and_the_view_as_the_cpu_writes_them(string shader)
    {
        var spirv = _compiler.Compile(shader);

        Assert.Empty(GpuLayout.ArrayElementDifferences<AimHighlightData>(spirv, 0, 0));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(spirv));
        Assert.Equal([0u], GpuLayout.DescriptorSets(spirv));
    }
}
