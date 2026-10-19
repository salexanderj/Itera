---
name: docs
description: "Where each kind of fact goes in this repository's docs (CLAUDE.md, GAME.md, the rules files, skills, ARCHITECTURE.md, docs/, and a game's design pages and records), the limits the documentation tests check, how to split a file, and what to update after a change. Use before writing or changing any doc, when a documentation or reference test fails, and when finishing a change."
---

# Keeping the docs

Agents read these docs more than people do, and each one costs context while it is loaded. The rules below keep what
a session loads small, keep each fact in one place, and keep every doc true to the code. The documentation tests check
the rules that can be checked (`tests/Itera.Engine.Tests/DocumentationTests.cs` for the framework's docs, the game's
own for its docs); the rest is this skill's.

## One home for each fact

Each kind of fact has one home, chosen by when it is read. Every other file names the home and does not repeat the
fact. Every home but the records states what is true now: a change replaces the old text, and never adds a dated note
beside it.

| Kind of fact | Its one home | Read when |
| --- | --- | --- |
| A rule for every task | `CLAUDE.md` (the framework's), `GAME.md` (the game's) | Every session |
| A rule for changing an area, and where its code is | The area's rules file, with narrow paths | A file of the area is opened |
| A task done step by step | A skill: its `SKILL.md`, with lists and recipes in files beside it | The task starts |
| A list the code holds: options, console commands, systems, tunables, replicated kinds, save sections | A generated file in `docs/reference/` | Looked up |
| A value the game reads | Its data file, with its help text; docs name its key | Looked up |
| Why the engine is built as it is | `ARCHITECTURE.md` (the overview and the reasons) or a write-up in `docs/` | Looked up |
| What the game should be, and what is decided | The game's design pages, as its `GAME.md` names them | Before building it |
| When and why something was decided | One line in the game's decision log | Searched |
| What was built, changed, and measured | The game's milestone record, and the commit message | Searched |

The records (the decision log, the milestone records, and the write-ups in `docs/`) are written once and only added to.
No session loads them whole.

## What each home holds

- **`CLAUDE.md`**: the rules every task needs, a pointer to the rules files' index, the commands, and the
  conventions. A rules file that Claude Code loads by its paths is dropped when the conversation is compacted, and loads
  again only when a matching file is read. A rule that must hold in every task therefore goes in `CLAUDE.md`.
- **A rules file** (`.claude/rules/<area>.md`, a game's `game-<area>.md`): what to do and not do when changing the
  area, and where its code is (its classes, files, systems, components, and data keys) and how the parts connect. It
  names a tunable or data key, not its value. It names its area's test classes, and describes none of them. It holds no
  dates, old values, measurements, or decisions. It may state a target the area is held to ("the step stays under
  8 ms") with no date.
- **A skill**: `SKILL.md` holds the procedure an agent follows each time. Long lists, recipes, and reference go in files
  beside it, each named from `SKILL.md` with what it holds, and read when needed.
- **`docs/reference/`**: generated from the code by `ReferenceTests` (the engine's and the game's). Never edit these
  files by hand.
- **`ARCHITECTURE.md`**: what the engine is, its dependencies, how a run and a match are put together, and why each
  design was chosen over the others. How an area works now is its rules file's.
- **A write-up in `docs/`**: a record of one piece of work: the choices, the reasons, and what was measured, with
  dates. It is not kept current; a later change gets its own record.

## The limits the tests check

| What | Limit |
| --- | --- |
| `CLAUDE.md` | 15,000 characters and 200 lines |
| Everything a session loads at its start (`CLAUDE.md`, what it imports, rules files with no paths) | 25,000 characters; `GAME.md` 10,000 and 200 lines |
| The rules files that opening any one file loads, together | 32,000 characters |
| A skill's `SKILL.md` | 16,000 characters |
| A sentence in `CLAUDE.md`, `GAME.md`, a rules file, a skill, an agent, or `ARCHITECTURE.md` | 60 words |
| Dates in `CLAUDE.md`, a rules file, a skill, an agent, or `ARCHITECTURE.md` | None |
| A path or code name in backticks in those docs | Names a file, folder, or name that is there |
| A generated file in `docs/reference/` | The same as the code makes now |

A rules file with no paths loads at every session's start, so every rules file has paths, and each path matches a
file. The limits apply in the repository's own sizes, so a growing area gets more small rules files, not larger ones.

## Writing

- Plain words in short, active sentences, one fact to a sentence. Aim for under 25 words; 60 is the limit.
- A list for parallel items, a table for items compared on several points, and prose for how parts connect.
- No nested brackets. No invented terms or metaphors.
- Write what the code does now. When it changes, replace the sentence; do not write "until", "since", or "was".
- Name a value's key, a class, or a file in backticks, exactly as the code has it, so the tests can check it.
- Write a placeholder in capitals or in angle brackets (`Data/NAME.json`, `game-<area>.md`), so no test takes it for a
  name.

## After a change

1. Where the code's behaviour, structure, or names changed, edit the area's rules file in place.
2. A new file goes in its area's paths (a game may require every file to be in one). A new area gets a rules file. The
   engine's are listed in the generated `docs/reference/engine-rules.md`, and the game's in a row of `GAME.md`'s table.
3. A decision goes on its design page and gets one line in the decision log.
4. What was built, and what it measured, goes in the milestone record and the commit message.
5. When a reference test fails, write the generated files again
   (`ITERA_WRITE_REFERENCE=1 dotnet test --filter ReferenceTests`), look over the diff, and commit them.
6. Run the documentation tests of both test projects before the commit:
   `dotnet test --filter "FullyQualifiedName~DocumentationTests"`.

## When a check fails

- **A file loads too much, or a file is too large.** Split it by sub-area. Make a rules file for the sub-area, with
  paths that match only its files. Move the sub-area's text there. Write the generated index again, or add the game's row to `GAME.md`'s table.
  Narrow a path that matches more than its area (a whole project's folder, every test).
- **A date.** The dated text is history: move it to the record, and write what is true now in its place.
- **A long sentence.** Split it into sentences of one fact each.
- **A name that is not there.** Write the code's name now. If the thing is gone, take the sentence out.
- **A generated file differs.** Write it again (above) and read the diff: it shows what the change did.

## A docs audit

At a milestone's end, and before a design review, check what the tests cannot: whether each doc's sentences are still
true. Give `reader` agents one part each (the framework's rules, the game's rules, the skills, the design pages, and
`ARCHITECTURE.md`). Each checks claims against the code (names, numbers, and behaviour), and lists copies of one fact in
two places and decisions that a later one replaced. Fix what they find in place, and record the audit in the milestone
record.
