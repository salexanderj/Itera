using System.Numerics;
using Arch.Core;
using Game.Components;
using Game.Systems;
using Game.WorldGeneration;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Tests;

/// <summary>
/// The starter's fox: put at the start on the ground 4 m in front of the start pose, its head towards the start's right,
/// looking about; then walking and running round its circle, facing the way it goes; a game without the model declared
/// stops; a step allocates nothing.
/// </summary>
public sealed class FoxSystemTests
{
    private const ulong Seed = 7;
    private const float Step = 1f / 60;
    private static readonly QueryDescription s_fox = new QueryDescription().WithAll<
        FoxRoute,
        MeshPlacement,
        DrawnMesh,
        MeshAnimator
    >();
    private readonly MeshAssets _assets = new(NullLogger<MeshAssets>.Instance);

    public FoxSystemTests() => GameModels.Declare(_assets);

    [Fact]
    public void The_start_puts_the_fox_on_the_ground_in_front_side_on()
    {
        using var world = World.Create();
        // Looking along -x (yaw a quarter turn), from 20 m up.
        var start = new StartPose(new Vector3(3, 20, -4), MathF.PI / 2, -0.2f);
        using var system = Create(world, start);

        system.Initialize();

        var (route, placement, mesh, animator) = Fox(world);
        Assert.Equal(_assets.Find(GameModels.Fox), mesh.Asset);
        Assert.Equal(ModelShadow.Casts, mesh.Shadow);
        var ground = (float)GameGenerator.SurfaceHeight(-100, -400, Seed) / 100;
        Assert.Equal(-1, placement.Position.X, 1e-4f);
        Assert.Equal(ground, placement.Position.Y, 1e-4f);
        Assert.Equal(-4, placement.Position.Z, 1e-4f);
        Assert.Equal(placement.Position, placement.PreviousPosition);
        Assert.Equal(1, placement.Scale);
        // Its head along -z, the right of a camera looking along -x.
        var head = Vector3.Transform(Vector3.UnitZ, placement.Orientation);
        Assert.Equal(0, head.X, 1e-5f);
        Assert.Equal(-1, head.Z, 1e-5f);
        // Its circle lies 3 m further on; the declared model has no rig until it is read, so no clip.
        Assert.Equal(-4, route.Centre.X, 1e-5f);
        Assert.Equal(-4, route.Centre.Z, 1e-5f);
        Assert.Equal(-1, animator.Clip);
    }

    [Fact]
    public void After_looking_about_it_walks_then_runs_round_its_circle_facing_the_way_it_goes()
    {
        using var world = World.Create();
        using var system = Create(world, new StartPose(new Vector3(0, 8, 0), 0, 0));
        system.Initialize();
        var start = Fox(world).Placement.Position;

        Run(system, FoxSystem.SurveySeconds - 1);
        Assert.Equal(start, Fox(world).Placement.Position);

        // A second into the walk, then a second into the run.
        Run(system, 2);
        var walked = Fox(world);
        Run(system, FoxSystem.WalkSeconds);
        var ran = Fox(world);

        foreach (var (route, placement, _, _) in new[] { walked, ran })
        {
            var flat = new Vector2(placement.Position.X - route.Centre.X, placement.Position.Z - route.Centre.Z);
            Assert.Equal(FoxSystem.CircleMetres, flat.Length(), 1e-3f);
            // Its head along the circle, the way it moved since the step before.
            var moved = placement.Position - placement.PreviousPosition;
            var head = Vector3.Transform(Vector3.UnitZ, placement.Orientation);
            Assert.True(Vector3.Dot(Vector3.Normalize(moved with { Y = 0 }), head) > 0.99f);
        }

        // Towards the start's right (+x), a metre round the circle at a walk.
        Assert.True(walked.Placement.Position.X > start.X + 0.9f);
        Assert.Equal(FoxSystem.WalkSpeed * Step, FlatStep(walked.Placement), 1e-4f);
        Assert.Equal(FoxSystem.RunSpeed * Step, FlatStep(ran.Placement), 1e-4f);
    }

