using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Assets;

/// <summary>
/// The run's mesh assets (<see cref="MeshAsset"/>): models a game declares from glTF files (<see cref="Declare"/>) and
/// meshes it builds in code (<see cref="Add"/>), each known by its name and its index (<see cref="Find"/>). A game
/// declares them at its run startup (<c>IRunStartup</c>, the run's "game" step), mods at theirs; the run's "mesh assets"
/// step, after the mods', reads every declared file on worker threads (<see cref="LoadAll"/>), and a file that cannot be
/// read stops the run with a message naming it. Assets stay for the whole run, so an index never goes stale and needs no
/// generation. A run without graphics reads the files without decoding their textures. Main thread.
/// </summary>
public sealed partial class MeshAssets(ILogger<MeshAssets> logger)
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    /// <summary>The assets declared and added.</summary>
    public int Count => _entries.Count;

    /// <summary>True once the run's "mesh assets" step has read every declared file.</summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// Declares the glTF file at <paramref name="path"/> (relative paths from the executable's folder) as the asset
    /// <paramref name="name"/>, its units <paramref name="metresPerUnit"/> metres (glTF's are metres; 0.01 for a file in
    /// centimetres), read at the run's "mesh assets" step. Returns its index. Before that step only.
    /// </summary>
    public int Declare(string name, string path, float metresPerUnit = 1f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!(metresPerUnit > 0) || !float.IsFinite(metresPerUnit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(metresPerUnit),
                metresPerUnit,
                "A model's unit must be a positive number of metres."
            );
        }

        var full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        return AddEntry(name, new Entry(name, full, metresPerUnit, null));
    }

    /// <summary>Adds a mesh built in code as the asset <paramref name="name"/> and returns its index. Before the "mesh assets" step only.</summary>
    public int Add(string name, MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return AddEntry(name, new Entry(name, null, 1f, asset));
    }

    /// <summary>The index of the asset <paramref name="name"/>, or -1 when there is none.</summary>
    public int Find(string name) => _byName.TryGetValue(name, out var index) ? index : -1;

    /// <summary>
    /// The asset at <paramref name="index"/> when there is one and it is read (a declared file's once the run's "mesh
    /// assets" step has read it; one added in code at once). Allocates nothing.
    /// </summary>
    public bool TryGet(int index, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MeshAsset? asset)
    {
        asset = (uint)index < (uint)_entries.Count ? _entries[index].Asset : null;
        return asset is not null;
    }

    /// <summary>The asset at <paramref name="index"/> (<see cref="Find"/>); a declared file's once the run has read it.</summary>
    public MeshAsset this[int index] =>
        _entries[index].Asset
        ?? throw new InvalidOperationException(
            $"The mesh asset '{_entries[index].Name}' is not read yet: models are read at the run's mesh assets step."
        );

    /// <summary>
    /// Reads every declared file not read yet, in parallel on worker threads, logs their warnings, and then the totals;
    /// with <paramref name="textures"/> false (no graphics), without decoding their images. Throws when a file cannot
    /// be read, after logging each that failed. The run's "mesh assets" step; once.
    /// </summary>
    internal void LoadAll(bool textures)
    {
        if (Loaded)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var warnings = new List<string>[_entries.Count];
        var failures = new Exception?[_entries.Count];
        Parallel.For(
            0,
            _entries.Count,
            i =>
            {
                var entry = _entries[i];
                warnings[i] = [];
                if (entry.Asset is not null || entry.Path is null)
                {
                    return;
                }

                try
                {
                    entry.Asset = GltfImport.Load(entry.Name, entry.Path, entry.MetresPerUnit, textures, warnings[i]);
                }
                catch (Exception ex)
                {
                    failures[i] = ex;
                }
            }
        );

        var failed = 0;
        string? firstFailure = null;
        for (var i = 0; i < _entries.Count; i++)
        {
            foreach (var warning in warnings[i])
            {
                LogWarning(_entries[i].Name, warning);
            }

            if (failures[i] is { } failure)
            {
                failed++;
                firstFailure ??= failure.Message;
                LogFailed(failure, _entries[i].Name, _entries[i].Path ?? "");
            }
        }

        if (failed > 0)
        {
            throw new InvalidOperationException($"{failed} mesh asset(s) could not be read; the first: {firstFailure}");
        }

        Loaded = true;
        long triangles = 0;
        long textureBytes = 0;
        var textureCount = 0;
        foreach (var entry in _entries)
        {
            triangles += entry.Asset!.TriangleCount;
            textureBytes += entry.Asset.TextureBytes;
            textureCount += entry.Asset.Textures.Length;
        }

        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var megabytes = textureBytes / (1024.0 * 1024.0);
        LogLoaded(
            _entries.Count,
            triangles,
            textureCount,
            megabytes,
            textures ? "" : " (not decoded: no graphics)",
            milliseconds
        );
    }

    private int AddEntry(string name, Entry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Loaded)
        {
            throw new InvalidOperationException(
                $"The mesh asset '{name}' comes after the run's mesh assets step: declare models at the run's startup."
            );
        }

        if (!_byName.TryAdd(name, _entries.Count))
        {
            throw new InvalidOperationException($"There is a mesh asset named '{name}' already.");
        }

        _entries.Add(entry);
        return _entries.Count - 1;
    }

    private sealed class Entry(string name, string? path, float metresPerUnit, MeshAsset? asset)
    {
        public string Name { get; } = name;

        public string? Path { get; } = path;

        public float MetresPerUnit { get; } = metresPerUnit;

        public MeshAsset? Asset { get; set; } = asset;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Mesh assets: {Models} model(s), {Triangles} triangles, {Textures} texture(s) of {Megabytes:F1} MB{Decoded}, read in {Milliseconds:F0} ms"
    )]
    private partial void LogLoaded(
        int models,
        long triangles,
        int textures,
        double megabytes,
        string decoded,
        double milliseconds
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mesh assets: {Name}: {Warning}")]
    private partial void LogWarning(string name, string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mesh assets: {Name} ({Path}) could not be read")]
    private partial void LogFailed(Exception exception, string name, string path);
}
