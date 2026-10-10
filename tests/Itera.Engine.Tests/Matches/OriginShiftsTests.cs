using System.Numerics;
using System.Reflection;
using Arch.Core;
using Arch.System;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Components;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// <see cref="OriginShifts"/>: the origin moves to the whole metres nearest the point asked, and everything the engine
/// keeps in local positions moves the other way (the components' positions, current and previous, and nothing else of
/// theirs; Bepu's bodies and statics; the lights' flashes, the particles' queued bursts and attractors, the retired
/// models), then the game's handlers hear of it; every component's vector field is either moved or listed as no
/// position; it moves only between steps and frames or in the first fixed system's update; and a shift allocates
/// nothing.
/// </summary>
public sealed class OriginShiftsTests : IDisposable
{
    /// <summary>The components' vector fields that are not positions: velocities, directions, and boxes about a pivot.</summary>
    private static readonly string[] s_notPositions =
    [
        "PlayerCommand.Move",
        "CharacterVelocity.MetresPerSecond",
        "CharacterVelocity.Wanted",
        "CharacterVelocity.Push",
        "CharacterContact.Downhill",
        "CharacterContact.WallNormal",
        "Climber.Normal",
        "BodyBox.Min",
        "BodyBox.Max",
        "CharacterStandIn.Placed",
        "CharacterStandIn.Push",
    ];

    private static readonly Vector3 s_by = new(300, -20, -512);

    private readonly PhysicsWorld _physics = new(
        NullLogger<PhysicsWorld>.Instance,
        Options.Create(new EngineOptions { Physics = { Threads = 1 } })
    );
    private readonly EcsWorld _ecs = new(NullLogger<EcsWorld>.Instance, new SystemTimes());
    private readonly VoxelModels _models = new();
    private readonly PointLights _lights = new();
    private readonly ParticleEmitter _particles = new(
        Options.Create(new EngineOptions()),
        NullLogger<ParticleEmitter>.Instance
    );
    private readonly WorldOrigin _origin = new();
    private readonly Recorder _handler = new();
    private readonly OriginShifts _shifts;

    public OriginShiftsTests()
    {
        _physics.Initialize();
        _shifts = new OriginShifts(
            _origin,
            _physics,
            _ecs,
            _models,
            _lights,
            _particles,
            [_handler],
            NullLogger<OriginShifts>.Instance
        );
    }

    public void Dispose()
    {
        _ecs.Dispose();
        _models.Dispose();
        _physics.Dispose();
    }

