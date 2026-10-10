using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Physics;

// Bodies of thrown balls and lifted pieces and their mirrors, and putting a body back into the grid where it rests.
public sealed partial class LooseBodies
{
    /// <summary>
    /// Makes a loose body of a ball of <paramref name="material"/> of <paramref name="radius"/> centimetres made from
    /// nothing (a thrown ball): the centimetres a sphere edit of that radius covers about its pivot's voxel
    /// (<see cref="VoxelModels.MakeBall"/>; each radius and material made once and shared), unturned, with its pivot (the
    /// low corner of its middle centimetre) at <paramref name="pivotMetres"/>, moving at <paramref name="velocity"/>. It
    /// takes nothing from the world and goes back as a sphere of its material filled into the air where it rests
    /// (<see cref="Settle"/>), or, without voxel terrain, becomes what the game decides (<see cref="ILooseBodyRest"/>).
    /// False when <see cref="MaxBodies"/> exist already. Throws as <see cref="VoxelModels.MakeBall"/>.
    /// </summary>
    public bool TryCreateBall(int radius, byte material, Vector3 pivotMetres, Vector3 velocity, out BodyHandle body) =>
        TryCreatePiece(
            Ball(radius, material),
            pivotMetres,
            Quaternion.Identity,
            velocity,
            false,
            radius,
            material,
            out body
        );

    /// <summary>
    /// Makes a mirror of a ball another game simulates (a session host's thrown ball, on a client): the same hull and
    /// model as <see cref="TryCreateBall"/>, kinematic as <see cref="TryCreateMirror(long, Vector3, Quaternion, out BodyHandle)"/>'s.
    /// </summary>
    public bool TryCreateBallMirror(
        int radius,
        byte material,
        Vector3 pivotMetres,
        Quaternion turn,
        out BodyHandle body
    ) => TryCreatePiece(Ball(radius, material), pivotMetres, turn, Vector3.Zero, true, radius, material, out body);

    /// <summary>
    /// Makes a loose body of the piece lifted by edit <paramref name="piece"/>, unturned, with the piece's pivot at
    /// <paramref name="pivotMetres"/>, moving at <paramref name="velocity"/>. False when <see cref="MaxBodies"/> exist
    /// already, no lift made the piece (always without voxel terrain), or the piece holds nothing that blocks.
    /// </summary>
    public bool TryCreate(long piece, Vector3 pivotMetres, Vector3 velocity, out BodyHandle body)
    {
        body = default;
        VoxelPiece voxels = default;
        return _voxels is not null
            && _voxels.TryGetPiece(piece, out voxels)
            && TryCreatePiece(voxels, pivotMetres, Quaternion.Identity, velocity, false, 0, 0, out body);
    }

    /// <summary>
    /// Makes a mirror of a body another game simulates: the same hull and model as
    /// <see cref="TryCreate(long, Vector3, Vector3, out BodyHandle)"/>, but a kinematic Bepu body that
    /// <see cref="MoveMirror"/> puts where the host says, every step. It pushes dynamic bodies and stand-ins, the
    /// character mover reads it as voxels (<see cref="CopyPoses"/>), and it is drawn like any body; it never rests,
    /// settles, or collides with the voxels (<see cref="VoxelColliders"/> skips kinematic bodies). A host's thrown ball
    /// has its own (<see cref="TryCreateBallMirror"/>).
    /// </summary>
    public bool TryCreateMirror(long piece, Vector3 pivotMetres, Quaternion turn, out BodyHandle body)
    {
        body = default;
        VoxelPiece voxels = default;
        return _voxels is not null
            && _voxels.TryGetPiece(piece, out voxels)
            && TryCreatePiece(voxels, pivotMetres, turn, Vector3.Zero, true, 0, 0, out body);
    }

    /// <summary>The ball of <paramref name="radius"/> and <paramref name="material"/>, made the first time it is asked for.</summary>
    private VoxelPiece Ball(int radius, byte material)
    {
        if (!_balls.TryGetValue((radius, material), out var ball))
        {
            ball = _models.MakeBall(radius, material);
            _balls.Add((radius, material), ball);
        }

        return ball;
    }

