using System.Diagnostics;
using Itera.Engine.Settings;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Networking;

/// <summary>The host's side: hellos, the join payload, and each new edit.</summary>
public sealed partial class SessionService
{
    /// <summary>The host's own player's join (<see cref="SessionPlayer.Join"/>); clients' follow it.</summary>
    private const int HostJoin = 1;

    private int _lastJoin = HostJoin;

    private void ReceiveAsHost(int peer, ReadOnlySpan<byte> data)
    {
        if (!_peers.TryGetValue(peer, out var client))
        {
            return;
        }

        var reader = new MessageReader(data);
        var kind = (MessageKind)reader.ReadByte();
        if (client.Joined && (byte)kind >= FirstGameKind)
        {
            ReceiveGame(client.Slot, (byte)kind, reader.ReadRest());
            return;
        }

        if (client.Joined && kind == MessageKind.Reply)
        {
            (
                _replication ?? throw new InvalidDataException("A reply came to a host that replicates nothing.")
            ).ReceiveReply(client.Slot, reader.ReadRest());
            return;
        }

        if (client.Joined || client.Slot != 0 || kind != MessageKind.Hello)
        {
            // Before it has joined a client sends only its hello, once, and after it only the game's messages.
            throw new InvalidDataException($"Unexpected message {(byte)kind} from a client.");
        }

        var protocol = reader.ReadInt();
        if (protocol != SessionProtocol.Version)
        {
            // Nothing after the version can be trusted to have this build's shape.
            Reject(
                peer,
                RejectReason.Protocol,
                $"the host runs protocol {SessionProtocol.Version}, the client {protocol}"
            );
            return;
        }

        // Quoted in a refusal, so kept short: a peer's strings are not trusted.
        var identity = SessionProtocol.Cut(reader.ReadString(), 64);
        var identityVersion = reader.ReadInt();
        var fixedStepHz = reader.ReadInt();
        var name = reader.ReadString();
        var kinds = reader.ReadULong();
        reader.EnsureEnd();
        var match = Match;
        if (
            !string.Equals(identity, match.Identity, StringComparison.Ordinal)
            || identityVersion != match.IdentityVersion
        )
        {
            Reject(
                peer,
                RejectReason.Identity,
                $"the host's match is {match.Identity} version {match.IdentityVersion}, the client's {identity} version {identityVersion}"
            );
            return;
        }

        if (fixedStepHz != match.FixedStepHz)
        {
            Reject(
                peer,
                RejectReason.StepRate,
                $"the host steps {match.FixedStepHz} times a second, the client {fixedStepHz}"
            );
            return;
        }

        if (kinds != (_replication?.KindsFingerprint ?? 0))
        {
            Reject(peer, RejectReason.ReplicatedKinds, "the host and the client replicate other kinds of entity");
            return;
        }

        var slot = FreeSlot();
        if (slot == 0)
        {
            Reject(peer, RejectReason.Full, $"the session has its {_settings.MaxPlayers} players");
            return;
        }

        client.Name = SessionProtocol.CutName(name);
        client.Slot = slot;
        SendWorld(peer, client);
    }

    /// <summary>
    /// Starts a client's join: the welcome, then the join payload (the edit log, saved now; without a world, its header
    /// alone, with no edits) as the transport has room for it (<see cref="SendDownload"/>), all on the reliable channel,
    /// in order. The client's slot is taken from now on; it joins once the last of the payload is sent.
    /// </summary>
    private void SendWorld(int peer, HostPeer client)
    {
        client.JoinStarted = Stopwatch.GetTimestamp();
        var payload = new MemoryStream();
        if (_voxels is null)
        {
            EditLogSerializer.WriteEmpty(
                payload,
                _identity.Id,
                _identity.Version,
                Match.Seed,
                Match.FixedStepHz,
                Match.Game
            );
        }
        else
        {
            _voxels.SaveEdits(payload, Match.FixedStepHz, Match.Game);
        }

        client.SavedMilliseconds = Stopwatch.GetElapsedTime(client.JoinStarted).TotalMilliseconds;
        client.Download = payload;
        client.DownloadSent = 0;
        client.SentThrough = _voxels?.EditCount ?? 0;

        _writer.Reset();
        _writer.WriteByte((byte)MessageKind.Welcome);
        _writer.WriteByte((byte)client.Slot);
        SessionProtocol.WriteMatch(_writer, Match);
        _writer.WriteLong(payload.Length);
        _writer.WriteLong(client.SentThrough);
        Send(peer);
        SendDownload(peer, client);
    }

