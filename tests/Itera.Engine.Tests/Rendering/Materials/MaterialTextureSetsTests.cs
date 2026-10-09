using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Materials;
using Itera.Engine.Rendering.Voxels;

namespace Itera.Engine.Tests.Rendering.Materials;

/// <summary>
/// The material texture sets' files (<see cref="DdsFile"/>) read back as written, sets loaded into the large and small
/// groups in name order with broken ones left out, and a material packed with its set (<see cref="MeshMaterial.From"/>).
/// </summary>
public sealed class MaterialTextureSetsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "itera-sets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(DdsFormat.Bc7Srgb)]
    [InlineData(DdsFormat.Bc5)]
    [InlineData(DdsFormat.Bc4)]
    public void A_file_reads_back_its_format_size_and_every_level(DdsFormat format)
    {
        var mips = Levels(format, 16, seed: 3);
        using var stream = new MemoryStream();
        DdsFile.Write(stream, format, 16, mips);
        var bytes = stream.ToArray();

        var dds = DdsFile.Read(bytes);

        Assert.Equal(format, dds.Format);
        Assert.Equal(16, dds.Texels);
        Assert.Equal(5, dds.Offsets.Count);
        Assert.Equal(148, dds.Offsets[0]);
        for (var i = 0; i < mips.Count; i++)
        {
            Assert.Equal(mips[i], bytes.AsSpan(dds.Offsets[i], dds.Lengths[i]).ToArray());
        }

        // Levels below a block are one block each: 16 texels are 4x4 blocks, then 2x2, then one, one, one.
        Assert.Equal([16, 4, 1, 1, 1], dds.Lengths.Select(l => l / DdsFile.BlockBytes(format)));
        Assert.Throws<InvalidDataException>(() => DdsFile.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<ArgumentException>(() => DdsFile.Write(new MemoryStream(), format, 16, mips.Take(4).ToList()));
    }

    [Fact]
    public void Sets_load_by_size_in_name_order_and_broken_ones_are_left_out()
    {
        WriteSet("b-ground", MaterialTextureSets.SmallTexels, 1.5f);
        WriteSet("a-ground", MaterialTextureSets.LargeTexels, 3.15f);
        WriteSet("c-rock", MaterialTextureSets.SmallTexels, 2f);
        WriteSet("d-broken", MaterialTextureSets.SmallTexels, 2f);
        File.Delete(Path.Combine(_folder, "d-broken", "height.dds"));
        var skipped = new List<string>();
        var sets = new MaterialTextureSets();

        sets.Load(_folder, (name, _) => skipped.Add(name));

        Assert.Equal(["d-broken"], skipped);
        Assert.Equal(["a-ground"], sets.Large.Select(s => s.Name));
        Assert.Equal(["b-ground", "c-rock"], sets.Small.Select(s => s.Name));
        Assert.True(sets.TryFind("c-rock", out var rock));
        Assert.Equal(new MaterialTextureSlot(false, 1, 2f, new Vector3(0.1f, 0.2f, 0.3f)), rock);
        Assert.True(sets.TryFind("a-ground", out var ground));
        Assert.True(ground.Large);
        Assert.Equal(0, ground.Layer);
        Assert.False(sets.TryFind("d-broken", out _));
        Assert.Equal(3, sets.Small[0].Files.Length);
        sets.ReleaseFiles();
        Assert.Empty(sets.Small[0].Files);

        new MaterialTextureSets().Load(Path.Combine(_folder, "missing"), (_, _) => Assert.Fail("nothing to skip"));
    }

    [Fact]
    public void A_material_carries_its_sets_place_width_mean_and_colour_match()
    {
        var look = new VoxelLook(Vector3.One, Vector3.One, Texture: "a-ground", TextureColourMatch: 0.5f);
        var slot = new MaterialTextureSlot(true, 4, 3.15f, new Vector3(0.1f, 0.2f, 0.3f));

        var material = MeshMaterial.From(in look, slot);
        var plain = MeshMaterial.From(in look);

        Assert.Equal(5u | MeshMaterial.LargeTextureBit | 128u << 24, material.Texture);
        Assert.Equal(3.15f, material.TextureMetres);
        Assert.Equal(slot.Mean, material.TextureMean);
        Assert.Equal(0u, plain.Texture);
    }

    [Theory]
    [InlineData(VoxelPattern.Strata, true)]
    [InlineData(VoxelPattern.Mottle, true)]
    [InlineData(VoxelPattern.Soil, false)]
    [InlineData(VoxelPattern.Streaks, false)]
    public void Stone_recipes_keep_their_bands_by_height(VoxelPattern pattern, bool bands)
    {
        var look = new VoxelLook(Vector3.One, Vector3.One, pattern, Texture: "a-stone");
        var slot = new MaterialTextureSlot(false, 0, 2.56f, Vector3.One);

        var material = MeshMaterial.From(in look, slot);

        Assert.Equal(bands, (material.Texture & MeshMaterial.BandsTextureBit) != 0);
    }

    private static List<byte[]> Levels(DdsFormat format, int texels, int seed)
    {
        var random = new Random(seed);
        var levels = new List<byte[]>();
        for (var i = 0; i < DdsFile.MipCount(texels); i++)
        {
            var level = new byte[DdsFile.MipBytes(format, texels, i)];
            random.NextBytes(level);
            levels.Add(level);
        }

        return levels;
    }

    private void WriteSet(string name, int texels, float metres)
    {
        var folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(folder);
        for (var i = 0; i < MaterialTextureSets.LayerFiles.Length; i++)
        {
            using var file = File.Create(Path.Combine(folder, MaterialTextureSets.LayerFiles[i]));
            var format = MaterialTextureSets.LayerFormats[i];
            DdsFile.Write(file, format, texels, Levels(format, texels, seed: i));
        }

        File.WriteAllText(
            Path.Combine(folder, "set.json"),
            $$"""{ "texels": {{texels}}, "metres": {{metres.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "mean": [0.1, 0.2, 0.3], "site": "test" }"""
        );
    }
}
