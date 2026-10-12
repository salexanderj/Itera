using System.Numerics;
using Itera.Engine.Maths;

namespace Itera.Engine.Objects;

/// <summary>
/// The turns of objects' headings (<see cref="ObjectPlacement.Heading"/>), the same on every peer: each heading's cosine
/// and sine from the deterministic series (<see cref="DeterministicMath.CosSin"/>) as integers scaled by
/// 2^<see cref="Shift"/>. A heading turns an object's own -z towards the world's +x: from its own axes to the world's,
/// (x, z) goes to (x cos - z sin, x sin + z cos). Copies in the world's voxels, edits, and collision turn through
/// <see cref="ToObject"/>; drawing through <see cref="Rotation"/>, the same angle in floats.
/// </summary>
public static class ObjectTurns
{
    /// <summary>The bits below the point of the scaled cosines and sines.</summary>
    public const int Shift = 16;

    private static readonly (int[] Cos, int[] Sin) s_table = Table();

    /// <summary>The scaled cosine of <paramref name="heading"/>.</summary>
    public static int Cos(int heading) => s_table.Cos[heading];

    /// <summary>The scaled sine of <paramref name="heading"/>.</summary>
    public static int Sin(int heading) => s_table.Sin[heading];

    /// <summary>
    /// The world offset (<paramref name="x"/>, <paramref name="z"/>) from an object's anchor, in centimetres, along its
    /// own axes at <paramref name="heading"/>: rounded down, in integers, so every peer gets the same.
    /// </summary>
    public static (long X, long Z) ToObject(int heading, long x, long z)
    {
        long cos = s_table.Cos[heading],
            sin = s_table.Sin[heading];
        return ((x * cos + z * sin) >> Shift, (z * cos - x * sin) >> Shift);
    }

    /// <summary>
    /// The offset (<paramref name="x"/>, <paramref name="z"/>) along an object's own axes at <paramref name="heading"/>, in
    /// centimetres, as a world offset from its anchor: rounded down, in integers, so every peer gets the same. Edits reach
    /// an object's voxels through it.
    /// </summary>
    public static (long X, long Z) FromObject(int heading, long x, long z)
    {
        long cos = s_table.Cos[heading],
            sin = s_table.Sin[heading];
        return ((x * cos - z * sin) >> Shift, (x * sin + z * cos) >> Shift);
    }

    /// <summary>The turn from an object's own axes to the world's at <paramref name="heading"/>, for drawing.</summary>
    public static Quaternion Rotation(int heading) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, -2 * MathF.PI * heading / ObjectPlacement.HeadingSteps);

    private static (int[] Cos, int[] Sin) Table()
    {
        var cosines = new int[ObjectPlacement.HeadingSteps];
        var sines = new int[ObjectPlacement.HeadingSteps];
        for (var heading = 0; heading < ObjectPlacement.HeadingSteps; heading++)
        {
            var (cos, sin) = DeterministicMath.CosSin(heading / (double)ObjectPlacement.HeadingSteps);
            cosines[heading] = (int)Math.Round(cos * (1 << Shift));
            sines[heading] = (int)Math.Round(sin * (1 << Shift));
        }

        return (cosines, sines);
    }
}
