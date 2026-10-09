using Microsoft.Extensions.Logging;

namespace Itera.Engine.Diagnostics;

/// <summary>One log line for the in-game console.</summary>
/// <param name="Level">Its level, which picks its colour.</param>
/// <param name="Text">The rendered message.</param>
public readonly record struct ConsoleLogLine(LogLevel Level, string Text);

/// <summary>
/// The newest log lines of Information and above, for the in-game console's scrollback. Any thread adds (the logger
/// calls <see cref="Add"/> from whichever thread logs, the render thread among them); the main thread takes them with
/// <see cref="Drain"/>. When more than <see cref="Capacity"/> lines arrive between two drains, the oldest go.
/// </summary>
public sealed class ConsoleLogBuffer
{
    /// <summary>Lines kept between two drains.</summary>
    public const int Capacity = 512;

    private readonly object _lock = new();
    private readonly ConsoleLogLine[] _lines = new ConsoleLogLine[Capacity];
    private int _start;
    private int _count;

    /// <summary>Adds a line. Any thread.</summary>
    public void Add(LogLevel level, string text)
    {
        lock (_lock)
        {
            var index = (_start + _count) % Capacity;
            _lines[index] = new ConsoleLogLine(level, text);
            if (_count < Capacity)
            {
                _count++;
            }
            else
            {
                _start = (_start + 1) % Capacity;
            }
        }
    }

    /// <summary>Hands every line added since the last call to <paramref name="take"/>, oldest first. Main thread.</summary>
    public int Drain(Action<ConsoleLogLine> take)
    {
        ArgumentNullException.ThrowIfNull(take);
        ConsoleLogLine[] taken;
        int count;
        lock (_lock)
        {
            count = _count;
            if (count == 0)
            {
                return 0;
            }

            taken = new ConsoleLogLine[count];
            for (var i = 0; i < count; i++)
            {
                taken[i] = _lines[(_start + i) % Capacity];
                _lines[(_start + i) % Capacity] = default;
            }

            _start = 0;
            _count = 0;
        }

        foreach (var line in taken)
        {
            take(line);
        }

        return count;
    }
}
