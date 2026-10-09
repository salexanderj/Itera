using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Audio;

/// <summary>
/// One-shot recipes' rendered variations on disk (<c>SoundCache/</c> in the data folder), so a start after the first
/// loads them instead of rendering them again (0.8 to 2.7 s in Release, 12 to 15 s in Debug, measured
/// 2026-10-03). A file holds one recipe's variations as 16-bit samples, named by the recipe and a key: a hash of the file
/// format, the sample rate, the levels and variations, the recipe's name and type, the build's configuration, and the
/// sound code of the recipe's assembly and of the engine (<see cref="SoundCodeOf"/>: a hash of the source files under
/// their <c>Audio/</c> folders, made at build time). A change to a recipe or to the synthesis blocks therefore renders it
/// again, and a commit that changes no sound code does not (the key holds no module version ids, which the
/// commit's hash changes). Files are written under a temporary name and renamed, so a crash or a second game never
/// leaves half a file under the final name; a damaged file is deleted. The newest <see cref="KeptVersions"/> files of
/// each recipe are kept (a Debug and a Release build have different keys), a read marks its file as the newest, and
/// <see cref="Prune(SoundRecipes)"/> deletes the files of recipes no longer registered and temporary files left by a
/// crash. A folder that cannot be written is not written again, but is still read. Used on the render thread only.
/// </summary>
public sealed partial class SoundCache
{
    /// <summary>The file format; raise it when the layout below changes.</summary>
    public const int Format = 1;

    /// <summary>How many files of one recipe are kept: the newest, by the time each was written or last read.</summary>
    public const int KeptVersions = 2;

    /// <summary>"ITSN", little-endian.</summary>
    private const uint Magic = 0x4E53_5449;

    private const int HeaderBytes = 5 * sizeof(int);

    /// <summary>The key's length in hex digits: 128 bits of the hash.</summary>
    private const int KeyDigits = 32;

    private const string Extension = ".sound";

    /// <summary>The assembly metadata the build writes the sound code's hash under (Directory.Build.targets).</summary>
    public const string SoundCodeKey = "SoundCode";

    /// <summary>How old a temporary file must be before it counts as left by a crash.</summary>
    private static readonly TimeSpan s_staleTemporary = TimeSpan.FromHours(1);

    private readonly string _directory;
    private readonly ILogger _logger;
    private bool _writable = true;

