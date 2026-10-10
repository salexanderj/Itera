using System.Numerics;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Assets;

/// <summary>
/// A mesh the engine draws as triangles with texture coordinates (not voxels): a model read from a glTF file
/// (<see cref="GltfImport"/>), or one a game builds in code. Its vertices are in the asset's space, in metres (the file's
/// node transforms and unit scale applied), ready to upload; its indices are into its own vertices, three a triangle,
/// counter-clockwise seen from the front; each primitive is a run of them drawn with one material. Read at the run's
/// "mesh assets" step and kept for the run (<see cref="MeshAssets"/>). A skinned asset also has a <see cref="Rig"/> and
/// each vertex's joints and weights (<see cref="Skin"/>); its vertices are where the mesh was bound to the rig.
/// </summary>
public sealed class MeshAsset
{
    public MeshAsset(
        string name,
        MeshAssetVertex[] vertices,
        uint[] indices,
        MeshPrimitive[] primitives,
        MeshMaterialData[] materials,
        MeshTextureData[] textures,
        Rig? rig = null,
        MeshSkinVertex[]? skin = null
    )
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(textures);
        if ((rig is null) != (skin is null) || (skin is not null && skin.Length != vertices.Length))
        {
            throw new ArgumentException(
                "A skinned asset needs a rig and one skin entry a vertex; a static one neither.",
                nameof(skin)
            );
        }

        Name = name;
        Rig = rig;
        Skin = skin;
        Vertices = vertices;
        Indices = indices;
        Primitives = primitives;
        Materials = materials;
        Textures = textures;
        BoundsMin = vertices.Length == 0 ? Vector3.Zero : new Vector3(float.MaxValue);
        BoundsMax = vertices.Length == 0 ? Vector3.Zero : new Vector3(float.MinValue);
        foreach (ref readonly var vertex in vertices.AsSpan())
        {
            BoundsMin = Vector3.Min(BoundsMin, vertex.Position);
            BoundsMax = Vector3.Max(BoundsMax, vertex.Position);
        }
    }

    /// <summary>The name the game knows the asset by (<see cref="MeshAssets.Find"/>).</summary>
    public string Name { get; }

    public MeshAssetVertex[] Vertices { get; }

    public uint[] Indices { get; }

    public MeshPrimitive[] Primitives { get; }

    public MeshMaterialData[] Materials { get; }

    /// <summary>The textures the materials name (<see cref="MeshTextureRef.Texture"/>), each with every mip level.</summary>
    public MeshTextureData[] Textures { get; }

    /// <summary>The joints and clips that pose the asset, or null for a static one.</summary>
    public Rig? Rig { get; }

    /// <summary>Each vertex's joints and weights in <see cref="Rig"/> (one a vertex), or null for a static asset.</summary>
    public MeshSkinVertex[]? Skin { get; }

    /// <summary>The corner of the vertices' box with the least x, y, and z, in the asset's space.</summary>
    public Vector3 BoundsMin { get; }

    /// <summary>The corner of the vertices' box with the greatest x, y, and z, in the asset's space.</summary>
    public Vector3 BoundsMax { get; }

    public int TriangleCount => Indices.Length / 3;

    /// <summary>The bytes the asset's textures take, every mip level of each.</summary>
    public long TextureBytes
    {
        get
        {
            long bytes = 0;
            foreach (var texture in Textures)
            {
                bytes += texture.Texels.Length;
            }

            return bytes;
        }
    }
}

/// <summary>A run of a mesh asset's indices drawn with one of its materials: <see cref="IndexCount"/> from <see cref="FirstIndex"/>.</summary>
public readonly record struct MeshPrimitive(int FirstIndex, int IndexCount, int Material);
