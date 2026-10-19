# Record: values, measurements, and reasons removed from the UI, sessions, saves, replication, audio, navigation, and voxel rules

Moved on 2026-10-19 from `.claude/rules/ui.md`, `sessions.md`, `replication.md`, `audio.md`, `navigation.md`, and
`voxels.md` when those files were cleaned to the docs rules (rules and the map only; options by key, no values).
The passages are quoted as they stood. The current values are in the code and in the generated
`docs/reference/engine-options.md`. The rules files were split: `ui.md` became `ui.md`, `ui-input.md`, and
`ui-menu.md`; `sessions.md` gave `saves.md`.

## From `ui.md`

### Layers and text

- "the starter's `MinimapLayer`, which keeps below the stats line while that shows"
- "The aim dot (`AimDotLayer`, the first of a match's layers, which the engine adds before the game's): a white square
  in a black one at the screen's centre, 2 and 4 pixels across on an even side, 3 and 5 on an odd one, at the screen's
  own pixels at any render scale; hidden while the loading screen is drawn."
- "`Amounts` formats counts as 500, 12.5K, 30M."
- "Silk.NET passes on no key repeats, so `InputService` repeats a key a text box says repeats (Backspace, Delete, Left,
  Right; `ITextInput.KeyDown` returns true) after 0.4 s, 30 times a second, for the menu's box too; the console
  repeats its own."
- "Characters arrive whole (`Rune`): `WindowService.SetCharacterCallback` takes GLFW's code points, since Silk.NET's
  `KeyChar` cuts one past U+FFFF to 16 bits, and `TextLine` keeps a UTF-16 pair whole; a glyph the UI font lacks draws
  as its box. GLFW 3.4 has no IME composition (preedit) support."
- "units are pixels of a 1080-pixel-high screen, scaled with the screen's height and the UI scale
  (`Ui.UnitPixelsFor`), text never under 10 px."
- "`UiFonts.Pixels(height)` is `Engine:Ui:FontPixels` units (18) of it, so 12 px at 720 and 24 px at 1440 ... The
  screen's height is the framebuffer's, so a Retina Mac's doubled pixels count."
- "The UI pass blends the UI over the picture in linear light (its target is sRGB), while Skia and `LayerPicture`
  compose in sRGB values, so a see-through colour shows more of the world in the game than in a picture (a dark panel
  at 88 % alpha lets about half through): choose see-through colours from a shot of the running game."
- "The UI scale is the player's (`PlayerSettings.UiScale`, 0.75 to 1.5, `"UiScale"` in `settings.json`,
  `settings uiscale`; `Engine:Ui:Scale` its default)".
- "`MenuPicturesTests` every menu page's at two sizes".
- "SkiaSharp 4.x: text APIs take `SKFont`; several `GRContext` members changed since 2.88."

### Loading screen

- "at most `Engine:Loading:MaxSeconds` (15), then a Warning."
- "it fades out over 0.3 s."

### The menu

- "its look `MenuLook`: the plain style at 32.4-unit text, with 3.24-unit outlines and 12.96-unit corners"
- "It plays the music track `Engine:Audio:MenuMusic` names (`MusicPlayer.MenuTrack`) while there is no match or one is
  being joined, fading in over 2 s, and fades it out as a match starts unless the game has played another".
- "`Engine:Ui:MenuPausesOffline` (false by default)".
- The Settings page's ranges: "mouse sensitivity (0.02 to 0.5 degrees a pixel), field of view (60 to 120 degrees
  across 16:9), UI scale (75 to 150 %, in steps of 5)".

## From `sessions.md`

- "`Mode=Host` listens on UDP port 47600 (`Port`)".
- "`SessionService.MaxUnreliableBytes` (1,020 bytes on LiteNetLib, measured in `LoopbackTests`; 1,024 on Steam)".
- "`MessageWriter.WritePosition` writes a `WorldPoint` as three int32 in 1/64 cm (0.16 mm, to ±335 km;
  `SessionProtocol.PositionStepsPerMetre`)"; "replication's snapshots carry them as lanes of 1/1,024 m".
- "A host simulates every body in its own origin, so bodies near a client far from the host's camera step in coarser
  floats (1 mm at 10 km, 8 mm at 100 km)."
- "Leaving while a match loads waits for the navigation thread (5 s at most; its fill cannot stop early)"; "a match's
  services are freed when it ends (its voxel memory to 0 MB, logged)".
