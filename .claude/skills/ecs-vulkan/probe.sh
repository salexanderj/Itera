#!/usr/bin/env bash
# Re-checks the facts that the ecs-vulkan skill states as rules: .NET struct and matrix layout, glslang buffer
# layouts through the pinned Shaderc, and Arch chunk memory. Run it after changing the .NET SDK, Arch, or
# Silk.NET version.
# Exit 0: every fact is unchanged. Exit 1: at least one line says CHANGED; re-check that fact and update
# notes.md and SKILL.md before relying on them.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet run -c Release --project "$here/probe" -nologo -v q
