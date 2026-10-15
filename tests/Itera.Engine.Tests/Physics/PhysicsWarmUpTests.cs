using System.Diagnostics;
using Itera.Engine.Loading;
using Itera.Engine.Matches;
using Itera.Engine.Physics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The physics warm-up (<see cref="PhysicsWorld.StartWarmUp"/>): it runs its steps in a world of its own, logs them,
/// and marks the run warmed up, leaving the match's world as it was; its loading task waits for it; a run already
/// warmed up starts none; disposing the world stops it.
/// </summary>
public sealed class PhysicsWarmUpTests
{
    private static readonly TimeSpan MostWait = TimeSpan.FromSeconds(60);

    [Fact]
    public void The_warm_up_runs_its_steps_in_a_world_of_its_own_and_marks_the_run()
    {
        var lines = new List<string>();
        var warmUps = new WarmUps();
        using var physics = new PhysicsWorld(new ListLogger(lines), OneThread(), warmUps: warmUps);
        physics.Initialize();
        var task = new PhysicsWarmUpLoadingTask(physics);

        physics.StartWarmUp();
        WaitUntilDone(physics);

        Assert.True(warmUps.Physics);
        Assert.True(task.Progress.Done);
        Assert.Equal((0, 0), (physics.Simulation.Bodies.ActiveSet.Count, physics.Simulation.Statics.Count));
        lock (lines)
        {
            Assert.Contains(lines, line => line.StartsWith($"Physics: warm-up ran {PhysicsWorld.WarmUpSteps} step(s)"));
            Assert.DoesNotContain(lines, line => line.Contains("failed", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_run_already_warmed_up_starts_none()
    {
        var warmUps = new WarmUps { Physics = true };
        using var physics = new PhysicsWorld(new ListLogger([]), OneThread(), warmUps: warmUps);
        physics.Initialize();

        physics.StartWarmUp();

        Assert.True(physics.WarmUpDone);
        Assert.True(new PhysicsWarmUpLoadingTask(physics).Progress.Done);
    }

    [Fact]
    public void Disposing_the_world_stops_the_warm_up()
    {
        var physics = new PhysicsWorld(new ListLogger([]), OneThread(), warmUps: new WarmUps());
        physics.Initialize();
        physics.StartWarmUp();

        var start = Stopwatch.GetTimestamp();
        physics.Dispose();

        Assert.True(physics.WarmUpDone);
        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2));
    }

    private static IOptions<EngineOptions> OneThread()
    {
        var options = new EngineOptions();
        options.Physics.Threads = 1;
        return Options.Create(options);
    }

    private static void WaitUntilDone(PhysicsWorld physics)
    {
        var start = Stopwatch.GetTimestamp();
        while (!physics.WarmUpDone)
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < MostWait, "The warm-up did not end.");
            Thread.Sleep(5);
        }
    }

    /// <summary>A logger that keeps each line, from any thread.</summary>
    private sealed class ListLogger(List<string> lines) : ILogger<PhysicsWorld>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (lines)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}
