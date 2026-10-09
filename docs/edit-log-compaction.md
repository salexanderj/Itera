# Edit log compaction

The world is its seed plus its edit log. The log only grows: every dig, placed stone, lift, and ball put back stays in
it, with each lifted piece. A column that loads is generated from the seed and then replays, in order, every edit that
reaches it. Part F asks what a long log costs and what to do about it. This note has the measurements
(2026-09-27) and the methods compared; the maintainer picks what gets built.

## Measurements

A profile test of the edit log (Release, this machine: Ryzen 7 9800X3D; it left with the game that held it). The logs
come from a walk of 30 cm steps within 20 m of a start point, near the ground: 70% 50 cm digs, 20% 50 cm spheres of stone placed, and 10%
6 cm digs. That is about what a player digging all the time for an hour at a few digs a second would make; four such
players make about 50,000 edits.

| Edits | Join payload | Host writes it | Client reads and indexes it |
|---|---|---|---|
| 1,000 | 50 KB | 1.0 ms | 3.1 ms |
| 10,000 | 498 KB | 3.9 ms | 17 ms |
| 50,000 | 2.5 MB | 4.0 ms | 9.6 ms |

An edit is 51 bytes in the payload and 56 bytes in memory, plus about 16 bytes per column it reaches at each level in the
index. A lifted piece adds about 1.9 KB to the payload (a thrown ball 25 cm across), and it stays in memory as long as
the log.

The time to generate the column that the most edits reach, on one worker thread:

| Edits | Level 0 | Level 1 | Level 2 | Level 3 | Level 4 |
|---|---|---|---|---|---|
| none | 2.6 ms | 2.2 ms | 1.7 ms | 1.5 ms | 3.5 ms |
| 1,000 | 117 ms (144 edits) | 42 ms (283) | 13 ms (429) | 4.3 ms (656) | 4.1 ms (656) |
| 10,000 | 381 ms (618) | 141 ms (1,030) | 46 ms (2,034) | 18 ms (4,936) | 6.9 ms (7,315) |
| 50,000 | 513 ms (1,166) | 224 ms (2,213) | 77 ms (4,563) | 31 ms (10,228) | 14 ms (22,349) |

A generated column's voxels take 100 to 160 KB of voxel memory without edits, and up to 290 KB when dug over (level 0,
10,000 edits); the coarse levels grow less.

When a column is installed, the main thread looks up the edits made since its job was queued: under 60 µs even for the
busiest level-4 column of the 50,000-edit log.

What the numbers say:
- **The size of the log is not the problem.** 50,000 edits make a 2.5 MB payload, written and read in about 15 ms in
  all. Only thrown balls grow it quickly, by their pieces: 10,000 throws would add about 19 MB.
- **Replaying edits into a column is the problem, at the fine levels.** A 50 cm dig costs about 0.8 ms at level 0 in
  every layer it reaches, because each voxel of its bounding box (about a million) is tested one by one
  (`VoxelEditing.ApplyToRegion`). A dug-over area makes level-0 columns that take 0.1 to 0.5 s each to load; walking
  back into one, or joining a game with one, loads it that much later. The coarse levels hold many more edits but
  test far fewer voxels per edit.

## Methods

### 1. Replay faster: fill each row's span

For a sphere, the voxels whose sample points it covers form one span per row, found from an integer square root; for a
box, every row is one span. A set or clear then fills the span at once; a fill or paint walks only the span. This is
exact: the span is the set of integers `d` with `d * d <= r * r - dy * dy - dz * dz`, the same test as now.

Measured with the 10,000-edit log, applying all its edits to the busiest column's layers, both ways giving the same
bytes: level 0 from 896 ms to 55 ms (16 times faster), level 1 from 235 to 23 ms, level 2 from 84 to 12 ms.

- Gains: column loads with edits about 10 times faster at the fine levels; live digs on the main thread gain too
  (`VoxelEditing.ApplyToChunk` tests voxel by voxel in the same way).
- Costs: none to the formats or the log; the edit code, and a test that the spans match the voxel test at every level.
- Does not shrink the log or drop pieces.

### 2. Keep edited columns when they unload

A column unloads when the streaming area leaves it, and its trees are freed. Edited columns could instead be kept in the
voxel memory, and freed first when memory runs short, so coming back loads them at once plus any newer edits.

- Gains: walking back into a dug area costs nothing.
- Costs: memory (the budget already caps it); bookkeeping for the kept columns and their snapshot sequence. The first
  visit, after a join or a load from a save, still replays everything.
- Does not shrink the log or drop pieces.

### 3. Snapshots of edited chunks ("baking")

When a resident chunk has taken many edits, copy its voxels as a snapshot tagged with the newest edit in it. A column job
then decodes the snapshot and replays only the edits after it. An edit whose every column at every level has a newer
snapshot is no longer needed in memory, and nor is a piece whose put-back edits all are.

- Gains: replay cost stops growing with the log; old edits and pieces put back can leave memory ("drop
  pieces put back").
- Costs: the most work. Snapshots are per chunk and per level (each level has its own voxels), so they take memory: the
  busiest level-0 column of the 50,000-edit log takes 264 KB as voxels, against about 60 KB for its 1,166 edits, and
  every level needs its own. Saves and
  the join payload either keep the whole log (small, but then nothing leaves the file) or carry snapshots (much larger
  than the edits for the same area). `EditLog` and `VoxelGpuMirror.UpdateChangeBox` number edits 1 to the count
  without gaps: the numbering stays; only old edits' stored bodies go, which the renderer never reads again (it reads
  the edits since its last frame).

### 4. Drop edits that later edits overwrite

An edit whose whole region a later set or clear covers changes nothing in the end, and could leave the log. Checking
cover by one later edit is exact and cheap; cover by several is not.

- Gains: small. Overlapping digs of the same size cover one another only when their centres are the same, so in the
  walk above hardly any edit is covered by one other.
- Costs: a pass over the log, and care with pieces (a lift must keep its piece while any edit puts it back).

### 5. Drop pieces put back

A piece must stay while any edit puts it back, because replaying that edit needs its voxels. Only snapshots (method 3)
make a piece unnecessary.

## Recommendation

Method 1 now: it removes most of the measured cost, changes no format, and is exact. Method 2 if walking back into
dug areas still loads slowly after it. Method 3 only when logs reach hundreds of thousands of edits, or thrown balls'
pieces use too much memory; nothing measured here needs it yet.

## Built: method 1 (2026-09-27)

The maintainer picked method 1. `VoxelEditing.ApplyToRegion` now finds each row's span (`RowSpan`) and fills it at once for a set,
clear, or lift, or walks only the span for a fill or paint; the column jobs, live digs (`ApplyToChunk`, per brick), and
wounds on character models all go through it. A test compares it with the voxel-by-voxel rule on 6,000 random spheres
and boxes of every operation at levels 0 to 4, and every pinned world hash is unchanged.

The busiest column's generation after it (Release, same logs):

| Edits | Level 0 | Level 1 | Level 2 | Level 3 | Level 4 |
|---|---|---|---|---|---|
| 10,000 | 54 ms (was 381) | 41 ms (141) | 25 ms (46) | 20 ms (18) | 7 ms (7) |
| 50,000 | 54 ms (was 513) | 38 ms (224) | 25 ms (77) | 18 ms (31) | 13 ms (14) |

What is left per edit is the check of its range against each layer; the coarse levels, where many edits each change a
few voxels, gain least. Methods 2 and 3 stay open.
