using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.MeshSample.Components;
using Itera.Modding;

namespace Itera.MeshSample.Systems;

/// <summary>
/// The fox (fixed group, before the engine's <c>MeshAnimatorSystem</c>): made at the start walking (its Walk clip)
/// round a circle of <see cref="CircleMetres"/> about <see cref="Middle"/> on the flat ground, clear of the yard's
/// pieces, at <see cref="Speed"/>, facing the way it goes. Its place on the circle is its <see cref="SampleFox"/>.
/// Reads and writes <see cref="SampleFox"/> and <see cref="MeshPlacement"/>; the engine's <c>MeshDrawSystem</c> draws
/// it.
/// </summary>
public sealed class SampleFoxSystem(World world, MeshAssets assets)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    /// <summary>The middle of its circle, in metres.</summary>
    public static Vector3 Middle { get; } = new(-4, 0, 1);

    public const float CircleMetres = 3;

    /// <summary>Its walking speed, in metres per second.</summary>
    public const float Speed = 1.1f;

    private static readonly QueryDescription s_fox = new QueryDescription().WithAll<SampleFox, MeshPlacement>();

    public override void Initialize()
    {
        EcsChecks.RequireAll<SampleFox, MeshPlacement>(in s_fox);
        var index = assets.Find(SampleRunStartup.Fox);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"The fox's model ({SampleRunStartup.Fox}) was not declared; SampleRunStartup declares it."
            );
        }

        var walk = assets[index].Rig?.FindClip("Walk") ?? -1;
        World.Create(new SampleFox(), Placement(0, null), DrawnMesh.Of(index), MeshAnimator.Playing(walk));
    }

    public override void Update(in FixedStepTime step)
    {
        var job = new Walk { Turn = Speed / CircleMetres * step.StepSeconds };
        World.InlineQuery<Walk, SampleFox, MeshPlacement>(in s_fox, ref job);
    }

    /// <summary>
    /// On the circle at <paramref name="angle"/>, facing along it (the fox's head is along +z), with the last step's
    /// pose before it.
    /// </summary>
    private static MeshPlacement Placement(float angle, MeshPlacement? before)
    {
        var at = Middle + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * CircleMetres;
        // Going round with the angle: the way is (-sin, 0, cos), a turn about +y from +z of atan2(-sin, cos).
        var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(-MathF.Sin(angle), MathF.Cos(angle)));
        var placement = MeshPlacement.At(at, facing);
        if (before is { } last)
        {
            placement.PreviousPosition = last.Position;
            placement.PreviousOrientation = last.Orientation;
        }

        return placement;
    }

    private struct Walk : IForEach<SampleFox, MeshPlacement>
    {
        public float Turn;

        public readonly void Update(ref SampleFox fox, ref MeshPlacement placement)
        {
            fox.Angle += Turn;
            placement = Placement(fox.Angle, placement);
        }
    }
}
