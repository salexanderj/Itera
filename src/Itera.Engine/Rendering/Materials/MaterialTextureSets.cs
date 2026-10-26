using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Itera.Engine.Rendering.Materials;

/// <summary>
/// A material's texture set as the GPU holds it: in the large arrays (<see cref="MaterialTextureSets.LargeTexels"/>) or
/// the small ones, at <paramref name="Layer"/>; a tile <paramref name="Metres"/> wide; its colour's mean in linear RGB
/// (<paramref name="Mean"/>), which the shader matches to the material's own colour; and the range its heights lie in
/// (<paramref name="Heights"/>).
/// </summary>
public readonly record struct MaterialTextureSlot(
    bool Large,
    int Layer,
    float Metres,
    Vector3 Mean,
    HeightRange Heights
)
{
    /// <summary>A slot whose heights may be anywhere from 0 to 1.</summary>
    public MaterialTextureSlot(bool large, int layer, float metres, Vector3 mean)
        : this(large, layer, metres, mean, HeightRange.Full) { }
}

/// <summary>
/// The world materials' texture sets (docs/rendering-redesign.md, the materials plan): each a folder of
/// <see cref="Folder"/> (copied from the game's <c>Textures/</c>, made by <c>tools/Textures</c>) holding
/// <c>set.json</c> (its size in texels, its width in metres, its colour's mean) and three DDS files: <c>colour.dds</c>
/// (BC7 sRGB, occlusion in alpha), <c>normal.dds</c> (BC5, the normal map's x and y), <c>height.dds</c> (BC4). Sets of
/// <see cref="LargeTexels"/> go in one group of arrays, of <see cref="SmallTexels"/> in the other. <c>MeshPass</c> loads
/// them at start and uploads them; <c>WorldMeshes</c> finds each material's set by the name its look gives.
/// </summary>
public sealed class MaterialTextureSets
{
    /// <summary>Texels along each side of a set of the large arrays (the forest floor's).</summary>
    public const int LargeTexels = 2048;

    /// <summary>Texels along each side of a set of the small arrays (every other).</summary>
    public const int SmallTexels = 1024;

    /// <summary>The file names of a set's three layers, in the arrays' order.</summary>
    public static readonly string[] LayerFiles = ["colour.dds", "normal.dds", "height.dds"];

    /// <summary>The formats of a set's three layers.</summary>
    public static readonly DdsFormat[] LayerFormats = [DdsFormat.Bc7Srgb, DdsFormat.Bc5, DdsFormat.Bc4];

    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, MaterialTextureSlot> _slots = new(StringComparer.Ordinal);
    private readonly List<LoadedSet> _large = [];
    private readonly List<LoadedSet> _small = [];

    /// <summary>The folder the sets are read from: <c>Textures/</c> next to the executable.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Textures");

    /// <summary>The sets of the large arrays, in layer order.</summary>
    public IReadOnlyList<LoadedSet> Large => _large;

    /// <summary>The sets of the small arrays, in layer order.</summary>
    public IReadOnlyList<LoadedSet> Small => _small;

    /// <summary>
    /// Reads every set in <paramref name="folder"/> (none when it does not exist), in name order. A set that cannot be
    /// read is left out, and <paramref name="skipped"/> hears its name and why.
    /// </summary>
    public void Load(string folder, Action<string, string> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        _slots.Clear();
        _large.Clear();
        _small.Clear();
        if (!Directory.Exists(folder))
        {
            return;
        }

        var names = Directory.GetDirectories(folder).Select(Path.GetFileName).OfType<string>().ToList();
        names.Sort(StringComparer.Ordinal);
        foreach (var name in names)
        {
            try
            {
                var set = Read(Path.Combine(folder, name), name);
                var group = set.Texels == LargeTexels ? _large : _small;
                _slots[name] = new MaterialTextureSlot(group == _large, group.Count, set.Metres, set.Mean, set.Heights);
                group.Add(set);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
            {
                skipped(name, e.Message);
            }
        }
    }

    /// <summary>The set named <paramref name="name"/>, when one was loaded.</summary>
    public bool TryFind(string name, out MaterialTextureSlot slot) => _slots.TryGetValue(name, out slot);

    /// <summary>Lets the files' bytes go once they are uploaded; the slots stay.</summary>
    public void ReleaseFiles()
    {
        foreach (var set in _large.Concat(_small))
        {
            set.Files = [];
        }
    }

    private static LoadedSet Read(string folder, string name)
    {
        var info =
            JsonSerializer.Deserialize<SetInfo>(File.ReadAllText(Path.Combine(folder, "set.json")), s_json)
            ?? throw new InvalidDataException("set.json is empty.");
        if (info.Texels is not (LargeTexels or SmallTexels))
        {
            throw new InvalidDataException($"set.json gives {info.Texels} texels, not {LargeTexels} or {SmallTexels}.");
        }

        if (info.Metres is not (> 0 and < 100) || info.Mean is not { Length: 3 })
        {
            throw new InvalidDataException("set.json needs a width in metres (0 to 100) and a mean of three values.");
        }

        var files = new byte[LayerFiles.Length][];
        var heights = HeightRange.Full;
        for (var i = 0; i < files.Length; i++)
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, LayerFiles[i]));
            var dds = DdsFile.Read(bytes);
            if (dds.Format != LayerFormats[i] || dds.Width != info.Texels || dds.Height != info.Texels)
            {
                throw new InvalidDataException(
                    $"{LayerFiles[i]} is {dds.Format} at {dds.Width}x{dds.Height} texels, not {LayerFormats[i]} at {info.Texels} square."
                );
            }

            if (dds.Format == DdsFormat.Bc4)
            {
                // The smaller levels are filtered from the largest, so they lie within its range.
                heights = HeightRange.FromBc4(bytes.AsSpan(dds.Offsets[0], dds.Lengths[0]));
            }

            files[i] = bytes;
        }

        return new LoadedSet(name, info.Texels, info.Metres, new Vector3(info.Mean[0], info.Mean[1], info.Mean[2]))
        {
            Files = files,
            Heights = heights,
        };
    }

    /// <summary>
    /// A set read from its folder: its name, size, width, colour's mean, the range of its heights, and its three files'
    /// bytes.
    /// </summary>
    public sealed class LoadedSet(string name, int texels, float metres, Vector3 mean)
    {
        public string Name { get; } = name;

        public int Texels { get; } = texels;

        public float Metres { get; } = metres;

        public Vector3 Mean { get; } = mean;

        /// <summary>The lowest and highest height the set's height layer holds (<see cref="HeightRange.Full"/> without one).</summary>
        public HeightRange Heights { get; init; } = HeightRange.Full;

        /// <summary>The DDS files of <see cref="LayerFiles"/>, until <see cref="ReleaseFiles"/>.</summary>
        public byte[][] Files { get; internal set; } = [];
    }

    private sealed class SetInfo
    {
        [JsonPropertyName("texels")]
        public int Texels { get; set; }

        [JsonPropertyName("metres")]
        public float Metres { get; set; }

        [JsonPropertyName("mean")]
        public float[]? Mean { get; set; }
    }
}
