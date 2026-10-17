using System.Numerics;

namespace Itera.Engine.Assets;

/// <summary>
/// The joints that pose a skinned mesh asset (a glTF skin), in metres, parents before their children, and its clips
/// (<see cref="RigClip"/>). Each joint has a parent (-1 for none), a fixed transform between it and that parent (the
/// nodes between them that are not joints, and for a joint without a parent joint every node above it to the scene's
/// root; usually none), its rest transform (<see cref="JointTransform"/>), and its inverse bind matrix (the asset's space
/// to the joint's in the pose the mesh was bound in). <see cref="WritePalette"/> turns a pose into the matrices the
/// skinning reads (<c>v * InverseBind * Global</c>, the vertex in the asset's space). A pose can be shaped before that:
/// two blended joint by joint (<see cref="Blend"/>, with weights a <see cref="Mask"/> sets: legs from one clip, the upper
/// body from another), a joint turned in the asset's space (<see cref="Turn"/>: a spine bent to an aim), and an arm or a
/// leg reaching a point (<see cref="ReachTwoBone"/>: a hand on a rifle). In floats, so not the same on every machine
/// (the engine's deterministic skeletons are <c>Animation.Skeleton</c>'s): for drawing, and for what one peer alone
/// judges (a host's hits on posed bodies).
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

    /// <summary>The index of the joint <paramref name="name"/> (its node's name in the file), or -1.</summary>
    public int FindJoint(string name)
    {
        for (var j = 0; j < Names.Length; j++)
        {
            if (string.Equals(Names[j], name, StringComparison.Ordinal))
            {
                return j;
            }
        }

        return -1;
    }

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
        // First each joint's place in the asset's space, then the inverse bind before it.
        Globals(locals, palette);
        for (var j = 0; j < Names.Length; j++)
        {
            palette[j] = InverseBinds[j] * palette[j];
        }
    }

    /// <summary>
    /// Each joint's transform in the asset's space in a pose (<paramref name="locals"/>, one a joint), into
    /// <paramref name="globals"/> (one a joint): <c>local * offset * parent's</c>, parents before children; a joint's place
    /// is its <c>Translation</c>. Allocates nothing.
    /// </summary>
    public void Globals(ReadOnlySpan<JointTransform> locals, Span<Matrix4x4> globals) =>
        GlobalsUpTo(locals, globals, Names.Length - 1);

    /// <summary>
    /// <see cref="Globals(ReadOnlySpan{JointTransform}, Span{Matrix4x4})"/> for the joints up to <paramref name="last"/>
    /// alone: parents come before their children, so each of those joints' parents is among them. The others' entries are
    /// left as they were. Allocates nothing.
    /// </summary>
    private void GlobalsUpTo(ReadOnlySpan<JointTransform> locals, Span<Matrix4x4> globals, int last)
    {
        for (var j = 0; j <= last; j++)
        {
            var global = locals[j].ToMatrix() * Offsets[j];
            globals[j] = Parents[j] < 0 ? global : global * globals[Parents[j]];
        }
    }

    /// <summary>
    /// Sets <paramref name="weight"/> into <paramref name="weights"/> (one a joint) for joint <paramref name="root"/> and
    /// every joint below it, leaving the others: the share of an upper layer a <see cref="Blend"/> takes from the spine up.
    /// </summary>
    public void Mask(int root, Span<float> weights, float weight)
    {
        if ((uint)root >= (uint)Names.Length)
        {
            return;
        }

        // Parents come first, so one pass finds every joint below the root. A joint in the mask is marked by its weight
        // being the one set; the marks live in a stack span so other weights already set are kept.
        Span<bool> inside = Names.Length <= 512 ? stackalloc bool[Names.Length] : new bool[Names.Length];
        inside.Clear();
        inside[root] = true;
        weights[root] = weight;
        for (var j = root + 1; j < Names.Length; j++)
        {
            if (Parents[j] >= 0 && inside[Parents[j]])
            {
                inside[j] = true;
                weights[j] = weight;
            }
        }
    }

    /// <summary>
    /// Two poses blended joint by joint into <paramref name="into"/>: each joint <paramref name="weights"/> of the way from
    /// <paramref name="under"/> to <paramref name="over"/> (rotations along the shorter way). Any of the three may be the
    /// same span. Allocates nothing.
    /// </summary>
    public static void Blend(
        ReadOnlySpan<JointTransform> under,
        ReadOnlySpan<JointTransform> over,
        ReadOnlySpan<float> weights,
        Span<JointTransform> into
    )
    {
        for (var j = 0; j < into.Length; j++)
        {
            var weight = weights[j];
            into[j] =
                weight <= 0 ? under[j]
                : weight >= 1 ? over[j]
                : new JointTransform(
                    Vector3.Lerp(under[j].Translation, over[j].Translation, weight),
                    Quaternion.Slerp(under[j].Rotation, over[j].Rotation, weight),
                    Vector3.Lerp(under[j].Scale, over[j].Scale, weight)
                );
        }
    }

    /// <summary>
    /// Turns joint <paramref name="joint"/> of a pose by <paramref name="turn"/>, a rotation in the asset's space about the
    /// joint's own place: the joint stays where it is and everything below it swings round it (a spine bent to an aim, a
    /// head turned to a target). <paramref name="globals"/> is scratch, one a joint (only the joint's parent and the
    /// joints before it are worked out: read none of it after). Allocates nothing.
    /// </summary>
    public void Turn(Span<JointTransform> locals, int joint, Quaternion turn, Span<Matrix4x4> globals)
    {
        if ((uint)joint >= (uint)Names.Length)
        {
            return;
        }

        GlobalsUpTo(locals, globals, Parents[joint]);
        // The joint's global is local * frame, where frame is its offset and its parent's global. Turned in the asset's
        // space after it, the global becomes local * frame * turn, so the new local is local * frame * turn * frame⁻¹.
        var frame = Parents[joint] < 0 ? Offsets[joint] : Offsets[joint] * globals[Parents[joint]];
        if (!Matrix4x4.Decompose(frame, out _, out var frameRotation, out _))
        {
            return;
        }

        var local = locals[joint];
        // Quaternions here read right to left (the right one first): the local, then the frame, then the turn, then back.
        var rotation = Quaternion.Inverse(frameRotation) * turn * frameRotation * local.Rotation;
        locals[joint] = local with { Rotation = Quaternion.Normalize(rotation) };
    }

    /// <summary>
    /// Sets each joint whose <paramref name="placing"/> entry is true to the transform in the asset's space that
    /// <paramref name="placed"/> gives it (a ragdoll's bodies posing the figure they stand for), the others keeping their
    /// locals and so following their parents: one pass, parents first, the pose's globals left in
    /// <paramref name="globals"/>. All three spans are one a joint. Allocates nothing.
    /// </summary>
    public void Place(
        Span<JointTransform> locals,
        ReadOnlySpan<Matrix4x4> placed,
        ReadOnlySpan<bool> placing,
        Span<Matrix4x4> globals
    )
    {
        for (var j = 0; j < Names.Length; j++)
        {
            // A joint's global is its local through its frame (its offset and its parent's global), so the local that
            // places it is the placed transform back through the frame.
            var frame = Parents[j] < 0 ? Offsets[j] : Offsets[j] * globals[Parents[j]];
            if (
                placing[j]
                && Matrix4x4.Invert(frame, out var inverse)
                && Matrix4x4.Decompose(placed[j] * inverse, out var scale, out var rotation, out var translation)
            )
            {
                locals[j] = new JointTransform(translation, Quaternion.Normalize(rotation), scale);
            }

            globals[j] = locals[j].ToMatrix() * frame;
        }
    }

    /// <summary>
    /// Moves joint <paramref name="joint"/> of a pose by <paramref name="offset"/> in the asset's space: it and every joint
    /// below it shift by that much, turned as they were (hips lowered so a foot reaches lower ground).
    /// <paramref name="globals"/> is scratch, one a joint (only the joint's parent and the joints before it are worked
    /// out: read none of it after). Allocates nothing.
    /// </summary>
    public void Move(Span<JointTransform> locals, int joint, Vector3 offset, Span<Matrix4x4> globals)
    {
        if ((uint)joint >= (uint)Names.Length)
        {
            return;
        }

        GlobalsUpTo(locals, globals, Parents[joint]);
        // The joint's place is its local translation through its frame (its offset and its parent's global), so a move in
        // the asset's space is the same move turned back through the frame.
        var frame = Parents[joint] < 0 ? Offsets[joint] : Offsets[joint] * globals[Parents[joint]];
        if (!Matrix4x4.Invert(frame, out var inverse))
        {
            return;
        }

        var local = locals[joint];
        locals[joint] = local with { Translation = local.Translation + Vector3.TransformNormal(offset, inverse) };
    }

    /// <summary>
    /// Bends the chain <paramref name="upper"/>, <paramref name="lower"/>, <paramref name="end"/> (a shoulder, an elbow,
    /// a hand; a hip, a knee, a foot) so the end joint reaches <paramref name="target"/>, in the asset's space, or points at
    /// it as far as the chain stretches; the middle joint bends towards <paramref name="pole"/> (where the elbow or knee
    /// points). The end joint keeps its own turn. <paramref name="globals"/> is scratch, one a joint (only the end joint
    /// and the joints before it are worked out: read none of it after). Returns false, the pose unchanged, for joints that
    /// are not such a chain. Allocates nothing.
    /// </summary>
    public bool ReachTwoBone(
        Span<JointTransform> locals,
        int upper,
        int lower,
        int end,
        Vector3 target,
        Vector3 pole,
        Span<Matrix4x4> globals
    )
    {
        if (
            (uint)end >= (uint)Names.Length
            || (uint)lower >= (uint)Names.Length
            || (uint)upper >= (uint)Names.Length
            || Parents[end] != lower
            || Parents[lower] != upper
        )
        {
            return false;
        }

        GlobalsUpTo(locals, globals, end);
        var a = globals[upper].Translation;
        var b = globals[lower].Translation;
        var c = globals[end].Translation;
        var upperLength = Vector3.Distance(a, b);
        var lowerLength = Vector3.Distance(b, c);
        var toTarget = target - a;
        var reach = toTarget.Length();
        if (upperLength < 1e-6f || lowerLength < 1e-6f || reach < 1e-6f)
        {
            return false;
        }

        var direction = toTarget / reach;
        reach = Math.Clamp(reach, MathF.Abs(upperLength - lowerLength) + 1e-4f, upperLength + lowerLength - 1e-4f);
        // The middle joint's new place: along the target's direction by the law of cosines, out towards the pole.
        var along = (upperLength * upperLength - lowerLength * lowerLength + reach * reach) / (2 * reach);
        var across = MathF.Sqrt(MathF.Max(0, upperLength * upperLength - along * along));
        var bend = pole - a;
        bend -= direction * Vector3.Dot(bend, direction);
        if (bend.LengthSquared() < 1e-10f)
        {
            // A pole on the line: bend the way the middle joint already does.
            bend = b - a;
            bend -= direction * Vector3.Dot(bend, direction);
        }

        bend = bend.LengthSquared() > 1e-10f ? Vector3.Normalize(bend) : Perpendicular(direction);
        var middle = a + direction * along + bend * across;
        Turn(locals, upper, Arc(b - a, middle - a), globals);
        GlobalsUpTo(locals, globals, end);
        var bent = globals[lower].Translation;
        Turn(locals, lower, Arc(globals[end].Translation - bent, a + direction * reach - bent), globals);
        return true;
    }

    /// <summary>The shortest turn from one direction to another (neither zero).</summary>
    private static Quaternion Arc(Vector3 from, Vector3 to)
    {
        var lengths = from.Length() * to.Length();
        if (!(lengths > 1e-12f) || !float.IsFinite(lengths))
        {
            return Quaternion.Identity;
        }

        // The axis's length is the angle's sine: two ways that are nearly one turn by nothing, or by half a turn about any
        // axis square to them when nearly opposite (normalising a cross product rounded to nothing would not be finite).
        var dot = Vector3.Dot(from, to) / lengths;
        var axis = Vector3.Cross(from, to) / lengths;
        var sin = axis.Length();
        if (!(sin > 1e-6f))
        {
            return dot >= 0
                ? Quaternion.Identity
                : Quaternion.CreateFromAxisAngle(Perpendicular(Vector3.Normalize(from)), MathF.PI);
        }

        return Quaternion.CreateFromAxisAngle(axis / sin, MathF.Atan2(sin, dot));
    }

    private static Vector3 Perpendicular(Vector3 direction) =>
        Vector3.Normalize(
            MathF.Abs(direction.X) < 0.9f
                ? Vector3.Cross(direction, Vector3.UnitX)
                : Vector3.Cross(direction, Vector3.UnitY)
        );
}
