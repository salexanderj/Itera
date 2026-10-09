namespace Itera.Modding;

/// <summary>Timing information for one rendered frame.</summary>
/// <param name="DeltaSeconds">Seconds since the previous frame. The engine clamps large values after a stall.</param>
/// <param name="TotalSeconds">Seconds since the game loop started.</param>
/// <param name="FrameIndex">Number of frames completed before this one.</param>
/// <param name="Alpha">Fraction of a fixed step that has elapsed since the last fixed update, in [0, 1). Use it to interpolate.</param>
public readonly record struct FrameTime(float DeltaSeconds, double TotalSeconds, ulong FrameIndex, float Alpha);