    [Fact]
    public void It_plays_its_survey_walk_and_run_clips_as_it_looks_about_walks_and_runs()
    {
        // A fox built in code with the three clips, in place of the file's.
        var assets = new MeshAssets(NullLogger<MeshAssets>.Instance);
        assets.Add(GameModels.Fox, RiggedFox());
        using var world = World.Create();
        using var system = new FoxSystem(
            world,
            assets,
            new StartPose(new Vector3(0, 8, 0), 0, 0),
            Seed,
            NullLogger<FoxSystem>.Instance
        );
        system.Initialize();
        // Each phase sampled 3 s into it, away from where float sums of the steps land either side of its start.
        Run(system, 3);
        var clips = new List<int> { Fox(world).Animator.Clip };

        foreach (var seconds in new[] { FoxSystem.SurveySeconds, FoxSystem.WalkSeconds, FoxSystem.RunSeconds })
        {
            Run(system, seconds);
            clips.Add(Fox(world).Animator.Clip);
        }

        // Survey, Walk, Run, then Survey again from its start.
        Assert.Equal([0, 1, 2, 0], clips);
        Assert.Equal(0, Fox(world).Animator.Seconds);
    }

    [Fact]
    public void Without_the_model_declared_the_start_stops()
    {
        using var world = World.Create();
        using var system = new FoxSystem(
            world,
            new MeshAssets(NullLogger<MeshAssets>.Instance),
            new StartPose(Vector3.Zero, 0, 0),
            Seed,
            NullLogger<FoxSystem>.Instance
        );

        Assert.Throws<InvalidOperationException>(system.Initialize);
    }

    [Fact]
    public void A_step_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world, new StartPose(new Vector3(0, 8, 0), 0, 0));
        system.Initialize();

        // Through looking about, walking, and running.
        Assert.Equal(
            0,
            SystemAllocation.BytesAllocated(
                system,
                tick => new FixedStepTime(Step, (ulong)tick),
                warmup: 600,
                measured: 1200
            )
        );
        Assert.Equal(1, world.CountEntities(in s_fox));
    }

    private FoxSystem Create(World world, StartPose start) =>
        new(world, _assets, start, Seed, NullLogger<FoxSystem>.Instance);

    /// <summary>How far the fox moved over the ground in its last step.</summary>
    private static float FlatStep(MeshPlacement placement)
    {
        var moved = placement.Position - placement.PreviousPosition;
        return new Vector2(moved.X, moved.Z).Length();
    }

    /// <summary>A one-joint asset with clips named as the fox's: Survey, Walk, Run.</summary>
    private static MeshAsset RiggedFox()
    {
        var vertices = new MeshAssetVertex[3];
        for (var i = 0; i < 3; i++)
        {
            vertices[i] = MeshAssetVertex.Pack(
                new Vector3(i, 0, 0),
                Vector3.UnitY,
                new Vector4(1, 0, 0, 1),
                Vector2.Zero,
                Vector2.Zero,
                Vector4.One
            );
        }

        var rig = new Rig(
            ["body"],
            [-1],
            [Matrix4x4.Identity],
            [JointTransform.Identity],
            [Matrix4x4.Identity],
            [2],
            [Clip("Survey"), Clip("Walk"), Clip("Run")]
        );
        var skin = new MeshSkinVertex[3];
        Array.Fill(skin, MeshSkinVertex.Pack(0, 0, 0, 0, Vector4.UnitX));
        return new MeshAsset(
            GameModels.Fox,
            vertices,
            [0, 1, 2],
            [new Itera.Engine.Assets.MeshPrimitive(0, 3, 0)],
            [MeshMaterialData.Default],
            [],
            rig,
            skin
        );
    }

    private static RigClip Clip(string name) =>
        new(
            name,
            [new RigTrack(0, RigPath.Translation, RigInterpolation.Linear, [0, 1], [Vector4.Zero, Vector4.UnitY])]
        );

    private static void Run(FoxSystem system, float seconds)
    {
        for (var i = 0; i < (int)MathF.Round(seconds / Step); i++)
        {
            system.Update(new FixedStepTime(Step, (ulong)i));
        }
    }

    private static (FoxRoute Route, MeshPlacement Placement, DrawnMesh Mesh, MeshAnimator Animator) Fox(World world)
    {
        var found = default((FoxRoute, MeshPlacement, DrawnMesh, MeshAnimator));
        var count = 0;
        world.Query(
            in s_fox,
            (ref FoxRoute route, ref MeshPlacement placement, ref DrawnMesh mesh, ref MeshAnimator animator) =>
            {
                found = (route, placement, mesh, animator);
                count++;
            }
        );
        Assert.Equal(1, count);
        return found;
    }
}
