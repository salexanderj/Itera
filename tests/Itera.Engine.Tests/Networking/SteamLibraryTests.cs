using System.Reflection;
using System.Runtime.InteropServices;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// Steam's libraries on the platform the tests run on, as CI runs them on Linux, Windows, and an arm64 Mac: the managed
/// wrapper always loads (an arm64 Mac through <see cref="NativeLibraries.AddSteamworksFallback"/>, which the tests'
/// start-up adds as <c>AddIteraEngine</c> does), and the native library where the engine ships one.
/// </summary>
public sealed class SteamLibraryTests
{
    [Fact]
    public void Steamworks_NET_loads_a_build_that_runs()
    {
        var assembly = Assembly.Load("Steamworks.NET");

        Assert.NotNull(assembly.GetType("Steamworks.SteamAPI"));
    }

    [Fact]
    public void The_fallback_is_the_packages_build_for_this_systems_x64()
    {
        var path = NativeLibraries.SteamworksFallback(AppContext.BaseDirectory);

        Assert.NotNull(path);
        var system =
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";
        Assert.Equal(
            system + "-x64",
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))))
        );
    }

    [Fact]
    public void Steams_native_library_loads_where_the_engine_ships_one()
    {
        var file =
            OperatingSystem.IsWindows() ? "steam_api64.dll"
            : OperatingSystem.IsMacOS() ? "libsteam_api.dylib"
            : "libsteam_api.so";
        // The macOS library is universal; the others are x64 only.
        var shipped = OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture == Architecture.X64;

        var loaded = NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, file), out var handle);
        if (loaded)
        {
            NativeLibrary.Free(handle);
        }

        Assert.Equal(shipped, loaded);
    }
}
