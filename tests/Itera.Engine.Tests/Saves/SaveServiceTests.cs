using System.Numerics;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Saves;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Saves;

/// <summary>
/// Named saves (<see cref="SaveService"/>, roadmap item 5, part E) with <see cref="SessionService"/> over
/// <see cref="FakeTransport"/>, in a temporary folder: a new save written at quit and started from again (its seed, not
/// the options'), a host started from a save sending the saved world and the save's match data to a client, refusals
/// that leave the file alone, no save on a client or after an exception, and the previous save kept as a backup.
/// </summary>
public sealed class SaveServiceTests : IDisposable
{
    private const int Floor = 200;
    private const ulong SavedSeed = 17;

    private static readonly BoxesGenerator s_generator = new(
        Floor,
        new VoxelBox(-40, 200, -40, 40, 260, 40, BoxesGenerator.Pattern)
    );

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "itera-save-tests-" + Guid.NewGuid().ToString("N")
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
    public void A_new_save_is_written_at_quit_and_the_next_game_starts_from_it_with_its_seed()
    {
        var first = Start(SessionMode.Offline, SavedSeed);
        Assert.False(first.Saves.StartedFromSave);
        Assert.False(File.Exists(first.Saves.FilePath));
        MakeEdits(first.World);
        first.Saves.Save(true, first.Session.Role, first.Session.Match);
        Assert.True(File.Exists(first.Saves.FilePath));

        // Another game whose options name seed 5 starts from the save.
        var second = Start(SessionMode.Offline, configuredSeed: 5);

        Assert.True(second.Saves.StartedFromSave);
        Assert.True(second.Session.MatchFromSave);
        Assert.Equal(SavedSeed, second.Session.Match.Seed);
        Assert.Equal(SavedSeed, second.World.Seed);
        Assert.Equal(3, second.World.EditCount);
        Assert.Equal(Snapshot(first.World), Snapshot(second.World));
    }

    [Fact]
    public void A_host_started_from_a_save_sends_the_saved_world_and_the_saves_match_data()
    {
        var first = Start(SessionMode.Offline, SavedSeed, game: [1, 2, 3]);
        MakeEdits(first.World);
        first.Saves.Save(true, first.Session.Role, first.Session.Match);

        // The host's own match data would be [9, 9]; the save's wins.
        var host = Start(SessionMode.Host, configuredSeed: 5, game: [9, 9]);
        var client = JoinWithoutSave(host.Session);

        Assert.Equal([1, 2, 3], host.Session.Match.Game);
        Assert.Equal([1, 2, 3], client.Session.Match.Game);
        Assert.Equal(SavedSeed, client.World.Seed);
        Assert.Equal(Snapshot(first.World), Snapshot(client.World));
    }

    [Fact]
    public void A_save_of_another_step_rate_is_refused_and_left_as_it_was()
    {
        var first = Start(SessionMode.Offline, SavedSeed);
        first.Saves.Save(true, first.Session.Role, first.Session.Match);
        var bytes = File.ReadAllBytes(first.Saves.FilePath!);

        var failure = Assert.Throws<SessionStartException>(() => Start(SessionMode.Offline, 5, fixedStepHz: 30));

        Assert.Contains("60 steps per second; this game at 30", failure.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(first.Saves.FilePath!));
    }

    [Fact]
    public void A_save_of_another_generator_is_refused_and_left_as_it_was()
    {
        var first = Start(SessionMode.Offline, SavedSeed);
        first.Saves.Save(true, first.Session.Role, first.Session.Match);
        var bytes = File.ReadAllBytes(first.Saves.FilePath!);

        var failure = Assert.Throws<SessionStartException>(() =>
            Start(SessionMode.Host, 5, generator: new DomeGenerator())
        );

        Assert.Contains("generator", failure.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(first.Saves.FilePath!));
    }

    /// <summary>A save whose header reads but whose edits are cut short is refused and kept.</summary>
    [Fact]
    public void A_save_with_a_good_header_and_a_damaged_body_is_refused_and_left_as_it_was()
    {
        var first = Start(SessionMode.Offline, SavedSeed);
        MakeEdits(first.World);
        first.Saves.Save(true, first.Session.Role, first.Session.Match);
        var path = first.Saves.FilePath!;
        var damaged = File.ReadAllBytes(path)[..^10];
        File.WriteAllBytes(path, damaged);

        var failure = Assert.Throws<SessionStartException>(() => Start(SessionMode.Offline, 5));

        Assert.Contains("cannot be loaded", failure.Message, StringComparison.Ordinal);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_file_that_is_not_a_save_is_refused_and_left_as_it_was()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "test.save");
        File.WriteAllText(path, "not a save at all");

        var failure = Assert.Throws<SessionStartException>(() => Start(SessionMode.Offline, 5));

