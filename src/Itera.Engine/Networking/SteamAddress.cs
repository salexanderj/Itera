using System.Globalization;

namespace Itera.Engine.Networking;

/// <summary>What a Steam address names.</summary>
public enum SteamAddressKind
{
    /// <summary>A lobby: join it, then connect to its owner.</summary>
    Lobby,

    /// <summary>A player: connect to them directly.</summary>
    Player,
}

/// <summary>Reads a host's address for Steam: <c>lobby:ID</c> or <c>steam:ID</c>, the ID a 64-bit Steam id.</summary>
public static class SteamAddress
{
    public const string LobbyPrefix = "lobby:";
    public const string PlayerPrefix = "steam:";

    public static bool TryParse(string? text, out SteamAddressKind kind, out ulong id)
    {
        kind = SteamAddressKind.Lobby;
        id = 0;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> digits;
        if (text.StartsWith(LobbyPrefix, StringComparison.Ordinal))
        {
            digits = text.AsSpan(LobbyPrefix.Length);
        }
        else if (text.StartsWith(PlayerPrefix, StringComparison.Ordinal))
        {
            kind = SteamAddressKind.Player;
            digits = text.AsSpan(PlayerPrefix.Length);
        }
        else
        {
            return false;
        }

        return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }
}
