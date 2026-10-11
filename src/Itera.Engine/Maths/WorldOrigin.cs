using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Maths;

/// <summary>
/// The match's origin: the absolute point, in whole metres, that its local positions are measured from. The simulation
/// and the renderer keep positions as local floats (<c>Vector3</c> metres from here), so they stay near zero and precise
/// anywhere on a large map once the origin follows the camera; voxels, edits, saves, and what the engine sends stay in
/// absolute centimetres (<see cref="VoxelCoord"/>), and points that cross the origin are <see cref="WorldPoint"/>s. Every
/// conversion between the two goes through here, in doubles: a local float becomes whole centimetres only as
/// floor((local + origin) * 100) (<see cref="WorldPoint.ToCentimetres"/>), and voxel cells as floor((local + origin) *
/// cells a metre). Scoped to the match (every service of a match shares one); a service made without one
/// (a test) measures from the map's middle. Main thread.
/// </summary>
public sealed class WorldOrigin
{
    /// <summary>Where the origin lies, in absolute centimetres: always whole metres.</summary>
    public VoxelCoord Centimetres { get; private set; }

    /// <summary>Where the origin lies, as an absolute point.</summary>
    public WorldPoint Metres => WorldPoint.FromCentimetres(Centimetres);

    /// <summary>How many times the origin has moved this match.</summary>
    public long Moves { get; private set; }

    /// <summary>The farthest the origin may lie from the map's middle on any axis, in metres: the edit format's reach.</summary>
    public const double MostMetres = 167_772.16;

    /// <summary>
    /// Puts the origin on the whole metres nearest <paramref name="start"/>, where the match starts
    /// (<see cref="IOriginStart"/>): a match's first origin, set at its "origin" step, before anything keeps a local
    /// position; it counts as no move.
    /// </summary>
    /// <exception cref="ArgumentException">The point is not finite or lies past <see cref="MostMetres"/>.</exception>
    /// <exception cref="InvalidOperationException">The origin has moved already.</exception>
    internal void StartAt(in WorldPoint start)
    {
        if (
            !start.IsFinite
            || Math.Abs(start.X) > MostMetres
            || Math.Abs(start.Y) > MostMetres
            || Math.Abs(start.Z) > MostMetres
        )
        {
            throw new ArgumentException($"A match cannot start its origin at {start}.", nameof(start));
        }

        if (Moves > 0)
        {
            throw new InvalidOperationException("The origin starts only before it has moved.");
        }

        Centimetres = WholeMetres(start);
    }

    /// <summary>The whole metres nearest <paramref name="point"/> (halves away from zero), in centimetres.</summary>
    internal static VoxelCoord WholeMetres(in WorldPoint point) => new(Whole(point.X), Whole(point.Y), Whole(point.Z));

    private static int Whole(double metres) =>
        (int)Math.Round(metres, MidpointRounding.AwayFromZero) * VoxelConstants.CentimetresPerMetre;

    /// <summary>The local position of the absolute point <paramref name="point"/>.</summary>
    public Vector3 ToLocal(in WorldPoint point)
    {
        var origin = Metres;
        return new((float)(point.X - origin.X), (float)(point.Y - origin.Y), (float)(point.Z - origin.Z));
    }

    /// <summary>The local position of the absolute centimetre <paramref name="centimetres"/>, from integers.</summary>
    public Vector3 ToLocal(VoxelCoord centimetres)
    {
        var o = Centimetres;
        const float perMetre = VoxelConstants.CentimetresPerMetre;
        return new(
            (float)(((long)centimetres.X - o.X) / (double)perMetre),
            (float)(((long)centimetres.Y - o.Y) / (double)perMetre),
            (float)(((long)centimetres.Z - o.Z) / (double)perMetre)
        );
    }

    /// <summary>The absolute point of the local position <paramref name="local"/>.</summary>
    public WorldPoint ToWorld(Vector3 local)
    {
        var origin = Metres;
        return new(origin.X + local.X, origin.Y + local.Y, origin.Z + local.Z);
    }

    /// <summary>The absolute x of the local x <paramref name="local"/>, in metres.</summary>
    public double WorldX(float local) => Centimetres.X / (double)VoxelConstants.CentimetresPerMetre + local;

    /// <summary>The absolute y of the local y <paramref name="local"/>, in metres.</summary>
    public double WorldY(float local) => Centimetres.Y / (double)VoxelConstants.CentimetresPerMetre + local;

    /// <summary>The absolute z of the local z <paramref name="local"/>, in metres.</summary>
    public double WorldZ(float local) => Centimetres.Z / (double)VoxelConstants.CentimetresPerMetre + local;

    /// <summary>The whole absolute centimetre that holds the local position <paramref name="local"/>.</summary>
    public VoxelCoord ToCentimetres(Vector3 local) => ToWorld(local).ToCentimetres();

    /// <summary>
    /// Puts the origin at <paramref name="centimetres"/> (whole metres) without moving anything that keeps local
    /// positions; tests use this to measure from another point.
    /// </summary>
    /// <exception cref="ArgumentException">A coordinate is not whole metres.</exception>
    internal void MoveTo(VoxelCoord centimetres)
    {
        const int perMetre = VoxelConstants.CentimetresPerMetre;
        if (centimetres.X % perMetre != 0 || centimetres.Y % perMetre != 0 || centimetres.Z % perMetre != 0)
        {
            throw new ArgumentException($"The origin lies on whole metres; {centimetres} is not.", nameof(centimetres));
        }

        Centimetres = centimetres;
        Moves++;
    }
}
