using Microsoft.Extensions.Logging;

namespace Itera.Engine.Diagnostics;

/// <summary>One log line for the control port, numbered in the order it was logged.</summary>
/// <param name="Sequence">Its number: one more than the line before.</param>
/// <param name="Level">Its level.</param>
/// <param name="Text">The rendered message.</param>
public readonly record struct ControlLogLine(long Sequence, LogLevel Level, string Text);

/// <summary>
/// The newest log lines of Debug and above, numbered, for the control port (<c>ControlService</c>): a reply carries the
/// lines logged while its request ran, and the <c>log</c> request the lines since a reader's last one. Unlike
/// <see cref="ConsoleLogBuffer"/>, reading takes nothing away, so any number of readers each keep their own place. Any
/// thread adds and reads; when more than <see cref="Capacity"/> lines arrive before a reader looks, it misses the oldest.
/// Filled only while the control port is enabled.
/// </summary>
public sealed class ControlLog
{
    /// <summary>Lines kept.</summary>
    public const int Capacity = 4096;

    private readonly object _lock = new();
    private readonly ControlLogLine[] _lines = new ControlLogLine[Capacity];
    private long _next;

    /// <summary>The number the next line will get: a reader that starts now reads from here.</summary>
    public long Next
    {
        get
        {
            lock (_lock)
            {
                return _next;
            }
        }
    }

    /// <summary>Adds a line. Any thread.</summary>
    public void Add(LogLevel level, string text)
    {
        lock (_lock)
        {
            _lines[_next % Capacity] = new ControlLogLine(_next, level, text);
            _next++;
        }
    }

    /// <summary>
    /// Adds to <paramref name="into"/> every line from number <paramref name="from"/> on of <paramref name="least"/> or
    /// above, oldest first, and returns the number to read from next time. Lines already gone are skipped. Any thread.
    /// </summary>
    public long Read(long from, LogLevel least, List<ControlLogLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_lock)
        {
            var start = Math.Max(from, _next - Capacity);
            for (var sequence = Math.Max(start, 0); sequence < _next; sequence++)
            {
                var line = _lines[sequence % Capacity];
                if (line.Level >= least)
                {
                    into.Add(line);
                }
            }

            return _next;
        }
    }
}
