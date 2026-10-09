namespace Itera.Engine.UI;

/// <summary>
/// Whole steps from the wheel's turns, keeping the parts of a step for the next turn: a touchpad sends many small ones,
/// which rounding each to a whole step dropped. A field of its owner, not
/// readonly.
/// </summary>
internal struct WheelSteps
{
    private float _part;

    /// <summary>The whole steps <paramref name="steps"/> and the parts kept from before make; the rest is kept.</summary>
    public int Take(float steps)
    {
        if (!float.IsFinite(steps))
        {
            return 0;
        }

        _part += steps;
        var whole = (int)_part;
        _part -= whole;
        return whole;
    }
}
