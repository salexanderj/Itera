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
   compression), copied by the csproj's `Models/*/**` rule, and a row in `src/Game/Models/SOURCES.md` with its source,
   authors, licence, and changes. Its textures compressed for shipping: `dotnet run -c Release --project tools/Textures
   -- model src/Game/Models/NAME/NAME.gltf` writes `NAME.textures/` beside it (BC7 and BC5, a quarter of the decoded
   size); commit it, and run it again when an image changes (a test like `FoxModelTests`' compressed-textures test
   catches one forgotten).
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
   [SECONDS]]` puts one in front of the camera, the log's `Mesh assets:` lines have no Warning or note about it, a
   `ScenarioTests` row finds it in `MeshInstances`, and for a skinned model a test like `FoxModelTests` compares its
   clips with SharpGLTF's own world matrices.

## The game's look

1. The game's `IAtmosphereSource` (the starter's `GameAtmosphere`; `AtmosphereSystem` in the frame systems asks it at
   the camera each frame) gives the sky (`At`) and the look (`LookAt`): start from `RenderLook.Default` and change values
   with `with` (`RenderLook.Default with { Exposure = 0.9f, WorldSaturation = 1 }`). A place or a time of day may blend
   two looks (`RenderLook.Lerp`); it runs every frame, so it allocates nothing.
2. Try values live with the console's run-only `look NAME VALUE` (`look NAME R G B` for a colour; `look` lists them,
   `look reset` drops them), then copy the ones chosen into code.
3. A moving sun: the source's `At` gives the `Atmosphere` with its `SunDirection` (a day's course, `SunPath.AtHour` the
   test day's; under the horizon it is night, or a moon as the light with a dim, cool sun colour), and the sky's and the
   look's colours for the hour.
4. Check it: `game.py shots ... --compare OLD` against a set taken before, and a look at the PNGs (`time HH:MM` and
   `sun AZIMUTH ELEVATION` show other hours).

## A render pass of the game's own

GPU work the engine does not do (a highlight, an outline, a screen effect, a compute pass) is a render pass; the
mesh sample's `AimHighlightPass` (`samples/Itera.MeshSample/Rendering/`) is a short one. Load the `vulkan` and
`ecs-vulkan` skills first, and read `.claude/rules/rendering.md`, "Passes a game adds".

1. A class implementing `IRenderPass`: its `Name` and `Stage`. `Initialize(setup)`: a host-visible buffer with a region a
   frame slot (`setup.Allocator.CreateBuffer(..., MemoryUsage.Upload, ...)`, `RenderPassSetup.FramesInFlight`
   regions), a set of it (`setup.CreateBufferSet`), a layout with the view's push constants (`setup.CreateLayout`,
   `sizeof(ViewConstants)`), and a pipeline for the stage's targets (`setup.CreateTriangles`). `Write(slot, frame)`
   writes the slot's region, write only, from what a frame system left in a run-wide service; `Record(context)`
   flushes the slot's region, binds, pushes `context.View`, and draws; `OnTargetsChanged` makes the pipeline again when
   the colour format or samples changed; `CompileShaders` and `ReplacePipelines` for shader reloads; `Dispose` safe
   twice and when `Initialize` never ran (runs without graphics).
2. Register it with the run-wide service it reads: `services.AddSingleton<IRenderPass, MyPass>()`. The match's frame
   system that fills the service clears it in its `Dispose`, since the pass outlives the match.
3. Its shaders go in the game's `Shaders/` folder, copied to `Shaders/Game/` (the sample's csproj item), and include the
   engine's `../view.glsl` (`viewClip`, `view.slot`); the struct they read is `layout(scalar)`, with a `GpuLayout` test
   in the game's tests (the sample's `AimHighlightPassTests`).
4. Check it: the log's `Render pass of the game's: NAME at the STAGE stage`, `validate.sh` (with `--binary` for a game
   outside `src/Game`) windowed and `--offscreen`, a screenshot of it, and its time in `game.py gpu` (`Game NAME`) and
   the stats line's `game passes`.

## An image view (a minimap, a camera's picture)

A view of the world the engine draws into an image (`RenderViews`; `.claude/rules/rendering.md`, "Image views"); the
starter's minimap (`src/Game/Minimap.cs`, `MinimapLayer.cs`, `Systems/MinimapSystem.cs`) is the example.

1. Declare it in the game's `IRunStartup.Start`: `views.Declare(name, width, height, everyFrames)` (keep the index),
   and its key (`KeyBindings.Register`) and a saved on/off (`PlayerSettings.RegisterSwitch`) if the player turns it on
   and off.
2. A frame system (after `CameraExtractSystem`) asks for it each frame it should be drawn: `views.Draw(index, camera)`
   with a `CameraView` (an orthographic one: `CameraProjection.Orthographic`, a height in metres, NaN near and far for
   the view distance each way round its position). It goes in `SystemOrderTests` and `GAME.md`, with a behaviour test
   (`RenderViews.Asked` gives what it asked) and a zero-allocation test.
3. To show it on the HUD, an `IMatchUiLayer` calls `views.Show(index, x, y, width, height)` in its `Bounds` (asked
   every frame it shows, at the screen's size) and draws its frame and marks round the rectangle, leaving the inside
   clear; register it `services.AddScoped<IMatchUiLayer, MyLayer>()`. The engine's stats line sits in the top-right
   corner too: keep clear of it (the starter's `MinimapLayer` takes it).
4. To show it on a mesh material (a screen in the world), call `views.ShowOnMaterial(index, assetName, materialName)`
   in the same startup: the material shows the picture for its colour, and for its emission when it glows, through its
   colour's texture coordinates (0 to 1 across the picture, v 0 at its top); the mesh sample's `YardCamera` and the
   yard's `yard-screen`.
5. Check it: the log's `Image view NAME: ...` line, `validate.sh` windowed and `--offscreen`, a screenshot with the UI
   (`screenshot 1 PATH`, not `noui`), and its cost in `game.py gpu` (`ImageViews`, a time per update).

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
   `ILooseBodyRest` for what a rested ball becomes, an `IAtmosphereSource` for its sky and look.
5. Check it: tests that play it through `HeadlessMatch` (`tests/Itera.MeshSample.Tests`), and
   `validate.sh --binary PATH` (the vulkan skill) for its drawing.

## Then

`dotnet build` (no warnings), `dotnet test` (redirected to a file), `dotnet csharpier check .`; a game run for what the
tests cannot see (the `run-game` skill: `game.py shots` for pictures, a startup script for an unattended check).
