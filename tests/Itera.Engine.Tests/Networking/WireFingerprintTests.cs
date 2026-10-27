using System.Numerics;
using Itera.Engine.Networking;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// The engine's part of the wire, pinned with the protocol version (<see cref="WireFingerprint"/>): every message a
/// host and a client send in a join with a payload holding an edit and a lift, a host's tunable not at its default, a
/// live edit, a live lift, and a changed tunable, and a client's leaving, recorded as sent. Change their bytes and this fails until <see cref="SessionProtocol.Version"/>
/// rises.
/// </summary>
public sealed class WireFingerprintTests : IDisposable
{
    private static readonly BoxesGenerator s_generator = new(
        200,
        new VoxelBox(-40, 200, -40, 40, 260, 40, BoxesGenerator.Pattern)
    );

    private readonly FakeNetwork _network = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    [Fact]
    public void The_engines_messages_have_the_shape_this_protocol_pins()
    {
        var hostWorld = Own(TestWorlds.Load(s_generator, Vector3.Zero, seed: 17));
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 250, 0), 15, VoxelEditOperation.Clear));
        Assert.True(
            hostWorld.TryLift(
                VoxelEdit.Box(new VoxelCoord(-10, 230, -10), new VoxelCoord(6, 5, 4), VoxelEditOperation.Lift),
                out _
            )
        );
        var hostTransport = new FakeTransport(_network) { Recorded = [] };
        var hostTunables = new Itera.Engine.Settings.Tunables();
        var speed = hostTunables.Register("test.speed", 4.5f, 0, 10, "");
        hostTunables.TrySet(speed, 6);
        var host = Own(
            Session(
                new SessionSettings { Mode = SessionMode.Host, PlayerName = "Host" },
                hostWorld,
                hostTransport,
                () => { },
                17,
                hostTunables
            )
        );
        host.Initialize();
        var clientWorld = Own(
            new VoxelWorld(
                new VoxelSettings
                {
                    StreamingRadiusMetres = 2,
                    Levels = 1,
                    MemoryBudgetMegabytes = 64,
                    Seed = 99,
                },
                0,
                s_generator,
                NullLogger<VoxelWorld>.Instance
            )
        );
        var clientTransport = new FakeTransport(_network) { Recorded = [] };
        var client = Own(
            Session(
                new SessionSettings
                {
                    Mode = SessionMode.Join,
                    Address = "host:47600",
                    PlayerName = "Client",
                },
                clientWorld,
                clientTransport,
                host.Poll,
                99
            )
        );

        client.Initialize();
        clientWorld.Initialize(client.Match.Seed);
        client.LoadJoinedWorld();
        hostWorld.Apply(
            VoxelEdit.Box(
                new VoxelCoord(30, 240, 0),
                new VoxelCoord(3, 3, 3),
                VoxelEditOperation.Set,
                BoxesGenerator.Stone
            ),
            maker: 1
        );
        Assert.True(
            hostWorld.TryLift(VoxelEdit.Sphere(new VoxelCoord(-20, 240, 20), 5, VoxelEditOperation.Lift), out _)
        );
        hostTunables.TrySet(speed, 7.25f);
        host.AfterFixedStep();
        client.Poll();
        client.Stop();
        host.Poll();

        Assert.Equal(4, clientWorld.EditCount);
        WireFingerprint.AssertPinned(
            49,
            "432E3D2EB6BBE344",
            WireFingerprint.Of([.. hostTransport.Recorded, .. clientTransport.Recorded])
        );
    }

    /// <summary>
    /// The replication's messages: a host's snapshots of three movers (whole, then as changes) with the owner's block of
    /// the client's predicted player, and the client's replies with its player's commands, walking, with its camera
    /// away from its body, and flying the debug camera, over ten steps after a join.
    /// </summary>
    [Fact]
    public void The_replications_messages_have_the_shape_this_protocol_pins()
    {
        using var harness = Replication.ReplicationHarness.Start(
            new Replication.MoverPlan
            {
                Count = 3,
                Speed = 2,
                Characters = true,
            },
            clientPlan: new Replication.MoverPlan
            {
                Characters = true,
                Script = tick => new Components.PlayerCommand
                {
                    Move = new System.Numerics.Vector2(0, 1),
                    Yaw = tick * 0.01f,
                    Buttons =
                        tick % 30 == 0 ? Components.PlayerButtons.Jump
                        : tick % 30 >= 25 ? Components.PlayerButtons.Fly
                        : tick % 30 >= 22 ? Components.PlayerButtons.Viewpoint
                        : Components.PlayerButtons.None,
                    Viewpoint = new System.Numerics.Vector3(0, 3, tick * -0.01f),
                },
            }
        );
        harness.Step(20);
        harness.HostTransport.Recorded = [];
        harness.ClientTransport.Recorded = [];

        harness.Step(10);

        Assert.Contains(harness.HostTransport.Recorded, message => message[0] == (byte)MessageKind.Snapshot);
        Assert.Contains(
            harness.ClientTransport.Recorded,
            message => message[0] == (byte)MessageKind.Reply && message.Length > 40
        );
        WireFingerprint.AssertPinned(
            49,
            "A85DC48B1199175C",
            WireFingerprint.Of([.. harness.HostTransport.Recorded, .. harness.ClientTransport.Recorded])
        );
    }

    private T Own<T>(T owned)
        where T : IDisposable
    {
        _owned.Add(owned);
        return owned;
    }

    private static SessionService Session(
        SessionSettings settings,
        VoxelWorld world,
        FakeTransport transport,
        Action wait,
        ulong seed,
        Itera.Engine.Settings.Tunables? tunables = null
    )
    {
        var clock = 0.0;
        return new SessionService(
            settings,
            60,
            seed,
            0,
            world,
            transport,
            NullLogger<SessionService>.Instance,
            () => clock,
            () =>
            {
                clock += 0.01;
                wait();
            },
            tunables: tunables
        );
    }
}
