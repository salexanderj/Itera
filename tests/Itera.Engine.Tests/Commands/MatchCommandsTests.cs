using Itera.Engine.Commands;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Commands;

/// <summary>
/// The console's <c>play</c>, <c>host</c>, <c>join</c>, and <c>leave</c>: each asks <see cref="MatchControl"/> for the
/// match it names (offline, hosted over UDP without Steam, joined over UDP or Steam by the address's form), none of
/// them a match the options started, and wrong arguments are refused.
/// </summary>
public sealed class MatchCommandsTests
{
    private readonly RecordingOutput _output = new();
    private readonly MatchControl _matches = new();
    private readonly ConsoleCommands _commands;
    private readonly MatchCommands _starts;

    public MatchCommandsTests()
    {
        _commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        var steam = new SteamService(
            new SteamSettings { Enabled = false },
            NullLogger<SteamService>.Instance,
            () => (null, "off")
        );
        steam.Initialize();
        var options = new EngineOptions();
        options.Voxels.Seed = 3;
        _starts = new MatchCommands(_commands, _matches, steam, Options.Create(options));
        _starts.Register();
    }

    [Fact]
    public void A_start_from_a_save_names_it_or_the_options_save_and_refuses_a_name_no_save_has()
    {
        Assert.Equal("my farm", _starts.OfflineStart(saveName: "my farm").SaveName);
        Assert.Equal("castle", _starts.HostStart(saveName: "castle").SaveName);
        // The options name none here.
        Assert.Null(_starts.OfflineStart().SaveName);
        Assert.Throws<ArgumentException>(() => _starts.OfflineStart(saveName: "../outside"));
        Assert.Throws<ArgumentException>(() => _starts.HostStart(saveName: " padded"));
    }

    [Fact]
    public void Play_starts_an_offline_match_of_the_seed_given_or_the_options()
    {
        _commands.Execute("play 7", _output);
        Assert.Equal(
            new MatchStart(SessionMode.Offline, SessionTransportKind.Direct, null, 7, null, false),
            _matches.TakeStart()
        );

        _commands.Execute("play", _output);
        Assert.Equal(3UL, _matches.TakeStart()!.Seed);
        Assert.Empty(_output.Failures);
    }

    [Fact]
    public void Host_without_steam_hosts_over_udp_and_refuses_steam()
    {
        _commands.Execute("host 9", _output);
        Assert.Equal(
            new MatchStart(SessionMode.Host, SessionTransportKind.Direct, null, 9, null, false),
            _matches.TakeStart()
        );

        _commands.Execute("host steam", _output);
        Assert.Null(_matches.TakeStart());
        Assert.Contains(_output.Failures, f => f.Contains("Steam is not running", StringComparison.Ordinal));
    }

    [Fact]
    public void Join_takes_its_transport_from_the_address()
    {
        _commands.Execute("join 127.0.0.1:47600", _output);
        var direct = _matches.TakeStart()!;
        Assert.Equal(SessionMode.Join, direct.Mode);
        Assert.Equal(SessionTransportKind.Direct, direct.Transport);
        Assert.Equal("127.0.0.1:47600", direct.Address);
        Assert.False(direct.FromCommandLine);

        _commands.Execute("join lobby:109775240000000001", _output);
        Assert.Equal(SessionTransportKind.Steam, _matches.TakeStart()!.Transport);

        _commands.Execute("join nowhere", _output);
        Assert.Null(_matches.TakeStart());
        Assert.Single(_output.Failures);
    }

    [Fact]
    public void Leave_asks_to_end_the_match_and_says_when_there_is_none()
    {
        _commands.Execute("leave", _output);

        Assert.Null(_matches.TakeLeave());
        Assert.Contains(_output.Failures, f => f.Contains("no match", StringComparison.Ordinal));
    }

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Answers { get; } = [];

        public List<string> Failures { get; } = [];

        public void Print(string text) => Answers.Add(text);

        public void PrintError(string text) => Failures.Add(text);
    }
}
