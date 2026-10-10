using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using SharpGLTF.Schema2;

namespace Itera.Engine.Assets;

/// <summary>
/// The skin and clips of a glTF file: the first skin a mesh of the scene uses becomes the asset's <see cref="Rig"/> (its
/// joints parents first, every translation in metres), each vertex of the meshes it skins takes its four largest joint
/// weights, summing to one, and each animation's channels on those joints become a <see cref="RigClip"/>.
/// </summary>
internal static partial class GltfImport
{
    /// <summary>The first skin a mesh node under <paramref name="node"/> uses, or null; a second, different one is warned about.</summary>
    private static Skin? FindSkin(Node node, Skin? found, ImportContext context)
    {
        if (node.Mesh is not null && node.Skin is { } skin)
        {
            if (found is null)
            {
                found = skin;
            }
            else if (found.LogicalIndex != skin.LogicalIndex && !context.SkinsWarned)
            {
                context.SkinsWarned = true;
                context.Warn(
                    "has meshes on more than one skin: only the first skin's are posed, the others stand as stored"
                );
            }
        }

        foreach (var child in node.VisualChildren)
        {
            found = FindSkin(child, found, context);
        }

        return found;
    }

    /// <summary>The order of the skin's joints in the rig, parents first: each node's depth below the scene's root, then the skin's order.</summary>
    private static RigPlan PlanRig(Skin skin)
    {
        var count = skin.JointsCount;
        var nodes = new Node[count];
        var depths = new int[count];
        for (var i = 0; i < count; i++)
        {
            nodes[i] = skin.GetJoint(i).Joint;
            for (var parent = nodes[i].VisualParent; parent is not null; parent = parent.VisualParent)
            {
                depths[i]++;
            }
        }

        var order = Enumerable.Range(0, count).OrderBy(i => depths[i]).ThenBy(i => i).ToArray();
        var rigOfSkin = new int[count];
        var rigOfNode = new Dictionary<int, int>();
        for (var r = 0; r < count; r++)
        {
            rigOfSkin[order[r]] = r;
            rigOfNode[nodes[order[r]].LogicalIndex] = r;
        }

        return new RigPlan(skin, order, rigOfSkin, rigOfNode);
    }

