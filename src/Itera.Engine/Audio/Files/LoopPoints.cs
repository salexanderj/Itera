namespace Itera.Engine.Audio.Files;

/// <summary>
/// The part of a sound that repeats, in frames (a frame is one sample of each channel): from <see cref="Start"/> to just
/// before <see cref="End"/>. The part before <see cref="Start"/> is an intro, played once. <see cref="None"/> when the
/// file names no loop: the whole sound repeats.
/// </summary>
/// <param name="Start">The first frame of the loop.</param>
/// <param name="End">The frame after the loop's last one.</param>
public readonly record struct LoopPoints(long Start, long End)
{
    /// <summary>No loop named: the whole sound repeats.</summary>
    public static LoopPoints None => new(-1, -1);

    /// <summary>True when a loop is named.</summary>
    public bool IsSet => Start >= 0 && End > Start;

    /// <summary>The frames the loop repeats.</summary>
    public long Length => IsSet ? End - Start : 0;

    /// <summary>
    /// These points when they lie within a sound of <paramref name="frames"/> frames (unknown when below 0), else
    /// <see cref="None"/> with why in <paramref name="problem"/>.
    /// </summary>
    public LoopPoints Within(long frames, out string? problem)
    {
        problem = null;
        if (!IsSet)
        {
            if (Start >= 0 || End >= 0)
            {
                problem = $"loop points {Start} to {End} name no frames: ignored";
            }

            return None;
        }

        if (frames >= 0 && End > frames)
        {
            problem = $"loop points {Start} to {End} reach past the sound's {frames} frames: ignored";
            return None;
        }

        return this;
    }
}
