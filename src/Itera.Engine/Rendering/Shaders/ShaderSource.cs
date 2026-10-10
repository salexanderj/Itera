using System.Text;
using System.Text.RegularExpressions;

namespace Itera.Engine.Rendering.Shaders;

/// <summary>
/// Where the shader files are: the engine's folder, and the game's own shaders, which its paths name under
/// <see cref="ShaderCompiler.GameFolder"/> (<c>Game/</c>) and which may lie in a folder of their own.
/// </summary>
/// <param name="Engine">Absolute path of the engine's shader folder.</param>
/// <param name="Game">Absolute path of the folder that <c>Game/</c> paths name.</param>
internal readonly record struct ShaderRoots(string Engine, string Game)
{
    /// <summary>The file a shader path (relative, forward slashes, no <c>..</c>) names.</summary>
    public string PathOf(string name)
    {
        var prefix = ShaderCompiler.GameFolder + "/";
        var (root, rest) = name.StartsWith(prefix, StringComparison.Ordinal)
            ? (Game, name[prefix.Length..])
            : (Engine, name);
        return Path.Combine(root, rest.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>
/// Reads a GLSL file and expands its <c>#include "file"</c> lines into one source text. Expansion happens before
/// compilation, not through shaderc's include callbacks, because the cache key must cover the included text and the
/// callbacks only see includes while compilation runs.
/// </summary>
/// <remarks>
/// <c>#version</c> stays on its own line. The expander adds <c>#extension GL_GOOGLE_cpp_style_line_directive</c>
/// directly after it and a <c>#line N "file"</c> directive around every included block, so compiler messages name
/// the right file and line. Paths are relative to the shader tree (the engine's folder, with the game's under
/// <c>Game/</c>, <see cref="ShaderRoots"/>); an include is relative to the including file and must stay inside the
/// tree, so a game's shader includes the engine's as <c>../view.glsl</c> wherever its folder lies. A file is expanded
/// once; a later include of it is skipped. Including a file that is still being expanded is an error (a cycle).
/// </remarks>
internal static partial class ShaderSource
{
    private const string LineDirectiveExtension = "#extension GL_GOOGLE_cpp_style_line_directive : enable";

    /// <summary>Returns the expanded source of the shader at <paramref name="path"/>.</summary>
    /// <param name="path">The shader's path in the tree: relative, forward slashes.</param>
    /// <param name="roots">The folders the tree's files are in.</param>
    public static string Expand(string path, ShaderRoots roots)
    {
        var name =
            Resolve(string.Empty, path)
            ?? throw new ShaderCompilationException($"Shader file '{path}' is outside the shader folder.");
        var state = new ExpansionState(roots);
        var builder = new StringBuilder();
        state.Active.Add(name);
        state.Expanded.Add(name);

        var lines = ReadLines(name, state, includedFrom: null);
        var versionIndex = Array.FindIndex(lines, l => l.TrimStart().StartsWith("#version", StringComparison.Ordinal));
        if (versionIndex < 0)
        {
            throw new ShaderCompilationException($"Shader '{name}' has no #version line.");
        }

        for (var i = 0; i <= versionIndex; i++)
        {
            builder.Append(lines[i]).Append('\n');
        }

        builder.Append(LineDirectiveExtension).Append('\n');
        AppendLine(builder, versionIndex + 2, name);
        AppendBody(builder, lines, versionIndex + 1, name, state);
        return builder.ToString();
    }

    /// <summary>
    /// The tree's path of <paramref name="include"/> written in the file <paramref name="from"/> (forward slashes, no
    /// <c>.</c> or <c>..</c>), or null when it leaves the tree or is absolute.
    /// </summary>
    internal static string? Resolve(string from, string include)
    {
        if (include.Length == 0 || Path.IsPathRooted(include) || include.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = new List<string>(from.Split('/'));
        parts.RemoveAt(parts.Count - 1);
        foreach (var part in include.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static void AppendBody(StringBuilder builder, string[] lines, int start, string name, ExpansionState state)
    {
        for (var i = start; i < lines.Length; i++)
        {
            var match = IncludePattern().Match(lines[i]);
            if (!match.Success)
            {
                builder.Append(lines[i]).Append('\n');
                continue;
            }

            var lineNumber = i + 1;
            var includeName = match.Groups[1].Value;
            var include =
                Resolve(name, includeName)
                ?? throw new ShaderCompilationException(
                    $"{name}:{lineNumber}: include \"{includeName}\" is outside the shader folder."
                );
            if (state.Active.Contains(include))
            {
                throw new ShaderCompilationException($"{name}:{lineNumber}: include \"{includeName}\" forms a cycle.");
            }

            if (!state.Expanded.Add(include))
            {
                // Already expanded once; keep the line count so later #line numbers stay right.
                builder.Append('\n');
                continue;
            }

            var includedLines = ReadLines(include, state, includedFrom: $"{name}:{lineNumber}");
            if (Array.Exists(includedLines, l => l.TrimStart().StartsWith("#version", StringComparison.Ordinal)))
            {
                throw new ShaderCompilationException($"{include}: an included file must not have a #version line.");
            }

            state.Active.Add(include);
            AppendLine(builder, 1, include);
            AppendBody(builder, includedLines, 0, include, state);
            state.Active.Remove(include);
            AppendLine(builder, lineNumber + 1, name);
        }
    }

    private static string[] ReadLines(string name, ExpansionState state, string? includedFrom)
    {
        var path = state.Roots.PathOf(name);
        if (!File.Exists(path))
        {
            var where = includedFrom is null ? string.Empty : $" (included from {includedFrom})";
            throw new ShaderCompilationException($"Shader file '{name}' was not found{where}.");
        }

        // A file that cannot be read (taken away or locked between the check and the read, no permission) fails the
        // compile like a missing one, so a reload keeps the old pipelines rather than faulting the render thread.
        try
        {
            return File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ShaderCompilationException($"Shader file '{name}' could not be read: {exception.Message}");
        }
    }

    private static void AppendLine(StringBuilder builder, int nextLineNumber, string name)
    {
        builder.Append("#line ").Append(nextLineNumber).Append(" \"").Append(name).Append("\"\n");
    }

    [GeneratedRegex("""^\s*#\s*include\s+"([^"]+)"\s*$""")]
    private static partial Regex IncludePattern();

    private sealed class ExpansionState(ShaderRoots roots)
    {
        public ShaderRoots Roots { get; } = roots;

        /// <summary>Files on the current include chain, by their path in the tree.</summary>
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);

        /// <summary>Files already expanded somewhere in this shader.</summary>
        public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);
    }
}
