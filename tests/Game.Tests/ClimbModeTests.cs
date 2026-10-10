using System.Numerics;
using Arch.Core;
using Game.Components;
using Itera.Engine.Components;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Tests;

/// <summary>
/// <see cref="ClimbMode"/> through the engine's <see cref="PlayerMovementSystem"/>, over mesh ground with climbable
/// walls (a 4 m one, a 2 m one) and a plain one: Space at a climbable wall grabs it instead of jumping; W climbs at 1.5
/// m/s and nothing held hangs; climbing past a wall's top pulls up onto it; Space on the wall jumps off away and up; Ctrl
/// lets go; Space at a plain wall jumps as ever; and climbing allocates nothing.
/// </summary>
public sealed class ClimbModeTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);

    // Looking along +x (forward is (-sin, -cos) of the yaw), and along -x.
    private const float East = -MathF.PI / 2;
    private const float West = MathF.PI / 2;

    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);
    private readonly MeshColliders _colliders;
    private readonly VoxelCharacterMover _mover;
    private readonly MovementProbes _probes;
    private Entity _player = Entity.Null;

    public ClimbModeTests()
    {
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
        Box(new Vector3(-10, -1, -10), new Vector3(10, 0, 10), 0);
        // The tall climbable wall's face at x = 1 m round z = 0, the short one's round z = 4, a plain one's at x = -1.
        Box(new Vector3(1, 0, -1), new Vector3(2, 4, 1), GameSurfaceTags.Climbable);
        Box(new Vector3(1, 0, 3), new Vector3(2, 2, 5), GameSurfaceTags.Climbable);
        Box(new Vector3(-2, 0, -1), new Vector3(-1, 4, 1), 0);
        _mover = new VoxelCharacterMover(new NothingPassable(), _colliders, cellCentimetres: 10);
        _probes = new MovementProbes(new CollisionSources(_colliders, new GameSurfaceTags()), _mover);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
    }

    [Fact]
    public void Space_at_a_climbable_wall_grabs_it_instead_of_jumping()
    {
        using var world = World.Create();
        using var system = Start(world, new Vector3(0.5f, 0, 0), East);

        Run(world, system, 1, Buttons(PlayerButtons.Jump));

        Assert.Equal(ClimbState.Climbing, Climber(world).State);
        Assert.False(Contact(world).Jumped);
        Assert.Equal(0, Feet(world).Y, 0.01f);
    }

    [Fact]
    public void W_climbs_at_its_speed_and_nothing_held_hangs()
    {
        using var world = World.Create();
        using var system = Start(world, new Vector3(0.5f, 0, 0), East);
        Run(world, system, 1, Buttons(PlayerButtons.Jump));

        Run(world, system, 30, new PlayerCommand { Yaw = East, Move = Vector2.UnitY });
        var climbed = Feet(world).Y;
        Run(world, system, 30, new PlayerCommand { Yaw = East });

        Assert.Equal(ClimbMode.ClimbSpeed * 0.5f, climbed, 0.03f);
        Assert.Equal(climbed, Feet(world).Y, 0.01f);
        Assert.Equal(ClimbState.Climbing, Climber(world).State);
    }

    [Fact]
    public void Climbing_past_a_walls_top_pulls_up_onto_it()
    {
        using var world = World.Create();
        using var system = Start(world, new Vector3(0.5f, 0, 4), East);
        Run(world, system, 1, Buttons(PlayerButtons.Jump));

        // W held until the climb ends: on, the player would walk over the 1 m top and off its far side.
        var steps = 0;
        do
        {
            Run(world, system, 1, new PlayerCommand { Yaw = East, Move = Vector2.UnitY });
        } while (Climber(world).State != ClimbState.Free && ++steps < 300);
        Run(world, system, 30, new PlayerCommand { Yaw = East });

        Assert.InRange(steps, 60, 120);
        Assert.True(Contact(world).Grounded);
        Assert.Equal(2, Feet(world).Y, 0.02f);
        Assert.InRange(Feet(world).X, 1.2f, 1.8f);
    }

    [Fact]
    public void Space_on_the_wall_jumps_off_away_and_up()
    {
        using var world = World.Create();
        using var system = Start(world, new Vector3(0.5f, 0, 0), East);
        Run(world, system, 1, Buttons(PlayerButtons.Jump));
        Run(world, system, 20, new PlayerCommand { Yaw = East, Move = Vector2.UnitY });
        var before = Feet(world);

        Run(world, system, 1, Buttons(PlayerButtons.Jump));
        var velocity = world.Get<CharacterVelocity>(_player).MetresPerSecond;
        Run(world, system, 10, new PlayerCommand { Yaw = East });

        Assert.Equal(ClimbState.Free, Climber(world).State);
        Assert.True(velocity.X < -3 && velocity.Y > 4, $"Velocity {velocity}.");
        Assert.True(Feet(world).X < before.X - 0.3f, $"Feet {Feet(world)}.");
    }

    [Fact]
    public void Ctrl_lets_go_and_the_player_falls()
    {
        using var world = World.Create();
        using var system = Start(world, new Vector3(0.5f, 0, 0), East);
        Run(world, system, 1, Buttons(PlayerButtons.Jump));
        Run(world, system, 30, new PlayerCommand { Yaw = East, Move = Vector2.UnitY });

        Run(world, system, 1, Buttons(PlayerButtons.Crouch));
        Run(world, system, 60, new PlayerCommand { Yaw = East });

        Assert.Equal(ClimbState.Free, Climber(world).State);
        Assert.Equal(0, Feet(world).Y, 0.01f);
        Assert.True(Contact(world).Grounded);
    }

    [Fact]
    public void Space_at_a_wall_that_is_not_climbable_jumps_as_ever()
    {
        using var world = World.Create();
        using var system = Start(world, new Vector3(-0.5f, 0, 0), West);

        Run(world, system, 1, Buttons(PlayerButtons.Jump));

        Assert.Equal(ClimbState.Free, Climber(world).State);
        Assert.True(Contact(world).Jumped);
        Assert.True(Feet(world).Y > 0.05f);
    }

    [Fact]
    public void Climbing_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Start(world, new Vector3(0.5f, 0, 0), East);
        Run(world, system, 1, Buttons(PlayerButtons.Jump));
        world.Set(_player, new PlayerCommand { Yaw = East, Move = Vector2.UnitY });

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step, warmup: 5, measured: 60));
        Assert.Equal(ClimbState.Climbing, Climber(world).State);
    }

    /// <summary>A command holding <paramref name="buttons"/>, looking where the player looks.</summary>
    private static PlayerCommand Buttons(PlayerButtons buttons) => new() { Yaw = float.NaN, Buttons = buttons };

    private int Box(Vector3 min, Vector3 max, uint tags)
    {
        Vector3 C(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        // Each face counter-clockwise from outside.
        Vector3[] corners =
        [
            C(0, 0, 0),
            C(1, 0, 0),
            C(1, 0, 1),
            C(0, 0, 1),
            C(0, 1, 0),
            C(1, 1, 0),
            C(1, 1, 1),
            C(0, 1, 1),
        ];
        uint[] indices =
        [
            0,
            1,
            2,
            0,
            2,
            3, // bottom
            4,
            7,
            6,
            4,
            6,
            5, // top
            0,
            4,
            5,
            0,
            5,
            1, // -z
            3,
            2,
            6,
            3,
            6,
            7, // +z
            0,
            3,
            7,
            0,
            7,
            4, // -x
            1,
            5,
            6,
            1,
            6,
            2, // +x
        ];
        return _colliders.Add(corners, indices, Matrix4x4.Identity, tags);
    }

    /// <summary>The engine's movement with the climbing mode, and a 1.7 m player standing at <paramref name="feet"/>.</summary>
    private PlayerMovementSystem Start(World world, Vector3 feet, float yaw)
    {
        var system = new PlayerMovementSystem(
            world,
            _mover,
            PlayArea.WithoutTerrain,
            NullLogger<PlayerMovementSystem>.Instance,
            mode: new ClimbMode(_probes)
        );
        system.Initialize();
        _player = world.Create(
            new LocalPlayer(),
            new PlayerCommand { Yaw = yaw },
            new PlayerMode(),
            CharacterSize.ForHeight(1.7f),
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            new CharacterVelocity(),
            new CharacterContact(),
            new EyeSmoothing(),
            MovementStats.Default,
            new Climber()
        );
        _yaw = yaw;
        Run(world, system, 10, new PlayerCommand { Yaw = yaw });
        return system;
    }

    private float _yaw;

    /// <summary><paramref name="ticks"/> steps with <paramref name="command"/>, its yaw the player's when not a number.</summary>
    private void Run(World world, PlayerMovementSystem system, int ticks, PlayerCommand command)
    {
        if (float.IsNaN(command.Yaw))
        {
            command.Yaw = _yaw;
        }

        for (var i = 0; i < ticks; i++)
        {
            world.Set(_player, command);
            system.Update(in Step);
        }
    }

    private Climber Climber(World world) => world.Get<Climber>(_player);

    private CharacterContact Contact(World world) => world.Get<CharacterContact>(_player);

    private Vector3 Feet(World world) => world.Get<CharacterPosition>(_player).Feet;
}