    public SoundCache(string directory, ILogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>The folder the files are in.</summary>
    public string Directory => _directory;

    /// <summary>The file for recipe <paramref name="name"/> rendered at <paramref name="sampleRate"/>.</summary>
    public string PathFor(string name, OneShotRecipe recipe, int sampleRate) =>
        Path.Combine(_directory, SafeName(name) + "-" + Key(name, recipe, sampleRate) + Extension);

    /// <summary>The cache key of recipe <paramref name="name"/> rendered at <paramref name="sampleRate"/>, in hex.</summary>
    public static string Key(string name, OneShotRecipe recipe, int sampleRate) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(KeyText(name, recipe, sampleRate))))[..KeyDigits];

    /// <summary>What <see cref="Key"/> hashes.</summary>
    internal static string KeyText(string name, OneShotRecipe recipe, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var type = recipe.GetType();
        return string.Join(
            '\0',
            Format.ToString(CultureInfo.InvariantCulture),
            sampleRate.ToString(CultureInfo.InvariantCulture),
            recipe.Levels.ToString(CultureInfo.InvariantCulture),
            recipe.VariationsPerLevel.ToString(CultureInfo.InvariantCulture),
            name,
            type.FullName,
            Configuration,
            SoundCodeOf(type.Assembly),
            SoundCodeOf(typeof(SoundCache).Assembly)
        );
    }

    /// <summary>
    /// The hash of <paramref name="assembly"/>'s sound code, written by the build as its <see cref="SoundCodeKey"/>
    /// metadata; for an assembly without it (a mod's), its module version id, which changes with any change to it.
    /// </summary>
    public static string SoundCodeOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (metadata.Key == SoundCodeKey && !string.IsNullOrEmpty(metadata.Value))
            {
                return metadata.Value;
            }
        }

        return assembly.ManifestModule.ModuleVersionId.ToString("N");
    }

    /// <summary>The engine's build configuration: Debug and Release builds keep their own files.</summary>
    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>
    /// Reads the variations of <paramref name="recipe"/> from <paramref name="path"/>: each one's length in samples, and
    /// every sample back to back. False when there is no file or it cannot be read; a file that does not match the
    /// recipe is deleted.
    /// </summary>
    public bool TryRead(string path, OneShotRecipe recipe, int sampleRate, out int[] lengths, out short[] samples)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        lengths = [];
        samples = [];
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnavailable(path, ex.Message);
            return false;
        }

        if (!TryParse(bytes, recipe, sampleRate, out lengths, out samples))
        {
            LogDamaged(path, bytes.Length);
            TryDelete(path);
            lengths = [];
            samples = [];
            return false;
        }

        try
        {
            // Marks it as the newest of its recipe's files, so the one in use is never pruned.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnavailable(path, ex.Message);
        }

        return true;
    }

    /// <summary>
    /// Writes the variations to <paramref name="path"/> (under a temporary name, then renamed), then deletes all but the
    /// newest <see cref="KeptVersions"/> files of recipe <paramref name="name"/>. A folder that cannot be written is
    /// logged once and not written again; reads go on.
    /// </summary>
    public void Write(
        string path,
        string name,
        OneShotRecipe recipe,
        int sampleRate,
        ReadOnlySpan<int> lengths,
        ReadOnlySpan<short> samples
    )
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (!_writable)
        {
            return;
        }

        var bytes = new byte[HeaderBytes + (lengths.Length * sizeof(int)) + (samples.Length * sizeof(short))];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], Format);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], recipe.Levels);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], recipe.VariationsPerLevel);
        var offset = HeaderBytes;
        foreach (var length in lengths)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], length);
            offset += sizeof(int);
        }

        foreach (var sample in samples)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[offset..], sample);
            offset += sizeof(short);
        }

        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writable = false;
            LogUnavailable(path, ex.Message);
            TryDelete(temporaryPath);
            return;
        }

        Prune(name);
    }

    /// <summary>
    /// Parses a file's bytes: the header must name this format, rate, and recipe's levels and variations, each length
    /// must be 1 to the recipe's samples, and the size must be exact.
    /// </summary>
    private static bool TryParse(
        ReadOnlySpan<byte> bytes,
        OneShotRecipe recipe,
        int sampleRate,
        out int[] lengths,
        out short[] samples
    )
    {
        lengths = [];
        samples = [];
        var total = recipe.Levels * recipe.VariationsPerLevel;
        if (
            bytes.Length < HeaderBytes + (total * sizeof(int))
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != Format
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != sampleRate
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) != recipe.Levels
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]) != recipe.VariationsPerLevel
        )
        {
            return false;
        }

        var most = recipe.SampleCount(sampleRate);
        var read = new int[total];
        long count = 0;
        for (var i = 0; i < total; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[(HeaderBytes + (i * sizeof(int)))..]);
            if (length < 1 || length > most)
            {
                return false;
            }

            read[i] = length;
            count += length;
        }

        var start = HeaderBytes + (total * sizeof(int));
        if (bytes.Length - start != count * sizeof(short))
        {
            return false;
        }

        var pcm = new short[count];
        var source = bytes[start..];
        if (BitConverter.IsLittleEndian)
        {
            source.CopyTo(MemoryMarshal.AsBytes(pcm.AsSpan()));
        }
        else
        {
            for (var i = 0; i < pcm.Length; i++)
            {
                pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(i * sizeof(short))..]);
            }
        }

        lengths = read;
        samples = pcm;
        return true;
    }

    /// <summary>
    /// Deletes the files of recipes not among <paramref name="recipes"/> (removed or renamed since they were written) and
    /// temporary files older than an hour (left by a crash), once every recipe registered is rendered. Render thread.
    /// </summary>
    public void Prune(SoundRecipes recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        if (!_writable || !System.IO.Directory.Exists(_directory))
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var id = 0; id < recipes.Count; id++)
        {
            names.Add(SafeName(recipes.NameOf(id)));
        }

        try
        {
            var now = DateTime.UtcNow;
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
            {
                if (file.Name.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    if (now - file.LastWriteTimeUtc > s_staleTemporary)
                    {
                        TryDelete(file.FullName);
                    }
                }
                else if (RecipeOf(file.Name) is { } recipe && !names.Contains(recipe))
                {
                    TryDelete(file.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnavailable(_directory, ex.Message);
        }
    }

    /// <summary>The recipe's safe name in a cache file's name (<c>name-KEY.sound</c>); null for any other file.</summary>
    private static string? RecipeOf(string fileName)
    {
        var keyStart = fileName.Length - Extension.Length - KeyDigits;
        if (
            !fileName.EndsWith(Extension, StringComparison.Ordinal)
            || keyStart < 2
            || fileName[keyStart - 1] != '-'
            || !IsHex(fileName.AsSpan(keyStart, KeyDigits))
        )
        {
            return null;
        }

        return fileName[..(keyStart - 1)];
    }

    /// <summary>Deletes all but the newest <see cref="KeptVersions"/> files of recipe <paramref name="name"/>.</summary>
    private void Prune(string name)
    {
        var prefix = SafeName(name) + "-";
        List<FileInfo> files;
        try
        {
            files = [];
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles(prefix + "*" + Extension))
            {
                // "step" must not take "step-soft"'s files: only the prefix, a key, and the extension.
                var key = file.Name.AsSpan(prefix.Length, file.Name.Length - prefix.Length - Extension.Length);
                if (key.Length == KeyDigits && IsHex(key))
                {
                    files.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnavailable(_directory, ex.Message);
            return;
        }

        if (files.Count <= KeptVersions)
        {
            return;
        }

        files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        for (var i = KeptVersions; i < files.Count; i++)
        {
            TryDelete(files[i].FullName);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnavailable(path, ex.Message);
        }
    }

    /// <summary>The name with anything but letters, digits, '-', and '_' as '_', for a file name on every platform.</summary>
    private static string SafeName(string name)
    {
        return string.Create(
            name.Length,
            name,
            static (span, source) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    var c = source[i];
                    span[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
                }
            }
        );
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigitUpper(c))
            {
                return false;
            }
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio: sound cache unavailable at {Path}: {Reason}")]
    private partial void LogUnavailable(string path, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Audio: cached sound {Path} ({Bytes} bytes) does not match its recipe; deleted, rendering again"
    )]
    private partial void LogDamaged(string path, int bytes);
}
