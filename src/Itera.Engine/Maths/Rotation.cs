namespace Itera.Engine.Maths;

/// <summary>
/// A rotation as a 3x3 matrix of doubles, rows by columns (<c>Mxy</c> is row x, column y), applied as <c>M v</c>. Built
/// from turns through <see cref="DeterministicMath.CosSin"/> and combined with scalar + - * / only, so a body turned the
/// same way fills the same voxels on every machine; <c>System.Numerics</c> matrices may fuse a multiply and an add on
/// one machine and not on another.
/// </summary>
public readonly record struct Rotation(
    double Mxx,
    double Mxy,
    double Mxz,
    double Myx,
    double Myy,
    double Myz,
    double Mzx,
    double Mzy,
    double Mzz
)
{
    /// <summary>No rotation.</summary>
    public static Rotation Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>
    /// <paramref name="turns"/> of a full turn about the x axis: a positive turn takes +y towards +z, so a limb that hangs
    /// down (-y) swings forward (-z).
    /// </summary>
    public static Rotation AboutX(double turns)
    {
        var (cos, sin) = DeterministicMath.CosSin(turns);
        return new(1, 0, 0, 0, cos, -sin, 0, sin, cos);
    }

    /// <summary><paramref name="turns"/> of a full turn about the y axis: a positive turn takes +z towards +x.</summary>
    public static Rotation AboutY(double turns)
    {
        var (cos, sin) = DeterministicMath.CosSin(turns);
        return new(cos, 0, sin, 0, 1, 0, -sin, 0, cos);
    }

    /// <summary>
    /// <paramref name="turns"/> of a full turn about the z axis: a positive turn takes +x towards +y, so a limb that hangs
    /// down (-y) swings towards +x.
    /// </summary>
    public static Rotation AboutZ(double turns)
    {
        var (cos, sin) = DeterministicMath.CosSin(turns);
        return new(cos, -sin, 0, sin, cos, 0, 0, 0, 1);
    }

    /// <summary>
    /// The rotation about the y axis that turns the front of a body (-z) to face horizontal direction
    /// (<paramref name="x"/>, <paramref name="z"/>), which need not have unit length.
    /// </summary>
    public static Rotation Facing(double x, double z)
    {
        var length = Math.Sqrt(x * x + z * z);
        var sin = -x / length;
        var cos = -z / length;
        return new(cos, 0, sin, 0, 1, 0, -sin, 0, cos);
    }

    /// <summary>
    /// The rotation whose columns are a frame: +x along (<paramref name="ax"/>, <paramref name="ay"/>,
    /// <paramref name="az"/>), +z towards (<paramref name="nx"/>, <paramref name="ny"/>, <paramref name="nz"/>) as near as
    /// it can at right angles to +x, and +y at right angles to both. Neither vector needs unit length, but they must not
    /// be parallel. Lays a flat shape along a direction with its thin side along a surface's normal.
    /// </summary>
    public static Rotation Frame(double ax, double ay, double az, double nx, double ny, double nz)
    {
        var length = Math.Sqrt(ax * ax + ay * ay + az * az);
        var (xx, xy, xz) = (ax / length, ay / length, az / length);
        var along = nx * xx + ny * xy + nz * xz;
        var (zx, zy, zz) = (nx - along * xx, ny - along * xy, nz - along * xz);
        length = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        (zx, zy, zz) = (zx / length, zy / length, zz / length);

        // y = z cross x, so the frame turns like the axes it stands for.
        var (yx, yy, yz) = (zy * xz - zz * xy, zz * xx - zx * xz, zx * xy - zy * xx);
        return new(xx, yx, zx, xy, yy, zy, xz, yz, zz);
    }

    /// <summary>This rotation after <paramref name="first"/>: <c>(this * first) v = this (first v)</c>.</summary>
    public Rotation Times(in Rotation first) =>
        new(
            Mxx * first.Mxx + Mxy * first.Myx + Mxz * first.Mzx,
            Mxx * first.Mxy + Mxy * first.Myy + Mxz * first.Mzy,
            Mxx * first.Mxz + Mxy * first.Myz + Mxz * first.Mzz,
            Myx * first.Mxx + Myy * first.Myx + Myz * first.Mzx,
            Myx * first.Mxy + Myy * first.Myy + Myz * first.Mzy,
            Myx * first.Mxz + Myy * first.Myz + Myz * first.Mzz,
            Mzx * first.Mxx + Mzy * first.Myx + Mzz * first.Mzx,
            Mzx * first.Mxy + Mzy * first.Myy + Mzz * first.Mzy,
            Mzx * first.Mxz + Mzy * first.Myz + Mzz * first.Mzz
        );

    /// <summary>The inverse of this rotation, which is its transpose.</summary>
    public Rotation Transposed() => new(Mxx, Myx, Mzx, Mxy, Myy, Mzy, Mxz, Myz, Mzz);

    /// <summary>The vector (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) rotated.</summary>
    public (double X, double Y, double Z) Apply(double x, double y, double z) =>
        (Mxx * x + Mxy * y + Mxz * z, Myx * x + Myy * y + Myz * z, Mzx * x + Mzy * y + Mzz * z);

    /// <summary>
    /// The mirror image of this rotation in the plane x = 0, for the other side of a body: the same swing forward and
    /// back, the opposite swing sideways and twist.
    /// </summary>
    public Rotation Mirrored() => new(Mxx, -Mxy, -Mxz, -Myx, Myy, Myz, -Mzx, Mzy, Mzz);

    /// <summary>
    /// The turn <paramref name="t"/> of the way from <paramref name="from"/> (0) to <paramref name="to"/> (1): their
    /// quaternions blended in a straight line and made unit length again, which is close to an even turn for the turns
    /// of a pose. Scalar + - * / and a square root only.
    /// </summary>
    public static Rotation Blend(in Rotation from, in Rotation to, double t)
    {
        var (aw, ax, ay, az) = from.Quaternion();
        var (bw, bx, by, bz) = to.Quaternion();
        // The shorter way round: q and -q are the same turn.
        var sign = aw * bw + ax * bx + ay * by + az * bz < 0 ? -1.0 : 1.0;
        var w = aw + (sign * bw - aw) * t;
        var x = ax + (sign * bx - ax) * t;
        var y = ay + (sign * by - ay) * t;
        var z = az + (sign * bz - az) * t;
        var length = Math.Sqrt(w * w + x * x + y * y + z * z);
        return FromQuaternion(w / length, x / length, y / length, z / length);
    }

    /// <summary>
    /// The rotation as a <see cref="System.Numerics.Quaternion"/>, for the renderer: <c>Vector3.Transform(v, q)</c>
    /// gives <see cref="Apply"/>'s result, in single precision.
    /// </summary>
    public System.Numerics.Quaternion ToQuaternion()
    {
        var (w, x, y, z) = Quaternion();
        return new System.Numerics.Quaternion((float)x, (float)y, (float)z, (float)w);
    }

    /// <summary>
    /// The unit quaternion (w, x, y, z) of the rotation, by its largest component (Shepperd's method).
    /// </summary>
    private (double W, double X, double Y, double Z) Quaternion()
    {
        var trace = Mxx + Myy + Mzz;
        if (trace > 0)
        {
            var s = 2 * Math.Sqrt(1 + trace);
            return (s / 4, (Mzy - Myz) / s, (Mxz - Mzx) / s, (Myx - Mxy) / s);
        }

        if (Mxx >= Myy && Mxx >= Mzz)
        {
            var s = 2 * Math.Sqrt(1 + Mxx - Myy - Mzz);
            return ((Mzy - Myz) / s, s / 4, (Mxy + Myx) / s, (Mxz + Mzx) / s);
        }

        if (Myy >= Mzz)
        {
            var s = 2 * Math.Sqrt(1 + Myy - Mxx - Mzz);
            return ((Mxz - Mzx) / s, (Mxy + Myx) / s, s / 4, (Myz + Mzy) / s);
        }

        var q = 2 * Math.Sqrt(1 + Mzz - Mxx - Myy);
        return ((Myx - Mxy) / q, (Mxz + Mzx) / q, (Myz + Mzy) / q, q / 4);
    }

    /// <summary>The rotation of the unit quaternion (w, x, y, z).</summary>
    private static Rotation FromQuaternion(double w, double x, double y, double z) =>
        new(
            1 - 2 * (y * y + z * z),
            2 * (x * y - z * w),
            2 * (x * z + y * w),
            2 * (x * y + z * w),
            1 - 2 * (x * x + z * z),
            2 * (y * z - x * w),
            2 * (x * z - y * w),
            2 * (y * z + x * w),
            1 - 2 * (x * x + y * y)
        );
}

