---
paths:
  - "src/Itera.Engine/{Networking,Matches}/**"
  - "src/Itera.Engine/Native/Steam/**"
  - "src/Itera.Engine/Components/NetworkComponents.cs"
  - "tests/Itera.Engine.Tests/{Networking,Matches}/**"
  - "tests/Shared/{FakeTransport,WireFingerprint,HostileValues,Watchdog}.cs"
---

## Sessions and Steam

Saves are in `.claude/rules/saves.md`.

### Joining and the protocol

Sessions are `Engine:Session` and `SessionService`. `Mode=Host` listens on UDP port `Port`. `Mode=Join` with `Address=host:port` joins it.

A match is a DI scope of its own (`MatchServices`), started after the run's steps, so the window is open. The loading screen's ring shows `Joining ...` and the download (`SessionService.PollJoin`).

The join runs in this order:

1. The client says hello.
2. The host answers with the match: its seed, step rate, and identity (`IMatchIdentity`). The identity is the generator's id and version with voxel terrain, plus the object kinds' fingerprint where the run has kinds. Without terrain it is the application's name.
3. The host sends its edit log with the pieces from its polls, as the transport has room (`ISessionTransport.ReliableRoom`).
4. The client builds its world from the host's seed, whatever its own options say.

Rules for the join:

- A game may send match data of its own in the welcome (`ISessionMatchData.Write`).
- A game gives a fingerprint of what a host and a client must share beyond the engine's checks: its content files and its own messages' version (`ISessionMatchData.Fingerprint`). Every hello carries it, and a host refuses a hello whose fingerprint differs (`RejectReason.GameData`).
- A game without voxel terrain sends a payload of the edit log's header alone, with its match identity where a world writes its generator (`EditLogSerializer.WriteEmpty`). Its client refuses any edit.
- A client drops a game message that arrives before the download ends.
- A hello that arrives before the host's world is made takes its slot and waits for it (`VoxelWorld.IsInitialized`).
- A client fails its join with no welcome within `ConnectTimeoutSeconds`, or with no part of the download for that long.
- A client hears no peer but its host. LiteNetLib refuses connections on a client's socket.
- Strings a peer sends pass through `LogText.Printable` before they are logged. `SessionProtocol.CutName`, `Cut`, and `TryReadDisconnect` do it.
- After each fixed step the host sends each client its new edits, and a client's world follows the host (`VoxelWorld.FollowHost`).
- Raise `SessionProtocol.Version` with any change to the wire, and pin the new fingerprint in `WireFingerprintTests`.
- The roster gives each slot's player a join number (`SessionPlayer.Join`). Per-slot systems keep a `SlotJoins` and forget a slot whose join changed.

### Where a match ends

A match the options started (`Engine:Session:Mode`) ends the run when it ends. The host quitting ends its clients' runs with exit code 0. A refused or failed join ends the run with an Error line and exit code 1.

Any other match goes back to a screen with no match and the reason. These are the console's `play`, `host`, and `join`, and the menu's. This also happens after an exception in the match's work (`MatchFlow.EndAfterFault`: logged at Error, not saved). The commands that start another match are in `MatchControl` and `MatchCommands`. `leave` ends a match.

One run may play many matches. A match's services are freed when it ends, and its voxel memory goes to zero. The edit and physics warm-ups run once a run (`WarmUps`). Leaving while a match loads waits for the navigation thread, because its fill cannot stop early, and for what a game's loading tasks wait on.

### What a game sends

A game sends its own messages. Each kind is a byte with the way it may travel: host to clients, clients to host, or both.

- A game registers `ISessionGameHandler`s in a match's scope. `GameMessageHandler` hands one kind to a method. A game sends with `SessionService.BeginMessage`.
- The engine drops a message that travels the wrong way, and the peer with it.
- A host's match tunables go to its clients by name, reliably, after the step they changed, and every one as each joins (`MessageKind.Tune`, `Tunables`, `.claude/rules/ui-input.md`). A client sets those it has within its own ranges and passes over the rest.

The three channels (`SessionChannel`):

