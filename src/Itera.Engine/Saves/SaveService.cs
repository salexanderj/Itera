using System.Diagnostics;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Saves;

/// <summary>
/// The named save (<c>Engine:Save:Name</c>): a host or offline game starts from
/// <c>Saves/NAME.save</c> in the data folder when it exists, and writes its world back to it when it quits. A save is the
/// world's edit log with the match settings (<see cref="VoxelWorld.SaveEdits"/>, format 6): the generator, the seed, the
/// step rate, the game's match data (its rules), and every edit with its pieces. At start (<see cref="ReadStart"/>, from
/// the session step) the save's seed and rules replace the options'; its edits load once the world exists
/// (<see cref="LoadEdits"/>). At quit (<see cref="Save"/>), only after a loop that ended without an exception, every
/// thrown ball still loose goes back into the grid where it is, then the log is written to a temporary file, the old
/// save is kept as <c>NAME.save.bak</c>, and the new one takes its place. A client never saves. A game without voxel
/// terrain saves the header alone, with its match identity (<see cref="IMatchIdentity"/>) where a world writes its
/// generator, and no edits (<see cref="EditLogSerializer.WriteEmpty"/>). Main thread.
/// </summary>
public sealed partial class SaveService
{
    private readonly string? _name;
    private readonly string _folder;
    private readonly VoxelWorld? _voxels;
    private readonly IMatchIdentity _identity;
    private readonly LooseBodies? _bodies;
    private readonly ILogger<SaveService> _logger;
    private byte[]? _loaded;

    public SaveService(
        IOptions<EngineOptions> options,
        MatchContext match,
        IMatchIdentity identity,
        ILogger<SaveService> logger,
        VoxelWorld? voxels = null,
        LooseBodies? bodies = null
    )
        : this(match.Start.SaveName, SaveSettings.Folder(options.Value.Application), voxels, bodies, logger, identity)
    { }

    /// <param name="name">The save's name, or null for none.</param>
    /// <param name="folder">The folder that holds the saves.</param>
    /// <param name="voxels">The world saved and loaded; null for a game without voxel terrain.</param>
    /// <param name="bodies">The loose bodies put back before saving; null for none.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="identity">The match's identity, which a world-less save is written and read with; the world's generator's when null.</param>
    internal SaveService(
        string? name,
        string folder,
        VoxelWorld? voxels,
        LooseBodies? bodies,
        ILogger<SaveService> logger,
        IMatchIdentity? identity = null
    )
    {
        _name = name;
        _folder = folder;
        _voxels = voxels;
        _identity =
            identity
            ?? (
                voxels is null
                    ? throw new ArgumentNullException(
                        nameof(identity),
                        "A save without a world needs the match's identity."
                    )
                    : new GeneratorMatchIdentity(voxels.Generator)
            );
        _bodies = bodies;
        _logger = logger;
    }

    /// <summary>The save's name, or null when this game saves nothing.</summary>
    public string? Name => _name;

    /// <summary>The save's file, or null without a name.</summary>
    public string? FilePath => _name is null ? null : Path.Combine(_folder, _name + ".save");

    /// <summary>True when this game started from its save's file.</summary>
    public bool StartedFromSave { get; private set; }

    /// <summary>
    /// The session step, on a host or offline: reads the save's header when its file exists, and returns it (its seed
    /// and match settings are the match's), or null for a new world. Throws <see cref="SessionStartException"/>, with
    /// the file left as it is, when the file is not a save, cannot be read, comes from another generator or generator
    /// version, or runs at another step rate than <paramref name="fixedStepHz"/>.
    /// </summary>
    internal EditLogHeader? ReadStart(int fixedStepHz, ulong configuredSeed)
    {
        if (FilePath is not { } path)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            LogNew(_name!, configuredSeed, path);
            return null;
        }

        EditLogHeader header;
        try
        {
            _loaded = File.ReadAllBytes(path);
            var stream = new MemoryStream(_loaded, writable: false);
            header =
                _voxels?.ReadSaveHeader(stream)
                ?? EditLogSerializer.ReadHeader(stream, _identity.Id, _identity.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _loaded = null;
            throw new SessionStartException($"The save {path} cannot be used: {ex.Message}", ex);
        }

        // Formats 1 and 2 hold no step rate: they are taken as this game's.
        if (header.FixedStepHz != 0 && header.FixedStepHz != fixedStepHz)
        {
            _loaded = null;
            throw new SessionStartException(
                $"The save {path} runs at {header.FixedStepHz} steps per second; this game at {fixedStepHz} (Engine:FixedStepHz)."
            );
        }

        StartedFromSave = true;
        LogStarting(_name!, header.Seed, header.Count, path);
        if (header.Seed != configuredSeed)
        {
            LogSeedReplaced(header.Seed, configuredSeed);
        }

        return header;
    }

