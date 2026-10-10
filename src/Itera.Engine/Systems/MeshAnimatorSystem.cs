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
/// keeps its time and only moves it on. During a fade the clip faded from moves on the same way, and the fade moves on
/// by the step; it ends a step after it is whole, so the frames between the last two steps still blend to it. Reads
/// <see cref="DrawnMesh"/>; writes <see cref="MeshAnimator"/>.
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
            var duration = DurationOf(Assets, mesh.Asset, animator.Clip);
            Move(
                ref animator.Seconds,
                ref animator.PreviousSeconds,
                animator.Speed * StepSeconds,
                duration,
                animator.Once
            );
            if (animator.FadeSeconds > 0)
            {
                if (animator.PreviousFaded >= animator.FadeSeconds)
                {
                    animator.FadeSeconds = 0;
                    return;
                }

                animator.PreviousFaded = animator.Faded;
                animator.Faded = Math.Min(animator.Faded + StepSeconds, animator.FadeSeconds);
                Move(
                    ref animator.FromSeconds,
                    ref animator.FromPreviousSeconds,
                    animator.FromSpeed * StepSeconds,
                    DurationOf(Assets, mesh.Asset, animator.FromClip),
                    animator.FromOnce
                );
            }
        }

        /// <summary>One clip's clock moved on by <paramref name="by"/> seconds, held or wrapped at its end.</summary>
        private static void Move(ref float seconds, ref float previous, float by, float duration, bool once)
        {
            previous = seconds;
            seconds += by;
            if (!(duration > 0))
            {
                return;
            }

            if (once)
            {
                seconds = Math.Clamp(seconds, 0, duration);
                return;
            }

            if (seconds >= duration || seconds < 0)
            {
                var wraps = MathF.Floor(seconds / duration) * duration;
                seconds -= wraps;
                previous -= wraps;
            }
        }
    }
}