    [Fact]
    public void The_origin_moves_to_the_nearest_whole_metres_and_what_the_engine_keeps_moves_the_other_way()
    {
        var world = _ecs.World;
        var player = world.Create(
            new CameraPose { Position = new Vector3(1, 2, 3) },
            new CharacterPosition { Feet = new Vector3(4, 5, 6), PreviousFeet = new Vector3(4, 5, 5.9f) },
            new Climber { Target = new Vector3(7, 8, 9), Normal = new Vector2(1, 0) }
        );
        var mesh = world.Create(
            new MeshPlacement { Position = new Vector3(-1, 0, 2), PreviousPosition = new Vector3(-1.1f, 0, 2) }
        );
        var body = world.Create(
            new BodyPose { Position = new Vector3(0, 3, 0), PreviousPosition = new Vector3(0, 3.1f, 0) },
            new BodyBox { Min = new Vector3(-0.1f), Max = new Vector3(0.1f) }
        );
        _lights.BeginFrame(0);
        _lights.Flash(new Vector3(10, 1, 0), Vector3.One, 5, 10);
        var recipe = _particles.Recipes.Register("test", new ParticleRecipe { LifetimeMin = 1, LifetimeMax = 2 });
        _particles.Emit(
            new ParticleBurst
            {
                Recipe = recipe,
                Count = 1,
                Origin = new Vector3(2, 2, 2),
            }
        );
        _particles.SetAttractor(1, new Vector3(3, 3, 3));
        _particles.StressOrigin = new Vector3(4, 4, 4);
        var cube = _models.Add([_models.Encode(Cube(4))], new VoxelCoord(4, 4, 4));
        _models.Retire(cube, 0, new Vector3(5, 0, 5), Quaternion.Identity, 0);
        var floor = _physics.Simulation.Statics.Add(
            new StaticDescription(new Vector3(0, -0.5f, 0), _physics.Simulation.Shapes.Add(new Box(10, 1, 10)))
        );
        var sphere = new Sphere(0.2f);
        var ball = _physics.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(new Vector3(1, 1, 1)),
                sphere.ComputeInertia(1),
                new CollidableDescription(_physics.Simulation.Shapes.Add(sphere), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );

        // -511.5 rounds away from zero.
        Assert.True(_shifts.ShiftTo(new WorldPoint(300.4, -19.6, -511.5)));

        Assert.Equal(new VoxelCoord(30_000, -2_000, -51_200), _origin.Centimetres);
        Assert.Equal(new Vector3(1, 2, 3) - s_by, world.Get<CameraPose>(player).Position);
        var feet = world.Get<CharacterPosition>(player);
        Assert.Equal((new Vector3(4, 5, 6) - s_by, new Vector3(4, 5, 5.9f) - s_by), (feet.Feet, feet.PreviousFeet));
        var climber = world.Get<Climber>(player);
        Assert.Equal((new Vector3(7, 8, 9) - s_by, new Vector2(1, 0)), (climber.Target, climber.Normal));
        var placement = world.Get<MeshPlacement>(mesh);
        Assert.Equal(
            (new Vector3(-1, 0, 2) - s_by, new Vector3(-1.1f, 0, 2) - s_by),
            (placement.Position, placement.PreviousPosition)
        );
        var pose = world.Get<BodyPose>(body);
        Assert.Equal(
            (new Vector3(0, 3, 0) - s_by, new Vector3(0, 3.1f, 0) - s_by),
            (pose.Position, pose.PreviousPosition)
        );
        Assert.Equal(new Vector3(-0.1f), world.Get<BodyBox>(body).Min);
        Assert.Equal(new Vector3(10, 1, 0) - s_by, _lights.Frame(0)[0].Position);
        var bursts = new GpuParticleBurst[4];
        _particles.Drain(bursts, 0);
        Assert.Equal(new Vector3(2, 2, 2) - s_by, bursts[0].Origin);
        Assert.Equal(new Vector4(new Vector3(3, 3, 3) - s_by, 1), _particles.Attractors[1]);
        Assert.Equal(default, _particles.Attractors[2]);
        Assert.Equal(new Vector3(4, 4, 4) - s_by, _particles.StressOrigin);
        Assert.Equal(new Vector3(5, 0, 5) - s_by, _models.Retired[0].Corner);
        Assert.Equal(new Vector3(0, -0.5f, 0) - s_by, _physics.Simulation.Statics[floor].Pose.Position);
        Assert.Equal(new Vector3(1, 1, 1) - s_by, _physics.Simulation.Bodies[ball].Pose.Position);
        Assert.Equal([new OriginShift(default, new VoxelCoord(30_000, -2_000, -51_200))], _handler.Shifts);
        Assert.Equal(s_by, _handler.Shifts[0].Metres);
        Assert.Equal(new Vector3(1, 2, 3) - s_by, _handler.Shifts[0].Apply(new Vector3(1, 2, 3)));

        // Already there: nothing moves and no handler hears of it.
        Assert.False(_shifts.ShiftTo(new WorldPoint(299.6, -20.4, -512)));
        Assert.Single(_handler.Shifts);
        Assert.Equal(1, _origin.Moves);
    }

    [Fact]
    public void Every_vector_of_every_component_is_a_moved_position_or_listed_as_none()
    {
        var types = typeof(CameraPose)
            .Assembly.GetTypes()
            .Where(type => type.IsValueType && type.Namespace == typeof(CameraPose).Namespace && !type.IsEnum)
            .ToArray();
        var fields = types
            .SelectMany(type =>
                type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(field => field.FieldType == typeof(Vector3) || field.FieldType == typeof(Vector2))
                    .Select(field => (Type: type, Field: field, Name: $"{type.Name}.{field.Name}"))
            )
            .ToArray();

        foreach (var (_, _, name) in fields)
        {
            Assert.True(
                OriginShifts.ShiftedComponents.Contains(name) ^ s_notPositions.Contains(name),
                $"{name} must be moved by the shift (OriginShifts.ShiftedComponents) or listed here as no position."
            );
        }

        Assert.All(OriginShifts.ShiftedComponents, name => Assert.Contains(name, fields.Select(field => field.Name)));

        // Each component with a vector, made with every vector at (1, 2, 3): after the shift only the positions moved.
        var world = _ecs.World;
        var made = new List<(Entity Entity, Type Type)>();
        foreach (var type in fields.Select(field => field.Type).Distinct())
        {
            var component = Activator.CreateInstance(type)!;
            foreach (var (_, field, _) in fields.Where(field => field.Type == type))
            {
                field.SetValue(
                    component,
                    field.FieldType == typeof(Vector3) ? new Vector3(1, 2, 3) : new Vector2(1, 2)
                );
            }

            var entity = world.Create([type]);
            world.Set(entity, component);
            made.Add((entity, type));
        }

        _shifts.ShiftTo(new WorldPoint(s_by.X, s_by.Y, s_by.Z));

        foreach (var (entity, type) in made)
        {
            var component = world.Get(entity, type)!;
            foreach (var (_, field, name) in fields.Where(field => field.Type == type))
            {
                object expected =
                    field.FieldType == typeof(Vector2) ? new Vector2(1, 2)
                    : OriginShifts.ShiftedComponents.Contains(name) ? new Vector3(1, 2, 3) - s_by
                    : new Vector3(1, 2, 3);
                Assert.True(expected.Equals(field.GetValue(component)), $"{name} is {field.GetValue(component)}.");
            }
        }
    }

