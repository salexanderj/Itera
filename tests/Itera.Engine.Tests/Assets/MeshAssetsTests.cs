using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using Itera.Engine.Maths;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Tests.Matches;
using Itera.Engine.Tests.Rendering.Meshes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="MeshAssets"/>: declaring and adding by name, reading at the run's step, variants and joined assets made
/// there, and what it refuses.
/// </summary>
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

    [Fact]
    public void A_joined_asset_seats_each_piece_at_its_parents_point_shares_its_textures_and_moves_to_its_origin()
    {
        var (bodyTexture, armTexture) = (Texture("body"), Texture("arm"));
        // The body: a vertex at its origin, a socket 1 m along x turned a quarter about z, and a muzzle.
        var body = new MeshAsset(
            "body",
            [
                MeshAssetVertex.Pack(
                    Vector3.Zero,
                    Vector3.UnitY,
                    new Vector4(1, 0, 0, 1),
                    Vector2.Zero,
                    Vector2.Zero,
                    Vector4.One
                ),
            ],
            [0, 0, 0],
            [new(0, 3, 0)],
            [MeshMaterialData.Default with { Name = "body", BaseColourTexture = new(0, MeshSampler.LinearRepeat, 0) }],
            [bodyTexture],
            points:
            [
                new MeshPoint(
                    "socket-arm",
                    -1,
                    Vector3.UnitX,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)
                ),
                new MeshPoint("muzzle", -1, new Vector3(5, 0, 0), Quaternion.Identity),
            ]
        );
        // The arm: a vertex 1 m along its x, its normal along x and its tangent along y, a grip, a tip, and a muzzle.
        var arm = new MeshAsset(
            "arm",
            [
                MeshAssetVertex.Pack(
                    Vector3.UnitX,
                    Vector3.UnitX,
                    new Vector4(0, 1, 0, -1),
                    Vector2.One,
                    Vector2.Zero,
                    Vector4.One
                ),
            ],
            [0, 0, 0],
            [new(0, 3, 0)],
            [MeshMaterialData.Default with { Name = "arm", BaseColourTexture = new(0, MeshSampler.LinearRepeat, 0) }],
            [armTexture],
            points:
            [
                new MeshPoint("grip", -1, new Vector3(0.5f, 0, 0), Quaternion.Identity),
                new MeshPoint("muzzle", -1, new Vector3(2, 0, 0), Quaternion.Identity),
            ]
        );
        _assets.Add("body", body);
        _assets.Add("arm", arm);

        var index = _assets.DeclareJoined("gun", [new("body"), new("arm", 0, "socket-arm")], origin: "grip");
        _assets.LoadAll(textures: false);
        var gun = _assets[index];

        // The arm turned a quarter about z and moved 1 m along x; the whole moved so its grip, at (1, 0.5, 0), is the
        // origin.
        Assert.Equal(
            (2, 6, 2, 2),
            (gun.Vertices.Length, gun.Indices.Length, gun.Materials.Length, gun.Textures.Length)
        );
        AssertNear(new Vector3(-1, -0.5f, 0), gun.Vertices[0].Position);
        AssertNear(new Vector3(0, 0.5f, 0), gun.Vertices[1].Position);
        AssertNear(Vector3.UnitY, Octahedral.UnpackSnorm16(gun.Vertices[1].Normal));
        AssertNear(-Vector3.UnitX, Octahedral.UnpackTangent(gun.Vertices[1].Tangent, out var handedness));
        Assert.Equal(-1, handedness);
        Assert.Equal(new uint[] { 0, 0, 0, 1, 1, 1 }, gun.Indices);
        Assert.Equal(new MeshPrimitive(3, 3, 1), gun.Primitives[1]);
        Assert.Equal(1, gun.Materials[1].BaseColourTexture.Texture);
        Assert.Same(bodyTexture, gun.Textures[0]);
        Assert.Same(armTexture, gun.Textures[1]);
        // The arm's muzzle replaced the body's.
        Assert.Equal(["socket-arm", "grip", "muzzle"], gun.Points.Select(point => point.Name));
        AssertNear(Vector3.Zero, gun.Points[1].Position);
        AssertNear(new Vector3(0, 1.5f, 0), gun.Points[2].Position);
        Assert.Equal(index, _assets.Find("gun"));
    }

    [Fact]
    public void A_joined_asset_of_nothing_a_skinned_piece_or_a_missing_point_or_origin_is_refused()
    {
        var plain = new MeshAsset(
            "plain",
            [new MeshAssetVertex()],
            [],
            [],
            [MeshMaterialData.Default],
            [],
            points: [new MeshPoint("seat", -1, Vector3.Zero, Quaternion.Identity)]
        );
        _assets.Add("plain", plain);
        _assets.Add("skinned", MeshAssetPassTests.SkinnedAsset("skinned"));

        Assert.Throws<ArgumentException>(() => _assets.DeclareJoined("none", []));
        Assert.Throws<InvalidOperationException>(() => _assets.DeclareJoined("ghost", [new("nothing")]));
        Assert.Throws<ArgumentException>(() => _assets.DeclareJoined("floating", [new("plain", 0, "seat")]));
        Assert.Throws<ArgumentException>(() => _assets.DeclareJoined("unseated", [new("plain"), new("plain")]));
        Assert.Throws<ArgumentException>(() => _assets.DeclareJoined("ahead", [new("plain"), new("plain", 1, "seat")]));

        foreach (
            var (pieces, origin, named) in new (MeshJoinPiece[], string?, string)[]
            {
                ([new("plain"), new("plain", 0, "socket")], null, "point 'socket'"),
                ([new("skinned")], null, "'skinned' is skinned"),
                ([new("plain")], "grip", "point 'grip'"),
            }
        )
        {
            var assets = new MeshAssets(NullLogger<MeshAssets>.Instance);
            assets.Add("plain", plain);
            assets.Add("skinned", MeshAssetPassTests.SkinnedAsset("skinned"));
            assets.DeclareJoined("joined", pieces, origin);
            var error = Assert.Throws<InvalidOperationException>(() => assets.LoadAll(textures: false));
            Assert.Contains(named, error.Message, StringComparison.Ordinal);
        }
    }

    private static MeshTextureData Texture(string name) =>
        MeshTextureData.FromRgba(name, 1, 1, MeshTextureRole.Colour, [128, 128, 128, 255]);

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"Expected {expected}, was {actual}.");

    [Fact]
    public void Levels_are_kept_largest_height_first_and_a_skinned_ones_on_its_rig_are_read()
    {
        var body = MeshAssetPassTests.SkinnedAsset("body");
        var asset = _assets.Add("body", body);
        var low = _assets.Add("low", OnRig("low", body.Rig!));
        var lowest = _assets.Add("lowest", OnRig("lowest", body.Rig!));

        _assets.DeclareLevel("body", "lowest", 20);
        _assets.DeclareLevel("body", "low", 80);
        _assets.LoadAll(textures: false);

        Assert.Equal([new MeshAssetLevel(low, 80), new MeshAssetLevel(lowest, 20)], _assets.LevelsOf(asset));
        Assert.Empty(_assets.LevelsOf(low));
    }

    [Fact]
    public void A_level_on_another_rig_or_a_skinned_level_of_a_rigid_asset_stops_the_step()
    {
        var other = new MeshAssets(NullLogger<MeshAssets>.Instance);
        other.Add("body", MeshAssetPassTests.SkinnedAsset("body"));
        other.Add("stranger", MeshAssetPassTests.SkinnedAsset("stranger"));
        other.DeclareLevel("body", "stranger", 50);
        var rigid = new MeshAssets(NullLogger<MeshAssets>.Instance);
        rigid.Add("crate", MeshAssetPassTests.Asset("crate", MeshMaterialData.Default, vertices: 3));
        rigid.Add("skinned", MeshAssetPassTests.SkinnedAsset("skinned"));
        rigid.DeclareLevel("crate", "skinned", 50);

        var onOther = Assert.Throws<InvalidOperationException>(() => other.LoadAll(textures: false));
        var onRigid = Assert.Throws<InvalidOperationException>(() => rigid.LoadAll(textures: false));

        Assert.Contains(
            "'stranger' is a level of 'body' but not bound on its rig",
            onOther.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("'crate', which is rigid", onRigid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_level_with_levels_a_level_of_a_level_and_a_height_twice_are_refused()
    {
        foreach (var name in (string[])["a", "b", "c", "d"])
        {
            _assets.Add(name, MeshAssetPassTests.Asset(name, MeshMaterialData.Default, vertices: 3));
        }

        _assets.DeclareLevel("a", "b", 50);

        Assert.Throws<InvalidOperationException>(() => _assets.DeclareLevel("b", "c", 20));
        Assert.Throws<InvalidOperationException>(() => _assets.DeclareLevel("c", "a", 20));
        var twice = Assert.Throws<InvalidOperationException>(() => _assets.DeclareLevel("a", "c", 50));
        Assert.Contains("below 50 pixels already", twice.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => _assets.DeclareLevel("d", "d", 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => _assets.DeclareLevel("a", "c", 0));
        Assert.Throws<InvalidOperationException>(() => _assets.DeclareLevel("a", "nothing", 20));
        _assets.LoadAll(textures: false);
        Assert.Throws<InvalidOperationException>(() => _assets.DeclareLevel("a", "c", 20));
    }

    [Fact]
    public void A_variant_has_no_levels_of_its_sources_and_takes_a_variant_of_a_level()
    {
        var body = MeshMaterialData.Default with { Name = "body" };
        var mesh = MeshAssetPassTests.SkinnedAsset("person");
        mesh = new MeshAsset("person", mesh.Vertices, mesh.Indices, mesh.Primitives, [body], [], mesh.Rig, mesh.Skin);
        _assets.Add("person", mesh);
        _assets.Add("person-low", OnRig("person-low", mesh.Rig!, body));
        _assets.DeclareLevel("person", "person-low", 40);
        var red = new Dictionary<string, Vector4> { ["body"] = new(1, 0, 0, 1) };
        var soldier = _assets.DeclareVariant("soldier", "person", red);
        var soldierLow = _assets.DeclareVariant("soldier-low", "person-low", red);
        var plain = _assets.DeclareVariant("plain", "person", red);
        _assets.DeclareLevel("soldier", "soldier-low", 40);

        _assets.LoadAll(textures: false);

        Assert.Equal([new MeshAssetLevel(soldierLow, 40)], _assets.LevelsOf(soldier));
        Assert.Empty(_assets.LevelsOf(plain));
    }

    /// <summary>A skinned triangle on <paramref name="rig"/>, as a level built in code is.</summary>
    private static MeshAsset OnRig(string name, Rig rig, MeshMaterialData? material = null)
    {
        var made = MeshAssetPassTests.SkinnedAsset(name);
        return new MeshAsset(
            name,
            made.Vertices,
            made.Indices,
            made.Primitives,
            material is null ? made.Materials : [material],
            [],
            rig,
            made.Skin
        );
    }

    private string WriteTriangle(string name)
    {
        var gltf = new GltfBuilder();
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        gltf.Node(new JsonObject { ["mesh"] = gltf.Mesh(GltfBuilder.Primitive(gltf.Accessor(positions))) });
        return gltf.WriteGltf(_folder, name);
    }
}
