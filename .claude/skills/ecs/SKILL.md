---
name: ecs
description: Rules and checks for any ECS work in Itera (Arch 2.1.0 and Arch.System 1.1.0). Use before reading, writing, reviewing, or debugging entities, components, systems, or queries, code under src/Itera.Engine/Ecs/ or a Systems/ or Components/ folder of the engine or the game, any code that uses Arch.Core, Arch.System, or Arch.Buffer, World, Entity, QueryDescription, CommandBuffer, BaseSystem, IFixedUpdateSystem, IFrameUpdateSystem, or EcsWorld. Also use before changing an Arch package version.
---

# ECS in Itera

Arch checks nothing at run time. The `Arch` NuGet package is a Release build, so its `Debug.Assert`
calls are gone. A stale entity handle, an `Add` of a component the entity already has, or a query
that reads a component the entity lacks corrupts the world or reads the wrong memory, with no
exception. There is no validation layer. The project's substitutes are the Debug-only checks in
`Itera.Engine.Ecs.EcsChecks`, the zero-allocation system test, and `probe.sh`. Every rule below that
says "probe" was confirmed by `probe/Program.cs` against the pinned packages.

## Workflow for every ECS change

1. Read the files you will change in full, plus `src/Itera.Engine/Ecs/EcsWorld.cs` and the method
   that registers systems (`AddGame` in `src/Game/GameServiceCollectionExtensions.cs`; the engine registers none, but its
   own systems live in `src/Itera.Engine/Systems/`). Read `design.md` before adding a component type, an entity kind, or a system.
   Read `arch-notes.md` before using an Arch API that the codebase does not use yet.
2. For each system, write down its group (fixed or frame), its place in the registration order, the
   components it reads, the components it writes, and the structural changes it makes.
3. Write the code against the rules below.
4. Add or update the system's tests: `tests/Game.Tests/` for game systems,
   `tests/Itera.Engine.Tests/Systems/` for engine systems. Both projects compile the helpers in
   `tests/Shared/` (`SystemAllocation`, `ArchStartup`, namespace `Itera.TestSupport`). Every
   system has a zero-allocation test (`SystemAllocation.BytesAllocated(system, time)` must be 0, with a
   `FixedStepTime` or `FrameTime`) and a behaviour test. Test classes run in parallel, those that touch Arch
   too: `ArchStartup` registers every component type before any test and keeps `World.Worlds` from growing (see
   arch-notes.md). A component type outside the engine's, the game's, and the test assembly's own types (a type from
   another library) must be added there. A class that reads the whole process (garbage collection, timings) carries
   `[Collection(SerialCollectionDefinition.Name)]`. A test that measures allocation itself
   takes its starting value from `SystemAllocation.StartMeasurement()`, which runs a gen0 collection
   first: without it, a collection triggered by another test's allocation makes
   `GC.GetAllocatedBytesForCurrentThread` report up to 8 KB that were never allocated.
5. `dotnet build` (zero warnings), `dotnet test` (Debug, so `EcsChecks` run), `dotnet csharpier check .`.
6. Run the game for a few seconds (`timeout --preserve-status -s INT 25 src/Game/bin/Debug/net10.0/Game --Engine:Session:Mode=Offline`)
   and read the newest log: no Critical line, `Shutdown complete ... exit code 0`. A failed `EcsChecks`
   check throws, which the loop logs at Critical. A Debug run stays on the loading screen for about 10 s, with the
   game's input blocked; for a short check of play add `--Engine:Loading:Enabled=false`, or run 20 s or more.
7. After changing the Arch or Arch.System version in `Directory.Packages.props`, run
   `.claude/skills/ecs/probe.sh` (without bash:
   `dotnet run -c Release --project .claude/skills/ecs/probe`). Any `CHANGED` line means a rule below
   may be wrong: re-read that part of the Arch source and update `arch-notes.md` and this file first.
8. In the summary to the user, state which tests ran and the allocation result.

## Rules

### Entity handles

- Check every handle that did not come from the current query iteration: stored handles, handles
  from another system, handles from outside the ECS. Use `AddChecked`, `RemoveChecked`, `GetChecked`,
  `SetChecked`, and `DestroyChecked` from `CheckedWorldExtensions`, or `world.IsAlive(entity)` when the
  code must handle a dead entity. Arch compares the version only in `IsAlive`. `Get` through a stale
  handle reads the entity that reuses the id, and `Destroy` through a stale handle destroys it (probe).
