using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;

namespace Itera.Engine.Physics;

/// <summary>
/// Contact rules: a pair collides when one side is dynamic, except that a character's stand-in touches loose bodies of
/// lifted pieces only (not the voxels, not other stand-ins), a body cut off a character touches the voxels only
/// (<see cref="BodyKind.Cut"/>), and a body touches only the voxel statics of the level it collides with
/// (<see cref="BodyKinds"/>). One material for all pairs.
/// </summary>
internal struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    private readonly BodyKinds _kinds;
    private SpringSettings _contactSpringiness;
    private float _maximumRecoveryVelocity;
    private float _frictionCoefficient;

    public NarrowPhaseCallbacks(BodyKinds kinds)
    {
        _kinds = kinds;
        _contactSpringiness = default;
        _maximumRecoveryVelocity = 0;
        _frictionCoefficient = 0;
    }

    public void Initialize(Simulation simulation)
    {
        _contactSpringiness = new SpringSettings(30, 1);
        _maximumRecoveryVelocity = 2f;
        _frictionCoefficient = 1f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AllowContactGeneration(
        int workerIndex,
        CollidableReference a,
        CollidableReference b,
        ref float speculativeMargin
    )
    {
        if (a.Mobility != CollidableMobility.Dynamic && b.Mobility != CollidableMobility.Dynamic)
        {
            return false;
        }

        // Statics are the voxels' boxes and a game's mesh colliders.
        if (a.Mobility == CollidableMobility.Static)
        {
            return TouchesVoxels(b.BodyHandle, a.StaticHandle);
        }

        if (b.Mobility == CollidableMobility.Static)
        {
            return TouchesVoxels(a.BodyHandle, b.StaticHandle);
        }

        // A body cut off a character touches no other body, and stand-ins do not touch each other.
        var kindA = _kinds[a.BodyHandle.Value];
        var kindB = _kinds[b.BodyHandle.Value];
        return kindA != BodyKind.Cut
            && kindB != BodyKind.Cut
            && !(kindA == BodyKind.StandIn && kindB == BodyKind.StandIn);
    }

    /// <summary>
    /// True when <paramref name="body"/> touches the static <paramref name="ground"/>: it is no stand-in, and the static is
    /// of the voxel level the body collides with (a giant's parts the coarse ones) or a game's, which every level touches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool TouchesVoxels(BodyHandle body, StaticHandle ground)
    {
        if (_kinds[body.Value] == BodyKind.StandIn)
        {
            return false;
        }

        var level = _kinds.StaticLevel(ground.Value);
        return level == BodyKinds.AnyLevel || level == _kinds.Level(body.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
    {
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ConfigureContactManifold<TManifold>(
        int workerIndex,
        CollidablePair pair,
        ref TManifold manifold,
        out PairMaterialProperties pairMaterial
    )
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = _frictionCoefficient;
        pairMaterial.MaximumRecoveryVelocity = _maximumRecoveryVelocity;
        pairMaterial.SpringSettings = _contactSpringiness;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ConfigureContactManifold(
        int workerIndex,
        CollidablePair pair,
        int childIndexA,
        int childIndexB,
        ref ConvexContactManifold manifold
    )
    {
        return true;
    }

    public readonly void Dispose() { }
}
