using System.Runtime.CompilerServices;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// Several matches in one run, headless (no window or GPU: the match's world-on-the-GPU step does nothing): offline
/// matches of seeds 1, 7, then 1 again, each started by its steps, run for some fixed steps, and ended, with the real
/// service collection. Nothing of an ended match stays: its services are collected, the console holds only the run's
/// commands, its voxel memory is freed, its Arch world is gone, and the third match starts as the first did. Each step is
/// the engine's own (<see cref="MatchServices.RunFixedStep"/>), with the world streaming round a centre. The game is
/// <see cref="EngineComposition"/>'s.
/// </summary>
[Collection(SerialCollectionDefinition.Name)]
public sealed class MatchLifetimeTests
{
    private const int Steps = 30;

    [Fact]
    public void Matches_of_one_run_leave_nothing_of_each_other()
    {
        using var provider = BuildServices();
        foreach (var startup in provider.GetServices<IRunStartup>())
        {
            startup.Start();
        }

        var commands = provider.GetRequiredService<ConsoleCommands>();
        string[] runCommands = [.. commands.All.Select(c => c.Name)];
        var worlds = World.WorldSize;

        var first = PlayOnce(provider, seed: 1);
        AssertGone(first);
        Assert.Equal(runCommands, commands.All.Select(c => c.Name));
        Assert.Equal(worlds, World.WorldSize);

        var second = PlayOnce(provider, seed: 7);
        AssertGone(second);
        Assert.Equal(7UL, second.Seed);
        // The second match registered the same commands again, without a clash.
        Assert.Equal(first.Commands, second.Commands);

        var third = PlayOnce(provider, seed: 1);
        AssertGone(third);
        Assert.Equal(runCommands, commands.All.Select(c => c.Name));
        Assert.Equal(worlds, World.WorldSize);
        // Started afresh: nothing of the first two carried over.
        Assert.Equal(first.Entities, third.Entities);
        Assert.Equal(first.Edits, third.Edits);
        Assert.Equal(first.Commands, third.Commands);
    }

    /// <summary>
    /// What a match left: weak references to its services, its world's voxel memory (the process's counter would count
    /// other tests' worlds too), and what it counted while it played.
    /// </summary>
    private sealed record Played(
        long MemoryLeft,
        WeakReference Services,
        WeakReference Ecs,
        WeakReference Voxels,
        WeakReference System,
        ulong Seed,
        int Entities,
        long Edits,
        string[] Commands
    );

    /// <summary>Starts an offline match of <paramref name="seed"/>, runs its fixed steps, and ends it, as GameLoop does.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Played PlayOnce(ServiceProvider provider, ulong seed)
    {
        var logger = NullLogger.Instance;
        var scope = provider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<MatchContext>()
            .Set(new MatchStart(SessionMode.Offline, SessionTransportKind.Direct, null, seed, null, false));
        var match = scope.ServiceProvider.GetRequiredService<MatchServices>();
        var sessionSteps = new EngineSteps(match.CreateSessionSteps(), logger);
        sessionSteps.Initialize(_ => { });
        var worldSteps = new EngineSteps(match.CreateWorldSteps(new MatchGpu(_ => { }, () => { }), () => 0), logger);
        worldSteps.Initialize(_ => { });
        var memory = match.Voxels.ReadMemory;

        // The engine's own step (MatchServices.RunFixedStep), with the world streaming round a centre between steps, as
        // the main loop runs it.
        match.Voxels.SetCenters([System.Numerics.Vector3.Zero]);
        for (var tick = 0UL; tick < Steps; tick++)
        {
            match.Voxels.Update(tick / 60.0);
            match.RunFixedStep(new FixedStepTime(1f / 60, tick), 4);
        }

        var system = scope.ServiceProvider.GetServices<IFixedUpdateSystem>().First();
        var played = new Played(
            0,
            new WeakReference(match),
            new WeakReference(match.Ecs),
            new WeakReference(match.Voxels),
            new WeakReference(system),
            match.Session.Match.Seed,
            match.Ecs.World.Size,
            match.Voxels.EditCount,
            [.. provider.GetRequiredService<ConsoleCommands>().All.Select(c => c.Name)]
        );

        match.EndedCleanly = true;
        match.Session.Stop();
        worldSteps.Shutdown();
        sessionSteps.Shutdown();
        scope.Dispose();
        return played with { MemoryLeft = memory.ReservedBytes };
    }

    private static void AssertGone(Played played)
    {
        for (var i = 0; i < 3 && (played.Services.IsAlive || played.Voxels.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(played.Services.IsAlive, "The match's services outlived it.");
        Assert.False(played.Ecs.IsAlive, "The match's ECS world outlived it.");
        Assert.False(played.Voxels.IsAlive, "The match's voxel world outlived it.");
        Assert.False(played.System.IsAlive, "A system of the match outlived it.");
        Assert.Equal(0, played.MemoryLeft);
    }

    private static ServiceProvider BuildServices() => EngineComposition.Build();
}
