# ECS references and current code

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

## The current ECS code

- `EcsWorld` creates the `World` in its constructor. The system factories in a game's composition read `EcsWorld.World`
  before `EcsWorld.Initialize` runs.
- `EcsWorld.FixedUpdate` runs `BeforeUpdate`, `Update` (each system's timed), and `AfterUpdate` for the fixed group.
  `EcsWorld.Update` does the same for the frame group. Both run on the main thread.
- `IFixedUpdateSystem` is `ISystem<FixedStepTime>`, so fixed systems see the tick number. `IFrameUpdateSystem` is
  `ISystem<FrameTime>`, so frame systems see `Alpha`.
- `EcsWorld.Dispose` is idempotent. It disposes both groups, then destroys the world, after physics shutdown. The DI
  container and `Group<T>.Dispose` both dispose a system, so every system's `Dispose` is idempotent.
- ECS initialises after physics, network, and UI, and shuts down before them (one ordered step list in `GameLoop`).
- System run order is the registration order in the game's composition (`AddGame`), with a comment on each
  registration saying why it sits there.
- Frame systems run once per frame the render thread asks for, so they pause while presentation blocks. Fixed systems
  never pause.
- `SystemAllocation` and `ArchStartup` live once, in `tests/Shared/`, compiled into both test projects. Test classes
  that use Arch run in parallel.
- The engine's general components and systems are in `src/Itera.Engine/Components/` and `Systems/`
  (`.claude/rules/systems.md`), each with a behaviour test and a zero-allocation test in
  `tests/Itera.Engine.Tests/Systems/`.
