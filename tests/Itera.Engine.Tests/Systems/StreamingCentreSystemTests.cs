using System.Numerics;
using Arch.Core;
using BepuPhysics;
using Itera.Engine.Components;
using Itera.Engine.Networking;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

public sealed class StreamingCentreSystemTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private readonly VoxelWorld _voxels = new(
        Options.Create(new EngineOptions()),
        new FlatTestGenerator(new VoxelGrid(10)),
        NullLogger<VoxelWorld>.Instance
    );

    public void Dispose() => _voxels.Dispose();

    [Fact]
    public void Streaming_follows_a_walking_players_eye()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(new CameraPose { Position = new Vector3(50, 50, 50) }, new ActiveCamera());
        CreatePlayer(world, flying: false);

        system.Update(in Step);

        Assert.Equal(new Vector3(3, 2.5f, -7), Assert.Single(_voxels.Centers.ToArray()));
    }

    [Fact]
    public void Streaming_follows_the_camera_while_the_player_flies()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(new CameraPose { Position = new Vector3(50, 50, 50) }, new ActiveCamera());
        CreatePlayer(world, flying: true);

        system.Update(in Step);

        Assert.Equal(new Vector3(50, 50, 50), Assert.Single(_voxels.Centers.ToArray()));
    }

    [Fact]
    public void Without_a_player_or_a_camera_the_centres_stay()
    {
        using var world = World.Create();
        using var system = Create(world);

        system.Update(in Step);

        Assert.Empty(_voxels.Centers.ToArray());
    }

    [Fact]
    public void A_host_also_streams_around_remote_players_and_bodies_away_from_everyone()
    {
        using var world = World.Create();
        using var system = Create(world, SessionRole.Host);
        CreatePlayer(world, flying: false);
        world.Create(new RemotePlayer(), new CharacterPosition { Feet = new Vector3(40, 1, 0) });
        // One body near the player (its view holds it) and one far from everyone.
        CreateBody(world, new Vector3(4, 1, -7));
        CreateBody(world, new Vector3(-60, 1, 30));

        system.Update(in Step);

        Assert.Equal(
            [new Vector3(3, 2.5f, -7), new Vector3(40, 1, 0), new Vector3(-60, 1, 30)],
            _voxels.Centers.ToArray()
        );
        // The remote player's disc: level 0 alone, as wide as Engine:Voxels:RemotePlayerRadiusMetres (16 m by default).
        var remote = _voxels.StreamingCentres[1];
        Assert.Equal(16, remote.Level0RadiusMetres);
        Assert.Equal(1, remote.Levels);
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }

    [Fact]
    public void Only_a_host_streams_around_remote_players()
    {
        using var world = World.Create();
        using var system = Create(world, SessionRole.Offline);
        CreatePlayer(world, flying: false);
        world.Create(new RemotePlayer(), new CharacterPosition { Feet = new Vector3(40, 1, 0) });

        system.Update(in Step);

        Assert.Equal(new Vector3(3, 2.5f, -7), Assert.Single(_voxels.Centers.ToArray()));
    }

    [Fact]
    public void Offline_bodies_away_from_the_player_are_streamed_around_but_not_on_a_client()
    {
        using var offlineWorld = World.Create();
        using var offline = Create(offlineWorld, SessionRole.Offline);
        CreatePlayer(offlineWorld, flying: false);
        CreateBody(offlineWorld, new Vector3(-60, 1, 30));
        offline.Update(in Step);
        var offlineCentres = _voxels.Centers.ToArray();

        using var clientWorld = World.Create();
        using var client = Create(clientWorld, SessionRole.Client);
        CreatePlayer(clientWorld, flying: false);
        CreateBody(clientWorld, new Vector3(-60, 1, 30));
        client.Update(in Step);

        Assert.Equal([new Vector3(3, 2.5f, -7), new Vector3(-60, 1, 30)], offlineCentres);
        Assert.Equal(new Vector3(3, 2.5f, -7), Assert.Single(_voxels.Centers.ToArray()));
    }

    [Fact]
    public void A_fast_body_is_streamed_around_along_its_way_ahead()
    {
        using var world = World.Create();
        using var system = Create(world);
        CreatePlayer(world, flying: false);
        // 30 m/s along x (half a metre a step), and falling: its disc reaches 15 m ahead along the ground.
        world.Create(
            new LooseBody(),
            new BodyPose { Position = new Vector3(-60, 1, 30), PreviousPosition = new Vector3(-60.5f, 1.2f, 30) }
        );

        system.Update(in Step);

        var centres = _voxels.Centers.ToArray();
        Assert.Equal(2, centres.Length);
        Assert.True(Vector3.Distance(new Vector3(-52.5f, 1, 30), centres[1]) < 0.01f, $"{centres[1]}");
        // 3 m around it, and half the 15 m ahead rounded up to whole metres.
        Assert.Equal(StreamingCentreSystem.BodyRadiusMetres + 8, _voxels.StreamingCentres[1].Level0RadiusMetres);
    }

    /// <summary>
    /// Where the world places voxel objects, a body's disc is wider by the widest kind's reach, so the areas of objects
    /// that may touch it are filled.
    /// </summary>
    [Fact]
    public void A_bodys_disc_is_wider_by_the_objects_reach()
    {
        var kinds = new Itera.Engine.Objects.VoxelObjectKinds();
        var shape = new Itera.Engine.Objects.VoxelObjectShape(
            5,
            new VoxelCoord(80, 20, 80),
            new VoxelCoord(200, 0, 200)
        );
        shape.FillBox(0, 0, 0, 400, 100, 400, FlatTestGenerator.Stone);
        kinds.Register("block", shape);
        using var voxels = new VoxelWorld(
            Options.Create(new EngineOptions()),
            new Objects.PlacingGenerator([]),
            NullLogger<VoxelWorld>.Instance,
            kinds: kinds
        );
        using var world = World.Create();
        var system = new StreamingCentreSystem(world, voxels);
        system.Initialize();
        CreatePlayer(world, flying: false);
        CreateBody(world, new Vector3(-60, 1, 30));

        system.Update(in Step);

        Assert.Equal(kinds.MostReachCentimetres / 100.0, voxels.ObjectReachMetres);
        Assert.Equal(
            StreamingCentreSystem.BodyRadiusMetres + Math.Ceiling(voxels.ObjectReachMetres),
            voxels.StreamingCentres[1].Level0RadiusMetres
        );
        system.Dispose();
    }

    /// <summary>
    /// On a world of 1 cm voxels and a 16 m view radius, where a giant's body collides with level 2 (at 10 cm it
    /// collides with level 0, as every body does): its disc streams levels 0 to 2.
    /// </summary>
    [Fact]
    public void A_giants_body_away_from_the_player_is_streamed_around_at_levels_0_to_2()
    {
        using var voxels = OneCentimetreWorld(new VoxelSettings { StreamingRadiusMetres = 16, Levels = 7 });
        using var physics = new PhysicsTestHarness();
        using var world = World.Create();
        var system = new StreamingCentreSystem(world, voxels, SessionRole.Offline, physics.Bodies);
        system.Initialize();
        CreatePlayer(world, flying: false);
        // Bodies need no Bepu body here: only the level each collides with is read.
        BodyHandle[] giants = [new(1), new(2)];
        foreach (var giant in giants)
        {
            physics.Physics.Kinds.Set(giant, BodyKind.Loose, VoxelColliders.CoarseLevel);
        }

        // A giant's body 73 m from the player, past its level-2 disc (64 m); one 27 m away, past its level-0 disc
        // (16 m) but well inside its level-2 one; and a ball beside that.
        CreateBody(world, new Vector3(-60, 1, 30), giants[0]);
        CreateBody(world, new Vector3(30, 1, 0), giants[1]);
        CreateBody(world, new Vector3(30, 1, 2), new BodyHandle(3));

        system.Update(in Step);
        system.Dispose();

        var centres = voxels.StreamingCentres.ToArray().Skip(1).OrderBy(centre => centre.Position.X).ToArray();
        Assert.Equal(2, centres.Length);
        Assert.Equal(new Vector3(-60, 1, 30), centres[0].Position);
        Assert.Equal(VoxelColliders.CoarseLevel + 1, centres[0].Levels);
        Assert.Equal(new Vector3(30, 1, 2), centres[1].Position);
        Assert.Equal(1, centres[1].Levels);
    }

    [Fact]
    public void A_giants_body_past_the_views_own_level_2_disc_is_streamed_around()
    {
        // On 1 cm voxels with a 16 m radius, level 0 drawn to 16 m and each level after it 1.5 times as far: the view
        // loads level 2 to 46.24 m, not to 4 times its level-0 radius (74.24 m).
        using var voxels = OneCentimetreWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 16,
                Levels = 7,
                Level0ReachMetres = 16,
                ReachGrowth = 1.5,
            }
        );
        using var physics = new PhysicsTestHarness();
        using var world = World.Create();
        var system = new StreamingCentreSystem(world, voxels, SessionRole.Offline, physics.Bodies);
        system.Initialize();
        CreatePlayer(world, flying: false);
        BodyHandle[] giants = [new(1), new(2)];
        foreach (var giant in giants)
        {
            physics.Physics.Kinds.Set(giant, BodyKind.Loose, VoxelColliders.CoarseLevel);
        }

        // From the player at (3, -7), with the body's own 3 m disc 12 m wide at level 2: one giant's body 55 m away, past
        // the view's level-2 disc less that (34.24 m) though within 4 times the level-0 disc less it (62.24 m); one 25 m
        // away, within both.
        CreateBody(world, new Vector3(58, 1, -7), giants[0]);
        CreateBody(world, new Vector3(28, 1, -7), giants[1]);

        system.Update(in Step);
        system.Dispose();

        Assert.True(voxels.StreamingCentres[0].View);
        Assert.Equal(46.24, voxels.RadiusMetres(VoxelColliders.CoarseLevel), 6);
        var centre = Assert.Single(voxels.StreamingCentres.ToArray().Skip(1));
        Assert.Equal(new Vector3(58, 1, -7), centre.Position);
        Assert.Equal(VoxelColliders.CoarseLevel + 1, centre.Levels);
    }

    [Fact]
    public void A_host_streams_around_at_most_32_bodies()
    {
        using var world = World.Create();
        using var system = Create(world, SessionRole.Host);
        CreatePlayer(world, flying: false);
        for (var i = 0; i < 40; i++)
        {
            CreateBody(world, new Vector3(100 + 10 * i, 1, 0));
        }

        system.Update(in Step);

        Assert.Equal(1 + StreamingCentreSystem.MaxBodyCentres, _voxels.Centers.Length);
    }

    [Fact]
    public void The_owner_streams_around_streamed_characters_away_from_every_player_and_a_client_none()
    {
        using var offlineWorld = World.Create();
        using var offline = Create(offlineWorld, SessionRole.Offline);
        CreatePlayer(offlineWorld, flying: false);
        // One far from the player, one its view holds, and one far off but not marked.
        CreateCharacter(offlineWorld, new Vector3(200, 1, 0));
        CreateCharacter(offlineWorld, new Vector3(5, 1, -7));
        offlineWorld.Create(new CharacterPosition { Feet = new Vector3(-200, 1, 0) });
        offline.Update(in Step);
        var offlineCentres = _voxels.Centers.ToArray();
        var far = _voxels.StreamingCentres[1];

        using var clientWorld = World.Create();
        using var client = Create(clientWorld, SessionRole.Client);
        CreatePlayer(clientWorld, flying: false);
        CreateCharacter(clientWorld, new Vector3(200, 1, 0));
        client.Update(in Step);

        Assert.Equal([new Vector3(3, 2.5f, -7), new Vector3(200, 1, 0)], offlineCentres);
        // As a remote player's: level 0 alone, as wide as Engine:Voxels:RemotePlayerRadiusMetres.
        Assert.Equal((16d, 1), (far.Level0RadiusMetres, far.Levels));
        Assert.Equal(new Vector3(3, 2.5f, -7), Assert.Single(_voxels.Centers.ToArray()));
    }

    [Fact]
    public void A_host_streams_around_at_most_32_streamed_characters()
    {
        using var world = World.Create();
        using var system = Create(world, SessionRole.Host);
        CreatePlayer(world, flying: false);
        for (var i = 0; i < 40; i++)
        {
            CreateCharacter(world, new Vector3(100 + 40 * i, 1, 0));
        }

        system.Update(in Step);

        Assert.Equal(1 + StreamingCentreSystem.MaxCharacterCentres, _voxels.Centers.Length);
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world);
        CreatePlayer(world, flying: false);
        CreateCharacter(world, new Vector3(200, 1, 0));

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }

    /// <summary>A character the world's owner streams around, its feet at <paramref name="feet"/>.</summary>
    private static void CreateCharacter(World world, Vector3 feet) =>
        world.Create(new StreamedCharacter(), new CharacterPosition { Feet = feet });

    private StreamingCentreSystem Create(World world, SessionRole role = SessionRole.Offline)
    {
        var system = new StreamingCentreSystem(world, _voxels, role);
        system.Initialize();
        return system;
    }

    /// <summary>A loose body at rest at <paramref name="position"/>.</summary>
    private static void CreateBody(World world, Vector3 position, BodyHandle body = default) =>
        world.Create(new LooseBody { Body = body }, new BodyPose { Position = position, PreviousPosition = position });

    private static void CreatePlayer(World world, bool flying) =>
        world.Create(
            new LocalPlayer(),
            new PlayerMode { Flying = flying },
            new CharacterPosition { Feet = new Vector3(3, 1, -7) },
            new CharacterSize { EyeMetres = 1.5f }
        );

    /// <summary>A flat world on 1 cm voxels, with <paramref name="settings"/>.</summary>
    private static VoxelWorld OneCentimetreWorld(VoxelSettings settings) =>
        new(
            Options.Create(new EngineOptions { Voxels = settings }),
            new FlatTestGenerator(VoxelGrid.Unit),
            NullLogger<VoxelWorld>.Instance
        );
}
