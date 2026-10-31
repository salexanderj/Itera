---
name: issues
description: How the repository tracks bugs, suggestions, and changes in GitHub issues, and how to read its CI runs. Use before filing, searching, updating, or closing an issue; when work turns up a bug or a change to leave for later; before a commit message that fixes an issue; and after a push, to check its CI run.
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
- One bug or one change to an issue. A tracking issue lists related ones as a task list (`- [ ] OWNER/REPO#N TITLE`).

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
  normal play, a crash, or a broken rule, and a `stage:` label when the work belongs to a time.
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
- Tick the issue's box in its tracking issue (`gh issue edit N --body-file BODY.md`).

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
  (`.claude/rules/tests.md`). A `Watchdog` limit that a slow runner breaks takes a larger `CI_TEST_TIME_SCALE`.
