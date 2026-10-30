using System.Diagnostics;
using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Commands;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Replication;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Matches;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Replication;

/// <summary>
/// A host and a client in one process (<see cref="HeadlessMatch"/> over the engine's real services, without terrain),
/// replicating test movers: each joined with an in-memory transport, wrapped in a <see cref="SimulatedNetwork"/> when
/// given conditions, whose clock is real time until the join is done and then the steps' (a 60th of a second each).
/// </summary>
internal sealed class ReplicationHarness : IDisposable
{
    private const double StepMilliseconds = 1000.0 / 60;

    /// <summary>The game message kind the client takes and ignores (<see cref="SendGame"/>).</summary>
    public const byte GameKind = SessionService.FirstGameKind;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private double _manual = -1;
    private FakeNetwork _network = null!;
    private IReadOnlyDictionary<string, string?>? _options;

    private ReplicationHarness(MoverPlan hostPlan, MoverPlan clientPlan)
    {
        HostPlan = hostPlan;
        ClientPlan = clientPlan;
    }

    public MoverPlan HostPlan { get; }

    public MoverPlan ClientPlan { get; }

    public ServiceProvider HostProvider { get; private set; } = null!;

    public ServiceProvider ClientProvider { get; private set; } = null!;

    public FakeTransport HostTransport { get; private set; } = null!;

    public FakeTransport ClientTransport { get; private set; } = null!;

    public HeadlessMatch Host { get; private set; } = null!;

    public HeadlessMatch Client { get; private set; } = null!;

    public ReplicationService HostReplication => Host.Match.Replication;

    public ReplicationService ClientReplication => Client.Match.Replication;

    /// <summary>The clock the simulated network reads, in milliseconds.</summary>
    public long NowMilliseconds => _manual >= 0 ? (long)_manual : _wall.ElapsedMilliseconds;

    /// <summary>
    /// Hosts a match of <paramref name="plan"/>'s movers and joins it; <paramref name="hostConditions"/> and
    /// <paramref name="clientConditions"/> wrap each side's transport.
    /// </summary>
    public static ReplicationHarness Start(
        MoverPlan plan,
        NetworkConditions hostConditions = default,
        NetworkConditions clientConditions = default,
        IReadOnlyDictionary<string, string?>? options = null,
        MoverPlan? clientPlan = null
    )
    {
        var harness = new ReplicationHarness(plan, clientPlan ?? new MoverPlan());
        var network = new FakeNetwork();
        harness._network = network;
        harness._options = options;
        // Characters walk on the test floor, so both worlds have terrain.
        var terrain = plan.Characters || harness.ClientPlan.Characters;
        harness.HostTransport = new FakeTransport(network);
        harness.ClientTransport = new FakeTransport(network);
        harness.HostProvider = Build(options, plan, harness.Wrap(harness.HostTransport, hostConditions, 11), terrain);
        harness.ClientProvider = Build(
            options,
            harness.ClientPlan,
            harness.Wrap(harness.ClientTransport, clientConditions, 12),
            terrain
        );
        harness.Host = new HeadlessRun(harness.HostProvider).Host(seed: 3);
        if (terrain)
        {
            harness.Host.Match.Voxels.SetCenters([System.Numerics.Vector3.Zero]);
        }

        Assert.True(harness.Host.WaitUntilLoaded());
        harness.Client = new HeadlessRun(harness.ClientProvider).Join(harness.Host);
        // From here the test's steps move the clock.
        harness._manual = harness._wall.ElapsedMilliseconds;
        if (terrain)
        {
            harness.Client.Match.Voxels.SetCenters([System.Numerics.Vector3.Zero]);
        }

        Assert.True(harness.Client.WaitUntilLoaded());
        return harness;
    }

    /// <summary>
    /// The client leaves (its match ends, its services go), the host notices at its next steps, and a new client (with no
    /// added delay) joins in its place, its slot the one freed.
    /// </summary>
    public void ReplaceClient()
    {
        Client.Dispose();
        ClientProvider.Dispose();
        Step(10, clientToo: false);
        ClientTransport = new FakeTransport(_network);
        ClientProvider = Build(_options, ClientPlan, ClientTransport);
        Client = new HeadlessRun(ClientProvider).Join(Host);
        Assert.True(Client.WaitUntilLoaded());
    }