- `Reliable`: once, in order.
- `Sequenced`: may be lost. LiteNetLib drops one older than the newest it has.
- `Unreliable`: may be lost or come out of order, and none is dropped for its age. Use it for messages that number and acknowledge themselves.

A message on `Sequenced` or `Unreliable` must fit one packet, `SessionService.MaxUnreliableBytes`. The session throws `ArgumentException` for a larger one. `LoopbackTests` measures the size.

Other session calls and helpers:

- `RoundTripMilliseconds(slot)` gives the round trip to a player: on a host, to each joined client; on a client, to the host.
- After each fixed step the session sends what the step queued at once (`ISessionTransport.Flush`).
- `NetEntities` gives the ids a host assigns the entities it sends, with a byte kind a game defines, checked alive at every lookup. `PlayerIds` gives each session slot's player id. `NetEntity` is the component.
- `TickRing` keeps the last values by sender tick. `JitterPlayback` delays playback a few ticks for smoothness.

Replicating entities is the engine's (`.claude/rules/replication.md`):

- Its packets are the engine's `Snapshot` (host to a client) and `Reply` (client to host) messages, on the unreliable channel.
- They reach the match's `ReplicationService` through `IReplicationEndpoint`, which attaches itself as the match starts.
- A client's hello carries the replicated kinds' fingerprint. The host refuses a hello whose fingerprint differs (`RejectReason.ReplicatedKinds`).

Positions in messages are absolute, because each peer keeps its own origin (`.claude/rules/engine.md`):

- `MessageWriter.WritePosition` writes a `WorldPoint` as three int32 in 1/64 cm (`SessionProtocol.PositionStepsPerMetre`). `WriteLocalPosition` writes a local one through the peer's origin.
- `MessageReader.ReadPosition` and `ReadLocalPosition` read them. Any twelve bytes are a finite point within reach, or are refused.
- The engine's own messages carry no positions this way. Replication's snapshots carry theirs as lanes (`.claude/rules/replication.md`).
- A host simulates every body in its own origin, so bodies near a client far from the host's camera step in coarser floats.

Each game makes its own particles and sounds. Test a game's messages with damaged bodies and hostile values in every field (`HostileValues` and `Watchdog` in `tests/Shared`).

### Testing a session far apart

`SimulatedNetwork` wraps any `ISessionTransport` and holds what it sends and what it receives for a delay.

- `Engine:Session:AddedDelayMilliseconds` adds a delay to every message on this peer's links, both ways. Set it on a client alone and the delay applies each way.
- `AddedJitterMilliseconds` adds up to that much more, drawn for each message.
- `AddedLossPercent` drops that share of the sequenced and unreliable messages this peer sends.
- Reliable messages are never dropped and keep their order to a peer. What the peer receives stays in order and is never dropped, so loss and reordering apply to what it sends.
- `SessionTransports` wraps the LiteNetLib and Steam transports once for the run when any of the three is set, and logs one Information line.
- Tests wrap a `FakeTransport` with a clock they step by hand (`SimulatedNetworkTests`).

The console commands that work only on the host are listed in the `run-game` skill.

### Steam

`SteamService` (`Engine:Steam`) starts at startup when the Steam client runs and the game has an App ID (`Engine:Steam:AppId`). Without an App ID, Steam stays off with an Information line. Without the client, it logs a Warning and the game plays without Steam.

`Engine:Session:Transport=Steam` carries a session through Steam's P2P sockets and relays.

- The host opens a friends-only lobby and sets its rich presence. Friends join from their friends list.
- Steam starts the game with `+connect_lobby ID`, which `Program.cs` turns into the menu joining that lobby. A client may also join with `Address=lobby:ID` or `steam:ID`.
- The host accepts only its lobby's members and its Steam friends.
- The host's lobby data gives its players (`itera_players`), limit (`itera_max`), and match key (`itera_match`). The match key is the protocol, the match identity's id and version, and the step rate (`SessionProtocol.MatchKey`, `ISessionTransport.SetLobbyData`).
- The menu's join page lists the friends who host (`SteamFriendList`, through a second listener, `ISteamFriendsListener`). It shows "Other version" where the match key differs. The key is made at each read, as the run's object kinds come after the list.
- A friend's "Join game" in the Steam overlay (`JoinRequests`) joins at once with no match. During a match it waits for the menu to ask (`Pending`, `Accept`, `Decline`).

