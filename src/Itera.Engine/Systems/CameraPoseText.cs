using System.Globalization;
using Itera.Engine.Maths;

namespace Itera.Engine.Systems;

/// <summary>
/// The text form of a camera pose: "x,y,z,yaw,pitch", the eye in absolute metres from the map's middle (whatever the
/// match's origin) and the angles in degrees, five finite numbers. The console's <c>tp</c> and a game's start pose read it with the same rule, so a bad value (a missing part,
/// text, NaN, or infinity) stops the game before the window opens instead of throwing in the loop or giving the player
/// a NaN yaw.
/// </summary>
public static class CameraPoseText
{
    /// <summary>The form the value must take, for messages.</summary>
    public const string Form = "x,y,z,yaw,pitch";

    /// <summary>
    /// Reads <paramref name="text"/> into the absolute eye position in metres and the yaw and pitch in radians. False
    /// when it is not five finite numbers.
    /// </summary>
    public static bool TryParse(string text, out WorldPoint position, out float yaw, out float pitch)
    {
        ArgumentNullException.ThrowIfNull(text);
        position = default;
        yaw = 0;
        pitch = 0;
        var parts = text.Split(',');
        if (parts.Length != 5)
        {
            return false;
        }

        Span<double> values = stackalloc double[5];
        for (var i = 0; i < 5; i++)
        {
            if (
                !double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !double.IsFinite(values[i])
            )
            {
                return false;
            }
        }

        const double radians = Math.PI / 180;
        position = new WorldPoint(values[0], values[1], values[2]);
        yaw = (float)(values[3] * radians);
        pitch = (float)(values[4] * radians);
        return true;
    }

    /// <summary>The pose as its text, the eye to the centimetre and the angles to the degree.</summary>
    public static string Format(in WorldPoint position, float yaw, float pitch)
    {
        const float degrees = 180 / MathF.PI;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{position.X:F2},{position.Y:F2},{position.Z:F2},{yaw * degrees:F0},{pitch * degrees:F0}"
        );
    }
}
