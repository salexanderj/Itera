using System.Globalization;
using System.Runtime.InteropServices;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Maths;
using Itera.Engine.Networking;

namespace Itera.Engine.Replication;

/// <summary>The host's side: the entities it replicates, each client's view of them, and the packets it sends.</summary>
public sealed partial class ReplicationService
{
    /// <summary>
    /// The states a host keeps of each entity, one for each change (a second of changes at 30 sends a second; a state
    /// that does not change stays as long as it lasts): a baseline older than these is not used, and the state goes whole.
    /// </summary>
    internal const int HostHistory = 32;

    /// <summary>The packets a host remembers for each client, to know what an acknowledgement covers.</summary>
    private const int PacketRing = 64;

    /// <summary>The most ids one packet removes; more wait for the next.</summary>
    private const int MostRemovalsPerPacket = 64;

    /// <summary>The most packets one send to a client holds, so a reply's 64 acknowledgements cover two sends.</summary>
    private const int MostPacketsPerSend = 32;

    /// <summary>Room for a packet's header (its kind, number, tick, and an empty removal count), in bytes.</summary>
    private const int HeaderBytes = 16;

    private readonly List<HostEntity> _entities = [];
    private readonly Stack<int> _freeEntities = new();
    private readonly Dictionary<uint, int> _hostIndex = [];
    private readonly HostClient?[] _clients = new HostClient?[SessionService.MaxSlots];
    private readonly SlotJoins _joins = new();
    private readonly List<Reply> _replies = [];
    private readonly List<Candidate> _candidates = [];
    private readonly MessageWriter _packet = new();
    private readonly MessageWriter _record = new();
    private ReplicatedState _captured;
    private ReplicatedState _baseline;
    private uint _lastId;
    private uint _lastGeneration;

    /// <summary>
    /// On a host or offline, replicates <paramref name="entity"/> as <paramref name="kind"/> from the next send, adding its
    /// <see cref="Replicated"/> (a structural change: outside a query). Its id is a player's, <c>PlayerIds.Of(slot)</c>
    /// (a client's player is its slot's: the host measures a reach from it), or with 0 the next the host hands out. A
    /// destroyed entity is removed from every client at the next send, and its id is free at once (a player's, for the
    /// next); <see cref="Remove"/> removes a live one (its <see cref="Replicated"/> stays, so remove that before adding it
    /// again). Each add is a new generation of its id: a client shows the new entity afresh. Throws on a client.
    /// </summary>
    public uint Add(Entity entity, ReplicatedKindId kind, uint id = 0)
    {
        if (_session.Role == SessionRole.Client)
        {
            throw new InvalidOperationException(
                "A client shows what the host replicates; it replicates nothing itself."
            );
        }

        var world = _ecs.World;
        if (!world.IsAlive(entity) || world.Has<Replicated>(entity))
        {
            throw new ArgumentException("Only a live entity not replicated already can be added.", nameof(entity));
        }

        if (kind.Value >= _kinds.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "No such replicated kind.");
        }

        if (id == 0)
        {
            id = ++_lastId;
            if (id >= PlayerIds.First)
            {
                throw new InvalidOperationException("The host has handed out every id below the players'.");
            }
        }
        else if (PlayerIds.SlotOf(id) < 0)
        {
            // Below the players' ids are the host's to hand out, so a game's own could meet one later.
            throw new ArgumentException($"The id {id} is not a player's; give 0 for the host's next.", nameof(id));
        }
        else if (_hostIndex.TryGetValue(id, out var held))
        {
            if (world.IsAlive(_entities[held].Entity))
            {
                throw new ArgumentException($"The id {id} is replicated already.", nameof(id));
            }

            // Its entity is gone (a player who died, before the next send noticed): the id is free.
            Retire(held);
        }

        var replicated = _kinds[kind];
        if (!_freeEntities.TryPop(out var index))
        {
            index = _entities.Count;
            _entities.Add(new HostEntity());
        }

        _entities[index].Start(entity, id, replicated, ++_lastGeneration);
        foreach (var client in _clients)
        {
            client?.Forget(index);
        }

