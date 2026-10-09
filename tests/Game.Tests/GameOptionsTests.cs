using Itera.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Tests;

/// <summary>
/// The game's options stop the game with the key named: the shipped settings pass; a height out of range, a camera
/// pose that does not parse, a misspelt key in a section of the game's, and a section the game does not have each fail;
/// and keys match in any case, as the configuration binder matches them.
/// </summary>
public sealed class GameOptionsTests
{
    [Fact]
    public void The_shipped_settings_pass()
    {
        using var services = GameComposition.Build();

        Validate(services);
    }

    [Theory]
    [InlineData("Game:Player:HeightMetres", "3", "Game:Player:HeightMetres must be between")]
    [InlineData("Game:World:CameraPose", "1,2,3", "Game:World:CameraPose must be")]
    [InlineData("Game:World:CameraPose", "5000,20,0,0,0", "Game:World:CameraPose must lie in the play area")]
    [InlineData("Game:Player:HeightMetre", "2", "Game:Player:HeightMetre is not an option")]
    [InlineData("Game:Wrld:CameraPose", "1,2,3,4,5", "Game:Wrld is not an option")]
    [InlineData("Game:Player:HeightMetres", "tall", "Game:Player")]
    public void A_bad_value_or_key_is_named(string key, string value, string message)
    {
        using var services = GameComposition.Build(new Dictionary<string, string?> { [key] = value });

        var failure = Assert.Throws<OptionsValidationException>(() => Validate(services));

        Assert.Contains(message, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_match_in_any_case()
    {
        using var services = GameComposition.Build(
            new Dictionary<string, string?>
            {
                ["GAME:PLAYER:HEIGHTMETRES"] = "2",
                ["game:world:camerapose"] = "1,20,3,0,0",
            }
        );

        Validate(services);
    }

    /// <summary>What start-up checks before the loop: the game's options (Program), then the engine's (the loop's).</summary>
    private static void Validate(IServiceProvider services)
    {
        GameServiceCollectionExtensions.ValidateOptions(services);
        _ = services.GetRequiredService<IOptions<EngineOptions>>().Value;
    }
}
