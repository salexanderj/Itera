# Itera.MeshSample

A small game on Itera without voxel terrain: it registers no generator and does not call `AddIteraVoxels`, so its
matches have no voxel world. It shows what such a game composes and how the engine's parts work over meshes:

- The yard (`Yard.cs`): a 60 m checker ground, stone blocks 1, 2, 3, and 4 m tall, ramps of 20 and 40 degrees, a pane
  of glass, two amber see-through cubes behind it as the start sees them (blended, two-sided materials), and a screen,
  all built
  in code as mesh assets (`MeshAssets.Add`, a texture through `MeshTextureData.FromRgba`), drawn by entities with
  `MeshPlacement` and `DrawnMesh`, and registered as mesh colliders with the stone tagged climbable (`YardSystem`).
- The player (`SampleSpawnSystem`) walks with the engine's movement and climbs the blocks with the engine's
  `ClimbingMode` (Space at a block grabs it; W, S, A, D on it; Space jumps off; Ctrl lets go; the top pulls up).
- The starter's fox (its files linked from `src/Game/Models/Fox/`) walks a circle (`SampleFoxSystem`).
- The console's `balls` drops loose bodies that land on the mesh ground and stay where they rest (no `ILooseBodyRest`).
- A render pass of its own (`Rendering/AimHighlightPass.cs`, an `IRenderPass` at the `Transparent` stage): the climbable
  block the player aims at within 1.5 m (`AimHighlightSystem`, the shared ray from where the player's tools aim, through
  `YardColliders` to the piece) glows along its edges, pulsing. Its shaders are in `Shaders/`, copied to
  `Shaders/Game/` beside the engine's by the item in `Itera.MeshSample.csproj`, and include the engine's `view.glsl`; `AimHighlightPassTests` checks them against
  `AimHighlightData`.
- An image view on a screen (`YardCamera`, `YardCameraSystem`): a camera high over the yard's south-west corner, looking
  in among the blocks, is drawn into a 384 by 216 image every other frame (`RenderViews`), shown on a screen 4 m up on a post
  past the blocks, ahead of the start (the yard's `yard-screen`, its `screen` material: `RenderViews.ShowOnMaterial`),
  which glows with it; the camera sees the screen too, showing its own last picture there.
- Its own menu in place of the engine's (`SampleMenu`, in its own look, `SampleMenuStyle`): a bar down the left with
  Play, Join, Settings, and Quit (over a match Resume, Settings, Leave, and Quit), and beside it the page an entry opens:
  the host's address (Enter joins; a bad one says why) or the engine's Settings page inside a panel of its own.
- Sounds and music read from files (`SampleSounds`, registered by `SampleRunStartup`; the files in `Sounds/`, made in
  code by `tools/sounds/make_sounds.py demo`, listed in `Sounds/SOURCES.md`): its menu plays the music track `menu` (an
  intro, then a loop its Ogg tags name) while there is no match; as a match starts `SampleSoundSystem` cross-fades to
  `yard` (looping whole), holds the screen's hum at its picture's middle (a WAV loop with a `smpl` chunk; walk up to the
  screen to hear it), plays a clang (one of three files) at a block's middle as the aim comes onto it, and a chime at the
  listener (stereo, 24-bit) once the loading screen lifts. The console's `music` and `sound files` show them.
- The sky is the engine's default (no `IAtmosphereSource`).
- Its play area is the yard (`PlayArea` with the yard's bounds, in place of the engine's 10 cm grid's): the mover's map
  edge is a wall at the yard's side, so nobody walks off it.

## A game's own menu

A game swaps the engine's menu by registering its own `IMenu` singleton after `AddIteraEngine` (the last registered is
the run's), as `AddMeshSample` does: `services.AddSingleton<IMenu, SampleMenu>()`. `SampleMenu` is a toolkit layer
(`UiLayer`) of about 430 lines; a game's menu copies these parts of it:

- `AboveMatch` overridden to true (the `IMenu` interface says so too, but `UiLayer`'s own member wins), so it draws
  over the HUD and keeps the pointer while the game's input is blocked.
- `Visible` is `IsOpen`: always with no match, while a join connects or a match starts (a join with no menu showing
  would leave the game's input free), and over a match once Escape opened it (`Escape`).
- `BeforeUpdate` follows `MatchControl`: a match starting (its `Started` count, which also catches one that failed
  within a frame) or ending closes the menu over it and shows the first page; `LastEnd` says why the last match ended.
- Its text box's typing goes through the menu's input: `GiveTyping` does nothing, `StillTyping` is true, and
  `TextFocus` is the box's `TextEntry` while its page shows; `Ui.StartTyping(id)` as the page opens, so Enter joins at once.
- The engine's Settings page: `SettingsPage.Build`, then `Footer` (true when Back is clicked); `Opened` as it opens,
  `PlayerSettings.Save` as it closes; `CapturingBinding`, `BindingPressed`, and `Escape` pass its key capture on.
- Over a match, the shade is a plain full-screen `Area` declared first, so it draws under the page (anchored elements
  draw over the flow).
- Its music: `FollowMusic`, from `BeforeUpdate`, plays its track (`MusicPlayer.Play`) while the phase is `Menu` or
  `Joining`, and stops it as a match starts unless the game played another meanwhile (`MusicPlayer.Current`); the
  engine's menu plays `Engine:Audio:MenuMusic`'s track the same way.
- The starts, leave, and quit go through `MatchControl.RequestStart` (`MatchCommands`' starts), `RequestLeave`, and
  `WindowService.RequestExit`. A game with Steam also lists the friends hosting (`SteamFriendList`, `Watching`) and
  answers `JoinRequests` (a friend's join asked for during a match waits for it, or for the match's end); the sample
  has Steam off.

Its tests (`SampleMenuTests`, over `SampleMenuHarness`) click it by what its elements read, with the engine's hooks for
a game's tests: `MatchControlStates` puts the test's `MatchControl` into the loop's states (a join connecting, a match
playing, its end), `MatchControl.PendingStart` and `PendingLeave` show what the menu asked for,
`PlayerSettings.WithoutFile` reads and writes no file, and a `MusicPlayer` with no audio thread says what it was asked
to play (`Current`). `SampleSoundSystemTests` hear its sounds in a match with no device through
`AudioService.QueueWithoutThread` (`QueuedEvents`, `QueuedLoops`; sound on), and `SampleSoundsTests` read each file. `SampleMenuPicturesTests` draws every page at 1280x720 and 2560x1440 into the
folder `ITERA_UI_PICTURES` names. The UI pass blends the UI over the picture in linear light, so a see-through panel
looks lighter over the world than in those pictures: take a shot of the running game (`game.py --port PORT shot`)
before settling a colour.

## Running it

Run it: `dotnet run --project samples/Itera.MeshSample -- --Engine:Session:Mode=Offline`. Its data folder is
`IteraMeshSample`; runs an agent starts add `--Engine:Application:DataFolder=IteraMeshSample-agent`. Vulkan validation:
`.claude/skills/vulkan/validate.sh --binary samples/Itera.MeshSample/bin/Debug/net10.0/Itera.MeshSample`. Its tests,
played without a window, are in `tests/Itera.MeshSample.Tests`. Its systems' run order, with reasons where they matter, is in
`MeshSampleServiceCollectionExtensions` (`AimHighlightSystem` after the camera's systems, since it aims from the camera's
pose, and `SampleSoundSystem` after it, since its clang follows the aim); unlike the starter, it has no
`SystemOrderTests` pinning it.