    [Fact]
    public void It_moves_between_steps_and_frames_and_in_the_first_fixed_systems_update_only()
    {
        var first = new Shifting(_ecs.World, _shifts, new WorldPoint(1, 0, 0));
        var second = new Shifting(_ecs.World, _shifts, new WorldPoint(2, 0, 0));
        var frame = new ShiftingFrame(_ecs.World, _shifts, new WorldPoint(3, 0, 0));
        _ecs.Initialize([first, second], [frame]);

        _ecs.FixedUpdate(new FixedStepTime(1f / 60, 1));
        _ecs.Update(new FrameTime(1f / 60, 0, 0, 0));

        // The first system's update moved it; its before- and after-update, the second's, and the frame's may not.
        Assert.Null(first.Failure);
        Assert.IsType<InvalidOperationException>(first.BeforeFailure);
        Assert.IsType<InvalidOperationException>(first.AfterFailure);
        Assert.IsType<InvalidOperationException>(second.Failure);
        Assert.IsType<InvalidOperationException>(frame.Failure);
        Assert.Equal(new VoxelCoord(100, 0, 0), _origin.Centimetres);
        Assert.True(_shifts.ShiftTo(new WorldPoint(4, 0, 0)));
        Assert.Throws<ArgumentException>(() => _shifts.ShiftTo(new WorldPoint(double.NaN, 0, 0)));
        Assert.Throws<ArgumentException>(() => _shifts.ShiftTo(new WorldPoint(0, 0, 170_000)));
    }

    [Fact]
    public void A_shift_allocates_nothing()
    {
        // Something of each kind it moves: components, a body and a static, a flash, a queued burst, an attractor, and a
        // retired model.
        _ecs.World.Create(new CameraPose(), new CharacterPosition(), new Climber());
        _ecs.World.Create(new BodyPose(), new MeshPlacement());
        var sphere = new Sphere(0.2f);
        _physics.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(Vector3.Zero),
                sphere.ComputeInertia(1),
                new CollidableDescription(_physics.Simulation.Shapes.Add(sphere), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
        _physics.Simulation.Statics.Add(new StaticDescription(Vector3.Zero, _physics.Simulation.Shapes.Add(sphere)));
        _lights.BeginFrame(0);
        _lights.Flash(Vector3.Zero, Vector3.One, 1, 100);
        var recipe = _particles.Recipes.Register("test", new ParticleRecipe { LifetimeMin = 1, LifetimeMax = 2 });
        _particles.Emit(new ParticleBurst { Recipe = recipe, Count = 1 });
        _particles.SetAttractor(1, Vector3.Zero);
        _models.Retire(_models.Add([_models.Encode(Cube(4))], new VoxelCoord(4, 4, 4)), 0, default, default, 0);
        for (var i = 0; i < 100; i++)
        {
            _shifts.ShiftTo(new WorldPoint(i % 2 * 600, 0, 0));
        }

        // Room for what the test's own handler records.
        _handler.Shifts.Capacity = 10_000;

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                _shifts.ShiftTo(new WorldPoint(i % 2 * 600, 0, 0));
            }
        });

        Assert.Equal(0, bytes);
    }

    private static byte[] Cube(int size)
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    voxels[x + (y << VoxelConstants.ChunkShift) + (z << (2 * VoxelConstants.ChunkShift))] = 1;
                }
            }
        }

        return voxels;
    }

    private sealed class Recorder : IOriginShiftHandler
    {
        public List<OriginShift> Shifts { get; } = [];

        public void OnOriginShifted(in OriginShift shift) => Shifts.Add(shift);
    }

    /// <summary>
    /// A fixed system that moves the origin in its update, and somewhere else first in its before- and after-update, and
    /// keeps what each threw.
    /// </summary>
    private sealed class Shifting(World world, OriginShifts shifts, WorldPoint to)
        : BaseSystem<World, FixedStepTime>(world),
            IFixedUpdateSystem
    {
        public Exception? BeforeFailure { get; private set; }

        public Exception? Failure { get; private set; }

        public Exception? AfterFailure { get; private set; }

        public override void BeforeUpdate(in FixedStepTime step) => BeforeFailure = Try(to.Plus(10, 0, 0));

        public override void Update(in FixedStepTime step) => Failure = Try(to);

        public override void AfterUpdate(in FixedStepTime step) => AfterFailure = Try(to.Plus(20, 0, 0));

        private InvalidOperationException? Try(WorldPoint point)
        {
            try
            {
                shifts.ShiftTo(point);
                return null;
            }
            catch (InvalidOperationException exception)
            {
                return exception;
            }
        }
    }

    /// <summary>A frame system that moves the origin in its update and keeps what was thrown.</summary>
    private sealed class ShiftingFrame(World world, OriginShifts shifts, WorldPoint to)
        : BaseSystem<World, FrameTime>(world),
            IFrameUpdateSystem
    {
        public Exception? Failure { get; private set; }

        public override void Update(in FrameTime frame)
        {
            try
            {
                shifts.ShiftTo(to);
            }
            catch (InvalidOperationException exception)
            {
                Failure = exception;
            }
        }
    }
}
