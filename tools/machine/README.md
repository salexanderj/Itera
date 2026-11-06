# Machine lock

`machine.py` shares this machine between the builds, tests, game runs, and benchmarks that sessions and people start.
One lock serves every checkout of Itera and of the games made from it, and their worktrees. Linux and macOS; on
Windows every hold is granted at once. Python 3, standard library only. A development tool, not game code.

| Hold | Who takes it | With others |
| --- | --- | --- |
| shared | a build, a filtered test run, a game run, `validate.sh` | any number at once |
| suite | a whole test suite (`dotnet test` with no `--filter`, `timings.py`) | shared, and one suite at a time |
| alone | the benchmark (`run.sh`), `timings.py --alone`, the user's `busy` | nothing else at once |

```bash
tools/machine/machine.py status                   # who holds the machine, and who waits to hold it alone
tools/machine/machine.py busy playing             # the user's: hold it alone (4 h at most; --hours H)
tools/machine/machine.py free                     # end the busy hold
tools/machine/machine.py run --alone -- COMMAND   # any command under the lock (--shared, --suite)
python3 -m unittest discover -s tools/machine     # its tests
```

## Who takes it

- A hook (`.claude/hooks/machine_lock.py`) gives every Bash call that runs `dotnet build`, `test`, `run`, `watch`,
  `publish`, `pack`, or `msbuild`, or a built `Game`, a first line that holds the machine until the call's shell ends
  (`machine.py hold --watch $$`). The command itself runs unchanged, in the same shell. A call that also runs
  `run.sh` or `timings.py --alone` holds it alone.
- When the machine is held alone, or a request to hold it alone waits its turn, the hook refuses a call in the
  foreground and names the holder. The call runs again with `run_in_background`, waits for the machine, and its
  completion notice comes when it has run. A call in the background waits up to an hour, one in the foreground 90 s.
- `tools/benchmark/run.sh` holds it alone, `.claude/skills/vulkan/validate.sh` shared, and `tools/tests/timings.py` as
  a suite (shared with a `--filter`, alone with `--alone`).
- `tools/control/game.py start` holds it shared for the game's life: the game keeps the lock open, so a benchmark
  waits until `game.py stop`. It waits 60 s for the machine, then refuses.
- `ITERA_MACHINE_LOCK=off` in Claude Code's environment turns the hook off. A fault in the hook lets the call run.

## How it works

- The lock is a file in `$XDG_RUNTIME_DIR/itera-machine` (`ITERA_MACHINE_DIR` names another folder), locked with
  `flock`. Shared and suite holds lock it shared, an alone hold exclusively, and a suite also locks a second file.
- Linux gives a waiting exclusive lock no turn of its own, so a request to hold alone writes an entry in `pending/`,
  and new shared holds wait behind it for its first 5 minutes (`PRIORITY_SECONDS`). After that they go ahead again,
  so a holder that never ends, such as a game left running, holds up the request alone and not every session.
- A wait or a refusal names each holder. A game run is named as one, with the `game.py stop` that ends it.
- Each hold writes an entry in `holders/` with its kind, label, folder, process, and start. An entry whose process has
  ended counts for nothing and is removed.
- The process that holds the lock keeps its file open where no child inherits it, so the compiler server or an
  MSBuild node a build leaves running never holds the machine. Only `game.py start` hands its file to the game.
- A command under a hold gets `ITERA_MACHINE` (the kind) in its environment. Under an alone hold, every hold inside it
  is granted at once. Under a shared one, a hold alone is refused, since it would wait for itself.
- When other holds share the machine, a shared hold's command gets `ITERA_TEST_TIME_SCALE=2` unless it is set, so the
  tests' wait limits stretch (`tests/Shared/TestTime.cs`).
- `hold` prints the scale its command gets, the caller's own when set, and the hook's first line exports it as a plain
  value. In a session isolated in a worktree, Claude Code refuses an export inside a condition or with a
  `${NAME:-...}` value.
- `run` exits with its command's code, and 75 when the machine stays held past `--wait`.
