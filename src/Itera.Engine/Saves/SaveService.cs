using System.Diagnostics;
using System.Text;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Saves;

/// <summary>
/// The named save (<c>Engine:Save:Name</c>): a host or offline game starts from <c>Saves/NAME.save</c> in the data
/// folder when it exists, and writes its match back to it. A save is the world's edit log with the match settings
/// (format 8): the generator, the seed, the step rate, the game's match data (its rules), every edit since the last
/// reset with its pieces, and the game's own state in sections (<see cref="ISaveSection"/>).
/// <list type="bullet">
/// <item>At start (<see cref="ReadStart"/>, from the session step) the whole file is read and checked, and its seed and
/// rules replace the options'; when it is damaged, <c>NAME.save.bak</c> is used instead, with a warning, and the next
/// write keeps the damaged file as <c>NAME.save.damaged</c> (a save that is whole but of another generator or a newer
/// build, or that cannot be opened, ends the start instead, and a save that is gone starts a new world). Its edits
/// load once the world exists (<see cref="LoadEdits"/>), and its sections go to the game's at the "save sections" step
/// (<see cref="BeginSections"/>), after the systems are made and before the first step.</item>
/// <item>A save is captured on the main thread between steps (the edit log frozen, <see cref="EditLog.Freeze"/>, and
/// each section's bytes), then written: to a temporary file, the old save kept as <c>NAME.save.bak</c>, and the new one
/// put in its place. One write runs at a time.</item>
/// <item>Every <c>Engine:Save:AutosaveMinutes</c> of the match's steps (<see cref="AfterFixedStep"/>), and at the
/// console's <c>save</c> (<see cref="SaveNow"/>), the capture is written on a background thread with a scratch of its own,
/// so no step waits for the disk; loose bodies are left out.</item>
/// <item>At a clean end, the last capture is taken before the ECS world goes (<see cref="EndSections"/>), after putting
/// every thrown ball still loose back into the grid, and written while the world's memory still exists
/// (<see cref="WriteAtEnd"/>, which first waits for a write still running). After an exception nothing is written,
/// and the last save stays.</item>
/// </list>
/// A client never saves. A game without voxel terrain saves the header and its sections, with its match identity
/// (<see cref="IMatchIdentity"/>) where a world writes its generator, and no edits (<see cref="EditLogSerializer.WriteEmpty"/>).
/// Main thread, but for the background writes.
/// </summary>
public sealed partial class SaveService
{
    private readonly string? _name;
    private readonly string _folder;
    private readonly VoxelWorld? _voxels;
    private readonly IMatchIdentity _identity;
    private readonly LooseBodies? _bodies;
    private readonly ILogger<SaveService> _logger;
    private readonly double _autosaveMinutes;

    // The save read at the start: its edits until the world takes them, its sections until the game's take them.
    private ReadSave? _read;
    private List<SavedSection>? _restoring;

    // Started from NAME.save.bak because NAME.save is damaged: the next write keeps the damaged file aside, not as the
    // backup.
    private bool _fromBackup;

    private ISaveSection[] _sections = [];
    private MatchSettings _match;
    private bool _saving;
    private long _stepsPerAutosave;
    private long _stepsSinceSave;
    private Task? _writing;
    private byte[]? _scratch;
    private Capture? _last;

    public SaveService(
        IOptions<EngineOptions> options,
        MatchContext match,
        IMatchIdentity identity,
        ILogger<SaveService> logger,
        VoxelWorld? voxels = null,
        LooseBodies? bodies = null
    )
        : this(
            match.Start.SaveName,
            SaveSettings.Folder(options.Value.Application),
            voxels,
            bodies,
            logger,
            identity,
            options.Value.Save.AutosaveMinutes
        ) { }

    /// <param name="name">The save's name, or null for none.</param>
    /// <param name="folder">The folder that holds the saves.</param>
    /// <param name="voxels">The world saved and loaded; null for a game without voxel terrain.</param>
    /// <param name="bodies">The loose bodies put back before the last save; null for none.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="identity">The match's identity, which a world-less save is written and read with; the world's generator's when null.</param>
    /// <param name="autosaveMinutes">How often the match saves while it plays, in minutes of its steps; 0 for never.</param>
    internal SaveService(
        string? name,
        string folder,
        VoxelWorld? voxels,
        LooseBodies? bodies,
        ILogger<SaveService> logger,
        IMatchIdentity? identity = null,
        double autosaveMinutes = 0
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
        _autosaveMinutes = autosaveMinutes;
    }

