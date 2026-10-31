using Silk.NET.Core.Loader;

namespace Itera.Engine.Tests;

public sealed class NativeLibrariesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"itera-native-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_library_in_the_portable_runtimes_folder_is_found_by_its_name()
    {
        var portable = NativeLibraries.PortableRuntimeId();
        Assert.NotNull(portable);
        var native = Path.Combine(_folder, "runtimes", portable, "native");
        Directory.CreateDirectory(native);
        var file = Path.Combine(native, "libexample.so");
        File.WriteAllText(file, string.Empty);

        var found = Assert.Single(NativeLibraries.Candidates(Path.Combine("lib", "libexample.so"), _folder));
        Assert.Equal(file, found);
    }

    [Fact]
    public void A_library_in_no_runtimes_folder_gives_no_path()
    {
        Directory.CreateDirectory(_folder);

        Assert.Empty(NativeLibraries.Candidates("libexample.so", _folder));
    }

    [Fact]
    public void Silks_default_resolver_takes_the_runtimes_folders_once()
    {
        NativeLibraries.AddPackageFolders();
        NativeLibraries.AddPackageFolders();

        var resolver = Assert.IsType<DefaultPathResolver>(PathResolver.Default);
        Assert.Single(resolver.Resolvers, found => found.Method.Name == "InPackageFolders");
    }
}
