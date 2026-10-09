using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering;

public sealed class SmaaPassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public SmaaPassTests()
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
    [InlineData("smaa-edges.frag")]
    [InlineData("smaa-weights.frag")]
    [InlineData("smaa-blend.frag")]
    public void Each_pass_s_push_constant_block_matches_the_struct(string shader)
    {
        var spirv = _compiler.Compile(shader);

        Assert.Empty(GpuLayout.PushConstantDifferences<SmaaConstants>(spirv));
    }
}
