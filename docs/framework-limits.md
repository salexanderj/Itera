# What kinds of game the framework blocks

Written 2026-10-09, from four read-only reviews of the code (rendering, the world and physics, sessions and saves, and
the game's structure, UI, sound, and input), whose main claims were checked against the code. The question: which
offline or peer-to-peer 3D games can a clone make without the engine getting in the way? `framework-plan.md` set the goal
of any kind of 3D game, with voxels supported well but not required.

## Where it fits today

Games whose world is voxel terrain in a map about 1 km across, seen through one perspective camera, offline or with up to
16 players: voxel survival and building, co-op shooters, tower defence, physics sandboxes, puzzles and platformers in
voxel rooms, and turn-based tactics. Their own code has the hooks it needs: its own systems and components, Bepu bodies
(`PhysicsWorld.Simulation` is public), UI layers, game messages with the playback helpers, levels as matches
(`MatchControl.RequestStart`), and save files of its own.

## Limits that need engine changes

Ordered by how many game types each blocks.

1. **Everything drawn is voxels.** `MeshVertex` has no texture coordinates; models are `VoxelModels` (at most 128 voxels a
   side, three levels, flat colours); no glTF or OBJ loader; no skinning (the `Animation/` poses move rigid voxel parts,
   one model per bone). Blocks most games with authored art: characters, vehicles, imported assets. Now models read from
   glTF files are drawn as textured triangle meshes with the world's light, skinned and posed by their clips, their
   textures compressed offline for shipping (`.claude/rules/mesh-assets.md`); no transparency yet (limit 3).
2. **A voxel world is required, in a fixed box.** When this was written, `MatchServices` took `VoxelWorld`,
   `PhysicsWorld`, and the voxel colliders, and every match needed an `IChunkGenerator`. Now a match runs without a voxel
   world, its characters and loose bodies over the game's mesh colliders; the box remains. The box is set by constants (`VoxelGrid`: ±512 m across, -51 to +102 m at 10 cm voxels); the view
   reaches 2 km at most; nothing shifts the origin. Blocks open worlds, flight, space, and long race tracks.
3. **Rendering is closed to a game.** The pass list is fixed in `FrameRenderer.Record`, and `IFrameRenderer` and
   `PipelineFactory` are internal (now a game adds its own passes at five stages, `IRenderPass`, and `PipelineFactory`
   is public, 2026-10-10); no transparent pass (only particles blended; now mesh assets' blended materials do too, sorted per object,
   2026-10-10); the look was constants in the shaders
   (now data a game sets, `RenderLook`, 2026-10-10); the sun's direction was fixed (now a game sets it, with night, 2026-10-10); no spot lights or shadowed point
   lights; one perspective camera (`mesh.vert` always divides by depth; `RenderView` holds one camera; now a camera's
   lens may be orthographic, `CameraLens`, and a game draws views into images, `RenderViews`, the starter's minimap,
   2026-10-10), so no split screen.
4. **Sessions are one host with clients.** At most 16 slots (`SessionProtocol.MaxSlots`); clients reach each other only
   through the host; the match ends when the host quits (no host migration); Steam lobbies are friends-only; nothing
   helps lockstep or rollback (Bepu is not bit-identical across CPUs, and peers' step clocks run apart).
5. **Physics settings are fixed.** One gravity vector, one contact material (`NarrowPhaseCallbacks`), no collision groups
   or triggers, fixed solver settings; `VoxelColliders` freezes any dynamic body, a game's too, where no level-0 column
   is loaded.
6. **Input, sound, the menu, and saves.** Keyboard and mouse only (gamepads are counted, not read; all devices feed one
   player); a game gets no typed text; sound is procedural and mono (no file loading, no music path); the menu is sealed
   and Escape always opens it; a save holds the edit log and 4 KB of the game's match data.
7. **Platforms.** Only Linux has run; Windows and macOS are untested, and macOS lacks a Vulkan loader. Vulkan 1.3 and BC
   textures are required.

Possible from game code, with work: a UI toolkit (the engine gives drawing layers), dialogue and cutscenes, replication
of players and bodies, and saving the game's own state.

## By game type

| Fits now | Workable with effort | Blocked by the engine |
|---|---|---|
| Voxel survival or building, co-op shooter, tower defence, physics sandbox, voxel puzzle or platformer, turn-based tactics | Strategy and isometric games (an orthographic lens since 2026-10-10), story games (sound and UI workarounds), small lockstep games with their own simulation, games built on imported models | Racing, flight, space, open-world RPG, split screen, fighting games, more than 16 players |

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
3. A map size the game sets, with origin shifting.
4. Gamepads, typed text, sound files and music, a menu a game can replace.
5. Physics materials, collision groups, and gravity set by the game.
