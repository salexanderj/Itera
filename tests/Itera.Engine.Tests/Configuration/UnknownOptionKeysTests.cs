using Itera.Engine.Tests.Matches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Configuration;

/// <summary>
/// A misspelt option key stops the run with the key named (<see cref="UnknownOptionKeys{TOptions}"/>): the defaults hold
/// no unknown key; one under <c>Engine</c> is named by its full path; a value that does not bind fails when the options
/// are read, naming its section; and keys match in any case, as the configuration binder matches them.
/// </summary>
public sealed class UnknownOptionKeysTests
{
    [Fact]
    public void The_defaults_have_no_unknown_key()
    {
        using var services = EngineComposition.Build();

        Validate(services);
    }

    [Theory]
    [InlineData("Engine:Renderer:VSync", "Engine:Renderer:VSync is not an option")]
    [InlineData("Engine:Voxel:Seed", "Engine:Voxel is not an option")]
    [InlineData("Engine:Window:Titel", "Engine:Window:Titel is not an option")]
    public void A_misspelt_key_is_named_by_its_full_path(string key, string message)
    {
        using var services = EngineComposition.Build(new Dictionary<string, string?> { [key] = "1" });

        var failure = Assert.Throws<OptionsValidationException>(() => Validate(services));

        Assert.Contains(message, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_match_in_any_case()
    {
        using var services = EngineComposition.Build(
            new Dictionary<string, string?> { ["engine:window:vsync"] = "false", ["ENGINE:CAMERA:FLYSPEED"] = "20" }
        );

        Validate(services);
    }

    /// <summary>What start-up checks before the loop: the engine's options.</summary>
    private static void Validate(IServiceProvider services) =>
        _ = services.GetRequiredService<IOptions<EngineOptions>>().Value;
}
