using Arch.Core;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Rendering.Meshes;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="MeshAnimatorSystem"/>: each step moves a clip's time on by its speed, keeping the time before; a looping clip
/// wraps both times at its end; one played once stops there; a speed of 0 holds it; a clip the asset does not have only
/// moves on; and a step allocates nothing.
/// </summary>
public sealed class MeshAnimatorSystemTests
{
    private const float Step = 0.25f;
    private static readonly QueryDescription s_animated = new QueryDescription().WithAll<MeshAnimator>();
    private readonly MeshAssets _assets = new(NullLogger<MeshAssets>.Instance);

    public MeshAnimatorSystemTests()
    {
        // Asset 0: a one-second "bend" clip; asset 1: a static asset.
        _assets.Add("bending", MeshAssetPassTests.SkinnedAsset("bending", bend: true));
        _assets.Add("still", MeshAssetPassTests.Asset("still", MeshMaterialData.Default, vertices: 3));
    }

    [Fact]
    public void A_step_moves_the_time_on_by_the_speed_and_keeps_the_time_before()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(DrawnMesh.Of(0), MeshAnimator.Playing(0, seconds: 0.2f, speed: 2));

        system.Update(new FixedStepTime(Step, 0));

        var animator = Single(world);
        Assert.Equal(0.2f, animator.PreviousSeconds);
        Assert.Equal(0.7f, animator.Seconds, 1e-6f);
    }

    [Fact]
    public void A_looping_clip_wraps_both_times_at_its_end()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(DrawnMesh.Of(0), MeshAnimator.Playing(0, seconds: 0.9f));

        system.Update(new FixedStepTime(Step, 0));

        // 1.15 s into a one-second clip: 0.15 s, and the time before 0.9 - 1 = -0.1, so the frame's blend runs on.
        var animator = Single(world);
        Assert.Equal(0.15f, animator.Seconds, 1e-5f);
        Assert.Equal(-0.1f, animator.PreviousSeconds, 1e-5f);
    }

    [Fact]
    public void A_clip_played_once_stops_at_its_end_and_a_speed_of_zero_holds()
    {
        using var world = World.Create();
        using var system = Create(world);
        var once = MeshAnimator.Playing(0, seconds: 0.9f);
        once.Once = true;
        var stopping = world.Create(DrawnMesh.Of(0), once);
        var held = world.Create(DrawnMesh.Of(0), MeshAnimator.Playing(0, seconds: 0.5f, speed: 0));

        system.Update(new FixedStepTime(Step, 0));

        Assert.Equal(1, world.Get<MeshAnimator>(stopping).Seconds);
        Assert.Equal(0.5f, world.Get<MeshAnimator>(held).Seconds);
    }

    [Fact]
    public void A_clip_the_asset_does_not_have_only_moves_on()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(DrawnMesh.Of(1), MeshAnimator.Playing(0, seconds: 0.9f));

        system.Update(new FixedStepTime(Step, 0));

        Assert.Equal(1.15f, Single(world).Seconds, 1e-5f);
    }

    [Fact]
    public void A_step_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world);
        for (var i = 0; i < 20; i++)
        {
            world.Create(DrawnMesh.Of(i % 2), MeshAnimator.Playing(0, seconds: i * 0.1f));
        }

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FixedStepTime(1f / 60, 0)));
    }

    private MeshAnimatorSystem Create(World world)
    {
        var system = new MeshAnimatorSystem(world, _assets);
        system.Initialize();
        return system;
    }

    private static MeshAnimator Single(World world)
    {
        var found = default(MeshAnimator);
        var count = 0;
        world.Query(
            in s_animated,
            (ref MeshAnimator animator) =>
            {
                found = animator;
                count++;
            }
        );
        Assert.Equal(1, count);
        return found;
    }
}
