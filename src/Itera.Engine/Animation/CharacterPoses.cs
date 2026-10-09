using Itera.Engine.Maths;

namespace Itera.Engine.Animation;

/// <summary>
/// Every body's pose, by slot: the turn of each bone (room for <see cref="Skeleton.MaxBones"/> whatever the body's
/// skeleton; bones past its skeleton's stay unturned), how far the skeleton is raised above the feet (its lift, from
/// foot placement; NaN stands it on its lowest foot), how far it stands to the right of its feet (its sway), and which
/// way it faces (its yaw), after the last fixed step and after the one before, so frames draw the body between the two as
/// they draw its feet. An animation system writes the poses once a step; hit tests read the current pose, and drawing
/// blends the two. Slot 0 is the rest pose and is never written: a body reads it until the animation system gives it a
/// slot of its own. A pose is derived from the animation state, not state itself. Main thread; allocates only when it
/// grows.
/// </summary>
public sealed class CharacterPoses
{
    private const int Bones = Skeleton.MaxBones;

    private readonly Stack<int> _free = new();
    private Rotation[] _current = new Rotation[Bones * 16];
    private Rotation[] _previous = new Rotation[Bones * 16];
    private double[] _lift = new double[16];
    private double[] _previousLift = new double[16];
    private double[] _sway = new double[16];
    private double[] _previousSway = new double[16];
    private float[] _yaw = new float[16];
    private float[] _previousYaw = new float[16];
    private int _next = 1;

    public CharacterPoses()
    {
        Array.Fill(_current, Rotation.Identity);
        Array.Fill(_previous, Rotation.Identity);
        Array.Fill(_lift, double.NaN);
        Array.Fill(_previousLift, double.NaN);
    }

    /// <summary>Slots given out and not freed.</summary>
    public int Count => _next - 1 - _free.Count;

    /// <summary>
    /// A new slot whose current and previous poses are both <paramref name="turns"/> (one per bone, the bones after them
    /// unturned), <paramref name="lift"/>, <paramref name="sway"/>, and <paramref name="yaw"/>, so the first frame draws the
    /// body still.
    /// </summary>
    public int Allocate(ReadOnlySpan<Rotation> turns, double lift = double.NaN, double sway = 0, float yaw = 0)
    {
        if (!_free.TryPop(out var slot))
        {
            slot = _next++;
            if (_next * Bones > _current.Length)
            {
                var length = _current.Length * 2;
                var old = _current.Length;
                Array.Resize(ref _current, length);
                Array.Resize(ref _previous, length);
                _current.AsSpan(old).Fill(Rotation.Identity);
                _previous.AsSpan(old).Fill(Rotation.Identity);
                Array.Resize(ref _lift, length / Bones);
                Array.Resize(ref _previousLift, length / Bones);
                Array.Resize(ref _sway, length / Bones);
                Array.Resize(ref _previousSway, length / Bones);
                Array.Resize(ref _yaw, length / Bones);
                Array.Resize(ref _previousYaw, length / Bones);
            }
        }

        turns.CopyTo(Span(_current, slot));
        turns.CopyTo(Span(_previous, slot));
        Span(_current, slot)[turns.Length..].Fill(Rotation.Identity);
        Span(_previous, slot)[turns.Length..].Fill(Rotation.Identity);
        _lift[slot] = lift;
        _previousLift[slot] = lift;
        _sway[slot] = sway;
        _previousSway[slot] = sway;
        _yaw[slot] = yaw;
        _previousYaw[slot] = yaw;
        return slot;
    }

    /// <summary>Gives <paramref name="slot"/> back; 0 is ignored. The caller sets its own slot to 0.</summary>
    public void Free(int slot)
    {
        if (slot <= 0)
        {
            return;
        }

        Span(_current, slot).Fill(Rotation.Identity);
        Span(_previous, slot).Fill(Rotation.Identity);
        _lift[slot] = double.NaN;
        _previousLift[slot] = double.NaN;
        _sway[slot] = 0;
        _previousSway[slot] = 0;
        _yaw[slot] = 0;
        _previousYaw[slot] = 0;
        _free.Push(slot);
    }

    /// <summary>The pose of <paramref name="slot"/> after the last fixed step, a turn per bone.</summary>
    public ReadOnlySpan<Rotation> Current(int slot) => Span(_current, slot);

