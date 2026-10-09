using System.Numerics;
using BepuPhysics;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// Stone below y = 0 (<see cref="FlatTestGenerator"/>, of 1 cm voxels or another grid's) loaded within 4 m of the origin
/// (or another distance), with a physics world, its voxel colliders, loose bodies, the bodies as voxels, and a character
/// mover that reads them, for system tests of loose bodies.
/// </summary>
internal sealed class PhysicsTestHarness : IDisposable
{
    public const float Step = 1f / 60;

    public PhysicsTestHarness(
        double settleSeconds = 3,
        int maxBodies = 64,
        double loadedMetres = 4,
        int levels = 1,
        VoxelGrid? grid = null
    )
    {
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings
            {
                StreamingRadiusMetres = loadedMetres,
                Levels = levels,
                WorkerThreads = 2,
            },
            Physics = new PhysicsSettings { SettleSeconds = settleSeconds, MaxLooseBodies = maxBodies },
        };
        Options = Microsoft.Extensions.Options.Options.Create(options);
        Voxels = new VoxelWorld(Options, new FlatTestGenerator(grid), NullLogger<VoxelWorld>.Instance);
        Voxels.Initialize();
        Voxels.SetCenters([Vector3.Zero]);
        VoxelLoading.WaitUntilLoaded(Voxels);

        Physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        Physics.Initialize();
        var passability = new NothingPassable();
        Colliders = new VoxelColliders(Physics, Voxels, passability, NullLogger<VoxelColliders>.Instance);
        Colliders.Initialize();
        Models = new VoxelModels(Voxels);
        Bodies = new LooseBodies(
            Physics,
            Voxels,
            Colliders,
            Models,
            passability,
            Options,
            NullLogger<LooseBodies>.Instance
        );
        BodyVoxels = new LooseBodyVoxels(Bodies, Voxels, passability);
        Mover = new VoxelCharacterMover(Voxels, passability, BodyVoxels);
    }

    public IOptions<EngineOptions> Options { get; }

    public VoxelWorld Voxels { get; }

    public PhysicsWorld Physics { get; }

    public VoxelColliders Colliders { get; }

    /// <summary>The models that draw the loose bodies.</summary>
    public VoxelModels Models { get; }

    public LooseBodies Bodies { get; }

    public LooseBodyVoxels BodyVoxels { get; }

    /// <summary>A character mover that reads the loose bodies too.</summary>
    public VoxelCharacterMover Mover { get; }

    /// <summary>Runs the voxel colliders and the physics step, as the loop does after the fixed group.</summary>
    public void RunPhysics(int steps = 1)
    {
        for (var i = 0; i < steps; i++)
        {
            Colliders.Update(Step);
            Physics.Step(Step);
            BodyVoxels.Refresh();
        }
    }

    /// <summary>The radius of the harness's ball, in centimetres.</summary>
    public const int BallRadius = 12;

    /// <summary>
    /// Makes a ball of stone of radius <see cref="BallRadius"/> from nothing, as a throw does, with its pivot at
    /// <paramref name="pivot"/>.
    /// </summary>
    public BodyHandle ThrowBall(Vector3 pivot, Vector3 velocity = default)
    {
        Assert.True(Bodies.TryCreateBall(BallRadius, FlatTestGenerator.Stone, pivot, velocity, out var body));
        return body;
    }

    /// <summary>
    /// A cut piece (a one-body ragdoll of a 10 by 40 by 10 cm block of stone) with its box's corner at
    /// <paramref name="corner"/>, drawn <paramref name="tint"/> of the way towards its tint colour.
    /// </summary>
    public BodyHandle CutBody(Vector3 corner, float tint = 0)
    {
        var size = new VoxelCoord(10, 40, 10);
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = FlatTestGenerator.Stone;
                }
            }
        }

        var tree = Models.Encode(voxels);
        var made = new List<BodyHandle>();
        Assert.True(
            Bodies.TryCreateRagdoll(
                [new LoosePart(-1, tree, size, corner, Quaternion.Identity, false)],
                [0],
                [],
                1000,
                Vector3.Zero,
                tint,
                made
            )
        );
        Assert.True(Bodies.IsCut(made[0]));
        return made[0];
    }

    public void Dispose()
    {
        Colliders.Dispose();
        Physics.Dispose();
        Voxels.Dispose();
    }
}
