using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Physics;

// Bodies of character parts: a cut's pieces and their mirrors, and ragdolls of several jointed bodies.
public sealed partial class LooseBodies
{
    /// <summary>
    /// Makes a loose body of several models lying where <paramref name="parts"/> say, such as the parts and pieces cut
    /// off a character, moving at <paramref name="velocity"/>. Bepu sees a compound of one convex hull per model (of its
    /// level-0 tree's blocking voxels) at <paramref name="density"/> kilograms per cubic metre, centred on its centre of
    /// mass, which is the body's position and pivot. The body starts unturned, so each model keeps its place and turn
    /// relative to it (<see cref="PartsOf"/>), and is drawn blended <paramref name="tint"/> towards its tint colour. It owns the
    /// models marked so and removes them, with their trees, when it goes; it never goes back into the grid
    /// (<see cref="IsCut"/>). It touches only the voxels (<see cref="BodyKind.Cut"/>). False when
    /// <see cref="MaxBodies"/> exist already or nothing in it blocks.
    /// </summary>
    public bool TryCreate(
        ReadOnlySpan<LoosePart> parts,
        float density,
        Vector3 velocity,
        float tint,
        out BodyHandle body
    )
    {
        body = default;
        return _bodies.Count < _maxBodies && Build(parts, density, velocity, tint, 0, false, out body);
    }

    /// <summary>
    /// Makes a mirror of a cut body another game simulates (a piece cut off a session host's character, on a client):
    /// the body <see cref="TryCreate(ReadOnlySpan{LoosePart}, float, Vector3, float, out BodyHandle)"/>
    /// would make of <paramref name="parts"/>, centred on the same centre of mass, but kinematic, for
    /// <see cref="MoveMirror"/> to put where the host says. It owns the models marked so, as a cut body does. It is made
    /// past <see cref="MaxBodies"/>, which the host's own limit keeps for it. False when
    /// nothing in it blocks; the caller then keeps its models.
    /// </summary>
    public bool TryCreateMirror(ReadOnlySpan<LoosePart> parts, float tint, out BodyHandle body) =>
        Build(parts, DensityKilogramsPerCubicMetre, Vector3.Zero, tint, 0, true, out body);

    /// <summary>
    /// Makes a ragdoll of several models lying where <paramref name="parts"/> say, such as a limb cut off a character or
    /// a whole character that collapses: the parts with the same entry in <paramref name="bodyOf"/> (0, 1, ...) make one
    /// body (as <see cref="TryCreate(ReadOnlySpan{LoosePart}, float, Vector3, float, out BodyHandle)"/> makes it), and
    /// each of <paramref name="joints"/> joins two of them with a ball socket at its point, a swing limit about its
    /// axis, and a twist limit of <see cref="RagdollTwistRadians"/> about it. Its bodies touch only the voxels, as
    /// every body cut off a character does, so not each other where their parts overlap at the joints; they rest only
    /// when all of them rest (<see cref="IsAtRest"/>), and so are removed together. The bodies made are added to
    /// <paramref name="made"/>, in the order of <paramref name="bodyOf"/>'s entries. A body whose parts block nothing is
    /// not made: the models it would own are removed, and its joints left out. False, with nothing made and the caller's
    /// models kept, when the bodies would pass <see cref="MaxBodies"/>, when <paramref name="bodyOf"/> and
    /// <paramref name="parts"/> differ in length or an entry is negative, or when a joint names a body outside
    /// <paramref name="bodyOf"/>'s entries or joins a body to itself.
    /// </summary>
    public bool TryCreateRagdoll(
        ReadOnlySpan<LoosePart> parts,
        ReadOnlySpan<int> bodyOf,
        ReadOnlySpan<RagdollJoint> joints,
        float density,
        Vector3 velocity,
        float tint,
        List<BodyHandle> made
    )
    {
        var count = 0;
        foreach (var index in bodyOf)
        {
            if (index < 0)
            {
                return false;
            }

            count = Math.Max(count, index + 1);
        }

        if (parts.Length != bodyOf.Length || count == 0 || _bodies.Count + count > _maxBodies)
        {
            return false;
        }

        foreach (ref readonly var joint in joints)
        {
            if (
                joint.BodyA < 0
                || joint.BodyA >= count
                || joint.BodyB < 0
                || joint.BodyB >= count
                || joint.BodyA == joint.BodyB
            )
            {
                return false;
            }
        }

        var ragdoll = ++_lastRagdoll;
        var handles = new BodyHandle?[count];
        var members = new List<BodyHandle>(count);
        var own = new List<LoosePart>();
        for (var b = 0; b < count; b++)
        {
            own.Clear();
            for (var i = 0; i < parts.Length; i++)
            {
                if (bodyOf[i] == b)
                {
                    own.Add(parts[i]);
                }
            }

            if (Build([.. own], density, velocity, tint, ragdoll, false, out var body))
            {
                handles[b] = body;
                members.Add(body);
                made.Add(body);
                continue;
            }

            foreach (var part in own)
            {
                if (part.Owned)
                {
                    _models.RemoveAndFree(part.Model);
                }
            }
        }

        if (members.Count == 0)
        {
            return true;
        }

        _ragdolls[ragdoll] = members;
        var joined = JoinRagdoll(ragdoll, handles, joints);
        LogRagdoll(ragdoll, members.Count, joined, parts.Length);
        return true;
    }

