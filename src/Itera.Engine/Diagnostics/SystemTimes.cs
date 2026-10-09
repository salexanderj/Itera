using System.Globalization;
using System.Text;

namespace Itera.Engine.Diagnostics;

/// <summary>
/// Each fixed system's CPU time over a stats interval: <see cref="Ecs.EcsWorld"/> records every system's update, and
/// <see cref="FrameStats"/> logs the slowest (<see cref="TakeSlowest"/>), so a slow step names what took its time. Added
/// after the Steam test of 2026-09-28, whose log gave only the systems' total. Main thread; recording allocates nothing.
/// </summary>
public sealed class SystemTimes
{
    private readonly List<string> _names = [];
    private long[] _sum = [];
    private long[] _max = [];
    private readonly StringBuilder _text = new();

    /// <summary>Systems added.</summary>
    public int Count => _names.Count;

    /// <summary>Adds a system by name; returns its index for <see cref="Record"/>.</summary>
    public int Add(string name)
    {
        _names.Add(name);
        Array.Resize(ref _sum, _names.Count);
        Array.Resize(ref _max, _names.Count);
        return _names.Count - 1;
    }

    /// <summary>Forgets every system: a match ended, and the next one's systems are added afresh.</summary>
    public void Clear()
    {
        _names.Clear();
        _sum = [];
        _max = [];
    }

    /// <summary>
    /// Records one update of system <paramref name="index"/> that took <paramref name="ticks"/> (Stopwatch ticks); an
    /// index from before a <see cref="Clear"/> past the systems added since is ignored.
    /// </summary>
    public void Record(int index, long ticks)
    {
        if ((uint)index >= (uint)_sum.Length)
        {
            return;
        }

        _sum[index] += ticks;
        if (ticks > _max[index])
        {
            _max[index] = ticks;
        }
    }

    /// <summary>
    /// The <paramref name="count"/> systems with the most time since the last call, slowest first, each with its
    /// average over <paramref name="steps"/> steps and its longest update in milliseconds ("MoveSystem 1.204 max 3.40");
    /// then clears every time. Empty when nothing is recorded or <paramref name="steps"/> is 0.
    /// </summary>
    public string TakeSlowest(int count, int steps)
    {
        _text.Clear();
        if (steps > 0)
        {
            Span<int> slowest = stackalloc int[Math.Min(count, _names.Count)];
            var found = 0;
            for (var i = 0; i < _names.Count; i++)
            {
                // Insert into the list of the slowest so far, kept sorted slowest first.
                var at = found;
                while (at > 0 && _sum[slowest[at - 1]] < _sum[i])
                {
                    at--;
                }

                if (at >= slowest.Length)
                {
                    continue;
                }

                for (var j = Math.Min(found, slowest.Length - 1); j > at; j--)
                {
                    slowest[j] = slowest[j - 1];
                }

                slowest[at] = i;
                found = Math.Min(found + 1, slowest.Length);
            }

            for (var i = 0; i < found; i++)
            {
                var system = slowest[i];
                if (i > 0)
                {
                    _text.Append(", ");
                }

                _text
                    .Append(_names[system])
                    .Append(' ')
                    .Append((Ms(_sum[system]) / steps).ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" max ")
                    .Append(Ms(_max[system]).ToString("F2", CultureInfo.InvariantCulture));
            }
        }

        Array.Clear(_sum);
        Array.Clear(_max);
        return _text.ToString();
    }

    private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
