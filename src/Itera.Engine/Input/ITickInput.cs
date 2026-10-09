namespace Itera.Engine.Input;

/// <summary>
/// The input of the fixed step being run, for fixed systems. <see cref="InputService"/> provides it in the game; tests
/// provide snapshots built with <see cref="InputAccumulator"/>. When one loop iteration runs several steps, the first
/// sees the presses and mouse movement since the previous step and the others see none, because no events were read
/// between them; held keys are the same in each.
/// </summary>
public interface ITickInput
{
    /// <summary>The input collected since the previous fixed step.</summary>
    ref readonly InputSnapshot Tick { get; }
}
