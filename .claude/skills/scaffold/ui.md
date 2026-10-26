# Adding to Itera: settings, screens, menus, and the hud

The `scaffold` skill's recipes for settings, screens, menus, and the hud. Each lists every file the addition touches, in order, and the test that fails when one is missed; finish with the skill's "Then".

## A game's own on/off setting or slider

A choice the player makes and the game keeps in `settings.json` (the starter's minimap shown or hidden; a slider for a
number, such as a density from 0 % to 100 %; `.claude/rules/ui.md`).

1. In the game's `IRunStartup.Start`: `settings.RegisterSwitch(name, defaultValue)` (keep the `GameSwitch`), and a key
   for it if the player toggles it (`KeyBindings.Register(id, label, hint, InputBinding.Of(key))`; the Keybindings tab
   lists it after the movement keys). The starter's `Minimap.Declare` does both.
2. A row on the Settings page, if the player sets it there: `SettingsRows.AddSwitch(which, label, hint, tab)` in the
   same `Start` (the engine's `SettingsRows.GameTab` or `GraphicsTab`, or a tab of the game's own). A key toggles it in a
   frame system (`settings.SetSwitch(which, !settings.IsOn(which))` when `settings.Keys.WasPressed(in input.Frame, key)`
   and the match's `PlayerInputBlock` is not `Blocked`), and the code it controls reads `IsOn` (both allocate nothing).
   The file is written when the player next leaves the Settings page or at the run's end.
3. Test it as the starter's `MinimapSystemTests` does (the key hides and shows it in the player's settings, and does
   nothing while the input is blocked); the engine's `PlayerSettingsTests` cover the file.

A slider takes the same steps with a number:

1. In `IRunStartup.Start`: `settings.RegisterValue(name, defaultValue, least, most)` (keep the `GameValue`). The name
   must differ from every switch's and value's.
2. A row: `SettingsRows.AddSlider(which, label, hint, tab, text)` in the same `Start`, where `text` turns the value into
   the text beside the slider (`v => $"{v:P0}"`). The slider's range is `least` to `most`.
3. The code the setting controls reads `settings.ValueOf(which)` (it allocates nothing), and runs again when
   `settings.Version` changes if it keeps a result. Test the row as `MenuLayerTests` does for a game's slider row.

## A text box, or a screen that Escape closes

A chat line, a save's name, or a sign's text the player types; an inventory or map that Escape shuts before the menu
opens (`.claude/rules/ui.md`).