### Players and the Steam start

- `Engine:Session:MaxPlayers` counts the host. `SessionService.Players` lists the session's players, and a game sends
  with `SendToHost`, `SendTo` (one slot), and `SendToAll` (every joined client, except the slot given, if any). `SendToHost` works on a
  joined client, and `SendTo` and `SendToAll` on a host.
- A host's game message to a client counts against that client's bytes (`IReplicationEndpoint.SpendGame`,
  `replication.md`). One that may be lost is not sent once the game's share of them is spent, counted in the stats at
  Debug.
- A player's name is `Engine:Session:PlayerName`, else the Steam name cut to 32 characters, else "Player".
- When Steam did not start the game (no `SteamAppId` variable), the game writes `steam_appid.txt` beside itself and
  moves the working directory there for the `SteamAPI.InitEx` call alone.
  The Steam library loads through `NativeLibrary.SetDllImportResolver`, since Steamworks.NET names only `steam_api`.
- `SteamTransport` refuses an oversized reliable message, skips a sequenced one while its queue is full, and ends a peer
  that holds too much: its limits are its constants.
- A Steam player who connects again ends their older open connection at once. The session hears that peer leave first, so
  the new hello takes the old slot, and the game's own rule for a returning player applies.
- Steam's networking writes its reports into the log through `SteamPlatform`: its errors at Error, warnings at Warning,
  important lines at Information, and the rest at Debug.

### Connection stats

- Each stats line (`Engine:Logging:StatsIntervalSeconds`) logs every connection at Debug: on a host each client, on a
  client its host.
- A transport that measures more than the round trip gives it through `ISessionTransport.TryGetStatus` (`ConnectionStatus`).
  Steam gives its real-time status: quality each way, rates, queues, unacknowledged bytes, and the route or relay.
- `ISessionTransport.DescribeConnection` gives the transport's own account on one line, Steam's detailed status. The
  replication's warnings carry it (`replication.md`).
- `SimulatedNetwork` passes both through from the transport it wraps.

### Tests

`SessionServiceTests`, `SessionGameMessageTests`, `SessionTuningTests`, `LoopbackTests`, `SteamTransportTests`, `SimulatedNetworkTests`, `SteamFriendListTests`, `SteamLibraryTests`, `JoinRequestsTests`, `MatchFlowTests`, `WireFingerprintTests`.

### Files in `src/Itera.Engine`

- `Networking/`: `SessionService` (`.Host.cs`, `.Client.cs`; the roster in `Players`), `ISessionTransport` with the LiteNetLib `NetworkService`, `SteamTransport`, and `SimulatedNetwork`, `SessionProtocol` with `MessageWriter` and `MessageReader`, `MatchSettings`, `SessionAddress`.
- Steam: `SteamService`, `ISteamPlatform` and `SteamPlatform` (the Steamworks.NET calls), `SteamAddress`, `SteamLaunchArguments`, `SteamFriendList`, `ISteamFriendsListener`. The native libraries are in `Native/Steam/`.
- A game's messages go through `ISessionGameHandler` and `BeginMessage` (`GameMessageHandler`), and its match data through `ISessionMatchData`. The helpers are `NetEntities` with `PlayerIds`, `TickRing`, and `JitterPlayback`.

### Package notes

- `Steamworks.NET` has no native libraries. Valve's `steam_api` from its Standalone release sits in `src/Itera.Engine/Native/Steam/` (see the README there) and must change with the package.
- Its managed wrapper differs by system, not by architecture. The package has no arm64 build, and its file for other
  platforms is a reference assembly. `NativeLibraries.AddSteamworksFallback` (at run time) and a target in
  `Directory.Build.targets` (published for osx-arm64) give an arm64 process its system's x64 build. With the universal
  macOS library, Steam starts on an arm64 Mac. Linux and Windows on arm64 have no Steam library, and play without Steam.
- `SteamPlatform` loads the native library from next to the game with a DllImport resolver. The default search on Linux does not look there for `steam_api`.
