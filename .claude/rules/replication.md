---
paths:
  - "src/Itera.Engine/Replication/**"
  - "src/Itera.Engine/Systems/Replication*.cs"
  - "tests/Itera.Engine.Tests/Replication/**"
---

## Replication

The host sends each client the entities a game marks, and a client shows them a little behind the host. `ReplicationService` does this. It is scoped, and lives in `src/Itera.Engine/Replication/`. The reasons for the design are in `docs/replication.md`.

The options are `Engine:Replication:SendHz`, `BytesPerSecond`, `PeakBytesPerSecond`, `GameShare`, and `InterpolationMilliseconds`.

### What a game does

- **Registers its kinds.** At the run's start (its `IRunStartup`) it registers each kind in `ReplicatedKinds`. The registry is run-wide, and the first match seals it. A kind has:
  - a name;
  - a `ReplicatedSchema` of fields;
  - a handler, `IReplicatedKind`. It is run-wide too, so it keeps no match's state. It is given the match's world and origin (`ReplicationContext`);
  - how far it reaches (`relevanceMetres`; 0 reaches every client wherever it is);
  - its priority, which is how fast it gains. A player's is high, a far body's low.
- **Defines the fields.** The fields are `AddPosition` (absolute), `AddAngle`, `AddRotation`, `AddScalar(min, max, bits)`, `AddInteger(min, max)`, and `AddFlags(bits)`. A kind has at most `ReplicatedState.MostLanes` lanes. Each call returns a typed field that sets and reads a `ReplicatedState`. A position goes through the match's origin (`ReplicationContext.Origin`).
- **Gives the kinds a position.** The first position field is where the entity is for relevance. A kind with a reach is measured from the client's player. The game therefore replicates each client's player under its slot's id with a position field. A client with none is sent no kind with a reach.
- **Shares a fingerprint.** The kinds' names and fields make a fingerprint that the hello carries. A host refuses a client whose kinds differ (`RejectReason.ReplicatedKinds`). The test kinds in `tests/Itera.Engine.Tests/Replication/ReplicationHarness.cs` are a worked example.
- **Marks entities, on the host or offline.** `ReplicationService.Add(entity, kind, id)` throws on a client. Call it outside a query. It adds `Replicated`. `MatchServices.Replication` is the match's service.
  - A player's id is its slot's (`PlayerIds.Of(slot)`). Other entities take id 0, which means the host's next. No other id is taken.
  - A destroyed entity is removed from every client at the next send, and its id is free at once. Offline, every step lets go of the destroyed.
  - `Remove(id)` removes a live entity, whose `Replicated` stays. Remove it before adding the entity again.
  - A player who leaves takes their entity along. The game removes it before the slot's next player is added under the same id.
  - Each add is a new generation of its id, which whole records carry. A client shows an id added again afresh (despawned, then spawned), even under another kind.
- **Writes the handler.** `Capture` writes the state on the host. On a client, `Spawn` makes the entity (the engine adds `Replicated`; `Entity.Null` leaves it unshown), `Apply` sets it each step, and `Despawn` removes it. A pose kept for drawing copies its last into its previous one first, as any fixed system does. All run on the main thread, outside queries.
- **Predicts its own player.** On a client, `ReplicationService.Predict(entity)` marks the player, an entity with the components `PlayerMovementSystem` moves by. On the host, the game replicates each client's player under its slot's id with those components (a `RemotePlayer`), and the host moves it from the client's commands.
- **Registers predicted parts.** Movement state in a component of the game's own (a wall run, a roll) is registered at the run's start (`ReplicatedKinds.RegisterPredicted`). The part is an `IPredictedState`: a name, a fixed number of bytes, `Capture` and `Restore` through the origin, `Differs`, and `IsValid`. All of a run's parts together stay within `ReplicatedKinds.MostPredictedBytes`, and the engine's climber is one of them. A correction puts the parts back with the rest. Their names and sizes go into the kinds' fingerprint.
- **Judges shots against the past.** On the host, `ReplicationService.ShownTickOf(slot)` gives the host's tick (with its fraction) that a client showed when it made the command its player moves by this step, at most `MostShownTicksBehind` steps back. It gives -1 for the host's own slot, on a client, and offline, where a game judges against now.
- **Registers the systems in this fixed order.**
  - `ReplicationReceiveSystem` early, after `OriginShiftSystem` and before the input and the move. It does a client's check and correction, shows what a client shows, and writes a host's commands into the clients' players.
  - `ReplicationReplySystem` after the input and before the move. It quantises and sends a client's command.
  - `ReplicationSendSystem` last.
  - Each does nothing in a role it does not serve.
