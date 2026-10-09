using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering;

/// <summary>The loading screen's shader reads its push constants as <see cref="LoadingConstants"/> lays them out.</summary>
public sealed class LoadingPassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public LoadingPassTests()
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
    public void The_push_constant_block_in_the_loading_shader_matches_the_struct()
    {
        var spirv = _compiler.Compile("loading.frag");

        Assert.Empty(GpuLayout.PushConstantDifferences<LoadingConstants>(spirv));
    }
}
