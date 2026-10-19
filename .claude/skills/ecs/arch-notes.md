# Arch 2.1.0 and Arch.System 1.1.0: checked behaviour

Checked against the source the NuGet packages were built from, and by running `probe/Program.cs` in Release. Paths are relative to each repository.

- Arch 2.1.0: `github.com/genaray/Arch`, tag `v2.1.0`, commit `04d52e7` (the commit in the package's
  nuspec). The assembly inside reports version 2.0.0; trust the package version.
- Arch.System 1.1.0: `github.com/genaray/Arch.Extended`, commit `1defffe`.
- Arch.System.SourceGenerator 2.1.0 (removed from Itera): `Arch.Extended` commit `3a65aa6`.

The GitBook docs (`arch-ecs.gitbook.io/arch`) and the GitHub wiki are partly out of date for 2.1.0
(the wiki is from 2024). Where they disagree with the source, the source wins. Known disagreements are
marked below.

## Build flavour

- The `Arch` package is the Release build without `EVENTS` or `PURE_ECS` (CI packs the `Default`
  flavour as `Arch`, `Events` as `Arch-Events`, the PureECS flavour as `Arch-PureECS`; `.github/workflows`).
- Release means every `Debug.Assert` in Arch is compiled out. Nothing in Arch checks its arguments.
- Without `EVENTS`, `World.SubscribeEntityCreated`, `SubscribeEntityDestroyed`, `SubscribeComponentAdded`,
  `SubscribeComponentSet`, and `SubscribeComponentRemoved` exist but their bodies are inside `#if EVENTS`
  (`src/Arch/Core/Events/World.Events.cs:40` onward). Handlers never run. Probe: 0 calls for 1 create.
  The GitBook "Events" page says to set the flag or use the `Arch-Events` package.

## World

- `World.Create(chunkSizeInBytes = 16_384, minimumAmountOfEntitiesPerChunk = 100, ...)`
  (`src/Arch/Core/World.cs:104`) takes a lock and stores the world in the static `World.Worlds` array
  (`World.cs:75`). World ids are recycled after `Dispose`. The lock is on the array itself, and `Create` replaces
  the array when it grows (`Array.Resize`), so a second thread can then lock the new array while the first still
  works under the old one. Worlds made on several threads are safe only while the array does not grow;
  `ArchStartup` gives it room for 65,536 worlds in the tests. The game makes its worlds on the main thread only.
- `World.Dispose` / `World.Destroy(world)` is idempotent (`_isDisposed`). It clears the world and frees
  its id. It does not dispose component values.
- `World.SharedJobScheduler` is static (`World.cs:92`): one scheduler for every world.
- `World.Query(in QueryDescription)` looks the description up in a per-world dictionary and creates a
  `Query` on the first call (`World.cs:388`). The `Query` re-matches archetypes only when the set of
  archetypes changed.
- `QueryDescription` equality and the query cache use the hash code only (`src/Arch/Core/Query.cs:451`),
  and archetypes are looked up by a hash of their component bit set. A hash collision between two
  different descriptions or component sets would mix them up. It is unlikely with few types; there is
  nothing to do about it in user code.

## Entity

- `Entity` is `Id`, `WorldId`, `Version` (12 bytes; `src/Arch/Core/Entity.cs`). `Entity.Null` is
  (-1, 0, -1). `default(Entity)` is (0, 0, 0). They are not equal. `IsAlive` returns false for both
  because it rejects `Version <= 0`.
- `World.IsAlive(entity)` (`World.cs:1640`) is the only call that compares the version. `Get`, `Set`,
  `Has`, `Add`, `Remove`, `TryGet`, `TryGetRef`, and `Destroy` use `entity.Id` alone.
- Destroying an entity queues its id with `Version + 1` for reuse (`World.cs:256`). The next create
  gets the same id with the new version.
- Probe: after destroy and re-create, `Get<A>(stale)` returned the new entity's value, and
  `Destroy(stale)` destroyed the new entity.
- There is no `EntityReference` type in 2.1.0 (not in `Arch.xml`, not found by reflection). The GitBook
  "EntityReference" page describes an older version. Store `Entity`; check with `IsAlive`.
- `World.IsAlive(entity, out bool exists)` returns `ref EntityData`, for a check and an access with one
  lookup.
