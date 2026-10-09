using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Networking;
using Itera.Engine.Time;
using Itera.Engine.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Commands;

/// <summary><c>pause</c>, <c>resume</c>, <c>step</c>, and <c>timescale</c> on the step accumulator, offline only.</summary>
public sealed class TimeCommandsTests
{
    private const float Step = 1f / 60f;
    private readonly FixedStepAccumulator _time = new(Step, 8);
    private readonly DevConsole _console;
    private SessionRole _role = SessionRole.Offline;

    public TimeCommandsTests()
    {
        var commands = new ConsoleCommands(() => _role, NullLogger<ConsoleCommands>.Instance);
        _console = new DevConsole(
            Options.Create(new EngineOptions()),
            commands,
            new ConsoleLogBuffer(),
            NullLogger<DevConsole>.Instance
        );
        new TimeCommands(commands, _time).Register();
    }

    [Fact]
    public void Pause_and_resume_stop_and_start_the_steps()
    {
        Run("pause");
        _time.Add(Step * 3);
        Assert.False(_time.TryConsumeStep(out _));

        Run("resume");
        _time.Add(Step);

        Assert.True(_time.TryConsumeStep(out _));
        Assert.Equal("resumed at tick 0", Last());
    }

    [Fact]
    public void Step_pauses_and_runs_the_steps_asked_for()
    {
        Run("step 3");

        Assert.True(_time.Paused);
        Assert.Equal(3, _time.StepsOwed);
        Assert.Equal("stepping 3 from tick 0, then paused", Last());
        Run("step 0");
        Assert.Equal(3, _time.StepsOwed);
    }

    [Fact]
    public void Timescale_sets_and_prints_the_runs_scale_within_its_range()
    {
        Run("timescale 4");
        Assert.Equal(4, _time.TimeScale);
        Run("timescale 20");
        Assert.Equal(4, _time.TimeScale);

        Run("timescale");

        Assert.Equal("timescale 4", Last());
    }

    [Fact]
    public void In_a_session_they_are_refused()
    {
        _role = SessionRole.Host;

        Run("pause");

        Assert.False(_time.Paused);
        Assert.StartsWith("pause: works only in an offline game", Last(), StringComparison.Ordinal);
    }

    private void Run(string line)
    {
        _console.Submit(line);
        _console.RunPending();
    }

    private string Last() => _console.ScrollbackLine(_console.ScrollbackCount - 1).Text;
}
