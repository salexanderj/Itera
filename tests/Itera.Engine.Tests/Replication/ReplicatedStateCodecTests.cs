using System.Numerics;
using Arch.Core;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Replication;

namespace Itera.Engine.Tests.Replication;

/// <summary>
/// Replicated state on the wire: each field reads back what was set within its step; a state goes as its changes from a
/// baseline (none for a lane that did not change); a damaged record is refused or read within its fields' ranges, never
/// as a value no field holds; the blend a client shows goes the short way round and steps whole numbers; a schema keeps
/// to its lanes and is sealed by its kind; and the kinds' rules and fingerprint.
/// </summary>
public sealed class ReplicatedStateCodecTests
{
    private static readonly (
        ReplicatedSchema Schema,
        PositionField At,
        AngleField Heading,
        RotationField Turn,
        ScalarField Speed,
        IntegerField Clip,
        FlagsField Flags
    ) s_fields = Fields();

    private static (
        ReplicatedSchema,
        PositionField,
        AngleField,
        RotationField,
        ScalarField,
        IntegerField,
        FlagsField
    ) Fields()
    {
        var schema = new ReplicatedSchema();
        var at = schema.AddPosition("at");
        var heading = schema.AddAngle("heading");
        var turn = schema.AddRotation("turn");
        var speed = schema.AddScalar("speed", -10, 10, 12);
        var clip = schema.AddInteger("clip", -1, 40);
        var flags = schema.AddFlags("flags", 5);
        return (schema, at, heading, turn, speed, clip, flags);
    }

