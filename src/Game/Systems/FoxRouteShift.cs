using System.Numerics;
using Arch.Core;
using Game.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;

namespace Game.Systems;

/// <summary>
/// Moves the fox's circle with the match's origin: <see cref="FoxRoute.Centre"/> is a local position of the game's own
/// component, which the engine's shift does not know of (it moves the fox's <c>MeshPlacement</c>).
/// </summary>
public sealed class FoxRouteShift(EcsWorld ecs) : IOriginShiftHandler
{
    private static readonly QueryDescription s_routes = new QueryDescription().WithAll<FoxRoute>();

    public void OnOriginShifted(in OriginShift shift)
    {
        var job = new Move { Metres = shift.Metres };
        ecs.World.InlineQuery<Move, FoxRoute>(in s_routes, ref job);
    }

    private struct Move : IForEach<FoxRoute>
    {
        public Vector3 Metres;

        public readonly void Update(ref FoxRoute route) => route.Centre -= Metres;
    }
}
