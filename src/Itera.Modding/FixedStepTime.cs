namespace Itera.Modding;

/// <summary>Timing information for one fixed simulation step.</summary>
/// <param name="StepSeconds">Length of every fixed step in seconds. Constant for the life of the process.</param>
/// <param name="Tick">Number of fixed steps of this match completed before this one (0 at the match's start).</param>
public readonly record struct FixedStepTime(float StepSeconds, ulong Tick);