1. A text box: keep a `TextEntry(maxLength, enter, escape)` (its `Line` holds the text and cursor) and give it the
   typing with `IFrameInput.SetTextFocus(entry)` when it opens (a key the game reads, a click on it). Every key but `
   goes to it until `ClearTextFocus(entry)`, which its `enter` and `escape` callbacks usually call; the player stops
   walking while it has the keys. Draw `entry.Line` in an `IMatchUiLayer` (redraw when `Line.Version` changes). A game
   with its own editing keys implements `ITextInput` instead.
2. A screen Escape closes: implement `IEscapeHandler` (`Escape()` returns true when it was open and closes, false to
   let Escape pass on), and `IFrameInput.AddEscapeHandler(screen)` once, at the match's start (a system's
   `Initialize`). The newest added is asked first. The engine drops both at the match's end.
3. Test them over the real `InputService` (`tests/Itera.Engine.Tests/Input/TextFocusTests` shows how) or a
   `HeadlessMatch` (`match.Input`).

## A HUD screen or panel on the UI toolkit

An inventory, a shop, a scoreboard, a dialogue box (`.claude/rules/ui.md`, the toolkit's paragraph):

1. The layer: a class deriving from `UiLayer` and `IMatchUiLayer` (or `IUiLayer` for a run-wide one), taking the run's
   `UiToolkit` and `IFrameInput` in its constructor (`: base(toolkit, input)`), with `Visible` while it shows and
   `Build(Ui ui)` declaring it every frame it shows:

   ```csharp
   protected override void Build(Ui ui)
   {
       using (ui.Column(Ui.Id("shop"), new UiBox { Panel = true, Padding = new UiEdges(32), Gap = 12,
                                                   Anchor = UiAnchor.Screen(UiPoint.Centre) }))
       {
           ui.Text("Shop", UiText.Heading);
           using (ui.Grid(Ui.Id("shop.items"), columns: 4, new UiBox { Gap = 8 }))
               for (var i = 0; i < _items.Count; i++)
                   if (ui.Area(Ui.Id(s_item, i), UiBox.Fixed(96, 96), version: _items[i].Version).Clicked)
                       Buy(i);
           if (ui.Button(Ui.Id("shop.close"), "Close").Clicked) Close();
       }
   }
   ```

   Sizes are units of a 1080-pixel-high screen. Ids are `Ui.Id("name")` (static readonly fields) or `Ui.Id(list, i)`.
   Strings declared each frame must be kept (a field, made again only when they change), or the layer allocates.
   `DrawArea(painter, element)` draws what an `Area` shows (an icon, a picture); give the area a `version` that changes
   when its picture does.
2. Its input: register it in the match's scope (`services.AddScoped<IMatchUiLayer, Shop>()`, after the engine's). A
   layer that takes clicks implements `IPanelInput` and sets itself as the panel while it shows
   (`input.SetPanel(this)`, `SetPanel(null)` as it closes): the pointer is freed and its buttons and the wheel reach it.
   A screen Escape closes is an `IEscapeHandler` (the section above); a text box in it gets the typing when clicked.
   Keyboard focus keys are the layer's own (`Keys`, a `UiKeys` from the game's bindings); the menu's reach only the menu.
3. Tests, as `tests/Itera.Engine.Tests/UI/Toolkit/UiLayerTests` does: the layer drawn on a `UISurface` with a stub
   `IFrameInput`, clicked by what its elements read (`layer.Ui.TryFind(label, out var element)`, `element.Rect`); its
   ink inside its bounds at a few screen sizes (`LayerInk`); an idle frame allocating nothing (`SystemAllocation`); its
   picture (`LayerPicture`, `ITERA_UI_PICTURES`). The console's `ui` prints it in a running game and `ui press LABEL`
   clicks it. Choose see-through colours from a shot of the running game: the UI pass blends in linear light.

## A page in the engine's menu, and Settings rows

1. A page: a class implementing `IMenuPage` (`Title`, `Build(Ui ui)` declaring its content inside the menu's panel,
   `Escape()` true when it closed something of its own), added in the game's `IRunStartup.Start` with
   `pages.Add(label, hint, page, MenuPlace.Main)` (or `MenuPlace.InGame` over a match, or `MenuPlace.Both`; `Main` by
   default; `MenuPages`, run-wide). Its button shows above
   Settings; the menu adds the title and Back.
2. Settings rows: `SettingsRows.AddSwitch` or `AddSlider` (the on/off setting or slider's section above).
3. Test it through the engine's menu as `MenuLayerTests.A_games_page_opens_from_its_button_and_its_switch_rows_show_on_their_tabs` does.

## A game's own menu

Copy `samples/Itera.MeshSample/SampleMenu.cs`; its README's "A game's own menu" lists what every menu must do
(`AboveMatch`, `Visible` through joins and starts, following `MatchControl`, typing through `TextFocus`, the Settings
page's key capture, the shade first in the flow).

1. Register it after `AddIteraEngine`: `services.AddSingleton<IMenu, MyMenu>()` (the last `IMenu` is the run's).
2. Settings: the engine's `SettingsPage` inside it (`Build`, then `Footer`), or the game's own rows over
   `PlayerSettings`. With Steam, list the friends hosting (`SteamFriendList`, `Watching`) and answer `JoinRequests`.
3. Tests, as `tests/Itera.MeshSample.Tests/SampleMenuTests` does: the menu built with a `MatchControl` the test moves
   (`MatchControlStates`: a join connecting, a match playing, its end), what it asked for read from
   `MatchControl.PendingStart` and `PendingLeave`, `PlayerSettings.WithoutFile()`; and one real match followed
   (`HeadlessRun`). Pictures of every page at 1280x720 and 2560x1440, and shots of the running game, for a look test.

## A UI style

A look of the game's own for the toolkit's elements: a class deriving from `UiStyle` (`samples/Itera.MeshSample/
SampleMenuStyle.cs`), its colours a `MenuStyle`, its body text in units (`base(bodyUnits, colours)`); override what
differs (`CornerUnits`, `OutlineUnits`, `TextUnits`, `Bold`, `DefaultBox`, or a kind's drawing, `DrawButton` and the
rest). Set it on one layer (`Ui.Style = new MyStyle()` in its constructor) or on every layer made after it
(`UiToolkit.Style`). A part made for another style (the engine's `SettingsPage`) sizes its controls itself, so it fits.
