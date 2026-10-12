---
paths:
  - "src/Itera.Engine/{Networking,Matches,Saves}/**"
  - "src/Itera.Engine/Native/Steam/**"
  - "src/Itera.Engine/Voxels/EditLog*.cs"
  - "src/Itera.Engine/Components/NetworkComponents.cs"
  - "tests/Itera.Engine.Tests/{Networking,Saves,Matches}/**"
  - "tests/Shared/{FakeTransport,WireFingerprint,HostileValues,Watchdog}.cs"
  - "tests/Itera.Engine.Tests/{Voxels/EditLogTests,GameLoopSaveTests}.cs"
---

## Sessions, Steam, and saves

Sessions (`Engine:Session`, `SessionService`): `Mode=Host` listens on UDP port 47600 (`Port`), and `Mode=Join` with
`Address=host:port` joins it. A match is a DI scope of its own (`MatchServices`), started after the run's steps, so
the window is open; the loading screen's ring shows `Joining ...` and the download (`SessionService.PollJoin`). The
client says hello; the host answers with the match (seed, step rate, and identity: `IMatchIdentity`, the generator's id
and version with voxel terrain, with the object kinds' fingerprint where the world places objects, else the
application's name) and its edit log with the pieces, sent
from its polls as the transport has room (`ISessionTransport.ReliableRoom`), and the client's world is built from the
host's seed whatever its own options say; a game may send match data of its own in the welcome and refuse a client
whose rules differ (`ISessionMatchData`: its hash). A game without voxel terrain sends a payload of the edit log's
header alone (its match identity where a world writes its generator, `EditLogSerializer.WriteEmpty`), and its client
refuses any edit. A client drops a game message that arrives before the download ends. After each fixed step the host sends
each client its new edits, and a client's world follows the host (`VoxelWorld.FollowHost`). The protocol version is
`SessionProtocol.Version`; raise it with any change to the wire, and pin the new fingerprint in the engine's
`WireFingerprintTests`. The roster gives each slot's player a join number (`SessionPlayer.Join`); per-slot systems keep a
`SlotJoins` and forget a slot whose join changed.

A match the options started (`Engine:Session:Mode`) ends the run when it ends: the host quitting ends its clients'
runs with exit code 0, and a refused or failed join ends the run with an Error line and exit code 1. Any other match
(the console's `play`, `host`, `join`, or the menu's) goes back to a screen with no match and the reason, also after an
exception in its work (`MatchFlow.EndAfterFault`: logged at Error, not saved), and the commands that start another
(`MatchControl`, `MatchCommands`); `leave` ends a match. One run may play many matches;
a match's services are freed when it ends (its voxel memory to 0 MB, logged), and the edit warm-up runs once a run
(`WarmUps`). Leaving while a match loads waits for the navigation thread (5 s at most; its fill cannot stop
early) and for what a game's loading tasks wait on.

What a game sends: its own messages, each kind a byte with the way it may travel (host to clients, clients to host,
or both), through `ISessionGameHandler`s it registers in a match's scope (`GameMessageHandler` hands one kind to a
method) and `SessionService.BeginMessage`; the engine drops a message that travels the wrong way and the peer with it.
Helpers: `NetEntities` (the ids a host gives the entities it sends, with a byte kind a game defines, checked alive at
every lookup; `PlayerIds`, each session slot's player id; the `NetEntity` component), `TickRing` (the last 16 values by
sender tick), and `JitterPlayback` (a few ticks' delay for smooth playback). Player and body replication is a game's
today (Zorf TD's is in its archive). Each peer keeps its own origin (`.claude/rules/engine.md`), so a position in a
message is absolute: `MessageWriter.WritePosition` writes a `WorldPoint` as three int32 in 1/64 cm (0.16 mm, to
±335 km; `SessionProtocol.PositionStepsPerMetre`), `WriteLocalPosition` a local one through the peer's origin, and
`MessageReader.ReadPosition` and `ReadLocalPosition` read them (any twelve bytes are a finite point within reach, or
refused); the engine's own messages carry no positions. A host simulates every body in its own origin, so bodies near
a client far from the host's camera step in coarser floats (1 mm at 10 km, 8 mm at 100 km). Each game makes its own particles and sounds. Test a game's messages with damaged
bodies and hostile values in every field (`HostileValues`, `Watchdog` in `tests/Shared`).

The commands that work only on the host are listed in the `run-game` skill.

Steam (`SteamService`, `Engine:Steam`) starts at startup when the Steam client runs and the game has an App ID
(`Engine:Steam:AppId`; 0, the default, leaves Steam off with an Information line; without the client, a Warning, and
the game plays without it). `Engine:Session:Transport=Steam` carries a session through Steam's P2P sockets
and relays: the host opens a friends-only lobby and sets its rich presence, so friends join from their friends list
(Steam starts the game with `+connect_lobby ID`, which `Program.cs` turns into the menu joining that lobby), or a client
joins with `Address=lobby:ID` or `steam:ID`; the host accepts only its lobby's members and its Steam friends. The host's
lobby data gives its players (`itera_players`), limit
(`itera_max`), and match key (`itera_match`: protocol, the match identity's id and version, step rate; `SessionProtocol.MatchKey`,
`ISessionTransport.SetLobbyData`). The menu's join page lists the friends who host (`SteamFriendList`, through a second
listener, `ISteamFriendsListener`; the loop updates it whatever menu shows, and keeps it fresh while a menu sets
`Watching`), with "Other version" where the match key differs. A friend's "Join game" in the Steam overlay
(`JoinRequests`) joins at once with no match, and during a match waits for the menu to ask (`Pending`, `Accept`,
`Decline`).

Named saves (`Engine:Save:Name`, `SaveService`): a host or offline game starts from `Saves/NAME.save` in the data
folder when it exists (its seed and match settings, a game's match data among them, replace the options'; another step
rate or generator version ends the run; a game without terrain saves and reads the header alone, with its match
identity, refuses a save holding edits, and holds no loose bodies) and writes its world there when the match ends
cleanly, after putting thrown balls still loose back into the grid: only the edits since the last reset, renumbered
from 1 (a join payload keeps every edit). The old file is kept as `NAME.save.bak`; a failed write
leaves the old save, deletes its temporary file, and is told at the menu. Save names refuse Windows device names (CON,
NUL, COM1, ...). The file is the edit log stream, format
6 (it may hold resets, capsule edits, and clears of some materials; formats 1 to 5 still read,
and an older build refuses format 6). A client never saves. The log indexes at most 2^24 column entries since its last
reset (`EditLog.MaxIndexEntries`, counted over all ten levels whatever `Engine:Voxels:Levels` is, so every game
agrees): a save, a join payload, or a host's edit past that is refused, and a game's edits
(`VoxelWorld.TryApply`, which also refuses an edit far outside the map) stop short of it.

### Files in `src/Itera.Engine`

  - `Networking/`: `SessionService` (`.Host.cs`, `.Client.cs`; the roster in `Players`), `ISessionTransport` with the
    LiteNetLib `NetworkService` and `SteamTransport`, `SessionProtocol` with `MessageWriter` and `MessageReader`,
    `MatchSettings`, `SessionAddress`; Steam: `SteamService`, `ISteamPlatform` and `SteamPlatform` (the
    Steamworks.NET calls), `SteamAddress`, `SteamLaunchArguments`, `SteamFriendList`, `ISteamFriendsListener`; the
    game's messages through `ISessionGameHandler` and `BeginMessage` (`GameMessageHandler`), its match data through
    `ISessionMatchData`; the helpers `NetEntities` with `PlayerIds`, `TickRing`, and `JitterPlayback`. Steam's native
    libraries in `Native/Steam/`.

### Package notes

- `Steamworks.NET 2024.8.0` (Steamworks SDK 1.60) has no native libraries; Valve's `steam_api` from its Standalone
  release sits in `src/Itera.Engine/Native/Steam/` (see the README there) and must change with the package. Its managed
  wrapper differs by platform (Windows packs structs to 8, Linux and macOS to 4); the package has no osx-arm64 or
  linux-arm64 build, so Steam does not start there. `SteamPlatform` loads the native library from next to the game with
  a DllImport resolver: the default search on Linux does not look there for `steam_api`.