    /// <summary>Runs <paramref name="ticks"/> steps of the host, then the client, the clock a 60th of a second each.</summary>
    public void Step(int ticks) => Step(ticks, clientToo: true);

    /// <summary>On the host, sends the client a game message of <paramref name="bytes"/>, its kind among them.</summary>
    public void SendGame(int bytes, SessionChannel channel)
    {
        var writer = Host.Match.Session.BeginMessage(GameKind);
        for (var i = 1; i < bytes; i++)
        {
            writer.WriteByte(0);
        }

        Host.Match.Session.SendTo(Client.Match.Session.Slot, channel);
    }

    /// <summary>Runs <paramref name="ticks"/> steps of the host alone, as while the client stalls and drops its steps.</summary>
    public void StepHost(int ticks) => Step(ticks, clientToo: false);

    private void Step(int ticks, bool clientToo)
    {
        for (var i = 0; i < ticks; i++)
        {
            _manual += StepMilliseconds;
            Host.Step(1);
            if (clientToo)
            {
                Client.Step(1);
            }
        }
    }

    /// <summary>What the client shows: each replicated entity's id and its <see cref="TestShown"/>.</summary>
    public Dictionary<uint, TestShown> Shown()
    {
        var shown = new Dictionary<uint, TestShown>();
        Client.World.Query(
            new QueryDescription().WithAll<Components.Replicated, TestShown>(),
            (ref Components.Replicated replicated, ref TestShown state) => shown[replicated.Id] = state
        );
        return shown;
    }

    public void Dispose()
    {
        Client?.Dispose();
        Host?.Dispose();
        ClientProvider?.Dispose();
        HostProvider?.Dispose();
    }

    private ISessionTransport Wrap(FakeTransport transport, NetworkConditions conditions, int seed) =>
        conditions.Any ? new SimulatedNetwork(transport, conditions, () => NowMilliseconds, seed) : transport;

    /// <summary>
    /// The engine's services, the test kinds, the mover and character systems, a client's input from the plan's script,
    /// the engine's movement, and the replication's systems, over <paramref name="transport"/>.
    /// </summary>
    public static ServiceProvider Build(
        IReadOnlyDictionary<string, string?>? options,
        MoverPlan plan,
        ISessionTransport transport,
        bool terrain = false
    ) =>
        EngineComposition.Build(
            options,
            services =>
            {
                services.AddSingleton<ISessionTransports>(new Transports(transport));
                services.AddSingleton(plan);
                // A game message a client takes and does nothing with, for the game's share of a client's bytes.
                services.AddScoped<ISessionGameHandler>(_ => new GameMessageHandler(
                    GameKind,
                    SessionMessageDirection.ToClients,
                    static (_, _) => { }
                ));
                services.AddSingleton<IRunStartup, TestKinds>();
                if (plan.Boost)
                {
                    services.AddScoped<IMovementMode, TestBoostMode>();
                }

                // A client shows first; the host moves its movers, then sends.
                services.AddScoped<IFixedUpdateSystem>(sp => new ReplicationReceiveSystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    sp.GetRequiredService<ReplicationService>()
                ));
                services.AddScoped<IFixedUpdateSystem>(sp => new TestMoverSystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    sp.GetRequiredService<ReplicationService>(),
                    sp.GetRequiredService<SessionService>(),
                    sp.GetRequiredService<WorldOrigin>(),
                    plan
                ));
                services.AddScoped<IFixedUpdateSystem>(sp => new TestCharacterSystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    sp.GetRequiredService<ReplicationService>(),
                    sp.GetRequiredService<SessionService>(),
                    plan
                ));
                // The client's command from the plan's script, after the step's start and before the reply records it.
                services.AddScoped<IFixedUpdateSystem>(sp => new TestInputSystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    plan
                ));
                services.AddScoped<IFixedUpdateSystem>(sp => new ReplicationReplySystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    sp.GetRequiredService<ReplicationService>()
                ));
                services.AddScoped<IFixedUpdateSystem>(sp => new PlayerMovementSystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    sp.GetRequiredService<Itera.Engine.Physics.VoxelCharacterMover>(),
                    sp.GetRequiredService<PlayArea>(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayerMovementSystem>.Instance,
                    mode: sp.GetService<IMovementMode>(),
                    physics: sp.GetRequiredService<Itera.Engine.Physics.PhysicsWorld>()
                ));
                services.AddScoped<IFixedUpdateSystem>(sp => new ReplicationSendSystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    sp.GetRequiredService<ReplicationService>(),
                    sp.GetRequiredService<ConsoleCommands>()
                ));
            },
            terrain
        );

    private sealed class Transports(ISessionTransport transport) : ISessionTransports
    {
        public ISessionTransport Transport(SessionTransportKind kind) => transport;
    }
}

