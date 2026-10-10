using Arch.Core;
using Arch.System;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Advances each <see cref="MeshAnimator"/>'s clip time (fixed group, before the systems that read a pose, after those
/// that choose clips): the time moves to the previous, and on by <see cref="MeshAnimator.Speed"/> times the step, so
/// clips follow the match's time and stop while it is paused. A looping clip wraps both times by its length, keeping the
/// frame's blend between them; one played once stops at its end. A clip index past the rig's (or a static asset's)
/// keeps its time and only moves it on. Reads <see cref="DrawnMesh"/>; writes <see cref="MeshAnimator"/>.
/// </summary>
public sealed class MeshAnimatorSystem(World world, MeshAssets assets)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    private static readonly QueryDescription s_animated = new QueryDescription().WithAll<MeshAnimator, DrawnMesh>();

    public override void Initialize() => EcsChecks.RequireAll<MeshAnimator, DrawnMesh>(in s_animated);

    public override void Update(in FixedStepTime step)
    {
        var job = new Advance { Assets = assets, StepSeconds = step.StepSeconds };
        World.InlineQuery<Advance, MeshAnimator, DrawnMesh>(in s_animated, ref job);
    }

    /// <summary>The length of the clip an animator plays, or 0 when it names none.</summary>
    internal static float DurationOf(MeshAssets assets, int asset, int clip)
    {
        if (!assets.TryGet(asset, out var model) || model.Rig is not { } rig || (uint)clip >= (uint)rig.Clips.Length)
        {
            return 0;
        }

        return rig.Clips[clip].Duration;
    }

    private struct Advance : IForEach<MeshAnimator, DrawnMesh>
    {
        public MeshAssets Assets;
        public float StepSeconds;

        public readonly void Update(ref MeshAnimator animator, ref DrawnMesh mesh)
        {
            animator.PreviousSeconds = animator.Seconds;
            animator.Seconds += animator.Speed * StepSeconds;
            var duration = DurationOf(Assets, mesh.Asset, animator.Clip);
            if (!(duration > 0))
            {
                return;
            }

            if (animator.Once)
            {
                animator.Seconds = Math.Clamp(animator.Seconds, 0, duration);
                return;
            }

            if (animator.Seconds >= duration || animator.Seconds < 0)
            {
                var wraps = MathF.Floor(animator.Seconds / duration) * duration;
                animator.Seconds -= wraps;
                animator.PreviousSeconds -= wraps;
            }
        }
    }
}
