namespace Itera.Engine.Networking;

/// <summary>
/// Turns the arguments Steam adds when a friend joins through Steam into session options. A host sets its rich
/// presence's <c>connect</c> to <c>+connect_lobby ID</c>; when a friend picks "Join game" and Itera is not running,
/// Steam starts it with those two arguments, which configuration would ignore. They become the menu with the lobby as
/// its address: the menu joins it at once, and a lobby that is gone leaves the player in the menu with the reason.
/// </summary>
public static class SteamLaunchArguments
{
    public const string ConnectLobby = "+connect_lobby";

    /// <summary>
    /// The lobby in a connect string Steam passes ("+connect_lobby ID", what a host's rich presence holds); false for
    /// anything else.
    /// </summary>
    public static bool TryReadLobby(string? connect, out ulong lobby)
    {
        lobby = 0;
        if (connect is null || !connect.StartsWith(ConnectLobby + " ", StringComparison.Ordinal))
        {
            return false;
        }

        return ulong.TryParse(
                connect.AsSpan(ConnectLobby.Length + 1).Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out lobby
            )
            && lobby != 0;
    }

    /// <summary>
    /// <paramref name="args"/> with <c>+connect_lobby ID</c> replaced by the options that start the menu joining that
    /// lobby through Steam.
    /// Returns the arguments unchanged when there is none.
    /// </summary>
    public static string[] Translate(string[] args)
    {
        var index = Array.IndexOf(args, ConnectLobby);
        if (index < 0 || index == args.Length - 1)
        {
            return args;
        }

        var lobby = args[index + 1];
        var translated = new List<string>(args.Length + 1);
        translated.AddRange(args.AsSpan(0, index));
        translated.AddRange(args.AsSpan(index + 2));
        translated.Add("--Engine:Session:Mode=Menu");
        translated.Add("--Engine:Session:Transport=Steam");
        translated.Add("--Engine:Session:Address=" + SteamAddress.LobbyPrefix + lobby);
        return [.. translated];
    }
}
