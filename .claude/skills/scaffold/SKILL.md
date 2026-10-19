---
name: scaffold
description: "Steps, file lists, and templates for adding to Itera or a game on it: a system (engine or game), an option, a console command, a game message, a generator change, a model from glTF, a sound from a file, music, the game's look and sun, the main camera's lens, a render pass of the game's own, an image view (a minimap, a screen in the world), a game's own on/off setting, a text box or a screen Escape closes, a HUD screen or panel on the UI toolkit, a page in the engine's menu, a game's own menu, a UI style, a physics material, a collision layer or group, a trigger zone, impact events, a test that plays the real game, and a game without voxel terrain. Use before adding any of these, so every place they touch changes together; the checklist tests (ChecklistTests, ShippedSettingsTests, SystemOrderTests, WorldTests) fail when one is missed."
---

# Adding to Itera

Each section lists every file the addition touches, in order, and the test that fails when one is missed. Load the
area's skill first where the hook asks for it (`ecs` for systems and components, `vulkan` for rendering).

## The recipes

The common recipes are below. The others are in files beside this one; read the one you need:

| Adding | File |
| --- | --- |
| A model from glTF, a sound from a file, music | `assets.md` |
| The game's look and sun, the main camera's lens, a render pass of the game's own, an image view | `rendering.md` |
| A game's own on/off setting, a text box or a screen Escape closes, a HUD screen or panel, a page in the engine's menu, a game's own menu, a UI style | `ui.md` |
| A physics material, a collision layer or group, a trigger zone, impact events | `physics.md` |
| A game without voxel terrain | `no-terrain.md` |

## A system

Engine system (any game may register it) in `src/Itera.Engine/Systems/`, or a game's in `src/Game/Systems/`.

