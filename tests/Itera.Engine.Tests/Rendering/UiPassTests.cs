using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering;

public sealed class UiPassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public UiPassTests()
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
    public void The_push_constant_block_in_the_ui_shader_matches_the_struct()
    {
        var spirv = _compiler.Compile("ui.frag");

        Assert.Empty(GpuLayout.PushConstantDifferences<UiConstants>(spirv));
    }

    [Fact]
    public void The_font_is_copied_next_to_the_assembly()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", Itera.Engine.UI.UiFonts.MonoFileName)));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", "OFL.txt")));
    }
}
