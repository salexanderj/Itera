using System.Diagnostics;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Itera.Engine.Assets;
using Itera.Engine.Rendering.Materials;

namespace Itera.Tools.Textures;

/// <summary>
/// The model mode (<c>model PATH.gltf ...</c>, README.md): for each model, every texture its import makes, decoded and
/// given every level by the engine as a run without compressed textures makes them (<see cref="CompressedTextures"/>),
/// compressed level by level (BC7 for colour and linear data, BC5 for normal maps), written as DDS files into the
/// model's <c>Model.textures</c> folder with the list of them and their images' hashes. Files of the folder the new list
/// does not name are deleted, and a model with no textures left loses its folder's files. A model that cannot be read is
/// reported and the next one made; the exit code is 1 when any failed.
/// </summary>
internal static class ModelTextures
{
    public static int Run(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("Usage: model PATH.gltf [PATH.gltf ...] (or .glb)");
            return 1;
        }

        var failed = 0;
        foreach (var path in paths)
        {
            try
            {
                Make(Path.GetFullPath(path));
            }
            catch (Exception ex)
                when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static void Make(string path)
    {
        var started = Stopwatch.GetTimestamp();
        var warnings = new List<string>();
        var textures = CompressedTextures.DecodeAll(path, warnings);
        foreach (var warning in warnings)
        {
            Console.Error.WriteLine(warning);
        }

        var model = Path.GetFileName(path);
        var folder = CompressedTextures.FolderOf(path);
        if (textures.Count == 0)
        {
            // An earlier run's files would still ship with the model.
            if (Directory.Exists(folder))
            {
                foreach (var old in Directory.GetFiles(folder, "*.dds"))
                {
                    File.Delete(old);
                }

                File.Delete(Path.Combine(folder, CompressedTextures.ListFile));
                if (Directory.GetFileSystemEntries(folder).Length == 0)
                {
                    Directory.Delete(folder);
                }
            }

            Console.WriteLine($"{model}: no textures to compress.");
            return;
        }

        Directory.CreateDirectory(folder);
        var entries = new List<CompressedTextureEntry>();
        long decodedBytes = 0;
        long compressedBytes = 0;
        foreach (var texture in textures)
        {
            var format = CompressedTextures.FormatOf(texture.Role);
            var mips = Compress(texture, format);
            var file = CompressedTextures.FileName(texture.Image, texture.Role);
            using (var stream = File.Create(Path.Combine(folder, file)))
            {
                DdsFile.Write(stream, format, texture.Width, texture.Height, mips);
            }

            entries.Add(
                new CompressedTextureEntry(
                    texture.Image,
                    texture.Role,
                    file,
                    texture.Width,
                    texture.Height,
                    texture.SourceHash
                )
            );
            decodedBytes += texture.Texels.Length;
            compressedBytes += mips.Sum(mip => (long)mip.Length);
        }

        foreach (var stale in Directory.GetFiles(folder, "*.dds"))
        {
            if (!entries.Any(entry => string.Equals(entry.File, Path.GetFileName(stale), StringComparison.Ordinal)))
            {
                File.Delete(stale);
            }
        }

        var version = typeof(BcEncoder).Assembly.GetName().Version?.ToString(3) ?? "?";
        CompressedTextures.WriteList(
            folder,
            new CompressedTextureList(model, $"BCnEncoder.Net {version}, quality Balanced", entries)
        );
        Console.WriteLine(
            $"{model}: {entries.Count} texture(s) compressed into {Path.GetFileName(folder)}, "
                + $"{compressedBytes / 1048576.0:F2} MB (decoded {decodedBytes / 1048576.0:F2} MB), "
                + $"{Stopwatch.GetElapsedTime(started).TotalSeconds:F0} s"
        );
    }

    /// <summary>Each of the texture's levels, as the engine made them, compressed in <paramref name="format"/>.</summary>
    private static List<byte[]> Compress(MeshTextureData texture, DdsFormat format)
    {
        // The engine's levels are RGBA8, or for a normal map RG8 (x and y), packed from the largest.
        var bytesPerTexel = texture.Format == MeshTextureFormat.Rg8Unorm ? 2 : 4;
        var mips = new List<byte[]>();
        var offset = 0;
        for (var level = 0; level < texture.Levels; level++)
        {
            var width = Math.Max(1, texture.Width >> level);
            var height = Math.Max(1, texture.Height >> level);
            var texels = texture.Texels.AsSpan(offset, width * height * bytesPerTexel);
            offset += texels.Length;
            mips.Add(Encode(format, width, height, texels, bytesPerTexel));
        }

        return mips;
    }

    /// <summary>
    /// One level compressed: its texels padded to whole 4x4 blocks by repeating the last column and row (the padding is
    /// never sampled, and repeating the edge keeps it from pulling a block's colours away from the level's own).
    /// </summary>
    private static byte[] Encode(DdsFormat format, int width, int height, ReadOnlySpan<byte> texels, int bytesPerTexel)
    {
        var paddedWidth = (width + 3) / 4 * 4;
        var paddedHeight = (height + 3) / 4 * 4;
        var rgba = new byte[paddedWidth * paddedHeight * 4];
        for (var y = 0; y < paddedHeight; y++)
        {
            var row = Math.Min(y, height - 1) * width;
            for (var x = 0; x < paddedWidth; x++)
            {
                var source = (row + Math.Min(x, width - 1)) * bytesPerTexel;
                var at = (y * paddedWidth + x) * 4;
                rgba[at] = texels[source];
                rgba[at + 1] = texels[source + 1];
                rgba[at + 2] = bytesPerTexel == 4 ? texels[source + 2] : (byte)0;
                rgba[at + 3] = bytesPerTexel == 4 ? texels[source + 3] : (byte)255;
            }
        }

        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.Format = format == DdsFormat.Bc5 ? CompressionFormat.Bc5 : CompressionFormat.Bc7;
        encoder.Options.IsParallel = true;
        return encoder.EncodeToRawBytes(rgba, paddedWidth, paddedHeight, PixelFormat.Rgba32, 0, out _, out _);
    }
}