- Store and compare whole `Entity` values, never `Entity.Id`. The version in `Entity` is what tells a
  recycled id apart. Arch 2.1.0 has no `EntityReference` type, although the online docs describe one.
- Use `Entity.Null` for "no entity". `default(Entity)` is a different value; neither is alive (probe).
- Call through the system's `World` (`World.Get<T>(entity)`), not the `Entity` extension methods
  (`entity.Get<T>()`). The extensions find the world in the static `World.Worlds` array by `WorldId`
  without a bounds check.
- Never send an `Entity` over the network or write one to a save. Ids are local and recycled. Give
  such entities a stable id component (see `design.md`).

### Components

- Components are structs with public fields (CA1051 is off for this). No reference-type fields
  (classes, strings, arrays, lists) without the user's approval: they are GC-tracked and slow iteration.
  Store an index or handle into a service-owned array instead.
- Keep components small and single-purpose. Split data that different systems read at different
  times. A tag is an empty struct: `public struct Enemy;`.
- `Add<T>` only when the entity lacks `T`; `Remove<T>`, `Get<T>`, and `Set<T>` only when it has `T`.
  A query's `WithAll`/`WithNone` can guarantee this; otherwise use the checked extensions. An `Add` of
  a component the entity has leaves its slot pointing past the end of its chunk (probe). A `Get` of a
  missing component reads another component's memory.
- State that changes every few ticks is a field in an existing component, not a component that is
  added and removed. Every `Add` and `Remove` copies the entity to another archetype.
- Arch registers a component type on first use in a static dictionary without a lock. First use of a
  type happens on the main thread, never inside a parallel job.

### Queries

- Build each `QueryDescription` once, in a `static readonly` field initialiser, with one call per
  filter that lists every type: `new QueryDescription().WithAll<Position, Velocity>().WithNone<Dead>()`.
  A second `WithAll` replaces the first instead of adding to it (probe). Never call a `With*` method on
  an existing field; on a readonly field it changes a copy.
- Every component type a query reads (lambda parameters, `IForEach` type arguments, `GetSpan` or
  `GetFirst` types in a chunk loop) must be in `WithAll` or `WithExclusive`. Arch reads the array
  without checking the archetype has it. Call `EcsChecks.RequireAll<...>(in description)` for each query
  in the system's `Initialize`.
- In `Update`, write queries in one of three ways:
  1. Default: an `IForEach<T0...>` struct run with `World.InlineQuery<TJob, T0...>(in description, ref job)`.
     Data the body needs goes in the struct's fields.
  2. Hot paths and SIMD: a chunk loop, `foreach (ref var chunk in World.Query(in description))`, with
     `foreach (var index in chunk)` over the entities. `GetSpan<T>()` spans the chunk's capacity, not
     its count, so never loop to the span's length.
  3. A `static` lambda, only when the body uses nothing but the entity's components and static members.
  Never pass a lambda that captures a local, a parameter, or `this`: it allocates 88, 88, and 64 bytes
  per call on .NET 10 (probe). .NET 10 can stack-allocate a delegate, but not a closure.
- `[Query]` source generation is not available. `Arch.System.SourceGenerator` 2.1.0 was removed
  because its output does not compile in this repo (CS8602 under nullable plus warnings as errors, and
  CS0436 when two projects reference it). Re-adding it needs a newer release and the user's approval.
- Queries run from the last entity to the first, and the order changes after every destroy and every
  structural change (probe). Never let a result that must match across peers or runs depend on
  iteration order; sort by a stable key when order matters.

### Structural changes

- Structural changes are `Create`, `Destroy`, `Add`, and `Remove`. Inside a query, only two are safe:
  creating an entity, and destroying the entity currently being visited. Any `Add` or `Remove`, and
  destroying any other entity, can make the query visit an entity twice or skip one (probe). Record
  those in a `CommandBuffer` and play it back after the query ends.
- A system that records structural changes owns one `CommandBuffer` in a readonly field, creates it in
  the constructor, calls `Playback(World)` after its queries in the same `Update`, and disposes it in
  `Dispose`. Reuse it; `Playback` clears it by default.
