namespace Itera.Engine.Matches;

/// <summary>
/// What a match starts as: offline, hosting, or joining, over which transport and to which address, from which seed,
/// and with which named save. A match is one world played from its start to its end; one run of the game may play
/// several (<see cref="MatchControl"/>).
/// </summary>
/// <param name="Mode">Offline, Host, or Join.</param>
/// <param name="Transport">Direct (LiteNetLib) or Steam.</param>
/// <param name="Address">The host to join; null unless <paramref name="Mode"/> is Join.</param>
/// <param name="Seed">The world's seed on a host or offline (a save's or the host's replaces it).</param>
/// <param name="SaveName">The named save a host or offline game starts from and writes, or null.</param>
/// <param name="FromCommandLine">
/// True when the options chose the match (<c>Engine:Session:Mode</c>): a failed start then ends the run with exit code
/// 1, and a client's session end ends it as before the menu existed, so scripts see both.
/// </param>
public sealed record MatchStart(
    SessionMode Mode,
    SessionTransportKind Transport,
    string? Address,
    ulong Seed,
    string? SaveName,
    bool FromCommandLine
)
{
    /// <summary>
    /// The match the options start: their session mode, transport, address, seed, and save name; null for the menu
    /// (<see cref="SessionMode.Menu"/>), which starts none until the player picks.
    /// </summary>
    public static MatchStart? FromOptions(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var session = options.Session;
        if (session.Mode == SessionMode.Menu)
        {
            return null;
        }

        return new MatchStart(
            session.Mode,
            session.Transport,
            session.Mode == SessionMode.Join ? session.Address : null,
            options.Voxels.Seed,
            options.Save.Name,
            FromCommandLine: true
        );
    }
}