- **Guards its effects.** A game's sounds and effects of the predicted player skip steps that run while `ReplicationService.Replaying`.

The console's `replication` prints what is sent or shown. `replication delay MS` sets the interpolation delay. Both belong to the send system.

### How the host sends

- The host captures each entity's state `Engine:Replication:SendHz` times a second. It keeps the last states, each from the tick it changed.
- For each joined client it takes the entities within reach that have a state the client has not acknowledged. The client's own player is left out: the client predicts it, and the owner's block carries the host's state of it.
- It orders them by priority. Each send adds the kind's priority times one plus the entity's nearness, twice for an entity the client has none of. A sent entity goes back to 0.
- It writes each as a record: the id, the kind, how many ticks back its baseline is, and its changes from the baseline (`ReplicatedStateCodec`). A baseline of 0 means the record is whole, and it is followed by the id's generation.
- Records go into packets that fit `MaxUnreliableBytes`, up to a limit per send, until the client's bytes are spent (`BytesPerSecond`; what a send leaves is kept, up to a send's share of `PeakBytesPerSecond`).
- A record too large for what is left is passed over for smaller ones.
- Every game message the host sends a client counts against the same bytes (`SpendGame`), so its state has the rest.
  The game's messages take at most `GameShare` of them, saved up as the rest are. Past it, one that may be lost is not
  sent; a reliable one always goes, and its bytes are counted.
