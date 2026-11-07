---
name: worktrees
description: How sessions work side by side, each in a git worktree of its own on its own branch, and land their commits on main, and how a session in the main checkout works beside them. Use in a session started with `claude -w NAME` (its folder is under .claude/worktrees), in the main checkout while worktree sessions run, before starting one, before committing or landing work from a worktree's branch, and when merging main into a branch or fast-forwarding main to it.
---

# Sessions in worktrees

Sessions that work at once each take a worktree of their own: a checkout of the repository in
`.claude/worktrees/NAME`, on the branch `worktree-NAME`. Each worktree session commits on its own branch. The session
in the main checkout, or the user, lands the commits on `main` (below). Only `main` is pushed.

## Starting

- From the main checkout, `claude -w NAME` opens the worktree NAME, or makes it. A worktree with that name already
  there opens with its files, its build, and its branch.
- `.claude/settings.json` sets `worktree.baseRef` to `head`, so a new worktree starts from the main checkout's local
  `main`, unpushed commits included.
- At the start of each task, bring the branch up to `main`: `git merge --ff-only main`. When the branch has commits
  of its own, `git merge --no-edit main` instead.
- A new worktree has no build. The first `dotnet build` builds everything; later builds are incremental. A worktree
  with its build takes about 6 GB.

## Working

- Builds, tests, and game runs share the machine through its lock (`tools/machine`), across every checkout.
- `tools/control/game.py` keeps each checkout's game apart: its state and its copy of the build are under the agent
  data folder's `Run/worktrees/NAME`, and `start` takes the first free control port from 47650. Commands after
  `start` find the port in that state.
- Engine changes still go through the one Itera checkout (`CLAUDE.md`). Sessions share it, so commit there by path.
  Then pull Itera's `main` into the branch: `git pull --no-rebase --no-edit itera main`.
- The auto memory and `.claude/settings.local.json` are the main checkout's, shared by every worktree. `CLAUDE.md`,
  `GAME.md`, the rules, and the skills are the branch's own copies.

## Landing on main

Claude Code keeps a session started with `claude -w` inside its worktree. It refuses that session's commands on the
main checkout, `git -C` included. So the worktree session readies its branch, and the main checkout's session lands it.

In the worktree, once a task's commits are done:

```bash
git merge --no-edit main   # the work landed since; build and test after a merge that changed code
```

Leave the tree clean, and say in the reply that the branch is ready, with its tests' result. Never move `main` from
the worktree (`git push .`, `git branch -f`, `git update-ref`): the main checkout's files would no longer match it.

In the main checkout:

```bash
git merge --ff-only worktree-NAME   # main moves to the branch
git push origin main                # when it is time to push; its pre-push hook checks the format
```

- With no session in the main checkout, the worktree session gives the user these two commands, each as
  `! git -C MAIN ...`, where MAIN is the main checkout's folder.
- Land a branch when it holds `main`, its tree is clean, and its tests passed after its last merge of `main`.
- Push `main` when the game's `GAME.md` says, since each push of code starts a CI run: landing alone starts none.
- Fast-forward `main` in the main checkout: `main` is checked out there, so git refuses to move it from a worktree.
  Pushing the branch straight to origin's `main` would leave local `main` behind.
- When the fast-forward is refused because `main` moved, the worktree session merges `main` again, tests, and says
  the branch is ready again.
- When it is refused because the main checkout has changes of its own to the same files, the session there commits
  them first. Never stash or reset another session's changes.
- Do not land while `tools/machine/machine.py status` shows a hold from the main checkout's folder: its build or test
  run would read files that the fast-forward changes under it.
- Integrate by merging `main`, never by rebasing: branches carry the merges of Itera's `main`.

## The main checkout's session

One session can work in the main checkout while others work in worktrees. It works on `main`, has no branch of its own,
and lands the worktrees' branches.

- Its commits are on `main` at once. Each one moves `main`, so a worktree's next fast-forward is refused until that
  worktree merges `main` again.
- Every session started without `-w` shares the main checkout's files and index. Commit by path
  (`git commit -- PATHS`), or check the index first, so nothing another session staged goes into the commit.
- A worktree's landing changes the files in the main checkout. Git refuses it while this session has uncommitted
  changes to the same files, so this session commits them first.
- After a landing, build again before testing or committing: the last build no longer matches the files.
- While it holds the machine lock, worktrees do not land (above).
- It learns that a worktree session is done from an idle notice: Claude Code's SendMessage tool, with notify_when_idle set. The notice
  comes at once when that session is already idle, so it asks again only after the session is busy.
- It pushes `main` for every session, so a round of work starts one CI run. When it also writes code, give it an area
  that the worktree sessions do not change.

## Ending

- When the session ends, Claude Code asks whether to keep a named worktree. Keep it to reopen it later with its build.
- Remove a worktree that is no longer used, once its commits are on `main`:
  `git worktree remove .claude/worktrees/NAME` and `git branch -d worktree-NAME`.
- A worktree session's transcripts are in a folder of their own under `~/.claude/projects/`, named from the worktree's
  path.
