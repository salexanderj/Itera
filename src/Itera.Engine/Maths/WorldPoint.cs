using System.Globalization;
using Itera.Engine.Voxels;

namespace Itera.Engine.Maths;

/// <summary>
/// A point of the world in metres from the map's middle, in doubles: where something is whatever the match's origin
/// (<see cref="WorldOrigin"/>). Positions the simulation and the renderer keep are local floats (<c>Vector3</c>, from the
/// origin); a point that crosses the origin (a start pose, <c>tp</c> and <c>pos</c>, a benchmark view, the play area, a
/// position in a message) is one of these, and turns local only through <see cref="WorldOrigin"/>. Doubles hold a
/// centimetre exactly to well past the edit format's 167 km.
/// </summary>
/// <param name="X">Metres along x.</param>
/// <param name="Y">Metres up.</param>
/// <param name="Z">Metres along z.</param>
public readonly record struct WorldPoint(double X, double Y, double Z)
{
    /// <summary>The map's middle.</summary>
    public static WorldPoint Zero => default;

    /// <summary>The point at <paramref name="centimetres"/>, exactly.</summary>
    public static WorldPoint FromCentimetres(VoxelCoord centimetres) =>
        new(
            centimetres.X / (double)VoxelConstants.CentimetresPerMetre,
            centimetres.Y / (double)VoxelConstants.CentimetresPerMetre,
            centimetres.Z / (double)VoxelConstants.CentimetresPerMetre
        );

    /// <summary>
    /// The whole centimetre that holds this point: its coordinates times 100, rounded down, a point less than
    /// <see cref="EdgeCentimetres"/> under a centimetre's edge counted on it. A centimetre written in metres is seldom
    /// exact in binary (1.13 m is 112.99999999999999 cm), and without that one in 14 would read back as the one below.
    /// </summary>
    public VoxelCoord ToCentimetres() => new(Centimetre(X), Centimetre(Y), Centimetre(Z));

    /// <summary>How far under a centimetre's edge, in centimetres, a point still counts as on it: 10 nm.</summary>
    public const double EdgeCentimetres = 1e-6;

    /// <summary>True when every coordinate is a finite number.</summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    /// <summary>The point moved by (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) metres.</summary>
    public WorldPoint Plus(double x, double y, double z) => new(X + x, Y + y, Z + z);

    /// <summary>The horizontal distance to <paramref name="other"/>, in metres.</summary>
    public double HorizontalDistanceTo(in WorldPoint other)
    {
        double dx = other.X - X,
            dz = other.Z - Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static int Centimetre(double metres)
    {
        var centimetres = metres * VoxelConstants.CentimetresPerMetre;
        var whole = Math.Floor(centimetres);
        return (int)(centimetres - whole >= 1 - EdgeCentimetres ? whole + 1 : whole);
    }

    /// <summary>The point as "x,y,z" in metres to the centimetre, the form a camera pose begins with.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X:0.##},{Y:0.##},{Z:0.##}");
}
