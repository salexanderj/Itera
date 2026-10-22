using System.Globalization;
using Itera.Engine.Assets;

namespace Itera.Tools.ModelCheck;

/// <summary>
/// Checks glTF models as the engine reads them (<see cref="ModelInspection"/>, README.md):
/// <c>[--json] [--unit METRES] [--budget FILE --class NAME] PATH ...</c>. Prints each model's counts, problems, and
/// notes, or every report as JSON. Exits 0 when every model is ready, 1 when one has a problem, and 2 for a wrong
/// command line or budget file.
/// </summary>
internal static class Program
{
    private const string Usage =
        "Usage: ModelCheck [--json] [--unit METRES] [--budget FILE --class NAME] PATH.glb|PATH.gltf ...";

    private static int Main(string[] args)
    {
        var json = false;
        var unit = 1f;
        string? budgetFile = null;
        string? budgetClass = null;
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--unit":
                    if (
                        !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out unit)
                        || !(unit > 0)
                        || !float.IsFinite(unit)
                    )
                    {
                        return Wrong("--unit takes a positive number of metres a unit");
                    }

                    i++;
                    break;
                case "--budget" when value is not null:
                    budgetFile = value;
                    i++;
                    break;
                case "--class" when value is not null:
                    budgetClass = value;
                    i++;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        return Wrong($"{args[i]} is not an option, or lacks its value");
                    }

                    paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count == 0 || (budgetFile is null) != (budgetClass is null))
        {
            return Wrong(paths.Count == 0 ? "no model named" : "--budget and --class go together");
        }

        ModelBudget? budget = null;
        if (budgetFile is not null)
        {
            try
            {
                if (!ModelBudget.ReadClasses(budgetFile).TryGetValue(budgetClass!, out budget))
                {
                    return Wrong($"{budgetFile} has no class '{budgetClass}'");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return Wrong(ex.Message);
            }
        }

        var reports = paths.Select(path => ModelInspection.Inspect(path, unit, budget)).ToList();
        if (json)
        {
            Console.WriteLine(ModelInspection.ToJson(reports));
        }
        else
        {
            foreach (var report in reports)
            {
                Print(report);
            }
        }

        return reports.TrueForAll(report => report.Ok) ? 0 : 1;
    }

    private static void Print(ModelReport report)
    {
        var verdict = report.Ok ? "ready" : $"{report.Problems.Count} problem(s)";
        if (report.Asset is { } asset)
        {
            var height = asset.BoundsMax.Y - asset.BoundsMin.Y;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{report.File}: {verdict}: {report.Triangles} triangles, {report.Vertices} vertices, "
                        + $"{report.Joints.Count} joints, {report.Clips.Count} clips, {report.Materials.Count} "
                        + $"materials, {report.Textures.Count} textures, {height:0.###} m tall"
                )
            );
        }
        else
        {
            Console.WriteLine($"{report.File}: refused");
        }

        foreach (var problem in report.Problems)
        {
            Console.WriteLine($"  {problem.Kind.ToString().ToLowerInvariant()}: {problem.Message}");
        }

        foreach (var note in report.Notes)
        {
            Console.WriteLine($"  note: {note}");
        }
    }

    private static int Wrong(string why)
    {
        Console.Error.WriteLine($"{why}\n{Usage}");
        return 2;
    }
}
