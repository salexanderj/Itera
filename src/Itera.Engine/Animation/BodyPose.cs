using Itera.Engine.Maths;

namespace Itera.Engine.Animation;

/// <summary>
/// A pose of a skeleton: a turn per bone in its own space, after its rest turn, with room for
/// <see cref="Skeleton.MaxBones"/> (the rest unturned). A pose holds no lengths, so the same pose fits every skeleton of a
/// plan: a short body and a tall one raise an arm by the same turns. Turns
/// about x swing a bone forward or back (a positive turn swings a hanging limb forward), about z sideways (a positive
/// turn swings a hanging limb towards +x, the body's right), about y twist it.
/// </summary>
public sealed class BodyPose
{
    private readonly Rotation[] _turns;

    private BodyPose(Rotation[] turns)
    {
        _turns = turns;
    }

    /// <summary>The rest pose: no bone turned.</summary>
    public static BodyPose Rest { get; } = new(CreateIdentities());

    /// <summary>The pose with <paramref name="bone"/> turned by <paramref name="turn"/> as well, after its turn here.</summary>
    public BodyPose With(int bone, in Rotation turn)
    {
        var turns = (Rotation[])_turns.Clone();
        turns[bone] = turn.Times(turns[bone]);
        return new BodyPose(turns);
    }

    /// <summary>
    /// The pose on the body's other side: each bone's turn goes to its partner in <paramref name="partner"/> (by bone; a
    /// bone on the middle is its own), mirrored across the middle (<see cref="Rotation.Mirrored"/>).
    /// </summary>
    public BodyPose Mirrored(ReadOnlySpan<int> partner)
    {
        var turns = new Rotation[_turns.Length];
        Array.Fill(turns, Rotation.Identity);
        for (var bone = 0; bone < partner.Length && bone < _turns.Length; bone++)
        {
            turns[partner[bone]] = _turns[bone].Mirrored();
        }

        return new BodyPose(turns);
    }

    /// <summary>Every bone's turn, by bone.</summary>
    public ReadOnlySpan<Rotation> Turns => _turns;

    /// <summary>
    /// Writes each bone's turn <paramref name="t"/> of the way from <paramref name="from"/> (0) to
    /// <paramref name="to"/> (1) into <paramref name="turns"/> (one per bone), for
    /// <see cref="Skeleton.Pose(ReadOnlySpan{Rotation}, Span{RigidTransform})"/>.
    /// </summary>
    public static void Blend(BodyPose from, BodyPose to, double t, Span<Rotation> turns)
    {
        for (var i = 0; i < turns.Length; i++)
        {
            turns[i] = Rotation.Blend(from._turns[i], to._turns[i], t);
        }
    }

    private static Rotation[] CreateIdentities()
    {
        var turns = new Rotation[Skeleton.MaxBones];
        Array.Fill(turns, Rotation.Identity);
        return turns;
    }
}
