using System.Globalization;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Matches;
using Itera.Engine.Networking;

namespace Itera.Engine.Replication;

/// <summary>The client's side: the host's snapshots read into each entity's states, shown blended a little behind the host.</summary>
public sealed partial class ReplicationService
{
    /// <summary>The states a client keeps of each entity (about two seconds at 30 sends a second), oldest first.</summary>
    internal const int ClientHistory = 64;

    /// <summary>
    /// How fast the client's estimate of the host's tick falls back, in ticks a step, so a delay that grew is followed: a
    /// packet that arrives sooner than the estimate says sets it at once.
    /// </summary>
    private const double ClockFallPerStep = 0.01;

    /// <summary>The most entities a client shows: a host that sends more is broken or hostile, and is refused.</summary>
    internal const int MostShown = 65536;

    /// <summary>The most removed ids a client remembers (64 packets of 64): more is a broken or hostile host.</summary>
    private const int MostRemembered = 16384;

    /// <summary>The ticks of the last snapshots read whole, for the gap rule (<see cref="PacketBetween"/>).</summary>
    private const int PacketTicksKept = 128;

    private readonly List<ShownEntity> _shown = [];
    private readonly Stack<int> _freeShown = new();
    private readonly Dictionary<uint, int> _shownIndex = [];
    private readonly Dictionary<uint, uint> _removedAt = [];
    private readonly List<Despawn> _despawns = [];
    private readonly List<int> _arrivals = [];
    private readonly MessageWriter _reply = new();
    private readonly int[] _packetTicks = new int[PacketTicksKept];
    private int _packetTicksCount;
    private int _packetTicksNext;
    private ReplicatedState _decoded;
    private ReplicatedState _from;
    private ReplicatedState _shownState;
    private uint _newestPacket;
    private ulong _receivedBits;
    private double _clockOffset;
    private bool _clockKnown;
    private long _snapshots;
    private long _unusable;

    // The watch on snapshots: the count it last saw, the steps since it rose, whether a reply went out (the host sends
    // nothing before), and whether the silence was reported.
    private long _snapshotsSeen;
    private int _stepsWithoutSnapshot;
    private bool _replied;
    private bool _silent;

    /// <summary>The entities a client shows (or has states of and will show).</summary>
    public int Shown => _shownIndex.Count;

    /// <summary>
    /// On a client, the seconds of its steps since it last read a snapshot, counted from its first reply; 0 on a host,
    /// offline, and while snapshots arrive. Past <see cref="SilenceSeconds"/> the host's packets are not reaching it.
    /// </summary>
    public double SecondsWithoutSnapshots => _stepsWithoutSnapshot / (double)_fixedStepHz;

    /// <summary>On a client: the host's tick less the client's, as the newest packets say; 0 before the first.</summary>
    public double ClockOffsetTicks => _clockOffset;

    void IReplicationEndpoint.ReceiveSnapshot(ReadOnlySpan<byte> body)
    {
        var reader = new MessageReader(body);
        var sequence = (uint)reader.ReadVarInt();
        var tick = reader.ReadVarInt();
        if (sequence == 0)
        {
            throw new InvalidDataException("A snapshot without a number.");
        }

        ReadOwner(ref reader);

        var removals = reader.ReadVarInt();
        if (removals > MostRemovalsPerPacket)
        {
            throw new InvalidDataException("A snapshot removes more ids than a packet holds.");
        }

        for (var i = 0; i < removals; i++)
        {
            RemoveShown((uint)reader.ReadVarInt(), sequence);
        }

        var usable = true;
        while (reader.Remaining > 0)
        {
            var id = (uint)reader.ReadVarInt();
            var kindValue = reader.ReadByte();
            if (kindValue >= _kinds.Count)
            {
                throw new InvalidDataException("A snapshot holds an entity of a kind that is not registered.");
            }

            var kind = _kinds[new ReplicatedKindId(kindValue)];
            var age = reader.ReadVarInt();
            if (age > tick)
            {
                throw new InvalidDataException("A snapshot's baseline is before the match started.");
            }

            var generation = age == 0 ? (uint)reader.ReadVarInt() : 0;
            var known = _shownIndex.TryGetValue(id, out var index);
            // A record older than the id's removal is late: the entity is gone (or came back whole since).
            var stale = _removedAt.TryGetValue(id, out var removedIn) && sequence < removedIn;
            if (known && age == 0 && _shown[index].Generation != generation && !stale)
            {
                if (_shown[index].LastPacket > sequence)
                {
                    // A late record of an earlier generation than the one shown.
                    ReplicatedStateCodec.Skip(ref reader, kind.Schema);
                    continue;
                }

                // The id was added again on the host (a player who came back): the old one goes, the new one is made.
                Forget(index);
                known = false;
            }

            if (known && _shown[index].Kind != kind)
            {
                throw new InvalidDataException("A snapshot gives an entity another kind.");
            }

            if (age == 0)
            {
                _from = default;
            }
            else if (stale || !known || !_shown[index].TryGet(tick - age, ref _from))
            {
                // Its baseline is not here: the packet is not acknowledged, so the host sends the entity again.
                ReplicatedStateCodec.Skip(ref reader, kind.Schema);
                usable &= stale;
                continue;
            }

            if (stale)
            {
                ReplicatedStateCodec.Skip(ref reader, kind.Schema);
                continue;
            }

            ReplicatedStateCodec.Read(ref reader, kind.Schema, in _from, out _decoded);
            if (!known)
            {
                if (_shownIndex.Count >= MostShown)
                {
                    throw new InvalidDataException($"The host sends more than {MostShown} entities.");
                }

                index = NewShown(id, kind, generation);
            }

            _shown[index].Keep(tick, in _decoded, sequence);
        }

        _snapshots++;
        if (!usable)
        {
            _unusable++;
            return;
        }

        Received(sequence);
        _arrivals.Add(tick);
        _packetTicks[_packetTicksNext] = tick;
        _packetTicksNext = (_packetTicksNext + 1) % PacketTicksKept;
        _packetTicksCount = Math.Min(_packetTicksCount + 1, PacketTicksKept);
    }

