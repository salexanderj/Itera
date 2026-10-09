using Itera.Engine.Loading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Loading;

/// <summary>
/// <see cref="LoadingScreen"/> with tasks the test finishes: it covers the picture until every task is done, then fades
/// out; at the time limit it stops waiting with a Warning naming what had not finished; turned off it is done at once;
/// its version changes only when a line would; and an update allocates nothing.
/// </summary>
public sealed class LoadingScreenTests
{
    private sealed class TestTask(string name) : ILoadingTask
    {
        public string Name => name;

        public LoadingProgress Progress { get; set; } = LoadingProgress.Waiting;

        public int Reads { get; private set; }

        LoadingProgress ILoadingTask.Progress
        {
            get
            {
                Reads++;
                return Progress;
            }
        }
    }

    private sealed class RecordingLogger : ILogger<LoadingScreen>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static LoadingScreen Create(
        RecordingLogger logger,
        TestTask[] tasks,
        bool enabled = true,
        double maxSeconds = 15
    )
    {
        var options = new EngineOptions();
        options.Loading.Enabled = enabled;
        options.Loading.MaxSeconds = maxSeconds;
        var screen = new LoadingScreen(Options.Create(options), logger);
        screen.Initialize(tasks);
        return screen;
    }

    [Fact]
    public void It_covers_the_picture_until_every_task_is_done_then_fades_out()
    {
        var logger = new RecordingLogger();
        var sounds = new TestTask("Sounds");
        var world = new TestTask("World");
        var screen = Create(logger, [sounds, world]);

        Assert.Equal(LoadingPhase.NotBegun, screen.Phase);
        screen.Begin(10);
        Assert.True(screen.Active);
        Assert.Equal(1, screen.Alpha);

        sounds.Progress = LoadingProgress.Finished;
        world.Progress = LoadingProgress.Part(false, 0.5f);
        screen.CountDroppedSteps(3);
        screen.Update(10.5);
        Assert.True(screen.Active);
        Assert.True(screen.TaskProgress(0).Done);
        Assert.Equal(0.5f, screen.TaskProgress(1).Fraction);

        // A finished task is not read again.
        var soundReads = sounds.Reads;
        world.Progress = LoadingProgress.Finished;
        screen.Update(12);
        Assert.Equal(soundReads, sounds.Reads);
        Assert.False(screen.Active);
        Assert.True(screen.Drawn);
        Assert.Equal(LoadingPhase.Fading, screen.Phase);
        Assert.Equal(1, screen.Alpha, 3);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("done after 2.0 s"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("Sounds 0.5 s, World 2.0 s); 3 fixed step(s) dropped"));

        screen.Update(12 + LoadingScreen.FadeSeconds / 2);
        Assert.Equal(0.5f, screen.Alpha, 3);

        screen.Update(12 + LoadingScreen.FadeSeconds);
        Assert.Equal(LoadingPhase.Done, screen.Phase);
        Assert.False(screen.Drawn);
        Assert.Equal(0, screen.Alpha);
    }

    [Fact]
    public void A_matchs_end_leaves_no_tasks_and_the_next_match_waits_for_its_own()
    {
        var logger = new RecordingLogger();
        var first = new TestTask("World") { Progress = LoadingProgress.Finished };
        var screen = Create(logger, [first]);
        screen.Begin(0);
        screen.Update(1);
        var version = screen.Version;

        screen.End();

        Assert.Equal(LoadingPhase.NotBegun, screen.Phase);
        Assert.Equal(0, screen.TaskCount);
        Assert.NotEqual(version, screen.Version);

        var second = new TestTask("Characters");
        screen.Initialize([second]);
        screen.Begin(5);
        screen.Update(6);
        Assert.True(screen.Active);
        Assert.Equal("Characters", screen.TaskName(0));
    }

    [Fact]
    public void At_the_time_limit_it_stops_waiting_and_names_what_had_not_finished()
    {
        var logger = new RecordingLogger();
        var sounds = new TestTask("Sounds") { Progress = LoadingProgress.Finished };
        var navigation = new TestTask("Navigation");
        var screen = Create(logger, [sounds, navigation], maxSeconds: 5);

        screen.Begin(0);
        screen.Update(4.9);
        Assert.True(screen.Active);
        screen.Update(5);

        Assert.Equal(LoadingPhase.Fading, screen.Phase);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("not finished: Navigation", warning.Message);
        Assert.Contains("finished: Sounds 4.9 s", warning.Message);
    }

    [Fact]
    public void After_a_reset_it_covers_the_picture_again_and_waits_for_every_task_afresh()
    {
        var logger = new RecordingLogger();
        var sounds = new TestTask("Sounds") { Progress = LoadingProgress.Finished };
        var world = new TestTask("World") { Progress = LoadingProgress.Finished };
        var screen = Create(logger, [sounds, world], maxSeconds: 5);
        screen.Begin(0);
        screen.Update(1);
        screen.Update(2);
        Assert.Equal(LoadingPhase.Done, screen.Phase);

        world.Progress = LoadingProgress.Part(false, 0.2f);
        screen.BeginAgain(20);
        var covering = screen.Active;
        var restarted = screen.Seconds;
        screen.Update(21);
        var waiting = screen.Active;
        var soundsDone = screen.TaskProgress(0).Done;
        world.Progress = LoadingProgress.Finished;
        screen.Update(22);

        Assert.True(covering);
        Assert.Equal(0, restarted);
        Assert.True(waiting);
        Assert.True(soundsDone);
        Assert.Equal(LoadingPhase.Fading, screen.Phase);
        // The second loading's own line, timed from the reset, and no Warning: its time limit starts again too.
        Assert.Equal(2, logger.Entries.Count(e => e.Message.StartsWith("Loading: done", StringComparison.Ordinal)));
        Assert.Contains(logger.Entries, e => e.Message.Contains("done after 2.0 s", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Turned_off_it_is_done_at_once()
    {
        var task = new TestTask("World");
        var screen = Create(new RecordingLogger(), [task], enabled: false);

        screen.Begin(0);
        screen.Update(1);

        Assert.Equal(LoadingPhase.Done, screen.Phase);
        Assert.False(screen.Drawn);
        Assert.Equal(0, task.Reads);
        screen.BeginAgain(2);
        Assert.Equal(LoadingPhase.Done, screen.Phase);
    }

    [Fact]
    public void The_version_changes_only_when_a_percentage_or_a_task_s_state_does()
    {
        var world = new TestTask("World") { Progress = LoadingProgress.Part(false, 0.101f) };
        var screen = Create(new RecordingLogger(), [world]);
        screen.Begin(0);
        screen.Update(0.1);
        var version = screen.Version;

        world.Progress = LoadingProgress.Part(false, 0.109f);
        screen.Update(0.2);
        Assert.Equal(version, screen.Version);

        world.Progress = LoadingProgress.Part(false, 0.12f);
        screen.Update(0.3);
        Assert.NotEqual(version, screen.Version);
    }

    [Fact]
    public void An_update_allocates_nothing()
    {
        var tasks = new[] { new TestTask("Sounds"), new TestTask("World") };
        var screen = Create(new RecordingLogger(), tasks, maxSeconds: 300);
        screen.Begin(0);
        for (var i = 0; i < 100; i++)
        {
            tasks[1].Progress = LoadingProgress.Part(false, i / 200f);
            screen.Update(i * 0.01);
        }

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            tasks[1].Progress = LoadingProgress.Part(false, i / 2000f);
            screen.Update(1 + i * 0.01);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.True(screen.Active);
    }
}
