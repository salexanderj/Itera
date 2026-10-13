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
        harness.HostTransport = new FakeTransport(network);
        harness.ClientTransport = new FakeTransport(network);
        harness.HostProvider = Build(options, plan, harness.Wrap(harness.HostTransport, hostConditions, 11));
        harness.ClientProvider = Build(
            options,
            harness.ClientPlan,
            harness.Wrap(harness.ClientTransport, clientConditions, 12)
        );
        harness.Host = new HeadlessRun(harness.HostProvider).Host(seed: 3);
        Assert.True(harness.Host.WaitUntilLoaded());
        harness.Client = new HeadlessRun(harness.ClientProvider).Join(harness.Host);
        // From here the test's steps move the clock.
        harness._manual = harness._wall.ElapsedMilliseconds;
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

    /// <summary>The engine's services, the test kinds, the mover system, and the replication's systems, over <paramref name="transport"/>.</summary>
    public static ServiceProvider Build(
        IReadOnlyDictionary<string, string?>? options,
        MoverPlan plan,
        ISessionTransport transport
    ) =>
        EngineComposition.Build(
            options,
            services =>
            {
                services.AddSingleton<ISessionTransports>(new Transports(transport));
                services.AddSingleton(plan);
                services.AddSingleton<IRunStartup, TestKinds>();
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
                services.AddScoped<IFixedUpdateSystem>(sp => new ReplicationReplySystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    sp.GetRequiredService<ReplicationService>()
                ));
                services.AddScoped<IFixedUpdateSystem>(sp => new ReplicationSendSystem(
                    sp.GetRequiredService<EcsWorld>().World,
                    sp.GetRequiredService<ReplicationService>(),
                    sp.GetRequiredService<ConsoleCommands>()
                ));
            },
            terrain: false
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

    /// <summary>Where the host puts each client's player.</summary>
    public WorldPoint PlayerAt { get; set; }

    /// <summary>Registers a third kind, so this run's kinds differ from a run's without it.</summary>
    public bool ExtraKind { get; set; }

    public ReplicatedKindId Mover { get; set; }

    public ReplicatedKindId Player { get; set; }

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

    public void Start()
    {
        plan.Mover = kinds.Register("test.mover", MoverSchema, new MoverKind(plan), plan.RelevanceMetres, priority: 1);
        plan.Player = kinds.Register("test.player", PlayerSchema, new PlayerKind(plan), priority: 4);
        if (plan.ExtraKind)
        {
            kinds.Register("test.extra", new ReplicatedSchema(), new PlayerKind(plan));
        }
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
        for (var slot = 1; slot < SessionService.MaxSlots; slot++)
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