/// <summary>What a test's movers do, and the kinds' ids once registered; one a run (a provider).</summary>
internal sealed class MoverPlan
{
    public int Count { get; set; } = 3;

    /// <summary>Metres a second along +x.</summary>
    public float Speed { get; set; } = 3;

    /// <summary>The movers' reach from a client's player; 0 for every client wherever it is.</summary>
    public float RelevanceMetres { get; set; }

    /// <summary>How far from a client's player the movers' priority grows at its full rate; 0 everywhere.</summary>
    public float FalloffMetres { get; set; }

    /// <summary>Where the host puts each client's player.</summary>
    public WorldPoint PlayerAt { get; set; }

    /// <summary>Registers a third kind, so this run's kinds differ from a run's without it.</summary>
    public bool ExtraKind { get; set; }

    public ReplicatedKindId Mover { get; set; }

    public ReplicatedKindId Player { get; set; }

    public ReplicatedKindId Character { get; set; }

    /// <summary>
    /// Characters that walk (the engine's movement components) on the test floor: on a client its own player, predicted,
    /// moved by <see cref="Script"/>; on the host each client's player, moved by the client's commands.
    /// </summary>
    public bool Characters { get; set; }

    /// <summary>On a client, its player's command at each tick; none stands still.</summary>
    public Func<ulong, Components.PlayerCommand>? Script { get; set; }

    /// <summary>
    /// Characters carry a <see cref="TestBoost"/> and move by <see cref="TestBoostMode"/>; with
    /// <see cref="BoostPredicted"/> its state is a predicted part (<see cref="TestBoostState"/>).
    /// </summary>
    public bool Boost { get; set; }

    public bool BoostPredicted { get; set; } = true;

    /// <summary>The game actions the kinds hold as levels (<see cref="ReplicatedKinds.HeldActions"/>).</summary>
    public uint HeldActions { get; set; }

    /// <summary>The game actions set while a key is held (<see cref="ReplicatedKinds.HeldKeyActions"/>).</summary>
    public uint HeldKeyActions { get; set; }

    /// <summary>On a client, its own player; on the host, each slot's.</summary>
    public Entity Own { get; set; } = Entity.Null;

    public Entity[] Remote { get; } = new Entity[SessionService.MaxSlots];

    /// <summary>On the host: the movers made, in order.</summary>
    public List<(Entity Entity, uint Id)> Movers { get; } = [];

    /// <summary>On a client: the entities its kinds made and removed.</summary>
    public int Spawns { get; set; }

    public int Despawns { get; set; }
}

/// <summary>A host's mover: where it is (local), its heading, and a count of its steps.</summary>
internal struct TestMover
{
    public Vector3 Position;
    public float Heading;
    public int Phase;
}

/// <summary>A host's player of a client: where it stands (local).</summary>
internal struct TestPlayer
{
    public Vector3 Feet;
}

/// <summary>What a client shows of a replicated mover or player: absolute, with how often it was set.</summary>
internal struct TestShown
{
    public WorldPoint At;
    public float Heading;
    public int Phase;
    public int Applied;
}

