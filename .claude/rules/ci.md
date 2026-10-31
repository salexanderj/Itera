---
paths:
  - ".github/**"
---

## CI

GitHub Actions builds and tests the repository. The workflows are the framework's: a game pulls them from Itera
unchanged, and sets what differs through repository variables. The `issues` skill says how to read a run.

### The workflows

- `.github/workflows/ci.yml` runs on every push to `main`, on each pull request, and by hand. On Linux it checks the
  format (`dotnet csharpier check .`), builds in Debug, and runs every test project. A newer push cancels a run still
  going, so a burst of pushes runs the suite once.
- `.github/workflows/nightly.yml` runs each night that `main` changed, and by hand. It builds and tests in Release on
  Linux, and in Debug on Windows and on macOS (`macos-latest`, an arm64 Mac), each on its own schedule (below).
- `.github/actions/prepare` installs the SDK that `global.json` pins and restores the NuGet cache. It fetches the Git
  LFS files through a cache keyed by the list of LFS objects, so a run downloads only the files the cache lacks.
  GitHub limits a month's LFS downloads.
- `.github/actions/test` builds, runs the suite through `tools/tests/timings.py`, writes its report to the run's
  summary, and uploads the results and the `dotnet test` output.

### Repository variables

Set one with `gh variable set NAME --body VALUE`. Unset, each has the effect in the last column.

| Variable | What it sets | Unset |
| --- | --- | --- |
| `CI_LINUX_RUNNER` | The runner label of the Linux jobs | `ubuntu-latest` |
| `CI_WINDOWS_SCHEDULE` | When the Windows job runs: `nightly`, `weekly` (Sunday's run, if `main` changed that week), or `off` (only by hand) | `nightly` |
| `CI_MACOS_SCHEDULE` | When the macOS job runs, as for Windows | `nightly` |
| `CI_TEST_TIME_SCALE` | `ITERA_TEST_TIME_SCALE` for the Linux jobs: it stretches every test's wait limit (`TestTime`) | 1 |
| `CI_PLATFORM_TEST_TIME_SCALE` | `ITERA_TEST_TIME_SCALE` for the Windows and macOS jobs, whose runners are slower | 4 |
| `CI_PLATFORM_TEST_THREADS` | The test classes run at once on Windows and macOS, one project at a time (`--threads`) | 0, one a processor |
| `CI_TEST_WALL_LIMIT` | The seconds the suite may take before the job fails | No limit |

### Rules

- Keep the workflows the same for every repository. Put what differs in a variable, with a default that suits Itera.
- The runners have no GPU, so no test may need a Vulkan device. GPU checks (`validate.sh`) and benchmarks run on the
  development machine.
- Shell steps that run on Windows use `shell: bash`, and Python there is `python`, not `python3`.
- Use each action at its newest major version, and change every use of an action together.
