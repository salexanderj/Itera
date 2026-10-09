using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Bodies on the statics <see cref="VoxelColliders"/> builds from 10 cm voxels with densities: a column's top voxel
/// ends where its density puts the ground's surface, so a ball rests on the surface the mesher draws, not on a voxel's
/// top up to 5 cm away.
/// </summary>
public sealed class SurfaceCollidersTests : IDisposable
{
    private const float Step = 1f / 60;
    private readonly VoxelWorld _world;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;

    public SurfaceCollidersTests()
    {
        _world = TestWorlds.Load(new DensityTerrainGenerator(new VoxelGrid(10)), new Vector3(1, 2, 1), radiusMetres: 4);
        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        _colliders = new VoxelColliders(_physics, _world, new NothingPassable(), NullLogger<VoxelColliders>.Instance);
        _colliders.Initialize();
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_ball_rests_on_the_floor_inside_its_voxel()
    {
        var shape = _physics.Simulation.Shapes.Add(new Sphere(0.1f));
        var ball = _physics.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(new Vector3(0.95f, 2.5f, 1.05f)),
                new Sphere(0.1f).ComputeInertia(1),
                new CollidableDescription(shape, 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );

        for (var i = 0; i < 120; i++)
        {
            _colliders.Update(Step);
            _physics.Step(Step);
        }

        // The floor at 153 cm, inside voxel 15, where its density crosses one half (152.2 cm); its top voxel is the
        // floor's voxel 14, whose top is at 150 cm.
        var position = _physics.Simulation.Bodies[ball].Pose.Position;
        Assert.InRange(position.Y - 0.1f, 1.515f, 1.535f);
    }
}
