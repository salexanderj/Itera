# What kinds of game the framework blocks

Written 2026-10-09, from four read-only reviews of the code (rendering, the world and physics, sessions and saves, and
the game's structure, UI, sound, and input), whose main claims were checked against the code. The question: which
offline or peer-to-peer 3D games can a clone make without the engine getting in the way? `framework-plan.md` set the goal
of any kind of 3D game, with voxels supported well but not required.

## Where it fits today

Games whose world is voxel terrain in a map about 1 km across, seen through one camera (perspective or orthographic,
with views drawn into images beside it), offline or with up to 16 players: voxel survival and building, co-op shooters,
tower defence, physics sandboxes, puzzles and platformers in voxel rooms, and turn-based tactics. Their own code has the
hooks it needs: its own systems and components, Bepu bodies (`PhysicsWorld.Simulation` is public), UI layers, game
messages with the playback helpers, levels as matches (`MatchControl.RequestStart`), and save files of its own.

## Limits that need engine changes

Ordered by how many game types each blocks.

1. **Everything drawn is voxels.** `MeshVertex` has no texture coordinates; models are `VoxelModels` (at most 128 voxels
   a side, three levels, flat colours); no glTF or OBJ loader; no skinning (the `Animation/` poses move rigid voxel
   parts, one model per bone). Blocks most games with authored art: characters, vehicles, imported assets. Now models
   read from glTF files are drawn as textured triangle meshes with the world's light, skinned and posed by their clips,
   their textures compressed offline for shipping (`.claude/rules/mesh-assets.md`), and blended materials drawn sorted
   per object (limit 3).
2. **A voxel world is required, in a fixed box.** When this was written, `MatchServices` took `VoxelWorld`,
   `PhysicsWorld`, and the voxel colliders, and every match needed an `IChunkGenerator`. Now a match runs without a
   voxel world, its characters and loose bodies over the game's mesh colliders; the box remains, now sized by the game
   in its generator's grid (`VoxelGrid.Sized`: up to ±160 km across, up and down too, by default ±512 m and -51
   to +102 m at 10 cm voxels, 2026-10-10), its columns storing only the layers round their ground; the view reaches
   8 km; navigation's grid follows the players; positions are measured from a match's origin
   (`WorldOrigin`, with absolute `WorldPoint`s where they cross it), which follows the camera (`OriginShiftSystem`,
   2026-10-10).
   Blocks open worlds, flight, space, and long race tracks.
3. **Rendering is open to a game, except for split screen, HDR, and some lights.** When this was written the pass list
   was fixed in `FrameRenderer.Record`, `IFrameRenderer` and `PipelineFactory` were internal, only particles blended,
   the look was constants in the shaders, the sun's direction was fixed, and there was one perspective camera. Since
   2026-10-10 a game adds its own passes at five stages (`IRenderPass`; `PipelineFactory` is public), mesh assets'
   blended materials are drawn sorted per object, the look is data a game sets (`RenderLook`), a game moves the sun,
   with night, a camera's lens may be orthographic (`CameraLens`), and a game draws views into images (`RenderViews`)
   that the HUD and mesh materials show (the starter's minimap, the mesh sample's screen; the choices and their reasons
   are in `docs/rendering-extension.md`). Still missing: split screen (one camera drawn to the screen, and input for one
   player), HDR rendering, spot lights, and shadowed point lights.
4. **Sessions are one host with clients.** At most 16 slots (`SessionProtocol.MaxSlots`); clients reach each other only
   through the host; the match ends when the host quits (no host migration); Steam lobbies are friends-only; nothing
   helps lockstep or rollback (Bepu is not bit-identical across CPUs, and peers' step clocks run apart).
5. **Physics is set by the game, within limits.** When this was written there was one gravity vector, one contact material
   (`NarrowPhaseCallbacks`), no collision groups or triggers, and fixed solver settings. Since 2026-10-11 a game sets
   the match's gravity and each body's share of it, the solver's substeps, iterations, and damping, physics materials
   (friction and an exact bounce) on bodies, mesh colliders, and voxel materials, collision layers and groups, trigger
   zones for bodies and characters, and impact events (`docs/physics-materials-and-groups.md`). Still missing: down is
   -Y for characters, the mover, and the terrain; the mover reads every mesh collider whatever its layer; materials per
   mesh triangle; impacts while a pair already touches; gravity is each peer's own. `VoxelColliders` freezes a dynamic
   body where no column of its level is loaded, unless the game marks it to keep moving.
6. **Input, sound, the menu, and saves.** Keyboard and mouse only (gamepads are counted, not read; all devices feed one
   player); sound was procedural and mono (no file loading, no music path) until 2026-10-11, when sounds and music
   from WAV and Ogg Vorbis files came (`docs/sound-files.md`); a save holds the edit log and 4 KB of the game's match
   data. When this was written a game got no typed text, the menu was sealed, and Escape always opened
   it; since 2026-10-11 a game's text box takes the typing, its screens take Escape first, it swaps the menu for its
   own or adds pages and Settings rows to the engine's, and a UI toolkit lays out, drives, and draws its screens
   (`docs/ui-toolkit.md`).
7. **Platforms.** Only Linux has run; Windows and macOS are untested, and macOS lacks a Vulkan loader. Vulkan 1.3 and BC
   textures are required.

Possible from game code, with work: dialogue and cutscenes (on the UI toolkit since 2026-10-11), and saving the game's
own state. Replicating entities from the host is the engine's since 2026-10-12 (`docs/replication.md`); predicting a
client's own player is not yet.

## By game type

| Fits now | Workable with effort | Blocked by the engine |
|---|---|---|
| Voxel survival or building, co-op shooter, tower defence, physics sandbox, voxel puzzle or platformer, turn-based tactics | Strategy and isometric games (an orthographic lens since 2026-10-10), story games (sound workarounds), small lockstep games with their own simulation, games built on imported models | Racing, flight, space, open-world RPG, split screen, fighting games, more than 16 players |

## Order of work

1. A general mesh path: triangle meshes with textures, glTF import, skinning, and voxels made optional (started
   2026-10-09). Done: glTF models read at the run's start and drawn as textured meshes with the world's light and
   shadows, skinned models posed by their clips on the GPU (`.claude/rules/rendering.md`, "Mesh assets"); matches,
   sessions, and saves without voxel terrain; mesh colliders that rays, loose bodies, and the character mover meet;
   voxel models and loose bodies without terrain, drawn there too; clip cross-fades; movement building blocks (wall
   normals, probes, a movement mode hook) and climbing (`ClimbingMode`); a sample game without voxel terrain
   (`samples/Itera.MeshSample`); models' textures compressed offline (`tools/Textures`' model mode). Finished
   2026-10-10. Left for later: moving colliders for the mover, IK on mesh rigs, walking on walls, loading models during
   play, morph targets (ARCHITECTURE.md, "Open work").