    /// <summary>The session step, on a client: says once that a client does not save.</summary>
    internal void IgnoreOnClient()
    {
        if (_name is not null)
        {
            LogClient(_name);
        }
    }

    /// <summary>
    /// The step after the world starts: loads the save's edits into it when this game started from its save. Throws
    /// <see cref="SessionStartException"/> when they cannot be loaded.
    /// </summary>
    internal void LoadEdits()
    {
        if (_loaded is null)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        long count;
        try
        {
            count = _voxels?.LoadEdits(new MemoryStream(_loaded, writable: false)) ?? CheckNoEdits(_loaded);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            throw new SessionStartException($"The save {FilePath} cannot be loaded: {ex.Message}", ex);
        }
        finally
        {
            _loaded = null;
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LogLoaded(count, _name!, elapsed);
    }

    /// <summary>
    /// The shutdown of the save step: writes the world to the save on a host or offline game with a name, when the loop
    /// ended without an exception (<paramref name="loopEnded"/>); otherwise the state may be broken, and nothing is
    /// written. Every thrown ball still loose goes back into the grid first. A failure to write is logged at Error.
    /// </summary>
    public void Save(bool loopEnded, SessionRole role, MatchSettings match)
    {
        if (FilePath is not { } path)
        {
            return;
        }

        if (role == SessionRole.Client)
        {
            return;
        }

        if (!loopEnded)
        {
            LogNotSaved(_name!);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var settled = _bodies?.SettleThrown() ?? 0;
        var temporary = path + ".tmp";
        try
        {
            using var bytes = new MemoryStream();
            // Only the edits since the last reset: a lost game's restart leaves the earlier games' edits out.
            long written = 0;
            if (_voxels is null)
            {
                EditLogSerializer.WriteEmpty(
                    bytes,
                    _identity.Id,
                    _identity.Version,
                    match.Seed,
                    match.FixedStepHz,
                    match.Game
                );
            }
            else
            {
                written = _voxels.SaveEdits(bytes, match.FixedStepHz, match.Game, afterLastReset: true);
            }

            Directory.CreateDirectory(_folder);
            using (var file = File.Create(temporary))
            {
                bytes.Position = 0;
                bytes.CopyTo(file);
                file.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            File.Move(temporary, path, overwrite: true);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var megabytes = bytes.Length / (1024.0 * 1024.0);
            LogSaved(_name!, written, megabytes, elapsed, settled, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Told at the menu too, and no half-written file left.
            LogFailed(ex, _name!, path);
            Failure = $"The save {_name} could not be written: {ex.Message}";
            TryDelete(temporary);
        }
    }

    /// <summary>A world-less game's save: its header read again, and no edits in it. Returns 0.</summary>
    private long CheckNoEdits(byte[] save)
    {
        var edits = new List<VoxelEdit>();
        EditLogSerializer.Read(new MemoryStream(save, writable: false), _identity.Id, _identity.Version, edits, []);
        return edits.Count == 0
            ? 0
            : throw new InvalidDataException($"It holds {edits.Count} voxel edit(s), and this game has no world.");
    }

    /// <summary>Why the last <see cref="Save"/> failed; null when it did not. The menu shows it.</summary>
    public string? Failure { get; private set; }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder in its place, or no right to: nothing more to do.
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: {Name} does not exist yet; a new world (seed {Seed}) is written to {Path} at quit"
    )]
    private partial void LogNew(string name, ulong seed, string path);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: starting from {Name} (seed {Seed}, {Edits} edit(s)) at {Path}; its seed and match settings replace the options'"
    )]
    private partial void LogStarting(string name, ulong seed, long edits, string path);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: the save's seed {Seed} is used, not Engine:Voxels:Seed {Configured}"
    )]
    private partial void LogSeedReplaced(ulong seed, ulong configured);

    [LoggerMessage(Level = LogLevel.Information, Message = "Save: loaded {Edits} edit(s) of {Name} in {Ms:F1} ms")]
    private partial void LogLoaded(long edits, string name, double ms);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Save: a client does not save; Engine:Save:Name {Name} is not used"
    )]
    private partial void LogClient(string name);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Save: {Name} was not written: the game stopped on an error, and its state may be broken"
    )]
    private partial void LogNotSaved(string name);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: wrote {Name} ({Edits} edit(s), {Megabytes:F2} MB) in {Ms:F1} ms, after putting {Settled} loose ball(s) back into the grid; {Path}"
    )]
    private partial void LogSaved(string name, long edits, double megabytes, double ms, int settled, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Save: could not write {Name} to {Path}")]
    private partial void LogFailed(Exception exception, string name, string path);
}
