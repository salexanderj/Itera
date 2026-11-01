using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Physics;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="PlayerMovementSystem"/> moving its characters on the step's workers (<see cref="StepWorkers"/>): 40
/// characters walking, sprinting, jumping, crouching, and standing among crates, blocks, and craters that change between
/// steps end every step as the same characters moved on the main thread do, bit for bit, with the engine's walking alone
/// or a movement mode that asks every probe; and a step on the workers allocates nothing on any thread.
/// </summary>
[RunsLong]
public sealed class PlayerMovementWorkersTests : IDisposable
{
    private const int Characters = 40;
    private static readonly FixedStepTime Step = new(1f / 60, 1);

    private readonly WorkersWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Characters_moved_on_the_steps_workers_end_as_those_moved_on_the_main_thread_bit_for_bit(bool moded)
    {
        using var workers = new StepWorkers(4);
        using var here = World.Create();
        using var there = World.Create();
        var hereMover = _world.Mover();
        var thereMover = _world.Mover();
        var thereProbes = new MovementProbes(_world.Sources(), thereMover);
        using var serial = System(
            here,
            hereMover,
            moded ? new Probing(new MovementProbes(_world.Sources(), hereMover)) : null,
            null,
            null
        );
        using var parallel = System(there, thereMover, moded ? new Probing(thereProbes) : null, thereProbes, workers);
        var (expected, got) = (Spawn(here), Spawn(there));
        var random = new Random(moded ? 53 : 59);
        for (var i = 0; i < 60; i++)
        {
            _world.Change(random, step: 1, metres: 8);
        }

        int grounded = 0,
            jumped = 0,
            resting = 0,
            walls = 0;
        for (var step = 0; step < 120; step++)
        {
            _world.Change(random, step, metres: 8);
            for (var i = 0; i < Characters; i++)
            {
                var command = Command(random, step, i);
                (here.Get<PlayerCommand>(expected[i]), there.Get<PlayerCommand>(got[i])) = (command, command);
            }

            serial.Update(in Step);
            parallel.Update(in Step);
            for (var i = 0; i < Characters; i++)
            {
                var (want, have) = (Read(here, expected[i]), Read(there, got[i]));
                if (!want.Equals(have))
                {
                    Assert.Fail($"Step {step}, character {i}: on the workers {have}, on the main thread {want}.");
                }

                grounded += want.Contact.Grounded ? 1 : 0;
                jumped += want.Contact.Jumped ? 1 : 0;
                resting += want.Rest.Resting ? 1 : 0;
                walls += want.Probed.Wall ? 1 : 0;
            }
        }

        // The workers moved every character: the system's own mover none.
        Assert.Equal(0, thereMover.Moves);
        Assert.True(hereMover.Moves > 1000, $"{hereMover.Moves} moves on the main thread.");
        Assert.True(
            grounded > 1000 && jumped > 20 && resting > 200,
            $"{grounded} grounded, {jumped} jumps, {resting} at rest."
        );
        Assert.True(!moded || walls > 50, $"{walls} walls probed.");
    }

    [Fact]
    public void Update_on_the_steps_workers_allocates_nothing_on_any_thread()
    {
        using var workers = new StepWorkers(3);
        using var world = World.Create();
        var mover = _world.Mover();
        var probes = new MovementProbes(_world.Sources(), mover);
        using var system = System(world, mover, new Probing(probes), probes, workers);
        var characters = Spawn(world);
        var random = new Random(61);
        for (var i = 0; i < 60; i++)
        {
            _world.Change(random, step: 1, metres: 8);
        }

        // Each cycle the characters start again where they were made, so the last cycle reads chunks the first made.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Cycle(world, system, characters);
        }

