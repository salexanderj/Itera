using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Physics;

namespace Itera.Engine.Replication;

/// <summary>
/// A client's own player, predicted, and the host's correction. The client moves its player at once from each step's
/// command (quantised first, so host and client move from the same numbers), keeps its last
/// <see cref="PredictionHistory"/> commands and the states they gave, and sends its last <see cref="CommandsPerReply"/>
/// commands with each reply. The host buffers a client's commands (<see cref="BufferedCommands"/> steps of them), and at
/// each step's start writes the next into that client's player (its slot's id) for the movement system to move it as any
/// player; a late command is covered by the last one without its jump. Each send's first packet to a client carries the
/// owner's block: the tick of the last command the host applied and the player's state after it. A client whose state at
/// that command differs (by more than <see cref="CorrectionMetres"/>, or on the ground, crouched, or climbing where the
/// host's is not) takes the host's state and runs its later commands again (<c>CharacterStepper</c>,
/// <see cref="Replaying"/> while it does); the jump this makes in what is drawn eases out (<c>EyeSmoothing.Correction</c>,
/// <see cref="CorrectionEase"/> of it kept a step; past <see cref="SnapMetres"/>, at once).
/// </summary>
public sealed partial class ReplicationService
{
    /// <summary>The commands each reply carries, newest first: one lost reply in seven costs nothing.</summary>
    internal const int CommandsPerReply = 8;

    /// <summary>The steps of commands and predicted states a client keeps: about two seconds, past any round trip it plays at.</summary>
    internal const int PredictionHistory = 128;

    /// <summary>How far a client's predicted feet may be from the host's before it is corrected, in metres.</summary>
    public const float CorrectionMetres = 0.01f;

    /// <summary>The share of a correction's jump in what is drawn kept each step: most of it is gone in a quarter second.</summary>
    public const float CorrectionEase = 0.85f;

    /// <summary>A correction's jump past this many metres is taken at once, not eased.</summary>
    public const float SnapMetres = 2;

    /// <summary>The steps of a client's commands a host holds before it applies them, against their jitter.</summary>
    internal const int BufferedCommands = 2;

    /// <summary>The steps of commands a host lets wait before it skips ahead to the newest less the buffer.</summary>
    private const int MostWaitingCommands = 8;

    /// <summary>The steps a host goes on with a client's last command when none arrives, before it stands it still.</summary>
    private const int MostMissedCommands = 30;

    /// <summary>
    /// The steps in a row a host may find no command waiting while the client's still arrive (they come later than the
    /// host takes them: the client stalled and dropped steps, or the delay grew) before it starts again from the newest
    /// less the buffer.
    /// </summary>
    private const int MostStarvedSteps = 3;

    /// <summary>The commands a host keeps of each client, by the client's tick.</summary>
    private const int CommandRing = 64;

    /// <summary>
    /// How far behind the host's step a client's shown tick may be (<see cref="ShownTickOf"/>): half a second, past any
    /// delay the game plays at, so a client cannot have its shots judged against a time long gone.
    /// </summary>
    public const int MostShownTicksBehind = 30;

    /// <summary>The steps of the tick a reply's shown ticks are in: 1/16 of a step.</summary>
    private const int ShownSteps = 16;

    /// <summary>
    /// The latest command tick a reply may name: past half of an int's range (about 414 days of steps at 60 Hz), so a
    /// command's tick plus the host's buffer never wraps past <see cref="int.MaxValue"/>.
    /// </summary>
    internal const int MostCommandTick = int.MaxValue / 2;

    private readonly PredictionEntry[] _history = new PredictionEntry[PredictionHistory];
    private Entity _predicted = Entity.Null;
    private int _lastCommandTick = -1;
    private bool _ownerPending;
    private int _ownerTick = -1;
    private PredictedState _ownerState;
    private PredictedState _ownerScratch;
    private long _corrections;
    private double _lastCorrectionMetres;

    // On a client, the host's tick shown this step (what it replicates is shown at it), -1 until its clock is known.
    private double _shownTick = -1;

    // On a host, the step's tick.
    private long _stepTick;

