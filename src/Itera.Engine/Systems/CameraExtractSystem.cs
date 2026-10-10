using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Hands the <see cref="ActiveCamera"/> to the renderer (<see cref="RenderView"/>) (frame group, registered after every
/// system that moves the camera, so it sees the frame's final pose). Reads <see cref="CameraPose"/> and
/// <see cref="CameraLens"/>; writes no components. With the player's settings a perspective lens draws with their field of
/// view instead of its own; an orthographic lens keeps its size.
/// </summary>
public sealed class CameraExtractSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    private static readonly QueryDescription s_query = new QueryDescription().WithAll<
        CameraPose,
        CameraLens,
        ActiveCamera
    >();

    private readonly RenderView _view;
    private readonly PlayerSettings? _settings;

    /// <param name="world">The ECS world.</param>
    /// <param name="view">Where the frame's camera goes.</param>
    /// <param name="settings">The player's settings, whose field of view is drawn with; the lens's without them.</param>
    public CameraExtractSystem(World world, RenderView view, PlayerSettings? settings = null)
        : base(world)
    {
        _view = view;
        _settings = settings;
    }

    public override void Initialize() => EcsChecks.RequireAll<CameraPose, CameraLens>(in s_query);

    public override void Update(in FrameTime frame)
    {
        var job = new Extract { FieldOfView = _settings?.VerticalFieldOfViewRadians ?? 0 };
        World.InlineQuery<Extract, CameraPose, CameraLens>(in s_query, ref job);
        if (!job.Found)
        {
            return;
        }

        _view.SetCamera(in job.View);
    }

    /// <summary>The view a pose and lens describe.</summary>
    public static CameraView ToView(in CameraPose pose, in CameraLens lens) =>
        new(
            pose.Position,
            Quaternion.CreateFromYawPitchRoll(pose.Yaw, pose.Pitch, 0),
            lens.VerticalFovRadians,
            lens.NearMetres,
            lens.Projection,
            lens.HeightMetres,
            lens.FarMetres
        );

    private struct Extract : IForEach<CameraPose, CameraLens>
    {
        public CameraView View;
        public bool Found;

        /// <summary>The player's vertical field of view in radians, or 0 for the lens's.</summary>
        public float FieldOfView;

        public void Update(ref CameraPose pose, ref CameraLens lens)
        {
            // There is one active camera; if a bug made several, the last one visited wins.
            View = ToView(in pose, in lens);
            if (FieldOfView > 0 && !View.Orthographic)
            {
                View = View with { VerticalFovRadians = FieldOfView };
            }

            Found = true;
        }
    }
}