    /// <summary>
    /// True when a snapshot of a tick after <paramref name="after"/> and before <paramref name="before"/> was read: an
    /// entity it did not carry was unchanged then (or waited for the budget), not lost.
    /// </summary>
    private bool PacketBetween(int after, int before)
    {
        for (var i = 0; i < _packetTicksCount; i++)
        {
            var tick = _packetTicks[i];
            if (tick > after && tick < before)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A client's step (<c>ReplicationReceiveSystem</c>): the entities the host removed go, its tick is estimated, and each
    /// entity is shown at that tick less the interpolation delay: made the first time (the kind's
    /// <see cref="IReplicatedKind.Spawn"/>), then set each step (<see cref="IReplicatedKind.Apply"/>).
    /// </summary>
    internal void ClientStep(ulong tick)
    {
        if (_session.Role != SessionRole.Client)
        {
            return;
        }

        WatchSnapshots();
        var context = Context;
        foreach (var despawn in _despawns)
        {
            if (context.World.IsAlive(despawn.Entity))
            {
                despawn.Kind.Handler.Despawn(in context, despawn.Entity);
            }
        }

        _despawns.Clear();
        FollowClock(tick);
        if (!_clockKnown)
        {
            _shownTick = -1;
            return;
        }

        var shownAt = tick + _clockOffset - (double)InterpolationMilliseconds * _fixedStepHz / 1000;
        _shownTick = shownAt;
        foreach (var shown in _shown)
        {
            if (
                !shown.Live
                || shown.Unshown
                || !shown.Blend(shownAt, SendIntervalTicks, this, ref _from, ref _shownState)
            )
            {
                continue;
            }

            if (shown.Entity == Entity.Null)
            {
                var entity = shown.Kind.Handler.Spawn(in context, in _shownState);
                if (entity == Entity.Null || !context.World.IsAlive(entity))
                {
                    shown.Unshown = true;
                    continue;
                }

                var mark = new Replicated { Id = shown.Id, Kind = shown.Kind.Id.Value };
                if (context.World.Has<Replicated>(entity))
                {
                    context.World.Set(entity, mark);
                }
                else
                {
                    context.World.Add(entity, mark);
                }

                shown.Entity = entity;
            }
            else if (!context.World.IsAlive(shown.Entity))
            {
                continue;
            }

            shown.Kind.Handler.Apply(in context, shown.Entity, in _shownState);
        }

        if (tick % 64 == 0)
        {
            ForgetOldRemovals();
        }
    }

    /// <summary>
    /// A client's reply (<c>ReplicationReplySystem</c>, every step after the input, before the move): the newest snapshot it
    /// read and which of the 64 before it arrived, so the host knows which states it holds; whether its match still loads
    /// under its loading screen (1) or plays (0); and its own player's last commands, this step's quantised in place and
    /// kept first.
    /// </summary>
    internal void SendReply(ulong tick)
    {
        if (_session.Role != SessionRole.Client)
        {
            return;
        }

        RecordCommand(tick);
        _reply.Reset();
        _reply.WriteByte((byte)MessageKind.Reply);
        _reply.WriteVarInt((int)_newestPacket);
        _reply.WriteULong(_receivedBits);
        _reply.WriteByte(_matches is { Phase: not MatchPhase.Playing } ? (byte)1 : (byte)0);
        WriteCommands(tick);
        _replied |= _session.SendEngineToHost(_reply.Written);
    }

    /// <summary>
    /// Counts the steps since a snapshot was read, from the first reply on, and logs once when they pass
    /// <see cref="SilenceSeconds"/> (with the connection's state, while it is still open) and once when snapshots come
    /// again. Counted in steps, so a step the client itself was late for counts once.
    /// </summary>
    private void WatchSnapshots()
    {
        if (_snapshots != _snapshotsSeen)
        {
            _snapshotsSeen = _snapshots;
            if (_silent)
            {
                _silent = false;
                LogSnapshotsAgain(SecondsWithoutSnapshots);
            }

            _stepsWithoutSnapshot = 0;
            return;
        }

        if (!_replied)
        {
            return;
        }

        _stepsWithoutSnapshot++;
        if (!_silent && _stepsWithoutSnapshot >= SilenceSeconds * _fixedStepHz)
        {
            _silent = true;
            var connection = Described(_session.DescribeConnection(0));
            LogNoSnapshots(SecondsWithoutSnapshots, connection);
        }
    }

    /// <summary>The estimate of the host's tick: the earliest-arriving packets set it, and it falls slowly between them.</summary>
    private void FollowClock(ulong tick)
    {
        if (_clockKnown)
        {
            _clockOffset -= ClockFallPerStep;
        }

        foreach (var hostTick in _arrivals)
        {
            var sample = hostTick - (double)tick;
            _clockOffset = _clockKnown ? Math.Max(_clockOffset, sample) : sample;
            _clockKnown = true;
        }

        _arrivals.Clear();
    }

    /// <summary>Packet <paramref name="sequence"/> was read whole: the next reply says so.</summary>
    private void Received(uint sequence)
    {
        if (_newestPacket == 0)
        {
            _newestPacket = sequence;
            return;
        }

        if (sequence > _newestPacket)
        {
            var shift = sequence - _newestPacket;
            _receivedBits = shift >= 64 ? 0 : _receivedBits << (int)shift;
            if (shift <= 64)
            {
                _receivedBits |= 1ul << (int)(shift - 1);
            }

            _newestPacket = sequence;
        }
        else if (sequence < _newestPacket && _newestPacket - sequence <= 64)
        {
            _receivedBits |= 1ul << (int)(_newestPacket - sequence - 1);
        }
    }

    /// <summary>The host removed <paramref name="id"/> in packet <paramref name="sequence"/>: unless a later packet showed it since.</summary>
    private void RemoveShown(uint id, uint sequence)
    {
        if (_shownIndex.TryGetValue(id, out var index))
        {
            var shown = _shown[index];
            if (shown.LastPacket > sequence)
            {
                return;
            }

            Forget(index);
        }

        if (_removedAt.Count >= MostRemembered && !_removedAt.ContainsKey(id))
        {
            throw new InvalidDataException($"The host removes more than {MostRemembered} ids at once.");
        }

        _removedAt[id] = _removedAt.TryGetValue(id, out var before) ? Math.Max(before, sequence) : sequence;
    }

    /// <summary>The shown entity at <paramref name="index"/> goes: its entity is removed at the next step.</summary>
    private void Forget(int index)
    {
        var shown = _shown[index];
        if (shown.Entity != Entity.Null)
        {
            _despawns.Add(new Despawn(shown.Entity, shown.Kind));
        }

        shown.Live = false;
        _shownIndex.Remove(shown.Id);
        _freeShown.Push(index);
    }

    private int NewShown(uint id, ReplicatedKind kind, uint generation)
    {
        if (!_freeShown.TryPop(out var index))
        {
            index = _shown.Count;
            _shown.Add(new ShownEntity());
        }

        _shown[index].Start(id, kind, generation);
        _shownIndex[id] = index;
        return index;
    }

    /// <summary>Removals older than every packet still in flight cannot meet a late record any more.</summary>
    private void ForgetOldRemovals()
    {
        foreach (var (id, sequence) in _removedAt)
        {
            if (_newestPacket > PacketRing && sequence < _newestPacket - PacketRing)
            {
                _removedAt.Remove(id);
            }
        }
    }

    private void PrintClient(CommandContext context)
    {
        var millisecondsPerTick = 1000.0 / _fixedStepHz;
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Showing {_shownIndex.Count} entities {InterpolationMilliseconds} ms behind the host; {_snapshots} snapshots read ({_unusable} without their baselines), newest {_newestPacket}; the host's clock {_clockOffset * millisecondsPerTick:F0} ms ahead of this one; {_corrections} corrections of this player, the last {_lastCorrectionMetres * 100:F1} cm."
            )
        );
    }

