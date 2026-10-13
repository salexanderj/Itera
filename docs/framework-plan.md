# Itera as a framework: plan

Started 2026-10-09. Status: done on 2026-10-09 (phases 0 to 8). Zorf TD's history is the private repository
`salexanderj/ZorfTD-archive`, whose `main` ends at the tag `zorftd-shelved`; the framework is `salexanderj/Itera`, from
one new commit.

## Goal

Itera becomes a framework that is cloned to start a game. The repository holds the engine (`src/Itera.Modding`,
`src/Itera.Engine`), a small starter game that runs it, the tools, and the Claude Code setup. A game is made by cloning
the repository and growing the starter game; later engine changes come from Itera with a pull from its remote. Zorf
TD is shelved: its code, docs, and assets leave the framework and are kept in an archive (Q1).

Lucy's requirements (2026-10-09):

- Any kind of 3D game. Voxels are not required by every game, but the engine supports them well, at least for
  volumetric world generation.
- Clone and build on top (this replaces the earlier answer of game repositories with the engine as a submodule).
- Remove Zorf TD-specific code from the engine, move general implementations from the game into the engine, and clean
  up every reference to Zorf TD and its docs.

## What the survey found (2026-10-09)

Six read-only reviews covered the game, the engine, the tests, and everything outside `src/` and `tests/`.

**The engine** has no game types. What ties it to Zorf TD:

- About 70 comments name game things (Stan, Titans, creeps, rifles, the fortress) in 41 files, and 58 notes of
  "Lucy's choice" with dates sit in 37 files.
