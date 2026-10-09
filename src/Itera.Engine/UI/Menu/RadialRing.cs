using System.Numerics;
using SkiaSharp;

namespace Itera.Engine.UI.Menu;

/// <summary>
/// The shape of a radial menu's buttons (<see cref="RadialMenu"/>) at any centre and size: the ring's
/// <see cref="RadialMenu.RingSlots"/> curved buttons, slot 0 at the top and the rest clockwise, and the middle circle's
/// three buttons (its top half, and its lower half split into two quarters), as paths and as a hit test. Its sizes are the menu's shares (<see cref="RadialMenu.CircleShare"/>,
/// <see cref="RadialMenu.RingInnerShare"/>, <see cref="RadialMenu.RingOuterShare"/>, <see cref="RadialMenu.GapShare"/>)
/// of <c>scale</c> pixels: <see cref="RadialMenu.ScaleShare"/> of the frame's height for the menu in the middle of the
/// screen, less for a smaller copy (a game's panel can show one for its ring's slots). Main thread (Skia's paths).
/// </summary>
public static class RadialRing
{
    /// <summary>How far the middle buttons' icons sit from the middle along each axis, as a share of the circle's radius.</summary>
    private const float MiddleIconInset = 0.42f;

    /// <summary>
    /// The slot at <paramref name="position"/> on a ring centred on <paramref name="centre"/> at <paramref name="scale"/>,
    /// or -1: one of the middle circle's three buttons, a button of the ring by its angle, or nothing in the gaps (on the
    /// lines between the middle's buttons too), between the circle and the ring, or past the ring.
    /// </summary>
    public static int Hit(Vector2 position, Vector2 centre, float scale)
    {
        var offset = position - centre;
        var radius = offset.Length();
        var halfGap = RadialMenu.GapShare * scale / 2;
        if (radius <= RadialMenu.CircleShare * scale)
        {
            // The top half above the line across the middle; under it, the two quarters either side of the line down.
            if (MathF.Abs(offset.Y) <= halfGap)
            {
                return -1;
            }

            if (offset.Y < 0)
            {
                return RadialMenu.Top;
            }

            return MathF.Abs(offset.X) <= halfGap ? -1
                : offset.X < 0 ? RadialMenu.LowerLeft
                : RadialMenu.LowerRight;
        }

        if (radius < RadialMenu.RingInnerShare * scale || radius > RadialMenu.RingOuterShare * scale)
        {
            return -1;
        }

        // Clockwise from straight up, screen y down; each button is 30 degrees wide, centred on its slot's hour (slot 0's
        // at the top).
        var angle = MathF.Atan2(offset.X, -offset.Y);
        var step = 2 * MathF.PI / RadialMenu.RingSlots;
        var hour = (int)MathF.Round(angle / step);
        var fromMiddle = MathF.Abs(angle - hour * step);
        if (radius * MathF.Sin(step / 2 - fromMiddle) < halfGap)
        {
            return -1;
        }

        return ((hour % RadialMenu.RingSlots) + RadialMenu.RingSlots) % RadialMenu.RingSlots;
    }

    /// <summary>The pixels a ring centred on <paramref name="centre"/> at <paramref name="scale"/> may cover, its outline included.</summary>
    public static SKRect Bounds(Vector2 centre, float scale)
    {
        var reach = RadialMenu.RingOuterShare * scale + 2 * RadialMenu.OutlineShare * scale + 2;
        return new SKRect(centre.X - reach, centre.Y - reach, centre.X + reach, centre.Y + reach);
    }

    /// <summary>The point <paramref name="distance"/> from <paramref name="centre"/> towards ring slot <paramref name="slot"/>'s hour.</summary>
    public static Vector2 Toward(Vector2 centre, int slot, float distance)
    {
        var angle = slot * 2 * MathF.PI / RadialMenu.RingSlots;
        return centre + distance * new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
    }

    /// <summary>Where ring slot <paramref name="slot"/>'s number is centred, near the ring's outer edge.</summary>
    public static Vector2 NumberAt(Vector2 centre, int slot, float scale) =>
        Toward(centre, slot, (RadialMenu.RingOuterShare - RadialMenu.NumberInsetShare) * scale);

