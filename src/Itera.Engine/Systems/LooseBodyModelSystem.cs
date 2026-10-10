using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Voxels;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Hands every loose body's models to the renderer (<see cref="ModelInstances"/>) (frame group, after
/// <c>CameraExtractSystem</c>, whose camera picks each cut piece's level, and before any system of the game that adds
/// more models: loose bodies come first, so when more models stand about than a frame holds
/// (<see cref="ModelInstances.MaxModels"/>) they are never the ones left out). Each body's piece is drawn where it is
/// between the last two physics steps, its pose blended by <see cref="FrameTime.Alpha"/> as
/// <see cref="DebugOutlineSystem"/> blends it, at level 0 (its only level) and tinted as the ground where it is
/// (<see cref="VoxelTintSampler"/>), so it matches the ground it settles into. A <see cref="CutPiece"/> is drawn model
/// by model (<see cref="LooseBodies.PartsOf"/>) at the level its distance from the camera picks (<see cref="LevelZeroMetres"/>
/// and <see cref="LevelOneMetres"/>, past which <see cref="CoarsestLevel"/>), tinted as its source
/// (<see cref="LooseBodies.TintOf"/>). Clients' mirrors of the host's bodies (<see cref="RemoteBody"/>) are drawn too.
/// Reads <see cref="LooseBody"/> and <see cref="BodyPose"/>; writes no components.
/// </summary>
public sealed class LooseBodyModelSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    /// <summary>How far from the camera a cut piece is drawn at level 0, in metres, by default.</summary>
    public const float LevelZeroMetres = 13.44f;

    /// <summary>How far from the camera a cut piece is drawn at level 1, in metres, by default; past it, the coarsest level.</summary>
    public const float LevelOneMetres = 26.88f;

    /// <summary>The coarsest level a cut piece is drawn at.</summary>
    public const int CoarsestLevel = 2;

    private static readonly QueryDescription s_bodies = new QueryDescription().WithAll<LooseBody, BodyPose>();

    private readonly ModelInstances _instances;
    private readonly LooseBodies _bodies;
    private readonly VoxelTintSampler _tint;
    private readonly RenderView _view;
    private readonly float _levelZeroReach;
    private readonly float _levelOneReach;

    /// <param name="world">The ECS world.</param>
    /// <param name="instances">The frame's models.</param>
    /// <param name="bodies">The loose bodies, whose models are drawn.</param>
    /// <param name="tint">The ground's tint, which a piece takes where it is.</param>
    /// <param name="view">The frame's view, for the camera.</param>
    /// <param name="levelZeroMetres">How far from the camera a cut piece is drawn at level 0, in metres.</param>
    /// <param name="levelOneMetres">How far from the camera a cut piece is drawn at level 1, in metres.</param>
    public LooseBodyModelSystem(
        World world,
        ModelInstances instances,
        LooseBodies bodies,
        VoxelTintSampler tint,
        RenderView view,
        float levelZeroMetres = LevelZeroMetres,
        float levelOneMetres = LevelOneMetres
    )
        : base(world)
    {
        _instances = instances;
        _bodies = bodies;
        _tint = tint;
        _view = view;
        _levelZeroReach = levelZeroMetres;
        _levelOneReach = levelOneMetres;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<LooseBody, BodyPose>(in s_bodies);
    }

    public override void Update(in FrameTime time)
    {
        var job = new AddBodies
        {
            Instances = _instances,
            Bodies = _bodies,
            Tint = _tint,
            Camera = _view.Camera.Position,
            LevelZeroReach = _levelZeroReach,
            LevelOneReach = _levelOneReach,
            Alpha = time.Alpha,
        };
        World.InlineQuery<AddBodies, LooseBody, BodyPose>(in s_bodies, ref job);
    }

    private struct AddBodies : IForEach<LooseBody, BodyPose>
    {
        public ModelInstances Instances;
        public LooseBodies Bodies;
        public VoxelTintSampler Tint;
        public Vector3 Camera;
        public float LevelZeroReach;
        public float LevelOneReach;
        public float Alpha;

        public readonly void Update(ref LooseBody body, ref BodyPose pose)
        {
            var position = Vector3.Lerp(pose.PreviousPosition, pose.Position, Alpha);
            var orientation = Quaternion.Slerp(pose.PreviousOrientation, pose.Orientation, Alpha);
            // A piece is drawn at level 0 (its only level), tinted as the ground; a cut piece at the level its distance
            // picks, tinted as its source.
            var cut = Bodies.IsCut(body.Body);
            var distance = Vector3.Distance(Camera, position);
            var level =
                !cut || distance <= LevelZeroReach ? 0
                : distance <= LevelOneReach ? 1
                : CoarsestLevel;
            var tint = cut ? Bodies.TintOf(body.Body) : Tint.At(Bodies.Origin.ToWorld(position));

            // Each model's box corner is its voxel (0, 0, 0), placed from the pivot in the body's axes.
            foreach (ref readonly var model in Bodies.PartsOf(body.Body))
            {
                var corner = position + Vector3.Transform(model.Corner, orientation);
                Instances.Add(model.Model, level, corner, orientation * model.Turn, tint);
            }
        }
    }
}
