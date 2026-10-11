using System.Numerics;
using BepuPhysics;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// A match without voxel terrain for system tests of loose bodies: a physics world with mesh ground at y = 0 from -10 to
/// 10 m on x and z (<see cref="MeshColliders"/>), voxel models in their own memory, loose bodies that fall out 20 m under
/// it, the bodies as the character mover's 10 cm cells, and a mover over the colliders that reads them.
/// </summary>
internal sealed class MeshGroundHarness : IDisposable
{
    public const float Step = 1f / 60;

    /// <summary>The radius of the harness's ball, in centimetres.</summary>
    public const int BallRadius = 12;

    /// <summary>The material of the harness's balls.</summary>
    public const byte Stone = 3;

    /// <summary>Below this height, in metres, a body has fallen out.</summary>
    public const float FallLimitMetres = -20;

    public MeshGroundHarness(
        double settleSeconds = 3,
        int maxBodies = 64,
        PhysicsMaterials? materials = null,
        PhysicsLayers? layers = null
    )
    {
        Options = Microsoft.Extensions.Options.Options.Create(
            new EngineOptions
            {
                Physics = new PhysicsSettings { SettleSeconds = settleSeconds, MaxLooseBodies = maxBodies },
            }
        );
        Physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, null, materials, layers);
        Physics.Initialize();
        Colliders = new MeshColliders(Physics, NullLogger<MeshColliders>.Instance);
        Vector3[] ground = [new(-10, 0, -10), new(-10, 0, 10), new(10, 0, 10), new(10, 0, -10)];
        Colliders.Add(ground, [0, 1, 2, 0, 2, 3], Matrix4x4.Identity);
        Models = new VoxelModels();
        var passability = new NothingPassable();
        Bodies = new LooseBodies(
            Physics,
            Models,
            passability,
            Options,
            NullLogger<LooseBodies>.Instance,
            () => FallLimitMetres
        );
        BodyVoxels = new LooseBodyVoxels(Bodies, passability, cellCentimetres: 10);
        Mover = new VoxelCharacterMover(passability, Colliders, cellCentimetres: 10, bodies: BodyVoxels);
    }

    public IOptions<EngineOptions> Options { get; }

    public PhysicsWorld Physics { get; }

    public MeshColliders Colliders { get; }

    public VoxelModels Models { get; }

    public LooseBodies Bodies { get; }

    public LooseBodyVoxels BodyVoxels { get; }

    public VoxelCharacterMover Mover { get; }

    /// <summary>Runs the physics step and reads the bodies for the mover, as a match without terrain does.</summary>
    public void RunPhysics(int steps = 1)
    {
        for (var i = 0; i < steps; i++)
        {
            Physics.Step(Step);
            BodyVoxels.Refresh();
        }
    }

    /// <summary>Makes a ball of radius <see cref="BallRadius"/> from nothing with its pivot at <paramref name="pivot"/>.</summary>
    public BodyHandle ThrowBall(Vector3 pivot, Vector3 velocity = default)
    {
        Assert.True(Bodies.TryCreateBall(BallRadius, Stone, pivot, velocity, out var body));
        return body;
    }

    /// <summary>A cut piece (a one-body ragdoll of a 10 by 40 by 10 cm block) with its box's corner at <paramref name="corner"/>.</summary>
    public BodyHandle CutBody(Vector3 corner)
    {
        var size = new VoxelCoord(10, 40, 10);
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = Stone;
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
                0,
                made
            )
        );
        return made[0];
    }

    public void Dispose()
    {
        Colliders.Dispose();
        Physics.Dispose();
        Models.Dispose();
    }
}