- "`Engine:Session:AddedDelayMilliseconds` (0 to 2000) ... `AddedJitterMilliseconds` (0 to 1000) ...
  `AddedLossPercent` (0 to 100)".
- "(`Engine:Steam:AppId`; 0, the default, leaves Steam off with an Information line; without the client, a Warning, and
  the game plays without it)".
- "`Steamworks.NET 2024.8.0` (Steamworks SDK 1.60) has no native libraries ... (Windows packs structs to 8, Linux and
  macOS to 4)".

### Saves (now `saves.md`)

- "every `Engine:Save:AutosaveMinutes` (5; 0 to 1,440, 0 for only at the end)".
- "The file is the edit log stream, format 10 (it may hold resets, capsule edits, clears of some materials (boxes, and
  since format 10 spheres), removals of voxel objects' parts, clears of their far copies, and objects placed and
  removed whole during play, and ends with the game's sections, at most 256, none in a join payload; its generator id
  is the world's, with the object kinds' fingerprint where the run has kinds, `VoxelObjectKinds.WorldId`, so a save of
  other kinds is refused; formats 1 to 9 still read, those before 7 with their plain generator id taken for such a
  world too, and an older build refuses format 10)."
- "The log indexes at most 2^24 column entries since its last reset (`EditLog.MaxIndexEntries`, counted over all ten
  levels whatever `Engine:Voxels:Levels` is, so every game agrees)".

## From `replication.md`

- Priorities: "(how fast it gains: a player 4, a character 1, a far body 0.25)".
- Fields: "`AddPosition` (absolute, three lanes of 1/1,024 m), `AddAngle` (65,536 steps a turn), `AddRotation` (smallest
  three, 12 bits each) ... at most 31 lanes (`ReplicatedState.MostLanes`)".
- "all of a run's parts at most `ReplicatedKinds.MostPredictedBytes`, 256, the engine's climber's 43 among them".
- "at most `MostShownTicksBehind` (30) steps back".
- Options: "`SendHz` (30, 1 to 60), `BytesPerSecond` (65,536, 4,096 to 1 MiB), `PeakBytesPerSecond` (131,072, at least
  `BytesPerSecond`, at most 4 MiB), `InterpolationMilliseconds` (100, 0 to 1,000)."
- "keeping its last 32 states"; "into packets that fit `MaxUnreliableBytes`, at most 32 a send, until the client's
  bytes are spent (`BytesPerSecond`, 64 KB/s, with what a send leaves kept up to a send's share of
  `PeakBytesPerSecond`, 128 KB/s)"; "(every second step at 60)".
- "up to 64, each numbered"; "The host remembers a client's last 64 packets."; "A client keeps 64 states of each
  entity by tick"; "It shows at most 65,536 entities and remembers at most 16,384 removed ids".
- "falling 0.01 tick a step so a longer delay is followed".
- Prediction: "the move in 1/127ths, the yaw in 65,536ths of a turn, the pitch in 32,767ths of a quarter turn"; "the
  reply carries the last 8 (newest first, the newest's tick), then the host's tick each was made at the sight of (in
  1/16 steps ...)"; "it starts once two steps of them wait beyond the first, so two wait against jitter; when more than
  eight wait it skips ahead; ... (after 30 missed, standing still); ... it starts again from the newest less two";
  "The client keeps 128 steps."; "a height past 100 m or none"; "(the feet more than 1 cm apart ...)"; "(0.85 of it
  kept a step; past 2 m at once)".
- "Measured figures (lag, budget) are in `docs/replication.md`."
- The test paragraph, which described the classes: "`ReplicatedStateCodecTests` (fields, changes from a baseline,
  damaged records, the blend, schemas and kinds), `ReplicationServiceTests` over `ReplicationHarness` (a host and a
  client in one process without terrain, test kinds of movers and players, a host system that moves them, the clock
  real until the join and then stepped; added delay through `SimulatedNetwork`): following the delay behind (and a set
  delay), through 100 ms each way with jitter and loss, a send of several packets, removals (also through loss, and
  `Remove`), an id added again under another kind, an id freed offline, reach, the budget, a client that takes a
  leaver's place, refused kinds, damaged snapshots and replies, a bad reply; the systems' behaviour and allocation
  tests; the wire fingerprint of the snapshot and reply ... (`WireFingerprintTests`); and `PredictionTests` (over the
  harness's test floor: a client's player moving at once and the host's copy ending where it does with no correction
  after the first second, also through 100 ms each way with jitter and loss; a host that moves the player correcting
  the client ...; after a client stall that drops steps, the host starting again and the corrections stopping; a
  game's movement state (the harness's test boost, `TestBoostState`) put back by each correction when it is a
  predicted part, and corrected again and again when it is not; the tick each client showed known on the host ...;
  commands read as quantised ...; damaged replies with commands; no allocation)".

