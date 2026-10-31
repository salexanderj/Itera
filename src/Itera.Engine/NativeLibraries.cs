using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace Itera.Engine;

/// <summary>
/// Lets Silk.NET find the native libraries its packages ship (GLFW, OpenAL Soft, Shaderc) in a build's
/// <c>runtimes/RID/native</c> folders, by this process's runtime identifier and the portable one for its system and
/// architecture (<c>linux-x64</c>). On a CI runner with Microsoft's .NET on Ubuntu, Silk.NET 2.23 looked for them only by
/// name in the system's folders, so the tests could load none of them. A published build has the libraries beside the
/// executable, where Silk.NET looks first.
/// </summary>
public static class NativeLibraries
{
    private static readonly Lock s_gate = new();
    private static bool s_added;

    /// <summary>
    /// Adds the runtimes folders to the end of Silk.NET's default path resolver, once. Call it before a Silk.NET API's
    /// <c>GetApi</c>. It sets a new list rather than adding to the old one, which another thread may be reading.
    /// </summary>
    public static void AddPackageFolders()
    {
        lock (s_gate)
        {
            if (s_added)
            {
                return;
            }

            if (PathResolver.Default is DefaultPathResolver resolver)
            {
                resolver.Resolvers = [.. resolver.Resolvers, InPackageFolders];
            }

            s_added = true;
        }
    }

    /// <summary>
    /// The files named as <paramref name="name"/> names one in <paramref name="baseDirectory"/>'s
    /// <c>runtimes/RID/native</c> folders, for each of <see cref="RuntimeIds"/>.
    /// </summary>
    internal static IEnumerable<string> Candidates(string name, string baseDirectory)
    {
        var file = Path.GetFileName(name);
        if (file.Length == 0)
        {
            yield break;
        }

        foreach (var runtime in RuntimeIds())
        {
            var path = Path.Combine(baseDirectory, "runtimes", runtime, "native", file);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>This process's runtime identifier, then the portable one for its system and architecture.</summary>
    internal static IEnumerable<string> RuntimeIds()
    {
        var own = RuntimeInformation.RuntimeIdentifier;
        yield return own;
        if (PortableRuntimeId() is { } portable && !string.Equals(portable, own, StringComparison.Ordinal))
        {
            yield return portable;
        }
    }

    /// <summary>The portable runtime identifier of this system and architecture (<c>osx-arm64</c>), or null.</summary>
    internal static string? PortableRuntimeId()
    {
        var system =
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : null;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => null,
        };
        return system is null || architecture is null ? null : $"{system}-{architecture}";
    }

    private static IEnumerable<string> InPackageFolders(string name) => Candidates(name, AppContext.BaseDirectory);
}