        Assert.Contains("cannot be used", failure.Message, StringComparison.Ordinal);
        Assert.Equal("not a save at all", File.ReadAllText(path));
    }

    [Fact]
    public void A_client_and_a_game_that_stopped_on_an_error_write_nothing()
    {
        var game = Start(SessionMode.Offline, SavedSeed);
        MakeEdits(game.World);

        game.Saves.Save(true, SessionRole.Client, game.Session.Match);
        game.Saves.Save(false, game.Session.Role, game.Session.Match);

        Assert.False(File.Exists(game.Saves.FilePath));
    }

    [Fact]
    public void The_previous_save_is_kept_as_a_backup()
    {
        var game = Start(SessionMode.Offline, SavedSeed);
        game.Saves.Save(true, game.Session.Role, game.Session.Match);
        var before = File.ReadAllBytes(game.Saves.FilePath!);
        MakeEdits(game.World);

        game.Saves.Save(true, game.Session.Role, game.Session.Match);

        Assert.Equal(before, File.ReadAllBytes(game.Saves.FilePath! + ".bak"));
        Assert.NotEqual(before, File.ReadAllBytes(game.Saves.FilePath!));
        Assert.False(File.Exists(game.Saves.FilePath! + ".tmp"));
    }

    /// <summary>
    /// A save holds only the edits after the last reset, renumbered from 1, and the next game starts from the world the
    /// reset left with them (a lost game's restart kept every earlier game's edits).
    /// </summary>
    [Fact]
    public void A_save_holds_only_the_edits_after_the_last_reset()
    {
        var first = Start(SessionMode.Offline, SavedSeed);
        MakeEdits(first.World);
        first.World.Reset();
        // The reset unloads every column; the lift needs them loaded again.
        TestWorlds.WaitUntilLoaded(first.World);
        MakeEdits(first.World);
        first.Saves.Save(true, first.Session.Role, first.Session.Match);

        var second = Start(SessionMode.Offline, configuredSeed: 5);

        Assert.Equal(7, first.World.EditCount);
        Assert.Equal(3, second.World.EditCount);
        Assert.Equal(0, second.World.LastReset);
        Assert.Equal(Snapshot(first.World), Snapshot(second.World));
    }

    /// <summary>
    /// A save that cannot be written leaves the old one as it was and says why for the menu.
    /// </summary>
    [Fact]
    public void A_save_that_cannot_be_written_keeps_the_old_one_and_says_why()
    {
        var game = Start(SessionMode.Offline, SavedSeed);
        game.Saves.Save(true, game.Session.Role, game.Session.Match);
        var before = File.ReadAllBytes(game.Saves.FilePath!);
        MakeEdits(game.World);
        // A folder in the way of the file written first.
        Directory.CreateDirectory(game.Saves.FilePath + ".tmp");

        game.Saves.Save(true, game.Session.Role, game.Session.Match);

        Assert.Equal(before, File.ReadAllBytes(game.Saves.FilePath!));
        Assert.NotNull(game.Saves.Failure);
        Assert.Contains("test", game.Saves.Failure, StringComparison.Ordinal);
    }

    /// <summary>A save that fails after its temporary file was written leaves no temporary file.</summary>
    [Fact]
    public void A_save_that_fails_after_writing_leaves_no_temporary_file()
    {
        var game = Start(SessionMode.Offline, SavedSeed);
        MakeEdits(game.World);
        // A folder where the save goes: the temporary file is written, and moving it there fails.
        Directory.CreateDirectory(game.Saves.FilePath!);

        game.Saves.Save(true, game.Session.Role, game.Session.Match);

        Assert.NotNull(game.Saves.Failure);
        Assert.False(File.Exists(game.Saves.FilePath + ".tmp"));
    }

    [Fact]
    public void A_game_without_a_world_saves_its_match_and_starts_from_it()
    {
        var first = StartWithoutWorld(SavedSeed, game: [4, 5]);
        first.Saves.Save(true, first.Session.Role, first.Session.Match);

        var second = StartWithoutWorld(configuredSeed: 5, game: [9]);

        Assert.True(second.Saves.StartedFromSave);
        Assert.Equal(SavedSeed, second.Session.Match.Seed);
        Assert.Equal([4, 5], second.Session.Match.Game);
        Assert.Equal(("Mesh Game", 1), (second.Session.Match.Identity, second.Session.Match.IdentityVersion));
    }

    [Fact]
    public void A_worlds_save_is_refused_by_a_game_without_a_world()
    {
        var world = Start(SessionMode.Offline, SavedSeed);
        MakeEdits(world.World);
        world.Saves.Save(true, world.Session.Role, world.Session.Match);
        var bytes = File.ReadAllBytes(world.Saves.FilePath!);

        // Another identity: refused by the header. The generator's own: refused for its edits.
        Assert.Throws<SessionStartException>(() => StartWithoutWorld(5));
        var same = new GeneratorMatchIdentity(s_generator);
        Assert.Throws<SessionStartException>(() => StartWithoutWorld(5, identity: same));
        Assert.Equal(bytes, File.ReadAllBytes(world.Saves.FilePath!));
    }

    /// <summary>A game without a world (match identity "Mesh Game" 1 unless given), offline, named save "test".</summary>
    private (SessionService Session, SaveService Saves) StartWithoutWorld(
        ulong configuredSeed,
        byte[]? game = null,
        IMatchIdentity? identity = null
    )
    {
        identity ??= new FixedIdentity("Mesh Game", 1);
        var saves = new SaveService("test", _folder, null, null, NullLogger<SaveService>.Instance, identity);
        var session = new SessionService(
            new SessionSettings { Mode = SessionMode.Offline },
            60,
            configuredSeed,
            0,
            null,
            new FakeTransport(_network),
            NullLogger<SessionService>.Instance,
            () => 0,
            () => { },
            matchData: game is null ? null : new FixedMatchData(game),
            saves: saves,
            identity: identity
        );
        _owned.Add(session);
        session.Initialize();
        session.LoadJoinedWorld();
        return (session, saves);
    }

    private sealed class FixedIdentity(string id, int version) : IMatchIdentity
    {
        public string Id => id;

        public int Version => version;
    }

    /// <summary>A game that owns its world, named save "test", its world loaded around the block.</summary>
    private (VoxelWorld World, SessionService Session, SaveService Saves) Start(
        SessionMode mode,
        ulong configuredSeed,
        int fixedStepHz = 60,
        IChunkGenerator? generator = null,
        byte[]? game = null
    )
    {
        var world = new VoxelWorld(
            WorldSettings(configuredSeed),
            0,
            generator ?? s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        _owned.Add(world);
        var saves = new SaveService("test", _folder, world, null, NullLogger<SaveService>.Instance);
        var session = new SessionService(
            new SessionSettings { Mode = mode },
            fixedStepHz,
            configuredSeed,
            0,
            world,
            new FakeTransport(_network),
            NullLogger<SessionService>.Instance,
            () => 0,
            () => { },
            matchData: game is null ? null : new FixedMatchData(game),
            saves: saves
        );
        _owned.Add(session);
        session.Initialize();
        world.Initialize(session.Match.Seed);
        session.LoadJoinedWorld();
        world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(world);
        return (world, session, saves);
    }

    private (VoxelWorld World, SessionService Session) JoinWithoutSave(SessionService host)
    {
        var world = new VoxelWorld(WorldSettings(99), 0, s_generator, NullLogger<VoxelWorld>.Instance);
        _owned.Add(world);
        var clock = 0.0;
        var session = new SessionService(
            new SessionSettings { Mode = SessionMode.Join, Address = "host:47600" },
            60,
            99,
            0,
            world,
            new FakeTransport(_network),
            NullLogger<SessionService>.Instance,
            () => clock,
            () =>
            {
                clock += 0.01;
                host.Poll();
            }
        );
        _owned.Add(session);
        session.Initialize();
        world.Initialize(session.Match.Seed);
        session.LoadJoinedWorld();
        world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(world);
        return (world, session);
    }

    /// <summary>A dig, a lift, and the piece put back turned elsewhere.</summary>
    private static void MakeEdits(VoxelWorld world)
    {
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(20, 240, -10), 15, VoxelEditOperation.Clear));
        Assert.True(
            world.TryLift(VoxelEdit.Sphere(new VoxelCoord(-10, 235, 10), 18, VoxelEditOperation.Lift), out var piece)
        );
        Assert.True(world.TryGetPiece(piece, out var voxels));
        world.Apply(
            VoxelEdit.PieceAt(
                piece,
                voxels.Radius,
                new VoxelCoord(60, 262, 50),
                VoxelEdit.PackTurn(new Quaternion(0.2f, 0.6f, 0.1f, 0.77f))
            )
        );
    }

    private static VoxelSettings WorldSettings(ulong seed) =>
        new()
        {
            StreamingRadiusMetres = 3,
            Levels = 1,
            MemoryBudgetMegabytes = 256,
            WorkerThreads = 2,
            Seed = seed,
        };

    /// <summary>Every level-0 voxel of the block and the air above it.</summary>
    private static byte[] Snapshot(VoxelWorld world)
    {
        var voxels = new List<byte>();
        for (var z = -100; z < 100; z += 2)
        {
            for (var y = 190; y < 300; y += 1)
            {
                for (var x = -100; x < 100; x += 1)
                {
                    Assert.True(world.TryGetMaterial(new VoxelCoord(x, y, z), out var material));
                    voxels.Add(material);
                }
            }
        }

        return [.. voxels];
    }

    private sealed class FixedMatchData(byte[] bytes) : ISessionMatchData
    {
        public void Write(MessageWriter writer)
        {
            foreach (var value in bytes)
            {
                writer.WriteByte(value);
            }
        }
    }
}
