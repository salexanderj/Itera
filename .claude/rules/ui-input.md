---
paths:
  - "src/Itera.Engine/Input/**"
  - "src/Itera.Engine/Commands/**"
  - "src/Itera.Engine/UI/{ConsoleLayer,ConsoleScript,DevConsole,TextEntry,TextLine}.cs"
  - "src/Itera.Engine/Settings/Tunables.cs"
  - "src/Itera.Engine/Systems/MovementKeys.cs"
  - "tests/Itera.Engine.Tests/Input/**"
  - "tests/Itera.Engine.Tests/Commands/**"
  - "tests/Itera.Engine.Tests/UI/{ConsoleLayer,DevConsole,TextLine}Tests.cs"
  - "tests/Itera.Engine.Tests/Settings/TunablesTests.cs"
---

## Input, text entry, the console, and tunables

The toolkit is in `.claude/rules/ui.md`. The menu and the settings are in `.claude/rules/ui-menu.md`.

### Text entry

A game's own text box (a chat line, a save's name) takes typing through `IFrameInput.SetTextFocus`. A box is an `ITextInput`; `TextEntry` is a ready-made one over a `TextLine`.

While the focus is set, the console and the menu are closed, and the game's input is not blocked:

- Every key goes to the box, Escape and Enter too. The box ends the typing itself, usually with `ClearTextFocus`.
- The backtick goes to the box while the console is off.
- The box gets the characters typed and pasted.
- No key reaches the game, and the keys held as the focus is set are let go.

The mouse, the wheel, and the pointer stay the game's. The backtick still opens the console.

Silk.NET passes on no key repeats. `InputService` repeats a key that a text box says repeats (`ITextInput.KeyDown` returns true), for the menu's box too. The console repeats its own keys.

Characters arrive whole as `Rune`s: `WindowService.SetCharacterCallback` takes GLFW's code points. `TextLine` keeps a UTF-16 pair whole. A glyph the UI font lacks draws as its box. GLFW has no IME composition (preedit) support.

A text box's callbacks and `IEscapeHandler.Escape` run inside the window's key event. Keep them short, make no ECS changes, and do not throw. An exception through GLFW's native frames ends the process unsaved. `InputService` keeps what a handler throws and throws it again on the loop's thread, after the window's events are pumped (`InputService.ThrowIfCallbackFailed`, also first in `Update`). There it ends the match as any fault in its work does.

### Escape and the pointer

Escape goes, in order, to:

1. the console, when open;
2. the menu, when open;
3. the game's text box;
4. the game's screens (`IEscapeHandler`, added with `AddEscapeHandler`): the newest first, each taking it or passing it on, with the pointer and keys left as they are;
5. the game's panel (`IPanelInput`);
6. otherwise, the menu opens.

A match's end drops its text box, screens, panel, and drawn pointer (`InputService.EndMatch`).

While a menu shows, the game gets no input, and a click never captures the pointer (`IMenuInput`, `InputService.MenuPointer`). A game draws its own pointer through `InputService.SetDrawnPointer` and takes pointer buttons for a panel through `SetPanel`.

### Snapshots and key bindings

`InputService.Frame` gives a snapshot per frame and `InputService.Tick` one per fixed step (`InputSnapshot`). `InputAccumulator` collects the events between them.

Key bindings (`KeyBindings`, `KeyAction`, `KeyRow`, `InputBinding`):

- The engine's `MovementKeys` register first, then a game's own. Each is registered once a run, by the game's `IRunStartup`.
- Escape and the backtick are never bound.
- An action has one key. A key that another action has leaves that one with None, unless the two never work at once (`KeyRow.Context`).
- A binding read from the file, or registered later, that clashes with another action keeps the file's key. `KeyBindings` unbinds the default.
- A system reads a binding with `PlayerSettings.Keys.WasPressed`, while the match's `PlayerInputBlock` lets input through.

### The console

`DevConsole` and `ConsoleLayer` are the console. `ConsoleScript` holds its scripts, which `DevConsole` runs (`Hold`, `RunScript`).

How the console works and the list of its commands are in the `run-game` skill. Change that list when you add or remove a command.

The commands are in `Commands/`, not `Console/`, which would hide `System.Console`. The command classes register their commands with `ConsoleCommands`:

- `EngineCommands`, `MatchCommands`, `SettingsCommands`, `TuningCommands`, `ParticleCommands`, `SoundCommands`, `PhysicsCommands`, `UiCommands`;
- `TimeCommands`: pause, step, and timescale;
- `EcsCommands`: `ecs`, registered at a match's ECS step.

The run-only `look`, `sun`, `time`, and `lens` (`EngineCommands`) set values over the frame's view: the look tuning of `RenderView`, `SunOverride`, and `LensOverride`. See `.claude/rules/rendering-look.md` (the look and the sun) and `rendering.md` (the lens).

### Tunables

`Tunables` (run-wide) holds the numbers a game tunes while it plays: movement, recoil, sway, part limits, an enemy's.

- Register each once at the run's start with `Register(name, value, least, most, help, scope)`. A name has 1 to 64 letters, digits, '.', '-', or '_', and the default lies within its range. The first match seals the registry.
- Read a tunable as `Tunable.Value`, which allocates nothing.
- The console's `tune` lists them, reads one, sets one within its range, or puts them back (`TuningCommands`).
- A `Match` tunable is one that a client predicts, so it must move by the host's numbers. It is set only offline or on a host. The host sends its value by name to its clients after the step it changed, and every one as each client joins, at its default too (the session's `Tune` message, `.claude/rules/sessions.md`). A client's tunables go back to their defaults when its session ends.
- A `Local` tunable is each game's own.

### Tests

`TextFocusTests`, `KeyBindingsTests`, `KeyboardFocusTests`, `InputInjectionTests`, `MenuInputTests`, `PanelInputTests`, `PointerCaptureTests`, `InputAccumulatorTests`, `DevConsoleTests`, `ConsoleLayerTests`, `TextLineTests`, `TunablesTests`, and the `...CommandsTests` classes in `Commands/`.

### Files in `src/Itera.Engine`

- `Input/`: `InputSnapshot` and `InputAccumulator`, `PointerCapture`, `IKeyboardFocus`, `IMenuInput` (with `UiPointer`), `IFrameInput` and `ITickInput`, `InputService`, `IPanelInput`, `ITextInput`, `IEscapeHandler`, `KeyBindings`, `InputBinding`.
- `Commands/`: the command classes above, with `ConsoleCommands`, `CommandLine`, `CommandContext`, `CommandScope`, and `IConsoleOutput`.
- `UI/`: `DevConsole`, `ConsoleLayer`, `ConsoleScript`, `TextEntry`, `TextLine`.
- `Settings/Tunables.cs`: `Tunables` with `Tunable` and `TuningScope`.
- `Systems/MovementKeys.cs`: the engine's movement actions.
