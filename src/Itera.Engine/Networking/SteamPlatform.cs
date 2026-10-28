using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using Steamworks;

namespace Itera.Engine.Networking;

/// <summary>
/// <see cref="ISteamPlatform"/> through Steamworks.NET: started by <see cref="TryStart"/>, which starts the Steam API.
/// Message data is read straight from Steam's message structs, so receiving allocates nothing per message. Steam's
/// networking writes what it reports of its relays and connections into the log, at its "Msg" level and above.
/// </summary>
internal sealed unsafe partial class SteamPlatform : ISteamPlatform, IDisposable
{
    private const int MessagesPerBatch = 64;

    /// <summary>Room for a connection's detailed status, which runs to about 2 KB; more is asked for when it needs it.</summary>
    private const int DetailedStatusBytes = 4096;

    // SteamNetworkingMessage_t starts with void* m_pData, int m_cbSize, and HSteamNetConnection m_conn.
    private static readonly int s_sizeOffset = IntPtr.Size;
    private static readonly int s_connectionOffset = IntPtr.Size + 4;

    private static int s_resolverSet;

    private readonly IntPtr[] _messages = new IntPtr[MessagesPerBatch];

    // Kept for the life of the platform: Steamworks.NET dispatches to them from RunCallbacks.
    private readonly Callback<SteamNetConnectionStatusChangedCallback_t> _connectionChanged;
    private readonly Callback<GameLobbyJoinRequested_t> _lobbyJoinRequested;
    private readonly Callback<GameRichPresenceJoinRequested_t> _presenceJoinRequested;
    private readonly CallResult<LobbyCreated_t> _lobbyCreated;
    private readonly CallResult<LobbyEnter_t> _lobbyEntered;
    private readonly Callback<PersonaStateChange_t> _personaChanged;
    private readonly Callback<LobbyDataUpdate_t> _lobbyDataUpdated;
    private readonly Callback<AvatarImageLoaded_t> _avatarLoaded;

    // Kept for the life of the platform: Steam calls it from its own threads until Dispose clears it.
    private readonly FSteamNetworkingSocketsDebugOutput _debugOutput;
    private readonly ILogger _logger;
    private readonly AppId_t _appId;
    private HSteamNetPollGroup _pollGroup;
    private bool _disposed;

