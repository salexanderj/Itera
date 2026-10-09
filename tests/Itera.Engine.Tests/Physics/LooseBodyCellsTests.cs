using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A loose body of a piece lifted from a world of 10 cm voxels, as the mover reads it (<see cref="LooseBodyVoxels"/>):
/// the piece holds centimetres, so each 10 cm world voxel round its pivot whose centre lands in the piece is blocked
/// (until 2026-10-07 a piece's centimetre was read as a world voxel, a body ten times its size).
/// </summary>
public sealed class LooseBodyCellsTests : IDisposable
{
    private readonly VoxelWorld _world;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;
    private readonly LooseBodies _bodies;
    private readonly LooseBodyVoxels _bodyVoxels;

    public LooseBodyCellsTests()
    {
        _world = TestWorlds.Load(new DensityTerrainGenerator(new VoxelGrid(10)), new Vector3(1, 2, 1), radiusMetres: 4);
        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        var passability = new NothingPassable();
        _colliders = new VoxelColliders(_physics, _world, passability, NullLogger<VoxelColliders>.Instance);
        _colliders.Initialize();
        _bodies = new LooseBodies(
            _physics,
            _world,
            _colliders,
            new VoxelModels(_world),
            passability,
            Options.Create(new EngineOptions()),
            NullLogger<LooseBodies>.Instance
        );
        _bodyVoxels = new LooseBodyVoxels(_bodies, _world, passability);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_body_fills_the_world_voxels_round_its_pivot()
    {
        // A block of the floor's dirt 50 by 40 by 50 cm: 5 by 4 by 5 voxels.
        Assert.True(
            _world.TryLift(
                VoxelEdit.Box(new VoxelCoord(0, 100, 0), new VoxelCoord(50, 40, 50), VoxelEditOperation.Lift),
                out var lifted
            )
        );
        Assert.True(_bodies.TryCreate(lifted, Vector3.Zero, Vector3.Zero, out var body));
        // Its pivot (the middle of its 50 x 40 x 50 cm) on a voxel's corner 3 m up: it fills the voxels from 2.75 to 2.25
        // m on x and z, and 2.8 to 3.2 m up.
        var reference = _physics.Simulation.Bodies[body];
        reference.Pose.Position += new Vector3(2, 3, 2) - _bodies.PivotOf(body);
        _bodyVoxels.Refresh();

        var over = new CircleFootprint(20, 20, 1.5);
        Assert.Equal(32, _bodyVoxels.HighestTop(in over, 0, 60));
        Assert.Equal(28, _bodyVoxels.LowestBottom(in over, 0, 60));
        var beside = new CircleFootprint(23.6, 20, 0.5);
        Assert.Equal(int.MinValue, _bodyVoxels.HighestTop(in beside, 0, 60));
        var far = new CircleFootprint(200, 200, 5);
        Assert.Equal(int.MinValue, _bodyVoxels.HighestTop(in far, 0, 400));
    }
}