    /// <summary>The save's name, or null when this game saves nothing.</summary>
    public string? Name => _name;

    /// <summary>The save's file, or null without a name.</summary>
    public string? FilePath => _name is null ? null : Path.Combine(_folder, _name + ".save");

    /// <summary>
    /// True when this game started from its save's file (or its backup): the match resumes, and the game's sections put
    /// its state back at the "save sections" step.
    /// </summary>
    public bool StartedFromSave { get; private set; }

    /// <summary>Why the last write failed; null when it did not. The menu shows it.</summary>
    public string? Failure { get; private set; }

    /// <summary>True while a save is being written on the background thread.</summary>
    public bool Writing => _writing is { IsCompleted: false };

    /// <summary>
    /// The session step, on a host or offline: reads the whole save when its file exists (or its backup, when the file is
    /// damaged), and returns its header (its seed and match settings are the match's), or null for a new world, also
    /// when the file is gone and its backup is not (a player who deletes a save starts afresh). Throws
    /// <see cref="SessionStartException"/>, with the files left as they are, when the save cannot be opened, is whole but
    /// of another generator, generator version, or a newer build (<see cref="EditLogSerializer.IsMismatch"/>: its backup
    /// is not used, so a later write cannot replace it), is damaged with no backup that reads, holds voxel edits in a game
    /// without a world, or runs at another step rate than <paramref name="fixedStepHz"/>.
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

        var backup = path + ".bak";
        ReadSave read;
        try
        {
            read = Read(path);
        }
        catch (InvalidDataException ex) when (!EditLogSerializer.IsMismatch(ex) && File.Exists(backup))
        {
            // Damaged: the backup, the save before it, takes its place.
            LogUsingBackup(_name!, ex.Message, backup);
            try
            {
                read = Read(backup);
            }
            catch (Exception second) when (IsReadFailure(second))
            {
                throw new SessionStartException(
                    $"The save {path} cannot be used: {ex.Message} Nor can its backup: {second.Message}",
                    ex
                );
            }

            _fromBackup = true;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            throw new SessionStartException($"The save {path} cannot be used: {ex.Message}", ex);
        }

        var header = read.Header;
        // Formats 1 and 2 hold no step rate: they are taken as this game's.
        if (header.FixedStepHz != 0 && header.FixedStepHz != fixedStepHz)
        {
            throw new SessionStartException(
                $"The save {path} runs at {header.FixedStepHz} steps per second; this game at {fixedStepHz} (Engine:FixedStepHz)."
            );
        }

        _read = read;
        _restoring = read.Sections;
        StartedFromSave = true;
        LogStarting(_name!, header.Seed, header.Count, read.Sections.Count, path);
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
        if (_read is not { } read)
        {
            return;
        }

        _read = null;
        if (_voxels is null)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        long count;
        try
        {
            count = _voxels.LoadEdits(read.Header, read.Edits, read.Pieces);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            throw new SessionStartException($"The save {FilePath} cannot be loaded: {ex.Message}", ex);
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LogLoaded(count, _name!, elapsed);
    }

    /// <summary>
    /// The "save sections" step, once the match's systems exist and before its first step: takes the game's sections
    /// (the match's <see cref="ISaveSection"/>s), and on a game that started from its save hands each section the save
    /// holds to the one of its name (a name the game has none of is passed over with a warning). From here a host or
    /// offline game with a name saves (<see cref="AfterFixedStep"/>, <see cref="SaveNow"/>). Throws
    /// <see cref="InvalidOperationException"/> when two sections share a name or one's name or version is not allowed,
    /// and <see cref="SessionStartException"/> when a saved section is of a newer version than the game's or cannot be
    /// read.
    /// </summary>
    internal void BeginSections(IEnumerable<ISaveSection> sections, SessionRole role, MatchSettings match)
    {
        _sections = [.. sections];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in _sections)
        {
            if (EditLogSerializer.SectionProblem(section.Name, section.Version) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            if (!names.Add(section.Name))
            {
                throw new InvalidOperationException($"Two save sections are named {section.Name}.");
            }
        }

        _match = match;
        _saving = FilePath is not null && role != SessionRole.Client;
        _stepsPerAutosave =
            _saving && _autosaveMinutes > 0
                ? Math.Max(1, (long)Math.Round(_autosaveMinutes * 60 * match.FixedStepHz))
                : 0;
        _stepsSinceSave = 0;
        if (_restoring is { } saved)
        {
            _restoring = null;
            Restore(saved);
        }
    }

