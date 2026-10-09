using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="LooseBodyModelSystem"/>: each loose body is drawn by its models at its pose between the last two physics
/// steps, a ball tinted as the ground where it is and at level 0, a cut piece tinted as its source and at the level its
/// distance from the camera picks; and adding them allocates nothing.
/// </summary>
public sealed class LooseBodyModelSystemTests : IDisposable
{
    private readonly PhysicsTestHarness _physics = new();
    private readonly ModelInstances _instances = new();
    private readonly RenderView _view = new(Options.Create(new EngineOptions()));

    public void Dispose() => _physics.Dispose();

    [Fact]
    public void A_ball_is_drawn_at_its_blended_pose_with_the_grounds_tint()
    {
        using var world = World.Create();
        using var system = Create(world, new SlopedTint());
        var body = _physics.ThrowBall(new Vector3(1, 0.2f, 0));
        _physics.Bodies.BoxOf(body, out var min, out var max);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        world.Create(
            new LooseBody { Body = body },
            new BodyPose
            {
                PreviousPosition = new Vector3(2, 0, 0),
                Position = new Vector3(6, 0, 0),
                PreviousOrientation = turn,
                Orientation = turn,
            },
            new BodyBox { Min = min, Max = max }
        );

        system.Update(new FrameTime(1f / 240, 1, 1, 0.25f));

        var draw = Assert.Single(_instances.Draws.ToArray());
        Assert.Equal(_physics.Bodies.ModelOf(body), draw.Model);
        Assert.Equal(0, draw.Level);
        // A quarter of the way from x = 2 to x = 6, with the box's corner turned about the pivot.
        var expected = new Vector3(3, 0, 0) + Vector3.Transform(min, turn);
        Assert.Equal(expected.X, draw.Corner.X, 1e-5f);
        Assert.Equal(expected.Y, draw.Corner.Y, 1e-5f);
        Assert.Equal(expected.Z, draw.Corner.Z, 1e-5f);
        Assert.Equal(turn, draw.Turn);
        Assert.Equal(new VoxelTintSampler(new SlopedTint()).At(new Vector3(3, 0, 0)), draw.Tint);
        Assert.True(draw.Tint > 0);
        Assert.Equal(ModelShadow.Casts, draw.Shadow);
    }

    [Fact]
    public void A_ball_far_from_the_camera_stays_at_level_0()
    {
        using var world = World.Create();
        using var system = Create(world, new NoVoxelTintMap());
        var body = _physics.ThrowBall(new Vector3(1, 0.2f, 0));
        Add(world, body, new Vector3(0, 0, -100));

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        Assert.Equal(0, Assert.Single(_instances.Draws.ToArray()).Level);
    }

    [Fact]
    public void A_cut_piece_is_drawn_at_the_level_its_distance_picks_and_tinted_as_its_source()
    {
        using var world = World.Create();
        using var system = Create(world, new SlopedTint());
        var near = _physics.CutBody(new Vector3(0, 0, 0), tint: 0.4f);
        var middle = _physics.CutBody(new Vector3(0, 0, 0), tint: 0.4f);
        var far = _physics.CutBody(new Vector3(0, 0, 0), tint: 0.4f);
        // The camera is at the origin: 5 m, 20 m, and 40 m away.
        Add(world, near, new Vector3(0, 0, -5));
        Add(world, middle, new Vector3(0, 0, -20));
        Add(world, far, new Vector3(0, 0, -40));

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        var draws = _instances.Draws.ToArray();
        Assert.Equal(3, draws.Length);
        Assert.Equal(
            [
                (_physics.Bodies.ModelOf(near), 0),
                (_physics.Bodies.ModelOf(middle), 1),
                (_physics.Bodies.ModelOf(far), 2),
            ],
            draws.Select(draw => (draw.Model, draw.Level)).OrderBy(pair => pair.Level).ToArray()
        );
        Assert.All(draws, draw => Assert.Equal(0.4f, draw.Tint));
    }

    [Fact]
    public void A_clients_mirror_is_drawn_too()
    {
        using var world = World.Create();
        using var system = Create(world, new NoVoxelTintMap());
        Assert.True(
            _physics.Bodies.TryCreateBallMirror(
                PhysicsTestHarness.BallRadius,
                FlatTestGenerator.Stone,
                new Vector3(1, 0.2f, 0),
                Quaternion.Identity,
                out var mirror
            )
        );
        world.Create(
            new LooseBody { Body = mirror },
            new BodyPose { Orientation = Quaternion.Identity, PreviousOrientation = Quaternion.Identity },
            new BodyBox(),
            new RemoteBody()
        );

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        Assert.Single(_instances.Draws.ToArray());
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world, new NoVoxelTintMap());
        Add(world, _physics.ThrowBall(new Vector3(1, 0.2f, 0)), Vector3.Zero);
        Add(world, _physics.CutBody(new Vector3(0, 0, 0)), new Vector3(0, 0, -20));

        // The list is cleared between frames by the loop; here every update adds two bodies' models, far below the limit.
        Assert.Equal(
            0,
            SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 1, 0.5f), warmup: 10, measured: 100)
        );
        Assert.True(_instances.Count > 0);
    }

    /// <summary>An entity for <paramref name="body"/>, at rest at <paramref name="at"/>.</summary>
    private static void Add(World world, BepuPhysics.BodyHandle body, Vector3 at) =>
        world.Create(
            new LooseBody { Body = body },
            new BodyPose
            {
                PreviousPosition = at,
                Position = at,
                PreviousOrientation = Quaternion.Identity,
                Orientation = Quaternion.Identity,
            },
            new BodyBox()
        );

    private LooseBodyModelSystem Create(World world, IVoxelTintMap map)
    {
        _view.SetCamera(new CameraView(Vector3.Zero, Quaternion.Identity, 1, 0.01f));
        var system = new LooseBodyModelSystem(world, _instances, _physics.Bodies, new VoxelTintSampler(map), _view);
        system.Initialize();
        return system;
    }

    /// <summary>A tint map that rises along x, from 0 at the map's west edge.</summary>
    private sealed class SlopedTint : IVoxelTintMap
    {
        public int TexelShift => 9;

        public int OriginX => -IVoxelTintMap.Size / 2 << 9;

        public int OriginZ => -IVoxelTintMap.Size / 2 << 9;

        public void Write(Span<byte> texels)
        {
            for (var i = 0; i < texels.Length; i++)
            {
                texels[i] = (byte)(i % IVoxelTintMap.Size);
            }
        }
    }
}
