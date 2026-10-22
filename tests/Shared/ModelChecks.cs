using Itera.Engine.Assets;

namespace Itera.TestSupport;

/// <summary>
/// Checks every glTF file a run declares (<see cref="MeshAssets.Files"/>) as the engine reads it
/// (<see cref="ModelInspection"/>), held to a game's budgets: a game's test of its models runs it over its composition's
/// <see cref="MeshAssets"/> once the run's startups have declared them.
/// </summary>
public static class ModelChecks
{
    /// <summary>
    /// The problems of every declared model, each read with its declared unit and held to the budget
    /// <paramref name="budgetOf"/> gives its name (null: none), as "NAME: KIND: MESSAGE". Empty when every model is ready.
    /// </summary>
    public static IReadOnlyList<string> Problems(MeshAssets assets, Func<string, ModelBudget?>? budgetOf = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var problems = new List<string>();
        foreach (var model in assets.Files)
        {
            var report = ModelInspection.Inspect(model.Path, model.MetresPerUnit, budgetOf?.Invoke(model.Name));
            problems.AddRange(report.Problems.Select(problem => $"{model.Name}: {problem.Kind}: {problem.Message}"));
        }

        return problems;
    }
}
