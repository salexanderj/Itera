using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// Where every mesh asset of the run lies in the mesh pass's asset buffers (<see cref="MeshAssetPass"/>): one vertex
/// buffer, one index buffer, one material table, and one texture array for all, each asset's after the one before;
/// texture 0 is a white stand-in, so an asset's texture i is <see cref="AssetPlace.FirstTexture"/> + i. Its primitives are
/// listed sorted by the pipeline that draws them (<see cref="MeshAssetRange"/>), so a list's commands, grouped by
/// primitive in that order, come out grouped by pipeline. A skinned asset's joints and weights lie in one skin buffer
/// (<see cref="AssetPlace.FirstSkin"/>), and its joints' places in the bound pose and their reaches in
/// <see cref="JointPositions"/> and <see cref="JointReaches"/> (from <see cref="AssetPlace.FirstJoint"/>), which bound a
/// posed instance. Built once, after the run has read its assets.
/// </summary>
public sealed class MeshAssetLayout
{
    /// <summary>The pipelines' ranges a list's commands are grouped in.</summary>
    public const int Ranges = 4;

    private MeshAssetLayout(
        AssetPlace[] assets,
        PrimitivePlace[] primitives,
        int[] assetPrimitives,
        int[] rangeStarts,
        int vertexCount,
        int indexCount,
        int materialCount,
        int textureCount,
        int skinVertexCount,
        Vector3[] jointPositions,
        float[] jointReaches
    )
    {
        SkinVertexCount = skinVertexCount;
        JointPositions = jointPositions;
        JointReaches = jointReaches;
        Assets = assets;
        Primitives = primitives;
        AssetPrimitives = assetPrimitives;
        RangeStarts = rangeStarts;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        MaterialCount = materialCount;
        TextureCount = textureCount;
    }

    /// <summary>Each asset's place, by its index in <see cref="MeshAssets"/>.</summary>
    public AssetPlace[] Assets { get; }

    /// <summary>Every primitive of every asset, sorted by range (<see cref="RangeStarts"/>).</summary>
    public PrimitivePlace[] Primitives { get; }

    /// <summary>For each asset, its primitives' places in <see cref="Primitives"/> (from <see cref="AssetPlace.FirstPrimitive"/>).</summary>
    public int[] AssetPrimitives { get; }

    /// <summary>The first primitive of each range in <see cref="Primitives"/>, and then their count.</summary>
    public int[] RangeStarts { get; }

    public int VertexCount { get; }

    public int IndexCount { get; }

    public int MaterialCount { get; }

    /// <summary>Textures of every asset, and the stand-in at 0.</summary>
    public int TextureCount { get; }

    /// <summary>The skinned assets' vertices, each with a skin entry (<c>MeshSkinVertex</c>).</summary>
    public int SkinVertexCount { get; }

    /// <summary>Each skinned asset's joints' places in the bound pose, in its space (<c>Rig.BindPositions</c>).</summary>
    public Vector3[] JointPositions { get; }

    /// <summary>How far from its joint each joint's vertices reach (<c>Rig.Reaches</c>), beside <see cref="JointPositions"/>.</summary>
    public float[] JointReaches { get; }

    /// <summary>The layout of the run's assets, every one read (<see cref="MeshAssets.Loaded"/>).</summary>
    public static MeshAssetLayout From(MeshAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var list = new MeshAsset[assets.Count];
        for (var i = 0; i < list.Length; i++)
        {
            list[i] = assets[i];
        }

        return From(list);
    }