    /// <summary>
    /// The body of <paramref name="voxels"/>, a piece of centimetres: a lift's, or a ball's made from nothing when
    /// <paramref name="ballRadius"/> is more than 0 (with <paramref name="ballMaterial"/>, what it goes back as).
    /// </summary>
    private bool TryCreatePiece(
        in VoxelPiece voxels,
        Vector3 pivotMetres,
        Quaternion turn,
        Vector3 velocity,
        bool kinematic,
        int ballRadius,
        byte ballMaterial,
        out BodyHandle body
    )
    {
        body = default;
        // A mirror follows another game's body, which that game counts against its own limit: kept here past this
        // game's limit (a client's own limit would drop the host's bodies).
        if (!kinematic && _bodies.Count >= _maxBodies)
        {
            return false;
        }

        _boxes.Build(_models.Memory, in voxels, in _passable);
        if (_boxes.Count == 0)
        {
            return false;
        }

        // Bepu sees the convex hull of the blocking voxels: one convex shape sweeps and collides with the voxel boxes
        // around it cheaply and without sinking, where a compound of hundreds of 1 cm boxes did neither.
        var boxes = _boxes.Boxes;
        if (_points.Length < boxes.Length * 8)
        {
            _points = new Vector3[boxes.Length * 8];
        }

        var pivot = voxels.Pivot;
        var count = 0;
        var mass = 0f;
        // A piece holds centimetres, whatever the world's voxels: a lift reads it a centimetre at a time, and a ball is
        // made of them.
        const float voxelMetres = Metres;
        foreach (var box in boxes)
        {
            mass += box.Voxels * (voxelMetres * voxelMetres * voxelMetres) * DensityKilogramsPerCubicMetre;
            for (var corner = 0; corner < 8; corner++)
            {
                // Relative to the pivot, in metres.
                _points[count++] =
                    new Vector3(
                        ((corner & 1) != 0 ? box.X1 + 1 : box.X0) - pivot.X,
                        ((corner & 2) != 0 ? box.Y1 + 1 : box.Y0) - pivot.Y,
                        ((corner & 4) != 0 ? box.Z1 + 1 : box.Z0) - pivot.Z
                    ) * voxelMetres;
            }
        }

        if (count > MaxHullPoints)
        {
            count = SupportPoints(_points.AsSpan(0, count));
        }

        var simulation = _physics.Simulation;
        // The hull is centred on its own middle; `centre` is where that lies from the pivot.
        var hull = new ConvexHull(_points.AsSpan(0, count), _physics.Pool, out var centre);
        var shape = simulation.Shapes.Add(hull);

        var pose = new RigidPose(pivotMetres + Vector3.Transform(centre, turn), turn);
        var collidable = new CollidableDescription(shape, ContinuousDetection.Continuous(1e-3f, 1e-3f));
        body = simulation.Bodies.Add(
            kinematic
                // A negative sleep threshold keeps a mirror awake between the host's poses.
                ? BodyDescription.CreateKinematic(
                    pose,
                    new BodyVelocity(velocity),
                    collidable,
                    new BodyActivityDescription(-1)
                )
                : BodyDescription.CreateDynamic(
                    pose,
                    new BodyVelocity(velocity),
                    hull.ComputeInertia(mass),
                    collidable,
                    new BodyActivityDescription(0.01f)
                )
        );
        _physics.Kinds.Set(body, BodyKind.Loose);
        var model = _models.Add([voxels.Tree], voxels.Size);
        var boxMin = new Vector3(-pivot.X, -pivot.Y, -pivot.Z) * voxelMetres;
        var boxMax =
            new Vector3(voxels.Size.X - pivot.X, voxels.Size.Y - pivot.Y, voxels.Size.Z - pivot.Z) * voxelMetres;
        _bodies.Add(
            body,
            new Loose(
                voxels,
                -centre,
                shape,
                mass,
                model,
                [new LooseModel(model, boxMin, Quaternion.Identity)],
                [],
                false,
                0,
                0
            )
            {
                BoxMin = boxMin,
                BoxMax = boxMax,
                Born = ++_births,
                BallRadius = ballRadius,
                BallMaterial = ballMaterial,
            }
        );
        _pieces.Add(body);
        LogCreated(
            body.Value,
            voxels.Sequence,
            ballRadius,
            hull.FaceToVertexIndicesStart.Length,
            mass,
            pivotMetres.X,
            pivotMetres.Y,
            pivotMetres.Z
        );
        return true;
    }

    /// <summary>
    /// Puts a loose body back into the grid where it lies and removes it: its pivot is rounded to the nearest centimetre
    /// corner. A ball (<see cref="TryCreateBall"/>) goes back as a sphere of its material and radius about the
    /// centimetre there, filled into the air (<see cref="VoxelEditOperation.Fill"/>: on the world's coarser voxels, the
    /// share of each that the sphere covers, as any fill); a lifted piece as a piece edit, its orientation rounded to a
    /// packed turn, which fills only air. Its model is retired: drawn where the body lies, blended
    /// <paramref name="tint"/> towards its tint colour, until the world's drawn copy holds the edit
    /// (<see cref="VoxelModels.Retire"/>). Returns the edit's sequence. Throws without voxel terrain
    /// (<see cref="CanSettle"/>).
    /// </summary>
    public long Settle(BodyHandle body, float tint = 0)
    {
        var loose = _bodies[body];
        if (loose.Cut)
        {
            throw new InvalidOperationException("A body cut off a character never goes back into the grid.");
        }

        if (_voxels is null)
        {
            throw new InvalidOperationException("A match without voxel terrain has no grid to settle a body into.");
        }

        var pose = _physics.Simulation.Bodies[body].Pose;
        var local = pose.Position + Vector3.Transform(loose.PivotOffset, pose.Orientation);
        // The absolute centimetre nearest the pivot.
        var pivot = _origin.ToWorld(local);
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        var corner = new VoxelCoord(
            (int)Math.Round(pivot.X * perMetre),
            (int)Math.Round(pivot.Y * perMetre),
            (int)Math.Round(pivot.Z * perMetre)
        );
        long sequence;
        if (loose.BallRadius > 0)
        {
            sequence = _voxels.Apply(
                VoxelEdit.Sphere(corner, loose.BallRadius, VoxelEditOperation.Fill, loose.BallMaterial)
            );
            LogBallSettled(body.Value, loose.BallRadius, corner.X, corner.Y, corner.Z, sequence);
        }
        else
        {
            sequence = _voxels.Apply(
                VoxelEdit.PieceAt(loose.Piece, loose.Radius, corner, VoxelEdit.PackTurn(pose.Orientation))
            );
            LogSettled(body.Value, loose.Piece, corner.X, corner.Y, corner.Z, sequence);
        }

        _models.Retire(loose.Model, 0, ModelCorner(in loose, local, pose.Orientation), pose.Orientation, tint);
        RemoveBody(body, removeModel: false);
        return sequence;
    }

