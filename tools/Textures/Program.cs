using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Itera.Engine.Rendering.Materials;
using SkiaSharp;

namespace Itera.Tools.Textures;

/// <summary>
/// Makes the world materials' texture sets (README.md), or with <c>model PATH.gltf ...</c> a model's compressed textures
/// (<see cref="ModelTextures"/>): for each set in <c>src/Game/Textures/sources.json</c> (or those
/// named on the command line), takes its colour, normal, occlusion, and height maps from the game's bake, from a texture
/// recipe it runs (<c>RecipeTools/make.py</c>), or from a CC0 photograph downloaded from Poly Haven or ambientCG into a
/// cache, makes every mip level (colour and occlusion averaged in linear light, normals averaged and
/// made unit length again), compresses them (BC7 sRGB with occlusion in alpha, BC5, BC4), and writes the set's folder
/// (<c>colour.dds</c>, <c>normal.dds</c>, <c>height.dds</c>, <c>set.json</c>) and <c>SOURCES.md</c>.
/// </summary>
internal static class Program
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions s_read = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        // --data-folder NAME, anywhere: the bakes and downloads of another data folder than the game's (an agent's runs
        // use theirs).
        string? dataFolder = null;
        var named = Array.IndexOf(args, "--data-folder");
        if (named >= 0 && named + 1 < args.Length)
        {
            dataFolder = args[named + 1];
            args = [.. args[..named], .. args[(named + 2)..]];
        }

        if (args.Length >= 1 && args[0] == "model")
        {
            return ModelTextures.Run(args[1..]);
        }

        s_http.DefaultRequestHeaders.UserAgent.ParseAdd("Itera-texture-tool/1.0");
        var repo = FindRepository();
        dataFolder ??= DataFolder(repo);

        var folder = Path.Combine(repo, "src", "Game", "Textures");
        var sources = ReadSources(Path.Combine(folder, "sources.json"));
        if (args.Length == 4 && args[0] == "--preview")
        {
            return Preview(
                sources,
                args[1],
                float.Parse(args[2], CultureInfo.InvariantCulture),
                args[3],
                dataFolder,
                repo
            );
        }

        var wanted =
            args.Length == 0 ? sources : sources.Where(s => args.Contains(s.Set, StringComparer.Ordinal)).ToList();
        if (wanted.Count == 0)
        {
            Console.Error.WriteLine($"No set of sources.json is named {string.Join(", ", args)}.");
            return 1;
        }

        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            dataFolder,
            "TextureDownloads"
        );
        foreach (var source in wanted)
        {
            var started = DateTime.UtcNow;
            var maps = Download(source, cache, repo);
            Make(source, maps, Path.Combine(folder, source.Set));
            Console.WriteLine(
                $"{source.Set}: {source.Site} {source.Asset}, {source.Texels} texels, {maps.Metres:F2} m, "
                    + $"{(DateTime.UtcNow - started).TotalSeconds:F0} s"
            );
        }

        WriteSourcesList(folder, sources);
        return 0;
    }

    /// <summary>
    /// <c>--preview SET RADIUS OUT.png</c>: the set's colour map (downloaded already) filtered with the anisotropic
    /// Kuwahara filter at <paramref name="radius"/> texels (0 for none), written as a PNG without compressing anything,
    /// to judge the filter quickly.
    /// </summary>
    private static int Preview(
        List<Source> sources,
        string set,
        float radius,
        string output,
        string dataFolder,
        string repo
    )
    {
        var source = sources.FirstOrDefault(s => s.Set == set);
        if (source is null)
        {
            Console.Error.WriteLine($"No set {set} in sources.json.");
            return 1;
        }

        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            dataFolder,
            "TextureDownloads"
        );
        var maps = Download(source, cache, repo);
        var colour = Decode(maps.Colour, source.Texels);
        var started = DateTime.UtcNow;
        if (radius > 0)
        {
            Stylize(colour, source.Texels, new KuwaharaSettings(radius));
        }

        var info = new SKImageInfo(source.Texels, source.Texels, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        colour.AsSpan().CopyTo(bitmap.GetPixelSpan());
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(output);
        data.SaveTo(file);
        Console.WriteLine($"{set} at radius {radius}: {(DateTime.UtcNow - started).TotalSeconds:F1} s");
        return 0;
    }

    /// <summary>
    /// The game's data folder name, <c>Engine:Application:DataFolder</c> in <c>src/Game/appsettings.json</c> (the engine's
    /// default, Itera, without it): where the game writes its bakes and the tool keeps its downloads.
    /// </summary>
    private static string DataFolder(string repo)
    {
        try
        {
            using var settings = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(repo, "src", "Game", "appsettings.json"))
            );
            return settings
                    .RootElement.GetProperty("Engine")
                    .GetProperty("Application")
                    .GetProperty("DataFolder")
                    .GetString()
                ?? "Itera";
        }
        catch (Exception ex)
            when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "Itera";
        }
    }

    private static string FindRepository()
    {
        for (
            var directory = new DirectoryInfo(Environment.CurrentDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "Itera.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "Run the tool inside the repository (no Itera.sln above the current folder)."
        );
    }

    private static List<Source> ReadSources(string path) =>
        JsonSerializer.Deserialize<List<Source>>(File.ReadAllText(path), s_read) ?? [];

    /// <summary>
    /// The four maps' files of <paramref name="source"/> and its width in metres: the game's bake, its texture recipe run
    /// into the cache, or its photograph downloaded once into the cache.
    /// </summary>
    private static Maps Download(Source source, string cache, string repo)
    {
        if (source.Site is "bake" or "recipe")
        {
            // The game's own bake of a recipe (the console's materials bake), in the data folder's Bakes/; or a texture
            // recipe's maps, made again each time into the cache.
            var baked =
                source.Site == "bake"
                    ? Path.Combine(Path.GetDirectoryName(cache)!, "Bakes", source.Asset)
                    : RunRecipe(source, Path.Combine(cache, "recipe", source.Set), repo);
            string? Baked(string name) => File.Exists(Path.Combine(baked, name)) ? Path.Combine(baked, name) : null;
            return new Maps(
                Baked("colour.png") ?? throw new InvalidDataException($"{source.Set}: no bake in {baked}."),
                Baked("normal.png") ?? throw new InvalidDataException($"{source.Set}: no normal map in {baked}."),
                Baked("occlusion.png"),
                Baked("height.png"),
                float.Parse(File.ReadAllText(Path.Combine(baked, "metres.txt")), CultureInfo.InvariantCulture)
            );
        }

        var resolution = source.Texels == MaterialTextureSets.LargeTexels ? "2k" : "1k";
        var folder = Path.Combine(cache, source.Site, source.Asset, resolution);
        Directory.CreateDirectory(folder);
        var info = Path.Combine(folder, "metres.txt");
        if (!File.Exists(info))
        {
            var metres = source.Site switch
            {
                "polyhaven" => DownloadPolyHaven(source.Asset, resolution, folder),
                "ambientcg" => DownloadAmbientCg(source.Asset, resolution.ToUpperInvariant(), folder),
                _ => throw new InvalidDataException($"{source.Set}: unknown site {source.Site}."),
            };
            File.WriteAllText(info, metres.ToString(CultureInfo.InvariantCulture));
        }

        string? Find(string name) => File.Exists(Path.Combine(folder, name)) ? Path.Combine(folder, name) : null;
        return new Maps(
            Find("colour.jpg") ?? throw new InvalidDataException($"{source.Set}: no colour map."),
            Find("normal.jpg") ?? throw new InvalidDataException($"{source.Set}: no normal map."),
            Find("occlusion.jpg"),
            Find("height.jpg"),
            float.Parse(File.ReadAllText(info), CultureInfo.InvariantCulture)
        );
    }

    /// <summary>
    /// Runs the texture recipe <paramref name="source"/> names (its asset: the recipe's file from the repository's root,
    /// with / between folders) at the set's size, writing its maps into <paramref name="output"/>, which it returns.
    /// </summary>
    private static string RunRecipe(Source source, string output, string repo)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(repo, "tools", "Textures", "RecipeTools", "make.py"));
        start.ArgumentList.Add(Path.Combine([repo, .. source.Asset.Split('/')]));
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--texels");
        start.ArgumentList.Add(source.Texels.ToString(CultureInfo.InvariantCulture));
        using var process =
            Process.Start(start) ?? throw new InvalidOperationException($"{source.Set}: Python did not start.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"{source.Set}: the recipe {source.Asset} failed (exit code {process.ExitCode})."
            );
        }

        return output;
    }

    private static float DownloadPolyHaven(string asset, string resolution, string folder)
    {
        var info = GetJson($"https://api.polyhaven.com/info/{asset}");
        var files = GetJson($"https://api.polyhaven.com/files/{asset}");
        foreach (
            var (map, name) in new[]
            {
                ("Diffuse", "colour"),
                ("nor_gl", "normal"),
                ("AO", "occlusion"),
                ("Displacement", "height"),
            }
        )
        {
            var url = files[map]?[resolution]?["jpg"]?["url"]?.GetValue<string>();
            if (url is not null)
            {
                Save(url, Path.Combine(folder, name + ".jpg"));
            }
        }

        // Poly Haven gives the photographed area's width in millimetres.
        return info["dimensions"]![0]!.GetValue<float>() / 1000f;
    }

    private static float DownloadAmbientCg(string asset, string resolution, string folder)
    {
        var found = GetJson($"https://ambientcg.com/api/v2/full_json?id={asset}&include=downloadData,dimensionsData")[
            "foundAssets"
        ]![0]!;
        var downloads = found["downloadFolders"]!["default"]!["downloadFiletypeCategories"]!["zip"]![
            "downloads"
        ]!.AsArray();
        var link = downloads.First(d => d!["attribute"]!.GetValue<string>() == $"{resolution}-JPG")![
            "downloadLink"
        ]!.GetValue<string>();
        var zip = Path.Combine(folder, "download.zip");
        Save(link, zip);
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                var name =
                    entry.Name.EndsWith("_Color.jpg", StringComparison.Ordinal) ? "colour"
                    : entry.Name.EndsWith("_NormalGL.jpg", StringComparison.Ordinal) ? "normal"
                    : entry.Name.EndsWith("_AmbientOcclusion.jpg", StringComparison.Ordinal) ? "occlusion"
                    : entry.Name.EndsWith("_Displacement.jpg", StringComparison.Ordinal) ? "height"
                    : null;
                if (name is not null)
                {
                    entry.ExtractToFile(Path.Combine(folder, name + ".jpg"), overwrite: true);
                }
            }
        }

        File.Delete(zip);
        // ambientCG gives the photographed area's width in centimetres, where it gives one.
        var width = found["dimensionX"]?.GetValue<float>();
        if (width is null or <= 0)
        {
            Console.Error.WriteLine($"{asset}: ambientCG gives no width; taken as 2 m.");
            return 2f;
        }

        return width.Value / 100f;
    }

    private static JsonNode GetJson(string url) =>
        JsonNode.Parse(s_http.GetStringAsync(new Uri(url)).GetAwaiter().GetResult())
        ?? throw new InvalidDataException($"{url} gave nothing.");

    private static void Save(string url, string path)
    {
        using var response = s_http.GetAsync(new Uri(url)).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var file = File.Create(path);
        response.Content.CopyToAsync(file).GetAwaiter().GetResult();
    }

    /// <summary>Makes the set's three files and its <c>set.json</c> in <paramref name="output"/>.</summary>
    private static void Make(Source source, Maps maps, string output)
    {
        var texels = source.Texels;
        var colour = Decode(maps.Colour, texels);
        if (source.Kuwahara is { } stylize)
        {
            Stylize(colour, texels, stylize);
        }

        var normal = Decode(maps.Normal, texels);
        var occlusion = maps.Occlusion is null ? null : Decode(maps.Occlusion, texels);
        if (source.Kuwahara is { AllMaps: true } every)
        {
            // The relief and hollows filtered as the colour is, so the lighting does not bring the photograph's fine
            // detail back; a normal map's directions made unit length again after.
            Stylize(normal, texels, every);
            if (occlusion is not null)
            {
                Stylize(occlusion, texels, every);
            }
        }

        var height = maps.Height is null ? null : Decode(maps.Height, texels);
        var count = texels * texels;

        // Linear colour with occlusion, the normal's x, y, z, and the height, as floats, for the mip levels.
        var linear = new Vector4[count];
        var normals = new Vector3[count];
        var heights = new float[count];
        var mean = Vector3.Zero;
        for (var i = 0; i < count; i++)
        {
            var c = new Vector3(ToLinear(colour[i * 4]), ToLinear(colour[i * 4 + 1]), ToLinear(colour[i * 4 + 2]));
            mean += c;
            linear[i] = new Vector4(c, occlusion is null ? 1f : occlusion[i * 4] / 255f);
            normals[i] = Vector3.Normalize(
                new Vector3(
                    normal[i * 4] / 255f * 2 - 1,
                    normal[i * 4 + 1] / 255f * 2 - 1,
                    normal[i * 4 + 2] / 255f * 2 - 1
                )
            );
            heights[i] = height is null ? 0.5f : height[i * 4] / 255f;
        }

        mean /= count;
        // Every set's heights about one middle, so at a border between two materials (mesh.frag) only their shapes
        // differ: a set whose heights lie mostly high would push its borders out.
        var heightShift = 0.5f - heights.Average();
        for (var i = 0; i < count; i++)
        {
            heights[i] = Math.Clamp(heights[i] + heightShift, 0f, 1f);
        }

        var colourMips = new List<byte[]>();
        var normalMips = new List<byte[]>();
        var heightMips = new List<byte[]>();
        for (var size = texels; size >= 1; size /= 2)
        {
            colourMips.Add(
                Encode(
                    CompressionFormat.Bc7,
                    size,
                    i => Rgba(ToSrgb(linear[i].X), ToSrgb(linear[i].Y), ToSrgb(linear[i].Z), Unit(linear[i].W))
                )
            );
            normalMips.Add(
                Encode(CompressionFormat.Bc5, size, i => Rgba(Signed(normals[i].X), Signed(normals[i].Y), 0, 255))
            );
            heightMips.Add(Encode(CompressionFormat.Bc4, size, i => Rgba(Unit(heights[i]), 0, 0, 255)));
            if (size > 1)
            {
                linear = Halve(linear, size, (a, b, c, d) => (a + b + c + d) / 4);
                normals = Halve(normals, size, (a, b, c, d) => Vector3.Normalize(a + b + c + d));
                heights = Halve(heights, size, (a, b, c, d) => (a + b + c + d) / 4);
            }
        }

        Directory.CreateDirectory(output);
        Write(Path.Combine(output, "colour.dds"), DdsFormat.Bc7Srgb, texels, colourMips);
        Write(Path.Combine(output, "normal.dds"), DdsFormat.Bc5, texels, normalMips);
        Write(Path.Combine(output, "height.dds"), DdsFormat.Bc4, texels, heightMips);
        var set = new JsonObject
        {
            ["texels"] = texels,
            ["metres"] = MathF.Round(maps.Metres, 3),
            ["mean"] = new JsonArray(Round(mean.X), Round(mean.Y), Round(mean.Z)),
            ["site"] = source.Site,
            ["asset"] = source.Asset,
            ["link"] = Link(source),
            ["licence"] = source.Site switch
            {
                "bake" => "the game's own (baked from its recipe)",
                "recipe" => "the game's own (made by its recipe)",
                _ => "CC0 1.0",
            },
            ["maps"] =
                (maps.Occlusion is null ? "no occlusion; " : "") + (maps.Height is null ? "no height" : "all four"),
            ["kuwahara"] = source.Kuwahara is { } k
                ? FormattableString.Invariant(
                    $"radius {k.Radius}, sharpness {k.Sharpness}, hardness {k.Hardness}, alpha {k.Alpha}, all maps {k.AllMaps}"
                )
                : null,
        };
        File.WriteAllText(Path.Combine(output, "set.json"), set.ToJsonString(s_write) + "\n");

        static JsonNode Round(float value) => MathF.Round(value, 4);
    }

    /// <summary>
    /// Filters the colour bytes <paramref name="colour"/> (RGBA, <paramref name="texels"/> square) with the anisotropic
    /// Kuwahara filter (<see cref="Kuwahara"/>), for a painted look; alpha is left as it is.
    /// </summary>
    private static void Stylize(byte[] colour, int texels, KuwaharaSettings settings)
    {
        var values = new Vector3[texels * texels];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new Vector3(colour[i * 4], colour[i * 4 + 1], colour[i * 4 + 2]) / 255f;
        }

        var filtered = Kuwahara.Filter(
            values,
            texels,
            settings.Radius,
            settings.Sharpness,
            settings.Hardness,
            settings.Alpha
        );
        for (var i = 0; i < filtered.Length; i++)
        {
            colour[i * 4] = Unit(filtered[i].X);
            colour[i * 4 + 1] = Unit(filtered[i].Y);
            colour[i * 4 + 2] = Unit(filtered[i].Z);
        }
    }

    /// <summary>Decodes the image at <paramref name="path"/> as RGBA bytes, <paramref name="texels"/> square (scaled if need be).</summary>
    private static byte[] Decode(string path, int texels)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException($"{path} is not an image.");
        using var rgba = decoded.Copy(SKColorType.Rgba8888);
        if (rgba.Width == texels && rgba.Height == texels)
        {
            return rgba.GetPixelSpan().ToArray();
        }

        Console.Error.WriteLine($"{path}: {rgba.Width}x{rgba.Height}, scaled to {texels} square.");
        using var scaled = rgba.Resize(
            new SKImageInfo(texels, texels, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
        );
        return scaled.GetPixelSpan().ToArray();
    }

    /// <summary>
    /// One level, <paramref name="size"/> square, compressed: its texels from <paramref name="texel"/> (a level smaller
    /// than a block repeated to fill one).
    /// </summary>
    private static byte[] Encode(CompressionFormat format, int size, Func<int, uint> texel)
    {
        var side = Math.Max(size, 4);
        var bytes = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var value = texel((y % size) * size + (x % size));
                var at = (y * side + x) * 4;
                bytes[at] = (byte)value;
                bytes[at + 1] = (byte)(value >> 8);
                bytes[at + 2] = (byte)(value >> 16);
                bytes[at + 3] = (byte)(value >> 24);
            }
        }

        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.Format = format;
        encoder.Options.IsParallel = true;
        return encoder.EncodeToRawBytes(bytes, side, side, PixelFormat.Rgba32, 0, out _, out _);
    }

    private static T[] Halve<T>(T[] level, int size, Func<T, T, T, T, T> mean)
    {
        var half = size / 2;
        var next = new T[half * half];
        for (var y = 0; y < half; y++)
        {
            for (var x = 0; x < half; x++)
            {
                var at = 2 * y * size + 2 * x;
                next[y * half + x] = mean(level[at], level[at + 1], level[at + size], level[at + size + 1]);
            }
        }

        return next;
    }

    private static void Write(string path, DdsFormat format, int texels, List<byte[]> mips)
    {
        using var file = File.Create(path);
        DdsFile.Write(file, format, texels, texels, mips);
    }

    private static uint Rgba(byte r, byte g, byte b, byte a) => r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

    private static float ToLinear(byte value)
    {
        var c = value / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static byte ToSrgb(float value)
    {
        var c = Math.Clamp(value, 0f, 1f);
        var s = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
        return (byte)MathF.Round(s * 255f);
    }

    private static byte Unit(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    /// <summary>A normal component, -1 to 1, as the byte the shader reads back as 2b/255 - 254/255 (127 for exactly 0).</summary>
    private static byte Signed(float value) => (byte)Math.Clamp(MathF.Round(127f + value * 127.5f), 0f, 255f);

    private static string Link(Source source) =>
        source.Site switch
        {
            "polyhaven" => $"https://polyhaven.com/a/{source.Asset}",
            "ambientcg" => $"https://ambientcg.com/view?id={source.Asset}",
            "recipe" => $"made by the recipe {source.Asset}",
            _ => "baked by the game (materials bake)",
        };

    private static void WriteSourcesList(string folder, List<Source> sources)
    {
        var text = new StringBuilder();
        text.AppendLine("# The world materials' texture sets and their sources");
        text.AppendLine();
        text.AppendLine(
            "Every set is made by `tools/Textures` from the game's own recipe baked by the console's `materials bake`, from "
                + "a texture recipe the tool runs (`tools/Textures/RecipeTools`), or from a CC0 1.0 photograph (public domain: commercial use, redistribution in a game, and no credit are all "
                + "allowed; Poly Haven's and ambientCG's licence pages, checked 2026-10-08). Written by the tool from "
                + "`sources.json`."
        );
        text.AppendLine();
        text.AppendLine("| Set | Texels | Site | Asset | Licence |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var source in sources)
        {
            var texels = source.Texels.ToString(CultureInfo.InvariantCulture);
            text.AppendLine(
                source.Site switch
                {
                    "bake" =>
                        $"| {source.Set} | {texels} | bake | {source.Asset}: the game's recipe, baked | the game's own |",
                    "recipe" => $"| {source.Set} | {texels} | recipe | `{source.Asset}` | the game's own |",
                    _ => $"| {source.Set} | {texels} | {source.Site} | [{source.Asset}]({Link(source)}) | CC0 1.0 |",
                }
            );
        }

        File.WriteAllText(Path.Combine(folder, "SOURCES.md"), text.ToString());
    }

    private sealed record Source(string Set, string Site, string Asset, int Texels, KuwaharaSettings? Kuwahara = null);

    /// <summary>
    /// The anisotropic Kuwahara filter's settings for a set (<c>"kuwahara": { "radius": 8 }</c> in sources.json): the
    /// ellipse's radius in texels where the photograph has no direction, the sectors' sharpness and hardness, and how far
    /// the ellipse stretches along the photograph's direction.
    /// </summary>
    private sealed record KuwaharaSettings(
        float Radius,
        float Sharpness = 8f,
        float Hardness = 8f,
        float Alpha = 1f,
        bool AllMaps = false
    );

    private sealed record Maps(string Colour, string Normal, string? Occlusion, string? Height, float Metres);
}
