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
- On a client, marks its own player for prediction (`ReplicationService.Predict(entity)`, an entity with the components
  `PlayerMovementSystem` moves by); on the host, replicates each client's player under its slot's id with those
  components (a `RemotePlayer`), which the host moves from the client's commands. Movement state a game keeps in a
  component of its own (a wall run, a roll) it registers at the run's start as a predicted part
  (`ReplicatedKinds.RegisterPredicted`, an `IPredictedState`: a name, a fixed number of bytes, `Capture` and `Restore`
  through the origin, `Differs`, `IsValid`; all of a run's parts at most `ReplicatedKinds.MostPredictedBytes`, 256, the
  engine's climber's 43 among them), so a correction puts it back with the rest; the parts' names and sizes go into
  the kinds' fingerprint.
- On the host, judges a client's player's shots against where their targets were when the client saw them:
  `ReplicationService.ShownTickOf(slot)` gives the host's tick (with its fraction) that client showed when it made the
  command its player moves by this step, at most `MostShownTicksBehind` (30) steps back; -1 for the host's own slot,
  on a client, and offline, where a game judges against now.
- Registers the systems in its fixed order: `ReplicationReceiveSystem` early (after `OriginShiftSystem`, before the
  input and the move: a client's check and correction, what it shows, and a host's commands written into the clients'
  players), `ReplicationReplySystem` after the input and before the move (a client's command quantised and sent), and
  `ReplicationSendSystem` last. A game's sounds and effects of the predicted player skip steps run while
  `ReplicationService.Replaying`. Each does
  nothing in a role it does not serve; `replication` (prints what is sent or shown; `replication delay MS` sets the
  interpolation delay) is the send system's command.

How it works:

- The options (`Engine:Replication`): `SendHz` (30, 1 to 60), `BytesPerSecond` (65,536, 4,096 to 1 MiB),
  `PeakBytesPerSecond` (131,072, at least `BytesPerSecond`, at most 4 MiB), `InterpolationMilliseconds` (100, 0 to
  1,000).
- The host captures each entity's state `Engine:Replication:SendHz` (30) times a second (every second step at 60),
  keeping its last 32 states, each from the tick it changed. For each joined client it takes the entities within reach that have a state
  the client has not acknowledged (its own player is left out: it predicts it, and the owner's block below carries the
  host's state of it), by priority (each send adds the
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
- A client's own player (`Predict`, `ReplicationService.Prediction.cs`): each step the reply system quantises its
  command in place (the move in 1/127ths, the yaw in 65,536ths of a turn, the pitch in 32,767ths of a quarter turn: what
  the host reads, bit for bit) and keeps it, and the reply carries the last 8 (newest first, the newest's tick), then
  the host's tick each was made at the sight of (in 1/16 steps: the newest's, then each one's step back). The
  player moves at once by the engine's movement. The host keeps each client's commands by its tick (also those it has
  passed), and at each step's start writes the next into the client's player: it starts once two steps of them wait
  beyond the first, so two wait against jitter; when more than eight wait it skips ahead; a missing one is the last
  without its jump and the game's actions (after 30 missed, standing still); and when none has waited for 3 steps while
  the client's still arrive (the client stalled and dropped steps, or the delay grew), it starts again from the newest
  less two, taking those steps' commands again, which corrects the client once. The client keeps 128 steps. Each send's first
  packet to a client carries the owner's block: the tick of the last command applied and the player's state after it
  (feet absolute, velocity, contact, mode, size, and the run's predicted parts' bytes: the engine's climber, then a
  game's own; one with a number that is not finite, a place past a message's reach, a size the mover refuses (a radius
  past `VoxelCharacterMover.MaxRadiusMetres` or none, a height past 100 m or none, a step past the height), or a part that
  is not valid, is not sent, and every number and part is checked when read, against bounds past any game's). A reply
  whose newest command lies past `int.MaxValue / 2` or whose shown tick lies past any tick is refused as damaged. At the step's
  start the client keeps the state the last step left with its command; when the host's state at a command differs from
  the one predicted (the feet more than 1 cm apart, on the ground or crouched where the other is not, or a part that
  says so: a climb in another state), it
  takes the host's and runs the later commands again through `CharacterStepper` (`Replaying`). What is drawn eases from
  where it was (`EyeSmoothing.Correction`, which the camera adds: 0.85 of it kept a step; past 2 m at once). While the
  player flies the debug camera nothing is predicted or sent. The host and the client start a few steps apart (the
  host's copy moves before the first command), so the first owner's state corrects once.
- Positions in states are absolute, so no buffer moves with the origin. Measured figures (lag, budget) are in
  `docs/replication.md`.

Tests: `ReplicatedStateCodecTests` (fields, changes from a baseline, damaged records, the blend, schemas and kinds),
`ReplicationServiceTests` over `ReplicationHarness` (a host and a client in one process without terrain, test kinds of
movers and players, a host system that moves them, the clock real until the join and then stepped; added delay through
`SimulatedNetwork`): following the delay behind (and a set delay), through 100 ms each way with jitter and loss, a
send of several packets, removals (also through loss, and `Remove`), an id added again under another kind, an id
freed offline, reach, the budget, a client that takes a leaver's place, refused kinds, damaged snapshots and replies, a
bad reply; the systems' behaviour and allocation tests; the wire fingerprint of the snapshot and reply, a predicted
player's commands and owner's block among them (`WireFingerprintTests`); and `PredictionTests` (over the harness's test
floor: a client's player moving at once and the host's copy ending where it does with no correction after the first
second, also through 100 ms each way with jitter and loss; a host that moves the player correcting the client, also
while it walks through 100 ms each way, the drawn jump starting where the player was drawn and easing out; after a
client stall that drops steps, the host starting again and the corrections stopping; a game's movement state (the
harness's test boost, `TestBoostState`) put back by each correction when it is a predicted part, and corrected again
and again when it is not; the tick each client showed known on the host, with no delay and through 100 ms each way;
commands read as quantised, numbers that are not finite among them; damaged replies with commands; no allocation); in
`ReplicatedStateCodecTests` the predicted parts' registration and a state's parts read back, damaged ones refused.

### Files in `src/Itera.Engine`

  - `Replication/`: `ReplicationService` (`.Host.cs`, `.Client.cs`, `.Prediction.cs` with `PredictedState`),
    `PredictedStates.cs` (`IPredictedState`, the engine's `ClimberPredictedState`), `ReplicatedKinds` with `IReplicatedKind`,
    `ReplicatedKind`, `ReplicatedKindId`, and `ReplicationContext`, `ReplicatedSchema`, the fields and
    `ReplicatedState` (`ReplicatedFields.cs`), and `ReplicatedStateCodec`. `Networking/IReplicationEndpoint` is the
    session's side.
  - `Systems/`: `ReplicationSendSystem`, `ReplicationReceiveSystem`, `ReplicationReplySystem`.
  - `Components/NetworkComponents.cs`: `Replicated`.