1. The class. A fixed system changes the simulation once a step; a frame system only presents (camera, models, UI).

   ```csharp
   /// <summary>What it does, in which group, where it runs and why, what it reads and writes.</summary>
   public sealed partial class NameSystem(World world, /* services */ ILogger<NameSystem> logger)
       : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
   {
       private static readonly QueryDescription s_query = new QueryDescription().WithAll<A, B>();

       public override void Initialize()
       {
           EcsChecks.RequireAll<A, B>(in s_query);
           // Startup entities; console commands: commands?.Register("name", "usage", "help", CommandScope.Match, Run);
       }

       public override void Update(in FixedStepTime step)
       {
           var job = new Job { /* data the body needs */ };
           World.InlineQuery<Job, A, B>(in s_query, ref job);
       }

       private struct Job : IForEach<A, B>
       {
           public void Update(ref A a, ref B b) { }
       }
   }
   ```

   A frame system derives from `BaseSystem<World, FrameTime>` and implements `IFrameUpdateSystem`. `Update` allocates
   nothing (the `ecs` skill's rules: no capturing lambdas, no LINQ, no enum `ToString`).
2. Its registration, in the game's run order with a comment saying why it runs there: `AddFixedSystems` or
   `AddFrameSystems` in `src/Game/GameServiceCollectionExtensions.cs` (`services.AddScoped<IFixedUpdateSystem>(sp =>
   new NameSystem(...))`).
3. The order lists: `s_fixedOrder` or `s_frameOrder` in `tests/Game.Tests/SystemOrderTests.cs`. `SystemOrderTests`
   fails when they differ from the registration. The generated `docs/reference/game-systems.md` lists the order too.
4. Its tests, `<Name>SystemTests` in `tests/Itera.Engine.Tests/Systems/` (engine) or `tests/Game.Tests/` (game), with a
   behaviour test and an allocation test whose name says "allocat":

   ```csharp
   [Fact]
   public void Update_allocates_nothing()
   {
       using var world = World.Create();
       using var system = new NameSystem(world, /* fakes */ NullLogger<NameSystem>.Instance);
       system.Initialize();

       Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FixedStepTime(1f / 60, 0)));
   }
   ```

   `ChecklistTests.Every_system_has_a_test_class_with_an_allocation_test` (engine and game) fails without them.
5. Docs: an engine system in `.claude/rules/systems.md` (or its area's rules file); a game's in its area's rules file.
   The generated `docs/reference/game-systems.md` and `game-commands.md` list it and its commands: write them again (the
   `docs` skill).
6. A position it keeps across steps, in a component or a field, is local to the match's origin and moves when the
   origin does:
   - A game's component field goes in an `IOriginShiftHandler` the game registers in the match's scope
     (`services.AddScoped<IOriginShiftHandler, NameShift>()`; the starter's `FoxRouteShift` moves `FoxRoute.Centre`).
     It also goes in the positions listed in the game's `ChecklistTests`, which fails for a vector field in neither of
     its lists.
   - An engine component's field goes in `OriginShifts`' moves and `ShiftedComponents` (`OriginShiftsTests`).
   - A place the system knows in absolute terms is a `WorldPoint`, made local through the match's `WorldOrigin` when it
     is used: a local position kept from the start goes stale at the first move.
   - The game's first origin is its `IOriginStart` (the starter's `StartPose`), and `OriginShiftSystem` comes first in
     its fixed systems.

## An option

1. The property, with a doc comment that gives its unit, default, and range: an engine option in its settings class in
   `src/Itera.Engine/EngineOptions.cs`; a game's in `src/Game/GameOptions.cs`.
2. Its check: a numeric engine option gets `Range(failures, "Section:Name", value, least, most)` in
   `src/Itera.Engine/EngineOptionsValidator.cs`; a game option a check in its validator in `GameOptions.cs` whose
   failure starts with the key. `ChecklistTests.Every_numeric_option_has_a_range` (engine) and
   `Every_numeric_game_option_has_a_range` (game) fail without it.
3. Its default in `src/Game/appsettings.json`, the code's value (null for none). `ShippedSettingsTests` checks both
   ways: every listed value is the code's default, and every option is listed.
4. A new game options section also goes in `AddOptions` and the `ValidateOptions` list of
   `GameServiceCollectionExtensions`, in `ShippedSettingsTests`' roots, and in `ChecklistTests`' game pairs.
5. Docs: the generated options reference (write it again: the `docs` skill), and the area's rules file for what it
   changes.

## A console command

1. Register it where its services live: a system's `Initialize` (`commands?.Register(name, usage, help, scope,
   handler)`; it goes when the match ends), or a run-wide command class registered at `GameLoop.InitializeUi`. The
   scope: `Local` (this game only), `Match` (host or offline: it changes the shared world), `Offline` (it changes what
   every peer must agree on, or time).
2. Usage errors print `context.PrintUsage()`; failures `context.PrintError(...)` (a startup script with
   `ExitOnFailure` stops on them). A command that must wait holds its script with `DevConsole.Hold` (see `wait` and
   `screenshot` in `EngineCommands`).
3. Its help is what `help` and the generated `docs/reference/game-commands.md` show: write the reference again (the
   `docs` skill), so the reference tests pass.
4. A test that runs it through `ConsoleCommands` and checks its answer and effect.

## A game message

1. A kind byte of 64 or more (`SessionProtocol`; below 64 is the engine's), and the way it may travel (host to
   clients, clients to host, or both).
2. A handler in the match's scope: `GameMessageHandler` for one kind, or an `ISessionGameHandler`
   (`.claude/rules/sessions.md`); sending through `SessionService.BeginMessage`. A position goes absolute, since each
   peer has its own origin: `writer.WriteLocalPosition(origin, local)` and `reader.ReadLocalPosition(origin)` with the
   match's `WorldOrigin` (or `WritePosition` and `ReadPosition` for a `WorldPoint`), never a local `Vector3` as floats.
3. Tests in `tests/Game.Tests` that send damaged bodies and hostile values in every field, each row under the
   `Watchdog` (`HostileValues`, `Watchdog` in `tests/Shared`).
4. A change to the engine's own messages raises `SessionProtocol.Version` and pins the new fingerprint in
   `WireFingerprintTests`.

## A change to what the generator makes

A change of its voxels, or of its world's size or voxel size (its grid: `VoxelGrid.Sized(base, halfWidthMetres,
floorMetres, topMetres)`, voxels of 1 cm to 1 m, up to ±160 km each way, up and down too;
`GameGenerator.WorldGrid` for the starter). A tall world costs memory only round its ground when its generator says
which layers may hold more than one fill (`IChunkGenerator.DescribeRange` and `DescribeLayers`; `HeightfieldGenerator`
answers them itself); one that does not stores every layer of a column, as before. Coarse voxels (50 cm, 1 m) make deformable terrain cheap; shapes under a
voxel then show only as soft dips, walls come in voxel steps, and a generator's centimetre-sized features (a 5 cm
surface layer, small stones) vanish.

1. Raise the generator's `Version` (`GameGenerator.Version` for the starter).
2. Pin the new hash and grid with it in `tests/Game.Tests/WorldTests`: the test compares `(Version, hash, grid
   bounds)`, so a new world, or a new size, with the old version fails.
3. The game's description of its world (its world rules file, or `GAME.md`).

## A test that plays the real game

For a feature that needs the real world and systems, play the game without a window (`HeadlessMatch`; see
`tests/Game.Tests/ScenarioTests` and `.claude/rules/tests.md`):

```csharp
using var provider = GameComposition.Build(new Dictionary<string, string?> { ["Engine:Physics:Threads"] = "1" });
using var match = new HeadlessRun(provider).Play(seed: 1);
Assert.True(match.WaitUntilLoaded());
match.Input.InjectKey(Key.W, true);
match.Step(60);
// Read match.World (an Arch query), match.Match.Voxels, or match.Services.
```

## Then

`dotnet build` (no warnings), `dotnet test` (redirected to a file), `dotnet csharpier check .`; a game run for what the
tests cannot see (the `run-game` skill: `game.py shots` for pictures, a startup script for an unattended check). Then
the docs, as the `docs` skill's "After a change" says.
