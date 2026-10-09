#!/usr/bin/env bash
# Re-checks the Arch behaviours that the ecs skill states as rules, against the Arch and Arch.System
# versions pinned in Directory.Packages.props. Run it after changing either version.
# Exit 0: every behaviour is unchanged. Exit 1: at least one line says CHANGED; re-read the Arch source
# for that behaviour and update arch-notes.md and SKILL.md before relying on them.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet run -c Release --project "$here/probe" -nologo -v q
