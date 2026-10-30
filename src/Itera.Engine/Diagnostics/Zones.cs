using System.Diagnostics;
using System.Text.Json;

namespace Itera.Engine.Diagnostics;

/// <summary>A zone's number (<see cref="Zones.Name"/>): a span of code named once and timed where it runs.</summary>
public readonly record struct ZoneId(int Value);

/// <summary>
/// Timed spans of code for a trace that Perfetto or Chrome's tracing opens: a span named once (<see cref="Name"/>, kept
/// in a static field), timed where it runs (<see cref="Time"/>, ended with its <c>using</c> scope), on any thread. Off
/// until <see cref="Start"/>, a zone costs one read of a flag. While recording, each thread keeps its last
/// <see cref="EventsAThread"/> zones in a ring of its own, made at its first zone; <see cref="Stop"/> ends the recording
/// and <see cref="WriteTrace"/> writes it. A zone allocates nothing after its thread's first; a zone still open when the
/// recording stops is dropped.
/// </summary>
public static class Zones
{
    /// <summary>The zones a thread keeps, its newest: at 16 bytes each, 2 MB a thread that times any.</summary>
    public const int EventsAThread = 1 << 17;

    /// <summary>The most zones a run names.</summary>
    public const int MostZones = 4096;

    private static readonly object s_lock = new();
    private static readonly List<string> s_names = [];
    private static readonly List<ZoneRing> s_rings = [];

    [ThreadStatic]
    private static ZoneRing? t_ring;

    private static volatile bool s_recording;
    private static long s_startedAt;

    /// <summary>True while zones are recorded.</summary>
    public static bool Recording => s_recording;

    /// <summary>The zones named so far.</summary>
    public static int Count
    {
        get
        {
            lock (s_lock)
            {
                return s_names.Count;
            }
        }
    }

    /// <summary>
    /// The zone named <paramref name="name"/>, named now if it is new: the same name gives the same zone. Once a zone's
    /// site, as a static field. Throws past <see cref="MostZones"/>.
    /// </summary>
    public static ZoneId Name(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (s_lock)
        {
            var found = s_names.IndexOf(name);
            if (found >= 0)
            {
                return new ZoneId(found);
            }

            if (s_names.Count == MostZones)
            {
                throw new InvalidOperationException($"At most {MostZones} zones; {name} is one too many.");
            }

            s_names.Add(name);
            return new ZoneId(s_names.Count - 1);
        }
    }

    /// <summary>Times <paramref name="zone"/> from now until the scope's end, while recording; nothing otherwise.</summary>
    public static ZoneScope Time(ZoneId zone) =>
        s_recording ? new ZoneScope(zone.Value, Stopwatch.GetTimestamp()) : default;

    /// <summary>Forgets every zone recorded and records from now.</summary>
    public static void Start()
    {
        lock (s_lock)
        {
            foreach (var ring in s_rings)
            {
                ring.Clear();
            }

            s_startedAt = Stopwatch.GetTimestamp();
            s_recording = true;
        }
    }

    /// <summary>Ends the recording; what was recorded is kept for <see cref="WriteTrace"/>.</summary>
    public static void Stop() => s_recording = false;

    /// <summary>
    /// Writes the zones recorded to <paramref name="path"/> as Chrome's trace JSON, each thread a track named as its
    /// thread is, times in microseconds from the recording's start; returns how many zones it wrote. Best after
    /// <see cref="Stop"/>: a thread still recording may change its newest zones while they are read.
    /// </summary>
    public static int WriteTrace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (s_lock)
        {
            using var stream = File.Create(path);
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteString("displayTimeUnit", "ms");
            json.WriteStartArray("traceEvents");
            var written = 0;
            var microseconds = 1e6 / Stopwatch.Frequency;
            for (var track = 0; track < s_rings.Count; track++)
            {
                var ring = s_rings[track];
                json.WriteStartObject();
                json.WriteString("name", "thread_name");
                json.WriteString("ph", "M");
                json.WriteNumber("pid", 1);
                json.WriteNumber("tid", track + 1);
                json.WriteStartObject("args");
                json.WriteString("name", ring.Thread);
                json.WriteEndObject();
                json.WriteEndObject();
                var count = ring.Written;
                for (var i = Math.Max(0, count - EventsAThread); i < count; i++)
                {
                    var zone = ring.Events[i & (EventsAThread - 1)];
                    if (zone.Start < s_startedAt || (uint)zone.Zone >= (uint)s_names.Count)
                    {
                        continue;
                    }

                    json.WriteStartObject();
                    json.WriteString("name", s_names[zone.Zone]);
                    json.WriteString("ph", "X");
                    json.WriteNumber("pid", 1);
                    json.WriteNumber("tid", track + 1);
                    json.WriteNumber("ts", Math.Round((zone.Start - s_startedAt) * microseconds, 3));
                    json.WriteNumber("dur", Math.Round(zone.Ticks * microseconds, 3));
                    json.WriteEndObject();
                    written++;
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();
            return written;
        }
    }

    /// <summary>A zone timed from <paramref name="start"/> ends now, on this thread's ring.</summary>
    internal static void End(int zone, long start)
    {
        if (!s_recording)
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - start;
        (t_ring ?? Ring()).Add(zone, start, ticks);
    }

    /// <summary>This thread's ring, made at its first zone and kept for the run.</summary>
    private static ZoneRing Ring()
    {
        var thread = Thread.CurrentThread;
        var ring = new ZoneRing(thread.Name ?? $"thread {thread.ManagedThreadId}");
        lock (s_lock)
        {
            s_rings.Add(ring);
        }

        t_ring = ring;
        return ring;
    }

    /// <summary>One zone recorded: its start (Stopwatch ticks), its length, and its zone.</summary>
    private struct ZoneEvent
    {
        public long Start;
        public int Ticks;
        public int Zone;
    }

    /// <summary>A thread's newest zones, written by that thread alone.</summary>
    private sealed class ZoneRing(string thread)
    {
        public readonly ZoneEvent[] Events = new ZoneEvent[EventsAThread];

        private long _written;

        public string Thread { get; } = thread;

        /// <summary>The zones written since the last clear; the newest <see cref="EventsAThread"/> are kept.</summary>
        public long Written => Volatile.Read(ref _written);

        public void Add(int zone, long start, long ticks)
        {
            ref var slot = ref Events[_written & (EventsAThread - 1)];
            slot.Start = start;
            slot.Ticks = (int)Math.Min(ticks, int.MaxValue);
            slot.Zone = zone;
            // A whole zone before the count that shows it.
            Volatile.Write(ref _written, _written + 1);
        }

        public void Clear() => Volatile.Write(ref _written, 0);
    }
}

/// <summary>A zone being timed (<see cref="Zones.Time"/>): its end is its scope's.</summary>
public readonly ref struct ZoneScope
{
    private readonly int _zone;
    private readonly long _start;

    internal ZoneScope(int zone, long start)
    {
        _zone = zone;
        _start = start;
    }

    /// <summary>Ends the zone; nothing for one begun while nothing was recorded.</summary>
    public void Dispose()
    {
        if (_start != 0)
        {
            Zones.End(_zone, _start);
        }
    }
}