- Each send has at least one packet, for the client's clock.
- A client is sent nothing until its first reply, which it makes once its world is loaded.
- Ids removed (destroyed, or out of the client's reach) go at the front of a send's first packet, each numbered, up to a limit. They go until a packet that carried that removal is acknowledged. An id back in reach, or added again, goes whole, and its removal is dropped.
- The host remembers a limited number of a client's last packets.

### How a client shows entities

- A packet (`MessageKind.Snapshot`, unreliable) holds its number, the host's tick, the removals, and then records to its end.
- A client reads a packet whole in the session's poll (`IReplicationEndpoint`). A damaged one ends the session, as any bad message does.
- A record whose baseline the client lacks leaves the packet unacknowledged, and the host sends again.
- The client keeps a number of states of each entity by tick. It ignores a record older than the id's removal, and a removal older than the id's newest record.
- It limits the entities it shows and the removed ids it remembers. A host that sends more is refused.
- The client's reply (`MessageKind.Reply`, unreliable, every step) holds its newest packet read and which of the packets before it arrived, and whether its match still loads: its `MatchControl.Phase` is not yet `Playing`, its loading screen covering it. An acknowledged packet's states become that client's baselines. A late acknowledgement of a state from before a removal counts for nothing, because each entity has an epoch.
- The client estimates the host's tick. It takes each packet's tick less the client's tick when the packet is read, and keeps the largest such value, which comes from the packet that arrived soonest. The value falls a little each step so a longer delay is followed.
- It shows each entity at that tick less `InterpolationMilliseconds`, blended between the two states round it.
  - Positions and scalars blend straight, angles the short way, and rotations along the sphere.
  - Whole numbers and flags step.
  - Before the first state it shows the first. Past the newest it shows the newest.
  - After a gap longer than two sends while other snapshots were read, the move takes the last send's time. The entity was unchanged, or it waited for the budget.
  - Across a gap with no snapshot read (loss), it blends straight.
- Positions in states are absolute, so no buffer moves with the origin.
- Measured figures (lag, budget) are in `docs/replication.md`.

### When the host's packets stop

The host sends each client that replies at least a packet each send, so a long gap means its packets do not arrive.

- A client counts its steps without a snapshot from its first reply on (`ReplicationService.SecondsWithoutSnapshots`).
- Past `ReplicationService.SilenceSeconds` it logs a Warning once, with the connection's account (`SessionService.DescribeConnection`). It logs again at Information when snapshots come back. `HostSilenceLayer` shows the count.
- The host notes each client that has replied but acknowledged no new packet for as long. Its Warning gives the time since that client's last reply: replies that still come mean the host's packets do not reach it.
- A host hitch or a long host step past the silence shows the notice for a moment, as it should.

### Prediction of a client's own player

`Predict` and `ReplicationService.Prediction.cs` (with `PredictedState`) do this.

- Each step the reply system quantises the command in place, to what the host reads, bit for bit: the move, the yaw, the pitch, and the camera place a command carries (in a message's 1/64 cm steps). It keeps the command.
- The reply carries the last few commands, newest first, with the newest's tick. It then carries the host's tick each command was made at the sight of. The ticks are in 1/16 steps: the newest command's first, then each later one's as a step back.
- The player moves at once by the engine's movement.
- The host keeps each client's commands by their tick, also those it has passed. At each step's start it writes the next into the client's player.
  - It starts once two steps of commands wait beyond the first, so two wait against jitter.
  - When more than a limit wait, it skips ahead.
  - A missing command is the last one without its jump and the game's actions, but those the game holds as levels
    (`ReplicatedKinds.HeldActions`, such as a mode it is in) and those set while a key is held
    (`ReplicatedKinds.HeldKeyActions`, such as a trigger). After a limit of misses, the player stands still and its
    keys are let go.
  - A client loads from its join until its last reply says it plays (`ReplicationService.IsLoading`), and again
    while one says it loads. Its player then plays none of its input, whatever its commands say: it stands, its look,
    its flying, and its levels kept. So a client that only claims to load gains nothing. A game keeps such a player
    out of harm's way.
  - When none has waited for several steps while the client's still arrive, it starts again from the newest less two, taking those steps' commands again. The client stalled and dropped steps, or the delay grew. This corrects the client once.
- The client keeps a number of steps of its own.
- Each send's first packet to a client carries the owner's block: the tick of the last command applied, and the player's state after it. The state is the feet (absolute), velocity, contact, mode, size, and the run's predicted parts' bytes, the engine's climber first, then a game's own.
- The host does not send a state with a number that is not finite, a place past a message's reach, a size the mover refuses, or a part that is not valid. A size the mover refuses is a radius past `VoxelCharacterMover.MaxRadiusMetres` or none, a height past its limit or none, or a step past the height.
- The client checks every number and part when it reads them, against bounds past any game's.
- A reply is refused as damaged when its newest command lies past `int.MaxValue / 2` or its shown tick lies past any tick.
- At the step's start the client keeps the state the last step left with its command. When the host's state at a command differs from the predicted one, it takes the host's state and runs the later commands again through `CharacterStepper` (`Replaying`).
- The states differ when the feet are more than a tolerance apart, when one is on the ground or crouched and the other is not, or when a part says so, such as a climb in another state.
- What is drawn eases from where it was (`EyeSmoothing.Correction`, which the camera adds).
- While the player flies the debug camera, each command carries the camera's place (`PlayerButtons.Fly`,
  `PlayerCommand.Viewpoint`), absolute in the reply and in both peers' rings of commands. The host's copy flies there:
  its `PlayerMode.Flying` follows the commands, and with none coming it stays flying where it was.
- A command with `PlayerButtons.Viewpoint` (a game's free camera away from the body) carries the place the same way.
  The body stays, `Flying` stays false, and corrections go on. With no commands coming, the place stays where it was.
- The client takes no correction while it flies. A later one runs each command again flying or walking as it did, and
  leaves the client's own `Flying` as it was.
- The host and the client start a few steps apart, because the host's copy moves before the first command. The first owner's state therefore corrects once.

### Tests

- `ReplicatedStateCodecTests`: fields, changes from a baseline, damaged records, the blend, schemas and kinds, and the predicted parts' registration.
- `ReplicationServiceTests` over `ReplicationHarness`: a host and a client in one process without terrain, with `SimulatedNetwork` for delay, jitter, and loss.
- `PredictionTests`: prediction and correction over the harness's test floor.
- The systems' behaviour and allocation tests in `Systems/`.
- `WireFingerprintTests`: the snapshot and reply, a predicted player's commands, and the owner's block.

### Files in `src/Itera.Engine`

- `Replication/`: `ReplicationService` (`.Host.cs`, `.Client.cs`, `.Prediction.cs` with `PredictedState`), `PredictedStates.cs` (`IPredictedState`, the engine's `ClimberPredictedState`), `ReplicatedKinds` with `IReplicatedKind`, `ReplicatedKind`, `ReplicatedKindId`, and `ReplicationContext`, `ReplicatedSchema`, the fields and `ReplicatedState` (`ReplicatedFields.cs`), and `ReplicatedStateCodec`. `Networking/IReplicationEndpoint` is the session's side.
- `Systems/`: `ReplicationSendSystem`, `ReplicationReceiveSystem`, `ReplicationReplySystem`.
- `Components/NetworkComponents.cs`: `Replicated`.