    /// <summary>True while a client runs its player's steps again after a correction: a game's sounds and effects skip them.</summary>
    public bool Replaying { get; private set; }

    /// <summary>On a client, the corrections the host's states have made so far.</summary>
    public long Corrections => _corrections;

    /// <summary>
    /// On a client, predicts <paramref name="player"/>, its own player (with the components <c>PlayerMovementSystem</c>
    /// moves by): its commands go to the host every step, and the host's states correct it. Nothing on a host or offline.
    /// </summary>
    public void Predict(Entity player)
    {
        if (_session.Role != SessionRole.Client)
        {
            return;
        }

        _predicted = player;
        _lastCommandTick = -1;
        Array.Clear(_history);
    }

    /// <summary>
    /// The start of a step (<c>ReplicationReceiveSystem</c>): on a client, its player's state checked against the host's
    /// and what the host replicates shown; on a host, each client's next command written into its player.
    /// </summary>
    internal void StepStart(ulong tick)
    {
        _stepTick = (long)tick;
        if (_session.Role == SessionRole.Host)
        {
            ApplyCommands();
        }
        else if (_session.Role == SessionRole.Client)
        {
            Reconcile((int)tick);
            ClientStep(tick);
        }
    }

    /// <summary>A command as a reply carries it: the move in 1/127ths, the look in 65,536ths of a turn and of half a turn up and down.</summary>
    internal static void WriteCommand(MessageWriter writer, in PlayerCommand command)
    {
        writer.WriteByte((byte)MoveSteps(command.Move.X));
        writer.WriteByte((byte)MoveSteps(command.Move.Y));
        var turn = TurnSteps(command.Yaw);
        writer.WriteByte((byte)turn);
        writer.WriteByte((byte)(turn >> 8));
        var pitch = PitchSteps(command.Pitch);
        writer.WriteByte((byte)pitch);
        writer.WriteByte((byte)(pitch >> 8));
        writer.WriteByte((byte)(command.Buttons & PlayerButtons.AllKnown));
        writer.WriteVarLong(command.Actions);
    }

    /// <summary>A command <see cref="WriteCommand"/> wrote; any bytes read as a command whose numbers are finite.</summary>
    internal static PlayerCommand ReadCommand(ref MessageReader reader)
    {
        var x = (sbyte)reader.ReadByte();
        var y = (sbyte)reader.ReadByte();
        var turn = (ushort)(reader.ReadByte() | (reader.ReadByte() << 8));
        var pitch = (short)(reader.ReadByte() | (reader.ReadByte() << 8));
        var buttons = (PlayerButtons)reader.ReadByte() & PlayerButtons.AllKnown;
        var actions = reader.ReadVarLong();
        if (actions < 0 || actions > uint.MaxValue)
        {
            throw new InvalidDataException("A command's actions are out of range.");
        }

        return new PlayerCommand
        {
            Move = new Vector2(MoveOf(x), MoveOf(y)),
            Yaw = YawOf(turn),
            Pitch = PitchOf(pitch),
            Buttons = buttons,
            Actions = (uint)actions,
        };
    }

    /// <summary>A command as the host will read it (<see cref="ReadCommand"/>): the client moves by exactly that.</summary>
    internal static void Quantise(ref PlayerCommand command)
    {
        command.Move = new Vector2(MoveOf(MoveSteps(command.Move.X)), MoveOf(MoveSteps(command.Move.Y)));
        command.Yaw = YawOf(TurnSteps(command.Yaw));
        command.Pitch = PitchOf(PitchSteps(command.Pitch));
        command.Buttons &= PlayerButtons.AllKnown;
    }

    private static sbyte MoveSteps(float value) =>
        float.IsFinite(value) ? (sbyte)Math.Clamp(MathF.Round(value * 127), -127, 127) : (sbyte)0;

    private static float MoveOf(sbyte steps) => Math.Max(steps, (sbyte)-127) / 127f;

    private static ushort TurnSteps(float yaw)
    {
        var steps = float.IsFinite(yaw) ? (long)Math.Round(yaw / (2 * Math.PI) * 65536) : 0;
        return (ushort)(((steps % 65536) + 65536) % 65536);
    }

