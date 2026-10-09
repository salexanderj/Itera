namespace Itera.Engine.Maths;

/// <summary>
/// Maths that gives the same bits on every machine: integer hashing, seeded draws, gradient noise, exact integer roots,
/// and series trigonometry. World generation and anything else every peer must compute alike from a seed (voxels, posed
/// bodies) use it, so only integer operations and scalar double <c>+ - * /</c> and <see cref="Math.Sqrt(double)"/> are
/// used. No <see cref="Math.Sin(double)"/> or <c>Pow</c> (they come from each platform's maths library), and no
/// <c>System.Numerics</c> vector helpers: in .NET 10 <c>Vector2.Lerp</c>, <c>Reflect</c>, and <c>Transform</c> use
/// <c>MultiplyAddEstimate</c>, which fuses the multiply and add on some machines and not on others.
/// </summary>
public static class DeterministicMath
{
    private const ulong Golden = 0x9E3779B97F4A7C15UL;
    private const double Diagonal = 0.70710678118654752;
    private const double Sqrt2 = 1.4142135623730951;

    // The 16 compass directions: cos and sin of multiples of 22.5 degrees.
    private static readonly double[] s_compassX =
    [
        1,
        0.92387953251128674,
        0.70710678118654752,
        0.38268343236508977,
        0,
        -0.38268343236508977,
        -0.70710678118654752,
        -0.92387953251128674,
        -1,
        -0.92387953251128674,
        -0.70710678118654752,
        -0.38268343236508977,
        0,
        0.38268343236508977,
        0.70710678118654752,
        0.92387953251128674,
    ];

    private static readonly double[] s_compassZ =
    [
        0,
        0.38268343236508977,
        0.70710678118654752,
        0.92387953251128674,
        1,
        0.92387953251128674,
        0.70710678118654752,
        0.38268343236508977,
        0,
        -0.38268343236508977,
        -0.70710678118654752,
        -0.92387953251128674,
        -1,
        -0.92387953251128674,
        -0.70710678118654752,
        -0.38268343236508977,
    ];

    /// <summary>The SplitMix64 finaliser.</summary>
    public static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>A hash of a lattice point, the seed, and a salt that names what the hash is for.</summary>
    public static ulong Hash(int x, int z, ulong seed, ulong salt) =>
        Mix(seed ^ Mix(((ulong)(uint)x) | ((ulong)(uint)z << 32)) ^ (salt * Golden));

    /// <summary>A hash of the seed and a salt, for choices made once per world.</summary>
    public static ulong Hash(ulong seed, ulong salt) => Mix(seed ^ Mix(salt * Golden));

    /// <summary>The next value of a SplitMix64 sequence, for drawing several values from one hash.</summary>
    public static ulong Next(ref ulong state)
    {
        state += Golden;
        return Mix(state);
    }

    /// <summary>A double in [0, 1) from the top 53 bits of <paramref name="hash"/>.</summary>
    public static double Unit(ulong hash) => (hash >> 11) * (1.0 / (1UL << 53));

    /// <summary>An integer in [<paramref name="min"/>, <paramref name="max"/>] from <paramref name="hash"/>.</summary>
    public static int Range(ulong hash, int min, int max) => min + (int)(hash % (ulong)(max - min + 1));

    /// <summary>
    /// The next double in [0, 1) of the sequence <paramref name="state"/> holds (<see cref="Next"/>, <see cref="Unit"/>).
    /// </summary>
    public static double NextUnit(ref ulong state) => Unit(Next(ref state));

    /// <summary>The next double of the sequence between <paramref name="min"/> and <paramref name="max"/>.</summary>
    public static double NextBetween(ref ulong state, double min, double max) =>
        min + (max - min) * NextUnit(ref state);

    /// <summary>The next integer of the sequence in [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static int NextRange(ref ulong state, int min, int max) => Range(Next(ref state), min, max);

    /// <summary>
    /// The first and last cells of edge <paramref name="cell"/> along one axis whose things, reaching up to
    /// <paramref name="reach"/> from their cell, may touch [<paramref name="min"/>, <paramref name="max"/>].
    /// </summary>
    public static void CellRange(int cell, int reach, int min, int max, out int first, out int last)
    {
        first = FloorDivide(min - reach, cell);
        last = FloorDivide(max + reach, cell);
    }

    /// <summary>Division rounded towards negative infinity.</summary>
    public static int FloorDivide(int value, int divisor)
    {
        var quotient = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }

    /// <summary>Division rounded towards negative infinity.</summary>
    public static long FloorDivide(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }

    /// <summary>Largest integer whose square is at most <paramref name="value"/> (0 for negative values).</summary>
    public static int SquareRoot(long value)
    {
        if (value <= 0)
        {
            return 0;
        }

        var root = (long)Math.Sqrt(value);
        while (root * root > value)
        {
            root--;
        }

        while ((root + 1) * (root + 1) <= value)
        {
            root++;
        }

        return (int)root;
    }

