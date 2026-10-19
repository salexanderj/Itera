---
paths:
  - "src/Itera.Engine/Saves/**"
  - "src/Itera.Engine/Voxels/EditLog*.cs"
  - "tests/Itera.Engine.Tests/Saves/**"
  - "tests/Itera.Engine.Tests/{Voxels/EditLogTests,GameLoopSaveTests}.cs"
---

## Saves

Sessions are in `.claude/rules/sessions.md`. The edit log's compaction is in `docs/edit-log-compaction.md`.

### Named saves

`Engine:Save:Name` names a save, and `SaveService` reads and writes it. A host or offline game starts from `Saves/NAME.save` in the data folder when it exists.

- The save's seed and match settings replace the options', and a game's match data is among them. Another step rate or generator version ends the run.
- A game without terrain saves and reads the header and its sections alone, with its match identity. It refuses a save holding edits and holds no loose bodies.
- A client never saves.
- Save names refuse Windows device names (CON, NUL, COM1, and the like).

The save holds the world and the game's own state. It is written at two times:

- Every `Engine:Save:AutosaveMinutes` of the match's steps, and at the console's `save`. The capture runs on the main thread at a step's end (the edit log frozen with `EditLog.Freeze`, and each section's bytes). A background thread writes it, one write at a time. Loose bodies are left out.
- When the match ends cleanly, after thrown balls still loose go back into the grid. The capture runs at the "save sections" step's shutdown, before the ECS world goes. The write runs at the save step's, after waiting for a write still running.

What a save holds:

- Only the edits since the last reset, renumbered from 1. A join payload keeps every edit.
- The edit log stream (`EditLogSerializer`). It may hold resets, capsule edits, clears of some materials in boxes and spheres, removals of voxel objects' parts, clears of their far copies, and objects placed and removed whole during play. It ends with the game's sections, which a join payload leaves out. A save holds at most `EditLogSerializer.MaxSections` sections; more are refused.
- The world's generator id, with the object kinds' fingerprint where the run has kinds (`VoxelObjectKinds.WorldId`). A save of other kinds is refused.
- Older formats still read, and an older build refuses a newer format.

The log indexes a limited number of column entries since its last reset (`EditLog.MaxIndexEntries`, counted over all levels whatever `Engine:Voxels:Levels` is). A save, a join payload, or a host's edit past the limit is refused. A game's edits (`VoxelWorld.TryApply`, which also refuses an edit far outside the map) stop short of it.

A game's own state is its `ISaveSection`s (`Saves/`, scoped):

- A section has a name of 1 to 64 letters, digits, '.', '-', or '_', a version from 1, `Capture(BinaryWriter)`, and `Restore(BinaryReader, version)`.
- The save hands each section what it held at the "save sections" step, which comes after "ecs": the systems exist and no step has run. `SaveService.StartedFromSave` tells a system that the match resumes.
- A saved section the game lacks is left out with a warning.
- A section of a newer version, or whose bytes do not read, ends the start.
- A section whose `Capture` throws is logged at Error and told at the menu. The last save stays, and the match goes on.

Damaged and mismatched files:

- The old file is kept as `NAME.save.bak`.
- A damaged save is passed over for its backup with a warning. The next write keeps the damaged file as `NAME.save.damaged`, not as the backup.
- A save that is whole but of another generator or a newer build (`EditLogSerializer.IsMismatch`), or that cannot be opened, ends the start with both files left.
- A deleted save starts a new world, whatever its backup.
- A failed write leaves the old save, deletes its temporary file, and is told at the menu.

- A save is written to `NAME.save.tmp` first, and replaces the last good save only once it is whole.

### Tests

`SaveServiceTests`, `SaveSectionsTests`, `SaveMatchTests`, `SaveFilesTests`, `EditLogTests`, `GameLoopSaveTests`.

### Files in `src/Itera.Engine`

- `Saves/`: `SaveService` and `ISaveSection`.
- `Voxels/EditLog*.cs`: the edit log and its file format (`EditLogSerializer`).