    private static float YawOf(ushort turn) => (float)((turn >= 32768 ? turn - 65536 : turn) * (2 * Math.PI) / 65536);

    private static short PitchSteps(float pitch) =>
        float.IsFinite(pitch) ? (short)Math.Clamp(Math.Round(pitch / (Math.PI / 2) * 32767), -32767, 32767) : (short)0;

    private static float PitchOf(short steps) => (float)(Math.Max(steps, (short)-32767) * (Math.PI / 2) / 32767);

    /// <summary>
    /// On a client, before its player moves (<c>ReplicationReplySystem</c>): the step's command quantised in place and
    /// kept with its tick. Nothing while the player flies the debug camera (its feet follow the camera, not commands).
    /// </summary>
    private void RecordCommand(ulong tick)
    {
        if (!Predicting(out var world) || world.Get<PlayerMode>(_predicted).Flying)
        {
            return;
        }

        ref var command = ref world.Get<PlayerCommand>(_predicted);
        Quantise(ref command);
        ref var entry = ref _history[(int)(tick % PredictionHistory)];
        entry.Stored = (int)tick + 1;
        entry.Command = command;
        entry.ShownTick = _shownTick;
        entry.HasState = false;
        _lastCommandTick = (int)tick;
    }

    /// <summary>The reply's commands: the newest and those before it, newest first, up to <see cref="CommandsPerReply"/>.</summary>
    private void WriteCommands(ulong tick)
    {
        var count = 0;
        if (_lastCommandTick == (int)tick)
        {
            while (count < CommandsPerReply && _lastCommandTick - count >= 0)
            {
                var t = _lastCommandTick - count;
                if (_history[t % PredictionHistory].Stored != t + 1)
                {
                    break;
                }

                count++;
            }
        }

        _reply.WriteVarInt(count);
        if (count == 0)
        {
            return;
        }

        _reply.WriteVarInt(_lastCommandTick);
        for (var i = 0; i < count; i++)
        {
            WriteCommand(_reply, in _history[(_lastCommandTick - i) % PredictionHistory].Command);
        }

        // The host's tick each was made at the sight of, in 1/16 steps: the newest's, then each one's step back from the
        // one before it (-1 for a tick not known yet).
        var before = 0L;
        for (var i = 0; i < count; i++)
        {
            var shown = ShownOf(_history[(_lastCommandTick - i) % PredictionHistory].ShownTick);
            _reply.WriteVarLong(i == 0 ? shown : before - shown);
            before = shown;
        }
    }

    private static long ShownOf(double tick) => tick < 0 ? -1 : (long)Math.Round(tick * ShownSteps);

    /// <summary>On a host: a reply's commands, kept by the client's tick for <see cref="ApplyCommands"/>.</summary>
    private static void ReadCommands(ref MessageReader reader, HostClient? client)
    {
        var count = reader.ReadVarInt();
        if (count > CommandsPerReply)
        {
            throw new InvalidDataException("A reply carries more commands than a reply holds.");
        }

        if (count == 0)
        {
            return;
        }

        var newest = reader.ReadVarInt();
        if (newest < count - 1)
        {
            throw new InvalidDataException("A reply's commands start before the match.");
        }

        if (newest > MostCommandTick)
        {
            throw new InvalidDataException("A reply's commands are past any tick.");
        }

        Span<PlayerCommand> commands = stackalloc PlayerCommand[CommandsPerReply];
        for (var i = 0; i < count; i++)
        {
            commands[i] = ReadCommand(ref reader);
        }

        var shown = 0L;
        const long mostShown = (long)int.MaxValue * ShownSteps;
        for (var i = 0; i < count; i++)
        {
            // Compared both ways rather than by Math.Abs, which throws for long.MinValue.
            var value = reader.ReadVarLong();
            if (value < -mostShown || value > mostShown)
            {
                throw new InvalidDataException("A reply's shown tick is out of range.");
            }

            shown = i == 0 ? value : shown - value;
            if (shown < -1 || shown > (long)int.MaxValue * ShownSteps)
            {
                throw new InvalidDataException("A reply's shown tick is out of range.");
            }

            var tick = newest - i;
            // Kept by tick whether or not the host has passed it (a host that got ahead goes back for them), unless the
            // slot holds a newer one.
            if (client is not null && client.CommandTicks[tick % CommandRing] < tick + 1)
            {
                client.Commands[tick % CommandRing] = commands[i];
                client.ShownTicks[tick % CommandRing] = shown < 0 ? -1 : shown / (double)ShownSteps;
                client.CommandTicks[tick % CommandRing] = tick + 1;
                if (tick > client.NewestCommand)
                {
                    client.NewestCommand = tick;
                    client.Arrived = true;
                }

                client.FirstCommand = client.FirstCommand < 0 ? tick : Math.Min(client.FirstCommand, tick);
            }
        }
    }

