namespace Itera.Engine.Systems;

/// <summary>
/// Whether the local player's input acts. While <see cref="Blocked"/>, <c>PlayerInputSystem</c> reads every
/// step as one with nothing pressed or held, and <c>PlayerCameraSystem</c> turns the look no further: a game sets
/// it for a screen that takes the player out of play (a match's end, a cutscene). One per match; main thread.
/// </summary>
public sealed class PlayerInputBlock
{
    /// <summary>True while the local player's input does nothing.</summary>
    public bool Blocked { get; set; }
}