/// <summary>The test kinds, registered at the run's start: a mover (position, heading, phase) and a player (position).</summary>
internal sealed class TestKinds(ReplicatedKinds kinds, MoverPlan plan) : IRunStartup
{
    public static readonly ReplicatedSchema MoverSchema = new();
    public static readonly PositionField MoverAt = MoverSchema.AddPosition("at");
    public static readonly AngleField MoverHeading = MoverSchema.AddAngle("heading");
    public static readonly IntegerField MoverPhase = MoverSchema.AddInteger("phase", 0, 99);
    public static readonly ReplicatedSchema PlayerSchema = new();
    public static readonly PositionField PlayerAt = PlayerSchema.AddPosition("feet");
    public static readonly ReplicatedSchema CharacterSchema = new();
    public static readonly PositionField CharacterAt = CharacterSchema.AddPosition("feet");

    public void Start()
    {
        plan.Mover = kinds.Register(
            "test.mover",
            MoverSchema,
            new MoverKind(plan),
            plan.RelevanceMetres,
            priority: 1,
            falloffMetres: plan.FalloffMetres
        );
        plan.Player = kinds.Register("test.player", PlayerSchema, new PlayerKind(plan), priority: 4);
        plan.Character = kinds.Register("test.character", CharacterSchema, new CharacterKind(plan), priority: 4);
        if (plan.ExtraKind)
        {
            kinds.Register("test.extra", new ReplicatedSchema(), new PlayerKind(plan));
        }

        if (plan.Boost && plan.BoostPredicted)
        {
            kinds.RegisterPredicted(new TestBoostState());
        }

        kinds.HeldActions = plan.HeldActions;
        kinds.HeldKeyActions = plan.HeldKeyActions;
    }

    private sealed class MoverKind(MoverPlan plan) : IReplicatedKind
    {
        public void Capture(in ReplicationContext context, Entity entity, ref ReplicatedState state)
        {
            ref var mover = ref context.World.Get<TestMover>(entity);
            MoverAt.Set(ref state, context.Origin.ToWorld(mover.Position));
            MoverHeading.Set(ref state, mover.Heading);
            MoverPhase.Set(ref state, mover.Phase);
        }

        public Entity Spawn(in ReplicationContext context, in ReplicatedState state)
        {
            plan.Spawns++;
            return context.World.Create(new TestShown());
        }

        public void Apply(in ReplicationContext context, Entity entity, in ReplicatedState state)
        {
            ref var shown = ref context.World.Get<TestShown>(entity);
            shown.At = MoverAt.Get(in state);
            shown.Heading = MoverHeading.Get(in state);
            shown.Phase = MoverPhase.Get(in state);
            shown.Applied++;
        }

        public void Despawn(in ReplicationContext context, Entity entity)
        {
            plan.Despawns++;
            context.World.Destroy(entity);
        }
    }

    private sealed class CharacterKind(MoverPlan plan) : IReplicatedKind
    {
        public void Capture(in ReplicationContext context, Entity entity, ref ReplicatedState state) =>
            CharacterAt.Set(
                ref state,
                context.Origin.ToWorld(context.World.Get<Components.CharacterPosition>(entity).Feet)
            );

        public Entity Spawn(in ReplicationContext context, in ReplicatedState state)
        {
            plan.Spawns++;
            return context.World.Create(new TestShown());
        }

        public void Apply(in ReplicationContext context, Entity entity, in ReplicatedState state)
        {
            ref var shown = ref context.World.Get<TestShown>(entity);
            shown.At = CharacterAt.Get(in state);
            shown.Applied++;
        }

        public void Despawn(in ReplicationContext context, Entity entity) => context.World.Destroy(entity);
    }

    private sealed class PlayerKind(MoverPlan plan) : IReplicatedKind
    {
        public void Capture(in ReplicationContext context, Entity entity, ref ReplicatedState state)
        {
            if (context.World.Has<TestPlayer>(entity))
            {
                PlayerAt.Set(ref state, context.Origin.ToWorld(context.World.Get<TestPlayer>(entity).Feet));
            }
        }

        public Entity Spawn(in ReplicationContext context, in ReplicatedState state)
        {
            plan.Spawns++;
            return context.World.Create(new TestShown());
        }

        public void Apply(in ReplicationContext context, Entity entity, in ReplicatedState state)
        {
            ref var shown = ref context.World.Get<TestShown>(entity);
            shown.At = PlayerAt.Get(in state);
            shown.Applied++;
        }

