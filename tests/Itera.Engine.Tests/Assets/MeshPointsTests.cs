using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using SharpGLTF.Schema2;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="GltfImport"/>'s points (<see cref="MeshPoint"/>) on files in centimetres written in the test: a named empty
/// at the root is a point in the asset's space, in metres; one under a joint, through a node that is not one, is in the
/// joint's space, and posed at rest it lands where the file's own world matrix puts the empty; nodes that hold something,
/// group others, or have no name are not points.
/// </summary>
public sealed class MeshPointsTests : IDisposable
{
    private const float Unit = 0.01f;

    private static readonly Quaternion s_quarterY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
    private static readonly Quaternion s_quarterZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

    private readonly string _folder = Directory.CreateTempSubdirectory("itera-points-").FullName;
    private readonly List<string> _warnings = [];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_named_empty_at_the_root_is_a_point_in_the_assets_space_in_metres()
    {
        var gltf = new GltfBuilder();
        AddTriangle(gltf);
        gltf.Node(
            new JsonObject
            {
                ["name"] = "top",
                ["translation"] = new JsonArray(100f, 200f, 300f),
                ["rotation"] = Rotation(s_quarterY),
            }
        );

        var asset = GltfImport.Load("box", gltf.WriteGltf(_folder, "box"), Unit, TextureReading.None, _warnings);

        var point = Assert.Single(asset.Points);
        Assert.Equal(("top", -1), (point.Name, point.Joint));
        AssertNear(new Vector3(1, 2, 3), point.Position);
        AssertNear(Vector3.Transform(Vector3.UnitX, s_quarterY), Vector3.Transform(Vector3.UnitX, point.Rotation));
        Assert.Equal(0, asset.FindPoint("top"));
        Assert.Equal(-1, asset.FindPoint("bottom"));
        Assert.Empty(_warnings);
    }

    [Fact]
    public void A_named_empty_under_a_joint_is_in_its_space_and_lands_where_the_file_puts_it()
    {
        var path = WriteArm();

        var asset = GltfImport.Load("arm", path, Unit, TextureReading.None, _warnings);

        // The leaf joint, the joints, and the holder hold something or group others: only the muzzle is a point.
        var rig = Assert.IsType<Rig>(asset.Rig);
        var point = Assert.Single(asset.Points);
        Assert.Equal(("muzzle", rig.FindJoint("hand")), (point.Name, point.Joint));
        Assert.Empty(_warnings);

        var locals = new JointTransform[rig.JointCount];
        var globals = new Matrix4x4[rig.JointCount];
        rig.Pose(-1, 0, locals);
        rig.Globals(locals, globals);
        var placed =
            Matrix4x4.CreateFromQuaternion(point.Rotation)
            * Matrix4x4.CreateTranslation(point.Position)
            * globals[point.Joint];

        var model = ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix });
        var expected = model.LogicalNodes.Single(node => node.Name == "muzzle").WorldMatrix;
        AssertNear(expected.Translation * Unit, placed.Translation);
        AssertNear(Vector3.TransformNormal(Vector3.UnitX, expected), Vector3.TransformNormal(Vector3.UnitX, placed));
    }

    [Fact]
    public void Nodes_that_group_others_or_have_no_name_are_not_points()
    {
        var gltf = new GltfBuilder();
        var mesh = AddTriangle(gltf, root: false);
        gltf.Node(new JsonObject { ["name"] = "group", ["children"] = new JsonArray(mesh) });
        gltf.Node(new JsonObject { ["translation"] = new JsonArray(1f, 0f, 0f) });

        var asset = GltfImport.Load("grouped", gltf.WriteGltf(_folder, "grouped"), 1f, TextureReading.None, _warnings);

        Assert.Empty(asset.Points);
    }

    /// <summary>A triangle on a node of its own; the node's index.</summary>
    private static int AddTriangle(GltfBuilder gltf, bool root = true)
    {
        Vector3[] positions = [new(0, 0, 0), new(100, 0, 0), new(0, 100, 0)];
        return gltf.Node(
            new JsonObject { ["mesh"] = gltf.Mesh(GltfBuilder.Primitive(gltf.Accessor(positions))) },
            root
        );
    }

    /// <summary>
    /// An arm in centimetres: the hip joint 100 up, the hand joint 50 above it, a leaf finger joint under the hand, and
    /// under the hand a holder (not a joint) 10 along x and turned a quarter about z, with the muzzle empty 20 along its
    /// y and turned a quarter about y. A skinned triangle on the hip and the hand.
    /// </summary>
    private string WriteArm()
    {
        var gltf = new GltfBuilder();
        var hipWorld = Matrix4x4.CreateTranslation(0, 100, 0);
        var handWorld = Matrix4x4.CreateTranslation(0, 50, 0) * hipWorld;
        var fingerWorld = Matrix4x4.CreateTranslation(0, 10, 0) * handWorld;
        Vector3[] positions = [new(0, 100, 0), new(10, 150, 0), new(0, 150, 0)];
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(positions),
                joints: gltf.Joints(0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0),
                weights: gltf.Accessor(new Vector4[] { new(1, 0, 0, 0), new(1, 0, 0, 0), new(1, 0, 0, 0) })
            )
        );
        // Nodes: 0 the hip, 1 the hand, 2 the finger, 3 the holder, 4 the muzzle, 5 the skinned mesh.
        gltf.Node(
            new JsonObject
            {
                ["name"] = "hip",
                ["translation"] = new JsonArray(0f, 100f, 0f),
                ["children"] = new JsonArray(1),
            }
        );
        gltf.Node(
            new JsonObject
            {
                ["name"] = "hand",
                ["translation"] = new JsonArray(0f, 50f, 0f),
                ["children"] = new JsonArray(2, 3),
            },
            root: false
        );
        gltf.Node(new JsonObject { ["name"] = "finger", ["translation"] = new JsonArray(0f, 10f, 0f) }, root: false);
        gltf.Node(
            new JsonObject
            {
                ["name"] = "holder",
                ["translation"] = new JsonArray(10f, 0f, 0f),
                ["rotation"] = Rotation(s_quarterZ),
                ["children"] = new JsonArray(4),
            },
            root: false
        );
        gltf.Node(
            new JsonObject
            {
                ["name"] = "muzzle",
                ["translation"] = new JsonArray(0f, 20f, 0f),
                ["rotation"] = Rotation(s_quarterY),
            },
            root: false
        );
        var inverses = new[] { hipWorld, handWorld, fingerWorld }.Select(world =>
            Matrix4x4.Invert(world, out var inverse) ? inverse : throw new InvalidOperationException()
        );
        var skin = gltf.Skin(gltf.Accessor([.. inverses]), 0, 1, 2);
        gltf.Node(
            new JsonObject
            {
                ["name"] = "arm",
                ["mesh"] = mesh,
                ["skin"] = skin,
            }
        );
        return gltf.WriteGltf(_folder, "arm");
    }

    private static JsonArray Rotation(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        if (Vector3.Distance(expected, actual) > 1e-4f)
        {
            Assert.Fail($"Expected {expected}, got {actual}");
        }
    }
}
