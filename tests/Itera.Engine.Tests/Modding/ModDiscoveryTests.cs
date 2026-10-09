using Itera.Engine.Modding;
using Itera.Modding;

namespace Itera.Engine.Tests.Modding;

public sealed class ModDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));

    public ModDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Missing_root_yields_nothing()
    {
        Assert.Empty(ModLoader.DiscoverModDirectories(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void Only_folders_with_a_manifest_are_returned_in_ordinal_order()
    {
        CreateMod("zeta");
        CreateMod("alpha");
        Directory.CreateDirectory(Path.Combine(_root, "no-manifest"));
        File.WriteAllText(Path.Combine(_root, "stray-file.txt"), string.Empty);

        var found = ModLoader.DiscoverModDirectories(_root).Select(d => Path.GetFileName(d) ?? string.Empty).ToArray();

        Assert.Equal(["alpha", "zeta"], found);
    }

    private void CreateMod(string folder)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        var manifest = new ModManifest
        {
            Id = folder,
            Name = folder,
            Version = "1",
            EntryAssembly = folder + ".dll",
        };
        File.WriteAllText(Path.Combine(directory, ModManifestReader.FileName), ModManifestReader.Write(manifest));
    }
}
