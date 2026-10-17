---
paths:
  - "src/Itera.Engine/{UI,Input,Settings,Loading,Commands}/**"
  - "src/Itera.Engine/Systems/MovementKeys.cs"
  - "tests/Itera.Engine.Tests/{UI,Input,Settings,Loading,Commands}/**"
  - "tests/Shared/LayerInk.cs"
  - "src/Game/MinimapLayer.cs"
---

## UI

The UI (`UISurface`, `UiPass`): Skia draws its layers into a CPU surface the size of the frame, only the area of the
layers that changed, each layer clipped to its bounds (`IUiLayer.Bounds` must hold all it draws; `LayerInk` checks), and
the changed rows go to a storage buffer that a full-screen pass blends over the picture. Under those pixels the pass
draws the image views a HUD layer shows this frame (`RenderViews.Show`, a rectangle of screen pixels each, from the
layer's `Bounds`: `.claude/rules/rendering.md`, "Image views"); the layer draws its frame and marks over them and leaves
the inside clear (the starter's `MinimapLayer`, which keeps below the stats line while that shows).

How the console works and the list of its commands are in the `run-game` skill; change that list when you add or remove a
command. The run-only `look`, `sun`, `time`, and `lens` (`EngineCommands`) set values over the frame's view
(`RenderView`'s look tuning, `SunOverride`, and `LensOverride`): `.claude/rules/rendering.md`.

The aim dot (`AimDotLayer`, the first of a match's layers, which the engine adds before the game's): a white square in a
black one at the screen's centre, 2 and 4 pixels across on an even side, 3 and 5 on an odd one, at the screen's own
pixels at any render scale; hidden while the loading screen is drawn. A game adds its own HUD layers
(`IMatchUiLayer`, scoped, registered after the engine's). `CentredMessageLayer` shows one of a fixed list of texts
(made into `TextBlobs` once) chosen each frame by a callback, with a shadow, allocating nothing; `Amounts` formats
counts as 500, 12.5K, 30M.

A game's own text box (a chat line, a save's name) takes the typing through `IFrameInput.SetTextFocus` (`ITextInput`;
`TextEntry` is a ready-made one over a `TextLine`): while it is set, and the console and the menu are closed and the
game's input is not blocked, every key goes to it (Escape and Enter too, so the box ends the typing itself, usually
with `ClearTextFocus`; ` too while the console is off), with the characters typed and pasted, and no key reaches the
game; the keys held as it is set are let go. The mouse, the wheel, and the pointer stay the game's, and ` still opens
the console. Silk.NET passes on no key repeats, so `InputService` repeats a key a text box says repeats (Backspace,
Delete, Left, Right; `ITextInput.KeyDown` returns true) after 0.4 s, 30 times a second, for the menu's box too; the
console repeats its own. The box's callbacks and `IEscapeHandler.Escape` run inside the window's key event: short, no
ECS changes, and better not throwing: what a handler throws inside a window callback is kept (an exception through
GLFW's native frames ends the process unsaved) and thrown again on the loop's thread after the window's events are
pumped (`InputService.ThrowIfCallbackFailed`, also first in `Update`), where it ends the match as any fault in its work
does. Characters
arrive whole (`Rune`): `WindowService.SetCharacterCallback` takes GLFW's code points, since Silk.NET's `KeyChar` cuts
one past U+FFFF to 16 bits, and `TextLine` keeps a UTF-16 pair whole; a glyph the UI font lacks draws as its box.
GLFW 3.4 has no IME composition (preedit) support. Escape goes, in order, to the console when open, the menu when open,
the game's text box, the game's screens (`IEscapeHandler`, `AddEscapeHandler`; the newest first, each taking it or
passing it on, with the pointer and keys left as they are), the game's panel (`IPanelInput`), and otherwise opens the
menu. A match's end drops its text box, screens, panel, and drawn pointer (`InputService.EndMatch`).

The UI toolkit (`UI/Toolkit/`): a layer derives from `UiLayer` and describes its UI in code in `Build(Ui ui)`, each
frame it is shown: containers closed with `using` (`Row`, `Column`, `Stack`, `Grid`, `Scroll`, and `Pressable`, a row
clicked as a whole, its state the scope's) and elements (`Text`, `Button`, `Switch`, `Slider`, `TextBox` over a
`TextEntry`, `Area` the game draws in `DrawArea`, redrawn when its `version` changes, `Space`), each with an id of the
code's (`Ui.Id("name")`, `Ui.Id(list, index)`; 0 for none), `UiMarks` (disabled, selected, passive: takes no pointer),
and a `UiBox` (sizes that fit, are fixed, grow from their content by weight, or take a share; padding, gaps, alignment,
an anchor over the rest, a panel, `Blocks` to stop the pointer reaching what lies under it), texts set
by `UiText` (a role the style sizes, a tone it colours, wrapping, the most lines). The layout runs as flexbox and Clay
do; text wraps or is cut with an ellipsis; units are pixels of a 1080-pixel-high screen, scaled with the screen's height
and the UI scale (`Ui.UnitPixelsFor`), text never under 10 px. The same rule sizes the layers that draw their own text
(the console, the stats line, the loading list, `CentredMessageLayer`): `UiFonts.Pixels(height)` is
`Engine:Ui:FontPixels` units (18) of it, so 12 px at 720 and 24 px at 1440, and `LayerFont` keeps a layer's font at that
size, resized when the screen or the scale changes, with margins in whole pixels (`Whole`). The screen's height is the
framebuffer's, so a Retina Mac's doubled pixels count. Each element returns its `UiState` from the input
matched against the last layout (hover, a click when released on the element pressed, a slider dragged also off it,
the wheel to the innermost scroll area, keyboard focus by Tab and the arrows with Enter or Space to press, a text box's
typing given through `SetTextFocus`); `Ui.Tween` eases values kept by id. Each kind is drawn by `UiStyle`, every part of
it virtual (the engine's plain look in `MenuStyle`'s colours); `UiPainter` draws fills, outlines, and text from blobs
kept while drawn, in the UI font with system fonts for characters it lacks (`UiTypefaces`). `UiToolkit` (run-wide)
holds the measurer, painter, style, scale, and the clicks (`menu-hover`, `menu-press`). A layer is drawn again only
when a hash of its result changes, allocating nothing for an unchanged page; a declaration that throws is logged once
at Error and shows nothing until one does not. `Ui.Dump()` prints the layout one element a line (kind, id, text,
rectangle, state), for tests and agents; tests pin rectangles with `FixedTextMeasurer` and draw a layer alone with
`LayerPicture` (`ITERA_UI_PICTURES` names a folder `UiLayerTests` keeps its page's picture in, and `MenuPicturesTests` every menu
page's at two sizes). The UI pass blends the UI over the picture in linear light (its target is sRGB), while Skia and
`LayerPicture` compose in sRGB values, so a see-through colour shows more of the world in the game than in a picture (a
dark panel at 88 % alpha lets about half through): choose see-through colours from a shot of the running game. A HUD layer gets the
pointer's button and the wheel only while a panel is set (`SetPanel`), as the menu and panels do, and no pointer while
the game's input is blocked (the menu open); navigation keys reach a layer through its `Keys`: the run's menu (an
`IMenu` toolkit layer) gets `IFrameInput.MenuKeys`, Tab, Shift+Tab, the arrows, Enter, and Space collected while it is
open (its text box keeps all but Tab). The right button reaches menus and panels (`UiPointer.RightPressed`,
`UiState.RightClicked`). `Invalidate()` redraws a layer whatever its UI says (what an area shows came from elsewhere).
The UI scale is the player's (`PlayerSettings.UiScale`, 0.75 to 1.5, `"UiScale"` in `settings.json`, `settings uiscale`;
`Engine:Ui:Scale` its default), handed to `UiToolkit.Scale`, which keeps it in `UiFonts.UiScale` for every layer.
`Ui.StartTyping(id)` gives a text box the typing from the layout that declares it, this one or the next (a page
that opens with its box typing). The console's `ui` prints every toolkit layer shown, an
element a line, and `ui press LABEL` clicks the element reading LABEL (`UiCommands`), so a tool or agent works a UI by
what it says.

Tunables (`Tunables`, run-wide): the numbers a game tunes while it plays (movement, recoil, sway, part limits, an
enemy's), each registered once at the run's start (`Register(name, value, least, most, help, scope)`: a name of 1 to 64
letters, digits, '.', '-', or '_', a default within its range; sealed by the first match) and read as
`Tunable.Value`, which allocates nothing. The console's `tune` lists them, reads one, sets one within its range, or puts
them back (`TuningCommands`). A `Match` tunable (what a client predicts must move by the host's numbers) is set only
offline or on a host, which sends its value by name to its clients after the step it changed, and every one as each
client joins, at its default too (the session's `Tune` message, `.claude/rules/sessions.md`); a client's go back to
their defaults when its session ends. A `Local` one is each game's own.

The stats line (F1, `stats`, `Engine:Ui:Stats`) shows frames per second, frame and GPU time, the render scale, fixed
steps per second, the session, and the living particles.

The loading screen (`LoadingScreen`, `Engine:Loading`) covers the picture from the main loop's first iteration until
every loading task (`ILoadingTask`, a DI service) is done: the engine's Sounds, Physics, World, Navigation, and Warm-up, and the
game's own after them (`ModelsLoadingTask` waits for a list of models to reach the GPU); at most
`Engine:Loading:MaxSeconds` (15), then a Warning. The loop runs as usual meanwhile, but the game's input is blocked
(Escape and the console still work). The render thread draws `loading.frag` (`LoadingPass`) instead of the world,
`LoadingLayer` lists the tasks, and it fades out over 0.3 s. After `reset` it covers the picture again
(`LoadingScreen.BeginAgain`, from the loop when `VoxelWorld.LastReset` moves) and frees the pointer.

The menu is the run's `IMenu` (a UI layer drawn over the HUD and under the console, and the `IMenuInput` the input
service sends a menu's input to): the engine's `MenuLayer` unless a game registers its own `IMenu` singleton after
`AddIteraEngine`, which then takes its place among the layers and in the loop. What a menu does goes through public
services, so a game's own menu loses nothing: `MatchControl` and `MatchCommands` (starts, with a save's name too;
leave; the phase and why the last match ended), `WindowService.RequestExit`, `PlayerSettings`, `SteamFriendList` (the
loop updates it whatever menu shows; a menu sets `Watching` while it lists the friends hosting), `JoinRequests` (a
friend's Steam "Join game": with no match it joins at once, during one it waits as `Pending` for the menu to ask,
`Accept` or `Decline`, and goes with that match), and `SaveFiles` (the data folder's saves, newest first). While a menu
is open the game gets no input, and with `Engine:Ui:MenuPausesOffline` (false by default) an offline match's steps are
held (`FixedStepAccumulator.Hold`, apart from the console's `pause`; a host's or a client's never wait). The mesh sample
swaps it for its own (`SampleMenu`, a toolkit layer in a look of its own, with the engine's `SettingsPage` inside); its
README lists what a game's menu must do (`AboveMatch`, `Visible` through joins and starts, following `MatchControl`,
its text box's typing through `TextFocus`, the Settings page's key capture, the shade first in the flow). A game's tests
of its menu move a `MatchControl` they made through the loop's states with `MatchControlStates` (a join connecting, a
match starting and playing, its end), read what the menu asked for from `MatchControl.PendingStart` and `PendingLeave`,
and give it `PlayerSettings.WithoutFile()`.

The engine's main menu (`UI/Menu/MenuLayer`, a toolkit layer, run-wide, drawn over the HUD and under the console; its
look `MenuLook`: the plain style at 32.4-unit text, with 3.24-unit outlines and 12.96-unit corners): a run starts at it unless
`Engine:Session:Mode` names a match (Offline, Host, or Join; the default is `Menu`). The main page has Play Offline,
Host Game (a friends-only Steam lobby when Steam runs, else UDP on `Engine:Session:Port`), Join Game (the friends who
host, and an address box: host:port, lobby:ID, or steam:ID), Settings, and Quit, with why the last match ended. In a
match, Escape opens it at once over the shaded picture and frees the pointer (Resume, Settings, Leave Match, Quit);
the match goes on behind it. While a menu shows, the game gets no input and a click never captures the pointer
(`IMenuInput`, `InputService.MenuPointer`); the address box takes the keys (`TextLine`). Escape goes back a page,
resumes, or cancels a join, and does nothing on the main page. A game adds pages to it (`MenuPages.Add(label, hint,
IMenuPage, MenuPlace)`: a button on the main page or the one over a match, before Settings, opening a panel with the
page's title, its `Build`, and Back; the page's `Escape` takes Escape first) and Settings rows (`SettingsRows`, below). Its look is a game's `MenuStyle` (the engine's default
is `MenuStyle.Plain`). It clicks with `menu-hover` as the pointer moves onto a control and `menu-press` as a button acts.
It plays the music track `Engine:Audio:MenuMusic` names (`MusicPlayer.MenuTrack`) while there is no match or one is being
joined, fading in over 2 s, and fades it out as a match starts unless the game has played another; a game's own menu
plays its music itself (`.claude/rules/audio.md`).

The Settings page (`SettingsPage`, a toolkit part a game's own menu shows inside its own: `Build`, then `Footer` for
Back, beside Reset keys on Keybindings; `CapturingBinding`, `BindingPressed`, and `Escape` for its key capture) has
four tabs, and a game's own (`SettingsRows`). Game: mouse sensitivity (0.02 to 0.5 degrees a pixel), field of view
(60 to 120 degrees across 16:9), UI scale (75 to 150 %, in steps of 5), VSync, and full screen
(`Engine:Window:Fullscreen`). Sound: Master Volume, and under it Music Volume and Effects Volume (`Volume`,
`MusicVolume`, and `EffectsVolume` in `settings.json`; `Engine:Audio:Volume`, `MusicVolume`, and `EffectsVolume` their
defaults). Graphics:
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
`<DataFolder>-agent`); give render options on the command line instead. A game's own on/off settings are
`GameSwitch`es its `IRunStartup` registers once a run (`PlayerSettings.RegisterSwitch(name, default)`, then `IsOn` and
`SetSwitch`, which allocate nothing); `settings.json` keeps them in a `"Game"` object, with any entry this run did not
register kept as it was. A game shows one on the Settings page with
`SettingsRows.AddSwitch(switch, label, hint, tab)` (after the engine's rows on Game or Graphics, or on a tab it names,
before Keybindings), or sets it itself, as the starter's M key does
(`MinimapSystem`: its own `KeyBindings` row, read with `PlayerSettings.Keys.WasPressed` while the match's
`PlayerInputBlock` lets input through). `SetSwitch` marks the settings changed; the file is written when the player next
leaves the Settings page, or at the run's end. Bindings read from the file or
registered later that clash with another action keep the file's (`KeyBindings` unbinds the default).

### Files in `src/Itera.Engine`

  - `UI/`: the surface and its layers (console, stats line, `LoadingLayer`), the font and the sizing rule (`UiFonts`, from
    `UI/Fonts/`, copied to `Fonts/`; `LayerFont`), `TextBlobs`, `TextLine`, `TextEntry`, the toolkit in `UI/Toolkit/` (`Ui` with its layout,
    input, and dump, `UiBox`, `UiSize`, `UiText`, `UiState`, `UiElement`, `UiInput`, `UiStyle`, `UiPainter`,
    `UiTypefaces`, `UiToolkit`, `UiLayer`), and the menu in `UI/Menu/` (`IMenu`, `MenuLayer`, `MenuPages` with
    `IMenuPage`, `SettingsPage`, `SettingsRows`, `MenuStyle`, `RadialMenu`, `RadialRing`, `ScrollBar`), `CentredMessageLayer`, `Amounts`.
  - `Input/`: snapshots per frame and per fixed step (`InputSnapshot`: `InputService.Frame` and `InputService.Tick`), `PointerCapture`, `IKeyboardFocus`, `IMenuInput` (`UiPointer`,
    `CapturingBinding`), `IFrameInput` and `InputService` (`SetDrawnPointer`, `SetPanel`, `SetTextFocus`,
    `AddEscapeHandler`), `IPanelInput`, `ITextInput`, `IEscapeHandler`, `KeyBindings`, `InputBinding`.
  - `Commands/` (not `Console/`: that namespace would hide `System.Console`): `ConsoleCommands`, `CommandLine`,
    `EngineCommands`, `MatchCommands`, `SettingsCommands`, `TuningCommands`, `ParticleCommands`, `SoundCommands`, `TimeCommands` (pause,
    step, timescale), `EcsCommands` (`ecs`, registered at a match's ECS step). The console's scripts:
    `UI/ConsoleScript`, run by `DevConsole` (`Hold`, `RunScript`). `Settings/`:
    `PlayerSettings`, `CommandLineKeys`, `AntiAliasMode`, `GameSwitch`, `Tunables` (with `Tunable` and `TuningScope`).

### Package notes

- SkiaSharp 4.x: text APIs take `SKFont`; several `GRContext` members changed since 2.88. Linux
  needs `SkiaSharp.NativeAssets.Linux` explicitly.
