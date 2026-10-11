# Typed text, a menu a game replaces, and the UI toolkit: decisions and their reasons

`docs/framework-limits.md` listed "a game gets no typed text; the menu is sealed and Escape always opens it" in limit 6,
and order of work item 4. On 2026-10-11 the engine gained, in seven phases: typed text and Escape for a game's screens
(4532d32), the menu's public services (aaad2f3), a UI toolkit (91c5a16, 2ff0229), the menu's keys, the right button, and
a player's UI scale (2fd28a2), the engine's menu rebuilt on the toolkit under one sizing rule (42d96af), the mesh
sample's own menu (e716bed), and hooks for a game's tests of its menu with these docs. How each part works is in
`.claude/rules/ui.md` and the `scaffold` skill; this file records what was chosen, what else was offered, and why.

## The round's scope

Item 4 named gamepads, typed text, sound files and music, and a menu a game can replace. Lucy chose typed text and the
menu for this round, sound files and music as the next item (WAV with a reader of the engine's own, and Ogg Vorbis with
NVorbis 0.10.5), and gamepads later ("not a high priority"). Halfway through planning she asked whether this was the
time for a UI toolkit; it was taken in, after the typed text and the menu's services, with a design round of its own.
Her requirements for it: as flexible, broad, simple, and elegant as possible; it speeds up iteration on the parts of a
UI that are almost always done one way; it imposes no design and steps back where a game's own choices belong; and it
will mostly be changed by Claude sessions and agents, so details that help them are welcome where they keep to the rest.

## Typed text and Escape

