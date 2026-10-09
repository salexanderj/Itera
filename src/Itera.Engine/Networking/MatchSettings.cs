namespace Itera.Engine.Networking;

/// <summary>
/// What every peer of a session must share to see the same world and run the same simulation: the seed, the fixed step
/// rate, and the generator that builds the world from the seed. The host's apply; a client takes them before its world
/// starts.
/// </summary>
/// <param name="Seed">The world seed.</param>
/// <param name="FixedStepHz">Fixed simulation steps per second.</param>
/// <param name="GeneratorId">Id of the world generator (<c>IChunkGenerator.Id</c>).</param>
/// <param name="GeneratorVersion">Its version: another builds other voxels from the seed.</param>
/// <param name="Game">
/// The game's own match data (its rules), written by the host's <see cref="ISessionMatchData"/>; empty when the game
/// registers none. At most <see cref="SessionProtocol.MaxMatchGameBytes"/> bytes.
/// </param>
public readonly record struct MatchSettings(
    ulong Seed,
    int FixedStepHz,
    string GeneratorId,
    int GeneratorVersion,
    byte[]? Game = null
);

/// <summary>
/// Writes the game's own match data (its rules) into the welcome, on the host and offline, so a client knows them
/// before its systems are built. Registered by the game; optional.
/// </summary>
public interface ISessionMatchData
{
    void Write(MessageWriter writer);
}

/// <summary>Which way a game message may travel.</summary>
public enum SessionMessageDirection
{
    /// <summary>From the host to its clients only: what the host owns (its characters, bodies, corrections).</summary>
    ToClients,

    /// <summary>From a client to the host only: a client's requests.</summary>
    ToHost,

    /// <summary>Either way: each game's own state, which a host also relays.</summary>
    Both,
}

/// <summary>
/// Receives the game's messages of one kind (<see cref="SessionService.FirstGameKind"/> to 255). Registered in DI, so
/// every handler exists before the session step: a client replays what arrived during its download while it loads
/// the joined world, before the ECS exists. A handler only stores what it gets, for the systems to read. Main thread.
/// </summary>
public interface ISessionGameHandler
{
    /// <summary>The message kind it receives.</summary>
    byte Kind { get; }

    /// <summary>
    /// Which way its messages may travel. The session drops a client that sends the host a message meant for clients,
    /// and a client ends its session when the host sends one meant for the host; the handler never sees either.
    /// </summary>
    SessionMessageDirection Direction { get; }

    /// <summary>
    /// A message from <paramref name="slot"/> (on a client, always the host's slot 0); <paramref name="body"/> is the
    /// message after its kind byte, valid only during the call. Throws <see cref="InvalidDataException"/> for a
    /// message it cannot accept, which drops that peer (on a client, ends the session). Any other exception it throws
    /// is logged at Error and does the same, so a peer's bytes never end the game.
    /// </summary>
    void Receive(int slot, ReadOnlySpan<byte> body);
}

/// <summary>
/// A slot of a session: whether a player holds it, their name, and the join that gave it to them: a number the host
/// gives each join, from 1 (its own player's), and 0 for a slot no player holds. A player who leaves and another who
/// takes the slot before the next step have different joins (<see cref="SlotJoins"/>: otherwise the
/// newcomer inherits the leaver's states, sheet, and place).
/// </summary>
public readonly record struct SessionPlayer(bool Present, string Name, int Join);

/// <summary>
/// Which slots' players changed since a system last looked. Each system keeps its own, so a system that runs before
/// or after another sees the same changes: a slot freed and taken again between two of its updates reads as a change,
/// where a slot's <see cref="SessionPlayer.Present"/> alone would not.
/// </summary>
public sealed class SlotJoins
{
    private readonly int[] _seen = new int[SessionService.MaxSlots];

    /// <summary>
    /// True when <paramref name="slot"/>'s player is not the one seen at the last call for it: it left, joined, or was
    /// replaced. Remembers the present one. Allocates nothing.
    /// </summary>
    public bool Changed(ReadOnlySpan<SessionPlayer> players, int slot) => Changed(players, slot, out _);

    /// <summary>
    /// As <see cref="Changed(ReadOnlySpan{SessionPlayer}, int)"/>, with the join seen before (0 when the slot was
    /// empty): a change from a join that is not 0 means a player left.
    /// </summary>
    public bool Changed(ReadOnlySpan<SessionPlayer> players, int slot, out int previous)
    {
        previous = _seen[slot];
        var join = slot < players.Length && players[slot].Present ? players[slot].Join : 0;
        if (previous == join)
        {
            return false;
        }

        _seen[slot] = join;
        return true;
    }
}