    /// <summary>
    /// On a host, at a step's start: each client's next command, in its order, written into its player (its slot's id),
    /// which the movement system then moves. The host starts once <see cref="BufferedCommands"/> steps of commands wait
    /// beyond the first, so that many always wait; when more than <see cref="MostWaitingCommands"/> wait it skips ahead,
    /// and when the next is missing the last goes on without its jump and the game's actions (still after a while). When
    /// none has waited for <see cref="MostStarvedSteps"/> steps while the client's still arrive, the host has got ahead
    /// of them for good (the client stalled, or the delay grew), and it starts again from the newest less the buffer,
    /// taking those steps' commands again: the client is corrected once.
    /// </summary>
    private void ApplyCommands()
    {
        var world = _ecs.World;
        foreach (var client in _clients)
        {
            if (
                client is null
                || client.NewestCommand < 0
                || !_hostIndex.TryGetValue(PlayerIds.Of(client.Slot), out var index)
                || !world.IsAlive(_entities[index].Entity)
                || !world.Has<PlayerCommand>(_entities[index].Entity)
            )
            {
                continue;
            }

            if (client.NextCommand < 0)
            {
                // The first commands: start once the buffer's worth wait beyond the first.
                if (client.NewestCommand < client.FirstCommand + BufferedCommands)
                {
                    continue;
                }

                client.NextCommand = client.NewestCommand - BufferedCommands;
            }
            else if (
                client.NewestCommand - client.NextCommand > MostWaitingCommands
                || (client.Starved >= MostStarvedSteps && client.Arrived)
            )
            {
                client.NextCommand = Math.Max(0, client.NewestCommand - BufferedCommands);
                client.Starved = 0;
            }

            client.Arrived = false;
            var tick = client.NextCommand;
            PlayerCommand command;
            if (client.CommandTicks[tick % CommandRing] == tick + 1)
            {
                command = client.Commands[tick % CommandRing];
                client.Shown = client.ShownTicks[tick % CommandRing];
                client.Missed = 0;
                client.Starved = 0;
            }
            else
            {
                // The client's view went on a step while its command did not come.
                client.Shown = client.Shown < 0 ? -1 : client.Shown + 1;
                command = client.LastCommand;
                command.Buttons &= ~PlayerButtons.Jump;
                // A game's actions may be one-shot: only those that hold a level are repeated.
                command.Actions &= _kinds.HeldActions;
                if (++client.Missed > MostMissedCommands)
                {
                    command.Move = Vector2.Zero;
                    command.Buttons = PlayerButtons.None;
                }

                client.Starved++;
                client.MissedTotal++;
            }

            world.Get<PlayerCommand>(_entities[index].Entity) = command;
            client.LastCommand = command;
            client.NewestApplied = tick;
            client.NextCommand = tick + 1;
        }
    }

    /// <summary>
    /// The owner's block of a send's first packet to a client: whether there is one, then the tick of the last command
    /// the host applied for its player and the player's state after it.
    /// </summary>
    private void WriteOwner(HostClient client)
    {
        var world = _ecs.World;
        var present =
            client.NewestApplied >= 0
            && _hostIndex.TryGetValue(PlayerIds.Of(client.Slot), out var index)
            && world.IsAlive(_entities[index].Entity)
            && PredictedState.CanCapture(world, _entities[index].Entity);
        if (present)
        {
            _ownerScratch.Capture(world, _entities[_hostIndex[PlayerIds.Of(client.Slot)]].Entity, _origin, _kinds);
            // A state a message cannot carry (a number not finite, a place past its reach) is not sent.
            present = _ownerScratch.CanWrite(_kinds);
        }

        _packet.WriteByte(present ? (byte)1 : (byte)0);
        if (!present)
        {
            return;
        }

        _packet.WriteVarInt(client.NewestApplied);
        _ownerScratch.Write(_packet, _kinds);
    }