- A game's text box takes the typing through `IFrameInput.SetTextFocus`: every key but ` goes to it, and none to the
  game, so a player typing "w" does not walk; the keys held as it opens are let go. ` still opens the console over it
  (Lucy's choice), so the console is never out of reach.
- Characters arrive whole. Silk.NET's `KeyChar` cuts a code point past U+FFFF to 16 bits (an emoji arrived as another
  character); the engine sets GLFW's character callback itself, and `TextLine` keeps a UTF-16 pair whole.
- Silk.NET 2.23 passes on no key repeats, so `InputService` repeats the keys a text box asks for (0.4 s, then 30 a
  second). GLFW 3.4 has no IME composition, so Chinese and Japanese input methods do not work yet.
- Escape goes to the console, the menu, the game's text box, the game's screens (`IEscapeHandler`, newest first), the
  game's panel, and only then opens the menu: an inventory closes before the menu opens.

## A menu a game replaces

- Swap it or add to it. Offered: swap the whole menu only (a game draws every page itself, settings included); add to
  the engine's menu only; both. Chosen: both. A game registers its own `IMenu`, or adds pages (`MenuPages`) and its
  switches as Settings rows (`SettingsRows`); the Settings page is a part a game's own menu shows (`SettingsPage`).
- Nothing a menu needs is the engine's menu's alone: starts and leaves (`MatchControl`, `MatchCommands`), quitting, the
  player's settings, the Steam friends hosting (`SteamFriendList`, which the loop now updates) and their joins
  (`JoinRequests`: at once with no match, asked about during one), and the saves (`SaveFiles`).
- Pause. Offered: a game chooses, the match going on by default; the same option, pausing by default; no change.
  Chosen: a game chooses (`Engine:Ui:MenuPausesOffline`, false), so the starter behaves as before. A host's and a
  client's matches never wait: a paused host would stall its peers.
- The demo: the mesh sample swaps the menu for its own; the starter keeps the engine's, unchanged in what it offers.

## Our own toolkit, on Skia

Readers checked the libraries in October 2026 against the engine's rules: Avalonia and Gum.SkiaSharp need SkiaSharp
3.x (the engine is on 4); RmlUi, ImGui.NET, Noesis, Ultralight, and YogaSharp are native libraries; Myra brings a second
text and drawing path beside Skia; Yoga.Net allocates 44 to 651 KB a layout, where a frame must allocate nothing. So the
toolkit is the engine's own (`UI/Toolkit/`), with no new package, drawing through the same Skia surface the other layers
use.

## Described in code

Offered: described in code, re-run while shown, with state kept by an id the code gives; a tree of widget objects built
once and changed by their properties (Unity, Godot, Avalonia). Chosen: described in code (`Build(Ui ui)`). Nothing has
to be kept in step with the game's data, which is where widget trees usually go wrong (rows added and removed by hand),
and an agent changes one function that reads like the result. The costs were paid inside the toolkit: nodes are structs
in a pooled array and scopes are ref structs, so a declaration allocates nothing; a page is drawn again only when a hash
of its result changes; input is matched against the last layout, as the old menu did.

The layout is flexbox's and Clay's (fit, fixed, grow by weight, share; padding, gaps, alignment; anchors over the rest;
grids; scroll areas), widths first, then wrapped text, then heights. Tests pin its rectangles with a measurer of fixed
advances, so they do not depend on the machine's font.

## Behaviour, with a look a game replaces

Offered: behaviour with a replaceable look; behaviour only (the game draws everything); full themed widgets (tabs,
dropdowns, trees, windows, a style sheet; +4 to 6 days). Chosen: behaviour with a replaceable look. Elements carry what
is almost always done one way (a click on release over the same element, a slider's drag, the wheel to the innermost
scroll area, a text box's typing, keyboard focus); each kind is drawn by a `UiStyle` whose every part is virtual, the
engine's plain one the default, and an `Area` is drawn by the game itself. A full widget set would have imposed a design.
The mesh sample's `SampleMenuStyle` shows a look of a game's own in 70 lines.

## One sizing rule, and a player's UI scale

- The rule. Offered: scale with the screen's height (sizes for a 1080-pixel-high screen); fixed pixels times the
  desktop's display scaling. Chosen: the screen's height, as most games do and as the menu already did (its text 3 % of
  the height): the same share of the screen at any resolution. Before, the console, stats line, and loading list used
  18 px times the display scaling, so they shrank on large screens.
- The rebuilt menu. Offered: one rule for all, with a look test on pictures of every page before and after; pixel
  identical. Chosen: one rule with the look test. The menu came out within 2 px of its old places; the console, stats
  line, and loading list now draw 12 px text at 1280x720 and 24 px at 2560x1440 (18 px at 1920x1080 as before), which
  Lucy approved as shown.
- The framebuffer's height counts a Retina Mac's doubled pixels, so the display-scaling factor went.
- The UI scale. Offered: a player's setting; an engine option only; none. Chosen: a player's setting, 75 to 150 %
  (`"UiScale"` in `settings.json`, `Engine:Ui:Scale` its default), on the Settings page's Game tab.

## What the first version has

Offered beyond the core, at their costs: keyboard focus (+1 day), wrapped text and fallback fonts (+1 to 1.5), tweens
(+0.5), grids (+0.5 to 1), nine-slice images (+0.5), a safe-area margin for TVs. Chosen: focus (Tab and the arrows to the
nearest element, Enter or Space to press, designed so gamepads drive the same focus later), wrapping with an ellipsis
and system fonts for characters the UI font lacks (`SKFontManager.MatchCharacter`), tweens, and grids. Not chosen:
nine-slice images and the safe area, which a game can add. No rich text markup and no right-to-left text.

## For agents and tests

- `Ui.Dump()` prints a layout one element a line (kind, id, text, rectangle, state); the console's `ui` prints every
  toolkit layer shown, and `ui press LABEL` clicks an element by what it reads, so a tool or an agent works a running
  UI by its words.
- Tests click by label, draw a layer alone (`LayerPicture`, `ITERA_UI_PICTURES` for pictures to look at), and check its
  ink stays in its bounds (`LayerInk`) and an idle frame allocates nothing.
- A game's tests of its own menu had no way in: `MatchControl`'s state is set by the loop alone. They now have
  `MatchControlStates` (a join connecting, a match starting and playing, its end, as `MatchFlow` gives them; a test
  checks them against a real match), `MatchControl.PendingStart` and `PendingLeave`, and `PlayerSettings.WithoutFile`.
- The UI pass blends the UI over the picture in linear light (its target is sRGB), while Skia composes in sRGB values:
  a see-through colour shows more of the world in the game than in a picture (a dark panel at 88 % alpha lets about
  half through). Lucy chose to leave it and note it: see-through colours are chosen from shots of the running game.

## Gamepads, for later

Read on 2026-10-11, for the item that takes them up:

- GLFW 3.4 (what Silk.NET uses) reads buttons, sticks, and triggers with about 395 controller mappings, but has no
  rumble and does not say what kind of pad it is (for the right button glyphs).
- SDL3-CS 3.4.18, for pads only beside GLFW: rumble, gyro, lights, the pad's kind, and native libraries for six
  platforms.
- Steam Input, for games on Steam only: the player's own bindings and glyphs through Steam.

The toolkit's focus is driven by `UiKeys` (next, previous, the four directions, press), so a pad's buttons map onto the
same keys. All devices feed one player today; split screen needs input per player (limit 3).

## Left for later

Gamepads (above); IME composition (GLFW has none); nine-slice images; a safe-area margin; rich text and right-to-left
text; drawing again only the elements that changed (a layer's whole changed area is drawn again and uploaded today); a
4:3 screen with the mesh sample's menu, whose Settings panel then runs past the right edge.
