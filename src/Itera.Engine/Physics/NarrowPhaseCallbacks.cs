using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;

namespace Itera.Engine.Physics;

/// <summary>
/// Contact rules: a pair collides when one side is dynamic, except that a character's stand-in touches loose bodies of
/// lifted pieces only (not the voxels, not other stand-ins), a body cut off a character touches the voxels only
/// (<see cref="BodyKind.Cut"/>), and a body touches only the voxel statics of the level it collides with
/// (<see cref="BodyKinds"/>); then a pair whose layers do not touch, or that shares a group other than 0, does not
/// (<see cref="BodyKinds.LayersAllow"/>, <see cref="PhysicsLayers"/>). A trigger zone (<see cref="BodyKind.Trigger"/>)
/// pairs with every body its layers allow (moving, asleep, frozen, or mirrored; not statics, stand-ins, or other zones):
/// a pair with a contact of depth 0 or more is recorded for <see cref="PhysicsTriggers"/>, and none makes a constraint.
/// A pair's material is its two sides' (<see cref="BodyKinds.ContactOf"/>): friction the product of their square
/// roots, the engine's contact springs (30 Hz, critically damped, 2 m/s) for all. A pair with a
/// bouncy side or a side marked <see cref="PhysicsMarks.ContactEvents"/> (not a stand-in) that is touching, or will be by
/// the step's end at its speeds with the match's gravity, is recorded for <see cref="PhysicsContacts"/>, which sets its
/// bounce and gives its impact events after the step; other pairs are not looked at further.
/// </summary>
internal struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    private readonly BodyKinds _kinds;
    private readonly PhysicsContacts? _contacts;
    private readonly PhysicsTriggers? _triggers;

    public NarrowPhaseCallbacks(BodyKinds kinds, PhysicsContacts? contacts = null, PhysicsTriggers? triggers = null)
    {
        _kinds = kinds;
        _contacts = contacts;
        _triggers = triggers;
    }

    public readonly void Initialize(Simulation simulation) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AllowContactGeneration(
        int workerIndex,
        CollidableReference a,
        CollidableReference b,
        ref float speculativeMargin
    ) =>
        IsTrigger(a) || IsTrigger(b)
            ? TriggerAllows(a, b) && _kinds.LayersAllow(a, b)
            : EngineAllows(a, b) && _kinds.LayersAllow(a, b);

    /// <summary>True for a trigger zone's body.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool IsTrigger(CollidableReference collidable) =>
        collidable.Mobility != CollidableMobility.Static && _kinds[collidable.BodyHandle.Value] == BodyKind.Trigger;

    /// <summary>A zone pairs with a body that is not a stand-in or a zone; never with a static.</summary>
    private readonly bool TriggerAllows(CollidableReference a, CollidableReference b)
    {
        if (a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static)
        {
            return false;
        }

        var kindA = _kinds[a.BodyHandle.Value];
        var kindB = _kinds[b.BodyHandle.Value];
        return !(kindA == BodyKind.Trigger && kindB == BodyKind.Trigger)
            && kindA != BodyKind.StandIn
            && kindB != BodyKind.StandIn;
    }

    /// <summary>The engine's own rules, before a game's layers and groups.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool EngineAllows(CollidableReference a, CollidableReference b)
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
        if (IsTrigger(pair.A) || IsTrigger(pair.B))
        {
            // A zone stops nothing: it notes a body that overlaps it.
            pairMaterial = default;
            if (_triggers is not null && Overlaps(ref manifold))
            {
                var aIsTrigger = IsTrigger(pair.A);
                _triggers.Record(
                    workerIndex,
                    aIsTrigger ? pair.A.BodyHandle : pair.B.BodyHandle,
                    aIsTrigger ? pair.B.BodyHandle : pair.A.BodyHandle
                );
            }

            return false;
        }

        ref readonly var a = ref _kinds.ContactOf(pair.A);
        ref readonly var b = ref _kinds.ContactOf(pair.B);
        pairMaterial.FrictionCoefficient = a.RootFriction * b.RootFriction;
        pairMaterial.MaximumRecoveryVelocity = ContactMaterial.MaximumRecoveryVelocity;
        pairMaterial.SpringSettings = ContactMaterial.Spring;
        var bounciness = MathF.Max(a.Bounciness, b.Bounciness);
        if (_contacts is not null && manifold.Count > 0)
        {
            var reports = _kinds.ReportsContacts(pair.A) || _kinds.ReportsContacts(pair.B);
            if (bounciness > 0 || reports)
            {
                RecordContact(workerIndex, pair, ref manifold, bounciness, reports);
            }
        }

        return true;
    }

    /// <summary>
    /// Records the pair's deepest contact with the speed the pair comes together at there, when it touches or will within
    /// the step. Stand-ins are left out (a character's push is read apart).
    /// </summary>
    private readonly void RecordContact<TManifold>(
        int workerIndex,
        CollidablePair pair,
        ref TManifold manifold,
        float bounciness,
        bool reports
    )
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        if (IsStandIn(pair.A) || IsStandIn(pair.B))
        {
            return;
        }

        var deepest = 0;
        var depth = manifold.GetDepth(0);
        for (var i = 1; i < manifold.Count; i++)
        {
            var d = manifold.GetDepth(i);
            if (d > depth)
            {
                depth = d;
                deepest = i;
            }
        }

        var contacts = _contacts!;
        var normal = manifold.GetNormal(deepest);
        var offsetA = manifold.GetOffset(deepest);
        var offsetB = manifold.Convex
            ? Unsafe.As<TManifold, ConvexContactManifold>(ref manifold).OffsetB
            : Unsafe.As<TManifold, NonconvexContactManifold>(ref manifold).OffsetB;
        var bodies = contacts.Bodies!;
        var relative =
            PhysicsContacts.VelocityAt(bodies, pair.A, offsetA)
            - PhysicsContacts.VelocityAt(bodies, pair.B, offsetA - offsetB);
        var approach = -Vector3.Dot(relative, normal);
        // Gravity, as each side falls by its share, pulls them together (or apart) by this, metres a second each second.
        var towards = -Vector3.Dot(contacts.Gravity * (FallShare(pair.A) - FallShare(pair.B)), normal);
        var step = contacts.StepSeconds;
        if (depth + MathF.Max(approach + towards * step, 0) * step < -PhysicsContacts.TouchingMetres)
        {
            return;
        }

        // The speed they meet at, gravity's part over the gap still between them added (none if they would not meet).
        if (depth < 0 && (approach > 0 || towards > 0))
        {
            approach = MathF.Sqrt(MathF.Max(approach * approach - 2 * towards * depth, 0));
        }

        var sideA = SideOf(pair.A);
        var sideB = SideOf(pair.B);
        var centreA =
            pair.A.Mobility == CollidableMobility.Static
                ? contacts.Statics![pair.A.StaticHandle].Pose.Position
                : bodies[pair.A.BodyHandle].Pose.Position;
        contacts.Record(
            workerIndex,
            new ContactRecord
            {
                A = pair.A,
                B = pair.B,
                SideA = sideA,
                SideB = sideB,
                Key = PhysicsContacts.KeyOf(sideA, sideB),
                Depth = depth,
                Point = centreA + offsetA,
                Reports = reports,
                Normal = normal,
                OffsetA = offsetA,
                OffsetB = offsetB,
                Approach = approach,
                Bounciness = bounciness,
            }
        );
    }

    /// <summary>True when a contact of the manifold has a depth of 0 or more: the shapes overlap.</summary>
    private static bool Overlaps<TManifold>(ref TManifold manifold)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        for (var i = 0; i < manifold.Count; i++)
        {
            if (manifold.GetDepth(i) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Its side of a pair: every voxel static is the one voxel ground.</summary>
    private readonly uint SideOf(CollidableReference collidable)
    {
        if (collidable.Mobility != CollidableMobility.Static)
        {
            return PhysicsContacts.BodySide(collidable.BodyHandle);
        }

        return _kinds.StaticLevel(collidable.StaticHandle.Value) == BodyKinds.AnyLevel
            ? PhysicsContacts.StaticSide(collidable.StaticHandle)
            : PhysicsContacts.VoxelGround;
    }

    /// <summary>The share of gravity <paramref name="collidable"/> falls by: none for a static or a kinematic body.</summary>
    private readonly float FallShare(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Dynamic ? _kinds.GravityScale(collidable.BodyHandle.Value) : 0;

    private readonly bool IsStandIn(CollidableReference collidable) =>
        collidable.Mobility != CollidableMobility.Static && _kinds[collidable.BodyHandle.Value] == BodyKind.StandIn;

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
