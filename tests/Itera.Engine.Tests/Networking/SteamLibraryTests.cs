using System.Reflection;
using System.Runtime.InteropServices;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// Steam's libraries on the platform the tests run on, as CI runs them on Linux, Windows, and an arm64 Mac: the managed
/// wrapper always loads, the build gives platforms the package has no build for its Unix x64 build marked for arm64
/// (<c>Directory.Build.targets</c>), and the native library loads where the engine ships one.
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
    public void The_build_gives_platforms_without_a_build_of_their_own_the_unix_x64_build_marked_for_arm64()
    {
        var shared = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Steamworks.NET.dll"));
        var unix = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-x64", "lib", "netstandard2.1", "Steamworks.NET.dll")
        );
        // The COFF header's machine field, after the "PE\0\0" that offset 0x3C points to.
        var machine = BitConverter.ToInt32(shared, 0x3C) + 4;

        Assert.Equal(0xAA64, BitConverter.ToUInt16(shared, machine));
        shared[machine] = unix[machine];
        shared[machine + 1] = unix[machine + 1];
        Assert.True(unix.AsSpan().SequenceEqual(shared), "Steamworks.NET.dll is not the package's Unix x64 build.");
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
