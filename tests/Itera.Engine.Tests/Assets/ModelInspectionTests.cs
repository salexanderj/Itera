using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using SkiaSharp;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="ModelInspection"/> on glTF files written in the test (<see cref="GltfBuilder"/>): a clean model is ready,
/// and each refusal, import warning, silent loss, and broken budget is a problem of its kind.
/// </summary>
public sealed class ModelInspectionTests : IDisposable
{
    private static readonly Vector3[] s_triangle = [new(0, 0, 0), new(1, 0, 0), new(0, 2, 0)];
    private static readonly Vector3[] s_up = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
    private static readonly Vector2[] s_uvs = [new(0, 0), new(1, 0), new(0, 1)];

    private readonly string _folder = Directory.CreateTempSubdirectory("itera-inspect-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_clean_model_is_ready_with_its_counts()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf, gltf.Material(new JsonObject { ["name"] = "paint" }));

        var report = ModelInspection.Inspect(gltf.WriteGlb(_folder, "clean"));

        Assert.True(report.Ok, string.Join("; ", report.Problems));
        Assert.Equal((1, 3), (report.Triangles, report.Vertices));
        Assert.Equal(["paint"], report.Materials);
        Assert.Equal([0f, 0f, 0f], report.BoundsMin);
        Assert.Equal([1f, 2f, 0f], report.BoundsMax);
        Assert.Empty(report.Joints);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void Every_required_extension_it_cannot_read_is_a_refusal()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf, gltf.Material(new JsonObject { ["name"] = "paint" }));
        gltf.ExtensionsRequired.AddRange(["KHR_draco_mesh_compression", "KHR_texture_basisu"]);
        gltf.ExtensionsUsed.AddRange(gltf.ExtensionsRequired);

        var report = ModelInspection.Inspect(gltf.WriteGltf(_folder, "packed"));

