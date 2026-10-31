using System.Numerics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// A reset of the world (<see cref="VoxelWorld.Reset"/>, the console's <c>reset</c>): every column unloads and loads
/// again as the generator makes it, edits after the reset stay (also through a later unload and reload), large edits
/// waiting to be applied are dropped, a log with a reset in it (a save, a host's join payload, a host's live edits)
/// gives the same voxels, and a world that follows a host cannot reset by itself.
/// </summary>
public sealed class VoxelResetTests : IDisposable
{
    private const ulong Seed = 7;
    private static readonly VoxelCoord s_ground = new(0, SlopeGenerator.Height(0, 0, Seed) - 10, 0);
    private static readonly VoxelCoord s_later = new(120, SlopeGenerator.Height(120, 60, Seed) - 10, 60);
    private readonly List<VoxelWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    [Fact]
    public void A_reset_brings_back_the_generated_voxels_and_keeps_the_edits_after_it()
    {
        var pristine = Loaded();
        var world = Loaded();
        world.Apply(VoxelEdit.Sphere(s_ground, 30, VoxelEditOperation.Clear));
        world.Apply(VoxelEdit.Box(s_ground, new VoxelCoord(40, 40, 40), VoxelEditOperation.Set, SlopeGenerator.Stone));

        var sequence = world.Reset();
        var residentAfterReset = world.ResidentColumns;
        var wantedAfterReset = world.WantedColumnsResident;
        Settle(world);
        AssertSame(pristine, world);
        world.Apply(VoxelEdit.Sphere(s_later, 20, VoxelEditOperation.Clear));
        // Everything unloads, then loads again: only the edit after the reset is replayed.
        world.SetCenters([new Vector3(5000, 0, 5000)]);
        Settle(world);
        world.SetCenters([Vector3.Zero]);
        Settle(world);

        Assert.Equal(3, sequence);
        Assert.Equal(3, world.LastReset);
        Assert.Equal(4, world.EditCount);
        Assert.Equal(0, residentAfterReset);
        Assert.False(wantedAfterReset);
        world.TryGetMaterial(s_later, out var dug);
        Assert.Equal(VoxelConstants.Air, dug);
        pristine.Apply(VoxelEdit.Sphere(s_later, 20, VoxelEditOperation.Clear));
        AssertSame(pristine, world);
    }

    [Fact]
    public void A_reset_drops_the_large_edits_still_waiting()
    {
        var pristine = Loaded();
        var world = Loaded();
        world.Apply(VoxelEdit.Sphere(s_ground, 150, VoxelEditOperation.Clear));
        Assert.True(world.PendingEditColumns > 0);

        world.Reset();

        Assert.Equal(0, world.PendingEditColumns);
        Assert.Equal(world.EditCount, world.AppliedEditCount);
        Settle(world);
        world.ApplyPendingEdits(int.MaxValue);
        AssertSame(pristine, world);
    }

    [Fact]
    public void A_log_with_a_reset_in_it_gives_the_same_voxels_saved_or_followed()
    {
        var world = Loaded();
        world.Apply(VoxelEdit.Sphere(s_ground, 30, VoxelEditOperation.Clear));
        Assert.True(world.TryLift(VoxelEdit.Sphere(s_later, 12, VoxelEditOperation.Lift), out _));
        world.Reset();
        world.Apply(VoxelEdit.Sphere(s_later, 20, VoxelEditOperation.Clear));
        Settle(world);
        using var saved = new MemoryStream();
        world.SaveEdits(saved, 60, []);

        // A save, or a host's join payload.
        var loaded = Created();
        saved.Position = 0;
        loaded.LoadEdits(saved);
        loaded.SetCenters([Vector3.Zero]);
        Settle(loaded);
        // A client that joined before any edit and follows the host's edits as they come.
        var follower = Loaded();
        follower.FollowHost();
        var runs = new List<(byte Material, int Length)>();
        for (var sequence = 1; sequence <= world.EditCount; sequence++)
        {
            var edit = world.GetEdit(sequence);
            if (edit.Operation == VoxelEditOperation.Lift)
            {
                Assert.True(world.CollectPieceRuns(sequence, runs));
                follower.ApplyHostLift(in edit, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(runs));
            }
            else
            {
                follower.ApplySequenced(in edit);
            }
        }

        Settle(follower);

        Assert.Equal(3, loaded.LastReset);
        Assert.Equal(3, follower.LastReset);
        AssertSame(world, loaded);
        AssertSame(world, follower);
    }

