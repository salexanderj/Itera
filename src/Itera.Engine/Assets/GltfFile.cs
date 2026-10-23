using SharpGLTF.Schema2;

namespace Itera.Engine.Assets;

/// <summary>
/// A glTF file opened once to read assets from (<see cref="GltfImport.Open"/>): its model, how its textures are read, and
/// what the assets read from it share, so each image is hashed, decoded, and made into a texture in each role once, and
/// every asset of the file that uses it holds the same <see cref="MeshTextureData"/>. One thread at a time.
/// </summary>
internal sealed class GltfFile(string path, ModelRoot model, TextureReading reading)
{
    private bool _listRead;
    private CompressedTextureList? _list;

    public string Path { get; } = path;

    public ModelRoot Model { get; } = model;

    public TextureReading Reading { get; } = reading;

    /// <summary>Each image read in a role, by its index and the role: its texture, or null for one that cannot be read.</summary>
    public Dictionary<(int Image, MeshTextureRole Role), MeshTextureData?> Textures { get; } = [];

    /// <summary>Each image decoded, or null for one that cannot be.</summary>
    public Dictionary<int, (int Width, int Height, byte[] Rgba)?> Decoded { get; } = [];

    /// <summary>Each image's hash (<see cref="CompressedTextures.Hash"/>), taken once for all its roles.</summary>
    public Dictionary<int, string> Hashes { get; } = [];

    /// <summary>True when the model has a list of compressed textures beside it, whether or not it reads.</summary>
    public bool HasCompressed { get; private set; }

    /// <summary>The hash of the image's bytes as the model stores them; empty for an image it does not hold.</summary>
    public string HashOf(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!Hashes.TryGetValue(image.LogicalIndex, out var hash))
        {
            var content = image.Content;
            hash = content.IsEmpty ? "" : CompressedTextures.Hash(content.Content.Span);
            Hashes[image.LogicalIndex] = hash;
        }

        return hash;
    }

    /// <summary>
    /// The model's list of compressed textures when the run prefers them and it has one, read the first time a texture
    /// asks (a list that cannot be read is a warning through <paramref name="warn"/>, and every texture is decoded).
    /// </summary>
    public CompressedTextureList? CompressedList(Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        if (_listRead || Reading != TextureReading.PreferCompressed)
        {
            return _list;
        }

        _listRead = true;
        var folder = CompressedTextures.FolderOf(Path);
        try
        {
            _list = CompressedTextures.ReadList(folder);
            HasCompressed = _list is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            HasCompressed = true;
            warn(
                $"{System.IO.Path.GetFileName(folder)}: {ex.Message}: its textures are decoded at load; make them again "
                    + $"with tools/Textures (`{CompressedTextures.Command}` on the source file)"
            );
        }

        return _list;
    }
}