        Assert.Null(report.Asset);
        Assert.Collection(
            report.Problems,
            problem => AssertProblem(problem, ModelProblemKind.Refused, "KHR_draco_mesh_compression"),
            problem => AssertProblem(problem, ModelProblemKind.Refused, "KHR_texture_basisu")
        );
    }

    [Fact]
    public void A_file_that_is_not_gltf_is_refused()
    {
        var path = Path.Combine(_folder, "nonsense.gltf");
        File.WriteAllText(path, "not json");

        var problem = Assert.Single(ModelInspection.Inspect(path).Problems);

        AssertProblem(problem, ModelProblemKind.Refused, "does not parse");
    }

    [Fact]
    public void An_import_warning_is_a_problem()
    {
        var gltf = new GltfBuilder();
        var lines = GltfBuilder.Primitive(gltf.Accessor(s_triangle));
        lines["mode"] = 1;
        gltf.Node(new JsonObject { ["mesh"] = gltf.Mesh(lines) });

        var report = ModelInspection.Inspect(gltf.WriteGltf(_folder, "lines"));

        Assert.Contains(report.Problems, problem => problem.Kind == ModelProblemKind.Import);
    }

    [Fact]
    public void A_primitive_without_a_material_and_a_material_without_a_name_are_problems()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf, material: null);
        AddTriangle(gltf, gltf.Material(new JsonObject()));

        var report = ModelInspection.Inspect(gltf.WriteGltf(_folder, "unnamed"));

        Assert.Collection(
            report.Problems,
            problem => AssertProblem(problem, ModelProblemKind.Content, "without a material"),
            problem => AssertProblem(problem, ModelProblemKind.Content, "material 0 with no name")
        );
    }

    [Fact]
    public void Normal_mapped_triangles_without_tangents_are_a_problem()
    {
        var gltf = new GltfBuilder();
        var normals = gltf.Texture(gltf.Image(GltfBuilder.Png(4, 4, new SKColor(128, 128, 255))));
        var material = gltf.Material(
            new JsonObject
            {
                ["name"] = "bumps",
                ["normalTexture"] = new JsonObject { ["index"] = normals },
            }
        );
        AddTriangle(gltf, material);

        var problem = Assert.Single(ModelInspection.Inspect(gltf.WriteGltf(_folder, "bumps")).Problems);

        AssertProblem(problem, ModelProblemKind.Content, "without tangents");
    }

    [Fact]
    public void A_vertex_on_more_than_four_joints_is_a_problem()
    {
        var problem = Assert.Single(ModelInspection.Inspect(WriteSkinned("five", fiveJoints: true)).Problems);

        AssertProblem(problem, ModelProblemKind.Content, "1 vertices that weigh more than four joints");
    }

    [Fact]
    public void A_mesh_off_the_skin_of_a_skinned_model_is_a_problem()
    {
        var problem = Assert.Single(ModelInspection.Inspect(WriteSkinned("apart", staticPart: true)).Problems);

        AssertProblem(problem, ModelProblemKind.Content, "3 vertices that weigh no joint");
    }

    [Fact]
    public void A_skinned_vertex_that_weighs_nothing_is_refused()
    {
        // SharpGLTF's reading refuses weights that do not sum to one, so the engine never loads such a file.
        var problem = Assert.Single(ModelInspection.Inspect(WriteSkinned("loose", oneLoose: true)).Problems);

        AssertProblem(problem, ModelProblemKind.Refused, "Weight Sum invalid");
    }

    [Fact]
    public void Joints_that_share_a_name_are_a_problem()
    {
        var problem = Assert.Single(
            ModelInspection.Inspect(WriteSkinned("twins", names: ["a", "b", "b", "c", "d"])).Problems
        );

        AssertProblem(problem, ModelProblemKind.Content, "2 joints named 'b'");
    }

    [Fact]
    public void A_clip_with_no_name_is_a_problem()
    {
        var problem = Assert.Single(ModelInspection.Inspect(WriteSkinned("anonymous", clipName: "")).Problems);

        AssertProblem(problem, ModelProblemKind.Content, "1 animations with no name");
    }

    [Fact]
    public void An_animation_of_no_joint_is_left_out_and_a_problem()
    {
        var report = ModelInspection.Inspect(WriteSkinned("stray", animateMeshNode: true));

        Assert.Contains(
            report.Problems,
            problem =>
                problem.Kind == ModelProblemKind.Content
                && problem.Message.Contains("left out", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void A_static_models_animations_are_left_out_and_a_problem()
    {
        var gltf = new GltfBuilder();
        var node = AddTriangle(gltf, gltf.Material(new JsonObject { ["name"] = "paint" }));
        gltf.Animation(
            "spin",
            (
                node,
                "rotation",
                gltf.Accessor(new float[] { 0, 1 }),
                gltf.Accessor([Quaternion.Identity, Quaternion.Identity]),
                "LINEAR"
            )
        );

        var report = ModelInspection.Inspect(gltf.WriteGltf(_folder, "spinning"));

        Assert.Contains(
            report.Problems,
            problem =>
                problem.Kind == ModelProblemKind.Content
                && problem.Message.Contains("1 animations", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void A_camera_node_is_left_out_and_a_problem()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf, gltf.Material(new JsonObject { ["name"] = "paint" }));
        gltf.Node(new JsonObject { ["name"] = "eye", ["camera"] = 0 });
        var path = gltf.WriteGltf(_folder, "camera");
        // The builder writes no cameras: add one for the node to hold.
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["cameras"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "perspective",
                ["perspective"] = new JsonObject { ["yfov"] = 1f, ["znear"] = 0.1f },
            }
        );
        File.WriteAllText(path, json.ToJsonString());

        var problem = Assert.Single(ModelInspection.Inspect(path).Problems);

        AssertProblem(problem, ModelProblemKind.Content, "1 nodes with a camera or a light");
    }

    [Fact]
    public void Points_that_share_a_name_are_a_problem()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf, gltf.Material(new JsonObject { ["name"] = "paint" }));
        gltf.Node(new JsonObject { ["name"] = "grip" });
        gltf.Node(new JsonObject { ["name"] = "grip", ["translation"] = new JsonArray(0f, 1f, 0f) });

        var report = ModelInspection.Inspect(gltf.WriteGltf(_folder, "grips"));

        AssertProblem(Assert.Single(report.Problems), ModelProblemKind.Content, "2 points named 'grip'");
        Assert.Equal(["grip", "grip"], report.Points.Select(point => point.Name));
    }

    [Fact]
    public void An_empty_node_with_no_name_is_a_problem()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf, gltf.Material(new JsonObject { ["name"] = "paint" }));
        gltf.Node(new JsonObject { ["translation"] = new JsonArray(0f, 1f, 0f) });

        var problem = Assert.Single(ModelInspection.Inspect(gltf.WriteGltf(_folder, "nameless")).Problems);

        AssertProblem(problem, ModelProblemKind.Content, "1 empty nodes with no name");
    }

    [Fact]
    public void A_mesh_used_at_two_nodes_is_a_note_not_a_problem()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf, gltf.Material(new JsonObject { ["name"] = "paint" }));
        gltf.Node(new JsonObject { ["mesh"] = 0, ["translation"] = new JsonArray(5f, 0f, 0f) });

        var report = ModelInspection.Inspect(gltf.WriteGltf(_folder, "twice"));

        Assert.True(report.Ok);
        Assert.Equal(6, report.Vertices);
        Assert.Contains("1 meshes at more than one node", Assert.Single(report.Notes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "mostTriangles": 0 }""", 1f, "1 triangles, over the budget's 0")]
    [InlineData("""{ "mostJoints": 4 }""", 1f, "5 joints, over the budget's 4")]
    [InlineData("""{ "mostMaterials": 0 }""", 1f, "1 materials, over the budget's 0")]
    [InlineData("""{ "leastHeight": 1.5 }""", 1f, "1 m tall, under the budget's 1.5 m")]
    [InlineData("""{ "mostHeight": 0.5 }""", 1f, "1 m tall, over the budget's 0.5 m")]
    [InlineData("""{ "mostHeight": 0.5 }""", 0.01f, "")]
    [InlineData("""{ "skinned": false }""", 1f, "has a rig, and the budget asks for none")]
    [InlineData("""{ "clips": ["wave", "jump"] }""", 1f, "no clip named 'jump'")]
    [InlineData("""{ "points": ["muzzle"] }""", 1f, "no point named 'muzzle'")]
    public void A_budget_breaks_on_each_limit_it_sets(string budget, float unit, string expected)
    {
        var budgets = Path.Combine(_folder, "budgets.json");
        File.WriteAllText(budgets, $$"""{ "class": {{budget}} }""");

        var report = ModelInspection.Inspect(WriteSkinned("budgeted"), unit, ModelBudget.ReadClasses(budgets)["class"]);

        if (expected.Length == 0)
        {
            Assert.True(report.Ok, string.Join("; ", report.Problems));
        }
        else
        {
            AssertProblem(Assert.Single(report.Problems), ModelProblemKind.Budget, expected);
        }
    }

    [Fact]
    public void A_budget_file_that_names_a_limit_there_is_not_is_refused()
    {
        var budgets = Path.Combine(_folder, "budgets.json");
        File.WriteAllText(budgets, """{ "prop": { "mostTriangle": 10 } }""");

        Assert.Throws<InvalidDataException>(() => ModelBudget.ReadClasses(budgets));
    }

    [Fact]
    public void The_json_names_its_fields_in_camel_case()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf, material: null);

        var json = JsonNode.Parse(ModelInspection.ToJson([ModelInspection.Inspect(gltf.WriteGltf(_folder, "json"))]))!;

        var report = json[0]!;
        Assert.False(report["ok"]!.GetValue<bool>());
        Assert.Equal("content", report["problems"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(1, report["triangles"]!.GetValue<int>());
        Assert.Null(report["asset"]);
    }

    /// <summary>A triangle 2 m tall with normals and uvs on a node of its own; the node's index.</summary>
    private static int AddTriangle(GltfBuilder gltf, int? material)
    {
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(s_triangle),
                gltf.Accessor(s_up),
                gltf.Accessor(s_uvs),
                gltf.Indices(0, 1, 2),
                material
            )
        );
        return gltf.Node(new JsonObject { ["mesh"] = mesh });
    }

    /// <summary>
    /// A triangle 1 m tall on a skin of five joints under a root (each 1 m above the last, identity inverse binds), with a
    /// clip "wave": by default each vertex on one joint, all named. <paramref name="staticPart"/> adds a triangle on a
    /// node of its own with no skin.
    /// </summary>
    private string WriteSkinned(
        string name,
        bool fiveJoints = false,
        bool oneLoose = false,
        string[]? names = null,
        string clipName = "wave",
        bool animateMeshNode = false,
        bool staticPart = false
    )
    {
        names ??= ["a", "b", "c", "d", "e"];
        var gltf = new GltfBuilder();
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        var primitive = GltfBuilder.Primitive(
            gltf.Accessor(positions),
            gltf.Accessor(s_up),
            indices: gltf.Indices(0, 1, 2),
            material: gltf.Material(new JsonObject { ["name"] = "skin" }),
            joints: gltf.Joints(0, 1, 2, 3, 1, 0, 0, 0, 2, 0, 0, 0),
            weights: gltf.Accessor(
                new Vector4[]
                {
                    fiveJoints ? new(0.2f) : new(1, 0, 0, 0),
                    oneLoose ? Vector4.Zero : new(1, 0, 0, 0),
                    new(1, 0, 0, 0),
                }
            )
        );
        if (fiveJoints)
        {
            var attributes = primitive["attributes"]!.AsObject();
            attributes["JOINTS_1"] = gltf.Joints(4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            attributes["WEIGHTS_1"] = gltf.Accessor(new Vector4[] { new(0.2f, 0, 0, 0), Vector4.Zero, Vector4.Zero });
        }

        // Nodes 0 to 4 the joints, each the child of the last; 5 the skinned mesh.
        for (var j = 0; j < 5; j++)
        {
            var joint = new JsonObject { ["translation"] = new JsonArray(0f, j == 0 ? 0f : 1f, 0f) };
            if (names[j] is { Length: > 0 } jointName)
            {
                joint["name"] = jointName;
            }

            if (j < 4)
            {
                joint["children"] = new JsonArray(j + 1);
            }

            gltf.Node(joint, root: j == 0);
        }

        var skin = gltf.Skin(gltf.Accessor(Enumerable.Repeat(Matrix4x4.Identity, 5).ToArray()), 0, 1, 2, 3, 4);
        var meshNode = gltf.Node(new JsonObject { ["mesh"] = gltf.Mesh(primitive), ["skin"] = skin });
        if (staticPart)
        {
            AddTriangle(gltf, gltf.Material(new JsonObject { ["name"] = "part" }));
        }

        gltf.Animation(
            clipName,
            (
                animateMeshNode ? meshNode : 1,
                "rotation",
                gltf.Accessor(new float[] { 0, 1 }),
                gltf.Accessor([Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f)]),
                "LINEAR"
            )
        );
        return gltf.WriteGltf(_folder, name);
    }

    private static void AssertProblem(ModelProblem problem, ModelProblemKind kind, string text)
    {
        if (problem.Kind != kind || !problem.Message.Contains(text, StringComparison.Ordinal))
        {
            Assert.Fail($"Expected a {kind} problem with '{text}', got {problem.Kind}: {problem.Message}");
        }
    }
}
