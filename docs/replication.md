# Replication: decisions and their reasons

Itera carried a session's messages, its edits, and its seed, but no entity: `NetEntities`, `TickRing`, and
`JitterPlayback` were helpers nothing used, and replicating players and bodies was each game's work. On 2026-10-12 the
engine gained replication as engine systems any game uses (`src/Itera.Engine/Replication/`, `.claude/rules/replication.md`).
This file records what was chosen, what else was offered, and why. Prediction of a client's own player with the host's
correction comes next, on top of it.

## What came before, and what was taken from it

A game built on Itera's first form (its archive) replicated by itself: each player's state every step, unquantised
(56 bytes), checked by the host and relayed; characters by dead reckoning, a state sent when the client's guess drifted
past a limit, under 640 bytes a client a step (1,000 walking creeps cost about 9.3 KB/s to a client); bodies as
kinematic mirrors shown 3 steps (50 ms) behind, with no blending between states. It had no acknowledged baselines, no
interpolation, and no tests with delay. Two of its lessons shaped this design: 350 creeps sent at once overran a Steam
connection (1.2 MB/s against about 250 KB/s) until a budget a client was added, and bodies that kept moving by tiny
amounts cost 110 to 140 KB/s until changes under a millimetre went unsent.

## The design

- **Kinds as schemas.** Offered: each game writes its own messages per kind; the engine replicates components it finds
  by reflection; a game declares each kind's fields. Chosen: declared fields (`ReplicatedSchema`) with a handler that
  captures, spawns, applies, and despawns. The engine then owns quantisation, deltas, blending, and the hostile-value
  checks for every game, and a field's range is known to the reader, so no damaged message is read as a value its field
  cannot hold. The kinds' names and fields give a fingerprint the hello carries, so peers of other kinds do not play.
- **Lanes of whole numbers.** A state is up to 31 whole numbers: a position three of 1/1,024 m, absolute (so no buffer
  moves with the origin, and peers with other origins agree), an angle 65,536 steps a turn, a rotation its smallest
  three components at 12 bits each (within about 0.1 degrees; at 10 bits the largest, rebuilt from the other three, was
  off by up to about 0.3 degrees), scalars and whole numbers in their ranges. 31, so a mask of changed lanes is one positive variable-length int.
- **Changes from what the client acknowledged.** Offered: whole states each time; dead reckoning (the archive's);
  changes from the last state the client acknowledged. Chosen: acknowledged baselines. A lane that did not change costs
  nothing, one that moved a little a byte (zigzag variable-length numbers), and an entity whose state the client holds is
  not sent at all, so resting bodies and standing characters cost nothing, without the archive's thresholds. The host
  keeps each entity's last 32 states, each from the tick it changed, so a baseline of an entity that stands still is
  never lost (an earlier form looked baselines up by the tick they were sent and resent every standing entity about once
  a second).
- **One unreliable channel, packets that number themselves.** Records, new entities, and removals go in the same
  packets, repeated until acknowledged, so no reliable message can arrive out of step with the states. For this the
  transports gained `SessionChannel.Unreliable` (9588e1f): LiteNetLib's sequenced channel drops a packet older than the
  newest it has, and a send of several packets may come out of order. A packet fits `MaxUnreliableBytes`, 1,020 bytes on
  LiteNetLib (measured in `LoopbackTests`; a comment said about 480) and 1,024 on Steam, and the session refuses a larger
  one.
- **Relevance and priority under a budget.** Each send, a client is sent the entities in reach (a kind's metres from its
  player, or every one) that have something new for it, highest priority first, until its bytes are spent: 64 KB/s,
  with what a send does not spend kept for the next, up to one send's share of 128 KB/s. Priority grows each send by the
  kind's weight and the entity's nearness, twice as fast for one the client lacks, and returns to 0 when sent, so an
  entity left out of one send is sent in a later one.
- **A rate and a delay.** State goes at 30 sends a second and replies every step; a client shows entities 100 ms behind
  the host, a setting the console changes during a match: three sends, the delay Glenn Fiedler's "Snapshot
  Interpolation" (gafferongames.com) gives for 2 to 5 % loss, and the Source engine's default (`cl_interp 0.1`, Valve's
  "Source Multiplayer Networking"). Offered: 20 or 60 sends a second; an adaptive delay. Chosen: 30 and a fixed 100 ms, adaptive
  later if tests show a need.
- **The client's clock.** The host's tick on each packet, less the client's when it is read, gives the host's lead; the
  largest (the packet that came soonest) is kept and falls slowly, so jitter does not move it and a longer delay is
  followed. The client shows the host's estimated tick less the delay, blended between the states round it.
- **Removals and ids added again.** A removal is numbered, and only an acknowledgement of a packet that carried that
  removal clears it, so a late acknowledgement cannot clear a newer removal of the same id. Each add of an id is a new
  generation, which whole records carry, so a client replaces what it shows under an id added again (a player who
  came back), even under another kind, rather than refusing it or blending from the old one.
- **A loading client is sent nothing.** Until a client's first reply (sent once its world is loaded) the host sends it
  nothing, so it does not keep seconds of whole states it cannot read yet.
- **Read in the poll, shown in the step.** A snapshot is read whole as it arrives, before any system runs, so a damaged
  one ends the session through the session's own error path; only making, showing, and removing entities touch the ECS,
  in the receive system.

## Measured

The service's tests run a host and a client in one process (`ReplicationHarness`, 2026-10-12). With no delay, a mover
is shown 5.99 to 6.02 steps behind the host: the interpolation delay (100 ms) alone. With the delay set to 150 ms, 8.99
to 9.03 steps. Through 100 ms each way with 10 ms of jitter and 5 % loss in each direction, 12.2 steps at the median
(the delay and the one-way delay make 12), 12.6 at nine in ten, 14.0 at 99 in 100, and at most 18.0, where a send was
lost or late. 400 movers under a budget of 8,192 bytes a second are sent 8,035 bytes a second of snapshots, and every
one reaches the client within two seconds. The tests hold these figures within a tenth or two of a step.

## Left for later

- Prediction of a client's own player and the host's correction (the host leaves a client's own player out of what it
  sends it for now).
- A short history of positions on the host for judging hits against what a client saw.
- A group far from every player sent as one record for the whole group, rather than one for each member, when crowds
  need it.
- Stamping edits with the host's tick, so a crater shows with what made it rather than up to the delay before.