    /// <summary>On a client: a snapshot's owner's block, the newest kept for the next step's check.</summary>
    private void ReadOwner(ref MessageReader reader)
    {
        var flags = reader.ReadByte();
        if (flags > 1)
        {
            throw new InvalidDataException("A snapshot's owner's flags are unknown.");
        }

        if (flags == 0)
        {
            return;
        }

        var tick = reader.ReadVarInt();
        var state = PredictedState.Read(ref reader, _kinds);
        if (tick > _ownerTick)
        {
            _ownerTick = tick;
            _ownerState = state;
            _ownerPending = true;
        }
    }

    /// <summary>
    /// On a client, at a step's start: the state the last step left kept with its command; the host's newest state,
    /// when it differs from the one predicted at that command, taken and the later commands run again; and the drawn
    /// jump eased.
    /// </summary>
    private void Reconcile(int tick)
    {
        if (!Predicting(out var world))
        {
            _ownerPending = false;
            return;
        }

        ref var last = ref _history[(tick - 1 + PredictionHistory) % PredictionHistory];
        if (tick > 0 && last.Stored == tick && !last.HasState)
        {
            last.State.Capture(world, _predicted, _origin, _kinds);
            last.HasState = true;
        }

        if (_ownerPending && !world.Get<PlayerMode>(_predicted).Flying)
        {
            _ownerPending = false;
            Correct(world, tick);
        }

        ref var eye = ref world.Get<EyeSmoothing>(_predicted);
        eye.PreviousCorrection = eye.Correction;
        eye.Correction = eye.Correction.LengthSquared() < 1e-6f ? Vector3.Zero : eye.Correction * CorrectionEase;
    }

    private void Correct(World world, int tick)
    {
        ref var entry = ref _history[_ownerTick % PredictionHistory];
        if (entry.Stored != _ownerTick + 1 || !entry.HasState || !_ownerState.Differs(in entry.State, _kinds))
        {
            return;
        }

        var before = world.Get<CharacterPosition>(_predicted).Feet;
        var drawn = world.Get<EyeSmoothing>(_predicted);
        Replaying = true;
        _ownerState.Restore(world, _predicted, _origin, _kinds);
        // The eye's easing is the prediction's (the host does not send it), the drawn correction the client's now.
        var eye = entry.State.Eye;
        eye.Correction = drawn.Correction;
        eye.PreviousCorrection = drawn.PreviousCorrection;
        world.Get<EyeSmoothing>(_predicted) = eye;
        entry.State = _ownerState;
        entry.State.Eye = eye;
        var seconds = 1f / _fixedStepHz;
        for (var t = _ownerTick + 1; t < tick; t++)
        {
            ref var next = ref _history[t % PredictionHistory];
            if (next.Stored != t + 1)
            {
                break;
            }

            world.Get<PlayerCommand>(_predicted) = next.Command;
            _stepper.Step(_predicted, seconds);
            next.State.Capture(world, _predicted, _origin, _kinds);
            next.HasState = true;
        }

        Replaying = false;
        var after = world.Get<CharacterPosition>(_predicted).Feet;
        var jump = before - after;
        ref var drawnNow = ref world.Get<EyeSmoothing>(_predicted);
        drawnNow.Correction += jump;
        if (drawnNow.Correction.Length() > SnapMetres)
        {
            drawnNow.Correction = Vector3.Zero;
            drawnNow.PreviousCorrection = Vector3.Zero;
        }

        _corrections++;
        _lastCorrectionMetres = jump.Length();
    }

