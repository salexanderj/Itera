using System.Numerics;
using Arch.Core;
using Arch.System;
using Game.Components;
using Game.WorldGeneration;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Game.Systems;

/// <summary>
/// The fox (<see cref="GameModels.Fox"/>) (fixed group, after <see cref="PlayerSpawnSystem"/>, before the engine's
/// <c>MeshAnimatorSystem</c>, which moves its clip on). At the match's start it stands on the ground (or a boulder)
/// <see cref="AheadMetres"/> in front of the start pose, side on to it, facing its right. Then, over and over: it looks
/// about (its Survey clip) for <see cref="SurveySeconds"/>, walks for <see cref="WalkSeconds"/> and runs for
/// <see cref="RunSeconds"/> round a circle of <see cref="CircleMetres"/> beyond where it started, which takes it away
/// from the start and back. Each step it stands on the voxel ground under it (a ray down; the generator's hills where none
/// is loaded) and faces the way it goes. Its route is <see cref="FoxRoute"/>. The engine's <c>MeshDrawSystem</c> draws
/// it. A game's own animated models move the same way.
/// </summary>
public sealed partial class FoxSystem(
    World world,
    MeshAssets assets,
    StartPose start,
    ulong seed,
    ILogger<FoxSystem> logger,
    VoxelWorld? voxels = null
) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
{
    /// <summary>How far in front of the start pose's eye the fox starts, in metres.</summary>
    public const float AheadMetres = 4;

    /// <summary>The radius of the circle it walks and runs round, in metres.</summary>
    public const float CircleMetres = 3;

    public const float SurveySeconds = 6;
    public const float WalkSeconds = 8;
    public const float RunSeconds = 6;

    /// <summary>Its walking and running speeds, in metres a second.</summary>
    public const float WalkSpeed = 1.1f;
    public const float RunSpeed = 4f;

    /// <summary>The length of one round of looking about, walking, and running.</summary>
    public const float CycleSeconds = SurveySeconds + WalkSeconds + RunSeconds;

    private static readonly QueryDescription s_fox = new QueryDescription().WithAll<
        FoxRoute,
        MeshPlacement,
        MeshAnimator
    >();

    private int _survey = -1;
    private int _walk = -1;
    private int _run = -1;

    public override void Initialize()
    {
        EcsChecks.RequireAll<FoxRoute, MeshPlacement, MeshAnimator>(in s_fox);
        var asset = assets.Find(GameModels.Fox);
        if (asset < 0)
        {
            throw new InvalidOperationException($"The model '{GameModels.Fox}' was not declared (GameModels.Declare).");
        }

        if (assets.TryGet(asset, out var model) && model.Rig is { } rig)
        {
            (_survey, _walk, _run) = (rig.FindClip("Survey"), rig.FindClip("Walk"), rig.FindClip("Run"));
        }

        var forward = ThirdPersonCamera.Forward(start.Yaw, 0);
        var at = start.Eye + forward * AheadMetres;
        var ground = GameGenerator.SurfaceHeight(
            (int)MathF.Round(at.X * VoxelConstants.CentimetresPerMetre),
            (int)MathF.Round(at.Z * VoxelConstants.CentimetresPerMetre),
            seed
        );
        at.Y = (float)ground / VoxelConstants.CentimetresPerMetre;
        // The circle lies beyond it: going round towards the start's right takes it away from the start and back.
        var centre = at + forward * CircleMetres;
        var angle = MathF.Atan2(at.Z - centre.Z, at.X - centre.X);
        var right = new Vector3(-forward.Z, 0, forward.X);
        World.Create(
            new FoxRoute { Centre = centre, Angle = angle },
            MeshPlacement.At(at, Facing(right)),
            DrawnMesh.Of(asset),
            MeshAnimator.Playing(_survey)
        );
        LogPlaced(at.X, at.Y, at.Z);
    }

    public override void Update(in FixedStepTime step)
    {
        var job = new Roam
        {
            StepSeconds = step.StepSeconds,
            Voxels = voxels,
            Seed = seed,
            Survey = _survey,
            Walk = _walk,
            Run = _run,
        };
        World.InlineQuery<Roam, FoxRoute, MeshPlacement, MeshAnimator>(in s_fox, ref job);
    }

    /// <summary>The turn about +y that points a model's front (+z) along <paramref name="direction"/>.</summary>
    private static Quaternion Facing(Vector3 direction) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(direction.X, direction.Z));

    /// <summary>
    /// The top of the ground under (x, z), in metres: the voxels' (a ray down from 16 m over its last height, above any
    /// boulder on its way), or the generator's hills where none is loaded.
    /// </summary>
    private static float GroundAt(VoxelWorld? voxels, float x, float z, float near, ulong seed)
    {
        const float Above = 16;
        if (
            voxels is not null
            && voxels.Raycast(new Vector3(x, near + Above, z), -Vector3.UnitY, 2 * Above, out var hit, coarse: true)
            && hit.DistanceMetres > 0
        )
        {
            return near + Above - hit.DistanceMetres;
        }

        var height = GameGenerator.Height(
            (int)MathF.Round(x * VoxelConstants.CentimetresPerMetre),
            (int)MathF.Round(z * VoxelConstants.CentimetresPerMetre),
            seed
        );
        return (float)height / VoxelConstants.CentimetresPerMetre;
    }

    private struct Roam : IForEach<FoxRoute, MeshPlacement, MeshAnimator>
    {
        public float StepSeconds;
        public VoxelWorld? Voxels;
        public ulong Seed;
        public int Survey;
        public int Walk;
        public int Run;

        public readonly void Update(ref FoxRoute route, ref MeshPlacement placement, ref MeshAnimator animator)
        {
            placement.PreviousPosition = placement.Position;
            placement.PreviousOrientation = placement.Orientation;
            route.Seconds = (route.Seconds + StepSeconds) % CycleSeconds;
            var (clip, speed) =
                route.Seconds < SurveySeconds ? (Survey, 0f)
                : route.Seconds < SurveySeconds + WalkSeconds ? (Walk, WalkSpeed)
                : (Run, RunSpeed);
            if (animator.Clip != clip)
            {
                animator = MeshAnimator.Playing(clip);
            }

            if (speed == 0)
            {
                return;
            }

            // Round the circle clockwise seen from above, the way the start's right lies.
            route.Angle -= speed / CircleMetres * StepSeconds;
            var (sin, cos) = MathF.SinCos(route.Angle);
            var x = route.Centre.X + CircleMetres * cos;
            var z = route.Centre.Z + CircleMetres * sin;
            var y = GroundAt(Voxels, x, z, placement.Position.Y, Seed);
            placement.Position = new Vector3(x, y, z);
            placement.Orientation = Facing(new Vector3(sin, 0, -cos));
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fox: standing at ({X:F2}, {Y:F2}, {Z:F2}) m")]
    private partial void LogPlaced(float x, float y, float z);
}
