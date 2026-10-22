using Itera.Engine.Assets;

namespace Game.Tests;

/// <summary>
/// Every model the game declares is ready as the engine reads it (<see cref="ModelChecks"/>): nothing refused, warned
/// of, left out, or unnamed; and each model built by a recipe within its class's budget
/// (<c>src/Game/ModelRecipes/budgets.json</c>).
/// </summary>
public sealed class GameModelsTests(GameRunFixture run) : IClassFixture<GameRunFixture>
{
    /// <summary>The budget class of each model built by a recipe, as its recipe names it (CLASS).</summary>
    private static readonly Dictionary<string, string> s_classes = new(StringComparer.Ordinal)
    {
        [GameModels.Crate] = "prop",
    };

    [Fact]
    public void Every_model_the_game_declares_is_ready_and_within_its_budget()
    {
        var assets = run.Get<MeshAssets>();
        var budgets = ModelBudget.ReadClasses(
            Path.Combine(RepositoryDocs.Root, "src", "Game", "ModelRecipes", "budgets.json")
        );

        Assert.NotEmpty(assets.Files);
        Assert.Empty(
            ModelChecks.Problems(assets, name => s_classes.TryGetValue(name, out var kind) ? budgets[kind] : null)
        );
    }

    [Fact]
    public void The_crate_has_its_point_on_its_lid()
    {
        var assets = run.Get<MeshAssets>();
        var crate = assets[assets.Find(GameModels.Crate)];

        var top = crate.Points[crate.FindPoint("top")];
        Assert.Equal(-1, top.Joint);
        Assert.Equal(crate.BoundsMax.Y, top.Position.Y, 0.01f);
    }
}
