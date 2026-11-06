# Test timing tools

`timings.py` runs the test suite with per-test results (`dotnet test --logger trx`) and prints where its time goes:
the run's wall time, the test-seconds summed, the slowest classes, and the slowest tests. Python 3 and the .NET SDK
only. A development tool, not game code.

```bash
python3 tools/tests/timings.py                          # dotnet test --no-build over the solution, then the report
python3 tools/tests/timings.py --build -- -c Release    # build first; the arguments after -- go to dotnet test
python3 tools/tests/timings.py -- tests/Game.Tests --filter "FullyQualifiedName~ScenarioTests"
python3 tools/tests/timings.py --read DIR --top 40      # the report of an earlier run's .trx files
python3 tools/tests/timings.py --max-wall 600           # fails when the run takes longer than 600 s
python3 tools/tests/timings.py --threads 0              # one test class a processor at once, one project at a time
```

- After the report it prints each failed test's message and the top of its stack trace. When `dotnet test` fails
  with no failed test (the test host ended early), it prints the end of the `dotnet test` output.
- CI runs the suite through it (`.github/actions/test`), with `--max-wall` from the repository variable
  `CI_TEST_WALL_LIMIT` (`.claude/rules/ci.md`).
- xunit runs the test classes in parallel and the tests of one class one after another, so a run lasts at least as
  long as its longest class. Each class line gives its summed seconds, its tests, and when it started and ended in the
  run, so the class that ends the run shows at the top.
- The results and the `dotnet test` output stay in the folder named at the end (`--out DIR` to choose it).
- A run holds the machine (`tools/machine`): as a suite, shared with a `--filter`. Times from a shared machine run
  long, so a run that measures takes `--alone`, and nothing else builds or tests until it ends.
- `.claude/rules/tests.md` holds the rules that keep tests light.

`published.py` starts a published build with no graphics, plays a short offline match, and checks that it ended with
exit code 0 and no Error or Fatal line in its log. With `--steam-library` it also fails when Steam's library did not
load. CI's nightly runs it on each platform (`.github/actions/publish-check`).

```bash
dotnet publish src/Game -c Release -r linux-x64 --self-contained -o publish/linux-x64
python3 tools/tests/published.py publish/linux-x64 --data /tmp/published-data --steam-library
```
