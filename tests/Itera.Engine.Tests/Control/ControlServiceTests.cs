using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Control;
using Itera.Engine.Diagnostics;
using Itera.Engine.Input;
using Itera.Engine.Loading;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Time;
using Itera.Engine.UI;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Control;

/// <summary>The control port over a real loopback connection, its main-thread side pumped by the test.</summary>
public sealed class ControlServiceTests : IDisposable
{
    private readonly ControlLog _log = new();
    private readonly ConsoleCommands _commands;
    private readonly DevConsole _console;
    private readonly LoadingScreen _loading;
    private readonly GpuTimings _gpu = new();
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly InputService _input;
    private readonly RecordingLogger _serviceLogger = new();
    private SessionRole _role = SessionRole.Offline;
    private ControlService? _service;
    private double _now;
    private ulong _frames;
    private ulong _steps;

    public ControlServiceTests()
    {
        _commands = new ConsoleCommands(() => _role, new ControlLogLogger<ConsoleCommands>(_log));
        _commands.Register("say", "text", "", CommandScope.Local, context => context.Print(context[0]));
        _commands.Register("hostonly", "", "", CommandScope.Match, context => context.Print("ran"));
        var options = Options.Create(new EngineOptions());
        _console = new DevConsole(options, _commands, new ConsoleLogBuffer(), NullLogger<DevConsole>.Instance);
        _loading = new LoadingScreen(options, NullLogger<LoadingScreen>.Instance);
        _loading.Initialize([]);
        _input = new InputService(_window, NullLogger<InputService>.Instance);
    }

    public void Dispose()
    {
        _service?.Dispose();
        _input.Dispose();
        _window.Dispose();
    }

    [Fact]
    public void Run_replies_with_the_commands_answers_and_the_log_lines_they_wrote()
    {
        var service = Start();
        using var client = Connect(service);

        var reply = Ask(client, """{"id":7,"op":"run","line":"say hello"}""");

        Assert.Equal(7, reply.GetProperty("id").GetInt64());
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(["hello"], Strings(reply, "answers"));
        Assert.Contains("[INF] Console: say hello", Strings(reply, "log"));
        // The answer shows in the in-game console too.
        Assert.Contains(Enumerable.Range(0, _console.ScrollbackCount), i => _console.ScrollbackLine(i).Text == "hello");
    }