## From `audio.md`

- "rendered at start as 4 levels of 4 variations (by default)"; "fades out over 80 ms after".
- "64 voices (`Engine:Audio:MaxVoices`), each up to a gain of 4 (`AudioMixer.MaxGain`), sounds under 0.002 of gain
  never started".
- "(paths from the game's folder; 1 to 16 files, each a variation, never the same twice running; each at most 30 s)";
  "at most 256 recipes in all"; "(one file of at most 120 s)"; "fading out ... over 80 ms".
- The error line: "`Audio: sound NAME: the file PATH cannot be read (WHY); it plays nothing`".
- "plays a sound at the listener (a sustained one for 4 s)".
- Music: "at most 64, in its `IRunStartup`"; "the last 8 kept"; "8 blocks of 4096 frames as 16 bits"; "so
  `Engine:Audio:MaxVoices` is at most 254, with 4 buffers queued"; "each at sin(t·π/2) of the music volume as t moves
  over the fade (0.71 each halfway)"; "`fadeSeconds = 2`".
- "An Ogg loop shorter than its start takes to decode (the start at about 350 times real time: a 0.3 s loop after
  100 s) underruns."
- "`VorbisDecoder` (NVorbis 1.0.0-rc.2 ...)".
- "NVorbis 1.0.0-rc.2 (MIT), a release candidate pinned for its fixes over 0.10.5 (whose seeks land off and can throw
  "GranulePos mismatch"). It allocates while it decodes and its `SeekTo` is wrong near a file's start (exact from 0)."

## From `navigation.md`

- "a grid of 50 cm cells"; "at most 128 cells read from the level-0 voxels"; "within about a centimetre of the
  source's ground".
- "so a 10 cm wall anywhere in it blocks it and a 30 cm block raises its floor" (a cell reads every object's column in
  its 50 cm square).
- "`Engine:Navigation:WindowCells`, 1024 by default, 256 to 4096"; "(`NavigationService.TileCells`, 64 cells, 32 m)";
  "when that corner lies more than a quarter of the window away, or differs while the focus is within an eighth of the
  window of its edge".
- "(`DefaultBudget`, 4,096)"; "for a window of 2,048 cells square they hold about 88 MB, so a match's walkers share
  one".

## From `voxels.md`

- "An edit larger than a chunk's worth of level-0 voxels (128^3: a 12.8 m cube in 10 cm voxels, so only a very large
  edit; smaller edits apply at once)"; "4 columns a step (`Engine:Voxels:EditColumnsPerStep`, a count so every game
  keeps the same pace ...)".
- "(`Engine:Voxels:RemotePlayerRadiusMetres`, 16 m ...)"; "a 3 m level-0 disc around each loose body away from every
  player (stretched ahead by 0.5 s of travel, at most 12 m ... at most 32 bodies ...)".
- "sets the world's voxel size, 1 cm to 1 m (`VoxelGrid.MostBaseCentimetres`; a coarse world suits terrain deformed in
  large strokes, at the cost of shapes under a voxel showing only as soft dips in the densities, walls in voxel steps,
  and the ground's 1 m steps in a piece lifted from it; an edit measures a voxel on at most 10 sub-rows each way, so
  one under about a tenth of a voxel changes nothing, and a small ball settling into coarse voxels leaves no trace;
  `Engine:Voxels:StreamingRadiusMetres` 0 takes the radius from the grid, 4.625 level-0 columns where that passes
  59.2 m)".
- "up to ±160 km each way, up and down too (`VoxelGrid.MostReachCentimetres`), and a grid of a base alone has the
  default bounds (±512 m and -51.2 to 102.4 m at 10 cm)."
- "So a world's height costs memory only where its ground and edits are: a column of a world 25,000 layers tall stores
  the layers one 12 layers tall does."
- "(up to 10 x 10 sub-rows along y and z, each to the centimetre along x: a 10 cm voxel's cubic centimetres)".
- The reason for the float rule: "(in .NET 10 `Vector2.Lerp`, `Reflect`, and `Transform` use `MultiplyAddEstimate`,
  which fuses the multiply and add on some machines and not on others)".
- "The starter's `GameGenerator` is one." (a heightfield generator)