    /// <summary>The quintic fade 6t^5 - 15t^4 + 10t^3: 0 at 0, 1 at 1, flat at both ends up to the second derivative.</summary>
    public static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    /// <summary>0 below <paramref name="edge0"/>, 1 above <paramref name="edge1"/>, a cubic step between.</summary>
    public static double SmoothStep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// 2D gradient noise on a lattice of <paramref name="cellSize"/> units (centimetres in world generation), about -1 to
    /// 1 and 0 at every lattice point. Eight gradient directions (the axes and the diagonals), quintic fade.
    /// </summary>
    public static double GradientNoise(int x, int z, int cellSize, ulong seed, ulong salt)
    {
        var cellX = FloorDivide(x, cellSize);
        var cellZ = FloorDivide(z, cellSize);
        var fx = (x - cellX * cellSize) / (double)cellSize;
        var fz = (z - cellZ * cellSize) / (double)cellSize;
        var n00 = Gradient(Hash(cellX, cellZ, seed, salt), fx, fz);
        var n10 = Gradient(Hash(cellX + 1, cellZ, seed, salt), fx - 1, fz);
        var n01 = Gradient(Hash(cellX, cellZ + 1, seed, salt), fx, fz - 1);
        var n11 = Gradient(Hash(cellX + 1, cellZ + 1, seed, salt), fx - 1, fz - 1);
        var u = Fade(fx);
        var bottom = n00 + (n10 - n00) * u;
        var top = n01 + (n11 - n01) * u;
        return (bottom + (top - bottom) * Fade(fz)) * Sqrt2;
    }

    /// <summary>1D gradient noise on a lattice of <paramref name="cellSize"/> units, about -1 to 1.</summary>
    public static double GradientNoise(int x, int cellSize, ulong seed, ulong salt)
    {
        var cell = FloorDivide(x, cellSize);
        var f = (x - cell * cellSize) / (double)cellSize;
        var g0 = Unit(Hash(cell, 0, seed, salt)) * 2 - 1;
        var g1 = Unit(Hash(cell + 1, 0, seed, salt)) * 2 - 1;
        var a = g0 * f;
        var b = g1 * (f - 1);
        return (a + (b - a) * Fade(f)) * 2;
    }

    /// <summary>
    /// A horizontal unit direction: compass direction <paramref name="index"/> of 16 (counted from +x towards +z, any
    /// integer), turned towards the next by <paramref name="turn"/> (-1 to 1) of about 11 degrees. Constants and sqrt only.
    /// </summary>
    public static (double X, double Z) Direction(int index, double turn)
    {
        var i = index & 15;
        var x = s_compassX[i] - s_compassZ[i] * 0.2 * turn;
        var z = s_compassZ[i] + s_compassX[i] * 0.2 * turn;
        var length = Math.Sqrt(x * x + z * z);
        return (x / length, z / length);
    }

    /// <summary>
    /// The direction of (<paramref name="x"/>, <paramref name="z"/>) as a share of a full turn, 0 to 1, counted from +x
    /// towards +z: <c>atan2(z, x) / 2 pi</c> within about 1e-10 of a turn. The angle is brought into the first octant,
    /// halved twice (<c>atan t = 2 atan(t / (1 + sqrt(1 + t^2)))</c>), and summed from its series.
    /// </summary>
    public static double Turns(double x, double z)
    {
        var alongX = Math.Abs(x);
        var alongZ = Math.Abs(z);
        var larger = Math.Max(alongX, alongZ);
        if (larger == 0)
        {
            return 0;
        }

        var t = Math.Min(alongX, alongZ) / larger;
        t /= 1 + Math.Sqrt(1 + t * t);
        t /= 1 + Math.Sqrt(1 + t * t);
        var squared = t * t;
        var angle =
            4
            * t
            * (
                1 - squared * (1.0 / 3 - squared * (1.0 / 5 - squared * (1.0 / 7 - squared * (1.0 / 9 - squared / 11))))
            );
        if (alongZ > alongX)
        {
            angle = Math.PI / 2 - angle;
        }

        if (x < 0)
        {
            angle = Math.PI - angle;
        }

        if (z < 0)
        {
            angle = 2 * Math.PI - angle;
        }

        var turns = angle / (2 * Math.PI);
        return turns < 1 ? turns : 0;
    }

    /// <summary>
    /// The cosine and sine of <paramref name="turns"/> of a full turn, within about 1e-12: the angle is brought within a
    /// quarter turn and both are summed from their series.
    /// </summary>
    public static (double Cos, double Sin) CosSin(double turns)
    {
        var quarters = turns * 4;
        var quarter = Math.Floor(quarters + 0.5);
        var angle = (quarters - quarter) * (Math.PI / 2);
        var squared = angle * angle;
        var sin =
            angle
            * (
                1
                - squared
                    / 6
                    * (
                        1
                        - squared
                            / 20
                            * (1 - squared / 42 * (1 - squared / 72 * (1 - squared / 110 * (1 - squared / 156))))
                    )
            );
        var cos =
            1
            - squared
                / 2
                * (
                    1
                    - squared / 12 * (1 - squared / 30 * (1 - squared / 56 * (1 - squared / 90 * (1 - squared / 132))))
                );
        return ((long)quarter & 3) switch
        {
            0 => (cos, sin),
            1 => (-sin, cos),
            2 => (-cos, -sin),
            _ => (sin, -cos),
        };
    }

    /// <summary>The dot product of one of eight gradients, picked by <paramref name="hash"/>, with (x, z).</summary>
    private static double Gradient(ulong hash, double x, double z) =>
        (hash & 7) switch
        {
            0 => x,
            1 => -x,
            2 => z,
            3 => -z,
            4 => (x + z) * Diagonal,
            5 => (z - x) * Diagonal,
            6 => (x - z) * Diagonal,
            _ => -(x + z) * Diagonal,
        };
}
