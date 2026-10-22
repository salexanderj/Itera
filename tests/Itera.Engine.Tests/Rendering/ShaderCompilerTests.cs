using System.Security.Cryptography;
using System.Text;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Shaderc;

namespace Itera.Engine.Tests.Rendering;

public sealed class ShaderCompilerTests : IDisposable
{
    private const string VertexSource = """
        #version 450
        void main() { gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly string _sources;
    private readonly ShaderCompiler _compiler;

    public ShaderCompilerTests()
    {
        _cache = Path.Combine(_root, "cache");
        _sources = Path.Combine(_root, "shaders");
        Directory.CreateDirectory(_sources);
        _compiler = new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, _cache, _sources);
        _compiler.Initialize();
    }

    public void Dispose()
    {
        _compiler.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Writes_and_reuses_the_disk_cache()
    {
        var first = _compiler.CompileSource(VertexSource, ShaderKind.VertexShader, "test.vert");
        var cached = Directory.GetFiles(_cache, "*.spv");
        var second = _compiler.CompileSource(VertexSource, ShaderKind.VertexShader, "test.vert");

        Assert.True(ShaderCompiler.LooksLikeSpirv(first));
        Assert.Single(cached);
        Assert.Equal(first, second);
        Assert.Empty(Directory.GetFiles(_cache, "*.tmp"));
    }

    [Fact]
    public void Reports_compiler_errors()
    {
        var ex = Assert.Throws<ShaderCompilationException>(() =>
            _compiler.CompileSource(
                "#version 450\nvoid main() { this is not glsl }",
                ShaderKind.FragmentShader,
                "broken.frag"
            )
        );

        Assert.Equal("broken.frag", ex.ShaderName);
        Assert.NotEqual(CompilationStatus.Success, ex.Status);
        Assert.Contains("broken.frag", ex.CompilerOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiles_a_file_with_nested_includes_and_takes_the_stage_from_the_extension()
    {
        WriteSource("lib/math.glsl", "float twice(float x) { return 2.0 * x; }");
        WriteSource("lib/colour.glsl", "#include \"math.glsl\"\nvec4 grey(float v) { return vec4(twice(v)); }");
        WriteSource(
            "main.frag",
            """
            #version 450
            #include "lib/colour.glsl"
            #include "lib/math.glsl"
            layout(location = 0) out vec4 colour;
            void main() { colour = grey(0.25); }
            """
        );

        var spirv = _compiler.Compile("main.frag");

        Assert.True(ShaderCompiler.LooksLikeSpirv(spirv));
    }

    [Fact]
    public void An_error_in_an_included_file_names_that_file_and_line()
    {
        WriteSource("common.glsl", "float ok() { return 1.0; }\nfloat broken() { return undefinedName; }");
        WriteSource(
            "main.comp",
            "#version 450\nlayout(local_size_x = 1) in;\n#include \"common.glsl\"\nvoid main() { }"
        );

        var ex = Assert.Throws<ShaderCompilationException>(() => _compiler.Compile("main.comp"));

        Assert.Contains("common.glsl:2", ex.CompilerOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_after_an_include_reports_the_line_in_the_including_file()
    {
        WriteSource("common.glsl", "float one() { return 1.0; }\nfloat two() { return 2.0; }");
        WriteSource(
            "main.comp",
            "#version 450\nlayout(local_size_x = 1) in;\n#include \"common.glsl\"\nvoid main() { x; }"
        );

        var ex = Assert.Throws<ShaderCompilationException>(() => _compiler.Compile("main.comp"));

        Assert.Contains("main.comp:4", ex.CompilerOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void A_games_shaders_come_from_their_own_folder_and_include_the_engines()
    {
        var game = Path.Combine(_root, "game-shaders");
        Directory.CreateDirectory(game);
        WriteSource("shared.glsl", "const float Shared = 1.0;");
        File.WriteAllText(Path.Combine(game, "own.glsl"), "const float Own = 2.0;");
        File.WriteAllText(
            Path.Combine(game, "glow.comp"),
            "#version 450\n#include \"../shared.glsl\"\n#include \"own.glsl\"\nlayout(local_size_x = 1) in;\n"
                + "void main() { float x = Shared + Own; }"
        );
        File.WriteAllText(
            Path.Combine(game, "escape.comp"),
            "#version 450\n#include \"../../x.glsl\"\nvoid main() { }"
        );
        File.WriteAllText(Path.Combine(game, "rooted.comp"), "#version 450\n#include \"/etc/x.glsl\"\nvoid main() { }");
        using var compiler = new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, _cache, _sources, game);
        compiler.Initialize();

        var spirv = compiler.Compile("Game/glow.comp");
        var escape = Assert.Throws<ShaderCompilationException>(() => compiler.Compile("Game/escape.comp"));
        var rooted = Assert.Throws<ShaderCompilationException>(() => compiler.Compile("Game/rooted.comp"));
        var missing = Assert.Throws<ShaderCompilationException>(() => _compiler.Compile("Game/glow.comp"));

        Assert.True(ShaderCompiler.LooksLikeSpirv(spirv));
        Assert.Equal(Path.GetFullPath(game), compiler.GameSourceDirectory);
        Assert.Contains("outside the shader folder", escape.Message, StringComparison.Ordinal);
        Assert.Contains("outside the shader folder", rooted.Message, StringComparison.Ordinal);
        // Without its folder, the game's shaders are looked for beside the executable, where this test has none.
        Assert.Contains("Game/glow.comp", missing.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "a.vert", "a.vert")]
    [InlineData("", "./x/../a.vert", "a.vert")]
    [InlineData("Game/glow.frag", "../view.glsl", "view.glsl")]
    [InlineData("Game/glow.frag", "parts/rim.glsl", "Game/parts/rim.glsl")]
    [InlineData("Game/glow.frag", "../../view.glsl", null)]
    [InlineData("a.vert", "..", null)]
    [InlineData("a.vert", "/etc/passwd", null)]
    [InlineData("a.vert", "x\\y.glsl", null)]
    public void Include_paths_resolve_inside_the_tree(string from, string include, string? expected)
    {
        Assert.Equal(expected, ShaderSource.Resolve(from, include));
    }

    [Fact]
    public void Include_cycles_missing_files_and_paths_outside_the_folder_are_errors()
    {
        WriteSource("a.glsl", "#include \"b.glsl\"");
        WriteSource("b.glsl", "#include \"a.glsl\"");
        WriteSource("cycle.comp", "#version 450\n#include \"a.glsl\"\nvoid main() { }");
        WriteSource("missing.comp", "#version 450\n#include \"nope.glsl\"\nvoid main() { }");
        WriteSource("outside.comp", "#version 450\n#include \"../outside.glsl\"\nvoid main() { }");

        var cycle = Assert.Throws<ShaderCompilationException>(() => _compiler.Compile("cycle.comp"));
        var missing = Assert.Throws<ShaderCompilationException>(() => _compiler.Compile("missing.comp"));
        var outside = Assert.Throws<ShaderCompilationException>(() => _compiler.Compile("outside.comp"));

        Assert.Contains("cycle", cycle.Message, StringComparison.Ordinal);
        Assert.Contains("nope.glsl", missing.Message, StringComparison.Ordinal);
        Assert.Contains("outside the shader folder", outside.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_cache_file_is_replaced()
    {
        var first = _compiler.CompileSource(VertexSource, ShaderKind.VertexShader, "test.vert");
        var cachePath = Assert.Single(Directory.GetFiles(_cache, "*.spv"));
        File.WriteAllBytes(cachePath, first[..10]);

        var second = _compiler.CompileSource(VertexSource, ShaderKind.VertexShader, "test.vert");

        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllBytes(cachePath));
    }

    [Fact]
    public void An_entry_keyed_without_the_compiler_settings_is_not_used()
    {
        // Before the key held the target and optimisation level, it was SHA-256 of kind, entry point, and source.
        // Plant a valid but different module under that old name: the compiler must not return it.
        var other = _compiler.CompileSource(
            "#version 450\nlayout(local_size_x = 1) in;\nvoid main() { }",
            ShaderKind.ComputeShader,
            "other.comp"
        );
        var oldKey = $"{ShaderKind.VertexShader}\0main\0{VertexSource}";
        var oldName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldKey))) + ".spv";
        File.WriteAllBytes(Path.Combine(_cache, oldName), other);

        var spirv = _compiler.CompileSource(VertexSource, ShaderKind.VertexShader, "test.vert");

        Assert.NotEqual(other, spirv);
    }

    private void WriteSource(string relativePath, string text)
    {
        var path = Path.Combine(_sources, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