- The `Entity` extension methods (`entity.Get<T>()`, `entity.IsAlive()`, ...) read
  `World.Worlds.DangerousGetReferenceAt(entity.WorldId)` (`src/Arch/Core/Extensions/EntityExtensions.cs:72`):
  no bounds check, and a null world after that world is disposed.

## Components

- `ComponentRegistry` (`src/Arch/Core/ComponentRegistry.cs:79`) is static and has no lock. `Component<T>`'s
  static constructor registers `T` on first use. Registering two new types at once from two threads can
  corrupt the dictionary. Registered types are never released, which also keeps a mod's assembly load
  context alive (ARCHITECTURE.md section 6). The tests register every component type at start, on one thread
  (`tests/Shared/ArchStartup.cs`), so their classes can run in parallel.
- Component ids start at 1. `ComponentType.ByteSize` is `Unsafe.SizeOf<T>()` for structs and the
  pointer size for classes.
- Class components and structs with reference fields are allowed by Arch. They are stored in normal
  managed arrays that the GC scans.
- `Add<T>` of a component the entity already has: `World.Move` asserts `source != destination`
  (`World.cs:331`), which is compiled out. Probe: no exception, and the entity's slot index became 2
  in an archetype holding 2 entities. `Get` then returns a value that the query never visits.
- `Get<T>` of a component the entity lacks: `Chunk.Index<T>()` (`src/Arch/Core/Chunk.cs:394`) reads the
  id-to-array lookup without a bounds check and gets -1, then reads `Components[-1]`. The scratch test
  returned another component's value. Not in the probe because it reads outside the arrays.
- `TryGet<T>` and `TryGetRef<T>(entity, out bool exists)` check the component but not the version.

## Queries

- `WithAll<T>()`, `WithAny<T>()`, `WithNone<T>()`, `WithExclusive<T>()` assign the filter
  (`Query.cs:395`, `src/Arch/Templates/QueryDescription.WithAll.cs:13`); they do not add to it. They
  return `ref QueryDescription` and mutate the value they are called on. Probe:
  `WithAll<A>().WithAll<B>()` holds only `B`.
- `Exclusive` cannot be combined with `All`, `Any`, or `None` (a compiled-out assert).
- `World.Query<T0...>(in description, ForEach<T0...>)` (`src/Arch/Templates/World.Query.cs:8`),
  `InlineQuery`, and the parallel variants call `chunk.GetFirst<T>()` for each type argument without
  checking that the description requires it. A type that only appears in `WithAny`, or not at all,
  reads another array. The scratch test read `A`'s value through a `B` parameter.
- Iteration runs from the last archetype to the first, the last chunk to the first, and the last
  entity to the first (`EntityEnumerator.MoveNext`, `src/Arch/Core/Enumerators.cs:390`).
- `Chunk.GetSpan<T>()` returns a span over the chunk's capacity, not its count. `foreach (var index in chunk)`
  visits `0..Count-1` backwards.
- Chunk sizing: at least 16 KB and at least 100 entities per chunk, rounded up to a multiple of 16 KB
  (`src/Arch/Core/Archetype.cs:806`). Each archetype allocates its first chunk when it is created.
- Chunks have `Count`, not `Size` as the wiki says.

## Structural changes

- Removal fills the gap with the archetype's last entity (`Archetype.Remove`, `Chunk.Remove` at
  `Chunk.cs:321`). Probe: query order `4,3,2,1,0`, then `3,2,4,0` after destroying the entity with V=1.
- Inside a query, with backwards iteration:
  - Destroying the current entity is safe. Probe: 300 visited once each, 0 left.
  - Creating entities is safe; new entities go to the end and are not visited. Probe: 300 visits,
    600 entities.
  - Destroying an entity not yet visited moves an already visited entity into its place. Probe: one
    entity visited twice.
  - `Add`/`Remove` moves the entity to another archetype, which the query may visit later. Probe: 5 of
    5 entities visited twice.
- The GitBook "Entities in Query" page agrees with these results.

## CommandBuffer (`src/Arch/Buffer/CommandBuffer.cs`)

- Recording (`Create`, `Destroy`, `Set`, `Add`, `Remove`) takes locks and may be called from several
  threads. `Playback` is not thread-safe; call it on the main thread.