        // A window no collection fell in, since one set off by another thread counts bytes never allocated; the workers'
        // count is the same window's.
        var helpers = long.MaxValue;
        var allocated = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            var counted = workers.AllocatedBytes;
            Cycle(world, system, characters);
            helpers = Math.Min(helpers, workers.AllocatedBytes - counted);
        });

        Assert.Equal((0L, 0L), (allocated, helpers));
        Assert.Equal(0, mover.Moves);
    }

    /// <summary>
    /// A second of steps from where the characters were made: every fourth stands, and the rest walk or sprint along
    /// their own way, turning back at its middle, and some jump.
    /// </summary>
    private static void Cycle(World world, PlayerMovementSystem system, Entity[] characters)
    {
        for (var i = 0; i < characters.Length; i++)
        {
            var feet = Start(i);
            world.Get<CharacterPosition>(characters[i]) = new CharacterPosition { Feet = feet, PreviousFeet = feet };
            world.Get<CharacterVelocity>(characters[i]) = default;
            world.Get<CharacterContact>(characters[i]) = default;
            if (world.Has<CharacterRest>(characters[i]))
            {
                world.Get<CharacterRest>(characters[i]).Resting = false;
            }
        }

        for (var step = 0; step < 60; step++)
        {
            for (var i = 0; i < characters.Length; i++)
            {
                world.Get<PlayerCommand>(characters[i]) =
                    i % 4 == 0
                        ? default
                        : new PlayerCommand
                        {
                            Move = new Vector2(0, 1),
                            Yaw = i * 0.7f + (step < 30 ? 0 : MathF.PI),
                            Buttons =
                                step == i % 45 ? PlayerButtons.Jump
                                : i % 3 == 0 ? PlayerButtons.Sprint
                                : 0,
                        };
            }

            system.Update(in Step);
        }
    }

    private PlayerMovementSystem System(
        World world,
        VoxelCharacterMover mover,
        IMovementMode? mode,
        MovementProbes? probes,
        StepWorkers? workers
    )
    {
        var system = new PlayerMovementSystem(
            world,
            mover,
            new PlayArea(_world.Voxels.Grid),
            NullLogger<PlayerMovementSystem>.Instance,
            mode: mode,
            probes: probes,
            workers: workers
        );
        system.Initialize();
        return system;
    }

    /// <summary>The characters, 1.4 m apart on a grid round the middle, dropped from a little over the ground.</summary>
    private static Entity[] Spawn(World world)
    {
        var characters = new Entity[Characters];
        for (var i = 0; i < Characters; i++)
        {
            var feet = Start(i);
            characters[i] = world.Create(
                new PlayerCommand(),
                new PlayerMode(),
                CharacterSize.ForHeight(1.7f),
                new CharacterPosition { Feet = feet, PreviousFeet = feet },
                new CharacterVelocity(),
                new CharacterContact { Waiting = true },
                new EyeSmoothing(),
                MovementStats.Default,
                new Probed { Index = i }
            );
        }

        return characters;
    }

    /// <summary>Where character <paramref name="i"/> starts: 1.4 m apart on a grid round the middle, a little over the ground.</summary>
    private static Vector3 Start(int i) => new((i % 8 - 3.5f) * 1.4f, 0.6f, (i / 8 - 2) * 1.4f);

    /// <summary>
    /// Character <paramref name="i"/>'s command: every fourth stands still, and the rest now and then walk, sprint, jump,
    /// or crouch a new way.
    /// </summary>
    private static PlayerCommand Command(Random random, int step, int i)
    {
        var yaw = random.NextSingle() * MathF.Tau;
        var roll = random.Next(10);
        if (i % 4 == 0 || (step + i) % 40 < 10)
        {
            return new PlayerCommand { Yaw = yaw };
        }

        return new PlayerCommand
        {
            Move = new Vector2(0, 1),
            Yaw = (i + step / 20) * 0.7f,
            Buttons =
                roll == 0 ? PlayerButtons.Jump
                : roll == 1 ? PlayerButtons.Crouch
                : roll < 5 ? PlayerButtons.Sprint
                : 0,
        };
    }

    private static Moved Read(World world, Entity character) =>
        new(
            world.Get<PlayerCommand>(character),
            world.Get<PlayerMode>(character),
            world.Get<CharacterSize>(character),
            world.Get<CharacterPosition>(character),
            world.Get<CharacterVelocity>(character),
            world.Get<CharacterContact>(character),
            world.Get<EyeSmoothing>(character),
            world.Has<CharacterRest>(character) ? world.Get<CharacterRest>(character) : default,
            world.Get<Probed>(character)
        );

    /// <summary>Everything a step writes of a character.</summary>
    private readonly record struct Moved(
        PlayerCommand Command,
        PlayerMode Mode,
        CharacterSize Size,
        CharacterPosition Position,
        CharacterVelocity Velocity,
        CharacterContact Contact,
        EyeSmoothing Eye,
        CharacterRest Rest,
        Probed Probed
    );

    /// <summary>What <see cref="Probing"/> found round a character at its last step.</summary>
    private struct Probed
    {
        public int Index;
        public bool Wall;
        public WallProbe WallProbe;
        public bool Ledge;
        public Vector3 Top;
        public bool Room;
        public bool Ground;
        public float GroundTop;
        public bool Hit;
        public SurfaceHit RayHit;
    }

    /// <summary>
    /// A movement mode that asks every probe round each character along its look and keeps the answers in its
    /// <see cref="Probed"/>, then takes the even characters' steps (walking along the look, rising at a ledge, jumping
    /// on Jump) and leaves the odd ones' to the engine. Its copy for a worker asks that worker's probes.
    /// </summary>
    private sealed class Probing(MovementProbes probes) : IMovementMode
    {
        public IMovementMode ForWorker(MovementProbes worker) => new Probing(worker);

        public bool Steer(in MovementModeInput input, ref MovementModeMove move)
        {
            ref var probed = ref input.World.Get<Probed>(input.Entity);
            var shape = new CharacterShape(input.Size.HeightMetres, input.Size.RadiusMetres, input.Size.StepMetres);
            var way = new Vector2(-MathF.Sin(input.Command.Yaw), -MathF.Cos(input.Command.Yaw));
            probed.Wall = probes.TryWallAhead(input.Feet, in shape, way, 0.8f, out probed.WallProbe);
            probed.Ledge =
                probed.Wall
                && probes.TryLedgeTop(input.Feet, in shape, in probed.WallProbe, 0.3f, 0.15f, out probed.Top);
            probed.Room = probes.HasRoom(input.Feet + new Vector3(0, 0.6f, 0), in shape);
            probed.Ground = probes.Mover.GroundTop(
                input.Feet + new Vector3(way.X, 0, way.Y) * 0.5f,
                0.1f,
                0.8f,
                0.8f,
                out probed.GroundTop
            );
            probed.Hit = probes.Sources.Raycast(
                input.Feet + new Vector3(0, input.Size.EyeMetres, 0),
                new Vector3(way.X, -0.5f, way.Y),
                8,
                out probed.RayHit
            );
            if (probed.Index % 2 == 1)
            {
                return false;
            }

            var up = input.Velocity.Y - input.Stats.Gravity * input.Seconds;
            move.Jumped = input.Contact.Grounded && (input.Command.Buttons & PlayerButtons.Jump) != 0;
            up =
                move.Jumped ? 4
                : probed.Ledge && input.Command.Move.Y > 0 ? 2
                : up;
            var speed = input.Command.Move.Y * ((input.Command.Buttons & PlayerButtons.Sprint) != 0 ? 6 : 3);
            move.Velocity = new Vector3(way.X * speed, up, way.Y * speed);
            move.StepMetres = input.Size.StepMetres;
            move.StepsInAir = true;
            return true;
        }
    }
}
