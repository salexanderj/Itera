using Itera.Engine.Diagnostics;
using Itera.Engine.Modding;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Modding;

public sealed class ModDispatcherTests
{
    [Fact]
    public void A_throwing_mod_is_isolated_and_stops_receiving_updates()
    {
        var dispatcher = new ModDispatcher(NullLogger.Instance);
        var bad = new RecordingMod { ThrowOnUpdate = true };
        var good = new RecordingMod();
        var badEntry = Wrap(bad, "bad");
        var goodEntry = Wrap(good, "good");
        dispatcher.Add(badEntry);
        dispatcher.Add(goodEntry);

        var frame = new FrameTime(0.016f, 0.016, 0, 0f);
        dispatcher.Update(in frame);
        dispatcher.Update(in frame);
        var step = new FixedStepTime(1f / 60f, 0);
        dispatcher.FixedUpdate(in step);

        Assert.True(badEntry.Faulted);
        Assert.False(goodEntry.Faulted);
        Assert.Equal(1, bad.Updates);
        Assert.Equal(0, bad.FixedUpdates);
        Assert.Equal(2, good.Updates);
        Assert.Equal(1, good.FixedUpdates);
    }

    [Fact]
    public void Faulted_mods_still_receive_exit_and_unload()
    {
        var dispatcher = new ModDispatcher(NullLogger.Instance);
        var bad = new RecordingMod { ThrowOnUpdate = true };
        var entry = Wrap(bad, "bad");
        dispatcher.Add(entry);
        var frame = new FrameTime(0.016f, 0.016, 0, 0f);
        dispatcher.Update(in frame);

        dispatcher.NotifyGameExit();
        dispatcher.NotifyUnloaded(entry);

        Assert.True(entry.Faulted);
        Assert.Equal(1, bad.GameExits);
        Assert.Equal(1, bad.Unloads);
    }

    [Fact]
    public void Mods_hear_of_each_match_and_one_that_throws_at_its_start_is_isolated()
    {
        var dispatcher = new ModDispatcher(NullLogger.Instance);
        var bad = new RecordingMod { ThrowOnMatchStart = true };
        var good = new RecordingMod();
        var badEntry = Wrap(bad, "bad");
        dispatcher.Add(badEntry);
        dispatcher.Add(Wrap(good, "good"));
        var first = new EmptyServices();
        var second = new EmptyServices();

        dispatcher.MatchStarted(first);
        dispatcher.MatchEnded();
        dispatcher.MatchStarted(second);
        dispatcher.MatchEnded();

        Assert.True(badEntry.Faulted);
        Assert.Equal([first, second], good.Matches);
        Assert.Equal(2, good.MatchEnds);
        // A faulted mod still hears the ends, to drop what it holds; not later starts.
        Assert.Single(bad.Matches);
        Assert.Equal(2, bad.MatchEnds);
    }

    [Fact]
    public void Load_reports_failure_when_OnModLoaded_throws()
    {
        var dispatcher = new ModDispatcher(NullLogger.Instance);
        var entry = Wrap(new RecordingMod { ThrowOnLoad = true }, "bad");

        Assert.False(dispatcher.Load(entry));
        Assert.True(entry.Faulted);
    }

    [Fact]
    public void Game_exit_runs_in_reverse_load_order()
    {
        var dispatcher = new ModDispatcher(NullLogger.Instance);
        var order = new List<string>();
        dispatcher.Add(Wrap(new RecordingMod { OnExit = () => order.Add("first") }, "first"));
        dispatcher.Add(Wrap(new RecordingMod { OnExit = () => order.Add("second") }, "second"));

        dispatcher.NotifyGameExit();

        Assert.Equal(["second", "first"], order);
    }

    [Fact]
    public void A_mod_that_unloads_another_mod_during_update_does_not_end_the_loop()
    {
        var dispatcher = new ModDispatcher(NullLogger.Instance);
        LoadedMod? victim = null;
        var remover = new RecordingMod { DuringUpdate = () => dispatcher.Remove(victim!) };
        var removed = new RecordingMod();
        var last = new RecordingMod();
        var removerEntry = Wrap(remover, "remover");
        victim = Wrap(removed, "victim");
        dispatcher.Add(removerEntry);
        dispatcher.Add(victim);
        dispatcher.Add(Wrap(last, "last"));

        var frame = new FrameTime(0.016f, 0.016, 0, 0f);
        dispatcher.Update(in frame);

        Assert.False(removerEntry.Faulted);
        Assert.Equal(0, removed.Updates);
        Assert.Equal(1, last.Updates);
        Assert.Equal(2, dispatcher.Mods.Count);
    }