    /// <summary>True on a client predicting a live player that has the components a step moves.</summary>
    private bool Predicting(out World world)
    {
        world = _ecs.World;
        return _session.Role == SessionRole.Client
            && _predicted != Entity.Null
            && world.IsAlive(_predicted)
            && PredictedState.CanCapture(world, _predicted)
            && world.Has<PlayerCommand>(_predicted)
            && world.Has<MovementStats>(_predicted);
    }

    /// <summary>
    /// On a host during a step, the host's tick (with its fraction) at which slot <paramref name="slot"/>'s client showed
    /// what it replicates when it made the command its player moves by this step: where it saw the others when it aimed.
    /// A game judges that player's shots against where their targets were then. At most
    /// <see cref="MostShownTicksBehind"/> steps behind this step and never past it; -1 for a slot with no client, before
    /// its clock is known, and on a client or offline (where a game judges against now).
    /// </summary>
    public double ShownTickOf(int slot)
    {
        if (_session.Role != SessionRole.Host)
        {
            return -1;
        }

        foreach (var client in _clients)
        {
            if (client is not null && client.Slot == slot)
            {
                return client.Shown < 0 ? -1 : Math.Clamp(client.Shown, _stepTick - MostShownTicksBehind, _stepTick);
            }
        }

        return -1;
    }

    /// <summary>A step a client predicted: its command, the host's tick it was made at the sight of, and the state it gave once moved.</summary>
    private struct PredictionEntry
    {
        /// <summary>The step's tick plus one; 0 for none.</summary>
        public int Stored;
        public PlayerCommand Command;
        public double ShownTick;
        public bool HasState;
        public PredictedState State;
    }
}

/// <summary>
/// A player's movement state, what a step reads and writes of it: the feet (absolute), the velocity, the contact, the
/// mode, the size, the eye's easing, and the parts a run registers (<see cref="IPredictedState"/>: the engine's climber,
/// then a game's own), as their bytes. The host sends it to the player's client; the client keeps it for each predicted
/// step and takes the host's when they differ.
/// </summary>
internal struct PredictedState
{
    public WorldPoint Feet;
    public Vector3 Velocity;
    public CharacterContact Contact;
    public PlayerMode Mode;
    public CharacterSize Size;
    public EyeSmoothing Eye;
    public PredictedStateBytes Parts;

    /// <summary>True when the entity has every component a state holds (the parts' are each optional).</summary>
    public static bool CanCapture(World world, Entity entity) =>
        world.Has<CharacterPosition>(entity)
        && world.Has<CharacterVelocity>(entity)
        && world.Has<CharacterContact>(entity)
        && world.Has<PlayerMode>(entity)
        && world.Has<CharacterSize>(entity)
        && world.Has<EyeSmoothing>(entity);

    public void Capture(World world, Entity entity, WorldOrigin origin, ReplicatedKinds kinds)
    {
        Feet = origin.ToWorld(world.Get<CharacterPosition>(entity).Feet);
        Velocity = world.Get<CharacterVelocity>(entity).MetresPerSecond;
        Contact = world.Get<CharacterContact>(entity);
        Mode = world.Get<PlayerMode>(entity);
        Size = world.Get<CharacterSize>(entity);
        Eye = world.Get<EyeSmoothing>(entity);
        Span<byte> parts = Parts;
        var at = 0;
        var predicted = kinds.Predicted;
        for (var i = 0; i < predicted.Count; i++)
        {
            var part = predicted[i];
            part.Capture(world, entity, origin, parts.Slice(at, part.Bytes));
            at += part.Bytes;
        }
    }

    /// <summary>Sets the entity to this state (not the eye's easing, which the caller sets); its feet start the next step here.</summary>
    public readonly void Restore(World world, Entity entity, WorldOrigin origin, ReplicatedKinds kinds)
    {
        ref var position = ref world.Get<CharacterPosition>(entity);
        position.Feet = origin.ToLocal(Feet);
        position.PreviousFeet = position.Feet;
        world.Get<CharacterVelocity>(entity).MetresPerSecond = Velocity;
        world.Get<CharacterContact>(entity) = Contact;
        world.Get<PlayerMode>(entity) = Mode;
        world.Get<CharacterSize>(entity) = Size;
        ReadOnlySpan<byte> parts = Parts;
        var at = 0;
        var predicted = kinds.Predicted;
        for (var i = 0; i < predicted.Count; i++)
        {
            var part = predicted[i];
            part.Restore(world, entity, origin, parts.Slice(at, part.Bytes));
            at += part.Bytes;
        }
    }

