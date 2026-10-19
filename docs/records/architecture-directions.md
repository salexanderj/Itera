# Architecture directions: measurements and dated accounts

Moved on 2026-10-19 from sections 8 and 9 of `ARCHITECTURE.md` (design directions, open work), when those sections were cut to what was chosen and why. The passages are verbatim, with their dates and the names the code had then. Some name the starter game or Zorf TD, where the measurements were made. This record is not kept current.

## Units and numbers: the origin

A walk with the origin following the player every 100 steps matches one without within a millimetre over 45 m (the
  one without rounds its feet to floats 30 to 40 m out, losing about a micrometre a step), and balls at rest settle
  into the same voxels. Moving it with 384 balls and their voxel statics took 0.6 ms in a Debug test on a busy machine
  (12 ms the first time, mostly compiling). The renderer draws everything local to it and takes it back only for what
  is drawn by place (the surfaces' noise, strata, tint, and texture tiles), choosing and ordering the world's columns
  from the view's absolute place and snapping the shadow maps' texels in absolute terms; the particles' GPU pool and
  the sounds playing move with it (`.claude/rules/rendering.md`, "Under a moved origin"). Pictures of the starter's
  start, far, high, and down views before and after the console's `origin move 300 0 -200` differed in 7 to 15 of
  921,600 pixels, each on a silhouette's edge (2026-10-10); resting debris kept its place. On the GPU the whole item
  (the grid's size through sparse columns) costs 0.4 to 1.0 % of the world pass: the benchmark at 256 m and four
  levels, offscreen at 2560x1440 on a quiet machine, two runs each in turn, took 1.736 ms at the start against 1.724
  before it, 2.366 against 2.346 at `high`, and 2.639 against 2.613 in the flight (the shadow maps the same), from the
  shaders' origin and texture tile work (2026-10-10). A flight of 2.8 km at
  500 m/s moved the origin 5 times and ended within a centimetre of where its 336 frames should take it; a walk with the
  origin following every 8 m matched one where it stayed within a millimetre.

## Voxel storage: columns that store a window of layers

Measured 2026-10-10: 64 columns of the hills test generator stored 111 layers in a world 12
  layers tall and in one 25,000 layers tall; a ball set 100 km up widened its column to 7,813 stored layers (62 KB of
  chunk state) and two runs that can hold a surface; a ball 60 m up over 1 cm voxels left blocks for 11 of the 45
  layers the column stores; a ray 100 km down took 0.6 ms in Debug.

## Voxel storage: views to 8 km

- Views to 8 km (measured 2026-10-10 on the starter's 8 km map, Release, offscreen at 2560x1440, seed 1, after the
  world loaded; at the start and at `distant`, 3.5 km out): with 7 to 10 levels and views of 2, 4, and 8 km, 542 to 672
  columns were resident (140 to 155 MB of voxels), the mesh heap held 209 to 276 MB of its 512 (nothing freed), the
  camera drew 708 to 1,243 blocks (1.3 to 2.4 million triangles; the list takes 32,768) and the cascades 248 and 563 at
  the start, the main thread spent 0.5 to 2 ms a frame on the meshes, and the world loaded in 2 to 3 s (once 5.3 s;
  a busy machine). Nine levels reach 17 km, so an 8 km view draws its last kilometres from level 8 (25.6 m voxels). The
  world pass's GPU time (RX 5700 XT, the benchmark offscreen at 2560x1440 on a quiet machine): at the start 1.81 ms
  with a 2 km view at any of 7 to 10 levels, 1.82 to 1.87 ms with 4 or 8 km at 8 to 10 levels, and 1.93 ms with 7
  levels at 4 or 8 km (their farthest level, 6.4 m voxels, is finer, so more triangles: 2.06 million against 1.59); at
  `high`, 40 m up, 2.91 to 3.07 ms (2.37 with the engine's 256 m and four levels: more ground is drawn where there was
  sky); at `distant` 1.92 to 2.06 ms; the shadow maps 0.08 to 0.18 ms throughout. The starter's 8 km at nine levels:
  1.85 ms at the start, 3.00 at `high`, 1.98 at `distant` (two runs within 0.2 %), against 1.74, 2.37, and 1.76 at
  256 m and four levels. Two faults showed only past 7 levels and were fixed: the level
  balance marked every finest column under each drawn column (262,144 for one level-9 column; 4.2 s a frame at 9
  levels), and a coarse column with a child past the view's end or the map's edge never split, so a 2 km view at 10
  levels drew the start from 51.2 m voxels.

## The edit log: formats, as they were counted

(the format is 10, whose generator id carries
  the voxel object kinds' fingerprint where the world places objects, whose edits may place and remove objects whole
  (since 9) and clear some materials in a sphere (since 10), and which ends with a game's sections (since 8), none in a
  join payload; formats 1 to 9 still read, those before 7 with their plain generator id taken for such a world too, and
  an older build refuses a newer format; `EditLogSerializer` lists what each added)

## The edit log: compaction

a long log is small (50,000 edits make a 2.5 MB payload) but replaying edits
  into a column costs. `VoxelEditing.ApplyToRegion` fills each row's span of a shape at once instead of testing each voxel,
  16 times faster at level 0 with the same bytes.

## Voxel columns: the edit warm-up

`StartEditWarmUp` (from the voxels' start-up step, once a run) applies shapes to throwaway chunks on a thread of its own,
round after round, so that .NET compiles the edit code again, optimised; .NET counts calls only once no new code has been
compiled for a moment, which the game's start-up puts off, so the warm-up goes on until two rounds in a row take under half
the first-pass time, eight rounds in a row have taken within a tenth of the best round and that is under three quarters of
the first second's best (composite ReadyToRun code, about 42 ms a round precompiled, about 90 while .NET measures it, then
28 for good), nothing in the process has been compiled for 0.75 s (`JitInfo.GetCompiledMethodCount`: the code was
optimised by the second round), or 10 s have passed (`EditWarmUpStop`). In a game's Steam build the process went on
compiling a few methods a second after the edit code had settled, so the quiet came only at about 8 s and the loading
screen waited that long; the steady rounds stop it at 3.8 s (a build from `dotnet build` stops at 1.6 s, its rounds
halving; 2026-10-15).

## The raster pipeline: meshing latency

A 2 m dig's blocks are meshed and staged within 3 to 11 ms (2 to 5 frames).

## The raster pipeline: the far shadow cascade

Balanced, as
  it was until 2026-10-11, the far pass split its columns back to the camera's in every view of the starter, so the
  coarser cut saved nothing; unbalanced, its shadow maps took 29 to 34 % less in the benchmark's flight (0.142 to 0.094
  ms at the engine's sun, 0.297 to 0.212 ms at 10 degrees) and 10 to 21 % less at `distant`, the world pass 0.6 to 3.4 %
  less, with the same picture but for a few blobs of shadow edge. The views at the map's middle did not change: a camera
  on the corner where columns of every level meet makes both passes choose the fewest columns, a ring of 12 a level.

## The raster pipeline: the distant shadow cascade

Its map is a
  third layer of the shadow maps (16 MB at 2048 texels), its texels about 2.4 m wide at 2 km; the world's command lists
  are four a slot (`MeshPass.WorldLists`), about 4 MB more of host-visible memory whether it is on or not. Measured on
  the starter's 8 km map at nine levels with a 2 km distant cascade and a sun 10 degrees up (the benchmark offscreen at
  2560x1440, two runs each, 2026-10-10): it doubles the shadow maps' time, 0.19 to 0.40 ms more (2.4 to 5.1 million
  more shadow triangles), so the world pass takes 9 to 18 % longer (1.93 to 2.11 ms at the start, 3.20 to 3.58 at
  `high`); the extra lookups cost almost nothing. Listing its blocks in the far pass instead cost the same and drew the
  same picture, since the far pass then chose the camera's columns (below). It changes the starter's picture little, because the 1.5-texel normal offset (3.6 m) leaves out the
  shadows of anything under about 4 m, the boulders included, and the starter's gentle slopes cast only under a very
  low sun: at 12 degrees 0.4 % of the start view's pixels changed, at 4 degrees 2.9 % of `high`'s, the back slopes of
  the hills 200 m to 4 km out in shadow. A game with taller ground (cliffs, mountains) gains more.

