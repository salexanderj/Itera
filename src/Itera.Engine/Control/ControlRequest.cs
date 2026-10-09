using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Control;

/// <summary>
/// One request to the control port (<see cref="ControlService"/>): a JSON object on one line, <c>{"id":N,"op":"...",
/// ...}</c>, and later its reply, also one line, <c>{"id":N,"ok":true,"answers":[...],"failures":[...],"log":[...],
/// ...}</c>. The connection's thread parses it and waits for the reply; the main thread (or, for a screenshot, the render
/// thread and a worker) completes it once, from any thread. Its answers are what the console commands it ran printed
/// (it is their <see cref="IConsoleOutput"/>), and its log the lines of Information and above logged from its start to
/// its reply.
/// </summary>
internal sealed class ControlRequest : IConsoleOutput
{
    /// <summary>Most log lines a reply carries; older ones are counted instead.</summary>
    public const int MostLogLines = 200;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Log lines hold quotes, angle brackets, and non-ASCII names: kept readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TaskCompletionSource<string> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _answers = [];
    private readonly List<string> _failures = [];
    private int _completed;

    private ControlRequest(long id, string op, JsonElement arguments)
    {
        Id = id;
        Op = op;
        Arguments = arguments;
    }

    /// <summary>The number the tool gave it, sent back in its reply (0 when it gave none).</summary>
    public long Id { get; }

    /// <summary>What it asks for: <c>run</c>, <c>log</c>, <c>wait</c>, <c>gpu</c>, <c>status</c>, and later stages' ops.</summary>
    public string Op { get; }

    /// <summary>The whole object, for the op's own fields.</summary>
    public JsonElement Arguments { get; }

    /// <summary>The log line it reads from: the next one when it started.</summary>
    public long LogFrom { get; set; }

    /// <summary>The game's clock when it started, in seconds.</summary>
    public double StartSeconds { get; set; }

    /// <summary>The frames built when it started.</summary>
    public ulong StartFrame { get; set; }

    /// <summary>Seconds it may wait before it fails.</summary>
    public double TimeoutSeconds { get; set; }

    /// <summary>
    /// For a request that waits: true once it is done, having completed itself. Called once per main loop iteration with
    /// the clock and the frames built. Null for a request completed when it started.
    /// </summary>
    public Func<ControlRequest, double, ulong, bool>? Check { get; set; }

    /// <summary>
    /// For a request that waits: what to undo when it ends before its condition held (a time-out, the game stopping).
    /// Main thread.
    /// </summary>
    public Action? Abandon { get; set; }

    /// <summary>True once <see cref="Complete"/> has run.</summary>
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    /// <summary>
    /// Reads a request from one line. Returns null and sets <paramref name="error"/> when the line is not a JSON object
    /// with a string <c>op</c>.
    /// </summary>
    public static ControlRequest? Parse(string line, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "a request is a JSON object";
                return null;
            }

            if (!root.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
            {
                error = "a request needs \"op\", a string";
                return null;
            }

            // A field of another kind is as if missing: TryGetInt64 throws on a string
            var id =
                root.TryGetProperty("id", out var idValue)
                && idValue.ValueKind == JsonValueKind.Number
                && idValue.TryGetInt64(out var number)
                    ? number
                    : 0;
            error = null;
            return new ControlRequest(id, op.GetString()!, root.Clone());
        }
        catch (JsonException exception)
        {
            error = "not JSON: " + exception.Message;
            return null;
        }
    }

    /// <summary>The reply to a line that was not a request.</summary>
    public static string ErrorReply(string error)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", 0);
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", error);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A string field, or <paramref name="fallback"/> when it is missing or not a string.</summary>
    public string? String(string name, string? fallback = null) =>
        Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : fallback;

    /// <summary>A number field, or <paramref name="fallback"/> when it is missing or not a number.</summary>
    public double Number(string name, double fallback) =>
        Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : fallback;

    /// <summary>A whole number field, or null when it is missing, not a number, or not whole.</summary>
    public long? Whole(string name) =>
        Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>A true or false field, or <paramref name="fallback"/> when it is missing.</summary>
    public bool Flag(string name, bool fallback) =>
        Arguments.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public void Print(string text)
    {
        lock (_answers)
        {
            _answers.Add(text);
        }
    }

    public void PrintError(string text)
    {
        lock (_answers)
        {
            _failures.Add(text);
        }
    }

    /// <summary>
    /// Builds the reply and hands it to the waiting connection; later calls do nothing. It is ok when
    /// <paramref name="error"/> is null and no command printed a failure. <paramref name="fields"/> writes the op's own
    /// fields into the reply's object. Any thread.
    /// </summary>
    public void Complete(ControlLog log, string? error = null, Action<Utf8JsonWriter>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        var lines = new List<ControlLogLine>();
        log.Read(LogFrom, LogLevel.Information, lines);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", Id);
            lock (_answers)
            {
                writer.WriteBoolean("ok", error is null && _failures.Count == 0);
                if (error is not null)
                {
                    writer.WriteString("error", error);
                }

                WriteStrings(writer, "answers", _answers);
                WriteStrings(writer, "failures", _failures);
            }

            WriteLog(writer, "log", lines);
            fields?.Invoke(writer);
            writer.WriteEndObject();
        }

        _reply.SetResult(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>Waits until the reply is built and returns it. The connection's thread.</summary>
    public string WaitForReply() => _reply.Task.GetAwaiter().GetResult();

    /// <summary>Writes <paramref name="lines"/> as an array of "[LVL] text" strings, the newest <see cref="MostLogLines"/>.</summary>
    public static void WriteLog(Utf8JsonWriter writer, string name, List<ControlLogLine> lines)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(lines);
        var skipped = Math.Max(0, lines.Count - MostLogLines);
        writer.WriteStartArray(name);
        if (skipped > 0)
        {
            writer.WriteStringValue(
                string.Create(CultureInfo.InvariantCulture, $"({skipped} earlier line(s) left out)")
            );
        }

        for (var i = skipped; i < lines.Count; i++)
        {
            writer.WriteStringValue(LevelTag(lines[i].Level) + " " + lines[i].Text);
        }

        writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, List<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string LevelTag(LogLevel level) =>
        level switch
        {
            LogLevel.Trace => "[VRB]",
            LogLevel.Debug => "[DBG]",
            LogLevel.Information => "[INF]",
            LogLevel.Warning => "[WRN]",
            LogLevel.Error => "[ERR]",
            _ => "[FTL]",
        };
}
