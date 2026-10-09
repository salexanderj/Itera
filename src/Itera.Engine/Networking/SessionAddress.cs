using System.Globalization;

namespace Itera.Engine.Networking;

/// <summary>Reads a host's address as <c>host:port</c>: a name, an IPv4 address, or <c>[IPv6]:port</c>.</summary>
public static class SessionAddress
{
    /// <summary>Splits <paramref name="text"/> into a host and a port from 1 to 65535. Returns false when it is not one.</summary>
    public static bool TryParse(string? text, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        var name = text[..colon];
        if (name.StartsWith('['))
        {
            if (!name.EndsWith(']') || name.Length < 3)
            {
                return false;
            }

            name = name[1..^1];
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            // An IPv6 address without brackets: its last colon is not the port's.
            return false;
        }

        if (
            string.IsNullOrWhiteSpace(name)
            || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 65_535
        )
        {
            return false;
        }

        host = name;
        port = number;
        return true;
    }
}