    [Fact]
    public void Each_field_reads_back_what_was_set_within_its_step()
    {
        var (_, at, heading, turn, speed, clip, flags) = s_fields;
        var state = default(ReplicatedState);
        var random = new Random(4);
        for (var i = 0; i < 1000; i++)
        {
            var point = new WorldPoint(
                (random.NextDouble() - 0.5) * 300_000,
                (random.NextDouble() - 0.5) * 2000,
                (random.NextDouble() - 0.5) * 300_000
            );
            var angle = (float)((random.NextDouble() - 0.5) * 20);
            var rotation = Quaternion.Normalize(
                new Quaternion(
                    (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f
                )
            );
            var value = (float)((random.NextDouble() - 0.5) * 20);
            at.Set(ref state, in point);
            heading.Set(ref state, angle);
            turn.Set(ref state, rotation);
            speed.Set(ref state, value);
            clip.Set(ref state, i % 50 - 5);
            flags.Set(ref state, (uint)i);

            Assert.True(at.Get(in state).DistanceTo(point) <= Math.Sqrt(3) * 0.5 / PositionField.StepsPerMetre + 1e-9);
            var angleError = Math.IEEERemainder(heading.Get(in state) - angle, 2 * Math.PI);
            Assert.True(Math.Abs(angleError) <= Math.PI / AngleField.StepsPerTurn + 1e-6);
            // q and -q are one rotation: the angle between them is what counts.
            var dot = Math.Abs(Quaternion.Dot(turn.Get(in state), rotation));
            Assert.True(
                2 * Math.Acos(Math.Min(1, dot)) < 0.2 * Math.PI / 180,
                $"Rotation {rotation} off by more than 0.2 degrees."
            );
            Assert.True(Math.Abs(speed.Get(in state) - Math.Clamp(value, -10, 10)) <= 20f / 4095 / 2 + 1e-5);
            Assert.Equal(Math.Clamp(i % 50 - 5, -1, 40), clip.Get(in state));
            Assert.Equal((uint)i & 31, flags.Get(in state));
        }
    }

    [Fact]
    public void Values_that_are_not_finite_or_past_a_fields_reach_are_held_to_it()
    {
        var (_, at, heading, turn, speed, _, _) = s_fields;
        var state = default(ReplicatedState);

        at.Set(ref state, new WorldPoint(double.NaN, 1e12, -1e12));
        heading.Set(ref state, float.PositiveInfinity);
        turn.Set(ref state, new Quaternion(float.NaN, 0, 0, 1));
        speed.Set(ref state, float.NaN);

        var point = at.Get(in state);
        Assert.Equal(0, point.X);
        Assert.Equal(SessionProtocol.MaxPositionMetres, point.Y, 2);
        Assert.Equal(-SessionProtocol.MaxPositionMetres, point.Z, 2);
        Assert.Equal(0, heading.Get(in state));
        Assert.True(Quaternion.Dot(turn.Get(in state), Quaternion.Identity) > 0.9999f);
        Assert.Equal(-10, speed.Get(in state));
    }

    [Fact]
    public void A_state_goes_as_its_changes_from_its_baseline()
    {
        var (schema, at, heading, _, _, clip, _) = s_fields;
        var baseline = default(ReplicatedState);
        at.Set(ref baseline, new WorldPoint(1200.5, 20, -3400.25));
        heading.Set(ref baseline, 1.2f);
        clip.Set(ref baseline, 3);
        var moved = baseline;
        at.Set(ref moved, new WorldPoint(1200.55, 20, -3400.2));
        var writer = new MessageWriter();

        // Nothing changed: the mask alone, one byte.
        ReplicatedStateCodec.Write(writer, schema, in baseline, in baseline);
        Assert.Equal(1, writer.Length);
        // Two axes moved 5 cm (51 steps each): a mask byte and a byte each.
        writer.Reset();
        ReplicatedStateCodec.Write(writer, schema, in moved, in baseline);
        Assert.Equal(3, writer.Length);
        var reader = new MessageReader(writer.Written);
        ReplicatedStateCodec.Read(ref reader, schema, in baseline, out var read);
        reader.EnsureEnd();
        Assert.True(ReplicatedStateCodec.Same(schema, in read, in moved));

        // Whole: its changes from the zero state.
        writer.Reset();
        ReplicatedStateCodec.Write(writer, schema, in moved, in ReplicatedStateCodec.Zero);
        reader = new MessageReader(writer.Written);
        ReplicatedStateCodec.Read(ref reader, schema, in ReplicatedStateCodec.Zero, out read);
        reader.EnsureEnd();
        Assert.True(ReplicatedStateCodec.Same(schema, in read, in moved));
    }

    [Fact]
    public void Every_damaged_record_is_refused_or_read_within_its_fields_ranges()
    {
        var (schema, at, heading, turn, speed, clip, flags) = s_fields;
        var baseline = default(ReplicatedState);
        at.Set(ref baseline, new WorldPoint(10, 2, 30));
        var state = baseline;
        at.Set(ref state, new WorldPoint(12, 2.5, 29));
        heading.Set(ref state, -2);
        turn.Set(ref state, Quaternion.CreateFromYawPitchRoll(1, 0.2f, 0));
        speed.Set(ref state, 3);
        clip.Set(ref state, 7);
        flags.Set(ref state, 9);
        var writer = new MessageWriter();
        ReplicatedStateCodec.Write(writer, schema, in state, in baseline);
        var rows = HostileValues.Damaged("state", writer.Written.ToArray()).ToList();

        Watchdog.Run(
            rows.Count,
            i => rows[i].Label,
            i =>
            {
                var reader = new MessageReader(rows[i].Body);
                try
                {
                    ReplicatedStateCodec.Read(ref reader, schema, in baseline, out var read);
                    for (var lane = 0; lane < schema.Lanes; lane++)
                    {
                        Assert.True(schema.Holds(lane, read.Lane(lane)), $"Lane {lane} read {read.Lane(lane)}.");
                    }
                }
                catch (InvalidDataException) { }
            }
        );
    }

    [Fact]
    public void A_signed_variable_length_number_reads_back_and_one_past_64_bits_is_refused()
    {
        var writer = new MessageWriter();
        foreach (var value in (long[])[0, 1, -1, 63, -64, 64, long.MaxValue, long.MinValue, 123_456_789_012])
        {
            writer.Reset();
            writer.WriteVarLong(value);
            var reader = new MessageReader(writer.Written);
            Assert.Equal(value, reader.ReadVarLong());
            reader.EnsureEnd();
        }

        writer.Reset();
        writer.WriteVarLong(-1);
        Assert.Equal(1, writer.Length);

        var tooLong = Enumerable.Repeat((byte)0xFF, 9).Append((byte)0x02).ToArray();
        Assert.Throws<InvalidDataException>(() => new MessageReader(tooLong).ReadVarLong());
        var neverEnds = Enumerable.Repeat((byte)0x80, 11).ToArray();
        Assert.Throws<InvalidDataException>(() => new MessageReader(neverEnds).ReadVarLong());
    }

    [Fact]
    public void The_blend_goes_the_short_way_and_steps_whole_numbers()
    {
        var (schema, at, heading, turn, speed, clip, _) = s_fields;
        var from = default(ReplicatedState);
        var to = default(ReplicatedState);
        at.Set(ref from, new WorldPoint(0, 0, 0));
        at.Set(ref to, new WorldPoint(2, 4, -6));
        heading.Set(ref from, 3.0f);
        heading.Set(ref to, -3.0f);
        turn.Set(ref from, Quaternion.Identity);
        turn.Set(ref to, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1));
        speed.Set(ref from, 0);
        speed.Set(ref to, 8);
        clip.Set(ref from, 2);
        clip.Set(ref to, 9);

        ReplicatedStateCodec.Blend(schema, in from, in to, 0.25f, out var blended);

        Assert.True(at.Get(in blended).DistanceTo(new WorldPoint(0.5, 1, -1.5)) < 0.002);
        // From 3 to -3 radians the short way passes pi: a quarter of the way is 3 + 0.0708.
        var expected = 3 + 0.25 * (2 * Math.PI - 6);
        Assert.Equal(Math.IEEERemainder(expected, 2 * Math.PI), heading.Get(in blended), 3);
        var angle = 2 * Math.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(turn.Get(in blended), Quaternion.Identity))));
        Assert.Equal(0.25, angle, 2);
        Assert.Equal(2, speed.Get(in blended), 2);
        Assert.Equal(2, clip.Get(in blended));
        ReplicatedStateCodec.Blend(schema, in from, in to, 1, out blended);
        Assert.Equal(9, clip.Get(in blended));
    }

    [Fact]
    public void A_schema_keeps_to_its_lanes_and_its_kind_seals_it()
    {
        var schema = new ReplicatedSchema();
        for (var i = 0; i < 10; i++)
        {
            schema.AddPosition("at" + i);
        }

        schema.AddAngle("last");
        Assert.Equal(ReplicatedState.MostLanes, schema.Lanes);
        Assert.Throws<InvalidOperationException>(() => schema.AddAngle("one too many"));
        Assert.Throws<ArgumentException>(() => new ReplicatedSchema().AddScalar("empty", 1, 1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicatedSchema().AddFlags("none", 0));

        var kinds = new ReplicatedKinds();
        var sealedSchema = new ReplicatedSchema();
        sealedSchema.AddPosition("at");
        kinds.Register("thing", sealedSchema, new NoKind());
        Assert.Throws<InvalidOperationException>(() => sealedSchema.AddAngle("late"));
    }

    [Fact]
    public void Kinds_refuse_a_name_twice_and_registering_once_sealed_and_their_fingerprint_follows_their_fields()
    {
        static ulong FingerprintOf(Action<ReplicatedSchema> add)
        {
            var kinds = new ReplicatedKinds();
            var schema = new ReplicatedSchema();
            add(schema);
            kinds.Register("mover", schema, new NoKind(), relevanceMetres: 50, priority: 2);
            return kinds.Fingerprint;
        }

        var first = FingerprintOf(s => s.AddPosition("at"));
        Assert.Equal(first, FingerprintOf(s => s.AddPosition("at")));
        Assert.NotEqual(first, FingerprintOf(s => s.AddPosition("feet")));
        Assert.NotEqual(first, FingerprintOf(s => s.AddAngle("at")));
        Assert.NotEqual(FingerprintOf(s => s.AddScalar("v", 0, 1, 8)), FingerprintOf(s => s.AddScalar("v", 0, 2, 8)));
        Assert.Equal(0ul, new ReplicatedKinds().Fingerprint);

        var kinds = new ReplicatedKinds();
        kinds.Register("one", new ReplicatedSchema(), new NoKind());
        Assert.Throws<InvalidOperationException>(() => kinds.Register("one", new ReplicatedSchema(), new NoKind()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            kinds.Register("two", new ReplicatedSchema(), new NoKind(), priority: 0)
        );
        kinds.Seal();
        Assert.Throws<InvalidOperationException>(() => kinds.Register("three", new ReplicatedSchema(), new NoKind()));
    }

    [Fact]
    public void Predicted_parts_refuse_a_name_twice_too_many_bytes_and_registering_once_sealed_and_change_the_fingerprint()
    {
        var kinds = new ReplicatedKinds();
        Assert.Equal(0ul, kinds.Fingerprint);
        Assert.Equal("engine.climber", Assert.Single(kinds.Predicted).Name);

        kinds.RegisterPredicted(new Part("game.dash", 8));

        Assert.NotEqual(0ul, kinds.Fingerprint);
        Assert.Equal(43 + 8, kinds.PredictedBytes);
        var other = new ReplicatedKinds();
        other.RegisterPredicted(new Part("game.dash", 9));
        Assert.NotEqual(kinds.Fingerprint, other.Fingerprint);
        Assert.Throws<InvalidOperationException>(() => kinds.RegisterPredicted(new Part("game.dash", 4)));
        Assert.Throws<InvalidOperationException>(() => kinds.RegisterPredicted(new Part("engine.climber", 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            kinds.RegisterPredicted(new Part("game.big", ReplicatedKinds.MostPredictedBytes - 51 + 1))
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => kinds.RegisterPredicted(new Part("game.none", 0)));
        kinds.RegisterPredicted(new Part("game.rest", ReplicatedKinds.MostPredictedBytes - 51));
        kinds.Seal();
        Assert.Throws<InvalidOperationException>(() => kinds.RegisterPredicted(new Part("game.late", 1)));
    }

    /// <summary>
    /// A predicted player's state as the owner's block carries it reads back with its parts; one whose climb is in a
    /// state there is not, whose parts are another size than the run's, or whose part a game says no player can have, is
    /// refused; and one with a number that is not finite is not sent.
    /// </summary>
    [Fact]
    public void A_predicted_state_reads_back_with_its_parts_and_damaged_parts_are_refused()
    {
        var kinds = new ReplicatedKinds();
        kinds.RegisterPredicted(new Part("game.dash", 8));
        using var world = World.Create();
        var origin = new WorldOrigin();
        var player = world.Create(
            new Components.CharacterPosition { Feet = new Vector3(1, 2, 3) },
            new Components.CharacterVelocity { MetresPerSecond = new Vector3(0, -1, 4) },
            new Components.CharacterContact { Grounded = true },
            new Components.PlayerMode(),
            Components.CharacterSize.ForHeight(1.7f),
            new Components.EyeSmoothing(),
            new Components.Climber
            {
                State = Components.ClimbState.Climbing,
                Normal = new Vector2(0, 1),
                Target = new Vector3(1, 3, 2),
                Seconds = 0.25f,
                Regrab = 0.1f,
            }
        );
        var state = default(PredictedState);
        state.Capture(world, player, origin, kinds);
        var writer = new MessageWriter();
        state.Write(writer, kinds);
        var bytes = writer.Written.ToArray();

        var reader = new MessageReader(bytes);
        var read = PredictedState.Read(ref reader, kinds);

        Assert.False(read.Differs(in state, kinds));
        world.Get<Components.Climber>(player) = default;
        read.Restore(world, player, origin, kinds);
        var climber = world.Get<Components.Climber>(player);
        Assert.Equal(Components.ClimbState.Climbing, climber.State);
        Assert.Equal(new Vector3(1, 3, 2), climber.Target);
        Assert.Equal(0.25f, climber.Seconds);

        // The parts are the message's last bytes: the climber's 43 (its state the second), then the game's 8.
        var climbAt = bytes.Length - 51;
        var badClimb = (byte[])bytes.Clone();
        badClimb[climbAt + 1] = 9;
        Assert.Throws<InvalidDataException>(() => ReadState(badClimb, kinds));
        var badPart = (byte[])bytes.Clone();
        badPart[^1] = 0xFF;
        Assert.Throws<InvalidDataException>(() => ReadState(badPart, kinds));
        Assert.Throws<InvalidDataException>(() => ReadState(bytes, new ReplicatedKinds()));
        var nan = state;
        ((Span<byte>)nan.Parts)[43 + 7] = 0xFF;
        Assert.False(nan.CanWrite(kinds));
    }

    /// <summary>
    /// An owner's block whose body the character mover refuses (it throws for a radius past its widest, a radius or height
    /// of 0, or a negative step; and a mass of 0 or a body far taller than any game's) is not sent, and a client refuses
    /// one a host sends, where it ended the client's match at its next correction.
    /// </summary>
    [Theory]
    [InlineData(2.5f, 1.7f, 0.4f, 75f)]
    [InlineData(0f, 1.7f, 0.4f, 75f)]
    [InlineData(0.26f, 0f, 0f, 75f)]
    [InlineData(0.26f, 1.7f, -1f, 75f)]
    [InlineData(0.26f, 1.7f, 0.4f, 0f)]
    [InlineData(0.26f, 500f, 0.4f, 75f)]
    public void An_owners_block_with_a_body_the_mover_refuses_is_neither_sent_nor_read(
        float radius,
        float height,
        float step,
        float mass
    )
    {
        var kinds = new ReplicatedKinds();
        var state = default(PredictedState);
        state.Size = Components.CharacterSize.ForHeight(1.7f) with
        {
            RadiusMetres = radius,
            HeightMetres = height,
            StepMetres = step,
            MassKilograms = mass,
        };
        var writer = new MessageWriter();
        state.Write(writer, kinds);
        var bytes = writer.Written.ToArray();

        Assert.False(state.CanWrite(kinds));
        Assert.Throws<InvalidDataException>(() => ReadState(bytes, kinds));
    }

    private static PredictedState ReadState(byte[] bytes, ReplicatedKinds kinds)
    {
        var reader = new MessageReader(bytes);
        return PredictedState.Read(ref reader, kinds);
    }

    /// <summary>A test part of <paramref name="bytes"/> bytes: valid unless its last byte is 0xFF.</summary>
    private sealed class Part(string name, int bytes) : IPredictedState
    {
        public string Name => name;

        public int Bytes => bytes;

        public void Capture(World world, Entity entity, WorldOrigin origin, Span<byte> state) => state.Fill(7);

        public void Restore(World world, Entity entity, WorldOrigin origin, ReadOnlySpan<byte> state) { }

        public bool Differs(ReadOnlySpan<byte> host, ReadOnlySpan<byte> predicted) => !host.SequenceEqual(predicted);

        public bool IsValid(ReadOnlySpan<byte> state) => state[^1] != 0xFF;
    }

    private sealed class NoKind : IReplicatedKind
    {
        public void Capture(in ReplicationContext context, Entity entity, ref ReplicatedState state) { }

        public Entity Spawn(in ReplicationContext context, in ReplicatedState state) => Entity.Null;

        public void Apply(in ReplicationContext context, Entity entity, in ReplicatedState state) { }

        public void Despawn(in ReplicationContext context, Entity entity) { }
    }
}