    /// <summary>The layout of <paramref name="assets"/>, in their order.</summary>
    public static MeshAssetLayout From(IReadOnlyList<MeshAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var places = new AssetPlace[assets.Count];
        var unsorted = new List<(PrimitivePlace Place, int Asset, int Local)>();
        var jointPositions = new List<Vector3>();
        var jointReaches = new List<float>();
        int vertices = 0,
            indices = 0,
            materials = 0,
            textures = 1,
            skins = 0;
        for (var a = 0; a < assets.Count; a++)
        {
            var asset = assets[a];
            var centre = (asset.BoundsMin + asset.BoundsMax) / 2;
            var radius = (asset.BoundsMax - asset.BoundsMin).Length() / 2;
            var (staticCentre, staticRadius) = UnweightedSphere(asset);
            places[a] = new AssetPlace(
                vertices,
                indices,
                0,
                asset.Primitives.Length,
                materials,
                textures,
                centre,
                radius,
                asset.Rig is null ? -1 : skins,
                jointPositions.Count,
                asset.Rig?.JointCount ?? 0,
                staticCentre,
                staticRadius
            )
            {
                VertexCount = asset.Vertices.Length,
            };
            if (asset.Rig is { } rig)
            {
                jointPositions.AddRange(rig.BindPositions);
                jointReaches.AddRange(rig.Reaches);
                skins += asset.Vertices.Length;
            }

            for (var p = 0; p < asset.Primitives.Length; p++)
            {
                var primitive = asset.Primitives[p];
                var material = asset.Materials[primitive.Material];
                unsorted.Add(
                    (
                        new PrimitivePlace(
                            indices + primitive.FirstIndex,
                            primitive.IndexCount,
                            vertices,
                            materials + primitive.Material,
                            RangeOf(material)
                        ),
                        a,
                        p
                    )
                );
            }

            vertices += asset.Vertices.Length;
            indices += asset.Indices.Length;
            materials += asset.Materials.Length;
            textures += asset.Textures.Length;
        }

        // A stable sort by range keeps each asset's primitives in their order within a range.
        var sorted = unsorted
            .Select((entry, order) => (entry, order))
            .OrderBy(e => e.entry.Place.Range)
            .ThenBy(e => e.order)
            .ToArray();
        var primitives = new PrimitivePlace[sorted.Length];
        var assetPrimitives = new int[sorted.Length];
        var firsts = new int[assets.Count];
        for (var a = 0; a < assets.Count; a++)
        {
            firsts[a] = a == 0 ? 0 : firsts[a - 1] + assets[a - 1].Primitives.Length;
        }

        var rangeStarts = new int[Ranges + 1];
        for (var i = 0; i < sorted.Length; i++)
        {
            var (place, asset, local) = sorted[i].entry;
            primitives[i] = place;
            assetPrimitives[firsts[asset] + local] = i;
            rangeStarts[(int)place.Range + 1]++;
        }

        for (var r = 1; r <= Ranges; r++)
        {
            rangeStarts[r] += rangeStarts[r - 1];
        }

        for (var a = 0; a < places.Length; a++)
        {
            places[a] = places[a] with { FirstPrimitive = firsts[a] };
        }

        return new MeshAssetLayout(
            places,
            primitives,
            assetPrimitives,
            rangeStarts,
            vertices,
            indices,
            materials,
            textures,
            skins,
            [.. jointPositions],
            [.. jointReaches]
        );
    }

    /// <summary>
    /// The bounding sphere of a skinned asset's vertices that no joint moves (their weights all zero), which stand where
    /// the asset stores them; a radius of -1 when there are none, or for a static asset.
    /// </summary>
    private static (Vector3 Centre, float Radius) UnweightedSphere(MeshAsset asset)
    {
        if (asset.Skin is not { } skin)
        {
            return (Vector3.Zero, -1);
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var v = 0; v < skin.Length; v++)
        {
            if (skin[v].Weights01 == 0 && skin[v].Weights23 == 0)
            {
                min = Vector3.Min(min, asset.Vertices[v].Position);
                max = Vector3.Max(max, asset.Vertices[v].Position);
            }
        }

        return min.X > max.X ? (Vector3.Zero, -1) : ((min + max) / 2, (max - min).Length() / 2);
    }

    /// <summary>The pipeline range a material's primitives draw in.</summary>
    public static MeshAssetRange RangeOf(MeshMaterialData material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var masked = material.AlphaMode != MeshAlphaMode.Opaque;
        return (masked, material.DoubleSided) switch
        {
            (false, false) => MeshAssetRange.Opaque,
            (false, true) => MeshAssetRange.OpaqueTwoSided,
            (true, false) => MeshAssetRange.Masked,
            _ => MeshAssetRange.MaskedTwoSided,
        };
    }

    /// <summary>
    /// An asset's place: its first vertex, index, material, and texture in the pass's buffers, its primitives' first entry
    /// in <see cref="AssetPrimitives"/> and their count, and its bounding sphere in its own space; for a skinned asset, its
    /// first skin entry (-1 for a static asset), its first joint in <see cref="JointPositions"/> and its joint count, and
    /// the sphere of its vertices no joint moves (a radius of -1 for none).
    /// </summary>
    public readonly record struct AssetPlace(
        int FirstVertex,
        int FirstIndex,
        int FirstPrimitive,
        int PrimitiveCount,
        int FirstMaterial,
        int FirstTexture,
        Vector3 Centre,
        float Radius,
        int FirstSkin = -1,
        int FirstJoint = 0,
        int Joints = 0,
        Vector3 StaticCentre = default,
        float StaticRadius = -1
    )
    {
        /// <summary>The asset's vertices (from its first to the next asset's).</summary>
        public int VertexCount { get; init; }
    }

    /// <summary>
    /// A primitive's place: its indices in the pass's index buffer, the vertex its indices count from, its material
    /// among every asset's, and the range it draws in.
    /// </summary>
    public readonly record struct PrimitivePlace(
        int FirstIndex,
        int IndexCount,
        int FirstVertex,
        int Material,
        MeshAssetRange Range
    );
}

/// <summary>The pipeline a mesh asset's primitive draws with: its material's alpha mode and sides.</summary>
public enum MeshAssetRange
{
    Opaque,
    OpaqueTwoSided,
    Masked,
    MaskedTwoSided,
}
