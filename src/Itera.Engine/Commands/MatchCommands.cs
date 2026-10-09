using System.Globalization;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's commands that start and end matches (<see cref="CommandScope.Local"/>): <c>play</c> offline, <c>host</c>,
/// <c>join</c>, and <c>leave</c>. Each asks <see cref="MatchControl"/>, which the loop answers at its next iteration; a
/// start ends the match now first. The menu asks the same. A match the console starts is not the options': a failed
/// start or a session's end goes back to the menu rather than ending the run.
/// </summary>
public sealed class MatchCommands(
    ConsoleCommands commands,
    MatchControl matches,
    SteamService steam,
    IOptions<EngineOptions> options
)
{
    /// <summary>Adds the commands to the registry. Once, at startup.</summary>
    public void Register()
    {
        commands.Register(
            "play",
            "[seed]",
            "Starts a match offline, from the seed (the options' by default); ends the match now first.",
            CommandScope.Local,
            Play
        );
        commands.Register(
            "host",
            "[seed] [steam|direct]",
            "Hosts a match: through Steam when it runs (friends see it), else on UDP (Engine:Session:Port).",
            CommandScope.Local,
            Host
        );
        commands.Register(
            "join",
            "address",
            "Joins a host: host:port over UDP, or lobby:ID or steam:ID through Steam; ends the match now first.",
            CommandScope.Local,
            Join
        );
        commands.Register("leave", "", "Ends the match and goes back to the menu.", CommandScope.Local, Leave);
    }

    /// <summary>
    /// The match hosted from the menu or the console: through Steam when it runs, so friends see it, else over UDP on the
    /// options' port.
    /// </summary>
    public MatchStart HostStart(ulong? seed = null, SessionTransportKind? transport = null) =>
        new(
            SessionMode.Host,
            transport ?? (steam.IsAvailable ? SessionTransportKind.Steam : SessionTransportKind.Direct),
            null,
            seed ?? options.Value.Voxels.Seed,
            options.Value.Save.Name,
            FromCommandLine: false
        );

    /// <summary>The match played offline from the menu or the console.</summary>
    public MatchStart OfflineStart(ulong? seed = null) =>
        new(
            SessionMode.Offline,
            SessionTransportKind.Direct,
            null,
            seed ?? options.Value.Voxels.Seed,
            options.Value.Save.Name,
            FromCommandLine: false
        );

    /// <summary>The match that joins <paramref name="address"/>: through Steam for <c>lobby:</c> and <c>steam:</c>.</summary>
    public MatchStart JoinStart(string address) =>
        new(
            SessionMode.Join,
            SteamAddress.TryParse(address, out _, out _) ? SessionTransportKind.Steam : SessionTransportKind.Direct,
            address,
            options.Value.Voxels.Seed,
            null,
            FromCommandLine: false
        );

    private void Play(CommandContext context)
    {
        if (!TryReadSeed(context, 0, out var seed))
        {
            return;
        }

        matches.RequestStart(OfflineStart(seed));
        context.Print("Starting a match offline.");
    }

    private void Host(CommandContext context)
    {
        SessionTransportKind? transport = null;
        var index = 0;
        if (
            index < context.Count
            && ulong.TryParse(context[index], NumberStyles.None, CultureInfo.InvariantCulture, out _)
        )
        {
            index++;
        }

        if (context.Is(index, "steam"))
        {
            if (!steam.IsAvailable)
            {
                context.PrintError("host steam: Steam is not running (" + steam.UnavailableReason + ").");
                return;
            }

            transport = SessionTransportKind.Steam;
        }
        else if (context.Is(index, "direct"))
        {
            transport = SessionTransportKind.Direct;
        }
        else if (index < context.Count)
        {
            context.PrintUsage();
            return;
        }

        if (!TryReadSeed(context, 0, out var seed))
        {
            return;
        }

        var start = HostStart(seed, transport);
        matches.RequestStart(start);
        context.Print(
            start.Transport == SessionTransportKind.Steam ? "Hosting a match through Steam." : "Hosting a match on UDP."
        );
    }

    private void Join(CommandContext context)
    {
        if (context.Count != 1)
        {
            context.PrintUsage();
            return;
        }

        var address = context[0];
        if (!SteamAddress.TryParse(address, out _, out _) && !SessionAddress.TryParse(address, out _, out _))
        {
            context.PrintError($"join: '{address}' is not host:port, lobby:ID, or steam:ID.");
            return;
        }

        matches.RequestStart(JoinStart(address));
        context.Print("Joining " + address + ".");
    }

    private void Leave(CommandContext context)
    {
        if (matches.Current is null)
        {
            context.PrintError("leave: no match is played.");
            return;
        }

        matches.RequestLeave("You left the match.");
    }

    /// <summary>The seed in argument <paramref name="index"/> when it is a number; null when it is not there or a word.</summary>
    private static bool TryReadSeed(CommandContext context, int index, out ulong? seed)
    {
        seed = null;
        if (index >= context.Count)
        {
            return true;
        }

        if (ulong.TryParse(context[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            seed = value;
            return true;
        }

        if (context.Command.Name == "host")
        {
            // host steam / host direct: no seed.
            return true;
        }

        context.PrintUsage();
        return false;
    }
}
