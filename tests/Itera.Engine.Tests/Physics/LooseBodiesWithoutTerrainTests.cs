using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Loose bodies in a match without voxel terrain (<see cref="MeshGroundHarness"/>): a ball and a cut body land on mesh
/// ground and rest, never frozen; one over no ground falls out under the fall limit; nothing settles into a grid and no
/// lift makes a body; a released body leaves its model to the game; the models keep their trees in their own memory,
/// freed with them (and bodies with terrain refuse such models); and the character mover meets a ball and stands on it.
/// </summary>
public sealed class LooseBodiesWithoutTerrainTests : IDisposable
{
    private readonly MeshGroundHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void A_ball_lands_on_mesh_ground_and_rests()
    {
        var ball = _harness.ThrowBall(new Vector3(0, 1, 0));

        _harness.RunPhysics(240);

        Assert.InRange(_harness.Bodies.PivotOf(ball).Y, 0.1f, 0.15f);
        Assert.True(_harness.Bodies.IsAtRest(ball));
        Assert.False(_harness.Bodies.HasFallenOut(ball));
        Assert.False(_harness.Bodies.CanSettle);
    }

    [Fact]
    public void A_cut_body_lands_on_mesh_ground_at_level_0()
    {
        var body = _harness.CutBody(new Vector3(0, 1, 0));

        _harness.RunPhysics(240);

        Assert.Equal(0, _harness.Bodies.CollisionLevel(body));
        Assert.InRange(_harness.Bodies.PivotOf(body).Y, -0.05f, 0.5f);
        Assert.True(_harness.Bodies.IsAtRestOrFrozen(body));
    }

    [Fact]
    public void A_ball_over_no_ground_falls_out_under_the_fall_limit()
    {
        var ball = _harness.ThrowBall(new Vector3(50, 0, 0));

        _harness.RunPhysics(60);
        var early = _harness.Bodies.HasFallenOut(ball);
        // 2.5 s: about 30 m down, under the harness's limit and over the default play area's (-61.2 m).
        _harness.RunPhysics(90);

        Assert.False(early);
        Assert.InRange(_harness.Bodies.PivotOf(ball).Y, -50, MeshGroundHarness.FallLimitMetres);
        Assert.True(_harness.Bodies.HasFallenOut(ball));
    }

    [Fact]
    public void Nothing_settles_into_a_grid_and_no_lift_makes_a_body()
    {
        var ball = _harness.ThrowBall(new Vector3(0, 1, 0));

        Assert.Throws<InvalidOperationException>(() => _harness.Bodies.Settle(ball));
        Assert.Equal(0, _harness.Bodies.SettleThrown());
        Assert.False(_harness.Bodies.TryCreate(1, Vector3.One, Vector3.Zero, out _));
        Assert.False(_harness.Bodies.TryCreateMirror(1, Vector3.One, Quaternion.Identity, out _));
        Assert.Throws<ArgumentException>(() => _harness.Bodies.PutBack(1));
        Assert.True(_harness.Bodies.Contains(ball));
    }

    [Fact]
    public void A_released_body_leaves_its_model_to_the_game()
    {
        var ball = _harness.ThrowBall(new Vector3(0, 1, 0));
        _harness.RunPhysics(240);
        var model = _harness.Bodies.ModelOf(ball);
        var pivot = _harness.Bodies.PivotOf(ball);

        var rested = _harness.Bodies.Rested(ball);
        _harness.Bodies.Release(ball);

        Assert.False(_harness.Bodies.Contains(ball));
        Assert.False(_harness.Physics.Simulation.Bodies.BodyExists(ball));
        Assert.Equal(1, _harness.Models.Count);
        Assert.Equal(
            (model, MeshGroundHarness.BallRadius, MeshGroundHarness.Stone),
            (rested.Model, rested.BallRadius, rested.BallMaterial)
        );
        Assert.Equal(pivot, rested.PivotMetres);
        // The ball's box reaches its radius on every side of its pivot's centimetre, turned as the ball lies.
        var corner = pivot + Vector3.Transform(new Vector3(-0.12f), rested.Orientation);
        Assert.True(
            Vector3.Distance(corner, rested.ModelCorner) < 1e-4f,
            $"Corner {rested.ModelCorner}, not {corner}."
        );
        _harness.Models.Remove(model);
        Assert.Equal(0, _harness.Models.Count);
    }

