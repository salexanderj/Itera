# Git hooks

`pre-push` checks the format of the files a push changes (`dotnet csharpier check`), as CI's first step checks the
whole tree, so a push that CI would fail on its format never starts a run. It needs the local tools
(`dotnet tool restore`). `git push --no-verify` skips it.

Git runs a checkout's hooks from `.git/hooks`, which a clone does not take from the repository, so each clone's
`.git/hooks/pre-push` calls this script. Worktrees share their checkout's hooks. Git gives the hook the pushed refs on
its standard input once, so the hook keeps them and hands them to each command:

```sh
#!/bin/sh
refs=$(cat)
printf '%s\n' "$refs" | "$(git rev-parse --show-toplevel)/tools/git/pre-push" "$@" || exit 1
# A clone with Git LFS keeps its line after it:
printf '%s\n' "$refs" | git lfs pre-push "$@"
```

Make the hook executable: `chmod +x .git/hooks/pre-push`. A clone without Git LFS leaves out the last line.
