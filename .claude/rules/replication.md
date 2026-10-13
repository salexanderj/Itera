---
paths:
  - "src/Itera.Engine/Replication/**"
  - "src/Itera.Engine/Systems/Replication*.cs"
  - "tests/Itera.Engine.Tests/Replication/**"
  - "tests/Itera.Engine.Tests/Systems/Replication*Tests.cs"
---

## Replication

The host sends each client the entities a game marks, and a client shows them a little behind the host
(`ReplicationService`, scoped, in `src/Itera.Engine/Replication/`; the reasons are in `docs/replication.md`).

What a game does:

- At the run's start (its `IRunStartup`), registers each kind in `ReplicatedKinds` (run-wide; the first match seals it):
  a name, a `ReplicatedSchema` of fields, a handler (`IReplicatedKind`: run-wide too, so it keeps no match's state and
  is given the match's world and origin, `ReplicationContext`), how far it reaches (`relevanceMetres`, 0 for every
  client wherever it is), and its priority (how fast it gains: a player 4, a character 1, a far body 0.25). Fields: `AddPosition` (absolute, three lanes of 1/1,024 m),
  `AddAngle` (65,536 steps a turn), `AddRotation` (smallest three, 12 bits each), `AddScalar(min, max, bits)`,
  `AddInteger(min, max)`, `AddFlags(bits)`; at most 31 lanes (`ReplicatedState.MostLanes`). Each returns a typed field
  that sets and reads a `ReplicatedState`; a position goes through the match's origin (`ReplicationContext.Origin`).
  The first position field is where the entity is for relevance; a kind with a reach is measured from the client's
  player, so the game replicates each client's player under its slot's id with a position field (a client with none is
  sent no kind with a reach). The kinds' names and fields make a fingerprint the hello carries: a host refuses a client
  whose kinds differ (`RejectReason.ReplicatedKinds`). The test kinds in `tests/Itera.Engine.Tests/Replication/
  ReplicationHarness.cs` are a worked example.
- On the host (or offline; it throws on a client), marks an entity with `ReplicationService.Add(entity, kind, id)`
  (outside a query; adds `Replicated`; `MatchServices.Replication` is the match's): a player's id is its slot's
  (`PlayerIds.Of(slot)`), others' 0 for the host's next (no other id is taken). A destroyed entity is removed from every
  client at the next send, and its id is free at once (offline every step lets go of the destroyed); `Remove(id)`
  removes a live one, whose `Replicated` stays (remove it before adding the entity again). A player who leaves takes
  theirs along: the game removes it before the slot's next player is added under the same id. Each add is a new
  generation of its id, which whole records carry: a client shows an id added again afresh (despawned and spawned),
  even under another kind.
- The handler's `Capture` writes the state on the host; on a client `Spawn` makes the entity (the engine adds
  `Replicated`; `Entity.Null` leaves it unshown), `Apply` sets it each step (a pose kept for drawing copies its last
  into its previous one first, as any fixed system does), and `Despawn` removes it. They run on the main thread
  outside queries.
- Registers the systems in its fixed order: `ReplicationReceiveSystem` early (after `OriginShiftSystem`, before what
  reads the shown entities), `ReplicationReplySystem` after the input, and `ReplicationSendSystem` last. Each does
  nothing in a role it does not serve; `replication` (prints what is sent or shown; `replication delay MS` sets the
  interpolation delay) is the send system's command.

How it works:

- The options (`Engine:Replication`): `SendHz` (30, 1 to 60), `BytesPerSecond` (65,536, 4,096 to 1 MiB),
  `PeakBytesPerSecond` (131,072, at least `BytesPerSecond`, at most 4 MiB), `InterpolationMilliseconds` (100, 0 to
  1,000).
