---
name: audit
description: Read-only audit of the Itera codebase for sound design, performance, and robustness. Writes a report to docs/audits/ and compares it with the previous report. Run with /audit.
argument-hint: "[1-12 | 3 5 8 | changes | continue]"
disable-model-invocation: true
effort: high
allowed-tools: Bash(git rev-parse *) Bash(git status *) Bash(git log *) Bash(git diff *) Bash(dotnet build *) Bash(dotnet test *)
disallowed-tools: Workflow Agent AskUserQuestion
---

# Itera audit

Audit the Itera codebase for sound design, performance, and robustness, and write one report of what could be done better. Work alone. Keep token use low by searching before reading.

Arguments: $ARGUMENTS

Choose the mode from the arguments:

- No arguments: a new full audit of areas 1 to 12.
- Area numbers or ranges (for example `3 5 8` or `1-6`): a new audit of those areas only. Area 1 always runs.
- `changes`: a new audit of what changed since the last audit. Find the newest report in docs/audits/ and the commit it records. Run area 1, list the files changed since that commit (`git diff --stat <commit>..HEAD`), and apply the checks of every area that covers those files to the changed code and the code that calls it. Then check each open finding of the previous report and mark it fixed or still open. If there is no previous report, run a full audit.
- `continue`: resume the newest report in docs/audits/ at its first unticked area, within the scope that report records.

## Rules

