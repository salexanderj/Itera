---
paths:
  - "src/Itera.Engine/UI/*.cs"
  - "src/Itera.Engine/UI/{Toolkit,Fonts}/**"
  - "src/Itera.Engine/Loading/**"
  - "tests/Itera.Engine.Tests/UI/*.cs"
  - "tests/Itera.Engine.Tests/UI/Toolkit/**"
  - "tests/Itera.Engine.Tests/Loading/**"
  - "tests/Shared/LayerInk.cs"
  - "src/Game/MinimapLayer.cs"
---

## UI toolkit and layers

The menu and the settings are in `.claude/rules/ui-menu.md`. Input, text entry, the console, and tunables are in `.claude/rules/ui-input.md`.

### The surface and the layers

Skia draws the UI's layers (`UISurface`, `UiPass`) into a CPU surface the size of the frame. Only the areas of the layers that changed are drawn again, and each layer is clipped to its bounds. Changed areas within 16 pixels of each other merge, and there are at most four; a layer that meets two areas is drawn into each. `IUiLayer.Bounds` must hold all a layer draws; `LayerInk` checks that. Each area's rows form a band (`UiBands`, merged where they overlap or touch). Each band is copied to a storage buffer, and a full-screen pass blends the pixels over the picture.

Under those pixels the pass draws the image views a HUD layer shows (`.claude/rules/rendering-views.md`). The layer draws its frame and marks over them and leaves the inside clear, as the starter's `MinimapLayer` does.

The pass blends in linear light, because its target is sRGB. Skia and `LayerPicture` compose in sRGB values. A see-through colour therefore shows more of the world in the game than in a picture. Choose see-through colours from a shot of the running game.

Layers the engine provides:

- `AimDotLayer`: the first of a match's layers, added before the game's. It draws at the screen's own pixels at any render scale and hides while the loading screen is drawn.
- `CentredMessageLayer`: shows one of a fixed list of texts, made into `TextBlobs` once, chosen each frame by a callback. It allocates nothing.
- `HostSilenceLayer`: a match's layer after the aim dot. On a client it shows "No data from the host for N s" while `ReplicationService.SecondsWithoutSnapshots` is at least `ReplicationService.SilenceSeconds`. It hides while the loading screen is drawn.
- `Amounts`: formats counts for the HUD (500, 12.5K, 30M).
- `StatsLayer`: the stats line (F1, the `stats` command, `Engine:Ui:Stats`). It shows frames per second, frame and GPU time, the render scale, fixed steps per second, the session, and the living particles.
- `LoadingLayer`: lists the loading tasks.

A game adds its own HUD layers as `IMatchUiLayer`s: scoped, registered after the engine's. A HUD layer gets the pointer's button and the wheel only while a panel is set (`SetPanel`), as the menu and panels do. It gets no pointer while the game's input is blocked (the menu is open).

### The toolkit (`UI/Toolkit/`)

A layer derives from `UiLayer` and describes its UI in code in `Build(Ui ui)`, each frame it is shown.