2. Rendering a game can extend: its own passes, transparency, look data, a moving sun, other cameras and views.
   Done: the look as data a game sets per frame (`RenderLook`); the sun's direction a game's, with night; mesh assets'
   blended materials, sorted per object; a game's own passes at five stages of the frame (`IRenderPass`); orthographic
   lenses; image views, shown by the HUD and by mesh materials (`RenderViews`; the starter's minimap, the mesh sample's
   screen). Finished 2026-10-10. Left for later: split screen (with input per player, limit 6), HDR rendering,
   order-independent transparency, transparent voxel materials, a physically based sky, spot lights, shadowed point
   lights, and image views with shadows and particles (ARCHITECTURE.md, "Open work").
3. A map size the game sets, with origin shifting. Done 2026-10-10 (`docs/map-size-and-origin.md`; the starter's view
   is its whole map, 8 km over nine levels, with lighter haze; shadows past 200 m are an engine option for a low sun,
   `Engine:Renderer:DistantShadowMetres`, off by default).
4. Typed text, a menu a game can replace, and a UI toolkit (Lucy chose on 2026-10-11 to take sound and gamepads apart).
   Done 2026-10-11: a game's text box takes the typing (whole code points), its screens take Escape before the menu,
   the menu is a game's to swap or add to (pages, Settings rows), Steam joins and saves are public services, an offline
   match may wait while the menu shows; the UI toolkit (`docs/ui-toolkit.md`), the engine's menu rebuilt on it under
   one sizing rule with a player's UI scale, and the mesh sample's own menu on it; hooks for a game's tests of its menu
   (`MatchControlStates`). Left for later: nine-slice images, a safe-area margin, rich text, right-to-left text, IME
   composition (GLFW 3.4 has none), per-element redraw areas.
5. Sound files and music: WAV (a reader of the engine's own) and Ogg Vorbis (NVorbis 0.10.5), with a music path.
   Done 2026-10-11 (`docs/sound-files.md`): WAV and Ogg Vorbis read (NVorbis 1.0.0-rc.2, not 0.10.5, whose seeks land
   off), sounds from files as one-shots and as loops round the points their files name, music streamed on a thread of
   its own with equal-power cross-fades and the menu's track an option, Master, Music, and Effects volumes on a Sound
   tab, and the mesh sample's sounds and music made in code. Left for later: EFX filters and reverb, Doppler, playlists,
   a track's own volume, ducking, more than two channels, and Opus, FLAC, or MP3.
6. Physics materials, collision groups, and gravity set by the game. Done 2026-10-11
   (`docs/physics-materials-and-groups.md`): records by handle reset on removal and a game's own bodies and statics; the
   solver's settings as options; the match's gravity and each body's share, characters falling by it and jumping higher
   under less; particles following it; physics materials with friction and an exact bounce pass on bodies, mesh
   colliders, and voxel materials (each voxel cell a static per material); characters gripping by the ground's
   friction; collision layers and groups; trigger zones for bodies and characters; impact events; and the mesh sample's
   physics corner. Left for later: materials per mesh triangle and per lifted piece, a mover filtered by layer, impacts
   while a pair touches, mesh and swept triggers, rays by layer, gravity on the wire, and down other than -Y for
   characters. The plain step costs what it did; every body marked and bouncy, 7 to 11 % more while bodies touch.
7. Voxel objects finer than the terrain (ARCHITECTURE.md's open work; Lucy's plan, 2026-10-11). Done 2026-10-12
   (`docs/voxel-objects.md`): kinds of finer voxels built in code and placed by the generator; drawn as models near the
   camera and as copies at their own size in the world's coarse levels, fading into each other; shaded as the ground;
   met by characters, bodies, rays, and navigation; damaged by edits and cut into falling parts (save format 7,
   protocol 39); the starter's forests and boulders. Left for later: lifts that take objects' voxels into the piece, a
   support check when the ground is dug from under an object, felled parts that stay, objects that move, kinds read
   from files, far forests past a kind's highest copied level, a hand-over level per kind, and the world pass looking
   down over a forest from 40 m (4.72 ms against the 4 ms budget).

Gamepads come later (not a high priority, 2026-10-11); the research for them is in `docs/ui-toolkit.md`.
