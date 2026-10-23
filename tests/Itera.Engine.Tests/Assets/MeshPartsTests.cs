using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using Microsoft.Extensions.Logging.Abstractions;
using SharpGLTF.Schema2;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// A file that holds a body and the parts that hang from its joints (<see cref="MeshAssets.Declare"/>'s node and rigOf):
/// the whole file leaves out a mesh not skinned under a joint, and counts it; that mesh's node read as a part stands in
/// its joint's space, the nodes between applied, so posed at rest it lands where the file puts it, with only its own
/// material and no rig; a node named nowhere or twice cannot be read; a body bound on another model's rig takes that
/// rig's joint order and clips, its vertices following their joints, and the file is listed once; a body whose joints
/// rest elsewhere, or that has clips of its own, stops the step naming the difference; a file's whole and its parts read
/// it once and hold the same texture where their materials use one image.
/// </summary>
public sealed class MeshPartsTests : IDisposable
{
    private static readonly Quaternion s_quarterZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

    private readonly string _folder = Directory.CreateTempSubdirectory("itera-parts-").FullName;
    private readonly List<string> _warnings = [];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_whole_file_leaves_out_a_mesh_under_a_joint_and_that_part_stands_in_its_joints_space()
    {
        var path = WriteBody("body", aFirst: true, clip: false);
        var findings = new ImportFindings();

        var whole = GltfImport.Load("body", path, 1, TextureReading.None, _warnings, findings: findings);
        var part = GltfImport.Load("guard", path, 1, TextureReading.None, _warnings, node: "guard");

        Assert.Equal(1, whole.TriangleCount);
        Assert.Equal((1, 1), (findings.PartMeshes, findings.PartTriangles));
        Assert.Null(part.Rig);
        Assert.Equal(1, part.TriangleCount);
        Assert.Equal("iron", Assert.Single(part.Materials).Name);
        Assert.Empty(_warnings);

        // Posed at rest on its joint, each of its corners lands where the file's own world matrix puts it.
        var rig = whole.Rig!;
        var locals = new JointTransform[rig.JointCount];
        var globals = new Matrix4x4[rig.JointCount];
        rig.Pose(-1, 0, locals);
        rig.Globals(locals, globals);
        var model = ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix });
        var world = model.LogicalNodes.Single(node => node.Name == "guard").WorldMatrix;
        Vector3[] corners = [new(0, 0, 0), new(0.1f, 0, 0), new(0, 0.1f, 0)];
        for (var i = 0; i < corners.Length; i++)
        {
            AssertNear(
                Vector3.Transform(corners[i], world),
                Vector3.Transform(part.Vertices[i].Position, globals[rig.FindJoint("a")])
            );
        }
    }

    [Fact]
    public void A_files_whole_and_its_parts_read_it_once_and_hold_the_same_texture()
    {
        var assets = new MeshAssets(NullLogger<MeshAssets>.Instance);
        var path = WriteBody("textured", aFirst: true, clip: false, textured: true);
        var body = assets.Declare("body", path);
        var guard = assets.Declare("guard", path, node: "guard");

        assets.LoadAll(textures: true);

        var texture = Assert.Single(assets[body].Textures);
        Assert.Same(texture, Assert.Single(assets[guard].Textures));
    }

    [Fact]
    public void A_part_named_by_no_node_or_by_two_cannot_be_read()
    {
        var path = WriteBody("body", aFirst: true, clip: false, twice: true);

        var none = Assert.Throws<InvalidDataException>(() =>
            GltfImport.Load("x", path, 1, TextureReading.None, _warnings, node: "nothing")
        );
        var two = Assert.Throws<InvalidDataException>(() =>
            GltfImport.Load("x", path, 1, TextureReading.None, _warnings, node: "guard")
        );

        Assert.Contains("no node named 'nothing'", none.Message, StringComparison.Ordinal);
        Assert.Contains("2 nodes named 'guard'", two.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_bound_on_anothers_rig_takes_its_joint_order_and_clips_and_its_file_is_listed_once()
    {
        var assets = new MeshAssets(NullLogger<MeshAssets>.Instance);
        assets.Declare("source", WriteBody("source", aFirst: true, clip: true));
        var path = WriteBody("bound", aFirst: false, clip: false);
        var bound = assets.Declare("bound", path, rigOf: "source");
        var guard = assets.Declare("guard", path, node: "guard");

        assets.LoadAll(textures: false);

        var source = assets[assets.Find("source")].Rig!;
        var rig = assets[bound].Rig!;
        Assert.Equal(source.Names, rig.Names);
        Assert.Same(source.Clips, rig.Clips);
        Assert.Equal("Wave", Assert.Single(rig.Clips).Name);
        // Its third vertex, weighted on b alone, follows b where the source's rig has it.
        Assert.Equal(source.FindJoint("b"), assets[bound].Skin![2].Joint(0));
        Assert.Null(assets[guard].Rig);
        Assert.Equal(2, assets.Files.Count);
        Assert.Equal(["source", "bound"], assets.Files.Select(file => file.Name));
        // In its own file a comes after b; bound, the source's order holds.
        var own = GltfImport.Load("own", path, 1, TextureReading.None, _warnings).Rig!;
        Assert.True(own.FindJoint("b") < own.FindJoint("a"));
        Assert.True(rig.FindJoint("a") < rig.FindJoint("b"));
    }

    [Fact]
    public void A_body_whose_joints_rest_elsewhere_or_with_clips_of_its_own_stops_the_step_naming_why()
    {
        var elsewhere = new MeshAssets(NullLogger<MeshAssets>.Instance);
        elsewhere.Declare("source", WriteBody("source", aFirst: true, clip: false));
        elsewhere.Declare("moved", WriteBody("moved", aFirst: false, clip: false, aAt: 0.3f), rigOf: "source");
        var clipped = new MeshAssets(NullLogger<MeshAssets>.Instance);
        clipped.Declare("source", WriteBody("source2", aFirst: true, clip: false));
        clipped.Declare("clipped", WriteBody("clipped", aFirst: true, clip: true), rigOf: "source");

        var moved = Assert.Throws<InvalidOperationException>(() => elsewhere.LoadAll(textures: false));
        var own = Assert.Throws<InvalidOperationException>(() => clipped.LoadAll(textures: false));

        Assert.Contains("joint 'a' rests otherwise", moved.Message, StringComparison.Ordinal);
        Assert.Contains("clip(s) of its own", own.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            new MeshAssets(NullLogger<MeshAssets>.Instance).Declare("x", "x.glb", rigOf: "nothing")
        );
    }

    /// <summary>
    /// A body in metres: the hip joint 1 up, its children a (<paramref name="aAt"/> along x) and b (0.2 along -x), the
    /// skin listing a before b or after it; a triangle skinned on the hip, a, and b in the material flesh; and under a,
    /// through a holder 0.1 up and turned a quarter about z, the guard, a triangle in the material iron not skinned (and a
    /// second guard under b when <paramref name="twice"/>). With <paramref name="clip"/>, a clip Wave turning a.
    /// </summary>
    private string WriteBody(
        string name,
        bool aFirst,
        bool clip,
        bool twice = false,
        float aAt = 0.2f,
        bool textured = false
    )
    {
        var gltf = new GltfBuilder();
        // With a texture, one image both materials use.
        var texture = textured ? gltf.Texture(gltf.Image(GltfBuilder.Png(4, 4, SkiaSharp.SKColors.Gray))) : -1;
        JsonObject Material(string named)
        {
            var material = new JsonObject { ["name"] = named };
            if (texture >= 0)
            {
                material["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorTexture"] = new JsonObject { ["index"] = texture },
                };
            }

            return material;
        }

        var flesh = gltf.Material(Material("flesh"));
        var iron = gltf.Material(Material("iron"));
        var hipWorld = Matrix4x4.CreateTranslation(0, 1, 0);
        var aWorld = Matrix4x4.CreateTranslation(aAt, 0, 0) * hipWorld;
        var bWorld = Matrix4x4.CreateTranslation(-0.2f, 0, 0) * hipWorld;
        var guard = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(new Vector3[] { new(0, 0, 0), new(0.1f, 0, 0), new(0, 0.1f, 0) }),
                material: iron
            )
        );
        // Nodes: 0 the hip, 1 a, 2 b, 3 the holder, 4 the guard, then the second guard, then the skinned body.
        gltf.Node(
            new JsonObject
            {
                ["name"] = "hip",
                ["translation"] = new JsonArray(0f, 1f, 0f),
                ["children"] = new JsonArray(1, 2),
            }
        );
        gltf.Node(
            new JsonObject
            {
                ["name"] = "a",
                ["translation"] = new JsonArray(aAt, 0f, 0f),
                ["children"] = new JsonArray(3),
            },
            root: false
        );
        gltf.Node(
            twice
                ? new JsonObject
                {
                    ["name"] = "b",
                    ["translation"] = new JsonArray(-0.2f, 0f, 0f),
                    ["children"] = new JsonArray(5),
                }
                : new JsonObject { ["name"] = "b", ["translation"] = new JsonArray(-0.2f, 0f, 0f) },
            root: false
        );
        gltf.Node(
            new JsonObject
            {
                ["name"] = "holder",
                ["translation"] = new JsonArray(0f, 0.1f, 0f),
                ["rotation"] = new JsonArray(s_quarterZ.X, s_quarterZ.Y, s_quarterZ.Z, s_quarterZ.W),
                ["children"] = new JsonArray(4),
            },
            root: false
        );
        gltf.Node(new JsonObject { ["name"] = "guard", ["mesh"] = guard }, root: false);
        if (twice)
        {
            gltf.Node(new JsonObject { ["name"] = "guard", ["mesh"] = guard }, root: false);
        }

        var body = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(new Vector3[] { new(0, 1, 0), new(aAt, 1, 0), new(-0.2f, 1, 0) }),
                joints: aFirst
                    ? gltf.Joints(0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0)
                    : gltf.Joints(0, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0),
                weights: gltf.Accessor(new Vector4[] { new(1, 0, 0, 0), new(1, 0, 0, 0), new(1, 0, 0, 0) }),
                material: flesh
            )
        );
        var order = aFirst ? new[] { hipWorld, aWorld, bWorld } : new[] { hipWorld, bWorld, aWorld };
        var inverses = order.Select(world =>
            Matrix4x4.Invert(world, out var inverse) ? inverse : throw new InvalidOperationException()
        );
        var skin = gltf.Skin(gltf.Accessor([.. inverses]), aFirst ? [0, 1, 2] : [0, 2, 1]);
        gltf.Node(
            new JsonObject
            {
                ["name"] = "body",
                ["mesh"] = body,
                ["skin"] = skin,
            }
        );
        if (clip)
        {
            var times = gltf.Accessor([0f, 1f]);
            var turns = gltf.Accessor([Quaternion.Identity, s_quarterZ]);
            gltf.Animation("Wave", (1, "rotation", times, turns, "LINEAR"));
        }

        return gltf.WriteGltf(_folder, name);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        if (Vector3.Distance(expected, actual) > 1e-4f)
        {
            Assert.Fail($"Expected {expected}, got {actual}");
        }
    }
}
