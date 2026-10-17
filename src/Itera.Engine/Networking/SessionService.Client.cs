using System.Diagnostics;
using System.Runtime.InteropServices;
using Itera.Engine.Settings;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Networking;

/// <summary>A client's side: connecting, the welcome, the download, and the host's edits.</summary>
public sealed partial class SessionService
{
    private const double ProgressIntervalSeconds = 1;

    private readonly List<byte[]> _early = [];
    private MemoryStream? _payload;
    private string _joinAddress = "";
    private double _joinStart;
    private double _joinDeadline;
    private double _nextProgress;
    private string? _joinFailure;
    private JoinState _join;
    private int _host = -1;
    private long _payloadBytes;
    private long _snapshotEdits;
    private string? _refusal;

    private enum JoinState
    {
        Connecting,
        Welcoming,
        Downloading,
        Downloaded,
        Loaded,
    }

    /// <summary>Starts connecting to the host; <see cref="PollJoin"/> carries the join on.</summary>
    private void StartJoin()
    {
        _joinAddress = _settings.Address ?? "";
        _joinStart = _clock();
        _joinDeadline = _joinStart + _settings.ConnectTimeoutSeconds;
        _nextProgress = _joinStart + ProgressIntervalSeconds;
        try
        {
            _transport.Connect(_joinAddress);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new SessionStartException($"Could not join {_joinAddress}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Carries a client's join on: takes what arrived from the host and says how far the join is, or why it failed
    /// (no answer or no welcome within the connect timeout, no data from the host for that long while the world
    /// downloads, a refusal, a world this game cannot accept). Once a step
    /// of the main loop until it says <see cref="JoinStage.Joined"/> (the host's world is downloaded; its world step
    /// loads it) or fails. On a host or offline, joined at once. A failed join stays failed; ending the match closes it.
    /// </summary>
    public JoinProgress PollJoin()
    {
        if (Role != SessionRole.Client || _join >= JoinState.Downloaded)
        {
            return new JoinProgress(JoinStage.Joined, _payloadBytes, _payloadBytes, null);
        }

        if (_joinFailure is not null)
        {
            return Failed();
        }

        _transport.Poll(this);
        if (_refusal is not null)
        {
            _joinFailure = $"Could not join {_joinAddress}: {_refusal}.";
            return Failed();
        }

        if (_join == JoinState.Downloaded)
        {
            var seed = Match.Seed;
            var megabytes = Megabytes(_payloadBytes);
            var milliseconds = (_clock() - _joinStart) * 1000;
            LogJoined(_joinAddress, Slot, seed, _snapshotEdits, megabytes, milliseconds);
            return new JoinProgress(JoinStage.Joined, _payloadBytes, _payloadBytes, null);
        }

        var now = _clock();
        if (_join < JoinState.Downloaded && now > _joinDeadline)
        {
            var stage = _join switch
            {
                JoinState.Connecting => "no answer",
                JoinState.Welcoming => "no welcome",
                _ => "no data from the host",
            };
            _joinFailure = $"Could not join {_joinAddress}: {stage} within {_settings.ConnectTimeoutSeconds} s.";
            return Failed();
        }

        var downloaded = _payload?.Length ?? 0;
        if (_join == JoinState.Downloading && now >= _nextProgress)
        {
            var done = Megabytes(downloaded);
            var total = Megabytes(_payloadBytes);
            LogDownloading(done, total);
            _nextProgress = now + ProgressIntervalSeconds;
        }

        var current = _join switch
        {
            JoinState.Connecting => JoinStage.Connecting,
            JoinState.Welcoming => JoinStage.Welcoming,
            _ => JoinStage.Downloading,
        };
        return new JoinProgress(current, downloaded, _payloadBytes, null);
    }

    /// <summary>The address this client joins, as given.</summary>
    public string JoinAddress => _joinAddress;

    private JoinProgress Failed() => new(JoinStage.Failed, _payload?.Length ?? 0, _payloadBytes, _joinFailure);

    private void OnConnectedToHost(int peer)
    {
        _host = peer;
        _join = JoinState.Welcoming;
        _writer.Reset();
        _writer.WriteByte((byte)MessageKind.Hello);
        _writer.WriteInt(SessionProtocol.Version);
        _writer.WriteString(_identity.Id);
        _writer.WriteInt(_identity.Version);
        _writer.WriteInt(_fixedStepHz);
        _writer.WriteString(_playerName);
        _writer.WriteULong(_replication?.KindsFingerprint ?? 0);
        Send(peer);
    }

    private void OnDisconnectedFromHost(TransportDisconnect reason, ReadOnlySpan<byte> data)
    {
        _host = -1;
        var said = SessionProtocol.TryReadDisconnect(data, out var kind, out _, out var text);
        if (_join < JoinState.Downloaded)
        {
            _refusal ??= said
                ? kind == DisconnectKind.Reject
                    ? "the host refused: " + text
                    : "the host closed the connection: " + text
                : reason switch
                {
                    TransportDisconnect.ConnectFailed => "no host answered",
                    TransportDisconnect.TimedOut => "the connection timed out",
                    _ => "the connection closed (" + reason + ")",
                };
            return;
        }

        if (said && kind == DisconnectKind.Bye)
        {
            EndSession(SessionEnd.HostLeft, "the host left: " + text);
        }
        else if (said && kind == DisconnectKind.ProtocolError)
        {
            EndSession(SessionEnd.Error, "the host found an error: " + text);
        }
        else
        {
            EndSession(SessionEnd.Lost, "the connection to the host was lost (" + reason + ")");
        }
    }

    private void ReceiveAsClient(ReadOnlySpan<byte> data)
    {
        if (_join == JoinState.Downloaded)
        {
            // The world does not exist yet: keep what arrived after the payload until it does (LoadDownload).
            _early.Add(data.ToArray());
            return;
        }

        var reader = new MessageReader(data);
        var kind = (MessageKind)reader.ReadByte();
        if (
            _join is JoinState.Welcoming or JoinState.Downloading
            && ((byte)kind >= FirstGameKind || kind == MessageKind.Snapshot)
        )
        {
            // The host sends game messages and snapshots only once the payload is queued, so one this early came on a
            // channel that may overtake the reliable download, and the host sends its like again: drop it (it would fail
            // the join as a protocol error).
            return;
        }

        switch (_join, kind)
        {
            case (JoinState.Welcoming, MessageKind.Welcome):
                ReadWelcome(ref reader);
                // The download's deadline is a stall's: no data from the host for the connect timeout.
                _joinDeadline = _clock() + _settings.ConnectTimeoutSeconds;
                break;
            case (JoinState.Downloading, MessageKind.JoinChunk):
                var chunk = reader.ReadRest();
                if (_payload!.Length + chunk.Length > _payloadBytes)
                {
                    throw new InvalidDataException("The join payload is longer than the host announced.");
                }

                _payload.Write(chunk);
                _joinDeadline = _clock() + _settings.ConnectTimeoutSeconds;
                break;
            case (JoinState.Downloading, MessageKind.JoinDone):
                reader.EnsureEnd();
                if (_payload!.Length != _payloadBytes)
                {
                    throw new InvalidDataException("The join payload is shorter than the host announced.");
                }

                _join = JoinState.Downloaded;
                break;
            case (JoinState.Loaded, MessageKind.Edit):
                var edit = SessionProtocol.ReadEdit(ref reader);
                var maker = SessionProtocol.ReadMaker(ref reader);
                reader.EnsureEnd();
                (_voxels ?? throw NoWorldForEdits()).ApplySequenced(in edit, maker);
                break;
            case (JoinState.Loaded, MessageKind.EditWithPiece):
                ReadLift(ref reader);
                break;
            case (JoinState.Loaded, MessageKind.Roster):
                ReadRoster(ref reader);
                break;
            case (JoinState.Loaded, MessageKind.Tune):
                ReadTuning(ref reader);
                break;
            case (JoinState.Loaded, MessageKind.Snapshot):
                (
                    _replication ?? throw new InvalidDataException("A snapshot came to a game that replicates nothing.")
                ).ReceiveSnapshot(reader.ReadRest());
                break;
            case (JoinState.Loaded, _) when (byte)kind >= FirstGameKind:
                ReceiveGame(0, (byte)kind, reader.ReadRest());
                break;
            default:
                throw new InvalidDataException($"Unexpected message {(byte)kind} while {_join}.");
        }
    }

    private void ReadWelcome(ref MessageReader reader)
    {
        var slot = reader.ReadByte();
        var match = SessionProtocol.ReadMatch(ref reader);
        var payloadBytes = reader.ReadLong();
        var snapshotEdits = reader.ReadLong();
        reader.EnsureEnd();
        if (
            !string.Equals(match.Identity, _identity.Id, StringComparison.Ordinal)
            || match.IdentityVersion != _identity.Version
            || match.FixedStepHz != _fixedStepHz
        )
        {
            throw new InvalidDataException("The host's match does not fit this game (its identity or step rate).");
        }

        // A slot the host could not give (0 is its own, and there are MaxSlots) is refused: the game indexes its arrays of
        // players by it, and slot 16 once crashed the client's run.
        var limit = (long)_settings.MaxJoinMegabytes << 20;
        if (
            payloadBytes < 0
            || payloadBytes > limit
            || snapshotEdits < 0
            || slot == 0
            || slot >= SessionProtocol.MaxSlots
        )
        {
            _refusal =
                payloadBytes > limit
                    ? $"the host's world is {Megabytes(payloadBytes)} MB; Engine:Session:MaxJoinMegabytes allows {_settings.MaxJoinMegabytes}"
                    : "the host's welcome is not valid";
            SessionProtocol.WriteDisconnect(_disconnectWriter, DisconnectKind.Bye, 0, "the client refused the world");
            _transport.Disconnect(_host, _disconnectWriter.Written);
            return;
        }

        _match = match;
        Slot = slot;
        _payloadBytes = payloadBytes;
        _snapshotEdits = snapshotEdits;
        _payload = new MemoryStream((int)payloadBytes);
        _join = JoinState.Downloading;
    }

    private void ReadLift(ref MessageReader reader)
    {
        var edit = SessionProtocol.ReadEdit(ref reader);
        var maker = SessionProtocol.ReadMaker(ref reader);
        var count = reader.ReadInt();
        // Every run holds at least one voxel of a box at most a chunk on each side, and takes at least two bytes.
        if (count < 1 || count > reader.Remaining / 2 || count > VoxelConstants.ChunkVoxels)
        {
            throw new InvalidDataException($"A lift claims {count} runs.");
        }

        _runs.Clear();
        for (var i = 0; i < count; i++)
        {
            _runs.Add((reader.ReadByte(), reader.ReadVarInt()));
        }

        reader.EnsureEnd();
        (_voxels ?? throw NoWorldForEdits()).ApplyHostLift(in edit, CollectionsMarshal.AsSpan(_runs), maker);
    }

    /// <summary>A world-less host's payload: its header, for the match's identity, and no edits. Returns 0.</summary>
    private long ReadEmptyPayload(Stream payload)
    {
        var edits = new List<VoxelEdit>();
        EditLogSerializer.Read(payload, _identity.Id, _identity.Version, edits, []);
        return edits.Count == 0
            ? 0
            : throw new InvalidDataException($"The host sent {edits.Count} voxel edit(s) to a game without a world.");
    }

    private static InvalidDataException NoWorldForEdits() =>
        new("The host sent a voxel edit to a game without a world.");

    /// <summary>
    /// The host's match tunables: each by name, set where this game has one of that name and the value is in its range
    /// (a host of another build may have others); a count past any run's is refused.
    /// </summary>
    private void ReadTuning(ref MessageReader reader)
    {
        var count = reader.ReadVarInt();
        if (count is < 1 or > Tunables.MostTunables)
        {
            throw new InvalidDataException($"A tuning message holds {count} tunables.");
        }

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var value = reader.ReadFloat();
            if (_tunables?.Find(name) is { Scope: TuningScope.Match } tunable && !_tunables.TrySet(tunable, value))
            {
                LogTuningRefused(name, value);
            }
        }

        reader.EnsureEnd();
    }

    private void ReadRoster(ref MessageReader reader)
    {
        var count = reader.ReadByte();
        if (count < 1 || count > SessionProtocol.MaxSlots)
        {
            throw new InvalidDataException($"A roster of {count} slots.");
        }

        var players = new SessionPlayer[count];
        for (var slot = 0; slot < count; slot++)
        {
            var present = reader.ReadByte() != 0;
            var name = SessionProtocol.Cut(reader.ReadString(), SessionProtocol.MaxNameLength * 4);
            var join = reader.ReadVarInt();
            if (present ? join <= 0 : join != 0)
            {
                throw new InvalidDataException($"A roster slot with join {join}.");
            }

            players[slot] = new SessionPlayer(present, name, join);
        }

        reader.EnsureEnd();
        _players = players;
        RosterVersion++;
    }

    /// <summary>
    /// Loads the payload into the world, applies what came after it, and makes the world follow the host. Without a world
    /// the payload is read for its header alone, and it must hold no edits.
    /// </summary>
    private void LoadDownload()
    {
        var start = Stopwatch.GetTimestamp();
        long edits;
        try
        {
            _payload!.Position = 0;
            edits = _voxels?.LoadEdits(_payload) ?? ReadEmptyPayload(_payload);
        }
        catch (InvalidDataException ex)
        {
            throw new SessionStartException("The host's world could not be loaded: " + ex.Message, ex);
        }

        _payload = null;
        _voxels?.FollowHost();
        _join = JoinState.Loaded;
        var loaded = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        foreach (var message in _early)
        {
            OnReceive(_host, message);
        }

        _early.Clear();
        LogLoaded(edits, loaded);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Session: the host's tunable {Name} = {Value} is out of this game's range; it is left as it was"
    )]
    private partial void LogTuningRefused(string name, float value);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Session: downloading the host's world, {Done} of {Total} MB"
    )]
    private partial void LogDownloading(string done, string total);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Session: joined {Address} in slot {Slot}, seed {Seed}; downloaded {Edits} edit(s) ({Megabytes} MB) in {Ms:F0} ms"
    )]
    private partial void LogJoined(string address, int slot, ulong seed, long edits, string megabytes, double ms);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Session: loaded {Edits} edit(s) of the host's world in {Ms:F1} ms"
    )]
    private partial void LogLoaded(long edits, double ms);
}

/// <summary>How far a client's join is (<see cref="SessionService.PollJoin"/>).</summary>
public enum JoinStage
{
    /// <summary>Connecting to the host.</summary>
    Connecting,

    /// <summary>Connected; waiting for the host's welcome.</summary>
    Welcoming,

    /// <summary>Downloading the host's world.</summary>
    Downloading,

    /// <summary>The host's world is here (or this game is no client): the match's world may start.</summary>
    Joined,

    /// <summary>The join failed; <see cref="JoinProgress.Failure"/> says why.</summary>
    Failed,
}

/// <summary>A client's join as <see cref="SessionService.PollJoin"/> last found it.</summary>
/// <param name="Stage">How far it is.</param>
/// <param name="DownloadedBytes">Bytes of the host's world downloaded so far.</param>
/// <param name="TotalBytes">Bytes the host announced (0 before its welcome).</param>
/// <param name="Failure">Why it failed, for the player and the log; null unless <paramref name="Stage"/> is Failed.</param>
public readonly record struct JoinProgress(JoinStage Stage, long DownloadedBytes, long TotalBytes, string? Failure);
