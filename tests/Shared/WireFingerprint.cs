using System.Security.Cryptography;
using Itera.Engine.Networking;

namespace Itera.TestSupport;

/// <summary>
/// A fingerprint of the bytes a session sends, pinned with <see cref="SessionProtocol.Version"/>
/// (three wire changes once landed after the bump they belonged to, so one protocol number
/// covered two wires and a peer of each would join and then misread). A change to the bytes fails until the version is
/// raised and both are pinned again.
/// </summary>
internal static class WireFingerprint
{
    /// <summary>The first 16 hex digits of a SHA-256 over each part, each behind its length.</summary>
    public static string Of(IEnumerable<byte[]> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, part.Length);
            hash.AppendData(length);
            hash.AppendData(part);
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }

    /// <summary>
    /// Passes when the protocol is <paramref name="version"/> and the bytes give <paramref name="fingerprint"/>; otherwise
    /// says what to do.
    /// </summary>
    public static void AssertPinned(int version, string fingerprint, string actual)
    {
        if (SessionProtocol.Version == version && actual == fingerprint)
        {
            return;
        }

        Assert.Fail(
            SessionProtocol.Version == version
                ? $"The wire changed (fingerprint {actual}, pinned {fingerprint}) while SessionProtocol.Version is still {version}: raise it in the same commit, then pin the new version and fingerprint here."
                : $"SessionProtocol.Version is {SessionProtocol.Version}: pin it here with the fingerprint {actual}."
        );
    }
}
