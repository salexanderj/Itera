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
- A render pass of its own (`Rendering/AimHighlightPass.cs`, an `IRenderPass` at the `Transparent` stage): the block
  the player aims at within 1.5 m (`AimHighlightSystem`, the shared ray from where the player's tools aim, through
  `YardColliders` to the piece) glows along its edges, pulsing. Its shaders are in `Shaders/`, copied to
  `Shaders/Game/` beside the engine's, and include the engine's `view.glsl`; `AimHighlightPassTests` checks them against
  `AimHighlightData`.
- An image view on a screen (`YardCamera`, `YardCameraSystem`): a camera high over the yard's south-west corner, looking
  at its middle, is drawn into a 384 by 216 image every other frame (`RenderViews`), shown on a screen 4 m up on a post
  past the blocks, ahead of the start (the yard's `yard-screen`, its `screen` material: `RenderViews.ShowOnMaterial`),
  which glows with it; the camera sees the screen too, showing its own last picture there.
- The sky is the engine's default (no `IAtmosphereSource`).
- Its play area is the yard (`PlayArea` with the yard's bounds, in place of the engine's 10 cm grid's): the mover's map
  edge is a wall at the yard's side, so nobody walks off it.

Run it: `dotnet run --project samples/Itera.MeshSample -- --Engine:Session:Mode=Offline`. Its data folder is
`IteraMeshSample`; runs an agent starts add `--Engine:Application:DataFolder=IteraMeshSample-agent`. Vulkan validation:
`.claude/skills/vulkan/validate.sh --binary samples/Itera.MeshSample/bin/Debug/net10.0/Itera.MeshSample`. Its tests,
played without a window, are in `tests/Itera.MeshSample.Tests`.
