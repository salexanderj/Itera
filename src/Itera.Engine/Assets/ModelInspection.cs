using System.Text.Json;
using System.Text.Json.Serialization;

namespace Itera.Engine.Assets;

/// <summary>
/// Checks a glTF file as the engine will read it. <see cref="GltfImport"/> reads it with every texture decoded, and the
/// report (<see cref="ModelReport"/>) lists what the import refuses, what it warns of, what it leaves out or makes up
/// without a warning (<see cref="ImportFindings"/>), and what breaks a budget (<see cref="ModelBudget"/>), with the
/// counts a model's author checks. A model is ready to ship when its report has no problems. <c>tools/ModelCheck</c>
/// runs it from the command line. Allocates freely: a tool's and a test's.
/// </summary>
public static class ModelInspection
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The report on the model at <paramref name="path"/>, read with <paramref name="metresPerUnit"/> as
    /// <see cref="MeshAssets.Declare"/> would read it, and held to <paramref name="budget"/> when one is given.
    /// </summary>
    public static ModelReport Inspect(string path, float metresPerUnit = 1f, ModelBudget? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = Path.GetFileName(path);
        string[] required;
        try
        {
            required = GltfImport.ReadRequiredExtensions(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ModelReport.Refusal(path, [ex.Message]);
        }

        // Every extension it cannot read, where the import stops at the first.
        var unread = required.Where(extension => !GltfImport.Reads(extension)).ToArray();
        if (unread.Length > 0)
        {
            return ModelReport.Refusal(path, [.. unread.Select(extension => GltfImport.RefusalOf(file, extension))]);
        }

        var warnings = new List<string>();
        var findings = new ImportFindings();
        MeshAsset asset;
        try
        {
            asset = GltfImport.Load(
                Path.GetFileNameWithoutExtension(path),
                path,
                metresPerUnit,
                TextureReading.Decode,
                warnings,
                findings: findings
            );
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ModelReport.Refusal(path, [ex.Message]);
        }

        var problems = warnings.Select(warning => new ModelProblem(ModelProblemKind.Import, warning)).ToList();
        var notes = new List<string>();
        CheckContent(file, asset, findings, problems, notes);
        if (budget is not null)
        {
            CheckBudget(file, asset, budget, problems);
        }

        return ModelReport.Of(path, asset, problems, notes);
    }

    /// <summary>The reports as indented JSON, names in camel case: what <c>tools/ModelCheck --json</c> prints.</summary>
    public static string ToJson(IReadOnlyList<ModelReport> reports) => JsonSerializer.Serialize(reports, s_json);

    /// <summary>What the import leaves out or makes up without a warning, and what a game cannot name or find.</summary>
    private static void CheckContent(
        string file,
        MeshAsset asset,
        ImportFindings findings,
        List<ModelProblem> problems,
        List<string> notes
    )
    {
        void Problem(string message) => problems.Add(new ModelProblem(ModelProblemKind.Content, $"{file} {message}"));

        if (asset.TriangleCount == 0)
        {
            Problem("has no triangles");
        }

        if (findings.Scenes > 1)
        {
            Problem($"has {findings.Scenes} scenes: only the default one is read");
        }

        if (findings.CameraOrLightNodes > 0)
        {
            Problem($"has {findings.CameraOrLightNodes} nodes with a camera or a light, which are left out");
        }

        if (findings.PrimitivesWithoutMaterial > 0)
        {
            Problem(
                $"has {findings.PrimitivesWithoutMaterial} primitives without a material: they take glTF's default, "
                    + "which has no name"
            );
        }

        for (var m = 0; m < asset.Materials.Length; m++)
        {
            var material = asset.Materials[m];
            if (material.Name.Length == 0 && !ReferenceEquals(material, MeshMaterialData.Default))
            {
                Problem($"has material {m} with no name: a game cannot name it to recolour it");
            }
        }

        if (findings.GeneratedTangentPrimitives > 0)
        {
            Problem(
                $"has {findings.GeneratedTangentPrimitives} normal-mapped primitives without tangents: the engine "
                    + "makes them by Lengyel's method, not MikkTSpace, so they differ at mirrored UV seams; export the "
                    + "tangents"
            );
        }

        if (findings.ExtraInfluenceVertices > 0)
        {
            Problem(
                $"has {findings.ExtraInfluenceVertices} vertices that weigh more than four joints: the engine keeps "
                    + "the four largest of each"
            );
        }

        if (asset.Skin is { } skin)
        {
            var unweighted = skin.Count(vertex => !(vertex.Weight(0) > 0));
            if (unweighted > 0)
            {
                Problem($"has {unweighted} vertices that weigh no joint: they stand still while the rig moves");
            }
        }

        if (findings.UnnamedJoints > 0)
        {
            Problem($"has {findings.UnnamedJoints} joints with no name");
        }

        if (findings.UnnamedClips > 0)
        {
            Problem($"has {findings.UnnamedClips} animations with no name, named by their index");
        }

        if (findings.LeftOutAnimations > 0)
        {
            Problem(
                $"has {findings.LeftOutAnimations} animations with no channel on a skin's joints, which are left out"
            );
        }

        if (asset.Rig is { } rig)
        {
            Shared(rig.Names, "joints", Problem);
            Shared(rig.Clips.Select(clip => clip.Name), "clips", Problem);
        }

        if (findings.ReusedMeshes > 0)
        {
            notes.Add(
                $"{file} uses {findings.ReusedMeshes} meshes at more than one node: each use counts its own vertices"
            );
        }

        foreach (var extension in findings.ExtensionsUsed.Distinct(StringComparer.Ordinal))
        {
            if (!GltfImport.Reads(extension) && extension != "KHR_texture_transform")
            {
                notes.Add($"{file} uses the glTF extension {extension}, which the engine does not read");
            }
        }
    }

    /// <summary>A problem for each name that more than one of <paramref name="what"/> has: a lookup by name finds the first.</summary>
    private static void Shared(IEnumerable<string> names, string what, Action<string> problem)
    {
        foreach (var group in names.GroupBy(name => name, StringComparer.Ordinal))
        {
            var count = group.Count();
            if (count > 1)
            {
                problem($"has {count} {what} named '{group.Key}': a lookup by name finds the first");
            }
        }
    }

    private static void CheckBudget(string file, MeshAsset asset, ModelBudget budget, List<ModelProblem> problems)
    {
        void Problem(string message) => problems.Add(new ModelProblem(ModelProblemKind.Budget, $"{file} {message}"));

        if (asset.TriangleCount > budget.MostTriangles)
        {
            Problem($"has {asset.TriangleCount} triangles, over the budget's {budget.MostTriangles}");
        }

        var joints = asset.Rig?.JointCount ?? 0;
        if (joints > budget.MostJoints)
        {
            Problem($"has {joints} joints, over the budget's {budget.MostJoints}");
        }

        if (asset.Materials.Length > budget.MostMaterials)
        {
            Problem($"has {asset.Materials.Length} materials, over the budget's {budget.MostMaterials}");
        }

        foreach (var texture in asset.Textures)
        {
            if (Math.Max(texture.Width, texture.Height) > budget.MostTextureTexels)
            {
                Problem(
                    $"has {texture.Name} of {texture.Width} by {texture.Height} texels, over the budget's "
                        + $"{budget.MostTextureTexels} a side"
                );
            }
        }

        var height = asset.BoundsMax.Y - asset.BoundsMin.Y;
        if (height < budget.LeastHeight)
        {
            Problem($"is {Metres(height)} m tall, under the budget's {Metres(budget.LeastHeight.Value)} m");
        }

        if (height > budget.MostHeight)
        {
            Problem($"is {Metres(height)} m tall, over the budget's {Metres(budget.MostHeight.Value)} m");
        }

        if (budget.Skinned is { } skinned && skinned != (asset.Rig is not null))
        {
            Problem(skinned ? "has no rig, and the budget asks for one" : "has a rig, and the budget asks for none");
        }

        foreach (var clip in budget.Clips)
        {
            if (asset.Rig?.FindClip(clip) is not >= 0)
            {
                Problem($"has no clip named '{clip}', which the budget asks for");
            }
        }
    }

    private static string Metres(float value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What a check of a model found (<see cref="ModelInspection"/>), and its counts.</summary>
public sealed record ModelReport
{
    /// <summary>The model's file, as given.</summary>
    public required string File { get; init; }

    /// <summary>True when the model has no problems.</summary>
    public bool Ok => Problems.Count == 0;

    public required IReadOnlyList<ModelProblem> Problems { get; init; }

    /// <summary>What is worth knowing but does not stop the model: meshes used twice, extensions the engine ignores.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public int Triangles { get; init; }

    /// <summary>The vertices as the engine holds them: a triangle without normals has three of its own, and a mesh used at two nodes is counted twice.</summary>
    public int Vertices { get; init; }

    /// <summary>The least corner of the vertices' box, x, y, and z in metres.</summary>
    public IReadOnlyList<float> BoundsMin { get; init; } = [0, 0, 0];

    /// <summary>The greatest corner of the vertices' box, x, y, and z in metres.</summary>
    public IReadOnlyList<float> BoundsMax { get; init; } = [0, 0, 0];

    /// <summary>The materials' names, in the model's order (glTF's default last when a primitive has none).</summary>
    public IReadOnlyList<string> Materials { get; init; } = [];

    /// <summary>The textures the import makes: one an image in each role a material uses it in.</summary>
    public IReadOnlyList<ModelTexture> Textures { get; init; } = [];

    /// <summary>The rig's joints, parents first; none for a static model.</summary>
    public IReadOnlyList<string> Joints { get; init; } = [];

    public IReadOnlyList<ModelClip> Clips { get; init; } = [];

    /// <summary>The model as the engine read it; null when it was refused.</summary>
    [JsonIgnore]
    public MeshAsset? Asset { get; init; }

    internal static ModelReport Refusal(string path, string[] reasons) =>
        new()
        {
            File = path,
            Problems = [.. reasons.Select(reason => new ModelProblem(ModelProblemKind.Refused, reason))],
        };

    internal static ModelReport Of(string path, MeshAsset asset, List<ModelProblem> problems, List<string> notes) =>
        new()
        {
            File = path,
            Problems = problems,
            Notes = notes,
            Triangles = asset.TriangleCount,
            Vertices = asset.Vertices.Length,
            BoundsMin = [asset.BoundsMin.X, asset.BoundsMin.Y, asset.BoundsMin.Z],
            BoundsMax = [asset.BoundsMax.X, asset.BoundsMax.Y, asset.BoundsMax.Z],
            Materials = [.. asset.Materials.Select(material => material.Name)],
            Textures =
            [
                .. asset.Textures.Select(texture => new ModelTexture(
                    texture.Name,
                    texture.Role,
                    texture.Width,
                    texture.Height
                )),
            ],
            Joints = asset.Rig?.Names ?? [],
            Clips = [.. (asset.Rig?.Clips ?? []).Select(clip => new ModelClip(clip.Name, clip.Duration))],
            Asset = asset,
        };
}

/// <summary>What kind of problem a check found.</summary>
public enum ModelProblemKind
{
    /// <summary>The engine cannot read the file.</summary>
    Refused,

    /// <summary>A warning of the import: something left out or read another way.</summary>
    Import,

    /// <summary>Something the import leaves out or makes up without a warning, or that a game cannot name or find.</summary>
    Content,

    /// <summary>Over or under a limit of the budget the model is held to.</summary>
    Budget,
}

/// <summary>One problem a check found, with the model's file name at the start of its message.</summary>
public sealed record ModelProblem(ModelProblemKind Kind, string Message);

/// <summary>A texture the import makes, its size in texels.</summary>
public sealed record ModelTexture(string Name, MeshTextureRole Role, int Width, int Height);

/// <summary>A clip of the rig and its length in seconds.</summary>
public sealed record ModelClip(string Name, float Seconds);

/// <summary>
/// The limits a model is held to (<see cref="ModelInspection"/>); a limit left null is not checked. A game keeps its
/// budgets by asset class in a JSON file (<see cref="ReadClasses"/>).
/// </summary>
public sealed record ModelBudget
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public int? MostTriangles { get; init; }

    public int? MostJoints { get; init; }

    public int? MostMaterials { get; init; }

    /// <summary>The longest side of any texture, in texels.</summary>
    public int? MostTextureTexels { get; init; }

    /// <summary>The least height of the vertices' box, in metres: a model in centimetres read as metres is 100 times too tall.</summary>
    public float? LeastHeight { get; init; }

    /// <summary>The greatest height of the vertices' box, in metres.</summary>
    public float? MostHeight { get; init; }

    /// <summary>True when the model must have a rig, false when it must not.</summary>
    public bool? Skinned { get; init; }

    /// <summary>The clips the model must have, by name.</summary>
    public IReadOnlyList<string> Clips { get; init; } = [];

    /// <summary>
    /// The budgets of a JSON file: an object whose members are asset classes, each a budget (<c>{ "prop": {
    /// "mostTriangles": 3000 } }</c>; comments allowed). Throws <see cref="InvalidDataException"/> for a file that
    /// does not parse or names a limit there is not.
    /// </summary>
    public static IReadOnlyDictionary<string, ModelBudget> ReadClasses(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ModelBudget>>(File.ReadAllText(path), s_json)
                ?? throw new InvalidDataException($"{path} holds no budgets.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }
}
