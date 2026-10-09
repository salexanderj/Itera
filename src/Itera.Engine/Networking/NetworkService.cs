using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Networking;

/// <summary>
/// The LiteNetLib transport: LAN and direct addresses. No socket is open until a session listens or connects: the game
/// does not open a port on every launch, and on Windows the firewall prompt appears only when the player hosts or
/// joins. LiteNetLib's own thread keeps connections alive between polls; everything it delivers waits for
/// <see cref="Poll"/> on the main thread.
/// </summary>
public sealed partial class NetworkService : ISessionTransport, IDisposable
{
    private readonly ILogger<NetworkService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _disconnectTimeoutMs;
    private readonly EventBasedNetListener _listener = new();
    private readonly Dictionary<int, NetPeer> _peers = [];
    private NetManager? _manager;
    private ISessionTransportListener? _polling;
    private bool _disposed;

    // A host name being looked up for Connect, and the port to connect to once it is.
    private Task<IPAddress[]>? _resolving;
    private string _resolvingHost = "";
    private int _resolvingPort;

    public NetworkService(ILogger<NetworkService> logger, ILoggerFactory loggerFactory, IOptions<EngineOptions> options)
        : this(logger, loggerFactory, options.Value.Session.DisconnectTimeoutSeconds) { }

    internal NetworkService(
        ILogger<NetworkService> logger,
        ILoggerFactory loggerFactory,
        double disconnectTimeoutSeconds
    )
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _disconnectTimeoutMs = (int)(disconnectTimeoutSeconds * 1000);
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
        _listener.NetworkErrorEvent += OnNetworkError;
    }

    /// <summary>UDP port the manager is bound to, or 0 when stopped.</summary>
    public int LocalPort => _manager?.LocalPort ?? 0;

    /// <summary>Routes LiteNetLib's log through <see cref="ILogger"/>. Opens no socket.</summary>
    public void Initialize()
    {
        NetDebug.Logger = new NetLoggerAdapter(_loggerFactory.CreateLogger("LiteNetLib"));
        LogReady();
    }

    public void Listen(int port)
    {
        Start(port);
        LogListening(LocalPort);
    }

    public void Connect(string address)
    {
        if (!SessionAddress.TryParse(address, out var host, out var port))
        {
            throw new ArgumentException($"'{address}' is not host:port.", nameof(address));
        }

        Start(0);
        LogConnecting(host, port);
        if (IPAddress.TryParse(host, out var literal))
        {
            if (!ConnectTo(new IPEndPoint(literal, port)))
            {
                throw new InvalidOperationException($"LiteNetLib could not start connecting to {host}:{port}.");
            }

            return;
        }

        // A name is looked up off the main thread and connected to at the poll that finds it
        // (LiteNetLib's own lookup is synchronous and stalled the window up to the resolver's
        // timeout).
        _resolving = Dns.GetHostAddressesAsync(host);
        _resolvingHost = host;
        _resolvingPort = port;
    }

    public void Poll(ISessionTransportListener listener)
    {
        if (_manager is null)
        {
            return;
        }

        _polling = listener;
        try
        {
            if (_resolving is { IsCompleted: true } resolving)
            {
                _resolving = null;
                ConnectResolved(resolving, listener);
            }

            _manager.PollEvents();
        }
        finally
        {
            _polling = null;
        }
    }

    public void Send(int peer, ReadOnlySpan<byte> data, SessionChannel channel)
    {
        if (_peers.TryGetValue(peer, out var netPeer))
        {
            var method = channel == SessionChannel.Reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced;
            netPeer.Send(data, 0, method);
        }
    }

    public void Disconnect(int peer, ReadOnlySpan<byte> data)
    {
        if (_peers.TryGetValue(peer, out var netPeer))
        {
            netPeer.Disconnect(data.ToArray());
        }
    }

    /// <summary>
    /// The smallest single-packet size of the connected peers' sequenced channels: LiteNetLib refuses a larger sequenced
    /// message. It starts near 500 bytes and grows as LiteNetLib finds each path's MTU.
    /// </summary>
    public int MaxUnreliableBytes
    {
        get
        {
            var smallest = int.MaxValue;
            foreach (var peer in _peers.Values)
            {
                smallest = Math.Min(smallest, peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced));
            }

            return smallest == int.MaxValue ? DefaultUnreliableBytes : smallest;
        }
    }

    /// <summary>What <see cref="MaxUnreliableBytes"/> gives with no peer: below LiteNetLib's first MTU.</summary>
    private const int DefaultUnreliableBytes = 480;

    public int RoundTripMilliseconds(int peer) =>
        _peers.TryGetValue(peer, out var netPeer) ? netPeer.RoundTripTime : -1;

    /// <summary>Disconnects every peer with <paramref name="data"/> and closes the socket. Safe to call again.</summary>
    public void Close(ReadOnlySpan<byte> data)
    {
        if (_manager is null)
        {
            return;
        }

        if (!data.IsEmpty)
        {
            var bytes = data.ToArray();
            _manager.DisconnectAll(bytes, 0, bytes.Length);
        }

        _manager.Stop();
        _manager = null;
        _peers.Clear();
        _resolving = null;
        LogStopped();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close([]);
    }

    private void Start(int port)
    {
        if (_manager is not null)
        {
            throw new InvalidOperationException("The network is already started.");
        }

        var manager = new NetManager(_listener) { AutoRecycle = true, DisconnectTimeout = _disconnectTimeoutMs };
        if (!manager.Start(port))
        {
            throw new InvalidOperationException(
                port == 0 ? "LiteNetLib could not bind a UDP socket." : $"LiteNetLib could not bind UDP port {port}."
            );
        }

        _manager = manager;
    }

    /// <summary>Starts connecting to the looked-up host, or tells the session no host answered.</summary>
    private void ConnectResolved(Task<IPAddress[]> resolving, ISessionTransportListener listener)
    {
        IPAddress? address = null;
        if (resolving.IsCompletedSuccessfully)
        {
            // IPv4 first, as LiteNetLib's own lookup chose.
            foreach (var found in resolving.Result)
            {
                if (
                    address is null
                    || (
                        found.AddressFamily == AddressFamily.InterNetwork
                        && address.AddressFamily != AddressFamily.InterNetwork
                    )
                )
                {
                    address = found;
                }
            }
        }

        if (address is null || !ConnectTo(new IPEndPoint(address, _resolvingPort)))
        {
            var why = address is not null
                ? "LiteNetLib could not start connecting"
                : resolving.Exception?.GetBaseException().Message ?? "no address";
            LogResolveFailed(_resolvingHost, why);
            listener.OnDisconnected(0, TransportDisconnect.ConnectFailed, []);
        }
    }

    /// <summary>Starts connecting; false when LiteNetLib cannot. A request without the session's key is ignored by the host.</summary>
    private bool ConnectTo(IPEndPoint endPoint) => _manager!.Connect(endPoint, SessionProtocol.ConnectKey) is not null;

    private void OnConnectionRequest(ConnectionRequest request)
    {
        request.AcceptIfKey(SessionProtocol.ConnectKey);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _peers[peer.Id] = peer;
        LogPeerConnected(peer.Id, peer.Address, peer.Port);
        _polling?.OnConnected(peer.Id);
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        _peers.Remove(peer.Id);
        LogPeerDisconnected(peer.Id, peer.Address, peer.Port, info.Reason, info.SocketErrorCode);
        var reason = info.Reason switch
        {
            DisconnectReason.RemoteConnectionClose => TransportDisconnect.Closed,
            DisconnectReason.Timeout => TransportDisconnect.TimedOut,
            DisconnectReason.DisconnectPeerCalled => TransportDisconnect.Local,
            _ => TransportDisconnect.ConnectFailed,
        };
        var data = info.AdditionalData is { AvailableBytes: > 0 } extra ? extra.GetRemainingBytesSpan() : [];
        _polling?.OnDisconnected(peer.Id, reason, data);
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        _polling?.OnReceive(peer.Id, reader.GetRemainingBytesSpan());
    }

    private void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        LogNetworkError(endPoint, socketError);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Network: ready; no socket until a session starts")]
    private partial void LogReady();

    [LoggerMessage(Level = LogLevel.Information, Message = "Network: listening on UDP port {Port}")]
    private partial void LogListening(int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network: connecting to {Host}:{Port}")]
    private partial void LogConnecting(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Network: could not find the host {Host}: {Reason}")]
    private partial void LogResolveFailed(string host, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Network: peer {PeerId} connected from {Address}:{Port}")]
    private partial void LogPeerConnected(int peerId, IPAddress address, int port);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Network: peer {PeerId} at {Address}:{Port} disconnected ({Reason}, socket error {SocketError})"
    )]
    private partial void LogPeerDisconnected(
        int peerId,
        IPAddress address,
        int port,
        DisconnectReason reason,
        SocketError socketError
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "Network: socket error {SocketError} from {EndPoint}")]
    private partial void LogNetworkError(IPEndPoint endPoint, SocketError socketError);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network: stopped")]
    private partial void LogStopped();
}
