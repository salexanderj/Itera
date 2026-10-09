using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering;

public sealed class UpscalePassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public UpscalePassTests()
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
    public void The_push_constant_block_in_the_upscale_shader_matches_the_struct()
    {
        var spirv = _compiler.Compile("fsr-easu.frag");

        Assert.Empty(GpuLayout.PushConstantDifferences<EasuConstants>(spirv));
    }

    [Fact]
    public void The_push_constant_block_in_the_sharpening_shader_matches_the_struct()
    {
        var spirv = _compiler.Compile("fsr-rcas.frag");

        Assert.Empty(GpuLayout.PushConstantDifferences<RcasConstants>(spirv));
    }

    [Theory]
    [InlineData(Format.B8G8R8A8Srgb, Format.B8G8R8A8Unorm)]
    [InlineData(Format.R8G8B8A8Srgb, Format.R8G8B8A8Unorm)]
    [InlineData(Format.A8B8G8R8SrgbPack32, Format.A8B8G8R8UnormPack32)]
    [InlineData(Format.B8G8R8A8Unorm, Format.B8G8R8A8Unorm)]
    public void The_source_is_read_through_the_unorm_twin_of_an_srgb_format(Format format, Format expected)
    {
        Assert.Equal(expected, UpscalePass.Unorm(format));
    }
}
