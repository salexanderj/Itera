---
name: issues
description: How the repository tracks bugs, suggestions, and changes in GitHub issues, their sub-issues, and a game's GitHub Project, and how to read its CI runs. Use before filing, searching, updating, or closing an issue; when work turns up a bug or a change to leave for later; before a commit message that fixes an issue; and after a push, to check its CI run.
---

# Issues and CI runs

Bugs, suggestions, and changes not done now are GitHub issues. The docs say what is true now, and hold no lists of open
work. CI (`.claude/rules/ci.md`) runs the whole suite after each push.

## Which repository

- Itera's (`gh` with `-R salexanderj/Itera`): the engine, the framework's docs and tools, `.claude/`, `.github/`, and
  the starter game. Itera is public: leave out log lines that name a person, an account, or a home folder.
- A game's own (its `origin`, which `gh` picks by default): the game's code, tests, data, design, and docs.
- A bug seen in a game whose cause is in the engine goes to Itera, and the game's issue, if any, links to it.

## Before filing

- Search open and closed issues first: `gh issue list --state all --search "WORDS"`. When one matches, add a comment
  to it instead of filing again.
- One bug or one change to an issue. A checklist in its body lists the parts of that one change.
- Related issues go under a tracking issue as its sub-issues, from either repository: `gh issue create --parent N`,
  or `gh issue edit N -R OWNER/REPO --parent PARENT_URL`. The parent shows how many have closed.

## Filing

Write the body to a file in the scratchpad, then file it:

```bash
gh issue create --title "Refuse a block placed into another block" --label bug --body-file BODY.md
gh label list    # the labels there are
```

- The title says the change in the imperative, or for a bug, what goes wrong.
- A bug's body: what happens, what should, the steps, the build (the log's first line or the commit), the log lines,
  and the code it traces to. Link code at a commit, so the lines stay put:
  `https://github.com/OWNER/REPO/blob/COMMIT/PATH#L10-L20`.
- A change's or suggestion's body: what to change, why, where in the code, and what shows it is done.
- Write it in the plain style of `CLAUDE.md`. Say what was checked and what is only suspected.
- Labels: one kind (`bug`, `enhancement`, `documentation`, `tests`, `tools`). Add `severity: high` for a likely bug in
  normal play, a crash, or a broken rule.
- When the game's `GAME.md` names a GitHub Project, file the issue into it (`--project "NAME"`; "Projects" below).
- Write `@name` only for a real login you mean to notify.
- The forms in `.github/ISSUE_TEMPLATE/` are for people on the web. People's reports come in through them.

## While working

- A bug or change outside the task gets an issue, named in the reply. It does not go into the task's commit.
- An issue's discussion holds what was found and tried. The decision, once made, goes to its home (the `docs` skill).

## Closing

- A commit that fixes an issue says so on a line of its own at its message's end. In Itera, always name the repository:
  `Fixes salexanderj/Itera#12`. A game pulls Itera's commits, and GitHub would read a bare `#12` in the game's push as
  the game's own issue 12, and close it. A game's own commit may write `Fixes #12`.
- Work done over several commits: close it by hand, naming them: `gh issue close N --comment "Fixed in COMMITS."`.
- Not to be done: `gh issue close N --reason "not planned" --comment "WHY"`.
- A closed sub-issue counts in its parent's progress by itself. Close a tracking issue when its last sub-issue closes.

## Projects

A game may keep a GitHub Project that holds its open issues and the engine's, with fields of its own, or with its
issues under a tracking issue for each area of the game. Its `GAME.md` names the project and how it is organised. `gh` needs the `project` scope for
it: the user runs `gh auth refresh -h github.com -s project` in a terminal.

```bash
gh issue edit N -R OWNER/REPO --add-project "NAME"                     # adds an issue filed without --project
gh project item-list NUMBER --owner OWNER --limit 500 --format json    # the items, each with its fields' values
gh project field-list NUMBER --owner OWNER --format json               # the fields' ids and their options' ids
gh project view NUMBER --owner OWNER --format json --jq .id            # the project's id
gh project item-edit --id ITEM --project-id PROJECT --field-id FIELD --single-select-option-id OPTION
```

- `item-list` gives each item's id (`ITEM`). `item-edit` sets one field of one item.
- Issues filed on the web or by another tool may be missing from the project. Add them when you find them.

## CI runs

After a push, check its run. Before a commit, run only the changed area's test classes; CI runs the rest.

```bash
gh run list --limit 5                                     # the newest runs and their state
gh run watch RUN --exit-status > /dev/null                # waits for the run; run it in the background
gh run view RUN --log-failed | tail -150                  # a failed run's failing steps
gh run download RUN --name tests-Linux-Debug --dir DIR    # the run's results (.trx) and dotnet test output
gh workflow run nightly.yml -f macos=true                 # the nightly by hand, with the Mac job
```

- The test step prints each failed test's message and the top of its stack trace, so `--log-failed` usually says
  enough.
- Fix a failed run in the next commit. A failure that cannot be fixed now gets an issue that links to the run.
- A test that fails only in CI points at a slower machine, another platform, or state shared across tests
  (`.claude/rules/tests.md`). A wait limit (`TestTime`) that a slow runner breaks takes a larger
  `CI_TEST_TIME_SCALE`, or `CI_PLATFORM_TEST_TIME_SCALE` on Windows and macOS.
