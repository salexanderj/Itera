using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Tests.Matches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Assets;

/// <summary><see cref="MeshAssets"/>: declaring and adding by name, reading at the run's step, and what it refuses.</summary>
public sealed class MeshAssetsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("itera-assets-").FullName;
    private readonly MeshAssets _assets = new(NullLogger<MeshAssets>.Instance);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Declared_files_are_read_at_the_step_and_found_by_name()
    {
        var first = _assets.Declare("first", WriteTriangle("first"));
        var second = _assets.Declare("second", WriteTriangle("second"), metresPerUnit: 0.01f);

        _assets.LoadAll(textures: true);

        Assert.True(_assets.Loaded);
        Assert.Equal(2, _assets.Count);
        Assert.Equal(first, _assets.Find("first"));
        Assert.Equal(second, _assets.Find("second"));
        Assert.Equal(-1, _assets.Find("third"));
        Assert.Equal("second", _assets[second].Name);
        Assert.Equal(new Vector3(0.01f, 0.01f, 0), _assets[second].BoundsMax);
        Assert.Equal(
            [
                new DeclaredModel("first", Path.Combine(_folder, "first.gltf"), 1f),
                new DeclaredModel("second", Path.Combine(_folder, "second.gltf"), 0.01f),
            ],
            _assets.Files
        );
    }

    [Fact]
    public void A_mesh_built_in_code_is_found_without_reading_anything()
    {
        var mesh = new MeshAsset("plane", [new MeshAssetVertex()], [], [], [MeshMaterialData.Default], []);

        var index = _assets.Add("plane", mesh);
        _assets.LoadAll(textures: false);

        Assert.Same(mesh, _assets[_assets.Find("plane")]);
        Assert.Equal(0, index);
    }

    [Fact]
    public void Extra_models_bind_from_command_line_keys_and_are_read_after_the_games()
    {
        var path = WriteTriangle("crate");
        using var services = EngineComposition.Build(
            new Dictionary<string, string?>
            {
                ["Engine:Assets:Extra:0:Name"] = "crate",
                ["Engine:Assets:Extra:0:Path"] = path,
                ["Engine:Assets:Extra:0:MetresPerUnit"] = "0.01",
            },
            terrain: false
        );
        _ = services.GetRequiredService<IOptions<EngineOptions>>().Value;
        var assets = services.GetRequiredService<MeshAssets>();
        var game = assets.Declare("game", WriteTriangle("game"));

        assets.LoadAll(textures: false);

        var crate = assets.Find("crate");
        Assert.True(crate > game);
        Assert.Equal(new Vector3(0.01f, 0.01f, 0), assets[crate].BoundsMax);
    }

    [Fact]
    public void An_extra_model_with_a_games_name_stops_the_read_naming_the_option()
    {
        var options = new EngineOptions();
        options.Assets.Extra = [new ExtraModel { Name = "fox", Path = WriteTriangle("other") }];
        var assets = new MeshAssets(NullLogger<MeshAssets>.Instance, Options.Create(options));
        assets.Declare("fox", WriteTriangle("fox"));

        var failure = Assert.Throws<InvalidOperationException>(() => assets.LoadAll(textures: false));

        Assert.StartsWith("Engine:Assets:Extra:0:Name is fox", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_twice_is_refused()
    {
        _assets.Declare("fox", WriteTriangle("fox"));

        Assert.Throws<InvalidOperationException>(() => _assets.Declare("fox", WriteTriangle("other")));
    }

    [Fact]
    public void A_model_declared_after_the_step_is_refused()
    {
        _assets.LoadAll(textures: false);

        Assert.Throws<InvalidOperationException>(() => _assets.Declare("late", WriteTriangle("late")));
    }

    [Fact]
    public void A_declared_file_is_not_there_before_the_step()
    {
        var index = _assets.Declare("early", WriteTriangle("early"));

        Assert.Throws<InvalidOperationException>(() => _assets[index]);
    }

    [Fact]
    public void A_file_that_cannot_be_read_stops_the_step_naming_it()
    {
        _assets.Declare("good", WriteTriangle("good"));
        _assets.Declare("missing", Path.Combine(_folder, "missing.gltf"));

        var error = Assert.Throws<InvalidOperationException>(() => _assets.LoadAll(textures: false));

        Assert.Contains("missing.gltf", error.Message, StringComparison.Ordinal);
        Assert.False(_assets.Loaded);
    }

    [Fact]
    public void A_unit_of_no_metres_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _assets.Declare("zero", WriteTriangle("zero"), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _assets.Declare("nan", WriteTriangle("nan"), float.NaN));
    }

    [Fact]
    public void A_variant_shares_its_sources_mesh_and_recolours_the_materials_named()
    {
        var body = MeshMaterialData.Default with { Name = "body", BaseColour = new Vector4(1, 0.5f, 0, 1) };
        var bands = MeshMaterialData.Default with { Name = "bands", BaseColour = new Vector4(0.4f, 0.1f, 0.7f, 1) };
        var mesh = new MeshAsset(
            "person",
            [new MeshAssetVertex()],
            [0, 0, 0],
            [new(0, 3, 0), new(0, 3, 1)],
            [body, bands],
            [],
            points: [new MeshPoint("hand", -1, Vector3.One, Quaternion.Identity)]
        );
        _assets.Add("person", mesh);
        var file = _assets.Declare("file", WriteTriangle("file"));

        var variant = _assets.DeclareVariant(
            "soldier",
            "person",
            new Dictionary<string, Vector4> { ["body"] = new(0.2f, 0.2f, 0.25f, 1) }
        );
        _assets.LoadAll(textures: false);

        var made = _assets[variant];
        Assert.Equal("soldier", made.Name);
        Assert.Same(mesh.Vertices, made.Vertices);
        Assert.Same(mesh.Indices, made.Indices);
        Assert.Same(mesh.Primitives, made.Primitives);
        Assert.Same(mesh.Points, made.Points);
        Assert.Equal(new Vector4(0.2f, 0.2f, 0.25f, 1), made.Materials[0].BaseColour);
        Assert.Equal("body", made.Materials[0].Name);
        Assert.Same(bands, made.Materials[1]);
        Assert.Equal(new Vector4(1, 0.5f, 0, 1), mesh.Materials[0].BaseColour);
        Assert.Equal((1, 2), (file, variant));
        Assert.Equal(variant, _assets.Find("soldier"));
    }

    [Fact]
    public void A_variant_of_nothing_or_of_a_material_its_source_lacks_is_refused()
    {
        var mesh = new MeshAsset("plane", [new MeshAssetVertex()], [], [], [MeshMaterialData.Default], []);
        _assets.Add("plane", mesh);
        var colours = new Dictionary<string, Vector4> { ["paint"] = Vector4.One };

        Assert.Throws<InvalidOperationException>(() => _assets.DeclareVariant("ghost", "nothing", colours));
        Assert.Throws<ArgumentException>(() =>
            _assets.DeclareVariant("nan", "plane", new Dictionary<string, Vector4> { [""] = new(float.NaN, 0, 0, 1) })
        );
        _assets.DeclareVariant("painted", "plane", colours);
        var error = Assert.Throws<InvalidOperationException>(() => _assets.LoadAll(textures: false));

        Assert.Contains("paint", error.Message, StringComparison.Ordinal);
        Assert.Contains("'plane' lacks", error.Message, StringComparison.Ordinal);
        Assert.False(_assets.Loaded);
    }

    private string WriteTriangle(string name)
    {
        var gltf = new GltfBuilder();
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        gltf.Node(new JsonObject { ["mesh"] = gltf.Mesh(GltfBuilder.Primitive(gltf.Accessor(positions))) });
        return gltf.WriteGltf(_folder, name);
    }
}
