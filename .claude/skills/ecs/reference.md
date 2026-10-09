# ECS references and audit record

## Sources

Arch and its extensions (read the source for the pinned version first; the docs lag behind it):

- Arch source at the 2.1.0 tag: https://github.com/genaray/Arch/tree/v2.1.0
- Arch.Extended (Arch.System, the source generator, Arch.LowLevel): https://github.com/genaray/Arch.Extended
  (Arch.System 1.1.0 is commit `1defffe`; the removed generator 2.1.0 is commit `3a65aa6`)
- Arch documentation: https://arch-ecs.gitbook.io/arch. Every page has a Markdown copy at the same
  URL plus `.md`; the index is https://arch-ecs.gitbook.io/arch/llms.txt. The most useful pages are
  "Entities in Query", "Structural changes", "Query-Techniques", and "Pass on data". The
  "EntityReference" page does not apply to 2.1.0, and the "Pass on data" example of a `static` lambda
  that uses a local does not compile.
- Arch wiki (2024, partly older than 2.0): https://github.com/genaray/Arch/wiki
- Job scheduler used by parallel queries: https://github.com/genaray/ZeroAllocJobScheduler
- Package versions on NuGet: https://api.nuget.org/v3-flatcontainer/arch/index.json (replace `arch`
  with `arch.system` or `arch.system.sourcegenerator`)

General ECS and .NET guidance:

- .NET 10 runtime changes, including escape analysis for delegates and why closures still allocate:
  https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime
- Designing with Flecs (library-independent advice on small components, single-purpose systems, and
  cached queries): https://www.flecs.dev/flecs/DesignWithFlecs.html
- ECS FAQ (Sander Mertens): https://github.com/SanderMertens/ecs-faq
- Unity Entities on the cost of structural changes and deferring them with command buffers:
  https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/systems-manage-structural-changes-intro.html

## Audit record, 2026-09-22

Audit of the ECS code at commit `6a57524` of the Zorf TD archive (`salexanderj/ZorfTD-archive`) against the rules in
`SKILL.md`.

### Fixed

- `HeartbeatSystem.Update` passed `World.Query` a lambda that captured a local (`logger`). That
  allocated 88 bytes every fixed tick (about 5 KB/s at 60 Hz). It now runs an `IForEach` struct with
  `InlineQuery`, and `HeartbeatSystemTests.Update_allocates_nothing` measures 0 bytes. The test fails
  with 88,000 bytes over 1000 updates against the old code.
- `Arch.System.SourceGenerator` 2.1.0 was referenced by Itera.Engine with no `[Query]` methods. Its
  output fails this repo's build (see `arch-notes.md`). Removed from `Itera.Engine.csproj` and
  `Directory.Packages.props`.
- Added `EcsChecks` and `CheckedWorldExtensions` (Debug-only precondition checks) with tests in
  `tests/Itera.Engine.Tests/Ecs/`, and the `SystemAllocation` helper (`tests/Shared/`).

### Open

- Systems are disposed twice: by the DI container and by `Group<T>.Dispose` from `EcsWorld.Dispose`.
  Harmless while `BaseSystem.Dispose` is empty; the rule that `Dispose` is idempotent covers it.
- System run order is the registration order in the game's composition (`AddGame`), with a comment on each
  registration saying why it sits there.
- No deferred-destruction tag, cleanup system, or stable network id component exists yet
  (`design.md` marks both as open design choices).
- Frame systems receive the frame delta but not `FixedStepAccumulator.Alpha`, which interpolation
  needs. (Closed 2026-09-22: systems receive `FixedStepTime` and `FrameTime`.)
- `Directory.Packages.props` pins `Arch.LowLevel` 1.1.5, but no project references it and transitive
  pinning is off, so the pin has no effect. Arch 2.1.0 depends on 1.1.5 anyway. (Closed 2026-09-22: pin removed.)

### Confirmed sound

- `EcsWorld` creates the `World` in its constructor. That is required: the system factories in
  a game's composition read `EcsWorld.World` before `EcsWorld.Initialize` runs.
- `EcsWorld.Dispose` is idempotent, disposes both groups, then destroys the world. It runs after
  physics shutdown, and no system touches physics.
- `EcsWorld.FixedUpdate` and `Update` run `BeforeUpdate`, `Update`, and `AfterUpdate` for their group,
  on the main thread.
- `HeartbeatSystem` builds its `QueryDescription` once in a `static readonly` field with one `WithAll`,
  and its component types are first used on the main thread.
- `probe.sh` reported every checked behaviour unchanged for Arch 2.1.0 and Arch.System 1.1.0.

## Update, 2026-09-22 (pre-feature review)

- `IFixedUpdateSystem` is `ISystem<FixedStepTime>` and `IFrameUpdateSystem` is `ISystem<FrameTime>`, so
  fixed systems see the tick number and frame systems see `Alpha`. `HeartbeatSystem` and its tests
  moved to `BaseSystem<World, FixedStepTime>`; `SystemAllocation` is generic over the time type.
- `SystemAllocation` and `ArchCollectionDefinition` live once, in `tests/Shared/`, compiled into both
  test projects. (2026-10-04: `ArchCollectionDefinition` was replaced by `ArchStartup`; test classes that use Arch
  now run in parallel.)
- ECS now initialises after physics, network, and UI, and shuts down before them (one ordered step
  list in `GameLoop`).
- Still open: the deferred-destruction tag and cleanup system, the stable network id component, and
  systems being disposed twice (harmless).

## Since 2026-09-25

The game the engine was built with added its own components and systems (characters, loose bodies, walking, world
generation, tools, cameras) in its own folders; that record is in the Zorf TD archive. In 2026-10 the general ones
moved into the engine (`src/Itera.Engine/Components/`, `Systems/`; `.claude/rules/systems.md`), each with a behaviour
test and a zero-allocation test in `tests/Itera.Engine.Tests/Systems/`.

## Update, 2026-09-22 (render thread)

Drawing moved to a render thread. ECS stays on the main thread. Frame systems run once per frame the
render thread asks for, so they pause while presentation blocks; fixed systems never pause.