    /// <summary>
    /// What a rested body is, for a game's <see cref="ILooseBodyRest"/>: its model and where that lies, its pivot, its
    /// turn, and a ball's radius and material. Throws for a body cut off a character. Allocates nothing.
    /// </summary>
    public RestedBody Rested(BodyHandle body)
    {
        var loose = _bodies[body];
        if (loose.Cut)
        {
            throw new InvalidOperationException("A body cut off a character goes after resting; no game takes it.");
        }

        var pose = _physics.Simulation.Bodies[body].Pose;
        var pivot = pose.Position + Vector3.Transform(loose.PivotOffset, pose.Orientation);
        return new RestedBody(
            body,
            loose.Model,
            ModelCorner(in loose, pivot, pose.Orientation),
            pose.Orientation,
            pivot,
            loose.BallRadius,
            loose.BallMaterial,
            loose.Mass
        );
    }

    /// <summary>
    /// Removes a loose body and leaves its model to the game (<see cref="LooseBodyRestChoice.TakeOver"/>): the model
    /// stays until the game removes it (<see cref="VoxelModels.Remove"/>), drawn only where the game draws it. Throws for
    /// a body cut off a character, whose models it owns.
    /// </summary>
    public void Release(BodyHandle body)
    {
        if (_bodies[body].Cut)
        {
            throw new InvalidOperationException("A body cut off a character owns its models; remove it instead.");
        }

        RemoveBody(body, removeModel: false);
    }

    /// <summary>The corner of a body's piece's box in the world, in metres, from its pivot there and its turn.</summary>
    private static Vector3 ModelCorner(in Loose loose, Vector3 pivotMetres, Quaternion orientation)
    {
        var piecePivot = loose.Voxels.Pivot;
        return pivotMetres
            + Vector3.Transform(new Vector3(-piecePivot.X, -piecePivot.Y, -piecePivot.Z) * Metres, orientation);
    }

    /// <summary>
    /// Puts every thrown ball (and lifted piece) still loose back into the grid where it is (<see cref="Settle"/>), as if
    /// each had rested: before a save, so the saved world holds it (and a lifted piece's hole is not left with nothing to
    /// fill it); one frozen out of loaded range too. Bodies cut off characters are left alone. Only a game that owns its world saves, so there
    /// are no mirrors of a host's bodies. Returns how many went back: none without voxel terrain, whose save holds no
    /// bodies.
    /// </summary>
    public int SettleThrown()
    {
        if (_voxels is null)
        {
            return 0;
        }

        var thrown = new List<BodyHandle>();
        foreach (var (handle, loose) in _bodies)
        {
            if (!loose.Cut)
            {
                thrown.Add(handle);
            }
        }

        foreach (var handle in thrown)
        {
            Settle(handle);
        }

        return thrown.Count;
    }

    /// <summary>
    /// Puts the piece lifted by edit <paramref name="piece"/> straight back where it was lifted from, unturned: for a
    /// lift whose body could not be made. Returns the edit's sequence. Throws <see cref="ArgumentException"/> for a
    /// piece no lift made (it would put back an empty piece at the origin; without voxel terrain no lift made any).
    /// </summary>
    public long PutBack(long piece)
    {
        VoxelPiece voxels = default;
        if (_voxels is null || !_voxels.TryGetPiece(piece, out voxels))
        {
            throw new ArgumentException($"No lift made piece {piece}.", nameof(piece));
        }

        return _voxels.Apply(VoxelEdit.PieceAt(piece, voxels.Radius, voxels.Min + voxels.Pivot, VoxelEdit.NoTurn));
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Loose body {Body} made: piece {Piece}, ball radius {Ball} cm, a hull of {Faces} faces, {Mass:F1} kg, pivot at ({X:F2}, {Y:F2}, {Z:F2}) m"
    )]
    private partial void LogCreated(int body, long piece, int ball, int faces, float mass, float x, float y, float z);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Loose body {Body} settled: piece {Piece} put back at voxel ({X}, {Y}, {Z}), edit {Edit}"
    )]
    private partial void LogSettled(int body, long piece, int x, int y, int z, long edit);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Loose body {Body} settled: a ball of radius {Radius} cm filled about ({X}, {Y}, {Z}), edit {Edit}"
    )]
    private partial void LogBallSettled(int body, int radius, int x, int y, int z, long edit);
}
