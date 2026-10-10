using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using Itera.Engine.Rendering.Materials;
using SkiaSharp;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// A model's compressed textures (<see cref="CompressedTextures"/>): read by the import in place of decoding where they
/// match the model's images; each kind of mismatch decoded with a warning naming the tool; a model without them decoded
/// with a note; the hash the tool and the import share taken from the image's bytes wherever the model keeps them. The
/// DDS files hold random blocks: the import copies them and never decodes them.
/// </summary>
public sealed class CompressedTexturesTests : IDisposable
{
    private static readonly Vector3[] s_triangle = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
    private static readonly Vector3[] s_up = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
    private static readonly Vector2[] s_uvs = [new(0, 0), new(1, 0), new(0, 1)];

    // A colour image wider than tall, and a normal map whose sides are no power of two.
    private static readonly byte[] s_colour = GltfBuilder.Png(8, 4, new SKColor(200, 100, 50, 255));
    private static readonly byte[] s_normal = GltfBuilder.Png(6, 6, new SKColor(128, 128, 255, 255));

    private readonly string _folder = Directory.CreateTempSubdirectory("itera-compressed-").FullName;
    private readonly List<string> _warnings = [];
    private readonly List<string> _notes = [];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Textures_whose_files_match_their_images_are_read_from_them()
    {
        var path = WriteModel();
        var files = Compress(path);

        var asset = Load(path);

        Assert.Empty(_warnings);
        Assert.Empty(_notes);
        var material = Assert.Single(asset.Materials);
        var colour = asset.Textures[material.BaseColourTexture.Texture];
        Assert.Equal(MeshTextureFormat.Bc7Srgb, colour.Format);
        Assert.True(colour.Compressed);
        Assert.Equal((8, 4, 4), (colour.Width, colour.Height, colour.Levels));
        Assert.Equal(files["image0-colour.dds"], colour.Texels);
        Assert.Equal(Hash(s_colour), colour.SourceHash);
        var normal = asset.Textures[material.NormalTexture.Texture];
        Assert.Equal(MeshTextureFormat.Bc5Unorm, normal.Format);
        Assert.Equal((6, 6, 3), (normal.Width, normal.Height, normal.Levels));
        Assert.Equal(files["image1-normal.dds"], normal.Texels);
        Assert.Equal(MipChain.LevelOffset(6, 6, 3, MeshTextureFormat.Bc5Unorm), normal.Texels.Length);
    }