    /// <summary>An entity a client shows: its id, kind, entity, and its last <see cref="ClientHistory"/> states by tick, oldest first.</summary>
    private sealed class ShownEntity
    {
        private readonly int[] _ticks = new int[ClientHistory];
        private long[] _lanes = [];
        private int _lanesEach;
        private int _count;

        public uint Id;
        public ReplicatedKind Kind = null!;
        public Entity Entity;
        public bool Live;

        /// <summary>The add of its id on the host it shows (whole records carry it).</summary>
        public uint Generation;

        /// <summary>The game's kind declined to make it (<see cref="IReplicatedKind.Spawn"/> gave none).</summary>
        public bool Unshown;

        /// <summary>The newest packet that carried a state of it.</summary>
        public uint LastPacket;

        public void Start(uint id, ReplicatedKind kind, uint generation)
        {
            Id = id;
            Kind = kind;
            Generation = generation;
            Entity = Entity.Null;
            Live = true;
            Unshown = false;
            LastPacket = 0;
            _count = 0;
            _lanesEach = kind.Schema.Lanes;
            if (_lanes.Length != ClientHistory * _lanesEach)
            {
                _lanes = new long[ClientHistory * _lanesEach];
            }
        }

        /// <summary>Keeps a state in tick order; with the history full, the oldest goes (or this one, if older still).</summary>
        public void Keep(int tick, in ReplicatedState state, uint packet)
        {
            LastPacket = Math.Max(LastPacket, packet);
            var at = _count;
            while (at > 0 && _ticks[at - 1] > tick)
            {
                at--;
            }

            if (at > 0 && _ticks[at - 1] == tick)
            {
                Write(at - 1, in state);
                return;
            }

            if (_count == ClientHistory)
            {
                if (at == 0)
                {
                    return;
                }

                // The oldest goes: everything before the new one moves down a place, which frees the place before it.
                Move(1, 0, at - 1);
                at--;
            }
            else
            {
                Move(at, at + 1, _count - at);
                _count++;
            }

            _ticks[at] = tick;
            Write(at, in state);
        }