/// <summary>
/// A rotation followed by a move: a point p goes to <c>R p + (X, Y, Z)</c>. Places a body part's bone space in its
/// body, and a body in the world, in the caller's unit (centimetres for voxel models).
/// </summary>
public readonly record struct RigidTransform(Rotation Rotation, double X, double Y, double Z)
{
    /// <summary>No change.</summary>
    public static RigidTransform Identity => new(Rotation.Identity, 0, 0, 0);

    /// <summary>A move with no rotation.</summary>
    public static RigidTransform Move(double x, double y, double z) => new(Rotation.Identity, x, y, z);

    /// <summary>The point (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) transformed.</summary>
    public (double X, double Y, double Z) Apply(double x, double y, double z)
    {
        var (rx, ry, rz) = Rotation.Apply(x, y, z);
        return (rx + X, ry + Y, rz + Z);
    }

    /// <summary>This transform after <paramref name="first"/>: first <paramref name="first"/>, then this.</summary>
    public RigidTransform Times(in RigidTransform first)
    {
        var (x, y, z) = Apply(first.X, first.Y, first.Z);
        return new(Rotation.Times(first.Rotation), x, y, z);
    }

    /// <summary>The transform that undoes this one: the rotation transposed, then the move turned back.</summary>
    public RigidTransform Inverse()
    {
        var back = Rotation.Transposed();
        var (x, y, z) = back.Apply(-X, -Y, -Z);
        return new(back, x, y, z);
    }
}
