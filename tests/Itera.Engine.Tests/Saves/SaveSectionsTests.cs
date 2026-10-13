using System.Numerics;
using Itera.Engine.Networking;
using Itera.Engine.Saves;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Saves;

/// <summary>
/// A game's own state in its save (<see cref="ISaveSection"/>, save format 8), the backup used when a save will not read,
/// and saves while the match plays (<see cref="SaveService.AfterFixedStep"/>, <see cref="SaveService.SaveNow"/>), over a
/// <see cref="SessionService"/> on <see cref="FakeTransport"/> in a temporary folder: sections captured at the end and
/// restored by name and version at the next start, a section the game lacks passed over, a newer or damaged one (or one
/// whose reader throws) stopping the start, two of one name refused, the backup taken when the save is damaged (the
/// damaged file kept aside) and kept by the next save, a save of a newer build refused whatever its backup, a deleted
/// save starting a new world, an autosave after its steps on a background thread, a section that throws when captured
/// leaving the last save and the match going on, a capture holding the edits and state of its moment and none made after,
/// the end waiting for a write still running, and no allocation until a save is due.
/// </summary>
public sealed class SaveSectionsTests : IDisposable
{
    private const ulong SavedSeed = 17;

    private static readonly BoxesGenerator s_generator = new(
        200,
        new VoxelBox(-40, 200, -40, 40, 260, 40, BoxesGenerator.Pattern)
    );

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "itera-save-sections-tests-" + Guid.NewGuid().ToString("N")
    );
    private readonly FakeNetwork _network = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Sections_captured_at_the_end_are_restored_by_name_and_version_at_the_next_start()
    {
        var first = Start(SavedSeed);
        var counter = new Section("game.counter", 2) { Value = 41 };
        var empty = new Section("game.empty", 1) { Empty = true };
        Begin(first, counter, empty);
        Dig(first.World, 0);
        counter.Value = 42;
        End(first);

        var second = Start(configuredSeed: 5);
        var counterAgain = new Section("game.counter", 2);
        var emptyAgain = new Section("game.empty", 1);
        Begin(second, emptyAgain, counterAgain);

        Assert.True(second.Saves.StartedFromSave);
        Assert.Equal(1, second.World.EditCount);
        Assert.Equal((42, 2), (counterAgain.Value, counterAgain.RestoredVersion));
        Assert.Equal(1, emptyAgain.RestoredVersion);
    }

    [Fact]
    public void A_section_the_game_does_not_have_is_passed_over_and_the_others_restored()
    {
        var first = Start(SavedSeed);
        Begin(first, new Section("game.gone", 1) { Value = 7 }, new Section("game.kept", 1) { Value = 8 });
        End(first);

        var second = Start(configuredSeed: 5);
        var kept = new Section("game.kept", 1);
        Begin(second, kept);
        End(second);

        Assert.Equal(8, kept.Value);
        // Not written again: a third start finds only the kept one.
        var third = Start(configuredSeed: 5);
        var gone = new Section("game.gone", 1);
        Begin(third, gone, new Section("game.kept", 1));
        Assert.Equal(0, gone.RestoredVersion);
    }

    [Fact]
    public void A_section_of_a_newer_version_or_with_damaged_bytes_stops_the_start_and_the_save_is_kept()
    {
        var first = Start(SavedSeed);
        Begin(first, new Section("game.counter", 3) { Value = 5 });
        End(first);
        var path = first.Saves.FilePath!;
        var bytes = File.ReadAllBytes(path);

        var newer = Assert.Throws<SessionStartException>(() =>
            Begin(Start(configuredSeed: 5), new Section("game.counter", 2))
        );
        var damaged = Assert.Throws<SessionStartException>(() =>
            Begin(Start(configuredSeed: 5), new Section("game.counter", 3) { ReadsTooMuch = true })
        );
        var refused = Assert.Throws<SessionStartException>(() =>
            Begin(Start(configuredSeed: 5), new Section("game.counter", 3) { ThrowsOnRestore = true })
        );

        Assert.Contains("version 3", newer.Message, StringComparison.Ordinal);
        Assert.Contains("up to version 2", newer.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be read", damaged.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be read", refused.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Two_sections_of_one_name_or_with_a_bad_name_are_refused()
    {
        var game = Start(SavedSeed);

        Assert.Throws<InvalidOperationException>(() =>
            Begin(game, new Section("game.twice", 1), new Section("game.twice", 2))
        );
        Assert.Throws<InvalidOperationException>(() => Begin(game, new Section("game twice", 1)));
        Assert.Throws<InvalidOperationException>(() => Begin(game, new Section("game.zero", 0)));
    }

    /// <summary>
    /// A save that will not read is passed over for its backup, with the match the backup held; the next save does not
    /// copy the damaged file over the backup, and is kept as the backup from then on.
    /// </summary>
    [Fact]
    public void A_save_that_will_not_read_starts_from_its_backup_which_the_next_save_keeps()
    {
        var first = Start(SavedSeed);
        var counter = new Section("game.counter", 1) { Value = 1 };
        Begin(first, counter);
        Dig(first.World, 0);
        first.Saves.Save(true, first.Session.Role, first.Session.Match);
        var good = File.ReadAllBytes(first.Saves.FilePath!);
        Dig(first.World, 1);
        counter.Value = 2;
        End(first);
        var path = first.Saves.FilePath!;
        Assert.Equal(good, File.ReadAllBytes(path + ".bak"));
        var damaged = File.ReadAllBytes(path)[..^7];
        File.WriteAllBytes(path, damaged);

        var second = Start(configuredSeed: 5);
        var counterAgain = new Section("game.counter", 1);
        Begin(second, counterAgain);

        Assert.True(second.Saves.StartedFromSave);
        Assert.Equal(1, second.World.EditCount);
        Assert.Equal(1, counterAgain.Value);
        counterAgain.Value = 5;
        End(second);
        Assert.Equal(good, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(damaged, File.ReadAllBytes(path + ".damaged"));
        Assert.Equal(1, Start(configuredSeed: 5).World.EditCount);
        // From now on the new save is the one kept.
        var third = Start(configuredSeed: 5);
        Begin(third);
        End(third);
        Assert.NotEqual(good, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void A_save_and_its_backup_that_will_not_read_stop_the_start_and_are_kept()
    {
        var game = Start(SavedSeed);
        Begin(game);
        game.Saves.Save(true, game.Session.Role, game.Session.Match);
        End(game);
        var path = game.Saves.FilePath!;
        File.WriteAllText(path, "not a save");
        File.WriteAllText(path + ".bak", "nor this");

        var failure = Assert.Throws<SessionStartException>(() => Start(configuredSeed: 5));

        Assert.Contains("Nor can its backup", failure.Message, StringComparison.Ordinal);
        Assert.Equal("not a save", File.ReadAllText(path));
        Assert.Equal("nor this", File.ReadAllText(path + ".bak"));
    }

    /// <summary>
    /// A save that is whole but written by a newer build is not damaged: the start ends, and neither it nor its backup
    /// is touched, so no later write replaces it with the backup's older match.
    /// </summary>
    [Fact]
    public void A_save_of_a_newer_build_is_refused_whatever_its_backup()
    {
        var game = Start(SavedSeed);
        Begin(game);
        game.Saves.Save(true, game.Session.Role, game.Session.Match);
        End(game);
        var path = game.Saves.FilePath!;
        var newer = File.ReadAllBytes(path);
        // The format version follows the 4-byte magic.
        newer[4] = EditLogSerializer.FormatVersion + 1;
        File.WriteAllBytes(path, newer);
        var backup = File.ReadAllBytes(path + ".bak");

        var failure = Assert.Throws<SessionStartException>(() => Start(configuredSeed: 5));

        Assert.Contains("this build reads", failure.Message, StringComparison.Ordinal);
        Assert.Equal(newer, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void A_deleted_save_starts_a_new_world_whatever_its_backup()
    {
        var game = Start(SavedSeed);
        Begin(game);
        Dig(game.World, 0);
        game.Saves.Save(true, game.Session.Role, game.Session.Match);
        End(game);
        File.Delete(game.Saves.FilePath!);

        var fresh = Start(configuredSeed: 5);

        Assert.False(fresh.Saves.StartedFromSave);
        Assert.Equal(5UL, fresh.World.Seed);
        Assert.Equal(0, fresh.World.EditCount);
    }

    [Fact]
    public void A_section_that_throws_when_captured_leaves_the_last_save_and_the_match_goes_on()
    {
        var game = Start(SavedSeed, autosaveMinutes: 1 / 3600.0);
        var counter = new Section("game.counter", 1) { Value = 1 };
        Begin(game, counter);
        game.Saves.Save(true, game.Session.Role, game.Session.Match);
        var before = File.ReadAllBytes(game.Saves.FilePath!);
        counter.ThrowsOnCapture = true;

        game.Saves.AfterFixedStep();
        game.Saves.WriteAtEnd();
        Assert.NotNull(game.Saves.Failure);
        End(game);

        Assert.Equal(before, File.ReadAllBytes(game.Saves.FilePath!));
        Assert.Contains("could not be taken", game.Saves.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void An_autosave_is_written_after_its_minutes_of_steps_on_a_background_thread()
    {
        // 10 steps at 60 a second.
        var game = Start(SavedSeed, autosaveMinutes: 10 / 3600.0);
        var counter = new Section("game.counter", 1) { Value = 3 };
        Begin(game, counter);
        Dig(game.World, 0);

        for (var step = 0; step < 9; step++)
        {
            game.Saves.AfterFixedStep();
        }

        Assert.False(game.Saves.Writing);
        Assert.False(File.Exists(game.Saves.FilePath));
        game.Saves.AfterFixedStep();
        counter.Value = 4;
        game.Saves.WriteAtEnd();

        Assert.True(File.Exists(game.Saves.FilePath));
        var again = Start(configuredSeed: 5);
        var counterAgain = new Section("game.counter", 1);
        Begin(again, counterAgain);
        Assert.Equal((1, 3), (again.World.EditCount, counterAgain.Value));
    }

    /// <summary>
    /// A save captured while the match goes on holds the edits and the state of its moment: edits and changes made while
    /// it is written are left out, and the end, which waits for it, writes them.
    /// </summary>
    [Fact]
    public void A_save_holds_what_was_there_when_it_was_captured_and_the_end_waits_for_it()
    {
        var game = Start(SavedSeed);
        var counter = new Section("game.counter", 1) { Value = 10 };
        Begin(game, counter);
        for (var i = 0; i < 4; i++)
        {
            Dig(game.World, i);
        }

        Assert.Null(game.Saves.SaveNow());
        for (var i = 4; i < 40; i++)
        {
            Dig(game.World, i);
        }

        counter.Value = 11;
        game.Saves.WriteAtEnd();
        var captured = Start(configuredSeed: 5);
        var counterCaptured = new Section("game.counter", 1);
        Begin(captured, counterCaptured);
        Assert.Equal((4, 10), (captured.World.EditCount, counterCaptured.Value));

        Assert.Null(game.Saves.SaveNow());
        game.Saves.EndSections(loopEnded: true);
        game.Saves.WriteAtEnd();

        Assert.False(game.Saves.Writing);
        var ended = Start(configuredSeed: 5);
        var counterEnded = new Section("game.counter", 1);
        Begin(ended, counterEnded);
        Assert.Equal((40, 11), (ended.World.EditCount, counterEnded.Value));
    }

    [Fact]
    public void A_match_that_ends_on_an_error_writes_nothing()
    {
        var game = Start(SavedSeed);
        Begin(game, new Section("game.counter", 1));

        game.Saves.EndSections(loopEnded: false);
        game.Saves.WriteAtEnd();

        Assert.False(File.Exists(game.Saves.FilePath));
    }

    [Fact]
    public void A_client_or_a_match_without_a_name_does_not_save_and_says_why()
    {
        var game = Start(SavedSeed);
        game.Saves.BeginSections([], SessionRole.Client, game.Session.Match);
        Assert.Contains("does not save", game.Saves.SaveNow(), StringComparison.Ordinal);

        var unnamed = new SaveService(null, _folder, game.World, null, NullLogger<SaveService>.Instance);
        unnamed.BeginSections([], SessionRole.Offline, game.Session.Match);
        Assert.Contains("no save name", unnamed.SaveNow(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Steps_allocate_nothing_until_a_save_is_due()
    {
        var game = Start(SavedSeed, autosaveMinutes: 60);
        Begin(game, new Section("game.counter", 1));
        game.Saves.AfterFixedStep();

        var allocated = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var step = 0; step < 1000; step++)
            {
                game.Saves.AfterFixedStep();
            }
        });

        Assert.Equal(0, allocated);
        Assert.False(game.Saves.Writing);
    }

    /// <summary>A test section: a number, written whole, or nothing at all.</summary>
    private sealed class Section(string name, int version) : ISaveSection
    {
        public int Value { get; set; }

        public bool Empty { get; init; }

        public bool ReadsTooMuch { get; init; }

        public bool ThrowsOnRestore { get; init; }

        public bool ThrowsOnCapture { get; set; }

        public int RestoredVersion { get; private set; }

        public string Name => name;

        public int Version => version;

        public void Capture(BinaryWriter writer)
        {
            if (ThrowsOnCapture)
            {
                throw new InvalidOperationException("A section that cannot write itself.");
            }

            if (!Empty)
            {
                writer.Write(Value);
            }
        }

        public void Restore(BinaryReader reader, int version)
        {
            RestoredVersion = version;
            if (ThrowsOnRestore)
            {
                throw new ArgumentOutOfRangeException(nameof(reader), "A section whose reader refuses what it reads.");
            }

            if (reader.BaseStream.Length > 0)
            {
                Value = reader.ReadInt32();
            }

            if (ReadsTooMuch)
            {
                reader.ReadInt64();
            }
        }
    }

    private static void Begin(Game game, params ISaveSection[] sections) =>
        game.Saves.BeginSections(sections, game.Session.Role, game.Session.Match);

    /// <summary>The match's end as its steps run it: the last capture, then the write.</summary>
    private static void End(Game game)
    {
        game.Saves.EndSections(loopEnded: true);
        game.Saves.WriteAtEnd();
    }

    /// <summary>A dig in the block, a little to the side for each <paramref name="index"/>.</summary>
    private static void Dig(VoxelWorld world, int index) =>
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(-30 + index, 240, -10), 5, VoxelEditOperation.Clear));

    private sealed record Game(VoxelWorld World, SessionService Session, SaveService Saves);

    /// <summary>An offline game that owns its world, named save "test", its world loaded around the block.</summary>
    private Game Start(ulong configuredSeed, double autosaveMinutes = 0)
    {
        var world = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 128,
                WorkerThreads = 2,
                Seed = configuredSeed,
            },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        _owned.Add(world);
        var saves = new SaveService(
            "test",
            _folder,
            world,
            null,
            NullLogger<SaveService>.Instance,
            autosaveMinutes: autosaveMinutes
        );
        var session = new SessionService(
            new SessionSettings { Mode = SessionMode.Offline },
            60,
            configuredSeed,
            0,
            world,
            new FakeTransport(_network),
            NullLogger<SessionService>.Instance,
            () => 0,
            () => { },
            saves: saves
        );
        _owned.Add(session);
        session.Initialize();
        world.Initialize(session.Match.Seed);
        session.LoadJoinedWorld();
        world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(world);
        return new Game(world, session, saves);
    }
}