## The raster pipeline: the look moved into data

moving the constants into data left the starter's pictures as they were (within 1 level) and, with that, made the
  world pass 1.2 to 2.2 % faster (0.02 to 0.05 ms at 2560x1440 offscreen, 2026-10-10; read from the buffer at every
  pixel instead, it was 1.4 to 2.1 % slower).

## The raster pipeline: cost

- **Cost** (Release, 2560x1440, render scale 1, 4x MSAA, shadows on, the benchmark's views): the world pass takes 1.7 to
  2.6 ms before materials and 2.6 to 3.9 ms with them, of it the shadow maps 0.3 to 1.4 ms. Over no anti-aliasing SMAA adds
  0.23 to 0.31 ms, MSAA 2x 0.40 to 0.46 ms, 4x 0.53 to 0.78 ms, and 8x 0.97 to 1.41 ms. The main thread spends 0.12 to 0.35
  ms a frame in `WorldMeshes.Upload`.

## Models: cost

- Cost: 1,000 near goblin models (each 19 parts of 1 cm voxels at coarser levels with distance) added 1.07 ms to the world
  pass at the benchmark's `crowd-near` view and about 0.6 ms at `crowd-far`.

## Voxel objects: cost

- Cost: the world pass stays within 4 ms in the starter's views but one, looking down over a forest from 40 m (4.72 ms),
  left as future work.

## The character mover: cost on blended ground

(`FineGround`; on smooth 1 m hills the 10
  cm mover's feet keep within about 2 cm of the 1 m mover's, and a walking move costs about 2.4 times as much)

## The character mover: cost over mesh colliders

A move over triangles alone in 10 cm cells costs about 1.5
  times one over 10 cm voxels in the tests' walks (30 against 20 microseconds); in 1 cm cells, about 6.5 times (130
  microseconds), as each column is read one by one where the voxels are read a brick's mask at a time.

