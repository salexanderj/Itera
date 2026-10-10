using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using Itera.Engine.Rendering.GpuData;
using SharpGLTF.Schema2;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="GltfImport"/>'s skins and clips on a file written in the test: a two-joint leg in centimetres under two
/// turned and moved nodes that are not joints, its skin listing the child joint first, its mesh node moved (which glTF
/// ignores for a skinned mesh). The rig comes out parents first, in metres; the rest pose skins every vertex to where it
/// was bound; a posed vertex lands where the file's own world matrices put it (SharpGLTF's, an independent reading).
/// </summary>
public sealed class RigImportTests : IDisposable
{
    private const float Unit = 0.01f;

    private static readonly Quaternion s_rootTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
    private static readonly Quaternion s_holderTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6);
    private static readonly Quaternion s_kneeBend = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

    // In centimetres, in the mesh's space (where the leg was bound): one at the hip, one above the knee, one between.
    private static readonly Vector3[] s_vertices = [new(50, 110, 0), new(50, 250, 0), new(50, 150, 0)];

    private readonly string _folder = Directory.CreateTempSubdirectory("itera-rig-").FullName;
    private readonly List<string> _warnings = [];
    private readonly string _path;

    public RigImportTests() => _path = WriteLeg();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_skin_becomes_a_rig_parents_first_in_metres()
    {
        var asset = GltfImport.Load("leg", _path, Unit, textures: false, _warnings);

        var rig = Assert.IsType<Rig>(asset.Rig);
        Assert.Equal(["hip", "knee"], rig.Names);
        Assert.Equal([-1, 0], rig.Parents);
        Assert.Equal(new Vector3(0, 1, 0), rig.Rest[0].Translation);
        Assert.Equal(new Vector3(0, 1, 0), rig.Rest[1].Translation);
        // The holder and the root are not joints: theirs stand between the scene and the hip, the nearer first, the move
        // in metres: the holder's 10 cm up, then the root's turn about y and 50 cm along x.
        var expectedOffset =
            Matrix4x4.CreateFromQuaternion(s_holderTurn)
            * Matrix4x4.CreateTranslation(0, 0.1f, 0)
            * Matrix4x4.CreateFromQuaternion(s_rootTurn)
            * Matrix4x4.CreateTranslation(0.5f, 0, 0);
        AssertNear(expectedOffset.Translation, rig.Offsets[0].Translation);
        AssertNear(Vector3.Transform(Vector3.UnitY, expectedOffset), Vector3.Transform(Vector3.UnitY, rig.Offsets[0]));
        Assert.Equal(Matrix4x4.Identity, rig.Offsets[1]);
        var clip = Assert.Single(rig.Clips);
        Assert.Equal(("bend", 1f), (clip.Name, clip.Duration));
        Assert.Equal(0, rig.FindClip("bend"));
        Assert.Equal(-1, rig.FindClip("walk"));
        Assert.Empty(_warnings);

        // Flat normals give each triangle its own three vertices; their skins follow them, joints in the rig's order,
        // the third vertex's 0.2 and 0.2 scaled to a half each.
        var skin = Assert.IsType<MeshSkinVertex[]>(asset.Skin);
        Assert.Equal(3, skin.Length);
        Assert.Equal((0, 1f), (skin[0].Joint(0), skin[0].Weight(0)));
        Assert.Equal((1, 1f), (skin[1].Joint(0), skin[1].Weight(0)));
        Assert.Equal(0.5f, skin[2].Weight(0), 1e-4f);
        Assert.Equal(0.5f, skin[2].Weight(1), 1e-4f);
        Assert.Equal(0, skin[2].Weight(2));

        // The knee's reach: the farther of the two vertices it moves (the second and the mixed third) from its bound place.
        var knee = rig.BindPositions[1];
        var reach = Math.Max(
            Vector3.Distance(s_vertices[1] * Unit, knee),
            Vector3.Distance(s_vertices[2] * Unit, knee)
        );
        Assert.Equal(reach, rig.Reaches[1], 1e-4f);
    }

    [Fact]
    public void The_rest_pose_skins_every_vertex_to_where_it_was_bound()
    {
        var asset = GltfImport.Load("leg", _path, Unit, textures: false, _warnings);

        var skinned = Skinned(asset, clip: -1, seconds: 0);

        for (var v = 0; v < s_vertices.Length; v++)
        {
            AssertNear(s_vertices[v] * Unit, skinned[v]);
        }
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.6f)]
    [InlineData(1f)]
    public void A_posed_vertex_lands_where_the_files_own_world_matrices_put_it(float seconds)
    {
        var asset = GltfImport.Load("leg", _path, Unit, textures: false, _warnings);
        var model = ModelRoot.Load(_path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix });
        var animation = model.LogicalAnimations[0];
        var skin = model.LogicalSkins[0];

        var skinned = Skinned(asset, clip: 0, seconds);

        for (var v = 0; v < s_vertices.Length; v++)
        {
            // The file's own: each weighted joint's inverse bind matrix, then its world matrix at that time.
            var expected = Vector3.Zero;
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = asset.Skin![v].Weight(slot);
                if (weight > 0)
                {
                    var joint = SkinJointOf(asset.Rig!.Names[asset.Skin[v].Joint(slot)], skin);
                    var (node, inverseBind) = skin.GetJoint(joint);
                    expected +=
                        weight
                        * Vector3.Transform(s_vertices[v], inverseBind * node.GetWorldMatrix(animation, seconds));
                }
            }

            AssertNear(expected * Unit, skinned[v]);
        }
    }

    /// <summary>The asset's vertices skinned on the CPU as <c>mesh-skin.comp</c> does, in clip <paramref name="clip"/>.</summary>
    internal static Vector3[] Skinned(MeshAsset asset, int clip, float seconds)
    {
        var rig = asset.Rig!;
        var locals = new JointTransform[rig.JointCount];
        var palette = new Matrix4x4[rig.JointCount];
        rig.Pose(clip, seconds, locals);
        rig.WritePalette(locals, palette);
        var positions = new Vector3[asset.Vertices.Length];
        for (var v = 0; v < positions.Length; v++)
        {
            var skin = asset.Skin![v];
            var weighed = 0f;
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = skin.Weight(slot);
                positions[v] += weight * Vector3.Transform(asset.Vertices[v].Position, palette[skin.Joint(slot)]);
                weighed += weight;
            }

            positions[v] = weighed > 0 ? positions[v] / weighed : asset.Vertices[v].Position;
        }

        return positions;
    }

    private static int SkinJointOf(string name, Skin skin)
    {
        for (var i = 0; i < skin.JointsCount; i++)
        {
            if (skin.GetJoint(i).Joint.Name == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException(name);
    }

    /// <summary>The inverse of an affine matrix with its last column exactly (0, 0, 0, 1), as glTF's validation asks.</summary>
    private static Matrix4x4 AffineInverse(Matrix4x4 matrix)
    {
        Matrix4x4.Invert(matrix, out var inverse);
        inverse.M14 = 0;
        inverse.M24 = 0;
        inverse.M34 = 0;
        inverse.M44 = 1;
        return inverse;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

    /// <summary>
    /// The leg: a root node (not a joint) turned a quarter about y and moved 50 cm along x; under it a holder (not a joint)
    /// turned a twelfth about x and moved 10 cm up; under that the hip 100 cm up, and the knee 100 cm above the hip; a skin listing the knee first; a mesh node moved 10 m (ignored); a clip that
    /// bends the knee a quarter about z over a second (linear) and lifts the hip 20 cm at half a second (step).
    /// </summary>
    private string WriteLeg()
    {
        var gltf = new GltfBuilder();
        var root = Matrix4x4.CreateFromQuaternion(s_rootTurn) * Matrix4x4.CreateTranslation(50, 0, 0);
        var holder = Matrix4x4.CreateFromQuaternion(s_holderTurn) * Matrix4x4.CreateTranslation(0, 10, 0);
        var hipWorld = Matrix4x4.CreateTranslation(0, 100, 0) * holder * root;
        var kneeWorld = Matrix4x4.CreateTranslation(0, 100, 0) * hipWorld;
        var hipInverse = AffineInverse(hipWorld);
        var kneeInverse = AffineInverse(kneeWorld);

        // The mesh's vertices are where the joints put them when bound: the test's points are in that space already.
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(s_vertices),
                joints: gltf.Joints(1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),
                weights: gltf.Accessor(new Vector4[] { new(1, 0, 0, 0), new(1, 0, 0, 0), new(0.2f, 0.2f, 0, 0) })
            )
        );
        // Nodes: 0 the root, 1 the holder, 2 the hip, 3 the knee, 4 the mesh.
        gltf.Node(
            new JsonObject
            {
                ["name"] = "root",
                ["rotation"] = new JsonArray(s_rootTurn.X, s_rootTurn.Y, s_rootTurn.Z, s_rootTurn.W),
                ["translation"] = new JsonArray(50f, 0f, 0f),
                ["children"] = new JsonArray(1),
            }
        );
        gltf.Node(
            new JsonObject
            {
                ["name"] = "holder",
                ["rotation"] = new JsonArray(s_holderTurn.X, s_holderTurn.Y, s_holderTurn.Z, s_holderTurn.W),
                ["translation"] = new JsonArray(0f, 10f, 0f),
                ["children"] = new JsonArray(2),
            },
            root: false
        );
        gltf.Node(
            new JsonObject
            {
                ["name"] = "hip",
                ["translation"] = new JsonArray(0f, 100f, 0f),
                ["children"] = new JsonArray(3),
            },
            root: false
        );
        gltf.Node(new JsonObject { ["name"] = "knee", ["translation"] = new JsonArray(0f, 100f, 0f) }, root: false);
        var skin = gltf.Skin(gltf.Accessor(new[] { kneeInverse, hipInverse }), 3, 2);
        gltf.Node(
            new JsonObject
            {
                ["name"] = "leg",
                ["mesh"] = mesh,
                ["skin"] = skin,
                ["translation"] = new JsonArray(1000f, 0f, 0f),
            }
        );
        gltf.Animation(
            "bend",
            (
                3,
                "rotation",
                gltf.Accessor(new float[] { 0, 1 }),
                gltf.Accessor(new[] { Quaternion.Identity, s_kneeBend }),
                "LINEAR"
            ),
            (
                2,
                "translation",
                gltf.Accessor(new float[] { 0, 0.5f }),
                gltf.Accessor(new Vector3[] { new(0, 100, 0), new(0, 120, 0) }),
                "STEP"
            )
        );
        return gltf.WriteGltf(_folder, "leg");
    }
}