- `Playback(world, dispose: true)` (`CommandBuffer.cs:283`) applies creates, then adds, then sets, then
  removes, then destroys. Record order does not matter. Probe: recording `Remove<A>` then `Add<A>`
  left the entity without `A`.
- `dispose: true` (the default) clears the buffer after playback so it can be reused. `false` keeps
  the commands for another playback.
- `Create(ComponentType[])` returns a placeholder entity with a negative id. Use it only with the same
  buffer; it becomes real during playback.
- Playback checks nothing (asserts compiled out). Probe: `Destroy` recorded twice for one entity threw
  `NullReferenceException` during playback.
- Recording and playing back an `Add` and a `Remove` of a tag allocated about 1.8 KB each time, after warm-up
  (measured, not a probe). `World.Add`/`World.Remove` on entities collected during
  the query and changed after it allocated nothing once the move between the two archetypes had happened once.

## Parallel queries (`src/Arch/Core/Jobs/World.Jobs.cs`)

- `ParallelQuery`, `InlineParallelQuery`, and `InlineParallelChunkQuery` throw unless the static
  `World.SharedJobScheduler` is set (probe). They block the caller until every job finishes.
- Work is split per archetype into `Environment.ProcessorCount` ranges of chunks (`World.Jobs.cs:108`).
  An archetype with one chunk runs on one thread.
- Each job gets its own copy of the job struct, so results written to struct fields are lost. Use a
  thread-safe accumulator.
- No structural changes and no first use of a component type inside a job.

## Arch.System 1.1.0 (Systems.cs of Arch.Extended)

- `ISystem<T>`: `Initialize`, `BeforeUpdate(in T)`, `Update(in T)`, `AfterUpdate(in T)`, `Dispose`.
  `BaseSystem<W, T>` implements all of them as empty virtual methods and holds `World`.
- `Group<T>` runs its systems in the order they were added, one phase at a time.
  `Group<T>.Dispose` disposes every system (probe).
- Metrics are compiled out (`#define ARCH_METRICS_DISABLED` at the top of the file).
- `Group<T>.Get<G>()` returns the first nested group's search result even when it found nothing
  (`Systems.cs:185`). Probe: a system after a nested group was not found.
- The package has no dependency on Arch; it works with any world type.

## Arch.System.SourceGenerator 2.1.0 (removed)

Recorded so it is not re-added by mistake. Tested in a scratch copy of the repo.

- The generated query method starts with `#nullable enable` and runs `foreach` over a `Query?` field.
  Under this repo's settings that is error CS8602. `.editorconfig` sections for the generated path did
  not suppress it.
- The generator emits public generic attributes (`All<T0...>`, `Any<...>`, `None<...>`,
  `Exclusive<...>`) into every assembly that references it. With both Itera.Engine and a game
  referencing it, a generic attribute is error CS0436.
- Other limits in that version:
  - Only the first `[All]`, `[Any]`, `[None]`, `[Exclusive]` attribute on a method is read.
  - Attributes are matched by a substring of their name.
  - Any parameter whose type is named `Entity` is treated as the Arch entity.
  - The generated `Update` passes only the first `[Data]` parameter.
  - Nested and generic classes are not supported, and parallel queries need `static` methods.
  - A static mutable field in the generator is not thread-safe (fixed in `e21a00b`).
- Arch.Extended `master` fixes the CS8602 (`_Query!`) and moves the attributes into Arch.System, but
  no release contains the fixes. The latest releases on NuGet are Arch 2.1.0, Arch.System 1.1.0, and
  Arch.System.SourceGenerator 2.1.0.

## .NET 10 and query allocations

Measured with `GC.GetAllocatedBytesForCurrentThread` in Debug and Release, per call of a method that
runs one query over 1000 entities (probe):

| Query form | Bytes per call |
|---|---|
| Lambda capturing a local | 88 |
| Lambda capturing a parameter | 88 |
| Lambda capturing `this` (reads a field) | 64 |
| Lambda using only static members, or `static` lambda | 0 |
| `IForEach` struct with `InlineQuery` | 0 |
| Chunk loop | 0 |

A capturing lambda allocates a closure object and a delegate each time the enclosing method runs.
.NET 10's escape analysis can put a delegate on the stack only when it does not escape the method;
passing it to `World.Query` makes it escape, and closures are never stack-allocated in .NET 10
("What's new in .NET 10 runtime", Microsoft Learn).
