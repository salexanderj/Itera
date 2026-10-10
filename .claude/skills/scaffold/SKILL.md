---
name: scaffold
description: "Steps, file lists, and templates for adding to Itera or a game on it: a system (engine or game), an option, a console command, a game message, a generator change, a model from glTF, and a test that plays the real game. Use before adding any of these, so every place they touch changes together; the checklist tests (ChecklistTests, ShippedSettingsTests, SystemOrderTests, WorldTests) fail when one is missed."
---

# Adding to Itera

Each section lists every file the addition touches, in order, and the test that fails when one is missed. Load the
area's skill first where the hook asks for it (`ecs` for systems and components, `vulkan` for rendering).

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
3. The order lists: `s_fixedOrder` or `s_frameOrder` in `tests/Game.Tests/SystemOrderTests.cs`, and `GAME.md`'s
   Systems list. `SystemOrderTests` fails when they differ from the registration.
4. Its tests, `NameSystemTests` in `tests/Itera.Engine.Tests/Systems/` (engine) or `tests/Game.Tests/` (game), with a
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
5. Docs: an engine system in `.claude/rules/systems.md` (or its area's rules file); a game's in `GAME.md`. Commands it
   registers go in the `run-game` skill's console list.

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
5. Docs: the `run-game` skill's Options list (and the area's rules file for what it changes).

## A console command

1. Register it where its services live: a system's `Initialize` (`commands?.Register(name, usage, help, scope,
   handler)`; it goes when the match ends), or a run-wide command class registered at `GameLoop.InitializeUi`. The
   scope: `Local` (this game only), `Match` (host or offline: it changes the shared world), `Offline` (it changes what
   every peer must agree on, or time).
2. Usage errors print `context.PrintUsage()`; failures `context.PrintError(...)` (a startup script with
   `ExitOnFailure` stops on them). A command that must wait holds its script with `DevConsole.Hold` (see `wait` and
   `screenshot` in `EngineCommands`).
3. Its name in backticks in the `run-game` skill's console list (host-only ones also in the line after it):
   `ChecklistTests.Every_console_command_is_in_the_run_game_skill` fails without it.
4. A test that runs it through `ConsoleCommands` and checks its answer and effect.

## A game message

1. A kind byte of 64 or more (`SessionProtocol`; below 64 is the engine's), and the way it may travel (host to
   clients, clients to host, or both).
2. A handler in the match's scope: `GameMessageHandler` for one kind, or an `ISessionGameHandler`
   (`.claude/rules/sessions.md`); sending through `SessionService.BeginMessage`.
3. Tests in `tests/Game.Tests` that send damaged bodies and hostile values in every field, each row under the
   `Watchdog` (`HostileValues`, `Watchdog` in `tests/Shared`).
4. A change to the engine's own messages raises `SessionProtocol.Version` and pins the new fingerprint in
   `WireFingerprintTests`.

## A change to what the generator makes

1. Raise the generator's `Version` (`GameGenerator.Version` for the starter).
2. Pin the new hash with it in `tests/Game.Tests/WorldTests`: the test compares `(Version, hash)`, so a new world with
   the old version fails.
3. `GAME.md`'s world description.

## A model from glTF

1. The files in `src/Game/Models/NAME/` (`.gltf` with its `.bin` and images, or one `.glb`; no Draco, meshopt, or Basis
   compression), copied by the csproj's `Models/*/*` rule, and a row in `src/Game/Models/SOURCES.md` with its source,
   authors, licence, and changes.
2. A name in `GameModels` and a `Declare` line there: the path from the executable's folder and the file's unit
   (`metresPerUnit`: 1 for metres, 0.01 for centimetres). `GameRunStartup` declares them at the run's start; a mod
   declares its own at its startup.
3. A system that places it: `World.Create(MeshPlacement.At(position, orientation, scale), DrawnMesh.Of(index))`, with
   the index from `MeshAssets.Find(name)` looked up once (`FoxSystem`); a fixed system that moves it copies the pose
   into the previous one first. `MeshDrawSystem` draws it. A glTF model's front is +z.
4. A model that things stand on or bump into gets a collider where it is placed: `MeshColliders.Add(asset, transform,
   tagsOf)` from a system's `Initialize` (or later), `tagsOf` giving each material's surface tags (game-defined bits;
   voxel materials' through an `IVoxelSurfaceTags`). Loose bodies land on it; `CollisionSources.Raycast` meets it.
5. A skinned model plays a clip with `MeshAnimator.Playing(rig.FindClip("Walk"))` added to it (the rig from
   `MeshAssets.TryGet`), and changes clip with `animator.FadeTo(clip, 0.3f)` (a fade) or a new `Playing`; a system that
   changes clips runs before the engine's `MeshAnimatorSystem`, which the game registers once, after every such
   system.
6. Check it: `meshes` lists it (triangles, materials, textures, size, joints, clips), `meshes place NAME [CLIP
   [SECONDS]]` puts one in front of the camera, the log's `Mesh assets:` lines have no Warning about it, a
   `ScenarioTests` row finds it in `MeshInstances`, and for a skinned model a test like `FoxModelTests` compares its
   clips with SharpGLTF's own world matrices.

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

## A game without voxel terrain

`samples/Itera.MeshSample` is one, to copy from. Its composition is `AddIteraEngine` and its own (`AddMeshSample`):

1. No `IChunkGenerator` and no `AddIteraVoxels()`: matches have no voxel world (`MatchServices.Terrain` null).
2. Its world as mesh assets: models from glTF (above) or built in code (`MeshAssets.Add` with a `MeshAsset`; textures
   by `MeshTextureData.FromRgba`), added at its `IRunStartup`; drawn by entities with `MeshPlacement` and `DrawnMesh`,
   and registered as colliders (`MeshColliders.Add(asset, transform, tagsOf)`) from a system's `Initialize`, each
   material's surface tags given by `tagsOf`.
3. The engine's systems that need no world, with `null` where a `VoxelWorld` is optional (`MapResetSystem`,
   `DebugBallSystem`, `PlayerCameraSystem`, `MeshDrawSystem`); no `StreamingCentreSystem` or benchmark. The character
   mover reads the colliders in cells of `Engine:Physics:CollisionCentimetres`; the play area is
   `PlayArea.WithoutTerrain` (±512 m) unless the game registers its own with its world's bounds
   (`services.Replace(...)`, as the sample does), whose edge is the mover's wall.
4. Optional: `ClimbingMode` as its `IMovementMode` with the tags it climbs (and a `Climber` on its players), an
   `ILooseBodyRest` for what a rested ball becomes, an `IAtmosphereSource` for its sky.
5. Check it: tests that play it through `HeadlessMatch` (`tests/Itera.MeshSample.Tests`), and
   `validate.sh --binary PATH` (the vulkan skill) for its drawing.

## Then

`dotnet build` (no warnings), `dotnet test` (redirected to a file), `dotnet csharpier check .`; a game run for what the
tests cannot see (the `run-game` skill: `game.py shots` for pictures, a startup script for an unattended check).
