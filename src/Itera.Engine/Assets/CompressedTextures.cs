using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Itera.Engine.Rendering.Materials;

namespace Itera.Engine.Assets;

/// <summary>
/// A model's textures compressed offline by <c>tools/Textures</c>' model mode (its README): beside <c>Model.gltf</c>,
/// the folder <c>Model.textures</c> holds a DDS file for each texture the import makes (an image in a role: BC7 sRGB for
/// colour, BC7 for linear data, BC5 for normal maps), with every level the import would make at load
/// (<see cref="MipChain"/>) compressed, and <see cref="ListFile"/>, which names each file with the SHA-256 of the image's
/// bytes in the model it was made from. A run with graphics reads a texture from there when its entry's hash matches
/// the image's (<c>GltfImport</c>), and decodes the image at load otherwise. The format of the folder is defined here
/// alone, for the tool and the import.
/// </summary>
public static class CompressedTextures
{
    /// <summary>The list of the folder's textures.</summary>
    public const string ListFile = "textures.json";

    /// <summary>What a warning or note tells the model's author to run.</summary>
    public const string Command = "dotnet run -c Release --project tools/Textures -- model PATH.gltf";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The folder of the compressed textures of the model at <paramref name="modelPath"/>: <c>Model.textures</c> beside it.</summary>
    public static string FolderOf(string modelPath) =>
        Path.Combine(Path.GetDirectoryName(modelPath) ?? "", Path.GetFileNameWithoutExtension(modelPath) + ".textures");

