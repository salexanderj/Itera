using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// Bounding spheres of mesh assets in their own space: a posed instance's (<see cref="Posed"/>, which
/// <see cref="MeshAssetDraws"/> culls by), and one round every pose a rig's clips give (<see cref="EveryPose"/>, which
/// <c>MeshDrawSystem</c> culls by before it poses). Main thread; <see cref="Posed"/> allocates nothing.
/// </summary>
internal static class MeshBounds
{
    /// <summary>Times each clip is posed at by <see cref="EveryPose"/>, its start and end among them.</summary>
    internal const int SamplesPerClip = 9;

    /// <summary>The largest length an axis of the asset's space takes in the world's: the scale its bounding sphere grows by.</summary>
    public static float LargestScale(in Matrix4x4 m)
    {
        var x = m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13;
        var y = m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23;
        var z = m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33;
        return MathF.Sqrt(Math.Max(x, Math.Max(y, z)));
    }

    /// <summary>The sphere round an asset's bounds as it stores them.</summary>
    public static (Vector3 Centre, float Radius) Bind(MeshAsset asset) =>
        ((asset.BoundsMin + asset.BoundsMax) / 2, (asset.BoundsMax - asset.BoundsMin).Length() / 2);

    /// <summary>
    /// A posed instance's bounding sphere in its asset's space: the box round each joint's posed place (its bound place
    /// <paramref name="jointPositions"/> through its skinning matrix in <paramref name="palette"/>) grown by its reach
    /// (times the joint's scale), and the asset's unmoved vertices' sphere (<paramref name="staticCentre"/>, a
    /// <paramref name="staticRadius"/> of -1 for none); the asset's own sphere when no joint reaches.
    /// </summary>
    public static (Vector3 Centre, float Radius) Posed(
        ReadOnlySpan<Vector3> jointPositions,
        ReadOnlySpan<float> reaches,
        ReadOnlySpan<Matrix4x4> palette,
        Vector3 staticCentre,
        float staticRadius,
        Vector3 centre,
        float radius
    )
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var j = 0; j < palette.Length; j++)
        {
            var reach = reaches[j];
            if (!(reach >= 0))
            {
                continue;
            }

            var at = Vector3.Transform(jointPositions[j], palette[j]);
            var grow = new Vector3(reach * LargestScale(in palette[j]));
            min = Vector3.Min(min, at - grow);
            max = Vector3.Max(max, at + grow);
        }

        if (staticRadius >= 0)
        {
            min = Vector3.Min(min, staticCentre - new Vector3(staticRadius));
            max = Vector3.Max(max, staticCentre + new Vector3(staticRadius));
        }

        return min.X > max.X ? (centre, radius) : ((min + max) / 2, (max - min).Length() / 2);
    }

    /// <summary>
    /// The bounding sphere of a skinned asset's vertices that no joint moves (their weights all zero), which stand where
    /// the asset stores them; a radius of -1 when there are none, or for a static asset.
    /// </summary>
    public static (Vector3 Centre, float Radius) Unweighted(MeshAsset asset)
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

    /// <summary>
    /// A sphere round every pose of an asset, about its bound sphere's centre (<see cref="Bind"/>): at least that sphere
    /// grown by its rig's largest joint reach, and round each joint and each <see cref="Posed"/> sphere of the rig's rest
    /// pose and of each clip at <see cref="SamplesPerClip"/> times from its start to its end. A static asset's is its bound
    /// sphere. Poses between the samples, blends of clips, and a game's shaping may reach a little past it. Allocates its
    /// scratch.
    /// </summary>
    public static (Vector3 Centre, float Radius) EveryPose(MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var (centre, radius) = Bind(asset);
        if (asset.Rig is not { } rig || rig.JointCount == 0)
        {
            return (centre, radius);
        }

        var most = 0f;
        foreach (var reach in rig.Reaches)
        {
            most = reach > most ? reach : most;
        }

        var bound = radius + most;
        var unweighted = Unweighted(asset);
        var locals = new JointTransform[rig.JointCount];
        var palette = new Matrix4x4[rig.JointCount];
        rig.Pose(-1, 0, locals);
        bound = Math.Max(bound, Reach(rig, locals, palette, unweighted, centre, radius));
        for (var clip = 0; clip < rig.Clips.Length; clip++)
        {
            var duration = rig.Clips[clip].Duration;
            for (var k = 0; k < SamplesPerClip; k++)
            {
                rig.Pose(clip, duration * k / (SamplesPerClip - 1), locals);
                bound = Math.Max(bound, Reach(rig, locals, palette, unweighted, centre, radius));
            }
        }

        return (centre, bound);
    }

    /// <summary>How far from <paramref name="centre"/> a pose reaches: its joints, and its <see cref="Posed"/> sphere.</summary>
    private static float Reach(
        Rig rig,
        JointTransform[] locals,
        Matrix4x4[] palette,
        (Vector3 Centre, float Radius) unweighted,
        Vector3 centre,
        float radius
    )
    {
        rig.WritePalette(locals, palette);
        var (posedCentre, posedRadius) = Posed(
            rig.BindPositions,
            rig.Reaches,
            palette,
            unweighted.Centre,
            unweighted.Radius,
            centre,
            radius
        );
        var reach = Vector3.Distance(posedCentre, centre) + posedRadius;
        for (var j = 0; j < palette.Length; j++)
        {
            reach = Math.Max(reach, Vector3.Distance(Vector3.Transform(rig.BindPositions[j], palette[j]), centre));
        }

        return reach;
    }
}
