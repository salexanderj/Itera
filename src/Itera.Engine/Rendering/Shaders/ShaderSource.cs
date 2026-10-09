using System.Text;
using System.Text.RegularExpressions;

namespace Itera.Engine.Rendering.Shaders;

/// <summary>
/// Reads a GLSL file and expands its <c>#include "file"</c> lines into one source text. Expansion happens before
/// compilation, not through shaderc's include callbacks, because the cache key must cover the included text and the
/// callbacks only see includes while compilation runs.
/// </summary>
/// <remarks>
/// <c>#version</c> stays on its own line. The expander adds <c>#extension GL_GOOGLE_cpp_style_line_directive</c>
/// directly after it and a <c>#line N "file"</c> directive around every included block, so compiler messages name
/// the right file and line. Include paths are relative to the including file and must stay inside the shader root.
/// A file is expanded once; a later include of it is skipped. Including a file that is still being expanded is an
/// error (a cycle).
/// </remarks>
internal static partial class ShaderSource
{
    private const string LineDirectiveExtension = "#extension GL_GOOGLE_cpp_style_line_directive : enable";

    /// <summary>Returns the expanded source of the shader at <paramref name="path"/>.</summary>
    /// <param name="path">Absolute path of the shader file.</param>
    /// <param name="root">Absolute path of the shader root. Every file, including <paramref name="path"/>, must be inside it.</param>
    public static string Expand(string path, string root)
    {
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var state = new ExpansionState(rootWithSeparator);
        var builder = new StringBuilder();
        state.Active.Add(path);
        state.Expanded.Add(path);

        var lines = ReadLines(path, state, includedFrom: null);
        var name = DisplayName(path, state);
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
        AppendBody(builder, lines, versionIndex + 1, path, state);
        return builder.ToString();
    }

    private static void AppendBody(StringBuilder builder, string[] lines, int start, string path, ExpansionState state)
    {
        var name = DisplayName(path, state);
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
            var includePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, includeName));
            if (!includePath.StartsWith(state.Root, StringComparison.Ordinal))
            {
                throw new ShaderCompilationException(
                    $"{name}:{lineNumber}: include \"{includeName}\" is outside the shader folder."
                );
            }

            if (state.Active.Contains(includePath))
            {
                throw new ShaderCompilationException($"{name}:{lineNumber}: include \"{includeName}\" forms a cycle.");
            }

            if (!state.Expanded.Add(includePath))
            {
                // Already expanded once; keep the line count so later #line numbers stay right.
                builder.Append('\n');
                continue;
            }

            var includedLines = ReadLines(includePath, state, includedFrom: $"{name}:{lineNumber}");
            if (Array.Exists(includedLines, l => l.TrimStart().StartsWith("#version", StringComparison.Ordinal)))
            {
                throw new ShaderCompilationException(
                    $"{DisplayName(includePath, state)}: an included file must not have a #version line."
                );
            }

            state.Active.Add(includePath);
            AppendLine(builder, 1, DisplayName(includePath, state));
            AppendBody(builder, includedLines, 0, includePath, state);
            state.Active.Remove(includePath);
            AppendLine(builder, lineNumber + 1, name);
        }
    }

    private static string[] ReadLines(string path, ExpansionState state, string? includedFrom)
    {
        if (!path.StartsWith(state.Root, StringComparison.Ordinal))
        {
            throw new ShaderCompilationException($"Shader file '{path}' is outside the shader folder '{state.Root}'.");
        }

        if (!File.Exists(path))
        {
            var where = includedFrom is null ? string.Empty : $" (included from {includedFrom})";
            throw new ShaderCompilationException($"Shader file '{DisplayName(path, state)}' was not found{where}.");
        }

        // A file that cannot be read (taken away or locked between the check and the read, no permission) fails the
        // compile like a missing one, so a reload keeps the old pipelines rather than faulting the render thread.
        try
        {
            return File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ShaderCompilationException(
                $"Shader file '{DisplayName(path, state)}' could not be read: {exception.Message}"
            );
        }
    }

    /// <summary>The file's path relative to the shader root, with forward slashes on every platform.</summary>
    private static string DisplayName(string path, ExpansionState state)
    {
        return Path.GetRelativePath(state.Root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void AppendLine(StringBuilder builder, int nextLineNumber, string name)
    {
        builder.Append("#line ").Append(nextLineNumber).Append(" \"").Append(name).Append("\"\n");
    }

    [GeneratedRegex("""^\s*#\s*include\s+"([^"]+)"\s*$""")]
    private static partial Regex IncludePattern();

    private sealed class ExpansionState(string root)
    {
        public string Root { get; } = root;

        /// <summary>Files on the current include chain.</summary>
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);

        /// <summary>Files already expanded somewhere in this shader.</summary>
        public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);
    }
}