    /// <summary>Sends each joining client the next shares of its payload its transport has room for.</summary>
    private void SendDownloads()
    {
        foreach (var (id, peer) in _peers)
        {
            if (peer.Download is not null)
            {
                SendDownload(id, peer);
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="client"/> the chunks of its payload its transport has room for now
    /// (<see cref="ISessionTransport.ReliableRoom"/>), and once the last is sent, JoinDone: it has joined, and the edits
    /// made since its payload follow. Steam holds what its send buffer refuses and ends a peer past 8 MB held, so a
    /// payload sent in one loop ended every Steam join past about 8.5 MB.
    /// </summary>
    private void SendDownload(int peer, HostPeer client)
    {
        var download = client.Download!;
        var bytes = download.GetBuffer().AsSpan(0, (int)download.Length);
        while (client.DownloadSent < bytes.Length)
        {
            var length = Math.Min(SessionProtocol.JoinChunkBytes, bytes.Length - client.DownloadSent);
            if (_transport.ReliableRoom(peer) <= length)
            {
                return;
            }

            _writer.Reset();
            _writer.WriteByte((byte)MessageKind.JoinChunk);
            _writer.WriteBytes(bytes.Slice(client.DownloadSent, length));
            Send(peer);
            client.DownloadSent += length;
        }

        _writer.Reset();
        _writer.WriteByte((byte)MessageKind.JoinDone);
        Send(peer);
        client.Download = null;
        client.Joined = true;
        client.Join = ++_lastJoin;
        UpdateRoster();
        // The host's numbers that are not the run's defaults, before anything moves by them.
        if (WriteTuning(changedSince: -1))
        {
            Send(peer);
        }
        var total = Stopwatch.GetElapsedTime(client.JoinStarted).TotalMilliseconds;
        var megabytes = Megabytes(bytes.Length);
        LogPlayerJoined(client.Name, client.Slot, client.SentThrough, megabytes, client.SavedMilliseconds, total);
    }

    /// <summary>
    /// Rebuilds <see cref="Players"/> from the host and its joined clients and sends the roster to every joined client.
    /// </summary>
    private void UpdateRoster()
    {
        var players = new SessionPlayer[_settings.MaxPlayers];
        Array.Fill(players, new SessionPlayer(false, "", 0));
        players[0] = new SessionPlayer(true, _playerName, HostJoin);
        foreach (var peer in _peers.Values)
        {
            if (peer.Joined && peer.Slot > 0 && peer.Slot < players.Length)
            {
                players[peer.Slot] = new SessionPlayer(true, peer.Name, peer.Join);
            }
        }

        _players = players;
        RosterVersion++;
        var present = 0;
        foreach (var player in players)
        {
            present += player.Present ? 1 : 0;
        }

        _transport.SetLobbyData(
            SessionProtocol.LobbyPlayersKey,
            present.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        _writer.Reset();
        _writer.WriteByte((byte)MessageKind.Roster);
        _writer.WriteByte((byte)players.Length);
        foreach (var player in players)
        {
            _writer.WriteByte(player.Present ? (byte)1 : (byte)0);
            _writer.WriteString(player.Name);
            _writer.WriteVarInt(player.Join);
        }

        foreach (var (id, peer) in _peers)
        {
            if (peer.Joined)
            {
                Send(id);
            }
        }
    }

    private void Reject(int peer, RejectReason reason, string text)
    {
        LogRejected(peer, reason, text);
        SessionProtocol.WriteDisconnect(_disconnectWriter, DisconnectKind.Reject, (byte)reason, text);
        _transport.Disconnect(peer, _disconnectWriter.Written);
        _peers.Remove(peer);
    }

    /// <summary>The lowest slot from 1 no joined client holds, or 0 when the session is full (the host is slot 0).</summary>
    private int FreeSlot()
    {
        for (var slot = 1; slot < _settings.MaxPlayers; slot++)
        {
            var taken = false;
            foreach (var peer in _peers.Values)
            {
                taken |= peer.Slot == slot;
            }

            if (!taken)
            {
                return slot;
            }
        }

        return 0;
    }

    /// <summary>Drops connections that sent no hello within the connect timeout.</summary>
    private void DropSilentPeers()
    {
        var now = _clock();
        _dropped.Clear();
        foreach (var (id, peer) in _peers)
        {
            if (peer.Slot == 0 && now - peer.ConnectedAt > _settings.ConnectTimeoutSeconds)
            {
                _dropped.Add(id);
            }
        }

        foreach (var id in _dropped)
        {
            Reject(id, RejectReason.NoHello, "no hello in time");
        }
    }

    /// <summary>Sends each joined client the edits of the log it has not been sent, lifts with their pieces.</summary>
    /// <summary>
    /// After a step on a host: the match tunables changed since the clients last had them, to every joined client.
    /// </summary>
    private void SendTuning()
    {
        if (_tunables is null || _tunables.Version == _sentTuning)
        {
            return;
        }

        var since = _sentTuning;
        _sentTuning = _tunables.Version;
        if (!WriteTuning(since))
        {
            return;
        }

        foreach (var (id, peer) in _peers)
        {
            if (peer.Joined)
            {
                Send(id);
            }
        }
    }

    /// <summary>
    /// Writes a tuning message of the match tunables changed since version <paramref name="changedSince"/> (-1: those not
    /// at their defaults); false when there are none.
    /// </summary>
    private bool WriteTuning(long changedSince)
    {
        if (_tunables is null)
        {
            return false;
        }

        var count = 0;
        foreach (var tunable in _tunables.All)
        {
            count += Sends(tunable, changedSince) ? 1 : 0;
        }

        if (count == 0)
        {
            return false;
        }

        _writer.Reset();
        _writer.WriteByte((byte)MessageKind.Tune);
        _writer.WriteVarInt(count);
        foreach (var tunable in _tunables.All)
        {
            if (Sends(tunable, changedSince))
            {
                _writer.WriteString(tunable.Name);
                _writer.WriteFloat(tunable.Value);
            }
        }

        return true;
    }

    private static bool Sends(Tunable tunable, long changedSince) =>
        tunable.Scope == TuningScope.Match
        && (changedSince < 0 ? tunable.Value != tunable.Default : tunable.Version > changedSince);

    private void SendNewEdits()
    {
        if (_voxels is null)
        {
            return;
        }

        var newest = _voxels.EditCount;
        foreach (var (id, peer) in _peers)
        {
            if (!peer.Joined)
            {
                continue;
            }

            for (var sequence = peer.SentThrough + 1; sequence <= newest; sequence++)
            {
                ref readonly var edit = ref _voxels.GetEdit(sequence);
                _writer.Reset();
                if (edit.Operation == VoxelEditOperation.Lift && _voxels.CollectPieceRuns(sequence, _runs))
                {
                    _writer.WriteByte((byte)MessageKind.EditWithPiece);
                    SessionProtocol.WriteEdit(_writer, in edit);
                    SessionProtocol.WriteMaker(_writer, _voxels.MakerOf(sequence));
                    _writer.WriteInt(_runs.Count);
                    foreach (var (material, length) in _runs)
                    {
                        _writer.WriteByte(material);
                        _writer.WriteVarInt(length);
                    }
                }
                else
                {
                    _writer.WriteByte((byte)MessageKind.Edit);
                    SessionProtocol.WriteEdit(_writer, in edit);
                    SessionProtocol.WriteMaker(_writer, _voxels.MakerOf(sequence));
                }

                Send(id);
            }

            peer.SentThrough = newest;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Session: {Name} joined in slot {Slot}; sent {Edits} edit(s) ({Megabytes} MB) after {SaveMs:F1} ms saving, {TotalMs:F1} ms in all"
    )]
    private partial void LogPlayerJoined(
        string name,
        int slot,
        long edits,
        string megabytes,
        double saveMs,
        double totalMs
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "Session: refused peer {Peer} ({Reason}): {Text}")]
    private partial void LogRejected(int peer, RejectReason reason, string text);
}
