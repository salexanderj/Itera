using Itera.Engine.Commands;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Particles;
using Itera.Engine.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Commands;

/// <summary>
/// The console's <c>particles stress</c>: a count past the bursts a frame takes is refused with the limit, since every
/// frame loops over it (a typo froze the game).
/// </summary>
public sealed class ParticleCommandsTests
{
    [Fact]
    public void A_stress_count_past_a_frames_bursts_is_refused_with_the_limit()
    {
        var options = Options.Create(new EngineOptions());
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        var emitter = new ParticleEmitter(options, NullLogger<ParticleEmitter>.Instance);
        emitter.Recipes.Register("smoke", new ParticleRecipe { LifetimeMin = 1, LifetimeMax = 2 });
        new ParticleCommands(
            commands,
            emitter,
            new ParticleStats(),
            new RenderView(options),
            new MatchControl()
        ).Register();
        var output = new Output();

        commands.Execute("particles stress smoke 1000000", output);

        Assert.Equal(0, emitter.StressBursts);
        var failure = Assert.Single(output.Failures);
        Assert.Contains(
            emitter.MaxBursts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            failure,
            StringComparison.Ordinal
        );

        commands.Execute($"particles stress smoke {emitter.MaxBursts}", output);
        Assert.Equal(emitter.MaxBursts, emitter.StressBursts);
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Failures { get; } = [];

        public void Print(string text) { }

        public void PrintError(string text) => Failures.Add(text);
    }
}