    [Fact]
    public void The_models_keep_their_trees_in_their_own_memory_and_free_it_with_them()
    {
        using var models = new VoxelModels();
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        voxels[5 + VoxelConstants.ChunkSize * (6 + VoxelConstants.ChunkSize * 7)] = MeshGroundHarness.Stone;

        var tree = models.Encode(voxels);
        var read = models.ReadVoxel(tree, 5, 6, 7);
        var used = models.Memory.UsedBytes;
        models.Dispose();

        Assert.Equal(MeshGroundHarness.Stone, read);
        Assert.True(used > 0);
        Assert.Throws<ObjectDisposedException>(() => models.Encode(voxels));
    }

    [Fact]
    public void The_character_mover_meets_a_ball_on_mesh_ground()
    {
        _harness.ThrowBall(new Vector3(1, 0.5f, 0));
        _harness.RunPhysics(240);
        var feet = Vector3.Zero;
        var velocity = Vector3.Zero;
        var hitWall = false;

        for (var i = 0; i < 30; i++)
        {
            velocity = new Vector3(4.5f, velocity.Y - 20 * MeshGroundHarness.Step, 0);
            var result = _harness.Mover.Move(
                ref feet,
                ref velocity,
                new CharacterShape(1.7f, 0.26f, 0.41f),
                MeshGroundHarness.Step
            );
            hitWall |= result.HitWall;
        }

        // The ball's cells start at x = 0.8 m.
        Assert.True(hitWall);
        Assert.InRange(feet.X, 0.4f, 0.65f);
    }

    [Fact]
    public void A_character_stands_on_a_ball_on_mesh_ground()
    {
        var ball = _harness.ThrowBall(new Vector3(1, 0.5f, 0));
        _harness.RunPhysics(240);
        var top = _harness.Bodies.PivotOf(ball).Y + 0.12f;
        var feet = new Vector3(1, 1, 0);
        var velocity = Vector3.Zero;
        var result = default(CharacterMoveResult);

        for (var i = 0; i < 60; i++)
        {
            velocity = new Vector3(0, velocity.Y - 20 * MeshGroundHarness.Step, 0);
            result = _harness.Mover.Move(
                ref feet,
                ref velocity,
                new CharacterShape(1.7f, 0.26f, 0.41f),
                MeshGroundHarness.Step
            );
        }

        // On the top of the cells the ball takes (each 10 cm cell whose centre lies in it: within a cell under its top),
        // not on the ground under it.
        Assert.True(result.Grounded);
        Assert.InRange(feet.Y, top - 0.1f, top + 0.001f);
    }

    [Fact]
    public void Reading_a_rested_body_allocates_nothing()
    {
        var ball = _harness.ThrowBall(new Vector3(0, 1, 0));
        _harness.RunPhysics(240);
        _harness.Bodies.Rested(ball);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            _harness.Bodies.Rested(ball);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Bodies_with_terrain_refuse_models_of_their_own_memory()
    {
        using var terrain = new PhysicsTestHarness();
        using var own = new VoxelModels();

        // A lifted piece's tree lies in the world's memory, which such models would not read.
        Assert.Throws<ArgumentException>(() =>
            new LooseBodies(
                terrain.Physics,
                terrain.Voxels,
                terrain.Colliders,
                own,
                new NothingPassable(),
                terrain.Options,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LooseBodies>.Instance
            )
        );
    }

    [Fact]
    public void Models_left_at_exit_keep_their_memory()
    {
        var models = new VoxelModels();
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        voxels[0] = MeshGroundHarness.Stone;

        models.LeaveMemoryAtExit();
        models.Dispose();

        // Not freed: a thread still encoding may write into it.
        Assert.Equal(MeshGroundHarness.Stone, models.ReadVoxel(models.Encode(voxels), 0, 0, 0));
    }
}
