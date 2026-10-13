using System.Globalization;
using System.Text;

namespace Itera.Engine.Replication;

/// <summary>
/// What a replicated kind sends: its fields in order, each taking one lane of a <see cref="ReplicatedState"/> (a
/// position three), with the range a client accepts for each lane and how it is blended. Built once, at the run's
/// start, before the kind is registered (<see cref="ReplicatedKinds.Register"/>), which seals it. The first position
/// field, if any, is where the entity is for relevance (<see cref="Focus"/>).
/// </summary>
public sealed class ReplicatedSchema
{
    private readonly List<LaneRule> _lanes = [];
    private readonly StringBuilder _description = new();
    private bool _sealed;

    /// <summary>The lanes the fields take.</summary>
    public int Lanes => _lanes.Count;

    /// <summary>The first position field: where the entity is, for relevance; null when the kind has none.</summary>
    public PositionField? Focus { get; private set; }

    /// <summary>A position, three lanes of 1/1,024 m (<see cref="PositionField"/>).</summary>
    public PositionField AddPosition(string name)
    {
        var lane = Add(name, "position", LaneBlend.Linear, -PositionField.MostSteps, PositionField.MostSteps, 3);
        var field = new PositionField(lane);
        Focus ??= field;
        return field;
    }

    /// <summary>A turn about one axis (<see cref="AngleField"/>).</summary>
    public AngleField AddAngle(string name) =>
        new(Add(name, "angle", LaneBlend.Angle, 0, AngleField.StepsPerTurn - 1, 1));

    /// <summary>A rotation (<see cref="RotationField"/>).</summary>
    public RotationField AddRotation(string name) =>
        new(Add(name, "rotation", LaneBlend.Rotation, 0, RotationField.MostPacked, 1));

    /// <summary>A number from <paramref name="min"/> to <paramref name="max"/> in 2^<paramref name="bits"/> steps (1 to 24 bits).</summary>
    public ScalarField AddScalar(string name, float min, float max, int bits)
    {
        if (!float.IsFinite(min) || !float.IsFinite(max) || !(max > min))
        {
            throw new ArgumentException($"A scalar's range must be finite and its maximum above its minimum: {name}.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 24);
        var steps = (1 << bits) - 1;
        var detail = string.Create(CultureInfo.InvariantCulture, $"scalar {min:R} {max:R} {bits}");
        return new ScalarField(Add(name, detail, LaneBlend.Linear, 0, steps, 1), min, max, steps);
    }

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>, not blended.</summary>
    public IntegerField AddInteger(string name, int min, int max)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max);
        var detail = string.Create(CultureInfo.InvariantCulture, $"integer {min} {max}");
        return new IntegerField(Add(name, detail, LaneBlend.Step, 0, (long)max - min, 1), min, max);
    }

    /// <summary>Flags in <paramref name="bits"/> bits (1 to 32), not blended.</summary>
    public FlagsField AddFlags(string name, int bits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 32);
        var detail = string.Create(CultureInfo.InvariantCulture, $"flags {bits}");
        return new FlagsField(Add(name, detail, LaneBlend.Step, 0, (1L << bits) - 1, 1), bits);
    }

    /// <summary>What the fields are, in order, for the kinds' fingerprint: peers with other fields do not play together.</summary>
    internal string Description => _description.ToString();

    internal LaneBlend BlendOf(int lane) => _lanes[lane].Blend;

    /// <summary>True when <paramref name="value"/> is a value lane <paramref name="lane"/> may hold.</summary>
    internal bool Holds(int lane, long value) => value >= _lanes[lane].Min && value <= _lanes[lane].Max;

    /// <summary>No more fields: called when the kind is registered.</summary>
    internal void Seal() => _sealed = true;

    private int Add(string name, string detail, LaneBlend blend, long min, long max, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_sealed)
        {
            throw new InvalidOperationException($"The schema is registered; the field {name} comes too late.");
        }

        if (_lanes.Count + count > ReplicatedState.MostLanes)
        {
            throw new InvalidOperationException(
                $"A schema holds at most {ReplicatedState.MostLanes} lanes; the field {name} takes it past that."
            );
        }

        var first = _lanes.Count;
        for (var i = 0; i < count; i++)
        {
            _lanes.Add(new LaneRule(blend, min, max));
        }

        _description.Append(name).Append(':').Append(detail).Append(';');
        return first;
    }

    private readonly record struct LaneRule(LaneBlend Blend, long Min, long Max);
}