    [Fact]
    public void An_unknown_command_and_a_refused_one_are_failures()
    {
        var service = Start();
        using var client = Connect(service);
        _role = SessionRole.Client;

        var reply = Ask(client, """{"op":"run","line":"nosuch; hostonly"}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        var failures = Strings(reply, "failures");
        Assert.Equal(2, failures.Length);
        Assert.StartsWith("nosuch: no such command", failures[0], StringComparison.Ordinal);
        Assert.StartsWith("hostonly: only a host", failures[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_wait_for_frames_replies_once_they_are_built_and_one_past_its_time_fails()
    {
        var service = Start();
        using var client = Connect(service);

        var frames = AskAsync(client, """{"op":"wait","for":"frames","n":5}""");
        Pump(service, () => service.WaitingCount > 0);
        var start = _frames;
        // Frames are counted until the service has finished the wait, not until the reply arrives.
        Pump(service, () => service.WaitingCount == 0, framesPerPump: 1);
        Assert.InRange(_frames - start, 5ul, 6ul);
        Pump(service, () => frames.IsCompleted);
        Assert.True(Reply(frames).GetProperty("ok").GetBoolean());

        var late = AskAsync(client, """{"op":"wait","for":"log","pattern":"never","timeout":0.5}""");
        Pump(service, () => late.IsCompleted, secondsPerPump: 0.1);
        var reply = Reply(late);
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.StartsWith("timed out", reply.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_wait_for_a_log_line_replies_with_the_line()
    {
        var service = Start();
        using var client = Connect(service);

        var wait = AskAsync(client, """{"op":"wait","for":"log","pattern":"Console: say th.*"}""");
        Pump(service, () => service.WaitingCount > 0);
        _console.RunNow("say there", new ControlLogOutput());
        Pump(service, () => wait.IsCompleted);

        Assert.Equal("Console: say there", Reply(wait).GetProperty("matched").GetString());
    }

    [Fact]
    public void Log_returns_the_lines_since_the_number_given_and_the_next_number()
    {
        var service = Start();
        using var client = Connect(service);
        _log.Add(LogLevel.Information, "first");
        _log.Add(LogLevel.Debug, "second");

        var all = Ask(client, """{"op":"log"}""");
        var next = all.GetProperty("next").GetInt64();
        _log.Add(LogLevel.Warning, "third");
        var since = Ask(client, $$"""{"op":"log","from":{{next}}}""");
        var warnings = Ask(client, """{"op":"log","level":"warning","tail":10}""");

        Assert.Equal(["[INF] first", "[DBG] second"], Strings(all, "lines"));
        Assert.Equal(["[WRN] third"], Strings(since, "lines"));
        Assert.Equal(["[WRN] third"], Strings(warnings, "lines"));
    }

    [Fact]
    public void A_request_that_is_not_one_gets_an_error_and_the_connection_goes_on()
    {
        var service = Start();
        using var client = Connect(service);

        var bad = Ask(client, "{not json");
        var noOp = Ask(client, """{"id":3}""");
        var unknown = Ask(client, """{"op":"fly"}""");
        var good = Ask(client, """{"op":"run","line":"say still"}""");

        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.StartsWith("not JSON", bad.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.False(noOp.GetProperty("ok").GetBoolean());
        Assert.False(unknown.GetProperty("ok").GetBoolean());
        Assert.Equal(["still"], Strings(good, "answers"));
    }

    /// <summary>A line that is not a JSON object is answered with an error and ends its connection.</summary>
    [Fact]
    public void A_line_that_is_not_a_json_object_is_refused_and_ends_the_connection()
    {
        var service = Start();
        using var client = Connect(service);

        var reply = Parse(client.Ask("not json"));

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.StartsWith("not a request", reply.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Throws<IOException>(() => client.Ask("""{"op":"log"}"""));
        using var another = Connect(service);
        Assert.True(Ask(another, """{"op":"log"}""").GetProperty("ok").GetBoolean());
    }

    /// <summary>
    /// A web page's POST to the port (a browser sends one to any address a page names) runs nothing: its first line
    /// ends the connection before its body is read, and a body sent alone without a line's end is never a request.
    /// </summary>
    [Theory]
    [InlineData(
        "POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: text/plain\r\n\r\n{\"op\":\"run\",\"line\":\"say from a page\"}"
    )]
    [InlineData("{\"op\":\"run\",\"line\":\"say from a page\"}")]
    public void A_web_pages_post_runs_nothing(string sent)
    {
        var service = Start();
        using (var raw = new TcpClient { ReceiveTimeout = 15_000 })
        {
            raw.Connect(IPAddress.Loopback, service.Port);
            var stream = raw.GetStream();
            stream.Write(Encoding.UTF8.GetBytes(sent));
            raw.Client.Shutdown(SocketShutdown.Send);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            // The game closes the connection: after an error for the first case, at once for the second.
            while (reader.ReadLine() is not null) { }
        }

        using var client = Connect(service);
        Assert.True(Ask(client, """{"op":"log"}""").GetProperty("ok").GetBoolean());

        Assert.DoesNotContain(
            Enumerable.Range(0, _console.ScrollbackCount),
            i => _console.ScrollbackLine(i).Text == "from a page"
        );
    }

    /// <summary>
    /// Fields of another kind than asked count as missing or fail their request, and the connection and the game go on
    /// (a string id ended the connection's thread, a string timeout the main thread).
    /// </summary>
    [Fact]
    public void Fields_of_another_kind_count_as_missing_or_fail_the_request_and_the_game_goes_on()
    {
        var service = Start();
        using var client = Connect(service);

        var stringId = Ask(client, """{"id":"seven","op":"run","line":"say a","timeout":"long"}""");
        var stringFrom = Ask(client, """{"op":"log","from":"x"}""");
        var stringCrop = Ask(client, """{"op":"shot","crop":["a",1,2,3]}""");
        var good = Ask(client, """{"op":"run","line":"say still"}""");

        Assert.Equal(0, stringId.GetProperty("id").GetInt64());
        Assert.Equal(["a"], Strings(stringId, "answers"));
        Assert.True(stringFrom.GetProperty("ok").GetBoolean());
        Assert.StartsWith("shot's \"crop\"", stringCrop.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(["still"], Strings(good, "answers"));
    }

    /// <summary>A line past the limit is answered with an error and ends its connection.</summary>
    [Fact]
    public void A_line_past_the_limit_is_refused_and_ends_the_connection()
    {
        var service = Start();
        using var client = Connect(service);

        var reply = Parse(client.Ask(new string('x', ControlService.MostLineChars + 10)));

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("longer than", reply.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Throws<IOException>(() => client.Ask("""{"op":"log"}"""));
        using var another = Connect(service);
        Assert.True(Ask(another, """{"op":"log"}""").GetProperty("ok").GetBoolean());
    }

    /// <summary>Past the most connections a tool is told so and closed.</summary>
    [Fact]
    public void Past_the_most_connections_a_tool_is_told_and_closed()
    {
        var service = Start();
        var clients = new List<Client>();
        try
        {
            for (var i = 0; i < ControlService.MostConnections; i++)
            {
                var client = Connect(service);
                clients.Add(client);
                Assert.True(Ask(client, """{"op":"log"}""").GetProperty("ok").GetBoolean());
            }

            using var extra = Connect(service);
            var refusal = Parse(extra.ReadLine()!);
            Assert.Contains("too many connections", refusal.GetProperty("error").GetString(), StringComparison.Ordinal);

            clients[0].Dispose();
            clients.RemoveAt(0);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                // The closed one's thread leaves the list a moment after it closes.
                using var next = Connect(service);
                var reply = Parse(next.Ask("""{"op":"log"}"""));
                if (reply.GetProperty("ok").GetBoolean())
                {
                    break;
                }

                Assert.True(DateTime.UtcNow < deadline, "no room after a connection closed");
                Thread.Sleep(10);
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// A wait-for-log pattern that backtracks without end on a line fails its wait instead of holding the main thread.
    /// </summary>
    [Fact]
    public void A_pattern_that_takes_too_long_fails_its_wait()
    {
        var service = Start();
        using var client = Connect(service);

        var wait = AskAsync(client, """{"op":"wait","for":"log","pattern":"(a+)+$"}""");
        Pump(service, () => service.WaitingCount > 0);
        _console.RunNow("say " + new string('a', 30) + "!", new ControlLogOutput());
        var started = DateTime.UtcNow;
        Pump(service, () => wait.IsCompleted);

        var reply = Reply(wait);
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("took too long", reply.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A shot that times out before any frame takes it is taken back, so the next shot is not refused for it.
    /// </summary>
    [Fact]
    public void A_shot_no_frame_took_is_taken_back_when_it_times_out()
    {
        var service = Start();
        using var client = Connect(service);

        var first = AskAsync(client, """{"op":"shot","timeout":0.05}""");
        Pump(service, () => first.IsCompleted, secondsPerPump: 0.01);
        var second = AskAsync(client, """{"op":"shot","timeout":0.05}""");
        Pump(service, () => second.IsCompleted, secondsPerPump: 0.01);

        Assert.StartsWith("timed out", Reply(first).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("timed out", Reply(second).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Input_holds_a_key_for_its_time_and_a_bad_action_is_refused()
    {
        var service = Start();
        using var client = Connect(service);

        var bad = Ask(client, """{"op":"input","actions":["jump W"]}""");
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.StartsWith("input: 'jump W'", bad.GetProperty("error").GetString(), StringComparison.Ordinal);

        var hold = AskAsync(client, """{"op":"input","actions":["hold W 0.5","tap 1"]}""");
        Pump(service, () => service.WaitingCount > 0);
        _input.BeginTick();
        Assert.True(_input.Tick.IsDown(Key.W));
        Assert.False(_input.Tick.WasPressed(Key.Number1));

        Pump(service, () => hold.IsCompleted, secondsPerPump: 0.1);
        Assert.True(Reply(hold).GetProperty("ok").GetBoolean());
        _input.BeginTick();
        Assert.False(_input.Tick.IsDown(Key.W));
        Assert.True(_input.Tick.WasPressed(Key.Number1));
    }

    [Fact]
    public void A_look_right_after_capture_on_turns_the_view()
    {
        var service = Start();
        using var client = Connect(service);

        Ask(client, """{"op":"input","actions":["capture on","look 40 -10"]}""");
        _input.BeginFrame();

        Assert.True(_input.Frame.PointerCaptured);
        Assert.Equal(new System.Numerics.Vector2(40, -10), _input.Frame.MouseDelta);
    }

    [Fact]
    public void A_scroll_turns_the_wheel()
    {
        var service = Start();
        using var client = Connect(service);

        Ask(client, """{"op":"input","actions":["capture on","scroll -2","scroll 0.5"]}""");
        _input.BeginTick();

        Assert.Equal(-1.5f, _input.Tick.Scroll);
    }

    [Fact]
    public void An_input_that_times_out_lets_its_keys_go()
    {
        var service = Start();
        using var client = Connect(service);

        var hold = AskAsync(client, """{"op":"input","actions":["hold ShiftLeft 100"],"timeout":0.3}""");
        Pump(service, () => hold.IsCompleted, secondsPerPump: 0.1);

        Assert.StartsWith("timed out", Reply(hold).GetProperty("error").GetString(), StringComparison.Ordinal);
        _input.BeginTick();
        Assert.False(_input.Tick.IsDown(Key.ShiftLeft));
    }

    [Fact]
    public void Disabled_it_opens_no_port()
    {
        var service = Create(enabled: false, port: 0);
        service.Initialize();

        Assert.False(service.Listening);
        Assert.Equal(0, service.Port);
    }

    [Fact]
    public void A_port_another_program_holds_is_a_warning_and_the_game_goes_on()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;

        var service = Create(enabled: true, port: port);
        service.Initialize();

        Assert.False(service.Listening);
        Assert.Contains(
            _serviceLogger.Entries,
            entry =>
                entry.Level == LogLevel.Warning && entry.Message.Contains("not available", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Shutting_down_answers_a_waiting_request()
    {
        var service = Start();
        using var client = Connect(service);

        var wait = AskAsync(client, """{"op":"wait","for":"seconds","n":100}""");
        Pump(service, () => service.WaitingCount > 0);
        service.Dispose();

        Assert.True(Finished(wait, TimeSpan.FromSeconds(5)));
        Assert.Equal("the game is shutting down", Reply(wait).GetProperty("error").GetString());
    }

    [Fact]
    public void Idle_iterations_allocate_nothing()
    {
        var service = Start();
        using var client = Connect(service);
        Ask(client, """{"op":"run","line":"say warm"}""");

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            service.RunPending(i, (ulong)i);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Wait_for_ticks_replies_once_the_fixed_steps_have_run()
    {
        var service = Start();
        using var client = Connect(service);
        var reply = AskAsync(client, """{"op":"wait","for":"ticks","n":3}""");

        Pump(service, () => reply.IsCompleted, stepsPerPump: 1);

        Assert.True(Reply(reply).GetProperty("ok").GetBoolean());
        Assert.True(_steps >= 3, $"replied after {_steps} step(s)");
    }

    [Fact]
    public void Query_reads_an_entitys_components_the_entities_with_a_component_and_the_archetypes()
    {
        using var world = World.Create();
        var player = world.Create(
            new LocalPlayer(),
            new CharacterPosition { Feet = new Vector3(1, 2, 3) },
            new PlayerMode { Flying = true }
        );
        world.Create(new CharacterPosition { Feet = new Vector3(4, 5, 6) });
        var service = Start(world: () => world, time: new FixedStepAccumulator(1f / 60, 8));
        using var client = Connect(service);

        var shown = Ask(client, """{"op":"query","entity":"player"}""");
        var listed = Ask(client, """{"op":"query","component":"characterposition","most":1}""");
        var counts = Ask(client, """{"op":"query"}""");
        var status = Ask(client, """{"op":"status"}""");
        var missing = Ask(client, """{"op":"query","entity":"999"}""");

        var components = shown.GetProperty("components");
        Assert.Equal($"{player.Id}:{player.Version}", shown.GetProperty("entity").GetString());
        Assert.Equal([1f, 2f, 3f], Floats(components.GetProperty("CharacterPosition").GetProperty("Feet")));
        Assert.True(components.GetProperty("PlayerMode").GetProperty("Flying").GetBoolean());
        Assert.Equal(2, listed.GetProperty("count").GetInt32());
        Assert.Single(listed.GetProperty("entities").EnumerateArray());
        Assert.Equal(2, counts.GetProperty("entities").GetInt32());
        Assert.Equal(2, counts.GetProperty("archetypes").GetArrayLength());
        Assert.Equal(2, status.GetProperty("entities").GetInt32());
        Assert.False(status.GetProperty("paused").GetBoolean());
        Assert.Equal(1, status.GetProperty("time_scale").GetDouble());
        Assert.Equal(
            [1f, 2f, 3f],
            Floats(status.GetProperty("player").GetProperty("CharacterPosition").GetProperty("Feet"))
        );
        Assert.False(missing.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Run_as_a_script_replies_once_its_waits_have_held_the_commands_after_them()
    {
        _commands.Register(
            "wait",
            "",
            "",
            CommandScope.Local,
            context => _console.Hold("wait", () => _console.Now.Steps >= 3)
        );
        var service = Start();
        using var client = Connect(service);
        var reply = AskAsync(client, """{"op":"run","line":"say a; wait; say b","script":true}""");

        Pump(service, () => reply.IsCompleted, stepsPerPump: 1);

        var answer = Reply(reply);
        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(["a", "b"], Strings(answer, "answers"));
        Assert.True(_steps >= 3, $"replied after {_steps} step(s)");
    }

    /// <summary>A reply whose fields cannot be written still comes, with the reason: the tool is never left waiting.</summary>
    [Fact]
    public void A_reply_whose_fields_cannot_be_written_comes_with_the_reason()
    {
        var request = ControlRequest.Parse("""{"id":4,"op":"status"}""", out var error)!;
        Assert.Null(error);

        request.Complete(_log, fields: _ => throw new InvalidOperationException("the world changed"));
        var reply = Parse(request.WaitForReply());

        Assert.Equal(4, reply.GetProperty("id").GetInt64());
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("failed writing the reply: the world changed", reply.GetProperty("error").GetString());
    }

    [Fact]
    public void Status_gives_the_origin_and_the_camera_absolute()
    {
        var view = new RenderView(Options.Create(new EngineOptions()));
        view.SetCamera(CameraView.Default with { Position = new Vector3(1.5f, 2, -3.25f) });
        var origin = new WorldOrigin();
        origin.StartAt(new WorldPoint(150_000, 20, -400));
        var service = Start(view: view, origin: origin);
        using var client = Connect(service);

        var reply = Ask(client, """{"op":"status"}""");

        Assert.Equal([150_000.0, 20, -400], Doubles(reply.GetProperty("origin")));
        Assert.Equal([150_001.5, 22, -403.25], Doubles(reply.GetProperty("camera_position")));
    }

    [Fact]
    public void Status_without_a_living_player_leaves_the_player_out()
    {
        using var world = World.Create();
        var player = world.Create(new LocalPlayer(), new CharacterPosition());
        world.Destroy(player);
        var service = Start(world: () => world);
        using var client = Connect(service);

        var reply = Ask(client, """{"op":"status"}""");

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.False(reply.TryGetProperty("player", out _));
    }

    [Fact]
    public void Query_without_a_match_fails()
    {
        var service = Start();
        using var client = Connect(service);

        var reply = Ask(client, """{"op":"query"}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("query: no match is played", reply.GetProperty("error").GetString());
    }

    private static float[] Floats(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetSingle())];