    /// <summary>
    /// Makes a ragdoll of <paramref name="shapes"/>, a body each (a figure drawn from a mesh, falling where it died),
    /// moving at <paramref name="velocity"/>, joined by <paramref name="joints"/> as
    /// <see cref="TryCreateRagdoll(ReadOnlySpan{LoosePart}, ReadOnlySpan{int}, ReadOnlySpan{RagdollJoint}, float, Vector3, float, List{BodyHandle})"/>
    /// joins its bodies. Its bodies have no models: nothing draws them, and the game reads their poses
    /// (<see cref="PivotOf"/>, <see cref="OrientationOf"/>) to pose what it draws. As every body cut off a character, they
    /// touch only the voxels and the mesh colliders (<see cref="BodyKind.Cut"/>), so neither characters nor other bodies
    /// nor what the world's rays meet; they rest, freeze, thaw, and go together. The bodies made are added to
    /// <paramref name="made"/> in the order of <paramref name="shapes"/>. False, with nothing made, when the bodies would
    /// pass <see cref="MaxBodies"/>, a shape is not finite, its radius or mass not above 0, or a joint names a body outside
    /// <paramref name="shapes"/> or joins a body to itself.
    /// </summary>
    public bool TryCreateRagdoll(
        ReadOnlySpan<RagdollShape> shapes,
        ReadOnlySpan<RagdollJoint> joints,
        Vector3 velocity,
        List<BodyHandle> made
    ) => TryCreateRagdoll(shapes, joints, velocity, Vector3.Zero, Vector3.Zero, made);

    /// <summary>
    /// Makes a ragdoll of <paramref name="shapes"/> as
    /// <see cref="TryCreateRagdoll(ReadOnlySpan{RagdollShape}, ReadOnlySpan{RagdollJoint}, Vector3, List{BodyHandle})"/>
    /// does, its whole turning at <paramref name="spin"/> (radians a second, about its direction) round
    /// <paramref name="about"/> as it starts: each body turns at the spin, moving at <paramref name="velocity"/> and as its
    /// middle turns round that point (a body starting to topple about its feet). Also false when the spin or the point is
    /// not finite.
    /// </summary>
    public bool TryCreateRagdoll(
        ReadOnlySpan<RagdollShape> shapes,
        ReadOnlySpan<RagdollJoint> joints,
        Vector3 velocity,
        Vector3 spin,
        Vector3 about,
        List<BodyHandle> made
    )
    {
        ArgumentNullException.ThrowIfNull(made);
        if (
            shapes.IsEmpty
            || _bodies.Count + shapes.Length > _maxBodies
            || !float.IsFinite(spin.X + spin.Y + spin.Z + about.X + about.Y + about.Z)
        )
        {
            return false;
        }

        foreach (ref readonly var shape in shapes)
        {
            if (
                !float.IsFinite(shape.From.X + shape.From.Y + shape.From.Z + shape.To.X + shape.To.Y + shape.To.Z)
                || !(shape.RadiusMetres > 0)
                || !(shape.MassKilograms > 0)
                || !float.IsFinite(shape.RadiusMetres + shape.MassKilograms)
            )
            {
                return false;
            }
        }

        foreach (ref readonly var joint in joints)
        {
            if (
                joint.BodyA < 0
                || joint.BodyA >= shapes.Length
                || joint.BodyB < 0
                || joint.BodyB >= shapes.Length
                || joint.BodyA == joint.BodyB
            )
            {
                return false;
            }
        }

        var ragdoll = ++_lastRagdoll;
        var handles = new BodyHandle?[shapes.Length];
        var members = new List<BodyHandle>(shapes.Length);
        for (var b = 0; b < shapes.Length; b++)
        {
            var body = BuildShape(in shapes[b], velocity, spin, about, ragdoll);
            handles[b] = body;
            members.Add(body);
            made.Add(body);
        }

        _ragdolls[ragdoll] = members;
        var joined = JoinRagdoll(ragdoll, handles, joints);
        LogRagdoll(ragdoll, members.Count, joined, shapes.Length);
        return true;
    }