    /// <summary>
    /// A vertex's skin entry: its four largest weights over JOINTS_0/WEIGHTS_0 and JOINTS_1/WEIGHTS_1, their joints in
    /// the rig's order, scaled to sum to one; none (all zero) where the primitive has no weights or they sum to zero.
    /// </summary>
    private static MeshSkinVertex SkinOf(int source, SkinAccessors accessors, RigPlan plan, ImportContext context)
    {
        Span<int> joints = stackalloc int[8];
        Span<float> weights = stackalloc float[8];
        var count = 0;
        AddInfluences(accessors.Joints0, accessors.Weights0, source, plan, joints, weights, ref count, context);
        AddInfluences(accessors.Joints1, accessors.Weights1, source, plan, joints, weights, ref count, context);

        // The four largest, by a few passes of selection.
        Span<int> kept = stackalloc int[4];
        Span<float> keptWeights = stackalloc float[4];
        var sum = 0f;
        for (var slot = 0; slot < 4; slot++)
        {
            var best = -1;
            for (var i = 0; i < count; i++)
            {
                if (weights[i] > 0 && (best < 0 || weights[i] > weights[best]))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            kept[slot] = joints[best];
            keptWeights[slot] = weights[best];
            sum += weights[best];
            weights[best] = 0;
        }

        if (!(sum > 0))
        {
            return default;
        }

        var normalised = new Vector4(keptWeights[0], keptWeights[1], keptWeights[2], keptWeights[3]) / sum;
        return MeshSkinVertex.Pack(kept[0], kept[1], kept[2], kept[3], normalised);
    }

    private static void AddInfluences(
        IReadOnlyList<Vector4>? jointList,
        IReadOnlyList<Vector4>? weightList,
        int source,
        RigPlan plan,
        Span<int> joints,
        Span<float> weights,
        ref int count,
        ImportContext context
    )
    {
        if (jointList is null || weightList is null)
        {
            return;
        }

        var j = jointList[source];
        var w = weightList[source];
        ReadOnlySpan<float> js = [j.X, j.Y, j.Z, j.W];
        ReadOnlySpan<float> ws = [w.X, w.Y, w.Z, w.W];
        for (var i = 0; i < 4; i++)
        {
            if (!(ws[i] > 0))
            {
                continue;
            }

            var skinJoint = (int)js[i];
            if (skinJoint < 0 || skinJoint >= plan.RigOfSkin.Length)
            {
                if (!context.JointsWarned)
                {
                    context.JointsWarned = true;
                    context.Warn($"weights a joint {skinJoint} its skin does not have: that weight is left out");
                }

                continue;
            }

            joints[count] = plan.RigOfSkin[skinJoint];
            weights[count] = ws[i];
            count++;
        }
    }

    /// <summary>
    /// The rig from the plan, in metres (every translation, the offsets', the rest pose's, the inverse binds', and the
    /// clips', times <paramref name="metresPerUnit"/>), with each joint's reach over the asset's skinned vertices.
    /// </summary>
    private static Rig BuildRig(ModelRoot model, RigPlan plan, float metresPerUnit, ImportContext context)
    {
        var count = plan.Order.Length;
        var names = new string[count];
        var parents = new int[count];
        var offsets = new Matrix4x4[count];
        var rest = new JointTransform[count];
        var inverseBinds = new Matrix4x4[count];
        for (var r = 0; r < count; r++)
        {
            var (node, inverseBind) = plan.Skin.GetJoint(plan.Order[r]);
            names[r] = node.Name ?? $"joint {node.LogicalIndex}";
            inverseBinds[r] = InMetres(inverseBind, metresPerUnit);

            // The nodes between the joint and its parent joint (or the scene's root), nearest first.
            var offset = Matrix4x4.Identity;
            var parent = node.VisualParent;
            while (parent is not null && !plan.RigOfNode.ContainsKey(parent.LogicalIndex))
            {
                offset *= parent.LocalMatrix;
                parent = parent.VisualParent;
            }

            parents[r] = parent is null ? -1 : plan.RigOfNode[parent.LogicalIndex];
            offsets[r] = InMetres(offset, metresPerUnit);
            if (!Matrix4x4.Decompose(node.LocalMatrix, out var scale, out var rotation, out var translation))
            {
                context.Warn(
                    $"joint '{names[r]}' has a transform that is not a scale, turn, and move: it rests unturned"
                );
                (scale, rotation, translation) = (Vector3.One, Quaternion.Identity, node.LocalMatrix.Translation);
            }

            rest[r] = new JointTransform(translation * metresPerUnit, rotation, scale);
        }

        var reaches = Reaches(context, inverseBinds);
        return new Rig(
            names,
            parents,
            offsets,
            rest,
            inverseBinds,
            reaches,
            ReadClips(model, plan, metresPerUnit, context)
        );
    }

    /// <summary>For each joint, the farthest vertex it moves from its place in the bound pose.</summary>
    private static float[] Reaches(ImportContext context, Matrix4x4[] inverseBinds)
    {
        var places = new Vector3[inverseBinds.Length];
        for (var j = 0; j < places.Length; j++)
        {
            places[j] = Matrix4x4.Invert(inverseBinds[j], out var bind) ? bind.Translation : Vector3.Zero;
        }

        // -1 for a joint that moves no vertex; 0 for one whose vertices all sit on it.
        var reaches = new float[inverseBinds.Length];
        Array.Fill(reaches, -1f);
        for (var v = 0; v < context.Vertices.Count; v++)
        {
            var skin = context.Skins[v];
            for (var slot = 0; slot < 4; slot++)
            {
                if (skin.Weight(slot) > 0)
                {
                    var joint = skin.Joint(slot);
                    reaches[joint] = Math.Max(
                        reaches[joint],
                        Vector3.Distance(context.Vertices[v].Position, places[joint])
                    );
                }
            }
        }

        return reaches;
    }

    /// <summary>Each animation with channels on the rig's joints as a clip; channels on other nodes or morph weights are left out, with a warning.</summary>
    private static RigClip[] ReadClips(ModelRoot model, RigPlan plan, float metresPerUnit, ImportContext context)
    {
        var clips = new List<RigClip>();
        var warnedNodes = false;
        var warnedWeights = false;
        foreach (var animation in model.LogicalAnimations)
        {
            var tracks = new List<RigTrack>();
            foreach (var channel in animation.Channels)
            {
                if (
                    channel.TargetNode is not { } target
                    || !plan.RigOfNode.TryGetValue(target.LogicalIndex, out var joint)
                )
                {
                    if (!warnedNodes)
                    {
                        warnedNodes = true;
                        context.Warn("animates nodes that are not joints of its skin, which are not drawn moving");
                    }

                    continue;
                }

                switch (channel.TargetNodePath)
                {
                    case PropertyPath.translation:
                        tracks.Add(
                            VectorTrack(joint, RigPath.Translation, channel.GetTranslationSampler(), metresPerUnit)
                        );
                        break;
                    case PropertyPath.rotation:
                        tracks.Add(RotationTrack(joint, channel.GetRotationSampler()));
                        break;
                    case PropertyPath.scale:
                        tracks.Add(VectorTrack(joint, RigPath.Scale, channel.GetScaleSampler(), 1));
                        break;
                    default:
                        if (!warnedWeights)
                        {
                            warnedWeights = true;
                            context.Warn("animates morph weights, which are not drawn");
                        }

                        break;
                }
            }

            if (tracks.Count > 0)
            {
                clips.Add(new RigClip(animation.Name ?? $"clip {animation.LogicalIndex}", [.. tracks]));
            }
        }

        return [.. clips];
    }

    private static RigTrack VectorTrack(int joint, RigPath path, IAnimationSampler<Vector3> sampler, float unit)
    {
        var interpolation = InterpolationOf(sampler.InterpolationMode);
        var times = new List<float>();
        var values = new List<Vector4>();
        if (interpolation == RigInterpolation.CubicSpline)
        {
            foreach (var (time, (tangentIn, value, tangentOut)) in sampler.GetCubicKeys())
            {
                times.Add(time);
                values.Add(new Vector4(tangentIn * unit, 0));
                values.Add(new Vector4(value * unit, 0));
                values.Add(new Vector4(tangentOut * unit, 0));
            }
        }
        else
        {
            foreach (var (time, value) in sampler.GetLinearKeys())
            {
                times.Add(time);
                values.Add(new Vector4(value * unit, 0));
            }
        }

        return new RigTrack(joint, path, interpolation, [.. times], [.. values]);
    }

    private static RigTrack RotationTrack(int joint, IAnimationSampler<Quaternion> sampler)
    {
        var interpolation = InterpolationOf(sampler.InterpolationMode);
        var times = new List<float>();
        var values = new List<Vector4>();
        if (interpolation == RigInterpolation.CubicSpline)
        {
            foreach (var (time, (tangentIn, value, tangentOut)) in sampler.GetCubicKeys())
            {
                times.Add(time);
                values.Add(AsVector(tangentIn));
                values.Add(AsVector(value));
                values.Add(AsVector(tangentOut));
            }
        }
        else
        {
            foreach (var (time, value) in sampler.GetLinearKeys())
            {
                times.Add(time);
                values.Add(AsVector(value));
            }
        }

        return new RigTrack(joint, RigPath.Rotation, interpolation, [.. times], [.. values]);
    }

    private static Vector4 AsVector(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private static RigInterpolation InterpolationOf(AnimationInterpolationMode mode) =>
        mode switch
        {
            AnimationInterpolationMode.STEP => RigInterpolation.Step,
            AnimationInterpolationMode.CUBICSPLINE => RigInterpolation.CubicSpline,
            _ => RigInterpolation.Linear,
        };

    /// <summary>
    /// A transform in file units as one in metres: <c>S⁻¹ M S</c> for a uniform scale S, which leaves its turn and scale
    /// and multiplies its translation.
    /// </summary>
    private static Matrix4x4 InMetres(Matrix4x4 matrix, float metresPerUnit)
    {
        matrix.M41 *= metresPerUnit;
        matrix.M42 *= metresPerUnit;
        matrix.M43 *= metresPerUnit;
        return matrix;
    }

    /// <summary>The skin the asset's rig is made from, the rig's joint order (skin indices), and the maps into it.</summary>
    private sealed record RigPlan(Skin Skin, int[] Order, int[] RigOfSkin, Dictionary<int, int> RigOfNode);

    /// <summary>A primitive's joints and weights, both sets of four where it has them.</summary>
    private sealed record SkinAccessors(
        IReadOnlyList<Vector4>? Joints0,
        IReadOnlyList<Vector4>? Weights0,
        IReadOnlyList<Vector4>? Joints1,
        IReadOnlyList<Vector4>? Weights1
    );
}
