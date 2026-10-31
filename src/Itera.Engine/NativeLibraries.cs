using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Silk.NET.Core.Loader;

namespace Itera.Engine;

/// <summary>
/// Finds the libraries the engine's packages ship in a build's <c>runtimes</c> folders where .NET or a package's own
/// loader does not. <see cref="AddPackageFolders"/> lets Silk.NET find its native libraries (GLFW, OpenAL Soft, Shaderc)
/// in <c>runtimes/RID/native</c>, by this process's runtime identifier and the portable one for its system and
/// architecture (<c>linux-x64</c>). On a CI runner with Microsoft's .NET on Ubuntu, Silk.NET 2.23 looked for them only by
/// name in the system's folders, so the tests could load none of them. A published build has the libraries beside the
/// executable, where Silk.NET looks first. <see cref="AddSteamworksFallback"/> gives Steamworks.NET to a platform its
/// package has no build for.
/// </summary>
public static class NativeLibraries
{
    private const string SteamworksName = "Steamworks.NET";

    private static readonly Lock s_gate = new();
    private static bool s_added;
    private static bool s_steamworks;

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

    /// <summary>
    /// Lets a process whose platform Steamworks.NET 2024.8 has no build for (osx-arm64, linux-arm64, win-arm64) load the
    /// package's build for the same system's x64, from the build's <c>runtimes</c> folder. The package's own fallback is
    /// a reference assembly, which .NET does not run, so such a process found no Steamworks.NET at all. Its code depends
    /// on the system (its import's name, its structs' packing), not the architecture. With macOS's universal library
    /// Steam then starts on an arm64 Mac; elsewhere the native library is missing, and the game plays without Steam.
    /// Once a process, before any Steamworks.NET type loads: <c>AddIteraEngine</c> calls it. A build published for
    /// osx-arm64 copies the osx-x64 build in itself (<c>Directory.Build.targets</c>).
    /// </summary>
    public static void AddSteamworksFallback()
    {
        lock (s_gate)
        {
            if (s_steamworks)
            {
                return;
            }

            AssemblyLoadContext.Default.Resolving += ResolveSteamworks;
            s_steamworks = true;
        }
    }

    /// <summary>
    /// The package's build of Steamworks.NET for this system's x64 under <paramref name="baseDirectory"/>, or null when
    /// the build has none.
    /// </summary>
    internal static string? SteamworksFallback(string baseDirectory)
    {
        var system =
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";
        var path = Path.Combine(
            baseDirectory,
            "runtimes",
            $"{system}-x64",
            "lib",
            "netstandard2.1",
            SteamworksName + ".dll"
        );
        return File.Exists(path) ? path : null;
    }

    private static Assembly? ResolveSteamworks(AssemblyLoadContext context, AssemblyName name) =>
        string.Equals(name.Name, SteamworksName, StringComparison.Ordinal)
        && SteamworksFallback(AppContext.BaseDirectory) is { } path
            ? context.LoadFromAssemblyPath(path)
            : null;
}
