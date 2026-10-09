using System.Text.Json;

namespace Itera.Modding.Tests;

public sealed class ModManifestReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));

    public ModManifestReaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Parse_reads_all_fields_case_insensitively()
    {
        const string json = """
            {
              "ID": "my.mod",
              "Name": "My Mod",
              "version": "1.2.3",
              "EntryAssembly": "MyMod.dll",
              "entryType": "MyMod.Entry",
              "apiVersion": 1,
              "dependencies": ["other.mod"]
            }
            """;

        var manifest = ModManifestReader.Parse(json);

        Assert.Equal("my.mod", manifest.Id);
        Assert.Equal("My Mod", manifest.Name);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("MyMod.dll", manifest.EntryAssembly);
        Assert.Equal("MyMod.Entry", manifest.EntryType);
        Assert.Equal(1, manifest.ApiVersion);
        Assert.Equal(["other.mod"], manifest.Dependencies);
    }

    [Fact]
    public void Parse_defaults_optional_fields()
    {
        var manifest = ModManifestReader.Parse("""{"id":"a","name":"A","version":"1","entryAssembly":"A.dll"}""");

        Assert.Null(manifest.EntryType);
        // A missing apiVersion is 0, never the host's version: Validate rejects it.
        Assert.Equal(0, manifest.ApiVersion);
        Assert.Empty(manifest.Dependencies);
    }

    [Fact]
    public void Validate_rejects_a_missing_api_version()
    {
        File.WriteAllBytes(Path.Combine(_directory, "Good.dll"), []);
        var manifest = new ModManifest
        {
            Id = "good",
            Name = "Good",
            Version = "1",
            EntryAssembly = "Good.dll",
        };

        var error = Assert.Single(ModManifestReader.Validate(manifest, _directory));
        Assert.StartsWith("ApiVersion is required", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_throws_when_a_required_field_is_missing()
    {
        Assert.Throws<JsonException>(() => ModManifestReader.Parse("""{"id":"a","name":"A","version":"1"}"""));
    }

    [Fact]
    public void Write_then_parse_round_trips()
    {
        var original = new ModManifest
        {
            Id = "round.trip",
            Name = "Round Trip",
            Version = "0.0.1",
            EntryAssembly = "RoundTrip.dll",
            EntryType = "RoundTrip.Mod",
            Dependencies = ["dep.one", "dep.two"],
        };

        var json = ModManifestReader.Write(original);
        var parsed = ModManifestReader.Parse(json);

        Assert.Contains("\"id\": \"round.trip\"", json, StringComparison.Ordinal);
        Assert.Contains("\n  \"name\"", json, StringComparison.Ordinal);
        Assert.Equal(original with { Dependencies = [] }, parsed with { Dependencies = [] });
        Assert.Equal(original.Dependencies, parsed.Dependencies);
    }

    [Fact]
    public void Validate_accepts_a_correct_manifest()
    {
        File.WriteAllBytes(Path.Combine(_directory, "Good.dll"), []);
        var manifest = new ModManifest
        {
            Id = "good-mod_1.0",
            Name = "Good",
            Version = "1.0.0",
            EntryAssembly = "Good.dll",
            ApiVersion = ModdingApi.Version,
        };

        Assert.Empty(ModManifestReader.Validate(manifest, _directory));
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("slash/id")]
    [InlineData("")]
    public void Validate_rejects_bad_ids(string id)
    {
        File.WriteAllBytes(Path.Combine(_directory, "Good.dll"), []);
        var manifest = new ModManifest
        {
            Id = id,
            Name = "Good",
            Version = "1.0.0",
            EntryAssembly = "Good.dll",
        };

        Assert.Contains(
            ModManifestReader.Validate(manifest, _directory),
            e => e.Contains("Id", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Validate_rejects_wrong_api_version()
    {
        File.WriteAllBytes(Path.Combine(_directory, "Good.dll"), []);
        var manifest = new ModManifest
        {
            Id = "good",
            Name = "Good",
            Version = "1",
            EntryAssembly = "Good.dll",
            ApiVersion = ModdingApi.Version + 1,
        };

        Assert.Contains(
            ModManifestReader.Validate(manifest, _directory),
            e => e.Contains("ApiVersion", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Validate_rejects_missing_entry_assembly()
    {
        var manifest = new ModManifest
        {
            Id = "good",
            Name = "Good",
            Version = "1",
            EntryAssembly = "Missing.dll",
        };

        Assert.Contains(
            ModManifestReader.Validate(manifest, _directory),
            e => e.Contains("not found", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Validate_rejects_entry_assembly_outside_the_mod_folder()
    {
        var manifest = new ModManifest
        {
            Id = "good",
            Name = "Good",
            Version = "1",
            EntryAssembly = Path.Combine("..", "Escape.dll"),
        };

        Assert.Contains(
            ModManifestReader.Validate(manifest, _directory),
            e => e.Contains("inside the mod folder", StringComparison.Ordinal)
        );
    }
}