        public void Despawn(in ReplicationContext context, Entity entity)
        {
            plan.Despawns++;
            context.World.Destroy(entity);
        }
    }
}

/// <summary>
/// On a host: makes the plan's movers at the start, a row 2 m apart along +x from the origin, and each client's player
/// as it joins, and moves the movers along +x each step, turning them and counting their steps (at speed 0 nothing).
/// </summary>
internal sealed class TestMoverSystem(
    World world,
    ReplicationService replication,
    SessionService session,
    WorldOrigin origin,
    MoverPlan plan
) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
{
    private static readonly QueryDescription s_movers = new QueryDescription().WithAll<TestMover>();
    private readonly SlotJoins _joins = new();
    private readonly Entity[] _players = new Entity[SessionService.MaxSlots];

    public override void Initialize()
    {
        if (session.Role == SessionRole.Client)
        {
            return;
        }

        for (var i = 0; i < plan.Count; i++)
        {
            var entity = World.Create(new TestMover { Position = new Vector3(i * 2, 0, 0) });
            plan.Movers.Add((entity, replication.Add(entity, plan.Mover)));
        }
    }

    public override void Update(in FixedStepTime step)
    {
        if (session.Role != SessionRole.Host)
        {
            return;
        }

        var players = session.Players;
        // With characters, the clients' players are those (TestCharacterSystem).
        for (var slot = 1; slot < SessionService.MaxSlots && !plan.Characters; slot++)
        {
            if (!_joins.Changed(players, slot))
            {
                continue;
            }

            // A player who left takes theirs along: the slot's id is the next player's.
            if (World.IsAlive(_players[slot]))
            {
                World.Destroy(_players[slot]);
                replication.Remove(PlayerIds.Of(slot));
            }

            _players[slot] = Entity.Null;
            if (slot < players.Length && players[slot].Present)
            {
                _players[slot] = World.Create(new TestPlayer { Feet = origin.ToLocal(plan.PlayerAt) });
                replication.Add(_players[slot], plan.Player, PlayerIds.Of(slot));
            }
        }

        var move = new Move { Step = plan.Speed * step.StepSeconds };
        World.InlineQuery<Move, TestMover>(in s_movers, ref move);
    }

    private struct Move : IForEach<TestMover>
    {
        public float Step;

        public readonly void Update(ref TestMover mover)
        {
            // At speed 0 a mover stands still: nothing of it changes.
            if (Step == 0)
            {
                return;
            }

            mover.Position.X += Step;
            mover.Heading += 0.01f;
            mover.Phase = (mover.Phase + 1) % 100;
        }
    }
}

/// <summary>
/// Characters that walk: on a client with the plan's <see cref="MoverPlan.Characters"/>, its own player made at the start
/// on the test floor at the middle and predicted; on the host, each joining client's player made there with the same
/// components and replicated under its slot's id (removed when the client leaves).
/// </summary>
internal sealed class TestCharacterSystem(
    World world,
    ReplicationService replication,
    SessionService session,
    MoverPlan plan
) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
{
    private readonly SlotJoins _joins = new();

    public override void Initialize()
    {
        if (session.Role == SessionRole.Client && plan.Characters)
        {
            plan.Own = Create(World, plan.Boost);
            World.Add(plan.Own, new Components.LocalPlayer());
            replication.Predict(plan.Own);
        }
    }

    public override void Update(in FixedStepTime step)
    {
        if (session.Role != SessionRole.Host || !plan.Characters)
        {
            return;
        }

        var players = session.Players;
        for (var slot = 1; slot < SessionService.MaxSlots; slot++)
        {
            if (!_joins.Changed(players, slot))
            {
                continue;
            }

            if (World.IsAlive(plan.Remote[slot]))
            {
                World.Destroy(plan.Remote[slot]);
                replication.Remove(PlayerIds.Of(slot));
            }

            plan.Remote[slot] = Entity.Null;
            if (slot < players.Length && players[slot].Present)
            {
                plan.Remote[slot] = Create(World, plan.Boost);
                World.Add(plan.Remote[slot], new Components.RemotePlayer());
                replication.Add(plan.Remote[slot], plan.Character, PlayerIds.Of(slot));
            }
        }
    }

    /// <summary>A 1.7 m character standing on the floor at the middle, waiting for its first step; with a boost, its <see cref="TestBoost"/>.</summary>
    public static Entity Create(World world, bool boost = false)
    {
        var feet = new Vector3(0, 0.05f, 0);
        var character = world.Create(
            new Components.PlayerCommand(),
            new Components.PlayerMode(),
            Components.CharacterSize.ForHeight(1.7f),
            new Components.CharacterPosition { Feet = feet, PreviousFeet = feet },
            new Components.CharacterVelocity(),
            new Components.CharacterContact { Waiting = true },
            new Components.EyeSmoothing(),
            Components.MovementStats.Default
        );
        if (boost)
        {
            world.Add(character, new TestBoost());
        }

        return character;
    }
}

