using System.Globalization;
using System.Numerics;

namespace Itera.Engine.Systems;

/// <summary>
/// The text form of a camera pose: "x,y,z,yaw,pitch", the eye in metres and the angles in degrees, five finite
/// numbers. The console's <c>tp</c> and a game's start pose read it with the same rule, so a bad value (a missing part,
/// text, NaN, or infinity) stops the game before the window opens instead of throwing in the loop or giving the player
/// a NaN yaw.
/// </summary>
public static class CameraPoseText
{
    /// <summary>The form the value must take, for messages.</summary>
    public const string Form = "x,y,z,yaw,pitch";

    /// <summary>
    /// Reads <paramref name="text"/> into the eye position in metres and the yaw and pitch in radians. False when it is
    /// not five finite numbers.
    /// </summary>
    public static bool TryParse(string text, out Vector3 position, out float yaw, out float pitch)
    {
        position = default;
        yaw = 0;
        pitch = 0;
        var parts = text.Split(',');
        if (parts.Length != 5)
        {
            return false;
        }

        Span<float> values = stackalloc float[5];
        for (var i = 0; i < 5; i++)
        {
            if (
                !float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !float.IsFinite(values[i])
            )
            {
                return false;
            }
        }

        const float radians = MathF.PI / 180;
        position = new Vector3(values[0], values[1], values[2]);
        yaw = values[3] * radians;
        pitch = values[4] * radians;
        return true;
    }
}