- `Playback` runs all creates, then adds, then sets, then removes, then destroys, whatever the record
  order (probe). Never record `Remove<T>` and `Add<T>` of the same type for one entity (the result is
  removed). Never record `Destroy` twice for one entity (playback throws `NullReferenceException`).
  Never record changes for an entity that another system may destroy before playback.
- For many entities at once, use the bulk operations: `World.Destroy(in description)`,
  `World.Add<T>(in description, value)`, `World.Remove<T>(in description)`, `World.Create(count, value)`,
  and `World.EnsureCapacity<T...>(count)`.
- An entity that owns something outside the ECS (a Bepu body, a GPU slot, a network id mapping)
  releases it before it is destroyed. `World.SubscribeEntityDestroyed` and the other `Subscribe*`
  methods compile to empty methods in the `Arch` package; handlers never run (probe).

### Systems

- A system implements exactly one of `IFixedUpdateSystem` (simulation, once per 60 Hz step; derive from
  `BaseSystem<World, FixedStepTime>`) or `IFrameUpdateSystem` (presentation, once per rendered frame;
  derive from `BaseSystem<World, FrameTime>`). Both time types come from `Itera.Modding`. Frame systems
  never change simulation state. See `design.md`.
- Register systems with a factory in the game's composition (`AddGame`; engine systems too, which the engine does not
  register itself). Registration
  order is run order within a group. Keep a comment there that says why the order is what it is.
- The constructor stores dependencies only. `Initialize` creates startup entities and runs the
  `EcsChecks.RequireAll` calls. `Update` does the work.
- `Dispose` is idempotent and calls `base.Dispose()` (CA2215). Both the DI container and
  `Group<T>.Dispose` dispose a system.
- Systems keep no per-entity state in fields; that belongs in components. Fields hold services, query
  descriptions, a `CommandBuffer`, and caches.
- All ECS work runs on the main thread. Do not add `ParallelQuery`, `World.SharedJobScheduler`, or a
  `JobScheduler` without the user's decision. The scheduler is static and shared by every world, and Bepu
  already runs its own worker threads.
- Do not rely on `Group<T>.Get<G>()`: in Arch.System 1.1.0 it stops searching at the first nested
  group (probe).

### Performance

- `Update` allocates no managed memory. The zero-allocation test enforces this for every system.
- No LINQ, no `foreach` over an interface-typed collection, and no non-generic Arch overloads (the
  `object` and `Type` versions box) in `Update`. Use the generic batch overloads (`Get<T0, T1>`) when
  reading several components of one entity.
- Logging in `Update` follows CLAUDE.md: per-tick lines are Trace. Compute log arguments into a local
  first.
- Do not call `World.TrimExcess` every frame.

## Enforcement

`.claude/hooks/skill_guard.py` runs before every Edit, Write, NotebookEdit, and Bash call. It denies
file writes under `src/Itera.Engine/Ecs/`, the `Systems/` and `Components/` folders of `src/Itera.Engine` and `src/Game`
(component files use no Arch namespace, so the folder is what marks them),
writes to `.cs` files that use or add `Arch.Core`, `Arch.System`, `Arch.Buffer`, or `Arch.LowLevel`, and
Bash commands that name one of those folders, until the session transcript shows this skill was loaded (a `Skill` call for `ecs`, or
a typed `/ecs`, after the last `/clear`). A denied call names the fix: load this skill, then retry.
The same hook guards Vulkan work for the `vulkan` skill, and ECS-to-GPU work for the `ecs-vulkan` skill.

## Files in this skill

- `arch-notes.md`: Arch 2.1.0 and Arch.System 1.1.0 behaviour, checked against their source.
- `design.md`: how the ECS fits Itera: what is an entity, component and system design, the fixed and
  frame groups, physics, networking, rendering, and mods.
- `reference.md`: sources, and the audit record of the current ECS code.
- `probe.sh` and `probe/`: re-checks the behaviours marked "probe" against the pinned packages.

## Package notes

- `Arch.System 1.1.0` builds and runs against `Arch 2.1.0`. `Arch.Relationships` does not; do not
  add it. `Arch.System.SourceGenerator` 2.1.0 was removed: its `[Query]` output fails this build
  (CS8602, and CS0436 when two projects reference it). Queries use `IForEach` structs or chunk loops.
- The `Arch` package is a Release build: it checks no arguments and its `Subscribe*` event methods
  never fire. `EcsChecks` and `CheckedWorldExtensions` in `Engine/Ecs/` add Debug-only checks.