    /// <summary>
    /// A save after a reset holds only the later edits, renumbered, and gives the same voxels; but a put-back after the
    /// reset of a piece lifted before it keeps the whole log, so the piece is there to put back.
    /// </summary>
    [Fact]
    public void A_save_after_a_reset_holds_the_later_edits_unless_one_puts_back_an_earlier_piece()
    {
        var world = Loaded();
        world.Apply(VoxelEdit.Sphere(s_ground, 30, VoxelEditOperation.Clear));
        Assert.True(world.TryLift(VoxelEdit.Sphere(s_later, 12, VoxelEditOperation.Lift), out var early));
        world.Reset();
        Settle(world);
        world.Apply(VoxelEdit.Sphere(s_later, 20, VoxelEditOperation.Clear));
        Assert.True(world.TryLift(VoxelEdit.Sphere(s_ground, 10, VoxelEditOperation.Lift), out _));
        Settle(world);

        using var later = new MemoryStream();
        Assert.Equal(2, world.SaveEdits(later, 60, [], afterLastReset: true));
        var loaded = Created();
        later.Position = 0;
        Assert.Equal(2, loaded.LoadEdits(later));
        loaded.SetCenters([Vector3.Zero]);
        Settle(loaded);
        Assert.Equal(0, loaded.LastReset);
        AssertSame(world, loaded);

        Assert.True(world.TryGetPiece(early, out var piece));
        world.Apply(VoxelEdit.PieceAt(early, piece.Radius, s_later, VoxelEdit.PackTurn(Quaternion.Identity)));
        using var whole = new MemoryStream();
        Assert.Equal(world.EditCount, world.SaveEdits(whole, 60, [], afterLastReset: true));
    }

    [Fact]
    public void A_world_that_follows_a_host_cannot_reset_by_itself()
    {
        var world = Loaded();
        world.FollowHost();

        Assert.Throws<InvalidOperationException>(() => world.Reset());
    }

    /// <summary>Compares the two worlds' level-0 and level-1 voxels over the loaded area around the dig and the later edit.</summary>
    private static void AssertSame(VoxelWorld expected, VoxelWorld actual)
    {
        for (var level = 0; level < 2; level++)
        {
            for (var x = -160; x <= 200; x += 7)
            {
                for (var z = -160; z <= 200; z += 11)
                {
                    var top = SlopeGenerator.Height(x, z, Seed);
                    for (var y = top - 60; y <= top + 20; y += 3)
                    {
                        var voxel = new VoxelCoord(x, y, z);
                        Assert.True(expected.TryGetMaterial(voxel, level, out var a));
                        Assert.True(actual.TryGetMaterial(voxel, level, out var b));
                        Assert.True(a == b, $"Level {level} voxel {voxel}: expected {a}, found {b}.");
                    }
                }
            }
        }
    }

    private VoxelWorld Created()
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = 3,
            Levels = 2,
            MemoryBudgetMegabytes = 64,
            WorkerThreads = 2,
            Seed = Seed,
        };
        var world = new VoxelWorld(settings, 0, new SlopeGenerator(), NullLogger<VoxelWorld>.Instance);
        world.Initialize();
        _worlds.Add(world);
        return world;
    }

    private VoxelWorld Loaded()
    {
        var world = Created();
        world.SetCenters([Vector3.Zero]);
        Settle(world);
        return world;
    }

    private static void Settle(VoxelWorld world)
    {
        var deadline = TestTime.Deadline(60);
        var quiet = 0;
        while (quiet < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "The voxel world did not settle.");
            world.Update(0);
            quiet = world.PendingColumns == 0 && world.PendingFrees == 0 && world.WantedColumnsResident ? quiet + 1 : 0;
            Thread.Sleep(1);
        }
    }
}