    private static double[] Doubles(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetDouble())];

    private ControlService Create(
        bool enabled,
        int port,
        Func<World?>? world = null,
        FixedStepAccumulator? time = null,
        RenderView? view = null,
        WorldOrigin? origin = null
    )
    {
        _service = new ControlService(
            enabled,
            port,
            _console,
            _log,
            _loading,
            _gpu,
            view ?? new RenderView(Options.Create(new EngineOptions())),
            new Screenshots(Path.Combine(Path.GetTempPath(), "itera-test-screenshots")),
            new ShaderReloads(new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, Path.GetTempPath())),
            _input,
            () => _role,
            () => { },
            _serviceLogger,
            world: world,
            time: time,
            origin: () => origin
        );
        return _service;
    }

    private ControlService Start(
        Func<World?>? world = null,
        FixedStepAccumulator? time = null,
        RenderView? view = null,
        WorldOrigin? origin = null
    )
    {
        var service = Create(enabled: true, port: 0, world, time, view, origin);
        service.Initialize();
        Assert.True(service.Listening);
        return service;
    }

    private static Client Connect(ControlService service) => new(service.Port);

    /// <summary>Sends a request and pumps the main-thread side until its reply comes.</summary>
    private JsonElement Ask(Client client, string line)
    {
        var reply = AskAsync(client, line);
        Pump(_service!, () => reply.IsCompleted);
        return Reply(reply);
    }

    private static JsonElement Reply(Task<string> reply) => Parse(reply.GetAwaiter().GetResult());

    private static bool Finished(Task task, TimeSpan timeout) => task.Wait(timeout);

    private static Task<string> AskAsync(Client client, string line) => Task.Run(() => client.Ask(line));

    private void Pump(
        ControlService service,
        Func<bool> done,
        int framesPerPump = 0,
        double secondsPerPump = 0,
        int stepsPerPump = 0
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "no reply within 10 s");
            // As the main loop does: input first, then the console, then the control port's requests.
            _input.Update();
            _console.RunPending(new ConsoleMoment(_steps, _frames, _now, true));
            service.RunPending(_now, _frames);
            _frames += (ulong)framesPerPump;
            _now += secondsPerPump;
            _steps += (ulong)stepsPerPump;
            Thread.Sleep(1);
        }
    }

    private static JsonElement Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static string[] Strings(JsonElement reply, string name) =>
        [.. reply.GetProperty(name).EnumerateArray().Select(value => value.GetString()!)];

    private sealed class Client : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public Client(int port)
        {
            // A reply that never comes fails the test instead of holding it.
            _client = new TcpClient { ReceiveTimeout = 15_000 };
            _client.Connect(IPAddress.Loopback, port);
            var stream = _client.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        }

        public string Ask(string line)
        {
            _writer.WriteLine(line);
            return _reader.ReadLine() ?? throw new IOException("the connection closed");
        }

        /// <summary>A line the game sent unasked; null once it closed.</summary>
        public string? ReadLine() => _reader.ReadLine();

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            _client.Dispose();
        }
    }

    /// <summary>A command output that keeps nothing (the answers go to the scrollback).</summary>
    private sealed class ControlLogOutput : IConsoleOutput
    {
        public void Print(string text) { }

        public void PrintError(string text) { }
    }

    /// <summary>Writes log lines of Debug and above into a <see cref="ControlLog"/>, as the game's sink does.</summary>
    private sealed class ControlLogLogger<T>(ControlLog log) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => log.Add(logLevel, formatter(state, exception));
    }

    private sealed class RecordingLogger : ILogger<ControlService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