- Containers close with `using`: `Row`, `Column`, `Stack`, `Grid`, `Scroll`, and `Pressable` (a row clicked as a whole, with the scope's state).
- Elements: `Text`, `Button`, `Switch`, `Slider`, `TextBox` over a `TextEntry`, `Area`, and `Space`. The game draws an `Area` in `DrawArea`, and it redraws when its `version` changes.
- Each element has an id from the code: `Ui.Id("name")` or `Ui.Id(list, index)`; 0 for none.
- `UiMarks` set an element disabled, selected, or passive (it takes no pointer).
- `UiBox` sets sizes (`UiSize`: fit the content, fixed, grow by weight, or a share), padding, gaps, alignment, an anchor over the rest, a panel, and `Blocks`, which stops the pointer reaching what lies under it.
- `UiText` sets a text's role (the style sizes it), its tone (the style colours it), wrapping, and the most lines.

The layout runs as flexbox and Clay do. Text wraps or is cut with an ellipsis.

Units are pixels of a screen 1080 pixels high. They scale with the screen's height and the UI scale (`Ui.UnitPixelsFor`). The screen's height is the framebuffer's.

The layers that draw their own text use the same rule: the console, the stats line, the loading list, and `CentredMessageLayer`. `UiFonts.Pixels(height)` gives `Engine:Ui:FontPixels` units of the screen. `LayerFont` keeps a layer's font at that size and resizes it when the screen or the scale changes. Margins are whole pixels (`Whole`).

Input and state:

- Each element returns its `UiState`, from the input matched against the last layout: hover, a click when released on the element pressed, a slider dragged also off it, the wheel to the innermost scroll area, and keyboard focus.
- Tab and the arrows move the focus, and Enter or Space presses.
- A text box takes typing through `SetTextFocus`. `Ui.StartTyping(id)` gives a box the typing from the layout that declares it, this one or the next.
- The right button reaches menus and panels (`UiPointer.RightPressed`, `UiState.RightClicked`).
- Navigation keys reach a layer through its `Keys`. The run's menu (an `IMenu` toolkit layer) gets `IFrameInput.MenuKeys`, collected while it is open. Its text box keeps all but Tab.
- `Ui.Tween` eases values kept by id.
- `Invalidate()` redraws a layer whatever its UI says; use it when what an area shows came from elsewhere.

Drawing:

- `UiStyle` draws each kind of element, and every part of it is virtual. The engine's plain look uses `MenuStyle`'s colours.
- `UiPainter` draws fills, outlines, and text from blobs kept while drawn. It uses the UI font, and system fonts for characters it lacks (`UiTypefaces`).
- `UiToolkit` (run-wide) holds the measurer, painter, style, scale, and the clicks `menu-hover` and `menu-press`.
- A layer is drawn again only when a hash of its result changes. An unchanged page allocates nothing.
- A declaration that throws is logged once at Error and shows nothing until one does not throw.

The UI scale is the player's (`PlayerSettings.UiScale`; `Engine:Ui:Scale` is its default). `UiToolkit.Scale` holds it and keeps it in `UiFonts.UiScale` for every layer.

Reading a UI:

- `Ui.Dump()` prints the layout one element a line (kind, id, text, rectangle, state), for tests and agents.
- The console's `ui` prints every toolkit layer shown, an element a line. `ui press LABEL` clicks the element reading LABEL (`UiCommands`). A tool or an agent works a UI by what it says.

### The loading screen

`LoadingScreen` (`Engine:Loading`) covers the picture from the main loop's first iteration until every loading task is done. A loading task is an `ILoadingTask`, a DI service.

- The engine's tasks are `SoundsLoadingTask`, `PhysicsWarmUpLoadingTask`, `WorldLoadingTask`, `ObjectsLoadingTask`, `NavigationLoadingTask`, and `EditWarmUpLoadingTask` (in `Loading/EngineLoadingTasks.cs`, the objects' in `Objects/VoxelObjects.cs`). The game's own come after them. `ModelsLoadingTask` waits for a list of models to reach the GPU.
- `Engine:Loading:MaxSeconds` limits the wait; past it the game logs a Warning and plays.
- As loading ends, done or at its limit, and before the fade, `LoadingScreen` runs one full compacting collection
  (`LoadingScreen.Collections`): what loading made and dropped sits in the oldest generation, which only a full
  collection frees, and in play that collection is one long step.
- The loop runs as usual meanwhile, but the game's input is blocked. Escape and the console still work.
- The render thread draws `loading.frag` (`LoadingPass`) instead of the world. `LoadingLayer` lists the tasks.
- After `reset` the screen covers the picture again (`LoadingScreen.BeginAgain`, called from the loop when `VoxelWorld.LastReset` moves) and frees the pointer.
- `LoadingDraw` carries the loading screen's opacity and seconds in a frame; while it covers the picture, the world's
  passes are not recorded. `WindowService.Wake` ends the main loop's wait when a frame is asked for or a control
  request arrives.

### Tests

`UiLayoutTests`, `UiInputTests`, `UiLayerTests`, `MenuPicturesTests`, `EngineLayerBoundsTests`, `UISurfaceTests`,
`AimDotLayerTests`, `CentredMessageLayerTests`, `HostSilenceLayerTests`, `AmountsTests`, `TextBlobsTests`,
`UiFontsTests`, `WheelStepsTests`, `LoadingScreenTests`, `ModelsLoadingTaskTests`. `ITERA_UI_PICTURES` names a folder
for the pictures `UiLayerTests` and `MenuPicturesTests` keep.

### Files in `src/Itera.Engine`

- `UI/`: the surface and its layers (`UISurface`, `IUiLayer`, `IMatchUiLayer`, `AimDotLayer`, `CentredMessageLayer`, `HostSilenceLayer`, `StatsLayer`, `LoadingLayer`, `Amounts`), the font and the sizing rule (`UiFonts`, from `UI/Fonts/`, copied to `Fonts/`; `LayerFont`), `TextBlobs`, and `WheelSteps`.
- `UI/Toolkit/`: `Ui` with its layout, input, and dump, `UiBox`, `UiSize`, `UiText`, `UiState`, `UiElement`, `UiInput`, `UiStyle`, `UiPainter`, `UiTypefaces`, `UiToolkit`, `UiLayer`.
- `Loading/`: `LoadingScreen`, `ILoadingTask`, the engine's tasks (`EngineLoadingTasks.cs`), `ModelsLoadingTask`.

### Package notes

- SkiaSharp 4.x: text APIs take `SKFont`. Linux needs `SkiaSharp.NativeAssets.Linux` explicitly.