- About 10 constants are tuned for the game:
  - the mover's 2 m widest body (a Titan)
  - the edit warm-up's tool shapes
  - the sky field's 160 x 80 x 160 m box
  - the navigation grid's 50 cm cell
  - the model levels' cap of 3
  - the ragdoll tuning
  - `Steam.AppId` = 1938520 (Zorf TD's), which the options check requires to be non-zero
- The shaders hold the art direction as constants: 13 values in `mesh.frag` and 6 in `lighting.glsl`. Comments there
  name the players' and enemy's bases.
- Features only the game uses:
  - ragdolls and cut bodies (about 750 lines)
  - thrown balls (about 400)
  - solid models, made for Stan's head (about 420)
  - the radial menu (about 550)
  - the material recipe library and baking (about 1,480, shaders included)
  - The tint map is already general (a two-colour blend over x and z).
- The engine has no world generator of its own, and every match needs one.
- These engine behaviours are tested only in `tests/ZorfTD.Tests`:
  - `MatchFlow`
  - `MatchLifetime` (ended matches leave nothing behind)
  - the composition and system order
  - `UnknownOptionKeys`
  - `VoxelTintSampler`

**Game code worth moving into the engine:**

- **Volumetric world generation:**
  - Moves almost unchanged:
    - deterministic maths and noise (`WorldMath`, 266 lines)
    - row fills (`VoxelRows`, 95)
    - shapes with exact row spans: `Capsule` 142, `Ellipsoid` 54, `Prism` 302, `RockPart` 139
    - `CameraPoseText` (45)
  - Needs separating from the map first:
    - the heightfield column cache in `WorldGenerator` (about 450 general lines)
    - fractal noise (from `Ground.Hills`)
    - a cached scalar field (from `CorruptionGrid`)
    - a tint map from a field (from `WorldTintMap`)
    - the jittered-grid placement used five times
    - a polyline index (from `TrailIndex`)
  - Tests: `WorldMathTests` (183), `ShapeSpanTests` (323), and the general parts of `CoarseLevelTests`.
- **Player and camera:**
  - camera, physics, and trimmed player components
  - `CameraRules`, `PlayArea` (from the match's grid), `CameraExtractSystem`, `DebugFlyCameraSystem`
  - the cores of `ThirdPersonCamera`, `AtmosphereSystem` (with a source interface), `PlayerInputSystem`,
    `PlayerCameraSystem`, and `PlayerMovementSystem` (`Move`, `tp`, `pos`)
  - the movement keys of `GameKeys`
  - the `ToolAim` ray
- **Physics glue:** `PhysicsReadSystem`, `StreamingCentreSystem`, `LooseBodyRoom`, `LooseBodySystem`, `StandInSystem`,
  `DebugOutlineSystem`, `MapResetSystem` with `WorldResets`.
- **The benchmark:**
  - Moves: the `BenchmarkSystem` machine, `IBenchmarkHost`, `BenchmarkView`, `EngineBenchmarkHost`, and the options
    (as `Engine:Benchmark`, without the crowd).
  - The log lines stay as they are, since `tools/benchmark/parse.py` reads them.
- **Networking helpers:** `TickRing` and `JitterPlayback` (105), `GameMessageHandler` (19), `NetEntities` (122).
- **Small pieces:**
  - `Amounts` (130)
  - the stock particle effects (about 350 lines of `GameEffects`: sparks, smoke, dust, debris, and others)
  - a centred message layer (from `RespawnLayer` and `LossLayer`)
  - a models loading task
  - the sound helpers `Hits.Contacts`, `Footfall`, and the sound material table
- **The character kit (Q3):**
  - pose maths: `Rotation`, `Skeleton`, `BodyPose`, `AnimationClip`, `Aim`, `LimbPose` (about 890)
  - foot IK and ground placement: `FootPlacement`, `SegmentPlacement`, `SupportPoints`, and the core of `StanceGround`
    (about 900)
  - voxel bodies from shapes: `BodyShape`, `BodyBox`, the core of `BodyPart`, the volume fill (about 900)
  - cutting a jointed model into pieces (about 450, plus a 70-line bridge to the engine's ragdolls)
  - hit queries against posed parts (about 270)
  - the pose store, placement, and far flipbooks

Everything else is Zorf TD's and is deleted: about 60,000 lines of `src/ZorfTD` and about 45,000 of
`tests/ZorfTD.Tests`.

**History.** 263 MB of the repository's history is Zorf TD's texture sets (255 versions of DDS files, stored
loose). The rest of the history packs into about 4 MB. Every clone downloads all of it.

**Untracked local files:**

- `publish/` (654 MB)
- `other_player_1.log` and `other_player_2.log` (9 MB)
- `docs/audits/` (748 KB)
- `.claude/settings.local.json`, which names publish paths, `tools/steam`, and `/home/lucy/dev/ZorfTD`

## Layout after the change

```
CLAUDE.md                  framework instructions, kept up to date from Itera; imports GAME.md
GAME.md                    the game's instructions: each clone's own (the starter's to begin with)
ARCHITECTURE.md            the engine
src/Itera.Modding, src/Itera.Engine
src/<starter> (Q2)         the starter game: Program.cs, composition, appsettings.json, a generator, a palette,
                           start pose, benchmark views
tests/Itera.*.Tests, tests/<starter>.Tests
samples/Itera.SampleMod
tools/control, tools/benchmark, tools/Textures, tools/steam (generic)
.claude/rules/<area>.md    the framework's areas;  .claude/rules/game-*.md  the game's
docs/                      engine write-ups
```

Rule for clones: change engine files only to fix or extend the engine, and send those changes back to Itera. Game
code stays in the game's folders, so a pull from Itera merges cleanly.

To check before relying on it: the `@path` import from `CLAUDE.md` into `GAME.md` in this Claude Code version.

## Phases

The code moves before the game is deleted. The game's tests are the only check that moved code still behaves, so
each move is built and tested against the game while it still exists.

0. **Archive Zorf TD** (after Q1). Tag `zorftd-shelved` at Zorf TD's last commit and push it after Lucy's go-ahead. If
   the history changes, push the full repository to the archive first.
1. **Move general code into the engine** while the game still uses it. About 2 to 4 days; the full character kit adds
   2 to 3. Each step is built, tested in full, and committed. The order:
   1. `WorldMath` and the shapes
   2. the generation toolkit
   3. the components and the camera, movement, and physics systems
   4. the benchmark
   5. the networking helpers
   6. the small pieces
   7. the character kit (Q3)
2. **Starter game.** About 1 to 2 days.
   - The game itself:
     - its composition
     - a world generator built on the toolkit (hills, caves from 3D noise, a few shapes)
     - a small palette
     - flying and walking
     - the console and the control port
     - benchmark views
     - `appsettings.json`
   - The engine-behaviour tests from `ZorfTD.Tests`, rewritten over the starter: `MatchFlow`, `MatchLifetime`,
     composition and order, `UnknownOptionKeys`, shipped settings, and the tint sampler.
3. **Point the tools at the starter:** the tools, the `run-game` skill, `validate.sh`, the skill guard hook, and the
   agents. About half a day.
4. **Delete Zorf TD.** About half a day.
   - Delete `src/ZorfTD`, `tests/ZorfTD.Tests`, `tools/PoseSheet`, the game's rules files, and the game's docs.
   - Fix the five places that name the game:
     - `Itera.sln`
     - `ArchStartup.cs:34`
     - `InternalsVisibleTo` in `Itera.Engine.csproj`
     - the sample mod's output path
     - the `Game:` keys in tests
5. **Engine cleanup.** About 1 day.
   - Reword the comments.
   - Turn the tuned constants into options.
   - Default `Steam.AppId` to 0 and skip Steam start-up at 0.
   - Make the warm-up shapes general.
   - Apply the features decision (Q3).
   - Move the reasons in the "Lucy's choice" notes to docs.
   - Reword the shader comments. The art constants stay as engine defaults for now.
6. **Docs and setup.** About 1 day.
   - `CLAUDE.md` for the framework and `GAME.md` for the starter.
   - `ARCHITECTURE.md` rewritten for the engine, from about 3,700 lines to about a third of that.
   - The docs kept, trimmed, or removed as the survey listed.
   - The rules, skills, and agents.
   - My memory: Zorf TD's memories go to an archive folder and leave the index.
   - A "Starting a game" section and a skill that renames the starter for a new game.
7. **Checks.** About half a day.
   - Build, all tests, and csharpier.
   - `validate.sh` on the starter.
   - A look at the world.
   - A benchmark baseline on the starter's views.
8. **History** (if Q1 chose to change it). Rewrite or start fresh, and set up the remotes.

In all, about 6 to 10 days, plus 2 to 3 with the full character kit.

## Decisions

### Answered 2026-10-09

- Q1. History: **fresh history.**
  - The framework starts from one new commit in a new GitHub repository (phase 8).
  - This repository, with its full history, stays on GitHub as the Zorf TD archive (renamed `salexanderj/ZorfTD-archive`
    in phase 8).
  - Docs in the framework cite no commit hashes, since none will exist there; they name the archive instead.
- Q2. The starter's folder: **fixed `src/Game`**.
  - Every clone keeps `src/Game`, `Game.csproj`, and `tests/Game.Tests`, so tools need no setting.
  - The display name, window title, and data folder come from `appsettings.json`.
- Q3. Character kit: **pose and IK only**, about 1,800 lines:
  - skeleton, poses, clips, aiming, deterministic rotation, foot IK, and resting bodies on voxel ground.
  - The engine's ragdoll and cut-body physics stays, tested but with no caller.
- Q4. Texture sets in the starter: **a few** (3 or 4, about 10 to 14 MB).

### Done

- 2026-10-09: tag `zorftd-shelved` on Zorf TD's last commit (pushed with the archive in phase 8).
- 2026-10-09, phase 1:
  - `DeterministicMath`, `Rotation`, and `RigidTransform` in `Itera.Engine.Maths`.
  - The engine's components in `src/Itera.Engine/Components/`:
    - `Bodiless` replaces the game's dead-player tag.
    - `PlayerCommand.Actions` carries a game's own buttons.
    - Crouching lives in `PlayerMode`.
  - `CameraRules`, `PlayArea` (from a match's grid), `MovementKeys`, and `PlayerInputBlock` in
    `src/Itera.Engine/Systems/`.
  - From the networking and small pieces: `TickRing`, `JitterPlayback`, `GameMessageHandler`, `NetEntities` (a byte
    kind), `NetEntity`, `Amounts`, `CentredMessageLayer`, `StockEffects` (seven recipes), and `ModelsLoadingTask`.
  - Where the engine's copies share a name with the game's types in namespaces the game imports, global aliases in
    `src/ZorfTD/EngineCopies.cs` and `tests/ZorfTD.Tests/EngineCopies.cs` keep the game's names its own until phase
    4.
- 2026-10-09, phase 1 (continued) and phase 2:
  - The world generation toolkit and `HeightfieldGenerator` (`Voxels/Generation/`), checked against itself (every
    description against its fills, a voxel-by-voxel reference) rather than by rebuilding the game's generator on it.
  - The pose and IK kit (`Animation/`), the player, camera, physics, and streaming systems (`Systems/`; the crouch's
    eye eases at the game's 2 m/s), the benchmark machine (`Engine:Benchmark`), and `DebugBallSystem` (`balls`, new:
    the starter has no other way to make models for validation).
  - `Engine:Steam:AppId` defaults to 0, which leaves Steam off.
  - The engine-behaviour tests moved to the engine's tests over `EngineComposition` (`MatchFlow`, `MatchLifetime`,
    composition order, unknown option keys).
  - The starter (`src/Game`, `tests/Game.Tests`): gentle hills of grass with gravel low down and stone boulders on
    `HeightfieldGenerator`, the texture sets `grass`, `dirt`, `stone`, and `gravel` (13.6 MB), the player made at the
    start, a clear-day sky, start pose, benchmark views, `Game:Player` and `Game:World` options, the engine's stock
    particle effects; named "Itera Game", data folder `IteraGame`.
- 2026-10-09, phase 3: `tools/control/zorf.py` became `game.py`; the benchmark, texture, and Steam tools, `validate.sh`,
  and the skill guard hook point at `src/Game` and read its names from its settings (`tools/benchmark/data_folder.py`;
  Steam depots and account from `src/Game/steam.env`).
- 2026-10-09, phase 4: `src/ZorfTD`, `tests/ZorfTD.Tests`, and `tools/PoseSheet` removed (670 files, about 140,000
  lines).
- 2026-10-09, phase 5: the engine's and its tests' wording names no game (comments, shader comments, test names, and
  sample data; no behaviour changed); the "Lucy's choice" attributions and audit tags left the code.
- 2026-10-09, phase 6: `CLAUDE.md` (the framework's, importing `GAME.md`), `GAME.md`, the rules files (engine areas;
  new `systems.md` and `animation.md`), the skills, the agents, `ARCHITECTURE.md` (1,288 lines), and `docs/`; my
  memory's Zorf TD files moved to an archive folder.
- 2026-10-09, phase 7: the whole suite passes (1,848 tests); both standard validation runs pass on the starter (models,
  debug lines, the upscale, particles, a screenshot, and a shader reload). The benchmark's first baseline on the
  starter's views (`tools/benchmark/run.sh starter-2`, Release, RX 5700 XT at 2560x1440, seed 1, the world pass; the
  flight at `Engine:Camera:CircleHeightMetres` 20 m, which a review raised from 12 m above the starter's hills):

  | start | start-low | down | up | high | far | flight |
  |---|---|---|---|---|---|---|
  | 1.75 ms | 1.86 ms | 1.85 ms | 1.05 ms | 2.40 ms | 1.96 ms | 2.67 ms |

  The views matched the first run (`starter-1`, flight at 12 m: 2.39 ms) within 0.2 %.

  The mesh heap held about 130 MB; the views drew 0.47 to 0.94 million triangles (0.9 to 2.4 million for the shadows).
  Runs on Zorf TD's views do not compare with these.
- 2026-10-09, phase 8 (Lucy's answers the same day):
  - The GitHub repository `salexanderj/Itera` was renamed `salexanderj/ZorfTD-archive` (private). Its `main` ends at
    Zorf TD's last commit, the tag `zorftd-shelved`; the branch `raymarcher` and the tag `raymarcher-final` are there
    too. The commits that turned the repository into the framework were not pushed there.
  - A new private repository `salexanderj/Itera` holds the framework, from one new commit with this tree.
  - The untracked build output (`publish/`), two player logs, and `docs/audits/` were deleted, and
    `.claude/settings.local.json` no longer names Zorf TD's paths.

### Left in the archive, general but not moved

Player and body replication between peers (since returned as the engine's replication, `docs/replication.md`), the crowd's push-apart, the flow-field follower (`CreepLanes`), the debug dig
and place tools, bodies from shapes, cutting a model into pieces, hit queries against posed parts, far flipbooks, the
tree kit, the polyline index, and the sound helpers. Each can be taken from the archive's tag `zorftd-shelved` when a
game needs it.

### How the moves are done

- Code that moved unchanged was copied into the engine with its tests (renamed, public, its docs reworded); the game
  kept its own copies until phase 4 deleted it, so no game code was adapted. The copied tests, with their pinned
  hashes unchanged, check that the copies give the same bits.
- Code that needed cutting went into the engine with its cuts and new or ported tests. Examples: `Skeleton` without
  health, and the core of `PlayerMovementSystem`.

### Defaults, to confirm at the phase that touches them

- Engine features only Zorf TD used (thrown balls, solid models, the radial menu, the recipe library, the tint map)
  stay as general features with reworded docs.
- The shader art constants stay as engine defaults; making them per-game data comes later.
- The reasons in the "Lucy's choice" notes move to docs; names and dates leave the code.
- `tools/steam` becomes a generic upload script that reads the App ID and depots from the game's settings.
- Pushing tags and deleting untracked files are asked when reached.