/// <summary>
/// On a client, its player's command each step from the plan's script, flying the debug camera while the command has
/// <see cref="Components.PlayerButtons.Fly"/> (as the engine's input system makes it).
/// </summary>
internal sealed class TestInputSystem(World world, MoverPlan plan)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    public override void Update(in FixedStepTime step)
    {
        if (plan.Script is { } script && World.IsAlive(plan.Own))
        {
            var command = script(step.Tick);
            World.Get<Components.PlayerCommand>(plan.Own) = command;
            World.Get<Components.PlayerMode>(plan.Own).Flying = (command.Buttons & Components.PlayerButtons.Fly) != 0;
        }
    }
}

/// <summary>A test boost (<see cref="MoverPlan.Boost"/>): the seconds of it left, and how many have started.</summary>
internal struct TestBoost
{
    public float Seconds;
    public int Started;
}

/// <summary>
/// The test's movement mode: the command's first action with no boost running starts one of <see cref="Seconds"/>, in
/// which the player runs along its look at <see cref="Speed"/>, falling as it would; its state is the player's
/// <see cref="TestBoost"/>, which a correction must put back for the client to run its commands again alike.
/// </summary>
internal sealed class TestBoostMode : IMovementMode
{
    public const float Seconds = 0.5f;
    public const float Speed = 9;

    public bool Steer(in MovementModeInput input, ref MovementModeMove move)
    {
        if (!input.World.Has<TestBoost>(input.Entity))
        {
            return false;
        }

        ref var boost = ref input.World.Get<TestBoost>(input.Entity);
        if (boost.Seconds <= 0 && (input.Command.Actions & 1) != 0)
        {
            boost.Seconds = Seconds;
            boost.Started++;
        }

        if (boost.Seconds <= 0)
        {
            return false;
        }

        boost.Seconds -= input.Seconds;
        var forward = ThirdPersonCamera.Forward(input.Command.Yaw, 0);
        move.Velocity = new Vector3(forward.X * Speed, input.Velocity.Y - 9.81f * input.Seconds, forward.Z * Speed);
        move.StepMetres = input.Size.StepMetres;
        return true;
    }
}

/// <summary>The test boost as a predicted part: its seconds left and how many have started.</summary>
internal sealed class TestBoostState : IPredictedState
{
    public string Name => "test.boost";

    public int Bytes => 8;

    public void Capture(World world, Entity entity, WorldOrigin origin, Span<byte> state)
    {
        var boost = world.Has<TestBoost>(entity) ? world.Get<TestBoost>(entity) : default;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(state, boost.Seconds);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(state[4..], boost.Started);
    }

    public void Restore(World world, Entity entity, WorldOrigin origin, ReadOnlySpan<byte> state)
    {
        if (world.Has<TestBoost>(entity))
        {
            world.Get<TestBoost>(entity) = new TestBoost
            {
                Seconds = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(state),
                Started = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(state[4..]),
            };
        }
    }

    public bool Differs(ReadOnlySpan<byte> host, ReadOnlySpan<byte> predicted) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(host[4..])
        != System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(predicted[4..]);

    public bool IsValid(ReadOnlySpan<byte> state)
    {
        var seconds = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(state);
        return float.IsFinite(seconds)
            && seconds <= TestBoostMode.Seconds
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(state[4..]) >= 0;
    }
}