- The host captures each entity's state `Engine:Replication:SendHz` (30) times a second (every second step at 60),
  keeping its last 32 states, each from the tick it changed. For each joined client it takes the entities within reach that have a state
  the client has not acknowledged (its own player is left out: its game moves it), by priority (each send adds the
  kind's priority times one plus its nearness, twice for an entity the client has none of; a sent one goes back to 0),
  and writes each as a record (id, kind, how many ticks back its baseline is, 0 for whole followed by the id's
  generation, and its changes from it: `ReplicatedStateCodec`) into packets that fit `MaxUnreliableBytes`, at most 32 a
  send, until the client's bytes are spent (`BytesPerSecond`, 64 KB/s, with what a send leaves kept up to a send's
  share of `PeakBytesPerSecond`, 128 KB/s); a record too large for what is left is passed over for smaller ones. Each
  send has at least one packet, for the client's clock. A client is sent nothing until its first reply (it replies
  once its world is loaded). Ids removed (destroyed, or out of the client's reach) go at the front of a send's first
  packet, up to 64, each numbered, until a packet that carried that removal is acknowledged; one back in reach (or
  added again) goes whole and its removal is dropped. The host remembers a client's last 64 packets.
- A packet (`MessageKind.Snapshot`, unreliable): its number, the host's tick, the removals, then records to its end.
  A client reads it whole in the session's poll (`IReplicationEndpoint`), so a damaged one ends the session as any bad
  message does; a record whose baseline it lacks leaves the packet unacknowledged, and the host sends again. A client
  keeps 64 states of each entity by tick, ignores a record older than the id's removal, and a removal older than the
  id's newest record. It shows at most 65,536 entities and remembers at most 16,384 removed ids: a host that sends more
  is refused.
- A client's reply (`MessageKind.Reply`, unreliable, every step): its newest packet read and which of the 64 before it
  arrived. An acknowledged packet's states become that client's baselines; a late acknowledgement of a state from
  before a removal counts for nothing (an epoch per entity).
- A client estimates the host's tick: each packet's tick less the client's when it is read, the largest such (the
  packet that came soonest) kept, falling 0.01 tick a step so a longer delay is followed. It shows each entity at that
  tick less `InterpolationMilliseconds` (100), blended between the two states round it (positions and scalars
  straight, angles the short way, rotations along the sphere, whole numbers and flags stepping); before the first state
  the first, past the newest the newest; after a gap longer than two sends in which other snapshots were read (so the
  entity was unchanged, or waited for the budget), the move takes the last send's time, and across a gap with no
  snapshot read (loss) it is blended straight.
- Positions in states are absolute, so no buffer moves with the origin. Measured figures (lag, budget) are in
  `docs/replication.md`.

Tests: `ReplicatedStateCodecTests` (fields, changes from a baseline, damaged records, the blend, schemas and kinds),
`ReplicationServiceTests` over `ReplicationHarness` (a host and a client in one process without terrain, test kinds of
movers and players, a host system that moves them, the clock real until the join and then stepped; added delay through
`SimulatedNetwork`): following the delay behind (and a set delay), through 100 ms each way with jitter and loss, a
send of several packets, removals (also through loss, and `Remove`), an id added again under another kind, an id
freed offline, reach, the budget, a client that takes a leaver's place, refused kinds, damaged snapshots and replies, a
bad reply; the systems' behaviour and allocation tests; and the wire fingerprint of the snapshot and reply
(`WireFingerprintTests`).

### Files in `src/Itera.Engine`

  - `Replication/`: `ReplicationService` (`.Host.cs`, `.Client.cs`), `ReplicatedKinds` with `IReplicatedKind`,
    `ReplicatedKind`, `ReplicatedKindId`, and `ReplicationContext`, `ReplicatedSchema`, the fields and
    `ReplicatedState` (`ReplicatedFields.cs`), and `ReplicatedStateCodec`. `Networking/IReplicationEndpoint` is the
    session's side.
  - `Systems/`: `ReplicationSendSystem`, `ReplicationReceiveSystem`, `ReplicationReplySystem`.
  - `Components/NetworkComponents.cs`: `Replicated`.
