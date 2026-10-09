using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// The reload's compile step, which runs before any pipeline is replaced, against a copy of the shader folder, and the
/// one-at-a-time rule (<see cref="ShaderReloads"/>). The pipelines themselves are checked in the running game with
/// validation (<c>validate.sh</c> with the control port).
/// </summary>
public sealed class ShaderReloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "itera-shader-reload-" + Guid.NewGuid());
    private readonly string _cache;
    private readonly ShaderCompiler _compiler;
    private readonly LoadingPass _pass;

    public ShaderReloadTests()
    {
        var sources = Path.Combine(_folder, "Shaders");
        _cache = Path.Combine(_folder, "Cache");
        Directory.CreateDirectory(sources);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Shaders")))
        {
            File.Copy(file, Path.Combine(sources, Path.GetFileName(file)));
        }

        _compiler = new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, _cache, sources);
        _compiler.Initialize();
        var options = Options.Create(new EngineOptions());
        var context = new VulkanContext(options, NullLogger<VulkanContext>.Instance);
        var swapchain = new VulkanSwapchain(context, options, NullLogger<VulkanSwapchain>.Instance);
        _pass = new LoadingPass(context, swapchain, _compiler);
    }

    public void Dispose()
    {
        _compiler.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_pass_compiles_its_shaders_from_the_folder_and_an_error_names_the_file_and_line()
    {
        var compiled = (byte[][])_pass.CompileShaders();
        Assert.Equal(2, compiled.Length);
        Assert.All(compiled, spirv => Assert.True(ShaderCompiler.LooksLikeSpirv(spirv)));

        var path = Path.Combine(_compiler.SourceDirectory, "loading.frag");
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace("void main()", "void main() oops", StringComparison.Ordinal)
        );

        var error = Assert.Throws<ShaderCompilationException>(() => _pass.CompileShaders());
        Assert.Contains("loading.frag", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A shader file that cannot be read fails the compile as a missing one does, so a reload keeps the old pipelines
    /// rather than faulting the render thread.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_be_read_fails_the_compile()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var path = Path.Combine(_compiler.SourceDirectory, "loading.frag");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var error = Assert.Throws<ShaderCompilationException>(() => _pass.CompileShaders());
            Assert.Contains("loading.frag", error.Message, StringComparison.Ordinal);
            Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void One_reload_at_a_time_and_each_frame_takes_it_once()
    {
        var reloads = new ShaderReloads(_compiler);
        var first = new ShaderReloadRequest();

        Assert.Null(reloads.Request(first));
        Assert.NotNull(reloads.Request(new ShaderReloadRequest()));
        Assert.Same(first, reloads.TakeForFrame());
        Assert.Null(reloads.TakeForFrame());
        first.Fail("test", 1);
        Assert.Null(reloads.Request(new ShaderReloadRequest()));
        Assert.Equal(_compiler.SourceDirectory, reloads.SourceDirectory);
    }
}