    /// <summary>
    /// Where slot <paramref name="slot"/>'s icon is centred: a ring button's between its number and the ring's inner
    /// edge, the top half's at its middle (a half disc's centroid is 0.42 of its radius from the line), and a quarter's on
    /// its diagonal at its centroid.
    /// </summary>
    public static Vector2 IconAt(Vector2 centre, int slot, float scale)
    {
        if (slot < RadialMenu.RingSlots)
        {
            var middle = (RadialMenu.RingInnerShare + RadialMenu.RingOuterShare) / 2 - RadialMenu.NumberInsetShare / 2;
            return Toward(centre, slot, middle * scale);
        }

        var inset = MiddleIconInset * RadialMenu.CircleShare * scale;
        return slot switch
        {
            RadialMenu.Top => new Vector2(centre.X, centre.Y - inset),
            RadialMenu.LowerLeft => new Vector2(centre.X - inset, centre.Y + inset),
            _ => new Vector2(centre.X + inset, centre.Y + inset),
        };
    }

    /// <summary>How many pixels across slot <paramref name="slot"/>'s icon is drawn.</summary>
    public static float IconSize(int slot, float scale) =>
        slot < RadialMenu.RingSlots ? 0.07f * scale
        : slot == RadialMenu.Top ? 0.7f * RadialMenu.CircleShare * scale
        : 0.56f * RadialMenu.CircleShare * scale;

    /// <summary>
    /// Fills <paramref name="paths"/> (<see cref="RadialMenu.Slots"/> long) with the buttons' outlines for a ring centred
    /// on <paramref name="centre"/> at <paramref name="scale"/>, disposing the paths it held.
    /// </summary>
    public static void Build(SKPath?[] paths, Vector2 centre, float scale)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfLessThan(paths.Length, RadialMenu.Slots);
        Free(paths);
        var halfGap = RadialMenu.GapShare * scale / 2;
        var inner = RadialMenu.RingInnerShare * scale;
        var outer = RadialMenu.RingOuterShare * scale;
        var circle = RadialMenu.CircleShare * scale;
        for (var slot = 0; slot < RadialMenu.RingSlots; slot++)
        {
            // Skia measures angles in degrees clockwise from +x; the slot's hour is clockwise from straight up. Each
            // edge keeps half the gap from the line between two buttons, at both radii, so the gap's sides are parallel.
            var middle = slot * 30f - 90;
            var outerHalf = 15 - Degrees(MathF.Asin(halfGap / outer));
            var innerHalf = 15 - Degrees(MathF.Asin(halfGap / inner));
            using var path = new SKPathBuilder();
            path.ArcTo(Square(centre, outer), middle - outerHalf, 2 * outerHalf, true);
            path.ArcTo(Square(centre, inner), middle + innerHalf, -2 * innerHalf, false);
            path.Close();
            paths[slot] = path.Detach();
        }

        // The middle: the top half closed by a chord half the gap above the line across the middle, and the two lower
        // quarters, each an arc and the corner kept half the gap from both lines.
        var square = Square(centre, circle);
        var tilt = Degrees(MathF.Asin(halfGap / circle));
        paths[RadialMenu.Top] = Part(square, 180 + tilt, 180 - 2 * tilt, null);
        paths[RadialMenu.LowerLeft] = Part(
            square,
            90 + tilt,
            90 - 2 * tilt,
            new SKPoint(centre.X - halfGap, centre.Y + halfGap)
        );
        paths[RadialMenu.LowerRight] = Part(
            square,
            tilt,
            90 - 2 * tilt,
            new SKPoint(centre.X + halfGap, centre.Y + halfGap)
        );
    }

    /// <summary>Disposes and clears every path in <paramref name="paths"/>.</summary>
    public static void Free(SKPath?[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        for (var i = 0; i < paths.Length; i++)
        {
            paths[i]?.Dispose();
            paths[i] = null;
        }
    }

    /// <summary>
    /// The part of a circle in <paramref name="square"/> an arc bounds, closed straight back to its start, or through
    /// <paramref name="corner"/> when one is given.
    /// </summary>
    private static SKPath Part(SKRect square, float start, float sweep, SKPoint? corner)
    {
        using var path = new SKPathBuilder();
        path.ArcTo(square, start, sweep, true);
        if (corner is { } at)
        {
            path.LineTo(at);
        }

        path.Close();
        return path.Detach();
    }

    private static SKRect Square(Vector2 centre, float radius) =>
        new(centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius);

    private static float Degrees(float radians) => radians * 180 / MathF.PI;
}
