---
paths:
  - "src/Itera.Engine/{UI,Input,Settings,Loading,Commands}/**"
  - "src/Itera.Engine/Systems/MovementKeys.cs"
  - "tests/Itera.Engine.Tests/{UI,Input,Settings,Loading,Commands}/**"
  - "tests/Shared/LayerInk.cs"
---

## UI

The UI (`UISurface`, `UiPass`): Skia draws its layers into a CPU surface the size of the frame, only the area of the
layers that changed, each layer clipped to its bounds (`IUiLayer.Bounds` must hold all it draws; `LayerInk` checks), and
the changed rows go to a storage buffer that a full-screen pass blends over the picture.

How the console works and the list of its commands are in the `run-game` skill; change that list when you add or remove a command.

The aim dot (`AimDotLayer`, the first of a match's layers, which the engine adds before the game's): a white square in a
black one at the screen's centre, 2 and 4 pixels across on an even side, 3 and 5 on an odd one, at the screen's own
pixels at any render scale; hidden while the loading screen is drawn. A game adds its own HUD layers
(`IMatchUiLayer`, scoped, registered after the engine's). `CentredMessageLayer` shows one of a fixed list of texts
(made into `TextBlobs` once) chosen each frame by a callback, with a shadow, allocating nothing; `Amounts` formats
counts as 500, 12.5K, 30M.

The stats line (F1, `stats`, `Engine:Ui:Stats`) shows frames per second, frame and GPU time, the render scale, fixed
steps per second, the session, and the living particles.

The loading screen (`LoadingScreen`, `Engine:Loading`) covers the picture from the main loop's first iteration until
every loading task (`ILoadingTask`, a DI service) is done: the engine's Sounds, World, Navigation, and Warm-up, and the
game's own after them (`ModelsLoadingTask` waits for a list of models to reach the GPU); at most
`Engine:Loading:MaxSeconds` (15), then a Warning. The loop runs as usual meanwhile, but the game's input is blocked
(Escape and the console still work). The render thread draws `loading.frag` (`LoadingPass`) instead of the world,
`LoadingLayer` lists the tasks, and it fades out over 0.3 s. The starter's takes about 5 s in Debug (the world; the
edit warm-up runs one round there, since Debug code is never optimised) and 2.5 s in Release. After `reset` it covers
the picture again
(`LoadingScreen.BeginAgain`, from the loop when `VoxelWorld.LastReset` moves) and frees the pointer.

The main menu (`UI/Menu/MenuLayer`, run-wide, drawn over the HUD and under the console): a run starts at it unless
`Engine:Session:Mode` names a match (Offline, Host, or Join; the default is `Menu`). The main page has Play Offline,
Host Game (a friends-only Steam lobby when Steam runs, else UDP on `Engine:Session:Port`), Join Game (the friends who
host, and an address box: host:port, lobby:ID, or steam:ID), Settings, and Quit, with why the last match ended. In a
match, Escape opens it at once over the shaded picture and frees the pointer (Resume, Settings, Leave Match, Quit);
the match goes on behind it. While a menu shows, the game gets no input and a click never captures the pointer
(`IMenuInput`, `InputService.MenuPointer`); the address box takes the keys (`TextLine`). Escape goes back a page,
resumes, or cancels a join, and does nothing on the main page. Its look is a game's `MenuStyle` (the engine's default
is `MenuStyle.Plain`). It clicks with `menu-hover` as the pointer moves onto a control and `menu-press` as a button acts.

The Settings page (`MenuLayer.Settings.cs`) has three tabs. Game: volume, mouse sensitivity (0.02 to 0.5 degrees a
pixel), field of view (60 to 120 degrees across 16:9), VSync, and full screen (`Engine:Window:Fullscreen`). Graphics:
dynamic render scaling, the dynamic scaling threshold, shadows, and anti-aliasing (a slider through Off, SMAA, MSAA 2x,
4x, and 8x; `AntiAliasMode`, kept as `AntiAliasing` and `MsaaSamples` in `settings.json`).
Keybindings (`KeyBindings`,
`KeyAction`, `KeyRow`, `InputBinding`; the engine's `MovementKeys` first, then a game's own, each registered once a run
by the game's `IRunStartup`): a button per action; a
click waits for the next key or mouse button. Escape and ` are never bound. One key an action: a key another action has
leaves that one None, unless the two never work at once (`KeyRow.Context`). Reset keys puts every binding back. Each
setting applies at once (`PlayerSettings`): the volume to `AudioService`; the render scaling, shadows
(`Engine:Renderer:Shadows`), and SMAA to `RenderView`; the world's samples by making the swapchain again on the render
thread, with `MeshPass`'s pipelines and images (`PlayerSettings.SamplesChanged`); the sensitivity,
field of view, and bindings where they are read (`PlayerInputSystem`, `PlayerCameraSystem`, `DebugFlyCameraSystem`,
`CameraExtractSystem`, `StatsLayer`); VSync by making the swapchain again on the render thread
(`VulkanSwapchain.SetVSync`); full screen through `WindowService.SetFullscreen`. Leaving the page, and the run's end,
write `settings.json` in the data folder through a file beside it (`"Keys"` holds only the changed bindings; keys this
build does not know are kept); the next start reads it (a step before the window) over the options, except keys the
command line gave (`CommandLineKeys`, prefixed or bare `key=value`), which win for that run. The console's `settings`,
`shadows`, and `sound volume` change the player's settings as the menu does, so a test run
that uses them writes its data folder's file (the player's, unless it runs with the agent data folder,
`<DataFolder>-agent`); give render options on the command line instead. Bindings read from the file or
registered later that clash with another action keep the file's (`KeyBindings` unbinds the default).

### Files in `src/Itera.Engine`

  - `UI/`: the surface and its layers (console, stats line, `LoadingLayer`), the font (`UiFonts`, from `UI/Fonts/`,
    copied to `Fonts/`), `TextBlobs`, `TextLine`, and the menu in `UI/Menu/` (`MenuLayer`, `MenuLayer.Settings.cs`,
    `MenuStyle`, `RadialMenu`, `RadialRing`, `ScrollBar`), `CentredMessageLayer`, `Amounts`.
  - `Input/`: snapshots per frame and per fixed step, `PointerCapture`, `IKeyboardFocus`, `IMenuInput` (`UiPointer`,
    `CapturingBinding`), `IFrameInput` and `InputService` (`SetDrawnPointer`, `SetPanel`), `IPanelInput`, `KeyBindings`,
    `InputBinding`.
  - `Commands/` (not `Console/`: that namespace would hide `System.Console`): `ConsoleCommands`, `CommandLine`,
    `EngineCommands`, `MatchCommands`, `SettingsCommands`, `ParticleCommands`, `SoundCommands`, `TimeCommands` (pause,
    step, timescale), `EcsCommands` (`ecs`, registered at a match's ECS step). The console's scripts:
    `UI/ConsoleScript`, run by `DevConsole` (`Hold`, `RunScript`). `Settings/`:
    `PlayerSettings`, `CommandLineKeys`.

### Package notes

- SkiaSharp 4.x: text APIs take `SKFont`; several `GRContext` members changed since 2.88. Linux
  needs `SkiaSharp.NativeAssets.Linux` explicitly.
