---
paths:
  - "src/Itera.Engine/UI/Menu/**"
  - "src/Itera.Engine/Settings/{PlayerSettings,CommandLineKeys,AntiAliasMode,GameSwitch}.cs"
  - "tests/Itera.Engine.Tests/UI/{MenuJoinPage,MenuLayer,MenuPictures,RadialMenu,RadialRing,ScrollBar}Tests.cs"
  - "tests/Itera.Engine.Tests/Settings/PlayerSettingsTests.cs"
---

## The menu and the settings

The toolkit is in `.claude/rules/ui.md`. Input, key bindings, and the console are in `.claude/rules/ui-input.md`.

### The menu

The menu is the run's `IMenu`: a UI layer drawn over the HUD and under the console. The input service sends a menu's input to the `IMenuInput` it implements.

The engine's `MenuLayer` is the menu unless a game registers its own `IMenu` singleton after `AddIteraEngine`. That one then takes its place among the layers and in the loop.

What a menu does goes through public services, so a game's own menu loses nothing:

- `MatchControl` and `MatchCommands`: start (with a save's name too), leave, the phase, and why the last match ended.
- `WindowService.RequestExit` and `PlayerSettings`.
- `SteamFriendList`: the loop updates it whatever menu shows. A menu sets `Watching` while it lists the friends hosting.
- `JoinRequests`: a friend's Steam "Join game". With no match it joins at once. During a match it waits as `Pending` for the menu to ask, `Accept` or `Decline`, and goes with that match.
- `SaveFiles`: the data folder's saves, newest first.

While a menu is open the game gets no input. With `Engine:Ui:MenuPausesOffline` an offline match's steps are held (`FixedStepAccumulator.Hold`, apart from the console's `pause`). A host's or a client's steps never wait.

A game that replaces the menu follows the mesh sample's README, which lists what a game's menu must do. The sample's menu is `SampleMenu`: a toolkit layer in a look of its own, with the engine's `SettingsPage` inside. The list covers `AboveMatch`, `Visible` through joins and starts, following `MatchControl`, the text box's typing through `TextFocus`, the Settings page's key capture, and the shade first in the flow.

A game's tests of its menu:

- move a `MatchControl` they made through the loop's states with `MatchControlStates`: a join connecting, a match starting and playing, its end;
- read what the menu asked for from `MatchControl.PendingStart` and `PendingLeave`;
- give it `PlayerSettings.WithoutFile()`.

### The engine's main menu

`MenuLayer` (`UI/Menu/`) is a toolkit layer, run-wide. Its look is `MenuLook`, and its style is `MenuStyle.Plain` unless a game gives its own `MenuStyle`.

A run starts at the menu unless `Engine:Session:Mode` names a match.

- The main page has Play Offline, Host Game, Join Game, Settings, and Quit. It shows why the last match ended.
- Host Game opens a friends-only Steam lobby when Steam runs, else UDP on `Engine:Session:Port`.
- Join Game lists the friends who host and has an address box that takes `host:port`, `lobby:ID`, or `steam:ID`. The box takes the keys through a `TextLine`.
- In a match, Escape opens the menu at once over the shaded picture and frees the pointer. It offers Resume, Settings, Leave Match, and Quit. The match goes on behind it.
- Escape goes back a page, resumes, or cancels a join, and does nothing on the main page.
- The menu clicks with `menu-hover` as the pointer moves onto a control and `menu-press` as a button acts.
- It plays the music track `Engine:Audio:MenuMusic` names (`MusicPlayer.MenuTrack`) while there is no match or one is being joined. It fades the track out as a match starts, unless the game has played another. A game's own menu plays its music itself (`.claude/rules/audio.md`).

A game adds to the engine's menu:

- Pages: `MenuPages.Add(label, hint, IMenuPage, MenuPlace)`. It makes a button on the main page or on the one over a match, before Settings. The button opens a panel with the page's title, its `Build`, and Back. The page's `Escape` takes Escape first.
- A corner: `MenuPages.AddCorner(IMenuCorner)`. It shows at the main page's bottom left a notice and its buttons, in a column over the band. An example is a line saying the game's data are not the shipped data, with a button to put them back.
- Settings rows: `SettingsRows`, below.

### The Settings page

`SettingsPage` is a toolkit part that a game's own menu shows inside its own. A menu calls `Build`, then `Footer` for Back, beside Reset keys on Keybindings. `CapturingBinding`, `BindingPressed`, and `Escape` carry its key capture.

It has four tabs, and a game adds to them (`SettingsRows`):

- Game: mouse sensitivity, field of view, UI scale, VSync, full screen (`Engine:Window:Fullscreen`), and Screen
  (`Engine:Window:Screen`). Screen is a slider through Main, each connected screen (`PlayerSettings.Screens`), and the
  setting's screen when it is not connected. It shows only when there are two steps or more.
