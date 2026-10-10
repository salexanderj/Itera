using System.Numerics;

namespace Itera.Engine.Assets;

/// <summary>
/// The joints that pose a skinned mesh asset (a glTF skin), in metres, parents before their children, and its clips
/// (<see cref="RigClip"/>). Each joint has a parent (-1 for none), a fixed transform between it and that parent (the
/// nodes between them that are not joints, and for a joint without a parent joint every node above it to the scene's
/// root; usually none), its rest transform (<see cref="JointTransform"/>), and its inverse bind matrix (the asset's space
/// to the joint's in the pose the mesh was bound in). <see cref="WritePalette"/> turns a pose into the matrices the
/// skinning reads (<c>v * InverseBind * Global</c>, the vertex in the asset's space). For drawing only, in floats: not
/// the same on every machine (the engine's deterministic skeletons are <c>Animation.Skeleton</c>'s).
/// </summary>
public sealed class Rig
{
    public Rig(
        string[] names,
        int[] parents,
        Matrix4x4[] offsets,
        JointTransform[] rest,
        Matrix4x4[] inverseBinds,
        float[] reaches,
        RigClip[] clips
    )
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(inverseBinds);
        ArgumentNullException.ThrowIfNull(reaches);
        ArgumentNullException.ThrowIfNull(clips);
        var count = names.Length;
        if (
            parents.Length != count
            || offsets.Length != count
            || rest.Length != count
            || inverseBinds.Length != count
            || reaches.Length != count
        )
        {
            throw new ArgumentException("A rig's joint arrays must all have one entry a joint.", nameof(parents));
        }

        for (var j = 0; j < count; j++)
        {
            if (parents[j] < -1 || parents[j] >= j)
            {
                throw new ArgumentException(
                    $"Joint {j}'s parent {parents[j]} does not come before it: a rig lists parents first.",
                    nameof(parents)
                );
            }
        }

        Names = names;
        Parents = parents;
        Offsets = offsets;
        Rest = rest;
        InverseBinds = inverseBinds;
        Reaches = reaches;
        Clips = clips;
        BindPositions = new Vector3[count];
        for (var j = 0; j < count; j++)
        {
            // The joint's place in the bound pose, in the asset's space: where its inverse bind matrix takes the origin from.
            BindPositions[j] = Matrix4x4.Invert(inverseBinds[j], out var bind) ? bind.Translation : Vector3.Zero;
        }
    }

    /// <summary>The joints' names in the file, for finding one and for messages.</summary>
    public string[] Names { get; }

    public int JointCount => Names.Length;

    /// <summary>Each joint's parent joint, before it in the list, or -1.</summary>
    public int[] Parents { get; }

    /// <summary>Each joint's fixed transform from its parent joint's space (or the scene's root) to its own parent node's.</summary>
    public Matrix4x4[] Offsets { get; }

    /// <summary>Each joint's transform when no clip moves it (its node's own, in its file).</summary>
    public JointTransform[] Rest { get; }

    public Matrix4x4[] InverseBinds { get; }

    /// <summary>
    /// The farthest any vertex a joint moves lies from it in the bound pose, in metres: its posed bounds' radius; -1 for a
    /// joint that moves no vertex.
    /// </summary>
    public float[] Reaches { get; }

    /// <summary>Each joint's place in the bound pose, in the asset's space.</summary>
    public Vector3[] BindPositions { get; }

    public RigClip[] Clips { get; }

    /// <summary>The index of the clip <paramref name="name"/>, or -1.</summary>
    public int FindClip(string name)
    {
        for (var i = 0; i < Clips.Length; i++)
        {
            if (string.Equals(Clips[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The joints' transforms in clip <paramref name="clip"/> (an index into <see cref="Clips"/>; -1 for the rest pose) at
    /// <paramref name="seconds"/>, into <paramref name="locals"/> (one a joint). Allocates nothing.
    /// </summary>
    public void Pose(int clip, float seconds, Span<JointTransform> locals)
    {
        Rest.AsSpan().CopyTo(locals);
        if ((uint)clip < (uint)Clips.Length)
        {
            Clips[clip].Sample(seconds, locals);
        }
    }

    /// <summary>
    /// The skinning matrices of a pose (<paramref name="locals"/>, one a joint) into <paramref name="palette"/> (one a
    /// joint): each joint's inverse bind matrix, then its place in the asset's space in that pose. Allocates nothing.
    /// </summary>
    public void WritePalette(ReadOnlySpan<JointTransform> locals, Span<Matrix4x4> palette)
    {
        // First each joint's place in the asset's space, parents before children, then the inverse bind before it.
        for (var j = 0; j < Names.Length; j++)
        {
            var global = locals[j].ToMatrix() * Offsets[j];
            palette[j] = Parents[j] < 0 ? global : global * palette[Parents[j]];
        }

        for (var j = 0; j < Names.Length; j++)
        {
            palette[j] = InverseBinds[j] * palette[j];
        }
    }
}
