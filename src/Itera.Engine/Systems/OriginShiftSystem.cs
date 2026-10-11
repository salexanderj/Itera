using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Keeps the match's origin near the active camera (fixed; a game registers it first, so the whole step runs in one
/// origin, the only place in a step it may move): when the camera is <c>Engine:Origin:ShiftMetres</c> or more from the
/// origin along any axis, the origin moves to the camera's whole metres (<see cref="OriginShifts"/>), and everything
/// kept in local positions with it; a jump the step will make (<c>tp</c>, <see cref="OriginShifts.RequestFollow"/>) goes
/// first, in place of the camera. Reads the <see cref="CameraPose"/> with <see cref="ActiveCamera"/>; makes no
/// structural changes; registered anywhere but first, it throws at the first step. Its <see cref="Initialize"/> moves
/// the origin a metre and back once, so the first move in play does not compile the move's code (a Debug build's first
/// move took 12 ms). A game that leaves it out never moves the origin on its own.
/// </summary>
public sealed class OriginShiftSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    private static readonly QueryDescription s_cameras = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private readonly OriginShifts _shifts;

    /// <param name="world">The match's ECS world.</param>
    /// <param name="shifts">The match's origin moves (with <c>Engine:Origin:ShiftMetres</c>).</param>
    public OriginShiftSystem(World world, OriginShifts shifts)
        : base(world)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        _shifts = shifts;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<CameraPose, ActiveCamera>(in s_cameras);
        // Towards the middle, so an origin at the map's edge stays within its reach.
        var at = _shifts.Origin.Metres;
        _shifts.ShiftTo(at.Plus(at.X > 0 ? -1 : 1, 0, 0));
        _shifts.ShiftTo(at);
    }

    public override void Update(in FixedStepTime step)
    {
        // Elsewhere a move would throw only once the camera went far: say so at the first step instead.
        if (!_shifts.MayMove)
        {
            throw new InvalidOperationException("OriginShiftSystem must be the first fixed system.");
        }

        // A jump this step makes (tp) asked for the origin first: the camera is still where it was.
        if (_shifts.TakeRequest(out var requested))
        {
            _shifts.Follow(in requested);
            return;
        }

        var job = new FindCamera();
        World.InlineQuery<FindCamera, CameraPose>(in s_cameras, ref job);
        if (job.Found)
        {
            _shifts.Follow(job.Position);
        }
    }

    /// <summary>The active camera's place.</summary>
    private struct FindCamera : IForEach<CameraPose>
    {
        public bool Found;
        public Vector3 Position;

        public void Update(ref CameraPose pose)
        {
            Found = true;
            Position = pose.Position;
        }
    }
}