    /// <summary>True when every number is finite and every place within a message's reach (<see cref="Write"/> takes it).</summary>
    public readonly bool CanWrite(ReplicatedKinds kinds) =>
        Within(Feet)
        && Finite(Velocity)
        && float.IsFinite(Contact.Downhill.X)
        && float.IsFinite(Contact.Downhill.Y)
        && float.IsFinite(Contact.WallNormal.X)
        && float.IsFinite(Contact.WallNormal.Y)
        && SizeFits(in Size)
        && PartsValid(kinds);

    /// <summary>The tallest body an owner's block may carry, in metres: past any game's, short of what the mover reads slowly.</summary>
    internal const float MostHeightMetres = 100;

    /// <summary>
    /// True when the character mover takes a body of <paramref name="size"/> (it throws for a radius past
    /// <see cref="VoxelCharacterMover.MaxRadiusMetres"/>, a height or radius of 0, or a negative step), its height and eye
    /// within <see cref="MostHeightMetres"/>, and its mass above 0: what a host may send and a client takes.
    /// </summary>
    private static bool SizeFits(in CharacterSize size) =>
        size.RadiusMetres > 0
        && size.RadiusMetres <= VoxelCharacterMover.MaxRadiusMetres
        && size.HeightMetres > 0
        && size.HeightMetres <= MostHeightMetres
        && size.EyeMetres >= 0
        && size.EyeMetres <= MostHeightMetres
        && size.StepMetres >= 0
        && size.StepMetres <= size.HeightMetres
        && size.MassKilograms > 0
        && size.MassKilograms <= 1e9f;

    private readonly bool PartsValid(ReplicatedKinds kinds)
    {
        ReadOnlySpan<byte> parts = Parts;
        var at = 0;
        var predicted = kinds.Predicted;
        for (var i = 0; i < predicted.Count; i++)
        {
            var part = predicted[i];
            if (!part.IsValid(parts.Slice(at, part.Bytes)))
            {
                return false;
            }

            at += part.Bytes;
        }

        return true;
    }

    private static bool Within(in WorldPoint point) =>
        point.IsFinite
        && Math.Abs(point.X) < SessionProtocol.MaxPositionMetres
        && Math.Abs(point.Y) < SessionProtocol.MaxPositionMetres
        && Math.Abs(point.Z) < SessionProtocol.MaxPositionMetres;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// True when the host's state and a predicted one differ enough to correct: the feet more than
    /// <see cref="ReplicationService.CorrectionMetres"/> apart, one on the ground or crouched where the other is not, or a
    /// part that says so (the engine's: a climb in another state).
    /// </summary>
    public readonly bool Differs(in PredictedState other, ReplicatedKinds kinds)
    {
        if (
            Feet.DistanceTo(other.Feet) > ReplicationService.CorrectionMetres
            || Contact.Grounded != other.Contact.Grounded
            || Mode.Crouching != other.Mode.Crouching
        )
        {
            return true;
        }

        ReadOnlySpan<byte> host = Parts;
        ReadOnlySpan<byte> predicted = other.Parts;
        var at = 0;
        var parts = kinds.Predicted;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part.Differs(host.Slice(at, part.Bytes), predicted.Slice(at, part.Bytes)))
            {
                return true;
            }

