using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Logging.Abstractions;

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

    private string WriteTriangle(string name)
    {
        var gltf = new GltfBuilder();
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        gltf.Node(new JsonObject { ["mesh"] = gltf.Mesh(GltfBuilder.Primitive(gltf.Accessor(positions))) });
        return gltf.WriteGltf(_folder, name);
    }
}
