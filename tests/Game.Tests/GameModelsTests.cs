using Itera.Engine.Assets;

namespace Game.Tests;

/// <summary>
/// Every model the game declares is ready as the engine reads it (<see cref="ModelChecks"/>): nothing refused, warned
/// of, left out, or unnamed. A game with budgets by asset class holds each model to its class's here too.
/// </summary>
public sealed class GameModelsTests(GameRunFixture run) : IClassFixture<GameRunFixture>
{
    [Fact]
    public void Every_model_the_game_declares_is_ready()
    {
        var assets = run.Get<MeshAssets>();

        Assert.NotEmpty(assets.Files);
        Assert.Empty(ModelChecks.Problems(assets));
    }
}