            at += part.Bytes;
        }

        return false;
    }

    public readonly void Write(MessageWriter writer, ReplicatedKinds kinds)
    {
        writer.WritePosition(in Feet);
        Vector(writer, Velocity);
        writer.WriteByte(
            (byte)(
                (Contact.Grounded ? 1 : 0)
                | (Contact.Steep ? 2 : 0)
                | (Contact.Waiting ? 4 : 0)
                | (Contact.Jumped ? 8 : 0)
                | (Mode.Flying ? 16 : 0)
                | (Mode.Crouching ? 32 : 0)
            )
        );
        writer.WriteFloat(Contact.Downhill.X);
        writer.WriteFloat(Contact.Downhill.Y);
        writer.WriteFloat(Contact.WallNormal.X);
        writer.WriteFloat(Contact.WallNormal.Y);
        writer.WriteByte(Contact.GroundMaterial.Value);
        writer.WriteInt((int)Contact.GroundTags);
        writer.WriteFloat(Size.HeightMetres);
        writer.WriteFloat(Size.RadiusMetres);
        writer.WriteFloat(Size.EyeMetres);
        writer.WriteFloat(Size.StepMetres);
        writer.WriteFloat(Size.MassKilograms);
        ReadOnlySpan<byte> parts = Parts;
        writer.WriteVarInt(kinds.PredictedBytes);
        writer.WriteBytes(parts[..kinds.PredictedBytes]);
    }

    /// <summary>
    /// A state <see cref="Write"/> wrote; a number that is not finite, or past any a game moves by (a speed of 100 km/s, a
    /// body of 10 km or 10^9 kg), parts of another size than this run's, or a part that is not one a player can have
    /// (<see cref="IPredictedState.IsValid"/>), is refused.
    /// </summary>
    public static PredictedState Read(ref MessageReader reader, ReplicatedKinds kinds)
    {
        var state = default(PredictedState);
        state.Feet = reader.ReadPosition();
        state.Velocity = new Vector3(Finite(ref reader, 1e5f), Finite(ref reader, 1e5f), Finite(ref reader, 1e5f));
        var flags = reader.ReadByte();
        if (flags > 63)
        {
            throw new InvalidDataException("A player's state has flags that are not known.");
        }

        state.Contact.Grounded = (flags & 1) != 0;
        state.Contact.Steep = (flags & 2) != 0;
        state.Contact.Waiting = (flags & 4) != 0;
        state.Contact.Jumped = (flags & 8) != 0;
        state.Mode.Flying = (flags & 16) != 0;
        state.Mode.Crouching = (flags & 32) != 0;
        state.Contact.Downhill = new Vector2(Finite(ref reader, 2), Finite(ref reader, 2));
        state.Contact.WallNormal = new Vector2(Finite(ref reader, 2), Finite(ref reader, 2));
        state.Contact.GroundMaterial = new PhysicsMaterialId(reader.ReadByte());
        state.Contact.GroundTags = (uint)reader.ReadInt();
        state.Size.HeightMetres = Positive(ref reader);
        state.Size.RadiusMetres = Positive(ref reader);
        state.Size.EyeMetres = Positive(ref reader);
        state.Size.StepMetres = Finite(ref reader, 1e4f);
        state.Size.MassKilograms = Positive(ref reader);
        if (!SizeFits(in state.Size))
        {
            // A size the mover would throw on at the correction's step would end the client's match.
            throw new InvalidDataException("A player's size is out of range.");
        }

        if (reader.ReadVarInt() != kinds.PredictedBytes)
        {
            throw new InvalidDataException("A player's state has parts of another size than this game's.");
        }

        Span<byte> parts = state.Parts;
        reader.ReadBytes(kinds.PredictedBytes).CopyTo(parts);
        if (!state.PartsValid(kinds))
        {
            throw new InvalidDataException("A player's state has a part a player cannot have.");
        }

        return state;
    }

    private static void Vector(MessageWriter writer, Vector3 value)
    {
        writer.WriteFloat(value.X);
        writer.WriteFloat(value.Y);
        writer.WriteFloat(value.Z);
    }

    private static float Finite(ref MessageReader reader, float most)
    {
        var value = reader.ReadFloat();
        return float.IsFinite(value) && Math.Abs(value) <= most
            ? value
            : throw new InvalidDataException("A player's state holds a number out of range.");
    }

    private static float Positive(ref MessageReader reader)
    {
        var value = reader.ReadFloat();
        return float.IsFinite(value) && value >= 0 && value <= 1e9f
            ? value
            : throw new InvalidDataException("A player's size is out of range.");
    }
}
