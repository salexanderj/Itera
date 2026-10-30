using System.Text.Json;
using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Diagnostics;

/// <summary>
/// <see cref="Zones"/> and the console's <c>zones</c>: a zone timed while nothing records is not written; zones timed
/// while recording are written as Chrome's trace JSON, nested ones inside their outer one and each thread its own named
/// track; a thread keeps its newest zones; one name is one zone; timing a zone allocates nothing once its thread has
/// timed one; <c>zones start</c> and <c>stop</c> write a trace to the data folder's <c>Traces</c>. Serial: the zones
/// are the process's, and every other test's systems time theirs.
/// </summary>
[Collection(SerialCollectionDefinition.Name)]
public sealed class ZonesTests : IDisposable
{
    private static readonly ZoneId s_outer = Zones.Name("test.outer");
    private static readonly ZoneId s_inner = Zones.Name("test.inner");
    private static readonly ZoneId s_other = Zones.Name("test.other thread");
    private static readonly ZoneId s_before = Zones.Name("test.before");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZonesTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Zones.Stop();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Zones_timed_while_recording_are_written_nested_and_on_their_threads_tracks_and_none_from_before()
    {
        using (Zones.Time(s_before)) { }

        Zones.Start();
        using (Zones.Time(s_outer))
        {
            using (Zones.Time(s_inner))
            {
                Thread.SpinWait(1000);
            }
        }

        var other = new Thread(() =>
        {
            using (Zones.Time(s_other)) { }
        })
        {
            Name = "zones test thread",
        };
        other.Start();
        other.Join();
        Zones.Stop();
        var events = Trace();

        Assert.DoesNotContain(events, e => e.Name == "test.before");
        var outer = Assert.Single(events, e => e.Name == "test.outer");
        var inner = Assert.Single(events, e => e.Name == "test.inner");
        var otherZone = Assert.Single(events, e => e.Name == "test.other thread");
        Assert.Equal(outer.Thread, inner.Thread);
        Assert.InRange(inner.Start, outer.Start, outer.Start + outer.Length);
        Assert.InRange(inner.Start + inner.Length, inner.Start, outer.Start + outer.Length + 0.001);
        Assert.NotEqual(outer.Thread, otherZone.Thread);
        Assert.Equal("zones test thread", ThreadName(otherZone.Thread));
    }

    [Fact]
    public void A_thread_keeps_its_newest_zones()
    {
        Zones.Start();
        var thread = new Thread(() =>
        {
            for (var i = 0; i < Zones.EventsAThread + 10; i++)
            {
                using (Zones.Time(i < 10 ? s_outer : s_inner)) { }
            }
        })
        {
            Name = "zones ring thread",
        };
        thread.Start();
        thread.Join();
        Zones.Stop();

        var events = Trace().Where(e => ThreadName(e.Thread) == "zones ring thread").ToList();

        Assert.Equal(Zones.EventsAThread, events.Count);
        Assert.All(events, e => Assert.Equal("test.inner", e.Name));
    }

    [Fact]
    public void One_name_is_one_zone_and_timing_one_allocates_nothing()
    {
        Assert.Equal(s_outer, Zones.Name("test.outer"));
        Zones.Start();
        using (Zones.Time(s_outer)) { }

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            using (Zones.Time(s_inner)) { }
        }

        Zones.Stop();
        for (var i = 0; i < 1000; i++)
        {
            using (Zones.Time(s_inner)) { }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Fact]
    public void Zones_start_and_stop_write_a_trace_to_the_data_folders_traces()
    {
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        var options = new EngineOptions();
        options.Application.DataFolder = _folder;
        new ZoneCommands(commands, Options.Create(options)).Register();
        var output = new Output();

        commands.Execute("zones stop", output);
        commands.Execute("zones start", output);
        using (Zones.Time(s_outer)) { }

        commands.Execute("zones stop", output);
        commands.Execute("zones", output);

        Assert.Equal(["zones: not recording; zones start first"], output.Errors);
        Assert.Equal("zones: recording", output.Lines[0]);
        Assert.StartsWith("zones: ", output.Lines[1]);
        var path = output.Lines[1][(output.Lines[1].IndexOf(" to ", StringComparison.Ordinal) + 4)..];
        Assert.Equal(Path.Combine(_folder, "Traces"), Path.GetDirectoryName(path));
        Assert.Contains("\"test.outer\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.StartsWith("zones: not recording, ", output.Lines[2]);
    }

    private readonly Dictionary<int, string> _threads = [];

    /// <summary>The zones of a trace written now: name, track, start and length in microseconds.</summary>
    private List<(string Name, int Thread, double Start, double Length)> Trace()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "trace.json");
        Zones.WriteTrace(path);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var events = new List<(string, int, double, double)>();
        foreach (var e in json.RootElement.GetProperty("traceEvents").EnumerateArray())
        {
            var thread = e.GetProperty("tid").GetInt32();
            if (e.GetProperty("ph").GetString() == "M")
            {
                _threads[thread] = e.GetProperty("args").GetProperty("name").GetString()!;
                continue;
            }

            events.Add(
                (
                    e.GetProperty("name").GetString()!,
                    thread,
                    e.GetProperty("ts").GetDouble(),
                    e.GetProperty("dur").GetDouble()
                )
            );
        }

        return events;
    }

    private string ThreadName(int thread) => _threads.TryGetValue(thread, out var name) ? name : "";

    private sealed class Output : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string line) => Lines.Add(line);

        public void PrintError(string line) => Errors.Add(line);
    }
}
