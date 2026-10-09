using System.Diagnostics;

namespace Itera.Engine.Time;

/// <summary>Wall-clock source for the game loop. Frame deltas are clamped so a stall cannot cause a simulation spiral.</summary>
public sealed class GameClock
{
    /// <summary>Largest frame delta the clock reports. Time beyond it is dropped.</summary>
    public const float MaxDeltaSeconds = 0.25f;

    private readonly Stopwatch _stopwatch = new();
    private double _lastSeconds;

    /// <summary>Seconds since <see cref="Start"/>.</summary>
    public double TotalSeconds => _stopwatch.Elapsed.TotalSeconds;

    /// <summary>Starts or restarts the clock.</summary>
    public void Start()
    {
        _stopwatch.Restart();
        _lastSeconds = 0;
    }

    /// <summary>Returns the clamped time since the previous call and marks the current time as the new reference.</summary>
    public float Tick()
    {
        var now = _stopwatch.Elapsed.TotalSeconds;
        var delta = now - _lastSeconds;
        _lastSeconds = now;
        return (float)Math.Min(delta, MaxDeltaSeconds);
    }
}