## The character mover: cost of a move

A player's move costs about 8 microseconds on flat
  ground in Release.

## Loose bodies: the physics warm-up

- **The physics warm-up.** Bepu makes its solver's code for each kind of joint and its collision code for each pair of
  shapes and kind of contact at run time (through reflection and generics over its own structs), so ahead-of-time
  compilation (ReadyToRun) covers none of it: in a game's first ragdoll the main thread waited while about 400 of Bepu's
  methods were compiled, a step of 44 ms in a composite ReadyToRun build and 66 ms in a Debug build (2026-10-15). `PhysicsWorld.StartWarmUp` (from the match's physics step, once a run, `WarmUps.Physics`) builds a world of
  its own with the match's settings and Bepu callbacks on a thread of its own, drops into it what the engine's bodies are
  made of (two people's ragdolls onto a voxel cell's static of boxes and a mesh ramp, a pile of convex hulls of three
  sizes, a client's kinematic mirror, a cut part's compound of hulls, a stand-in, and a trigger zone), steps it 4 s (they
  land, sleep, wake, and a ragdoll freezes and thaws), takes it all away, and frees it, in about 0.2 s; the loading screen
  waits for it (Physics). The first deaths then took a step of 4.9 ms at most, and a few kinds of contact are still
  compiled as they first occur, a few at a step.

## Navigation: sizes and times

About 6 MB a
  grid (two are kept), 1 MB a field, and 1 MB a size at 1024. A walker on open ground crossed 2 km of a 4 km map to its
  goal with 11 moves of the 1024-cell window, each building its field in 30 ms in Release (153 ms in Debug); on a large
  map a whole field takes far less than the old map-wide grid's, whatever the map's size.

## Navigation: field time

A field takes about 0.2 s in Release.

## Navigation: partial recompute

A partial recompute was measured first and not built: a whole rebuild runs
  off the main thread in about a second.

## Anti-aliasing: SMAA cost

It costs 0.23 to 0.31 ms at 2560x1440.
On Zorf TD's view along its players' path (a measurement of that game's world, at 2560x1440) the world took 1.26 ms with
one sample, 1.65 with two, 1.95 with four, and 2.52 with eight, and SMAA added 0.30.

## Particles: measured

Measured with about 237,000 alive: the move takes 0.16
ms and the draws 0.84 to 0.89 ms; 16,000 large smoke sprites cost 3.9 ms, so large blended sprites cost fill rate, not
count.

## Sound: render time

Rendering
34 one-shots of 520 variations took 0.8 to 2.7 s at start in Release and 12 to 15 s in Debug, on the render thread, 26.7 MB
of buffers.

## Open work: the mesh path (built part)

Built: glTF models with textures (decoded at load or compressed offline), skinning, clip
  fades, mesh colliders, and matches without voxel terrain (`samples/Itera.MeshSample`).

## Open work: rendering a game can extend (built part)

- **Rendering a game can extend** (finished 2026-10-10): the look is data (`RenderLook`), the sun moves, with night,
  mesh assets' blended materials are drawn sorted per object, a game adds passes of its own at five stages
  (`IRenderPass`), a camera's lens may be orthographic, and a game draws views into images (`RenderViews`, the starter's
  minimap; a mesh material may show one, the mesh sample's screen).

## Open work: rendering a game can extend (low sun cost)

A low sun's shadow
  maps cost more (the starter's start view at 2560x1440: 0.15 ms at the engine's sun, 0.19 ms at 30 degrees, 0.36 ms at
  15, 0.40 ms at 5; their longer reach adds 0.01 to 0.03 ms of that); at night they cost 0.015 ms.

## Open work: typed text and the UI toolkit (built part)

- **Typed text, a menu a game replaces, and the UI toolkit** (built 2026-10-11): a game's text box and Escape screens,
  its own menu or pages and Settings rows in the engine's, and a toolkit that lays out, drives, and draws its screens
  under one sizing rule with a player's UI scale (`docs/ui-toolkit.md`).

## Open work: sound files and music (built part)

- **Sound files and music** (built 2026-10-11): sounds read from WAV and Ogg Vorbis files (NVorbis 1.0.0-rc.2), played
  as one-shots or looping while sustained, round the loop points their files name; music streamed from files on a
  thread of its own and cross-fading at equal power on two decks, the menu's track an option; Master, Music, and Effects
  volumes on a Sound tab; the mesh sample's sounds and music made in code (`docs/sound-files.md`).

## Open work: physics a game sets (built part)

- **Physics a game sets** (built 2026-10-11): gravity and each body's share, the solver's settings, physics materials with
  an exact bounce on bodies, mesh colliders, and voxel materials, characters gripping by the ground, layers and groups,
  trigger zones, and impact events (`docs/physics-materials-and-groups.md`).

## Open work: physics a game sets (cost)

The plain step costs what it did; every body marked and bouncy,
  7 to 11 % more while bodies touch.

## Open work: large maps (built part)

- **Large maps** (built 2026-10-10): a map's size set in its generator's grid (up to ±160 km, up and down too), voxels of
  1 cm to 1 m, positions kept local to a match's origin with absolute types (`WorldPoint`, `VoxelCoord`) wherever
  they cross it, and moves of the origin following the camera (`OriginShiftSystem`, `OriginShifts`; the renderer,
  particles, and sound follow them), positions in messages absolute, and the starter's map 8 km across (broad relief
  past 1 km), seen whole (an 8 km view over nine levels, with lighter haze), and views to 8 km (levels 8 to 10;
  measured above, "Views to 8 km"), a third shadow cascade for the distance as an option (`DistantShadowMetres`), a nav grid that moves with the players over any map (`Engine:Navigation:WindowCells`),
  and columns that store only the layers round their ground and edits, so a map's height costs memory only where
  something is, with the renderer's level choice counting a column's height (a camera high over the ground meshes no
  fine level under it).

## Open work: replication (built part)

(A short history of positions for judging
  hits is built: `Physics/HitHistory`, the hit shapes a game records each step.)

## Open work: voxel objects (built part)

- **Voxel objects finer than the terrain** (built 2026-10-12): kinds of finer voxels placed by the generator, drawn as
  models near the camera and as copies at their own size in the world's coarse levels with dithered fades between them,
  met by characters, bodies, rays, and navigation, damaged by edits, and cut into falling parts; the starter's forests
  and boulders (`docs/voxel-objects.md`).

## Open work: voxel objects (performance to win back)

Performance to win back: the world pass
  looking down over a forest from 40 m (the starter's `forest-high`, 4.72 ms against the 4 ms budget), where GPU
  culling of the models, fewer far copies' triangles, or a shorter hand-over fade would each help.

## Open work: the edit log (built part)

(Saving on a timer, off the
  main thread, is built: the autosave every `Engine:Save:AutosaveMinutes`, written on a background thread.)

## Open work: sound (built part)

(recordings play
  from files since 2026-10-11, `docs/sound-files.md`)

## Details taken out of the design directions (the review's concern 9, 2026-10-19)

Numbers and names the directions gave that no rules file holds; the code holds them now.

- sessions.md: `MaxPlayers` counts the host. `SessionService.Players`, `BeginMessage`, `SendToHost`, `SendTo(slot)`, `SendToAll(exceptSlot)`. A player's name: `Engine:Session:PlayerName`, else the Steam name cut to 32 characters, else "Player". `steam_appid.txt` next to the game, the working directory moved there only for the Steam API's start. The Steam library loads through `NativeLibrary.SetDllImportResolver`, because Steamworks.NET names only `steam_api`. Steam limits: a reliable message over 512 KB refused; a sequenced message skipped while more than 32 KB are queued; a peer with more than 8 MB held ended; reliable room 256 KB queued at most. Join chunks 16 KB.
- saves.md: the save's temporary file is `NAME.save.tmp`.
- voxels.md: brick = 64-byte occupancy mask + 12-byte record; blocks with a 16-byte palette and 1, 2, or 4 index bits per voxel, or a byte per voxel above 16 materials; air+one material brick 76 bytes. Chunk at 10 cm: 12.8 m, cells 3.2 m, bricks 80 cm. `VoxelMemory` pools: fixed-size block pools in 1 MB slabs; each thread keeps up to 64 free blocks a pool, takes/gives 32 at a time under the pool's lock. Default grid bounds (±500 m, floor at first chunk at or below -40.96 m, top above 81.92 m rounded to 8; at 10 cm ±512, -51.2, +102.4). Default streaming radius (59.2 m on ≤10 cm voxels; 4.625 level-0 columns coarser; 592 m at 1 m). `LevelDistances` from `Level0ReachMetres` and `ReachGrowth`. `VoxelChangeLog` entry kinds incl. chunk changed with a mask of its 64 blocks of 32 voxels. A `VoxelPiece` is one chunk tree of at most 128 voxels a side.
- physics: loose bodies 2000 kg per cubic metre of blocking voxels; moves split into parts of at most 24 voxels; slope limit 45 degrees by default (`MovementStats`); stand-ins: a character within 3 m of a body, velocity change at most 10 m/s^2 towards the wanted velocity.
- rendering: the near cascade's blocker search takes 8 taps; `UpscalePass`'s upscaled image is 10 bits a colour; `DynamicRenderScale` sets `RenderView.RenderScale` in whole hundredths, aiming at 95 %/90 %; the menu's thresholds 30, 60, 75, 90, 100, 120, 144, 165, 180, 240, 360.
- particles: each particle in the pool is 64 bytes.
- audio: a stream recipe's generator runs 512 samples at a time with four blocks queued (about 43 ms at 48 kHz); `Sustain` plays a stream for 0.1 s after the last; at most 128 loops wait.
- ui: `Engine:Console:Run` runs commands once the first match's world and systems exist.