        _hostIndex[id] = index;
        world.Add(entity, new Replicated { Id = id, Kind = kind.Value });
        return id;
    }

    /// <summary>
    /// Stops replicating the entity with <paramref name="id"/>: every client that shows it removes it. Its
    /// <see cref="Replicated"/> stays; nothing when the id is not replicated.
    /// </summary>
    public void Remove(uint id)
    {
        if (_hostIndex.TryGetValue(id, out var index))
        {
            Retire(index);
        }
    }

    /// <summary>The entities replicated now, on a host or offline.</summary>
    public int Replicating => _hostIndex.Count;

    void IReplicationEndpoint.ReceiveReply(int slot, ReadOnlySpan<byte> body)
    {
        var reader = new MessageReader(body);
        var newest = (uint)reader.ReadVarInt();
        var bits = reader.ReadULong();
        ReadCommands(ref reader, _clients[slot]);
        reader.EnsureEnd();
        if (newest != 0 && (_clients[slot] is not { } client || newest >= client.NextPacket))
        {
            // A client cannot have received a packet not sent yet.
            throw new InvalidDataException($"A reply acknowledges packet {newest}, which was not sent.");
        }

        _replies.Add(new Reply(slot, newest, bits));
    }

    /// <summary>
    /// A host's step (<c>ReplicationSendSystem</c>): the clients that joined or left, the replies since the last step,
    /// and every <see cref="SendIntervalTicks"/> steps the entities' states captured and each client's packets sent.
    /// Offline, only the destroyed entities are let go.
    /// </summary>
    internal void HostStep(ulong tick)
    {
        if (_session.Role == SessionRole.Offline)
        {
            RetireGone();
            return;
        }

        if (_session.Role != SessionRole.Host)
        {
            return;
        }

        FollowRoster();
        TakeReplies();
        if (tick % (ulong)SendIntervalTicks != 0)
        {
            return;
        }

        var hostTick = (int)tick;
        Capture(hostTick);
        foreach (var client in _clients)
        {
            if (client is not null)
            {
                Send(client, hostTick);
            }
        }
    }

    private void FollowRoster()
    {
        var players = _session.Players;
        for (var slot = 1; slot < _clients.Length; slot++)
        {
            if (!_joins.Changed(players, slot, out var previous))
            {
                continue;
            }

            if (previous != 0 && _clients[slot] is not null)
            {
                _clients[slot] = null;
                LogClientLeft(slot);
            }

            if (slot < players.Length && players[slot].Present)
            {
                var client = new HostClient(slot);
                client.Grow(_entities.Count);
                _clients[slot] = client;
                LogClientJoined(slot, _fixedStepHz / SendIntervalTicks, _settings.BytesPerSecond);
            }
        }
    }

    private void TakeReplies()
    {
        foreach (var reply in _replies)
        {
            if (_clients[reply.Slot] is not { } client)
            {
                continue;
            }

            // It replies once its world is loaded: from then on it reads what it is sent.
            client.Replied = true;
            if (reply.Newest == 0)
            {
                continue;
            }

            Acknowledge(client, reply.Newest);
            for (var back = 0; back < 64; back++)
            {
                if ((reply.Bits & (1ul << back)) != 0 && reply.Newest > back + 1)
                {
                    Acknowledge(client, reply.Newest - (uint)back - 1);
                }
            }
        }

        _replies.Clear();
    }

    /// <summary>What packet <paramref name="sequence"/> carried is now the client's: its states are baselines, its removals done.</summary>
    private static void Acknowledge(HostClient client, uint sequence)
    {
        var packet = client.Packets[sequence % PacketRing];
        if (packet.Sequence != sequence || packet.Acknowledged)
        {
            return;
        }

        packet.Acknowledged = true;
        for (var i = 0; i < packet.Count; i++)
        {
            var index = packet.Indices[i];
            // A state of an entity the client was told to forget since, or of an index taken again, counts for nothing.
            if (index < client.Epochs.Length && client.Epochs[index] == packet.Epochs[i])
            {
                client.Baselines[index] = Math.Max(client.Baselines[index], packet.Tick);
            }
        }

        // Only the removals this packet carried: the same id removed again since is a newer removal.
        for (var i = 0; i < packet.RemovedCount; i++)
        {
            client.Acknowledged(packet.Removed[i]);
        }

        client.AcknowledgedPackets++;
    }

    /// <summary>Offline: lets go of every entity destroyed since, so its id is free.</summary>
    private void RetireGone()
    {
        var world = _ecs.World;
        for (var index = 0; index < _entities.Count; index++)
        {
            if (_entities[index].Live && !world.IsAlive(_entities[index].Entity))
            {
                Retire(index);
            }
        }
    }

    /// <summary>Each live entity's state now, kept in its history; one destroyed since is removed from the clients.</summary>
    private void Capture(int tick)
    {
        var context = Context;
        for (var index = 0; index < _entities.Count; index++)
        {
            var entity = _entities[index];
            if (!entity.Live)
            {
                continue;
            }

            if (!context.World.IsAlive(entity.Entity))
            {
                Retire(index);
                continue;
            }

            _captured = default;
            entity.Kind.Handler.Capture(in context, entity.Entity, ref _captured);
            entity.Keep(tick, in _captured);
        }
    }

    /// <summary>
    /// One client's send: the entities in its reach with something new for it, by priority, in packets until its bytes
    /// are spent (a record too large for what is left is passed over for smaller ones), at most
    /// <see cref="MostPacketsPerSend"/> packets; removals in the first. At least a packet, for its clock and removals. A
    /// client sent nothing yet that has not replied is loading its world: it is sent nothing until it replies.
    /// </summary>
    private void Send(HostClient client, int tick)
    {
        if (!client.Replied)
        {
            return;
        }

        var perSend = (double)_settings.BytesPerSecond * SendIntervalTicks / _fixedStepHz;
        var most = (double)_settings.PeakBytesPerSecond * SendIntervalTicks / _fixedStepHz;
        client.Tokens = Math.Min(client.Tokens + perSend, most);
        client.Grow(_entities.Count);
        Choose(client);

        var limit = _session.MaxUnreliableBytes;
        var budget = (int)client.Tokens;
        var spent = 0;
        var packets = 1;
        StartPacket(client, tick, removals: true);
        foreach (var candidate in _candidates)
        {
            var index = candidate.Index;
            if (!WriteRecord(client, _entities[index], index, tick))
            {
                continue;
            }

            var size = _record.Length;
            if (_packet.Length + size > limit)
            {
                // A packet of its own, if the send has room for one more.
                if (packets == MostPacketsPerSend || spent + _packet.Length + HeaderBytes + size > budget)
                {
                    continue;
                }

                spent += FinishPacket(client);
                packets++;
                StartPacket(client, tick, removals: false);
            }

            if (spent + _packet.Length + size > budget)
            {
                continue;
            }

            _packet.WriteBytes(_record.Written);
            client.Current!.Add(index, client.Epochs[index]);
            client.Sent[index] = true;
            client.Priorities[index] = 0;
        }

        spent += FinishPacket(client);
        client.Tokens -= spent;
    }

    /// <summary>
    /// The entities a client is sent this time, highest priority first: within its reach and with a state it has not
    /// acknowledged; one that left its reach is removed from it instead. Its own player is left out: it predicts it, and the
    /// owner's block carries the host's state of it.
    /// </summary>
    private void Choose(HostClient client)
    {
        _candidates.Clear();
        var own = PlayerIds.Of(client.Slot);
        var focused = _hostIndex.TryGetValue(own, out var ownIndex) && _entities[ownIndex].Focused;
        var focus = focused ? _entities[ownIndex].Focus : default;
        for (var index = 0; index < _entities.Count; index++)
        {
            var entity = _entities[index];
            if (!entity.Live || entity.Count == 0 || entity.Id == own)
            {
                continue;
            }

            var reach = entity.Kind.RelevanceMetres;
            var nearness = 1.0;
            if (reach > 0)
            {
                var distance =
                    focused && entity.Focused ? Distance(in focus, in entity.Focus) : double.PositiveInfinity;
                if (distance > reach)
                {
                    if (client.Baselines[index] >= 0 || client.Sent[index])
                    {
                        // Out of its reach: the client forgets it, and is sent it whole if it comes back.
                        client.Remove(entity.Id);
                        client.Forget(index);
                    }

                    continue;
                }

                nearness = 1 - distance / reach;
            }

            var baselineTick = client.Baselines[index];
            if (baselineTick >= 0 && entity.TryGet(baselineTick, ref _baseline))
            {
                entity.Newest(ref _captured);
                if (ReplicatedStateCodec.Same(entity.Kind.Schema, in _captured, in _baseline))
                {
                    // The client has this state already.
                    client.Priorities[index] = 0;
                    continue;
                }
            }

            // A new entity, or one the client has not seen move, gains faster than one it has a recent state of.
            var gain = entity.Kind.Priority * (1 + nearness) * (baselineTick < 0 ? 2 : 1);
            client.Priorities[index] += (float)gain;
            // Back in reach (or added again) before its removal was acknowledged: it is sent whole, with its generation,
            // so the removal is not needed.
            client.DropRemovals(entity.Id);

            _candidates.Add(new Candidate(client.Priorities[index], index));
        }

        CollectionsMarshal
            .AsSpan(_candidates)
            .Sort(
                static (a, b) =>
                    b.Priority != a.Priority ? b.Priority.CompareTo(a.Priority) : a.Index.CompareTo(b.Index)
            );
    }

    /// <summary>
    /// Writes an entity's newest state as a record: its id, kind, how many ticks back its baseline is (0: whole, then the
    /// id's generation, so a client knows an entity added again under it), and its changes from it. False when the entity
    /// has no state yet.
    /// </summary>
    private bool WriteRecord(HostClient client, HostEntity entity, int index, int tick)
    {
        if (entity.Count == 0)
        {
            return false;
        }

        entity.Newest(ref _captured);
        var baselineTick = client.Baselines[index];
        var age = 0;
        if (baselineTick >= 0 && entity.TryGet(baselineTick, ref _baseline))
        {
            age = tick - baselineTick;
        }
        else
        {
            // Whole: its changes from the zero state.
            _baseline = default;
        }

        _record.Reset();
        _record.WriteVarInt((int)entity.Id);
        _record.WriteByte(entity.Kind.Id.Value);
        _record.WriteVarInt(age);
        if (age == 0)
        {
            _record.WriteVarInt((int)(entity.Generation & int.MaxValue));
        }

        ReplicatedStateCodec.Write(_record, entity.Kind.Schema, in _captured, in _baseline);
        return true;
    }

    /// <summary>
    /// Starts a client's next packet: its number, the tick, and, in a send's first, the owner's block (its player's state
    /// after the last command applied) and the ids it removes (up to a packet's share).
    /// </summary>
    private void StartPacket(HostClient client, int tick, bool removals)
    {
        var sequence = client.NextPacket++;
        var packet = client.Packets[sequence % PacketRing];
        packet.Begin(sequence, tick);
        client.Current = packet;
        _packet.Reset();
        _packet.WriteByte((byte)MessageKind.Snapshot);
        _packet.WriteVarInt((int)sequence);
        _packet.WriteVarInt(tick);
        if (removals)
        {
            WriteOwner(client);
        }
        else
        {
            _packet.WriteByte(0);
        }

        var count = removals ? Math.Min(client.Removals.Count, MostRemovalsPerPacket) : 0;
        _packet.WriteVarInt(count);
        for (var i = 0; i < count; i++)
        {
            var removal = client.Removals[i];
            _packet.WriteVarInt((int)removal.Id);
            packet.Remove(removal.Number);
        }
    }

    /// <summary>Sends the client's packet and returns its bytes.</summary>
    private int FinishPacket(HostClient client)
    {
        var bytes = _packet.Length;
        _session.SendEngineTo(client.Slot, _packet.Written);
        client.Current = null;
        client.BytesSent += bytes;
        client.PacketsSent++;
        return bytes;
    }

    /// <summary>The entity at <paramref name="index"/> is no longer replicated: every client that has it removes it.</summary>
    private void Retire(int index)
    {
        var entity = _entities[index];
        foreach (var client in _clients)
        {
            if (
                client is not null
                && index < client.Baselines.Length
                && (client.Baselines[index] >= 0 || client.Sent[index])
            )
            {
                client.Remove(entity.Id);
            }

            client?.Forget(index);
        }

        _hostIndex.Remove(entity.Id);
        entity.Live = false;
        _freeEntities.Push(index);
    }

    private static double Distance(in WorldPoint a, in WorldPoint b)
    {
        double x = a.X - b.X,
            y = a.Y - b.Y,
            z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private void PrintHost(CommandContext context)
    {
        var culture = CultureInfo.InvariantCulture;
        context.Print(
            string.Create(
                culture,
                $"Replicating {_hostIndex.Count} entities, {_fixedStepHz / SendIntervalTicks} sends a second, {_settings.BytesPerSecond} bytes a second each client (peak {_settings.PeakBytesPerSecond})."
            )
        );
        foreach (var client in _clients)
        {
            if (client is null)
            {
                continue;
            }

            var known = 0;
            for (var i = 0; i < client.Baselines.Length; i++)
            {
                known += client.Baselines[i] >= 0 ? 1 : 0;
            }

            context.Print(
                string.Create(
                    culture,
                    $"  client {client.Slot}: {known} entities acknowledged, {client.Removals.Count} removals waiting, {client.PacketsSent} packets ({client.AcknowledgedPackets} acknowledged), {client.BytesSent} bytes; its commands {client.NewestCommand - client.NewestApplied} waiting, {client.MissedTotal} missed"
                )
            );
        }
    }

    /// <summary>
    /// An entity the host replicates: its id, kind, and its last <see cref="HostHistory"/> states, each kept once with the
    /// tick it was first captured at, standing for every tick until the next.
    /// </summary>
    private sealed class HostEntity
    {
        private readonly int[] _ticks = new int[HostHistory];
        private long[] _lanes = [];
        private int _head;
        private int _lanesEach;

        public Entity Entity;
        public uint Id;
        public ReplicatedKind Kind = null!;
        public bool Live;
        public int Count;

        /// <summary>Which add of its id this is: a client shows a new generation afresh.</summary>
        public uint Generation;
        public WorldPoint Focus;
        public bool Focused;

        public void Start(Entity entity, uint id, ReplicatedKind kind, uint generation)
        {
            Entity = entity;
            Id = id;
            Generation = generation;
            Kind = kind;
            Live = true;
            Count = 0;
            _head = 0;
            Focused = false;
            _lanesEach = kind.Schema.Lanes;
            if (_lanes.Length != HostHistory * _lanesEach)
            {
                _lanes = new long[HostHistory * _lanesEach];
            }
        }

        public void Keep(int tick, in ReplicatedState state)
        {
            if (Count > 0 && SameAsNewest(in state))
            {
                // Unchanged: the newest state stands for this tick too, so a baseline from before stays found.
                return;
            }

            _head = (_head + 1) % HostHistory;
            _ticks[_head] = tick;
            for (var lane = 0; lane < _lanesEach; lane++)
            {
                _lanes[_head * _lanesEach + lane] = state.Lanes[lane];
            }

            Count = Math.Min(Count + 1, HostHistory);
            if (Kind.Schema.Focus is { } focus)
            {
                Focus = focus.Get(in state);
                Focused = true;
            }
        }

        public void Newest(ref ReplicatedState state) => Copy(_head, ref state);

        /// <summary>The state at <paramref name="tick"/>: the newest kept from it or before; false when that is gone.</summary>
        public bool TryGet(int tick, ref ReplicatedState state)
        {
            for (var back = 0; back < Count; back++)
            {
                var slot = (_head - back + HostHistory) % HostHistory;
                if (_ticks[slot] <= tick)
                {
                    Copy(slot, ref state);
                    return true;
                }
            }

            return false;
        }

        private bool SameAsNewest(in ReplicatedState state)
        {
            for (var lane = 0; lane < _lanesEach; lane++)
            {
                if (_lanes[_head * _lanesEach + lane] != state.Lanes[lane])
                {
                    return false;
                }
            }

            return true;
        }

        private void Copy(int slot, ref ReplicatedState state)
        {
            state = default;
            for (var lane = 0; lane < _lanesEach; lane++)
            {
                state.Lanes[lane] = _lanes[slot * _lanesEach + lane];
            }
        }
    }

    /// <summary>One client, from the host's side: what it acknowledged of each entity, and the packets sent it.</summary>
    private sealed class HostClient(int slot)
    {
        public int Slot { get; } = slot;

        public uint NextPacket = 1;
        public double Tokens;
        public long BytesSent;
        public long PacketsSent;
        public long AcknowledgedPackets;

        /// <summary>True once it has replied: its world is loaded, and it reads what it is sent.</summary>
        public bool Replied;
        public SentPacket? Current;
        public readonly SentPacket[] Packets = CreatePackets();

        /// <summary>Ids it is to remove, each numbered, until a packet that carried that removal is acknowledged.</summary>
        public readonly List<Removal> Removals = [];

        /// <summary>Its player's commands by its tick (the tick plus one in <see cref="CommandTicks"/>; 0 for none).</summary>
        public readonly PlayerCommand[] Commands = new PlayerCommand[CommandRing];
        public readonly int[] CommandTicks = new int[CommandRing];

        // The host's tick each command was made at the sight of, by the client's tick; and the applied one's.
        public readonly double[] ShownTicks = new double[CommandRing];
        public double Shown = -1;

        /// <summary>The newest command's tick it sent, the next to apply, and the last applied; -1 for none.</summary>
        public int NewestCommand = -1;
        public int NextCommand = -1;
        public int NewestApplied = -1;
        public PlayerCommand LastCommand;

        /// <summary>The first command's tick it sent; -1 before.</summary>
        public int FirstCommand = -1;

        /// <summary>True when a command newer than any before arrived since the host last applied one.</summary>
        public bool Arrived;

        /// <summary>The steps in a row its next command was missing, and in all; and the steps in a row none waited.</summary>
        public int Missed;
        public long MissedTotal;
        public int Starved;
        private int _nextRemoval;

        /// <summary>By entity index: the tick of the newest state it acknowledged, or -1 when it has none.</summary>
        public int[] Baselines = [];

        /// <summary>By entity index: true once a state was sent since it last forgot the entity.</summary>
        public bool[] Sent = [];

        /// <summary>By entity index: what it has gained since it was last sent.</summary>
        public float[] Priorities = [];

        /// <summary>By entity index: counts each forgetting, so a late acknowledgement of a state before it counts for nothing.</summary>
        public int[] Epochs = [];

        public void Grow(int count)
        {
            if (Baselines.Length >= count)
            {
                return;
            }

            var size = Math.Max(count, Math.Max(16, Baselines.Length * 2));
            var old = Baselines.Length;
            Array.Resize(ref Baselines, size);
            Array.Resize(ref Sent, size);
            Array.Resize(ref Priorities, size);
            Array.Resize(ref Epochs, size);
            Array.Fill(Baselines, -1, old, size - old);
        }

        /// <summary>The client is to remove <paramref name="id"/>.</summary>
        public void Remove(uint id) => Removals.Add(new Removal(id, ++_nextRemoval));

        /// <summary>A packet that carried removal <paramref name="number"/> arrived: that removal is done.</summary>
        public void Acknowledged(int number)
        {
            for (var i = 0; i < Removals.Count; i++)
            {
                if (Removals[i].Number == number)
                {
                    Removals.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>Every removal of <paramref name="id"/> waiting: it is sent whole instead.</summary>
        public void DropRemovals(uint id)
        {
            for (var i = Removals.Count - 1; i >= 0; i--)
            {
                if (Removals[i].Id == id)
                {
                    Removals.RemoveAt(i);
                }
            }
        }

        /// <summary>The client is taken to have nothing of the entity at <paramref name="index"/>.</summary>
        public void Forget(int index)
        {
            if (index >= Baselines.Length)
            {
                return;
            }

            Baselines[index] = -1;
            Sent[index] = false;
            Priorities[index] = 0;
            Epochs[index]++;
        }

        private static SentPacket[] CreatePackets()
        {
            var packets = new SentPacket[PacketRing];
            for (var i = 0; i < packets.Length; i++)
            {
                packets[i] = new SentPacket();
            }

            return packets;
        }
    }

    /// <summary>A packet sent to a client: its number, tick, the entities' states it carried, and the ids it removed.</summary>
    private sealed class SentPacket
    {
        public uint Sequence;
        public int Tick;
        public bool Acknowledged;
        public int Count;
        public int[] Indices = new int[32];
        public int[] Epochs = new int[32];
        public int RemovedCount;

        /// <summary>The numbers of the removals it carried.</summary>
        public int[] Removed = new int[MostRemovalsPerPacket];

        public void Begin(uint sequence, int tick)
        {
            Sequence = sequence;
            Tick = tick;
            Acknowledged = false;
            Count = 0;
            RemovedCount = 0;
        }

        public void Add(int index, int epoch)
        {
            if (Count == Indices.Length)
            {
                Array.Resize(ref Indices, Count * 2);
                Array.Resize(ref Epochs, Count * 2);
            }

            Indices[Count] = index;
            Epochs[Count++] = epoch;
        }

        public void Remove(int number) => Removed[RemovedCount++] = number;
    }

    private readonly record struct Reply(int Slot, uint Newest, ulong Bits);

    private readonly record struct Removal(uint Id, int Number);

    private readonly record struct Candidate(float Priority, int Index);
}
