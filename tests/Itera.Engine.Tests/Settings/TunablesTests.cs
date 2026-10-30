using Itera.Engine.Commands;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Settings;

/// <summary>
/// The run's tunables (<see cref="Tunables"/>) and the console's <c>tune</c> (<see cref="TuningCommands"/>): names, ranges,
/// and registering once sealed refused; a value set within its range and refused outside it; each scope put back to its
/// defaults; a default read again (rebased) within its range and refused outside it; and the command listing, listing
/// those changed, reading with a default's source, setting, refusing, and putting back.
/// </summary>
public sealed class TunablesTests
{
    [Fact]
    public void A_tunable_keeps_to_its_name_and_range_and_registering_once_sealed_is_refused()
    {
        var tunables = new Tunables();
        var speed = tunables.Register("move.walk", 4.5f, 0, 20, "Walking speed, m/s.");

        Assert.Same(speed, tunables.Find("move.walk"));
        Assert.Null(tunables.Find("move.run"));
        Assert.Equal((4.5f, 4.5f, TuningScope.Match), (speed.Value, speed.Default, speed.Scope));
        Assert.Throws<ArgumentException>(() => tunables.Register("move.walk", 1, 0, 2, ""));
        Assert.Throws<ArgumentException>(() => tunables.Register("move walk", 1, 0, 2, ""));
        Assert.Throws<ArgumentException>(() => tunables.Register("", 1, 0, 2, ""));
        Assert.Throws<ArgumentException>(() => tunables.Register("x", 3, 0, 2, ""));
        Assert.Throws<ArgumentException>(() => tunables.Register("y", float.NaN, 0, 2, ""));
        tunables.Seal();
        Assert.Throws<InvalidOperationException>(() => tunables.Register("late", 1, 0, 2, ""));
    }

    [Fact]
    public void A_value_is_set_within_its_range_and_each_scope_goes_back_to_its_defaults()
    {
        var tunables = new Tunables();
        var speed = tunables.Register("move.walk", 4.5f, 0, 20, "");
        var feel = tunables.Register("camera.ease", 0.5f, 0, 1, "", TuningScope.Local);
        var version = tunables.Version;

        Assert.True(tunables.TrySet(speed, 7));
        Assert.False(tunables.TrySet(speed, 21));
        Assert.False(tunables.TrySet(speed, float.PositiveInfinity));
        Assert.True(tunables.TrySet(feel, 0.25f));
        Assert.Equal((7f, 0.25f), (speed.Value, feel.Value));
        Assert.True(tunables.Version > version);

        tunables.Reset(TuningScope.Match);
        Assert.Equal((4.5f, 0.25f), (speed.Value, feel.Value));
        tunables.Reset(TuningScope.Local);
        Assert.Equal(0.5f, feel.Value);
    }

    [Fact]
    public void A_default_read_again_becomes_the_default_and_the_value_within_its_range()
    {
        var tunables = new Tunables();
        var speed = tunables.Register("move.walk", 4.5f, 0, 20, "", source: "move.json walk");
        var version = tunables.Version;

        Assert.True(tunables.Rebase(speed, 6));
        Assert.False(tunables.Rebase(speed, 21));
        Assert.True(tunables.TrySet(speed, 8));
        tunables.Reset(TuningScope.Match);

        Assert.Equal((6f, 6f, "move.json walk"), (speed.Value, speed.Default, speed.Source));
        Assert.True(tunables.Version > version);
    }

    [Fact]
    public void Tune_lists_reads_sets_refuses_and_puts_back()
    {
        var tunables = new Tunables();
        var speed = tunables.Register("move.walk", 4.5f, 0, 20, "Walking speed, m/s.", source: "move.json walk");
        tunables.Register("move.run", 6, 0, 20, "Running speed, m/s.");
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        new TuningCommands(commands, tunables, new MatchControl()).Register();
        var output = new Output();

        commands.Execute("tune", output);
        commands.Execute("tune move.walk 6", output);
        commands.Execute("tune move.walk 99", output);
        commands.Execute("tune move.crawl 1", output);
        commands.Execute("tune move.walk x", output);
        var changedFrom = output.Lines.Count;
        commands.Execute("tune changed", output);
        var changed = output.Lines.Skip(changedFrom).ToList();
        commands.Execute("tune move.walk", output);

        Assert.Contains(
            output.Lines,
            line => line.StartsWith("move.walk = 4.5 (0 to 20, match)", StringComparison.Ordinal)
        );
        Assert.Contains(
            output.Lines,
            line => line.StartsWith("move.walk = 6 (0 to 20, match, default 4.5)", StringComparison.Ordinal)
        );
        Assert.Equal(3, output.Errors.Count);
        Assert.Equal(6f, speed.Value);
        Assert.Equal(2, changed.Count);
        Assert.StartsWith("move.walk = 6", changed[0], StringComparison.Ordinal);
        Assert.Equal("1 tunable(s) not at their defaults", changed[1]);
        Assert.Equal("  its default from move.json walk", output.Lines[^1]);

        commands.Execute("tune default", output);
        Assert.Equal(4.5f, speed.Value);
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Errors.Add(text);
    }
}
