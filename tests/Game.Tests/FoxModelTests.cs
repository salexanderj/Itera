using System.Numerics;
using Itera.Engine.Assets;
using Itera.Engine.Matches;
using Itera.Engine.Rendering.Materials;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;

namespace Game.Tests;

/// <summary>
/// The fox as the run reads it (<c>Models/Fox/Fox.gltf</c>, in centimetres, under a root node that is not a joint): 24
/// joints and its three clips; its rest pose skins every vertex to where the file stores it; a vertex in a clip lands
/// where the file's own world matrices put it (SharpGLTF's, an independent reading), to a millimetre; its compressed
/// textures (<c>Fox.textures</c>) were made from its image.
/// </summary>
public sealed class FoxModelTests
{
    private static readonly string s_path = Path.Combine(AppContext.BaseDirectory, "Models", "Fox", "Fox.gltf");

    [Fact]
    public void The_fox_has_its_joints_and_clips()
    {
        var fox = Read();

        var rig = Assert.IsType<Rig>(fox.Rig);
        Assert.Equal(24, rig.JointCount);
        Assert.Equal(["Survey", "Walk", "Run"], rig.Clips.Select(clip => clip.Name));
        Assert.Equal(0.708f, rig.Clips[1].Duration, 1e-3f);
    }

    [Fact]
    public void Its_rest_pose_skins_every_vertex_to_where_the_file_stores_it()
    {
        var fox = Read();

        var skinned = Skin(fox, clip: -1, seconds: 0);

        for (var v = 0; v < skinned.Length; v++)
        {
            Assert.True(
                Vector3.Distance(fox.Vertices[v].Position, skinned[v]) < 1e-3f,
                $"vertex {v}: {fox.Vertices[v].Position} at rest, {skinned[v]} skinned"
            );
        }
    }

    [Fact]
    public void Its_compressed_textures_were_made_from_its_image()
    {
        var list = CompressedTextures.ReadList(CompressedTextures.FolderOf(s_path));
        var textures = CompressedTextures.DecodeAll(s_path, []);

        // A changed Texture.png needs Fox.textures made again: tools/Textures' model mode on src/Game/Models/Fox/Fox.gltf.
        Assert.NotNull(list);
        Assert.Equal(
            textures.Select(texture => (texture.Image, texture.Role, texture.SourceHash)),
            list.Textures.Select(entry => (entry.Image, entry.Role, entry.Sha256))
        );
        var file = Path.Combine(CompressedTextures.FolderOf(s_path), list.Textures[0].File);
        var dds = DdsFile.Read(File.ReadAllBytes(file));
        Assert.Equal((DdsFormat.Bc7Srgb, 1024, 1024), (dds.Format, dds.Width, dds.Height));
    }

    [Theory]
    [InlineData("Walk", 0.3f)]
    [InlineData("Run", 0.9f)]
    [InlineData("Survey", 2.2f)]
    public void A_vertex_in_a_clip_lands_where_the_files_world_matrices_put_it(string name, float seconds)
    {
        var fox = Read();
        var model = ModelRoot.Load(s_path);
        var animation = model.LogicalAnimations.Single(a => a.Name == name);
        var skin = model.LogicalSkins[0];
        var primitive = model.LogicalMeshes[0].Primitives[0];
        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        var joints = primitive.GetVertexAccessor("JOINTS_0").AsVector4Array();
        var weights = primitive.GetVertexAccessor("WEIGHTS_0").AsVector4Array();
        var worlds = new Matrix4x4[skin.JointsCount];
        for (var j = 0; j < worlds.Length; j++)
        {
            var (node, inverseBind) = skin.GetJoint(j);
            worlds[j] = inverseBind * node.GetWorldMatrix(animation, seconds);
        }

        var skinned = Skin(fox, fox.Rig!.FindClip(name), seconds);

        // The file has no normals or indices: each of its vertices is the asset's vertex of the same number.
        for (var v = 0; v < positions.Count; v += 7)
        {
            var w = weights[v];
            var j = joints[v];
            var expected =
                (
                    w.X * Vector3.Transform(positions[v], worlds[(int)j.X])
                    + w.Y * Vector3.Transform(positions[v], worlds[(int)j.Y])
                    + w.Z * Vector3.Transform(positions[v], worlds[(int)j.Z])
                    + w.W * Vector3.Transform(positions[v], worlds[(int)j.W])
                )
                / (w.X + w.Y + w.Z + w.W)
                * 0.01f;
            Assert.True(
                Vector3.Distance(expected, skinned[v]) < 1e-3f,
                $"vertex {v}: expected {expected}, skinned {skinned[v]}"
            );
        }
    }

    /// <summary>The fox as the run reads it, without textures.</summary>
    private static MeshAsset Read()
    {
        using var provider = GameComposition.Build(new Dictionary<string, string?>());
        _ = new HeadlessRun(provider);
        var assets = provider.GetRequiredService<MeshAssets>();
        return assets[assets.Find(GameModels.Fox)];
    }

    /// <summary>The asset's vertices skinned on the CPU as <c>mesh-skin.comp</c> skins them, in clip <paramref name="clip"/>.</summary>
    private static Vector3[] Skin(MeshAsset asset, int clip, float seconds)
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
            var sum = 0f;
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = skin.Weight(slot);
                positions[v] += weight * Vector3.Transform(asset.Vertices[v].Position, palette[skin.Joint(slot)]);
                sum += weight;
            }

            positions[v] = sum > 0 ? positions[v] / sum : asset.Vertices[v].Position;
        }

        return positions;
    }
}