    /// <summary>
    /// A cut body of <paramref name="form"/>'s capsule (or sphere), centred on its middle, unturned, the capsule turned
    /// in it along its ends, belonging to <paramref name="ragdoll"/>, turning at <paramref name="spin"/> and moving as its
    /// middle turns at it round <paramref name="about"/>.
    /// </summary>
    private BodyHandle BuildShape(in RagdollShape form, Vector3 velocity, Vector3 spin, Vector3 about, int ragdoll)
    {
        var simulation = _physics.Simulation;
        var along = form.To - form.From;
        var length = along.Length();
        var middle = (form.From + form.To) / 2;
        using var builder = new CompoundBuilder(_physics.Pool, simulation.Shapes, 1);
        if (length > 1e-4f)
        {
            // Bepu's capsule lies along its own +y.
            builder.Add(
                new Capsule(form.RadiusMetres, length),
                new RigidPose(middle, ArcFromUp(along / length)),
                form.MassKilograms
            );
        }
        else
        {
            builder.Add(new Sphere(form.RadiusMetres), new RigidPose(middle), form.MassKilograms);
        }

        builder.BuildDynamicCompound(out var children, out var inertia, out var centre);
        var shape = simulation.Shapes.Add(new Compound(children));
        var body = simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(centre),
                new BodyVelocity(velocity + Vector3.Cross(spin, centre - about), spin),
                inertia,
                new CollidableDescription(shape, ContinuousDetection.Passive),
                new BodyActivityDescription(0.01f)
            )
        );
        _physics.Kinds.Set(body, BodyKind.Cut, 0, ragdoll);
        var reach = new Vector3(form.RadiusMetres);
        _bodies.Add(
            body,
            new Loose(default, Vector3.Zero, shape, form.MassKilograms, -1, [], [], true, 0, ragdoll)
            {
                BoxMin = Vector3.Min(form.From, form.To) - reach - centre,
                BoxMax = Vector3.Max(form.From, form.To) + reach - centre,
                Born = ++_births,
            }
        );
        return body;
    }

    /// <summary>The shortest turn from +y to <paramref name="direction"/> (of length 1).</summary>
    internal static Quaternion ArcFromUp(Vector3 direction)
    {
        var dot = Math.Clamp(direction.Y, -1, 1);
        if (dot > 1 - 1e-6f)
        {
            return Quaternion.Identity;
        }

        if (dot < -1 + 1e-6f)
        {
            return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        }

        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }

    /// <summary>
    /// Joins <paramref name="ragdoll"/>'s bodies (<paramref name="handles"/>, by index; null for one not made) by
    /// <paramref name="joints"/>: a ball socket at each joint's point, a swing limit about its axis, and a twist limit of
    /// <see cref="RagdollTwistRadians"/> about it, kept for a thaw to put back; the number joined.
    /// </summary>
    private int JoinRagdoll(int ragdoll, BodyHandle?[] handles, ReadOnlySpan<RagdollJoint> joints)
    {
        var simulation = _physics.Simulation;
        var joined = 0;
        var links = new List<RagdollLink>(joints.Length);
        foreach (var joint in joints)
        {
            if (handles[joint.BodyA] is not { } a || handles[joint.BodyB] is not { } b)
            {
                continue;
            }

            // The bodies start unturned, so their local axes are the world's.
            var offsetA = joint.Point - simulation.Bodies[a].Pose.Position;
            var offsetB = joint.Point - simulation.Bodies[b].Pose.Position;
            var axis = Vector3.Normalize(joint.Axis);
            var springs = new SpringSettings(30, 1);
            var basis = TwistBasis(axis);
            var link = new RagdollLink(
                a,
                b,
                new BallSocket
                {
                    LocalOffsetA = offsetA,
                    LocalOffsetB = offsetB,
                    SpringSettings = springs,
                },
                new SwingLimit
                {
                    AxisLocalA = axis,
                    AxisLocalB = axis,
                    MinimumDot = MathF.Cos(joint.MaxSwingRadians),
                    SpringSettings = springs,
                },
                new TwistLimit
                {
                    LocalBasisA = basis,
                    LocalBasisB = basis,
                    MinimumAngle = -RagdollTwistRadians,
                    MaximumAngle = RagdollTwistRadians,
                    SpringSettings = springs,
                }
            );
            Join(in link);
            links.Add(link);
            joined++;
        }

        _links[ragdoll] = links;
        return joined;
    }

    /// <summary>Adds a joint's ball socket, swing limit, and twist limit to the solver.</summary>
    private void Join(in RagdollLink link)
    {
        var solver = _physics.Simulation.Solver;
        solver.Add(link.A, link.B, link.Ball);
        solver.Add(link.A, link.B, link.Swing);
        solver.Add(link.A, link.B, link.Twist);
    }

    /// <summary>
    /// Puts back the joints of <paramref name="ragdoll"/> after its bodies thawed together (<see cref="VoxelColliders"/>):
    /// Bepu dropped them when every body of it was made kinematic. A joint one of whose bodies has gone stays gone.
    /// </summary>
    private void Rejoin(int ragdoll)
    {
        if (!_links.TryGetValue(ragdoll, out var links))
        {
            return;
        }

        var joined = 0;
        foreach (var link in links)
        {
            if (
                _bodies.TryGetValue(link.A, out var a)
                && a.Ragdoll == ragdoll
                && _bodies.TryGetValue(link.B, out var b)
                && b.Ragdoll == ragdoll
            )
            {
                Join(in link);
                joined++;
            }
        }

        LogRejoined(ragdoll, joined);
    }

    /// <summary>
    /// A turn that takes the Z axis to <paramref name="axis"/>: a twist limit's basis, whose Z axis is the twist axis. The
    /// bodies start unturned, so one basis serves both.
    /// </summary>
    private static Quaternion TwistBasis(Vector3 axis)
    {
        var dot = axis.Z;
        if (dot > 0.9999f)
        {
            return Quaternion.Identity;
        }

        if (dot < -0.9999f)
        {
            return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        }

        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, axis)), MathF.Acos(dot));
    }

    /// <summary>
    /// Makes one body of <paramref name="parts"/> in ragdoll <paramref name="ragdoll"/> (0 for none): a compound of one
    /// convex hull per model (of its level-0 tree's blocking voxels) at <paramref name="density"/>, centred on its
    /// centre of mass; <paramref name="kinematic"/> for a mirror (<see cref="TryCreateMirror(ReadOnlySpan{LoosePart}, float, out BodyHandle)"/>).
    /// A body whose parts are all of 4 cm voxels (a large body's) collides with the coarse voxel cells
    /// (<see cref="CollisionLevel"/>). False when nothing in it blocks.
    /// </summary>
    private bool Build(
        ReadOnlySpan<LoosePart> parts,
        float density,
        Vector3 velocity,
        float tint,
        int ragdoll,
        bool kinematic,
        out BodyHandle body
    )
    {
        body = default;
        if (parts.IsEmpty)
        {
            return false;
        }

        var simulation = _physics.Simulation;
        using var builder = new CompoundBuilder(_physics.Pool, simulation.Shapes, parts.Length);
        var mass = 0f;
        var hulls = 0;
        // A giant's body (every part of 4 cm voxels or coarser) collides with the world's coarse voxels, when it has them.
        // Without voxel terrain a body meets only the game's mesh colliders, which every level touches.
        var coarseLevel = _voxels is null ? 0 : VoxelColliders.CoarseLevelOf(_voxels.Grid);
        var level = _voxels is not null && _voxels.Levels > coarseLevel ? coarseLevel : 0;
        foreach (ref readonly var part in parts)
        {
            var coarse = CellCentimetres(in part) >= VoxelColliders.CoarsePartCentimetres;
            level = Math.Min(level, coarse ? coarseLevel : 0);
        }

        foreach (ref readonly var part in parts)
        {
            if (!TryBuildPartHull(in part, out var hull, out var centre, out var voxels))
            {
                continue;
            }

            var cell = CellMetres(in part);
            var partMass = voxels * (cell * cell * cell) * density;
            builder.Add(hull, new RigidPose(part.Corner + Vector3.Transform(centre, part.Turn), part.Turn), partMass);
            mass += partMass;
            hulls++;
        }

        if (hulls == 0)
        {
            return false;
        }

        // A mirror is centred as the body it mirrors is, on the centre of mass, so the host's poses place it.
        builder.BuildDynamicCompound(out var children, out var inertia, out var centreOfMass);
        var shape = simulation.Shapes.Add(new Compound(children));
        // No sweeps for cut pieces a pile of them falling into a hole spent most of its
        // collision time sweeping. Their contacts still reach as far as they move in a step; only a small piece moving
        // fast could pass a 1 cm wall. Thrown balls keep their sweeps.
        var collidable = new CollidableDescription(shape, ContinuousDetection.Passive);
        body = simulation.Bodies.Add(
            kinematic
                ? BodyDescription.CreateKinematic(
                    new RigidPose(centreOfMass),
                    new BodyVelocity(velocity),
                    collidable,
                    new BodyActivityDescription(-1)
                )
                : BodyDescription.CreateDynamic(
                    new RigidPose(centreOfMass),
                    new BodyVelocity(velocity),
                    inertia,
                    collidable,
                    new BodyActivityDescription(0.01f)
                )
        );
        _physics.Kinds.Set(body, BodyKind.Cut, level, ragdoll);

        var models = new LooseModel[parts.Length];
        var owned = new List<int>();
        var boxMin = new Vector3(float.PositiveInfinity);
        var boxMax = new Vector3(float.NegativeInfinity);
        for (var i = 0; i < parts.Length; i++)
        {
            ref readonly var part = ref parts[i];
            var corner = part.Corner - centreOfMass;
            models[i] = new LooseModel(part.Model, corner, part.Turn);
            if (part.Owned)
            {
                owned.Add(part.Model);
            }

            for (var c = 0; c < 8; c++)
            {
                var point =
                    corner
                    + Vector3.Transform(
                        new Vector3(
                            (c & 1) != 0 ? part.Size.X : 0,
                            (c & 2) != 0 ? part.Size.Y : 0,
                            (c & 4) != 0 ? part.Size.Z : 0
                        ) * CellMetres(in part),
                        part.Turn
                    );
                boxMin = Vector3.Min(boxMin, point);
                boxMax = Vector3.Max(boxMax, point);
            }
        }

        _bodies.Add(
            body,
            new Loose(default, Vector3.Zero, shape, mass, -1, models, [.. owned], true, tint, ragdoll)
            {
                BoxMin = boxMin,
                BoxMax = boxMax,
                Born = ++_births,
            }
        );
        LogCut(body.Value, parts.Length, hulls, mass, centreOfMass.X, centreOfMass.Y, centreOfMass.Z);
        return true;
    }

    /// <summary>
    /// The convex hull of <paramref name="part"/>'s blocking voxels, from its model's corner in its own axes (metres), where
    /// the hull's middle lies from there, and how many voxels block. A shared part's hull is built once from its voxel
    /// boxes and then from the corners that hull kept. A hull of more than <see cref="PartHullDirections"/> corners is
    /// built again from its farthest corner in each of that many directions. False when nothing in it blocks.
    /// </summary>
    private bool TryBuildPartHull(in LoosePart part, out ConvexHull hull, out Vector3 centre, out long voxels)
    {
        var pool = _physics.Pool;
        var key = new SharedPart(part.Tree.State, part.Size, CellCentimetres(in part));
        var cell = CellMetres(in part);
        if (!part.Owned && _sharedHulls.TryGetValue(key, out var shared))
        {
            hull = new ConvexHull(shared.Corners, pool, out centre);
            voxels = shared.Voxels;
            return true;
        }

        _boxes.Build(_models.Memory, new VoxelPiece(0, default, part.Size, part.Tree.State, 0, 0), in _passable);
        var boxes = _boxes.Boxes;
        if (boxes.Length == 0)
        {
            hull = default;
            centre = default;
            voxels = 0;
            return false;
        }

        if (_points.Length < boxes.Length * 8)
        {
            _points = new Vector3[boxes.Length * 8];
        }

        var count = 0;
        voxels = 0;
        foreach (var box in boxes)
        {
            voxels += box.Voxels;
            for (var corner = 0; corner < 8; corner++)
            {
                _points[count++] =
                    new Vector3(
                        (corner & 1) != 0 ? box.X1 + 1 : box.X0,
                        (corner & 2) != 0 ? box.Y1 + 1 : box.Y0,
                        (corner & 4) != 0 ? box.Z1 + 1 : box.Z0
                    ) * cell;
            }
        }

        var points = _points.AsSpan(0, count);
        ConvexHullHelper.CreateShape(points, pool, out var data, out centre, out hull);
        var hullCorners = data.OriginalVertexMapping.Length;
        if (hullCorners > PartHullDirections)
        {
            // A large hull (a large body's parts) is built again from its farthest corners in each direction.
            if (_hullCorners.Length < hullCorners)
            {
                _hullCorners = new Vector3[hullCorners];
            }

            for (var i = 0; i < hullCorners; i++)
            {
                _hullCorners[i] = points[data.OriginalVertexMapping[i]];
            }

            data.Dispose(pool);
            hull.Dispose(pool);
            points = _hullCorners.AsSpan(0, SupportPoints(_hullCorners.AsSpan(0, hullCorners), s_partHullDirections));
            ConvexHullHelper.CreateShape(points, pool, out data, out centre, out hull);
        }

        if (!part.Owned)
        {
            var corners = new Vector3[data.OriginalVertexMapping.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] = points[data.OriginalVertexMapping[i]];
            }

            _sharedHulls[key] = new SharedHull(corners, voxels);
        }

        data.Dispose(pool);
        return true;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Loose body {Body} cut off a character: {Models} model(s), a compound of {Hulls} hull(s), {Mass:F1} kg, centre at ({X:F2}, {Y:F2}, {Z:F2}) m"
    )]
    private partial void LogCut(int body, int models, int hulls, float mass, float x, float y, float z);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Ragdoll {Ragdoll}: {Bodies} bod(ies) joined by {Joints} joint(s), {Parts} part(s)"
    )]
    private partial void LogRagdoll(int ragdoll, int bodies, int joints, int parts);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Loose bodies: ragdoll {Ragdoll} thawed, {Joints} joint(s) put back"
    )]
    private partial void LogRejoined(int ragdoll, int joints);

    /// <summary>A shared part's tree, size, and voxels' edge in centimetres: what its hull depends on.</summary>
    private readonly record struct SharedPart(ChildState Tree, VoxelCoord Size, int CellCentimetres);

    /// <summary>
    /// The edge of one of <paramref name="part"/>'s model voxels, in centimetres: its model's unit at level 0 (1 cm for
    /// most, 5 cm for an object kind of 5 cm voxels), four times it at level 2; a part with no model (a test's, -1) is of
    /// 1 cm voxels.
    /// </summary>
    private int CellCentimetres(in LoosePart part) =>
        (part.Model >= 0 ? _models.UnitCentimetres(part.Model) : VoxelModels.Centimetres) << part.Level;

    /// <summary>The edge of one of <paramref name="part"/>'s model voxels, in metres (<see cref="CellCentimetres"/>).</summary>
    private float CellMetres(in LoosePart part) => CellCentimetres(in part) / (float)VoxelConstants.CentimetresPerMetre;

    /// <summary>A shared part's hull corners (from its model's corner, metres) and its blocking voxels.</summary>
    private sealed record SharedHull(Vector3[] Corners, long Voxels);
}