    private void Restore(List<SavedSection> saved)
    {
        var start = Stopwatch.GetTimestamp();
        var restored = 0;
        foreach (var section in saved)
        {
            var own = Array.Find(_sections, s => string.Equals(s.Name, section.Name, StringComparison.Ordinal));
            if (own is null)
            {
                LogUnknownSection(_name!, section.Name, section.Bytes.Length);
                continue;
            }

            if (section.Version > own.Version)
            {
                throw new SessionStartException(
                    $"The save {FilePath} holds the section {section.Name} at version {section.Version}; this game reads it up to version {own.Version}, so a newer build wrote it."
                );
            }

            try
            {
                using var reader = new BinaryReader(
                    new MemoryStream(section.Bytes, writable: false),
                    Encoding.UTF8,
                    leaveOpen: false
                );
                own.Restore(reader, section.Version);
            }
            catch (Exception ex)
            {
                // Whatever the game's reader threw, the match does not start from a state half put back.
                throw new SessionStartException(
                    $"The save {FilePath} holds the section {section.Name}, which cannot be read: {ex.Message}",
                    ex
                );
            }

            restored++;
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LogRestored(restored, _name!, elapsed);
    }

    /// <summary>
    /// After each fixed step (<see cref="MatchServices.RunFixedStep"/>): on a host or offline game with a name and
    /// <c>Engine:Save:AutosaveMinutes</c> above 0, captures the match once that long has passed in its steps since the
    /// last save and writes it on a background thread; when the last write still runs, it tries again at the next step.
    /// Allocates nothing until a save is due.
    /// </summary>
    public void AfterFixedStep()
    {
        if (_stepsPerAutosave == 0 || ++_stepsSinceSave < _stepsPerAutosave || Writing)
        {
            return;
        }

        SaveInBackground("autosave");
    }

    /// <summary>
    /// The console's <c>save</c>, between steps: captures the match now and writes it on a background thread. Returns null
    /// when it did, or why it did nothing (no name, a client, or a write still running).
    /// </summary>
    public string? SaveNow()
    {
        if (!_saving)
        {
            return FilePath is null
                ? "this match has no save name (Engine:Save:Name)."
                : "this game does not save (a client, or a match not yet playing or ending).";
        }

        if (Writing)
        {
            return "a save is being written; try again in a moment.";
        }

        SaveInBackground("save");
        return null;
    }

    /// <summary>
    /// The shutdown of the "save sections" step, while the ECS world and the game's systems still exist: on a host or
    /// offline game with a name whose match ended without an exception (<paramref name="loopEnded"/>), puts every thrown
    /// ball still loose back into the grid and captures the last save, which <see cref="WriteAtEnd"/> writes. After an
    /// exception the state may be broken, and nothing is captured.
    /// </summary>
    internal void EndSections(bool loopEnded)
    {
        if (_saving)
        {
            if (loopEnded)
            {
                WaitForWrite();
                var settled = _bodies?.SettleThrown() ?? 0;
                _last = TryCapture("quit", settled, _match);
            }
            else
            {
                LogNotSaved(_name!);
            }
        }

        _saving = false;
        _stepsPerAutosave = 0;
        _sections = [];
    }

    /// <summary>
    /// The shutdown of the save step, before the world's memory goes: waits for a write still running, then writes the
    /// last capture (<see cref="EndSections"/>) on this thread. A failure to write is logged at Error, and kept in
    /// <see cref="Failure"/>.
    /// </summary>
    internal void WriteAtEnd()
    {
        WaitForWrite();
        if (_last is { } last)
        {
            _last = null;
            Write(last);
        }
    }

    /// <summary>
    /// Saves now, on this thread: on a host or offline game with a name, when <paramref name="loopEnded"/> (the state is
    /// whole), every thrown ball still loose goes back into the grid, then the world and the sections the game gave
    /// (<see cref="BeginSections"/>) are written. A failure to write is logged at Error, and kept in <see cref="Failure"/>.
    /// A match saves through its steps; this is for a game's tools and tests.
    /// </summary>
    public void Save(bool loopEnded, SessionRole role, MatchSettings match)
    {
        if (FilePath is null || role == SessionRole.Client)
        {
            return;
        }

        if (!loopEnded)
        {
            LogNotSaved(_name!);
            return;
        }

        WaitForWrite();
        var settled = _bodies?.SettleThrown() ?? 0;
        if (TryCapture("quit", settled, match) is { } capture)
        {
            Write(capture);
        }
    }

    private void SaveInBackground(string why)
    {
        _stepsSinceSave = 0;
        if (TryCapture(why, 0, _match) is { } capture)
        {
            _writing = Task.Run(() => Write(capture));
        }
    }

    /// <summary>
    /// <see cref="CaptureNow"/>, or null when a section (or the world) threw: logged at Error and kept in
    /// <see cref="Failure"/>, the last save left as it was, and the match going on.
    /// </summary>
    private Capture? TryCapture(string why, int settled, MatchSettings match)
    {
        try
        {
            return CaptureNow(why, settled, match);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(ex, _name!, why);
            Failure = $"The save {_name} could not be taken: {ex.Message}";
            return null;
        }
    }

    /// <summary>Waits for a write on the background thread, if one runs.</summary>
    private void WaitForWrite()
    {
        if (_writing is not { } writing)
        {
            return;
        }

        if (!writing.IsCompleted)
        {
            var start = Stopwatch.GetTimestamp();
            writing.Wait();
            var waited = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            LogWaited(_name!, waited);
        }

        _writing = null;
    }

    /// <summary>
    /// The match as it is now, on the main thread between steps: the edit log frozen (its written edits never change) and
    /// each section's bytes.
    /// </summary>
    private Capture CaptureNow(string why, int settled, MatchSettings match)
    {
        var start = Stopwatch.GetTimestamp();
        var edits = _voxels?.FreezeEdits();
        var sections = new SavedSection[_sections.Length];
        for (var i = 0; i < sections.Length; i++)
        {
            var section = _sections[i];
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
            {
                section.Capture(writer);
            }

            sections[i] = new SavedSection(section.Name, section.Version, bytes.ToArray());
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var count = edits?.Log.Count ?? 0;
        LogCaptured(why, _name!, count, sections.Length, elapsed);
        return new Capture(why, edits, match, sections, settled);
    }

    /// <summary>
    /// Writes a capture: to a temporary file, flushed to the disk, then the old save copied to the backup (unless the
    /// game started from the backup because the save would not read), then the new one put in the save's place. Any
    /// failure leaves the old save and no temporary file. On either thread, one write at a time.
    /// </summary>
    private void Write(Capture capture)
    {
        var path = FilePath!;
        var start = Stopwatch.GetTimestamp();
        var temporary = path + ".tmp";
        try
        {
            using var bytes = new MemoryStream();
            long written = 0;
            var match = capture.Match;
            if (capture.Edits is { } edits)
            {
                // Only the edits since the last reset: a lost game's restart leaves the earlier games' edits out.
                written = EditLogSerializer.Write(
                    bytes,
                    edits.WorldId,
                    edits.GeneratorVersion,
                    edits.Seed,
                    match.FixedStepHz,
                    match.Game,
                    edits.Log,
                    edits.Memory,
                    _scratch ??= new byte[VoxelConstants.ChunkVoxels],
                    afterLastReset: true,
                    capture.Sections
                );
            }
            else
            {
                EditLogSerializer.WriteEmpty(
                    bytes,
                    _identity.Id,
                    _identity.Version,
                    match.Seed,
                    match.FixedStepHz,
                    match.Game,
                    capture.Sections
                );
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
                // A save the game passed over for its backup is kept aside; otherwise the save is the new backup.
                File.Copy(path, path + (_fromBackup ? ".damaged" : ".bak"), overwrite: true);
            }

            File.Move(temporary, path, overwrite: true);
            // The save reads again: from now on it is the one kept as the backup.
            _fromBackup = false;
            Failure = null;
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var megabytes = bytes.Length / (1024.0 * 1024.0);
            LogSaved(capture.Why, _name!, written, capture.Sections.Length, megabytes, elapsed, capture.Settled, path);
        }
        catch (Exception ex)
        {
            // Told at the menu too, and no half-written file left. On the background thread an exception has nowhere
            // else to go.
            LogFailed(ex, _name!, path);
            Failure = $"The save {_name} could not be written: {ex.Message}";
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Reads a save whole and checks it: its edits, pieces, and sections, with the world's identity (or the match's,
    /// without a world, where it must hold no edits).
    /// </summary>
    private ReadSave Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var edits = new List<VoxelEdit>();
        var pieces = new List<StoredPiece>();
        var sections = new List<SavedSection>();
        var (id, version) = _voxels?.SaveIdentity ?? (_identity.Id, _identity.Version);
        var header = EditLogSerializer.Read(
            new MemoryStream(bytes, writable: false),
            id,
            version,
            edits,
            pieces,
            sections
        );
        if (_voxels is null && edits.Count != 0)
        {
            throw new InvalidDataException($"It holds {edits.Count} voxel edit(s), and this game has no world.");
        }

        return new ReadSave(header, edits, pieces, sections);
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException;

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

    /// <summary>A save read at the start: its header, edits, pieces, and sections.</summary>
    private sealed record ReadSave(
        EditLogHeader Header,
        List<VoxelEdit> Edits,
        List<StoredPiece> Pieces,
        List<SavedSection> Sections
    );

    /// <summary>
    /// What one save writes, captured on the main thread: why (an autosave, the console's save, or the last at quit),
    /// the frozen edit log (null without a world), the match's settings, the sections' bytes, and the balls put back.
    /// </summary>
    private sealed record Capture(
        string Why,
        FrozenEdits? Edits,
        MatchSettings Match,
        SavedSection[] Sections,
        int Settled
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: {Name} does not exist yet; a new world (seed {Seed}) is written to {Path}"
    )]
    private partial void LogNew(string name, ulong seed, string path);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: starting from {Name} (seed {Seed}, {Edits} edit(s), {Sections} section(s)) at {Path}; its seed and match settings replace the options'"
    )]
    private partial void LogStarting(string name, ulong seed, long edits, int sections, string path);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Save: {Name} cannot be read ({Problem}); starting from its backup {Backup}"
    )]
    private partial void LogUsingBackup(string name, string problem, string backup);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: the save's seed {Seed} is used, not Engine:Voxels:Seed {Configured}"
    )]
    private partial void LogSeedReplaced(ulong seed, ulong configured);

    [LoggerMessage(Level = LogLevel.Information, Message = "Save: loaded {Edits} edit(s) of {Name} in {Ms:F1} ms")]
    private partial void LogLoaded(long edits, string name, double ms);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: restored {Sections} section(s) of {Name} in {Ms:F1} ms"
    )]
    private partial void LogRestored(int sections, string name, double ms);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Save: {Name} holds the section {Section} ({Bytes} bytes), which this game does not have; it is left out, and not written again"
    )]
    private partial void LogUnknownSection(string name, string section, int bytes);

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
        Level = LogLevel.Debug,
        Message = "Save: captured {Name} for {Why} ({Edits} edit(s), {Sections} section(s)) in {Ms:F2} ms"
    )]
    private partial void LogCaptured(string why, string name, long edits, int sections, double ms);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Save: waited {Ms:F1} ms for the write of {Name} to finish")]
    private partial void LogWaited(string name, double ms);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Save: wrote {Name} ({Why}: {Edits} edit(s), {Sections} section(s), {Megabytes:F2} MB) in {Ms:F1} ms, after putting {Settled} loose ball(s) back into the grid; {Path}"
    )]
    private partial void LogSaved(
        string why,
        string name,
        long edits,
        int sections,
        double megabytes,
        double ms,
        int settled,
        string path
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Save: could not take {Name} for {Why}; the last save stays, and the match goes on"
    )]
    private partial void LogCaptureFailed(Exception exception, string name, string why);

    [LoggerMessage(Level = LogLevel.Error, Message = "Save: could not write {Name} to {Path}")]
    private partial void LogFailed(Exception exception, string name, string path);
}