- Sound: Master Volume, with Music Volume and Effects Volume under it. `settings.json` keeps them as `Volume`, `MusicVolume`, and `EffectsVolume`; `Engine:Audio:Volume`, `MusicVolume`, and `EffectsVolume` are their defaults.
- Graphics: dynamic render scaling, its threshold, shadows, and anti-aliasing. Anti-aliasing is a slider through Off, SMAA, and the MSAA sample counts (`AntiAliasMode`, kept as `AntiAliasing` and `MsaaSamples` in `settings.json`).
- Keybindings: a button per action (`KeyBindings` in `.claude/rules/ui-input.md`). A click waits for the next key or mouse button. Reset keys puts every binding back.

Each setting applies at once through `PlayerSettings`:

- The volumes go to `AudioService`.
- Render scaling, shadows (`Engine:Renderer:Shadows`), and SMAA go to `RenderView`.
- The world's samples remake the swapchain on the render thread, with `MeshPass`'s pipelines and images (`PlayerSettings.SamplesChanged`).
- Sensitivity, field of view, and bindings apply where they are read: `PlayerInputSystem`, `PlayerCameraSystem`, `DebugFlyCameraSystem`, `CameraExtractSystem`, `StatsLayer`.
- VSync remakes the swapchain on the render thread (`VulkanSwapchain.SetVSync`).
- Full screen goes through `WindowService.SetFullscreen`, and Screen through `WindowService.SetScreen`. Opening the page
  reads the connected screens again (`PlayerSettings.ReadScreens`).
- The console's `settings screen NAME|main` takes a connected screen's name in any case, and refuses any other.
- The UI scale (`PlayerSettings.UiScale`, `"UiScale"` in `settings.json`, `settings uiscale`) goes to `UiToolkit.Scale`.

### `settings.json`

- Leaving the page, and the run's end, write `settings.json` in the data folder through a file beside it.
- `"Keys"` holds only the changed bindings. Keys this build does not know are kept.
- The next start reads the file, a step before the window, over the options. Keys the command line gave (`CommandLineKeys`, prefixed or bare `key=value`) win for that run.
- The console's `settings`, `shadows`, and `sound volume` change the player's settings as the menu does. A test run that uses them writes its data folder's file, which is the player's unless the run uses the agent data folder (`<DataFolder>-agent`). Give render options on the command line instead.

A game's own on/off settings are `GameSwitch`es:

- Its `IRunStartup` registers each once a run with `PlayerSettings.RegisterSwitch(name, default)`.
- `IsOn` and `SetSwitch` read and set it, and allocate nothing.
- `settings.json` keeps them in a `"Game"` object, with any entry this run did not register kept as it was.
- `SetSwitch` marks the settings changed. The file is written when the player next leaves the Settings page, or at the run's end.
- A game shows a switch on the Settings page with `SettingsRows.AddSwitch(switch, label, hint, tab)`. The row goes after the engine's rows on Game or Graphics, or on a tab it names, before Keybindings.
- A game may instead set a switch itself: its own `KeyBindings` row, read with `PlayerSettings.Keys.WasPressed`.

A game's own number settings are `GameValue`s:

- Its `IRunStartup` registers each once a run with `PlayerSettings.RegisterValue(name, default, least, most)`. Switches and values share one set of names, and a repeat throws `ArgumentException`.
- The bounds must be finite, with `least` not above `most`. The default is kept within them.
- The file's value is used when the `"Game"` object holds a number of that name, kept within the bounds.
- `ValueOf` and `SetValue` read and set it, and allocate nothing. `SetValue` keeps the value within the bounds and ignores one that is not finite or does not change.
- `LeastOf`, `MostOf`, and `ValueName` give the bounds and the name kept in the file.
- `settings.json` writes the values after the switches, rounded to four places, then the entries this run did not register.
- A game shows a value on the Settings page with `SettingsRows.AddSlider(value, label, hint, tab, text)`. `text` gives the value's text, such as `v => $"{v:P0}"`. The slider takes its bounds from `PlayerSettings`.
- `SettingsPage` calls `text` only when the value changes, and a move calls `SetValue` at once.
- A tab shows the game's switch and slider rows in the order they were added, after the engine's rows. `SettingsRows.Switches` and `Sliders` list each kind.

### Tests

`MenuLayerTests` and `MenuJoinPageTests` work the menu by what its elements read. `MenuPicturesTests` draws every menu page. Other classes: `RadialMenuTests`, `RadialRingTests`, `ScrollBarTests`, `PlayerSettingsTests`.

### Files in `src/Itera.Engine`

- `UI/Menu/`: `IMenu`, `MenuLayer`, `MenuPages` with `IMenuPage` and `IMenuCorner`, `SettingsPage`, `SettingsRows`, `MenuStyle`, `RadialMenu`, `RadialRing`, `ScrollBar`.
- `Settings/`: `PlayerSettings`, `CommandLineKeys`, `AntiAliasMode`, `GameSwitch`, `GameValue`.