        public bool TryGet(int tick, ref ReplicatedState state)
        {
            for (var i = _count - 1; i >= 0; i--)
            {
                if (_ticks[i] == tick)
                {
                    Read(i, ref state);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The state to show at tick <paramref name="at"/> (fractional): before the first, the first; past the newest, the
        /// newest; between two, blended, but after a gap longer than two sends in which other snapshots were read, the
        /// move takes the last <paramref name="sendTicks"/> before the later one (the entity waited, unchanged; a gap with
        /// no snapshot read was loss, and is blended straight).
        /// </summary>
        public bool Blend(
            double at,
            int sendTicks,
            ReplicationService owner,
            ref ReplicatedState scratch,
            ref ReplicatedState result
        )
        {
            if (_count == 0)
            {
                return false;
            }

            var before = _count - 1;
            while (before >= 0 && _ticks[before] > at)
            {
                before--;
            }

            if (before < 0 || before == _count - 1)
            {
                Read(Math.Max(before, 0), ref result);
                return true;
            }

            double t0 = _ticks[before],
                t1 = _ticks[before + 1];
            var waited = t1 - t0 > 2 * sendTicks && owner.PacketBetween(_ticks[before], _ticks[before + 1]);
            var start = waited ? t1 - sendTicks : t0;
            Read(before, ref scratch);
            if (at <= start)
            {
                result = scratch;
                return true;
            }

            var later = default(ReplicatedState);
            Read(before + 1, ref later);
            var share = (float)((at - start) / (t1 - start));
            ReplicatedStateCodec.Blend(Kind.Schema, in scratch, in later, share, out result);
            return true;
        }

        private void Move(int from, int to, int count)
        {
            if (count <= 0)
            {
                return;
            }

            Array.Copy(_ticks, from, _ticks, to, count);
            Array.Copy(_lanes, from * _lanesEach, _lanes, to * _lanesEach, count * _lanesEach);
        }

        private void Write(int slot, in ReplicatedState state)
        {
            for (var lane = 0; lane < _lanesEach; lane++)
            {
                _lanes[slot * _lanesEach + lane] = state.Lanes[lane];
            }
        }

        private void Read(int slot, ref ReplicatedState state)
        {
            state = default;
            for (var lane = 0; lane < _lanesEach; lane++)
            {
                state.Lanes[lane] = _lanes[slot * _lanesEach + lane];
            }
        }
    }

    private readonly record struct Despawn(Entity Entity, ReplicatedKind Kind);
}