    [Fact]
    public void A_file_made_from_another_image_is_decoded_with_a_warning()
    {
        var path = WriteModel();
        Compress(path, change: entry => entry.Role == MeshTextureRole.Colour ? entry with { Sha256 = "00" } : entry);

        var asset = Load(path);

        var colour = asset.Textures[asset.Materials[0].BaseColourTexture.Texture];
        Assert.Equal(MeshTextureFormat.Rgba8Srgb, colour.Format);
        Assert.False(colour.Compressed);
        Assert.Equal(Hash(s_colour), colour.SourceHash);
        Assert.True(asset.Textures[asset.Materials[0].NormalTexture.Texture].Compressed);
        var warning = Assert.Single(_warnings);
        Assert.Contains("image0-colour.dds was made from another image", warning, StringComparison.Ordinal);
        Assert.Contains(CompressedTextures.Command, warning, StringComparison.Ordinal);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_file_of_another_size_than_its_entry_is_decoded_with_a_warning()
    {
        var path = WriteModel();
        Compress(path, change: entry => entry.Role == MeshTextureRole.Colour ? entry with { Width = 16 } : entry);

        var asset = Load(path);

        Assert.False(asset.Textures[asset.Materials[0].BaseColourTexture.Texture].Compressed);
        Assert.Contains("image0-colour.dds is 8x4, not the 16x4", Assert.Single(_warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void One_image_in_two_roles_and_an_image_of_one_texel_read_their_own_files()
    {
        // Image 0 is the base colour and the occlusion; image 1, one texel, the metalness and roughness.
        var gltf = new GltfBuilder();
        var colour = gltf.Texture(gltf.Image(s_colour));
        var texel = gltf.Texture(gltf.Image(GltfBuilder.Png(1, 1, new SKColor(255, 128, 0, 255))));
        var material = gltf.Material(
            new JsonObject
            {
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorTexture"] = new JsonObject { ["index"] = colour },
                    ["metallicRoughnessTexture"] = new JsonObject { ["index"] = texel },
                },
                ["occlusionTexture"] = new JsonObject { ["index"] = colour },
            }
        );
        gltf.Node(
            new JsonObject
            {
                ["mesh"] = gltf.Mesh(
                    GltfBuilder.Primitive(
                        gltf.Accessor(s_triangle),
                        gltf.Accessor(s_up),
                        gltf.Accessor(s_uvs),
                        material: material
                    )
                ),
            }
        );
        var path = gltf.WriteGltf(_folder, "model");
        var files = Compress(path);

        var asset = Load(path);

        Assert.Empty(_warnings);
        Assert.Equal(["image0-colour.dds", "image0-linear.dds", "image1-linear.dds"], files.Keys.Order());
        var data = asset.Materials[0];
        var baseColour = asset.Textures[data.BaseColourTexture.Texture];
        var occlusion = asset.Textures[data.OcclusionTexture.Texture];
        var metalRough = asset.Textures[data.MetalRoughTexture.Texture];
        Assert.Equal((MeshTextureFormat.Bc7Srgb, 0), (baseColour.Format, baseColour.Image));
        Assert.Equal((MeshTextureFormat.Bc7Unorm, 0), (occlusion.Format, occlusion.Image));
        Assert.Equal(files["image0-linear.dds"], occlusion.Texels);
        Assert.Equal(
            (MeshTextureFormat.Bc7Unorm, 1, 1, 1),
            (metalRough.Format, metalRough.Width, metalRough.Levels, metalRough.Texels.Length / 16)
        );
    }

    [Fact]
    public void A_texture_the_list_does_not_name_is_decoded_with_a_warning()
    {
        var path = WriteModel();
        Compress(path, keep: entry => entry.Role != MeshTextureRole.Normal);

        var asset = Load(path);

        Assert.Equal(MeshTextureFormat.Rg8Unorm, asset.Textures[asset.Materials[0].NormalTexture.Texture].Format);
        Assert.True(asset.Textures[asset.Materials[0].BaseColourTexture.Texture].Compressed);
        Assert.Contains("has no entry", Assert.Single(_warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_in_another_format_or_cut_short_is_decoded_with_a_warning()
    {
        var path = WriteModel();
        var folder = CompressedTextures.FolderOf(path);
        Compress(path);
        // The colour's file written again as BC5 (a normal map's format), the normal map's cut short.
        var colour = CompressedTextures.DecodeAll(path, []).First(t => t.Role == MeshTextureRole.Colour);
        using (var stream = File.Create(Path.Combine(folder, "image0-colour.dds")))
        {
            DdsFile.Write(stream, DdsFormat.Bc5, colour.Width, colour.Height, Levels(DdsFormat.Bc5, 8, 4));
        }

        var normalFile = Path.Combine(folder, "image1-normal.dds");
        File.WriteAllBytes(normalFile, File.ReadAllBytes(normalFile)[..^1]);

        var asset = Load(path);

        Assert.All(asset.Textures, texture => Assert.False(texture.Compressed));
        Assert.Equal(2, _warnings.Count);
        Assert.Contains(_warnings, w => w.Contains("image0-colour.dds is Bc5, not Bc7Srgb", StringComparison.Ordinal));
        Assert.Contains(_warnings, w => w.Contains("image1-normal.dds cannot be read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData(
        """{ "model": "m.gltf", "encoder": "e", "textures": [ { "image": 0, "role": "Colour", "file": "../out.dds", "width": 8, "height": 4, "sha256": "00" } ] }"""
    )]
    public void A_list_that_cannot_be_read_is_one_warning_and_every_texture_is_decoded(string list)
    {
        var path = WriteModel();
        var folder = CompressedTextures.FolderOf(path);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, CompressedTextures.ListFile), list);

        var asset = Load(path);

        Assert.All(asset.Textures, texture => Assert.False(texture.Compressed));
        Assert.Contains("model.textures", Assert.Single(_warnings), StringComparison.Ordinal);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_model_without_compressed_textures_is_decoded_with_a_note()
    {
        var asset = Load(WriteModel());

        Assert.All(asset.Textures, texture => Assert.False(texture.Compressed));
        Assert.Empty(_warnings);
        var note = Assert.Single(_notes);
        Assert.Contains("2 texture(s) decoded at load, with no model.textures", note, StringComparison.Ordinal);
        Assert.Contains(CompressedTextures.Command, note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_without_graphics_reads_no_textures_and_notes_nothing()
    {
        var path = WriteModel();
        Compress(path);

        var asset = GltfImport.Load("test", path, 1, TextureReading.None, _warnings, _notes);

        Assert.Empty(asset.Textures);
        Assert.Empty(_warnings);
        Assert.Empty(_notes);
    }

    [Fact]
    public void What_the_tool_compresses_is_decoded_even_beside_matching_files()
    {
        var path = WriteModel();
        Compress(path);

        var textures = CompressedTextures.DecodeAll(path, _warnings);

        Assert.Equal(
            [
                (0, MeshTextureRole.Colour, MeshTextureFormat.Rgba8Srgb),
                (1, MeshTextureRole.Normal, MeshTextureFormat.Rg8Unorm),
            ],
            textures.Select(t => (t.Image, t.Role, t.Format))
        );
        Assert.Equal([Hash(s_colour), Hash(s_normal)], textures.Select(t => t.SourceHash));
        Assert.Empty(_warnings);
    }

    [Theory]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("file", false)]
    [InlineData("file", true)]
    [InlineData("buffer", false)]
    [InlineData("buffer", true)]
    public void The_hash_is_of_the_image_file_s_bytes_wherever_the_model_keeps_them(string where, bool glb)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, $"{where}-{glb}")).FullName;
        var gltf = new GltfBuilder();
        var image = where switch
        {
            "data" => gltf.Image(s_colour),
            "file" => gltf.ImageFile("colour.png"),
            _ => gltf.ImageInBuffer(s_colour),
        };
        File.WriteAllBytes(Path.Combine(folder, "colour.png"), s_colour);
        AddTriangle(gltf, new JsonObject { ["index"] = gltf.Texture(image) }, normal: null);
        var path = glb ? gltf.WriteGlb(folder, "model") : gltf.WriteGltf(folder, "model");

        var texture = Assert.Single(CompressedTextures.DecodeAll(path, _warnings));

        Assert.Equal(Hash(s_colour), texture.SourceHash);
        Assert.Equal(0, texture.Image);
    }

    private MeshAsset Load(string path) =>
        GltfImport.Load("test", path, 1, TextureReading.PreferCompressed, _warnings, _notes);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A triangle whose material has a base colour texture (image 0) and a normal map (image 1).</summary>
    private string WriteModel()
    {
        var gltf = new GltfBuilder();
        var colour = new JsonObject { ["index"] = gltf.Texture(gltf.Image(s_colour)) };
        var normal = new JsonObject { ["index"] = gltf.Texture(gltf.Image(s_normal)) };
        AddTriangle(gltf, colour, normal);
        return gltf.WriteGltf(_folder, "model");
    }

    private static void AddTriangle(GltfBuilder gltf, JsonObject colour, JsonObject? normal)
    {
        var material = new JsonObject { ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = colour } };
        if (normal is not null)
        {
            material["normalTexture"] = normal;
        }

        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(s_triangle),
                gltf.Accessor(s_up),
                gltf.Accessor(s_uvs),
                material: gltf.Material(material)
            )
        );
        gltf.Node(new JsonObject { ["mesh"] = mesh });
    }

    /// <summary>
    /// The model's compressed textures as <c>tools/Textures</c> makes them, but with random blocks: a file for each
    /// texture the import makes (<paramref name="keep"/> chooses which, <paramref name="change"/> alters an entry), and
    /// the list. Returns each file's levels, packed.
    /// </summary>
    private static Dictionary<string, byte[]> Compress(
        string path,
        Func<CompressedTextureEntry, bool>? keep = null,
        Func<CompressedTextureEntry, CompressedTextureEntry>? change = null
    )
    {
        var folder = CompressedTextures.FolderOf(path);
        Directory.CreateDirectory(folder);
        var entries = new List<CompressedTextureEntry>();
        var files = new Dictionary<string, byte[]>();
        foreach (var texture in CompressedTextures.DecodeAll(path, []))
        {
            var file = CompressedTextures.FileName(texture.Image, texture.Role);
            var entry = new CompressedTextureEntry(
                texture.Image,
                texture.Role,
                file,
                texture.Width,
                texture.Height,
                texture.SourceHash
            );
            if (keep is not null && !keep(entry))
            {
                continue;
            }

            var format = CompressedTextures.FormatOf(texture.Role);
            var levels = Levels(format, texture.Width, texture.Height);
            using (var stream = File.Create(Path.Combine(folder, file)))
            {
                DdsFile.Write(stream, format, texture.Width, texture.Height, levels);
            }

            files[file] = [.. levels.SelectMany(level => level)];
            entries.Add(change is null ? entry : change(entry));
        }

        CompressedTextures.WriteList(folder, new CompressedTextureList(Path.GetFileName(path), "test", entries));
        return files;
    }

    private static List<byte[]> Levels(DdsFormat format, int width, int height)
    {
        var random = new Random(width * 31 + height);
        var levels = new List<byte[]>();
        for (var i = 0; i < DdsFile.MipCount(width, height); i++)
        {
            var level = new byte[DdsFile.MipBytes(format, width, height, i)];
            random.NextBytes(level);
            levels.Add(level);
        }

        return levels;
    }
}