    /// <summary>
    /// Mods' callbacks are timed: a mod's fixed update joins the fixed systems' times by its name, the call
    /// in progress is named for a signal that arrives while a mod hangs, and a slow call logs a Warning once it is the
    /// mod's longest.
    /// </summary>
    [Fact]
    public void Callbacks_are_timed_named_while_they_run_and_a_slow_one_is_logged()
    {
        var times = new SystemTimes();
        var logger = new ListLogger();
        var dispatcher = new ModDispatcher(logger, times);
        string? during = null;
        var slow = new RecordingMod
        {
            DuringUpdate = () =>
            {
                during = dispatcher.CurrentCall;
                Thread.Sleep((int)ModDispatcher.SlowCallMilliseconds + 20);
            },
        };
        dispatcher.Add(Wrap(slow, "slowmod"));

        var step = new FixedStepTime(1f / 60f, 0);
        dispatcher.FixedUpdate(in step);
        var frame = new FrameTime(0.016f, 0.016, 0, 0f);
        dispatcher.Update(in frame);

        Assert.StartsWith("Mod slowmod", times.TakeSlowest(1, 1), StringComparison.Ordinal);
        Assert.Equal("slowmod OnUpdate", during);
        Assert.Null(dispatcher.CurrentCall);
        var warning = Assert.Single(logger.Lines, line => line.Level == LogLevel.Warning);
        Assert.Contains("slowmod's OnUpdate took", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Timed_updates_allocate_nothing()
    {
        var dispatcher = new ModDispatcher(NullLogger.Instance, new SystemTimes());
        dispatcher.Add(Wrap(new RecordingMod(), "first"));
        dispatcher.Add(Wrap(new RecordingMod(), "second"));
        var frame = new FrameTime(0.016f, 0.016, 0, 0f);
        var step = new FixedStepTime(1f / 60f, 0);
        dispatcher.Update(in frame);
        dispatcher.FixedUpdate(in step);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            dispatcher.Update(in frame);
            dispatcher.FixedUpdate(in step);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// A mod's fixed-step time joins each match's systems afresh: they were cleared as the match's world started, and the
    /// index the mod took at start-up named a system.
    /// </summary>
    [Fact]
    public void A_mods_step_time_is_named_after_the_systems_are_cleared_and_added_again()
    {
        var times = new SystemTimes();
        var dispatcher = new ModDispatcher(NullLogger.Instance, times);
        var mod = new RecordingMod { DuringFixedUpdate = () => Thread.Sleep(2) };
        dispatcher.Add(Wrap(mod, "slow"));

        // A match's world: the times cleared, its systems added, then the mods told.
        times.Clear();
        times.Add("SystemA");
        times.Add("SystemB");
        dispatcher.MatchStarted(EmptyServiceProvider.Instance);
        dispatcher.FixedUpdate(new FixedStepTime(1f / 60f, 0));

        var slowest = times.TakeSlowest(1, 1);
        Assert.StartsWith("Mod slow", slowest, StringComparison.Ordinal);

        // An index from before a clear, past the systems added since, is ignored.
        times.Clear();
        times.Add("SystemC");
        times.Record(5, 100);

        Assert.Equal("SystemC 0.000 max 0.00", times.TakeSlowest(1, 1));
    }

    private static LoadedMod Wrap(IGameMod mod, string id)
    {
        var manifest = new ModManifest
        {
            Id = id,
            Name = id,
            Version = "1",
            EntryAssembly = id + ".dll",
        };
        var context = new ModContext(manifest, Path.GetTempPath(), NullLogger.Instance, EmptyServiceProvider.Instance);
        return new LoadedMod(manifest, Path.GetTempPath(), loadContext: null, mod, context);
    }

    private sealed class RecordingMod : IGameMod
    {
        public bool ThrowOnLoad { get; init; }

        public bool ThrowOnUpdate { get; init; }

        public Action? OnExit { get; init; }

        public Action? DuringUpdate { get; init; }

        public Action? DuringFixedUpdate { get; init; }

        public int Updates { get; private set; }

        public int FixedUpdates { get; private set; }

        public int GameExits { get; private set; }

        public bool ThrowOnMatchStart { get; init; }

        public List<IServiceProvider> Matches { get; } = [];

        public int MatchEnds { get; private set; }

        public void OnMatchStarted(IServiceProvider match)
        {
            Matches.Add(match);
            if (ThrowOnMatchStart)
            {
                throw new InvalidOperationException("match start failure");
            }
        }

        public void OnMatchEnded()
        {
            MatchEnds++;
        }

        public int Unloads { get; private set; }

        public void OnModLoaded(IModContext context)
        {
            if (ThrowOnLoad)
            {
                throw new InvalidOperationException("load failure");
            }
        }

        public void OnUpdate(in FrameTime time)
        {
            Updates++;
            DuringUpdate?.Invoke();
            if (ThrowOnUpdate)
            {
                throw new InvalidOperationException("update failure");
            }
        }

        public void OnFixedUpdate(in FixedStepTime time)
        {
            DuringFixedUpdate?.Invoke();
            FixedUpdates++;
        }

        public void OnGameExit()
        {
            GameExits++;
            OnExit?.Invoke();
        }

        public void OnModUnloaded()
        {
            Unloads++;
        }
    }

    /// <summary>A match's services, as a mod sees them; these resolve nothing.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