1. Read-only. Change no source, test, shader, doc, config, or skill file, and do not commit. The only file you write is the report.
2. Search first, read second. Use Grep and Glob with narrow patterns, then read only the line ranges that matter. Read a whole file only when it is central to the area. Never read the same file in full twice; keep notes in the report instead.
3. Keep command output short: pipe build, test, and log output through grep or tail.
4. CLAUDE.md is already in your context; read an area's `.claude/rules/` file with the Read tool before auditing that area. Do not read ARCHITECTURE.md in full. Read section 9 (open work) and the headings of section 8's decisions (`grep -n '^### ' ARCHITECTURE.md`). Read a section 8 decision in full only when a finding touches it. Skip the research write-ups in docs/.
5. Do not report anything the docs list as known or left for later, unless you have new evidence that it is worse or more urgent. Do not propose reversing a recorded decision (ARCHITECTURE.md section 8, the user's choices) without concrete evidence; put such proposals in their own section.
6. Do not propose changes to shader performance. Check shaders only where they meet the C# code: struct layouts, bindings, and push constants against the GpuLayout tests.
7. Before you search or read src/Itera.Engine/Rendering/, src/Itera.Engine/Ecs/, a Systems/ or Components/ folder, or Rendering/GpuData/, load the matching project skill (vulkan, ecs, or ecs-vulkan) once. Open only the rule files in it that the area needs.
8. Every finding names a file and line range. Before you record a finding of Medium or above, check it again: follow the call path, the callers, and the thread it runs on. If you cannot confirm it, lower its confidence instead of dropping it.
9. Give each finding a stable ID: the area code and a short name, for example THREADS-voxel-memory-lock. If the previous report has a finding for the same problem, reuse its ID.
10. The file and type names in this skill were correct when it was written. If one no longer exists, find its successor by search and list the old name under "Stale names in the audit skill".
11. No filler. If an area is sound, write one line saying so. Record at most 40 findings; if you have more, keep the most important.
12. Put open questions in the report instead of asking me.

## Report file and progress

Create docs/audits/audit-<today's date>.md (add -2, -3, and so on if the name is taken). At the top, record the date, the commit (`git rev-parse HEAD`), the mode, the areas in scope, and a checklist of those areas. After each area, write its findings into the report and tick its box. If your context is compacted, reread the checklist and continue at the first unticked area.

If docs/audits/ holds an earlier report, read only its finding IDs, titles, and severities (by grep), not the whole file. In the final pass, mark each earlier finding as fixed, still open, or not checked (outside this run's scope).

## Areas, in order

1. BASE, baseline. Record `git rev-parse HEAD` and `git status --short`. Run `dotnet build` and `dotnet test`; record only warnings, errors, failures, and skipped tests. If the game can open a window here, run one Release build for about 30 s that exercises streaming (see Commands in CLAUDE.md), end it with `timeout --preserve-status -s INT`, and grep the newest log for the frame stats, voxel stats, and `Meshes over` lines and any Warning or Error.
2. SIZE, size map. Count lines in every .cs and GLSL file. Record the total and the 30 largest files; the 15 largest get a structure check in area 12.
3. THREADS. Grep for `lock (`, `Interlocked`, `volatile`, `Monitor.`, `new Thread`, `Task.Run`, `ThreadPool`, `Parallel.`, and wait handles. Read the hand-offs in full: FrameExchange, RenderThread, VoxelWorkers and ColumnJob, MeshWorkers and the jobs WorldMeshes hands them, and the VoxelMemory lock. Check what each thread touches against the thread rules in CLAUDE.md, including the one Vulkan call the main thread may make (`StagingBuffer.Flush`).
4. GPU, Vulkan and GPU memory. FrameRenderer, FrameSync, swapchain recreation, GpuMemoryAllocator, BlockSuballocator, StagingBuffer, GpuRangeHeap, and the barriers in MeshPass, DepthTarget, DebugLinePass, ParticlePass, and the other passes. Check synchronisation against the vulkan skill's synchronization.md, resource lifetime across frame slots, and what happens when a heap or block is full or fragmented.
5. HOT, hot paths and allocation. The main loop in GameLoop, VoxelWorld.Update, WorldMeshes.Upload, VoxelColliders.Update, LooseBodyVoxels.Refresh, ModelInstances, and the update methods of the fixed and frame systems. In those files, grep for LINQ, lambdas, new collections, `$"`, `params`, `.ToString()`, `Enum.`, and foreach over interfaces. Then check which of these paths the zero-allocation tests do not cover.
6. LIFE, resource lifetime and failure. Grep for `Dispose(`, native allocation, and Bepu add and remove calls. Check that Dispose is idempotent, the shutdown order in GameLoop.CreateSteps, every `SetShape` call against the Bepu note in `.claude/rules/physics.md`, and what happens when the voxel memory budget is full, a worker throws, a mod throws, or the device is lost.
7. DET, determinism. In src/Itera.Engine/Voxels/Generation/, src/Itera.Engine/Maths/, src/Itera.Engine/Animation/, the game's WorldGeneration/, and VoxelEditing, count hits per file first, then check each hit for `Math.`, `MathF.`, `Random`, `Vector2.`, `Vector3.`, `Dictionary`, `HashSet`, `FusedMultiplyAdd`, and `Parallel` against the determinism rules in `.claude/rules/voxels.md`. The edit log will be the save and join payload, so replaying edits must give the same voxels on every machine.
8. INPUT, untrusted input. EditLogSerializer, the save format, ModManifestReader, and the options validators. Check bounds, counts, sizes, and version handling as if a hostile peer sent the data. Check that every numeric option in EngineOptions and PlayerOptions has a range in its validator.
9. LAYER, layering and readiness. Check that project references follow Itera.Modding <- Itera.Engine <- Game. Grep the engine for one game's terms (names of a game's content, materials, or rules); the engine names no game. Note what stands in the way of the open work in ARCHITECTURE.md: a headless host with no window or GPU, a match without a voxel world, more than one player (grep LocalPlayer), and a mod API narrower than IModContext.Services.
10. RULES, project rules. Check the Conventions section of CLAUDE.md (and `.claude/rules/rendering.md` for GpuData structs) by grep: logger calls that bypass [LoggerMessage], per-frame logs above Trace, string comparisons without StringComparison, paths joined with "/", disposal of Silk.NET API containers, time structs not passed by `in`, and GpuData structs without a GpuLayout test.
11. TESTS. List the test files. For the risky code found in areas 3 to 8, check whether a matching test file exists and read only its method names. Grep the tests for Thread.Sleep, Task.Delay, and Stopwatch to find tests that depend on timing.
12. STRUCT, structure. For the 15 largest files, read member lists and the longest methods, not whole files. Look for types that do too much, duplicated logic, and unclear ownership.

Throughout, also note statements in CLAUDE.md, `.claude/rules/`, the skills, or ARCHITECTURE.md that the code no longer matches (area code DOCS), and code likely to fail on Windows or macOS (area code XPLAT).

## Final pass

When every area in scope is ticked, rewrite the report into these sections:

1. Summary: the baseline results in a few lines, then the ten most important findings, one line each.
2. Since the previous report: its findings now fixed, still open, or not checked, and the count of new findings.
3. Findings, ordered by severity, then effort: ID, title, severity, confidence, files and lines, what is wrong, why it matters, suggested fix, effort (S, M, L), and new or still open.
4. Known items with new evidence.
5. Recorded decisions worth revisiting, with evidence.
6. Readiness for the open work (ARCHITECTURE.md section 9).
7. Docs that no longer match the code.
8. Hypotheses to measure, each with the exact measurement.
9. Open questions for me.
10. Stale names in the audit skill.
11. Areas found sound, one line each.

Severity:
- Critical: crash, hang, data race, GPU fault, memory corruption, a save or determinism break, or a way for a peer's data to crash or corrupt the game.
- High: a likely bug, a clear cost on a hot path, or a broken project rule.
- Medium: a design weakness that will slow the open work, or risky code without tests.
- Low: clarity, naming, duplication, small cleanups.

Confidence: confirmed (code path traced, or shown by a test or log), likely, or hypothesis (needs a measurement).

Then reply with the report's path, the counts by severity, the count of earlier findings now fixed, and the top five findings.