    private SteamPlatform(ILogger logger)
    {
        _logger = logger;
        _debugOutput = OnDebugOutput;
        SteamNetworkingUtils.SetDebugOutputFunction(
            ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Msg,
            _debugOutput
        );
        LocalSteamId = SteamUser.GetSteamID().m_SteamID;
        _connectionChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionChanged);
        _lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
        _presenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnPresenceJoinRequested);
        _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
        _personaChanged = Callback<PersonaStateChange_t>.Create(OnPersonaChanged);
        _lobbyDataUpdated = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
        _avatarLoaded = Callback<AvatarImageLoaded_t>.Create(OnAvatarLoaded);
        _appId = SteamUtils.GetAppID();
        _pollGroup = SteamNetworkingSockets.CreatePollGroup();
        // Fetches the relay network's layout now, so the first connection does not wait for it.
        SteamNetworkingUtils.InitRelayNetworkAccess();
    }

    public ulong LocalSteamId { get; }

    public ISteamPlatformListener? Listener { get; set; }

    public ISteamFriendsListener? FriendsListener { get; set; }

    /// <summary>
    /// Starts the Steam API for <paramref name="appId"/>. Returns null, with the reason, when Steam is not running or
    /// refuses; throws <see cref="DllNotFoundException"/> (or <see cref="BadImageFormatException"/>) when this platform
    /// has no Steam library. When Steam did not start the game, <c>steam_appid.txt</c> next to the game names the app;
    /// the Steam API looks for it in the working directory, which is changed for the call and put back.
    /// <paramref name="logger"/> takes what Steam's networking reports.
    /// </summary>
    public static SteamPlatform? TryStart(uint appId, ILogger logger, out string reason)
    {
        SetResolver();
        var launchedBySteam = Environment.GetEnvironmentVariable("SteamAppId") is not null;
        var workingDirectory = Environment.CurrentDirectory;
        ESteamAPIInitResult result;
        string message;
        try
        {
            if (!launchedBySteam)
            {
                WriteAppIdFile(appId);
                // Put back in the finally below, before anything else runs: relative paths in the options (a map
                // image) are resolved later, against the directory the game was started in.
                Environment.CurrentDirectory = AppContext.BaseDirectory;
            }

            result = SteamAPI.InitEx(out message);
        }
        finally
        {
            Environment.CurrentDirectory = workingDirectory;
        }

        if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
        {
            reason = result switch
            {
                ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient => "the Steam client is not running",
                ESteamAPIInitResult.k_ESteamAPIInitResult_VersionMismatch =>
                    "the Steam client is older than the game's Steam library",
                _ => string.IsNullOrEmpty(message) ? "the Steam API did not start" : message,
            };
            return null;
        }

        reason = "";
        return new SteamPlatform(logger);
    }

    public string PersonaName => SteamFriends.GetPersonaName();

    public int FriendCount => SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);

    public ulong FriendAt(int index) =>
        SteamFriends.GetFriendByIndex(index, EFriendFlags.k_EFriendFlagImmediate).m_SteamID;

    public string FriendName(ulong friend) => SteamFriends.GetFriendPersonaName(new CSteamID(friend));

    public bool IsFriend(ulong steamId) =>
        SteamFriends.HasFriend(new CSteamID(steamId), EFriendFlags.k_EFriendFlagImmediate);

    public bool InLobby(ulong lobby, ulong steamId)
    {
        var id = new CSteamID(lobby);
        var members = SteamMatchmaking.GetNumLobbyMembers(id);
        for (var i = 0; i < members; i++)
        {
            if (SteamMatchmaking.GetLobbyMemberByIndex(id, i).m_SteamID == steamId)
            {
                return true;
            }
        }

        return false;
    }

    public ulong FriendLobby(ulong friend)
    {
        var id = new CSteamID(friend);
        if (
            SteamFriends.GetFriendGamePlayed(id, out var game)
            && game.m_gameID.AppID() == _appId
            && game.m_steamIDLobby.m_SteamID != 0
        )
        {
            return game.m_steamIDLobby.m_SteamID;
        }

        // A host's rich presence names its lobby too ("+connect_lobby ID"), for a lobby Steam does not show as the game's.
        return SteamLaunchArguments.TryReadLobby(SteamFriends.GetFriendRichPresence(id, "connect"), out var lobby)
            ? lobby
            : 0;
    }

    public void RequestLobbyData(ulong lobby) => SteamMatchmaking.RequestLobbyData(new CSteamID(lobby));

    public string LobbyData(ulong lobby, string key) => SteamMatchmaking.GetLobbyData(new CSteamID(lobby), key) ?? "";

    public bool FriendAvatar(ulong friend, out int width, out int height, out byte[]? rgba)
    {
        width = 0;
        height = 0;
        rgba = null;
        var image = SteamFriends.GetMediumFriendAvatar(new CSteamID(friend));
        if (image == -1)
        {
            return false;
        }

        if (
            image == 0
            || !SteamUtils.GetImageSize(image, out var w, out var h)
            || w == 0
            || h == 0
            || w > 512
            || h > 512
        )
        {
            return true;
        }

        var pixels = new byte[(int)(w * h * 4)];
        if (!SteamUtils.GetImageRGBA(image, pixels, pixels.Length))
        {
            return true;
        }

        width = (int)w;
        height = (int)h;
        rgba = pixels;
        return true;
    }

    public void RunCallbacks() => SteamAPI.RunCallbacks();

    public uint CreateListenSocket() => SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null).m_HSteamListenSocket;

    public void CloseListenSocket(uint socket) =>
        SteamNetworkingSockets.CloseListenSocket(new HSteamListenSocket(socket));

    public uint ConnectP2P(ulong steamId)
    {
        var identity = default(SteamNetworkingIdentity);
        identity.SetSteamID64(steamId);
        return SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null).m_HSteamNetConnection;
    }

    public bool AcceptConnection(uint connection) =>
        SteamNetworkingSockets.AcceptConnection(new HSteamNetConnection(connection)) == EResult.k_EResultOK;

    public void CloseConnection(uint connection, int reason, string? debug, bool linger) =>
        SteamNetworkingSockets.CloseConnection(new HSteamNetConnection(connection), reason, debug, linger);

    public void Watch(uint connection) =>
        SteamNetworkingSockets.SetConnectionPollGroup(new HSteamNetConnection(connection), _pollGroup);

    public SteamSendResult Send(uint connection, ReadOnlySpan<byte> data, bool reliable, out int code)
    {
        var flags = reliable
            ? Constants.k_nSteamNetworkingSend_Reliable
            : Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
        fixed (byte* bytes = data)
        {
            var result = SteamNetworkingSockets.SendMessageToConnection(
                new HSteamNetConnection(connection),
                (IntPtr)bytes,
                (uint)data.Length,
                flags,
                out _
            );
            code = (int)result;
            return result switch
            {
                EResult.k_EResultOK => SteamSendResult.Sent,
                EResult.k_EResultLimitExceeded => SteamSendResult.QueueFull,
                _ => SteamSendResult.Refused,
            };
        }
    }

    public void ReceiveMessages(SteamMessageHandler handler)
    {
        int count;
        do
        {
            count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, _messages, MessagesPerBatch);
            for (var i = 0; i < count; i++)
            {
                var message = _messages[i];
                try
                {
                    var data = Marshal.ReadIntPtr(message);
                    var size = Marshal.ReadInt32(message, s_sizeOffset);
                    var connection = (uint)Marshal.ReadInt32(message, s_connectionOffset);
                    handler(connection, new ReadOnlySpan<byte>((void*)data, size));
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(message);
                }
            }
        } while (count == MessagesPerBatch);
    }

    public int RoundTripMilliseconds(uint connection)
    {
        var status = default(SteamNetConnectionRealTimeStatus_t);
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(
            new HSteamNetConnection(connection),
            ref status,
            0,
            ref lanes
        );
        return result == EResult.k_EResultOK ? status.m_nPing : -1;
    }

    public bool TryGetStatus(uint connection, out ConnectionStatus status)
    {
        var handle = new HSteamNetConnection(connection);
        var now = default(SteamNetConnectionRealTimeStatus_t);
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        var info = default(SteamNetConnectionInfo_t);
        if (
            SteamNetworkingSockets.GetConnectionRealTimeStatus(handle, ref now, 0, ref lanes) != EResult.k_EResultOK
            || !SteamNetworkingSockets.GetConnectionInfo(handle, out info)
        )
        {
            status = default;
            return false;
        }

        var relayed = (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0;
        var relay = PopCode(info.m_idPOPRelay.m_SteamNetworkingPOPID);
        var route =
            !relayed ? "direct"
            : relay.Length > 0 ? "relayed through " + relay
            : "relayed";
        status = new ConnectionStatus(
            now.m_nPing,
            now.m_flConnectionQualityLocal,
            now.m_flConnectionQualityRemote,
            now.m_flOutPacketsPerSec,
            now.m_flOutBytesPerSec,
            now.m_flInPacketsPerSec,
            now.m_flInBytesPerSec,
            now.m_nSendRateBytesPerSecond,
            now.m_cbPendingReliable,
            now.m_cbPendingUnreliable,
            now.m_cbSentUnackedReliable,
            now.m_usecQueueTime.m_SteamNetworkingMicroseconds,
            route
        );
        return true;
    }

    public string DetailedStatus(uint connection)
    {
        var handle = new HSteamNetConnection(connection);
        var size = DetailedStatusBytes;
        // A positive result is the size the text needs: once more with that.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = SteamNetworkingSockets.GetDetailedConnectionStatus(handle, out var text, size);
            if (result < 0)
            {
                return "";
            }

            if (result == 0)
            {
                return OneLine(text ?? "");
            }

            size = result;
        }

        return "";
    }

    public int QueuedBytes(uint connection)
    {
        var status = default(SteamNetConnectionRealTimeStatus_t);
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(
            new HSteamNetConnection(connection),
            ref status,
            0,
            ref lanes
        );
        return result == EResult.k_EResultOK ? status.m_cbPendingReliable + status.m_cbPendingUnreliable : -1;
    }

    public void CreateLobby(int maxMembers) =>
        _lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxMembers));

    public void JoinLobby(ulong lobby) => _lobbyEntered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby)));

    public void LeaveLobby(ulong lobby) => SteamMatchmaking.LeaveLobby(new CSteamID(lobby));

    public void SetLobbyData(ulong lobby, string key, string value) =>
        SteamMatchmaking.SetLobbyData(new CSteamID(lobby), key, value);

    public void SetRichPresence(string? key, string? value)
    {
        if (key is null)
        {
            SteamFriends.ClearRichPresence();
        }
        else
        {
            SteamFriends.SetRichPresence(key, value);
        }
    }

    /// <summary>Destroys the poll group and shuts the Steam API down. Safe to call again.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connectionChanged.Dispose();
        _lobbyJoinRequested.Dispose();
        _presenceJoinRequested.Dispose();
        _lobbyCreated.Dispose();
        _lobbyEntered.Dispose();
        _personaChanged.Dispose();
        _lobbyDataUpdated.Dispose();
        _avatarLoaded.Dispose();
        if (_pollGroup != HSteamNetPollGroup.Invalid)
        {
            SteamNetworkingSockets.DestroyPollGroup(_pollGroup);
            _pollGroup = HSteamNetPollGroup.Invalid;
        }

        SteamNetworkingUtils.SetDebugOutputFunction(
            ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_None,
            null
        );
        SteamAPI.Shutdown();
    }

    /// <summary>
    /// What Steam's networking reports, on any thread (Steam may hold its lock): its bugs and errors at Error, its
    /// warnings at Warning, its important messages at Information, and the rest at Debug. Nothing may throw into Steam.
    /// </summary>
    private void OnDebugOutput(ESteamNetworkingSocketsDebugOutputType type, StringBuilder message)
    {
        try
        {
            var text = OneLine(message.ToString());
            switch (type)
            {
                case ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Bug:
                case ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Error:
                    LogSteamError(text);
                    break;
                case ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Important:
                    LogSteamImportant(text);
                    break;
                case ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Warning:
                    LogSteamWarning(text);
                    break;
                default:
                    LogSteamMessage(text);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Dropped: an exception into Steam's native code would end the process.
        }
    }

    /// <summary>Steam's text with its lines trimmed and joined by "; ", so it stays one line of the log.</summary>
    private static string OneLine(string text) =>
        LogText.Printable(
            string.Join("; ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        );

    /// <summary>A relay's code ("iad"): Steam packs its letters as bytes 2, 1, and 0 of the id, and any fourth as byte 3.</summary>
    private static string PopCode(uint id)
    {
        if (id == 0)
        {
            return "";
        }

        Span<char> code = [(char)((id >> 16) & 0xFF), (char)((id >> 8) & 0xFF), (char)(id & 0xFF), (char)(id >> 24)];
        return LogText.Printable(new string(code[..(code[3] == 0 ? 3 : 4)]));
    }

    /// <summary>
    /// Loads Steam's library from next to the game. Steamworks.NET's import names only <c>steam_api</c> (or
    /// <c>steam_api64</c>), which the default search does not look for in the game's folder on Linux.
    /// </summary>
    private static void SetResolver()
    {
        if (Interlocked.Exchange(ref s_resolverSet, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(
            typeof(SteamAPI).Assembly,
            static (name, assembly, searchPath) =>
            {
                if (!name.StartsWith("steam_api", StringComparison.Ordinal))
                {
                    return IntPtr.Zero;
                }

                var file =
                    OperatingSystem.IsWindows() ? "steam_api64.dll"
                    : OperatingSystem.IsMacOS() ? "libsteam_api.dylib"
                    : "libsteam_api.so";
                var path = Path.Combine(AppContext.BaseDirectory, file);
                return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
            }
        );
    }

    private static void WriteAppIdFile(uint appId)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
        var text = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            if (!File.Exists(path) || !string.Equals(File.ReadAllText(path).Trim(), text, StringComparison.Ordinal))
            {
                File.WriteAllText(path, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file the Steam API refuses to start, which TryStart reports.
        }
    }

    private void OnConnectionChanged(SteamNetConnectionStatusChangedCallback_t change)
    {
        var info = change.m_info;
        var state = info.m_eState switch
        {
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected =>
                SteamConnectionState.Connected,
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer =>
                SteamConnectionState.ClosedByPeer,
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally =>
                SteamConnectionState.Problem,
            // Reported once for a new connection; the transport accepts an incoming one then. FindingRoute follows the
            // accept and needs nothing (accepting again fails).
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting =>
                SteamConnectionState.Connecting,
            // None follows a connection this side closed, and FindingRoute an accepted one; nothing to report.
            _ => (SteamConnectionState)(-1),
        };
        if (state < 0)
        {
            return;
        }

        Listener?.OnConnectionChanged(
            change.m_hConn.m_HSteamNetConnection,
            info.m_identityRemote.GetSteamID64(),
            state,
            info.m_hListenSocket != HSteamListenSocket.Invalid,
            info.m_eEndReason,
            info.m_szEndDebug ?? ""
        );
    }

    private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
    {
        var succeeded = !ioFailure && result.m_eResult == EResult.k_EResultOK;
        if (Listener is null)
        {
            // Its transport closed before Steam answered: nobody would leave it.
            LeaveIfMade(result.m_ulSteamIDLobby, succeeded);
            return;
        }

        Listener.OnLobbyCreated(result.m_ulSteamIDLobby, succeeded);
    }

    private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
    {
        var succeeded =
            !ioFailure
            && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
        if (Listener is null)
        {
            LeaveIfMade(result.m_ulSteamIDLobby, succeeded);
            return;
        }

        var owner = succeeded ? SteamMatchmaking.GetLobbyOwner(new CSteamID(result.m_ulSteamIDLobby)).m_SteamID : 0;
        Listener.OnLobbyEntered(result.m_ulSteamIDLobby, owner, succeeded);
    }

    private void LeaveIfMade(ulong lobby, bool succeeded)
    {
        if (succeeded && lobby != 0)
        {
            LeaveLobby(lobby);
        }
    }

    private void OnLobbyJoinRequested(GameLobbyJoinRequested_t request) =>
        FriendsListener?.OnJoinRequested(
            request.m_steamIDFriend.m_SteamID,
            SteamAddress.LobbyPrefix + request.m_steamIDLobby.m_SteamID.ToString(CultureInfo.InvariantCulture)
        );

    /// <summary>Rich presence's connect string is "+connect_lobby ID" (what the host sets): read as lobby:ID.</summary>
    private void OnPresenceJoinRequested(GameRichPresenceJoinRequested_t request) =>
        FriendsListener?.OnJoinRequested(
            request.m_steamIDFriend.m_SteamID,
            SteamLaunchArguments.TryReadLobby(request.m_rgchConnect, out var lobby)
                ? SteamAddress.LobbyPrefix + lobby.ToString(CultureInfo.InvariantCulture)
                : ""
        );

    private void OnPersonaChanged(PersonaStateChange_t change)
    {
        const EPersonaChange watched =
            EPersonaChange.k_EPersonaChangeName
            | EPersonaChange.k_EPersonaChangeGamePlayed
            | EPersonaChange.k_EPersonaChangeRichPresence
            | EPersonaChange.k_EPersonaChangeComeOnline
            | EPersonaChange.k_EPersonaChangeGoneOffline;
        if ((change.m_nChangeFlags & watched) != 0)
        {
            FriendsListener?.OnFriendChanged(change.m_ulSteamID);
        }
    }

    private void OnLobbyDataUpdated(LobbyDataUpdate_t update) => FriendsListener?.OnLobbyData(update.m_ulSteamIDLobby);

    private void OnAvatarLoaded(AvatarImageLoaded_t loaded) =>
        FriendsListener?.OnAvatarLoaded(loaded.m_steamID.m_SteamID);

    [LoggerMessage(Level = LogLevel.Error, Message = "Steam networking: {Text}")]
    private partial void LogSteamError(string text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steam networking: {Text}")]
    private partial void LogSteamWarning(string text);

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam networking: {Text}")]
    private partial void LogSteamImportant(string text);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Steam networking: {Text}")]
    private partial void LogSteamMessage(string text);
}