    /// <summary>The pose of <paramref name="slot"/> after the step before the last.</summary>
    public ReadOnlySpan<Rotation> Previous(int slot) => Span(_previous, slot);

    /// <summary>The lift of <paramref name="slot"/> after the last fixed step, in level-0 voxels; NaN for none.</summary>
    public double Lift(int slot) => _lift[slot];

    /// <summary>
    /// The sway of <paramref name="slot"/> after the last fixed step: how far the body stands to the right of its feet,
    /// in level-0 voxels.
    /// </summary>
    public double Sway(int slot) => _sway[slot];

    /// <summary>Sets the sway of <paramref name="slot"/> (not 0) for this step, after <see cref="Next"/>.</summary>
    public void SetSway(int slot, double sway)
    {
        if (slot <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot 0 is the rest pose and is never written.");
        }

        _sway[slot] = sway;
    }

    /// <summary>The sway of <paramref name="slot"/> <paramref name="alpha"/> of the way from its previous to its current.</summary>
    public double BlendSway(int slot, float alpha) => _previousSway[slot] + (_sway[slot] - _previousSway[slot]) * alpha;

    /// <summary>Sets the yaw of <paramref name="slot"/> (not 0) for this step, after <see cref="Next"/>, in radians.</summary>
    public void SetYaw(int slot, float yaw)
    {
        if (slot <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot 0 is the rest pose and is never written.");
        }

        _yaw[slot] = yaw;
    }

    /// <summary>
    /// The yaw of <paramref name="slot"/> <paramref name="alpha"/> of the way from its previous to its current, the short
    /// way round, in radians: frames draw a turning body turning smoothly, not in steps of the fixed rate.
    /// </summary>
    public float BlendYaw(int slot, float alpha)
    {
        var from = _previousYaw[slot];
        return from + MathF.IEEERemainder(_yaw[slot] - from, 2 * MathF.PI) * alpha;
    }

    /// <summary>Sets the lift of <paramref name="slot"/> (not 0) for this step, after <see cref="Next"/>.</summary>
    public void SetLift(int slot, double lift)
    {
        if (slot <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot 0 is the rest pose and is never written.");
        }

        _lift[slot] = lift;
    }

    /// <summary>
    /// Starts a new step for <paramref name="slot"/> (not 0): its current pose, lift, sway, and yaw become its previous
    /// ones, and the span returned, its current pose, is for the caller to write (and <see cref="SetLift"/>,
    /// <see cref="SetSway"/>, and <see cref="SetYaw"/> the rest).
    /// </summary>
    public Span<Rotation> Next(int slot)
    {
        if (slot <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot 0 is the rest pose and is never written.");
        }

        var current = Span(_current, slot);
        current.CopyTo(Span(_previous, slot));
        _previousLift[slot] = _lift[slot];
        _previousSway[slot] = _sway[slot];
        _previousYaw[slot] = _yaw[slot];
        return current;
    }

    /// <summary>
    /// Makes <paramref name="slot"/>'s previous pose, lift, sway, and yaw its current ones, after they were written: for a body
    /// whose poses were not built for a while (far away), so the frames drawn next do not blend from an old pose.
    /// </summary>
    public void Settle(int slot)
    {
        if (slot <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot 0 is the rest pose and is never written.");
        }

        Span(_current, slot).CopyTo(Span(_previous, slot));
        _previousLift[slot] = _lift[slot];
        _previousSway[slot] = _sway[slot];
        _previousYaw[slot] = _yaw[slot];
    }

    /// <summary>
    /// Writes <paramref name="slot"/>'s pose <paramref name="alpha"/> of the way from its previous (0) to its current (1)
    /// into <paramref name="turns"/> (one per bone, for as many bones as it holds: its skeleton's), and returns its lift
    /// as far between; NaN when the current lift is, and the current lift when only the previous is.
    /// </summary>
    public double Blend(int slot, float alpha, Span<Rotation> turns)
    {
        var previous = Span(_previous, slot);
        var current = Span(_current, slot);
        for (var bone = 0; bone < turns.Length; bone++)
        {
            turns[bone] = Rotation.Blend(previous[bone], current[bone], alpha);
        }

        var (from, to) = (_previousLift[slot], _lift[slot]);
        return double.IsNaN(from) ? to : from + (to - from) * alpha;
    }

    private static Span<Rotation> Span(Rotation[] turns, int slot) => turns.AsSpan(slot * Bones, Bones);
}