    /// <summary>The file of image <paramref name="image"/> (glTF's index) in <paramref name="role"/>.</summary>
    public static string FileName(int image, MeshTextureRole role) =>
        role switch
        {
            MeshTextureRole.Colour => $"image{image}-colour.dds",
            MeshTextureRole.Linear => $"image{image}-linear.dds",
            MeshTextureRole.Normal => $"image{image}-normal.dds",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    /// <summary>The compressed format of a texture in <paramref name="role"/>.</summary>
    public static DdsFormat FormatOf(MeshTextureRole role) =>
        role switch
        {
            MeshTextureRole.Colour => DdsFormat.Bc7Srgb,
            MeshTextureRole.Linear => DdsFormat.Bc7Unorm,
            MeshTextureRole.Normal => DdsFormat.Bc5,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    /// <summary>The SHA-256 of an image's bytes as the model stores them (PNG, JPEG, or WebP), in lower-case hex.</summary>
    public static string Hash(ReadOnlySpan<byte> encoded) => Convert.ToHexStringLower(SHA256.HashData(encoded));

    /// <summary>
    /// Every texture the import of the model at <paramref name="modelPath"/> makes, decoded and given every level as a
    /// run without compressed textures makes it (RGBA8, or RG8 for a normal map), with its image and that image's hash:
    /// what <c>tools/Textures</c> compresses. Warnings go into <paramref name="warnings"/>. Throws
    /// <see cref="InvalidDataException"/> for a file the import cannot read, and the file system's exceptions for one
    /// it cannot open.
    /// </summary>
    public static IReadOnlyList<MeshTextureData> DecodeAll(string modelPath, List<string> warnings) =>
        GltfImport
            .Load(Path.GetFileNameWithoutExtension(modelPath), modelPath, 1f, TextureReading.Decode, warnings)
            .Textures;

    /// <summary>
    /// The list in <paramref name="folder"/>, or null when it has none. Throws <see cref="InvalidDataException"/> for a
    /// list that does not parse or names a file outside the folder.
    /// </summary>
    public static CompressedTextureList? ReadList(string folder)
    {
        var path = Path.Combine(folder, ListFile);
        if (!File.Exists(path))
        {
            return null;
        }

        CompressedTextureList? list;
        try
        {
            list = JsonSerializer.Deserialize<CompressedTextureList>(File.ReadAllText(path), s_json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{ListFile} does not parse: {ex.Message}", ex);
        }

        if (list?.Textures is null)
        {
            throw new InvalidDataException($"{ListFile} lists no textures.");
        }

        foreach (var entry in list.Textures)
        {
            // A file of the folder itself (no folder in its name, no character a file name cannot hold), and a hash.
            if (
                entry?.File is not { Length: > 0 } file
                || string.IsNullOrEmpty(entry.Sha256)
                || !string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal)
                || file.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            )
            {
                throw new InvalidDataException($"{ListFile} has an entry without a file of the folder's or a hash.");
            }
        }

        return list;
    }

    /// <summary>Writes <paramref name="list"/> into <paramref name="folder"/>.</summary>
    public static void WriteList(string folder, CompressedTextureList list) =>
        File.WriteAllText(Path.Combine(folder, ListFile), JsonSerializer.Serialize(list, s_json) + "\n");

    /// <summary>
    /// Texture <paramref name="image"/> in <paramref name="role"/> from the folder, when <paramref name="list"/> has it,
    /// made from an image of <paramref name="hash"/>, in its role's format, at the entry's size, with every level whole;
    /// otherwise null and what is wrong in <paramref name="problem"/>.
    /// </summary>
    internal static MeshTextureData? TryRead(
        string folder,
        CompressedTextureList list,
        string name,
        int image,
        MeshTextureRole role,
        string hash,
        out string problem
    )
    {
        problem = "";
        CompressedTextureEntry? entry = null;
        foreach (var candidate in list.Textures)
        {
            if (candidate.Image == image && candidate.Role == role)
            {
                entry = candidate;
                break;
            }
        }

        if (entry is null)
        {
            problem = $"{ListFile} has no entry for it";
            return null;
        }

        if (!string.Equals(entry.Sha256, hash, StringComparison.Ordinal))
        {
            problem = $"{entry.File} was made from another image (the hashes differ)";
            return null;
        }

        byte[] bytes;
        DdsFile dds;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(folder, entry.File));
            dds = DdsFile.Read(bytes);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or InvalidDataException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
            )
        {
            problem = $"{entry.File} cannot be read: {ex.Message}";
            return null;
        }

        var format = FormatOf(role);
        if (dds.Format != format)
        {
            problem = $"{entry.File} is {dds.Format}, not {format}";
            return null;
        }

        if (dds.Width != entry.Width || dds.Height != entry.Height)
        {
            problem = $"{entry.File} is {dds.Width}x{dds.Height}, not the {entry.Width}x{entry.Height} its entry gives";
            return null;
        }

        var levels = dds.Offsets.Count;
        var start = dds.Offsets[0];
        var end = dds.Offsets[levels - 1] + dds.Lengths[levels - 1];
        var meshFormat = role switch
        {
            MeshTextureRole.Colour => MeshTextureFormat.Bc7Srgb,
            MeshTextureRole.Linear => MeshTextureFormat.Bc7Unorm,
            _ => MeshTextureFormat.Bc5Unorm,
        };
        return new MeshTextureData(
            name,
            dds.Width,
            dds.Height,
            levels,
            role,
            meshFormat,
            bytes[start..end],
            image,
            hash
        );
    }
}

/// <summary>A model's compressed textures (<see cref="CompressedTextures.ListFile"/>): the model's file, the encoder, and each texture.</summary>
public sealed record CompressedTextureList(
    string Model,
    string Encoder,
    IReadOnlyList<CompressedTextureEntry> Textures
);

/// <summary>
/// One compressed texture: the model's image (glTF's index) and the role it was made for, its DDS file in the folder,
/// its size, and the SHA-256 of the image's bytes it was made from (<see cref="CompressedTextures.Hash"/>).
/// </summary>
public sealed record CompressedTextureEntry(
    int Image,
    MeshTextureRole Role,
    string File,
    int Width,
    int Height,
    string Sha256
);
