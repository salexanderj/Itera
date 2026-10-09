using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Rendering;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Outlines loose bodies while the debug lines are on (<c>lines</c>; frame group, before <c>CameraExtractSystem</c>): each
/// body's piece box, turned with it, and each character's stand-in cylinder while it has one, between their last two
/// poses by <see cref="FrameTime.Alpha"/>. Reads <see cref="BodyPose"/>, <see cref="BodyBox"/>,
/// <see cref="CharacterStandIn"/>, <see cref="CharacterPosition"/>, and <see cref="CharacterSize"/>; writes
/// <see cref="DebugLines"/>; no structural changes.
/// </summary>
public sealed class DebugOutlineSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    /// <summary>The colour of a loose body's outline.</summary>
    public static readonly uint BodyColour = DebugLines.Colour(255, 170, 40);

    /// <summary>The colour of a stand-in's outline.</summary>
    public static readonly uint StandInColour = DebugLines.Colour(80, 220, 255);

    private static readonly QueryDescription s_bodies = new QueryDescription().WithAll<BodyPose, BodyBox>();

    private static readonly QueryDescription s_standIns = new QueryDescription().WithAll<
        CharacterStandIn,
        CharacterPosition,
        CharacterSize
    >();

    private readonly DebugLines _lines;

    public DebugOutlineSystem(World world, DebugLines lines)
        : base(world)
    {
        _lines = lines;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<BodyPose, BodyBox>(in s_bodies);
        EcsChecks.RequireAll<CharacterStandIn, CharacterPosition, CharacterSize>(in s_standIns);
    }

    public override void Update(in FrameTime time)
    {
        if (!_lines.Enabled)
        {
            return;
        }

        var job = new Outline { Lines = _lines, Alpha = time.Alpha };
        World.InlineQuery<Outline, BodyPose, BodyBox>(in s_bodies, ref job);
        var standIns = new OutlineStandIn { Lines = _lines, Alpha = time.Alpha };
        World.InlineQuery<OutlineStandIn, CharacterStandIn, CharacterPosition, CharacterSize>(
            in s_standIns,
            ref standIns
        );
    }

    private struct OutlineStandIn : IForEach<CharacterStandIn, CharacterPosition, CharacterSize>
    {
        public DebugLines Lines;
        public float Alpha;

        public readonly void Update(
            ref CharacterStandIn standIn,
            ref CharacterPosition position,
            ref CharacterSize size
        )
        {
            if (standIn.Present)
            {
                var feet = Vector3.Lerp(position.PreviousFeet, position.Feet, Alpha);
                Lines.AddCylinder(feet, size.RadiusMetres, size.HeightMetres, StandInColour);
            }
        }
    }

    private struct Outline : IForEach<BodyPose, BodyBox>
    {
        public DebugLines Lines;
        public float Alpha;

        public readonly void Update(ref BodyPose pose, ref BodyBox box)
        {
            var position = Vector3.Lerp(pose.PreviousPosition, pose.Position, Alpha);
            var orientation = Quaternion.Slerp(pose.PreviousOrientation, pose.Orientation, Alpha);
            Lines.AddBox(position, orientation, box.Min, box.Max, BodyColour);
        }
    }
}
