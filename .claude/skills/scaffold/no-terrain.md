# Adding to Itera: a game without voxel terrain

The `scaffold` skill's recipes for a game without voxel terrain. Each lists every file the addition touches, in order, and the test that fails when one is missed; finish with the skill's "Then".

## A game without voxel terrain

`samples/Itera.MeshSample` is one, to copy from. Its composition is `AddIteraEngine` and its own (`AddMeshSample`):

1. No `IChunkGenerator` and no `AddIteraVoxels()`: matches have no voxel world (`MatchServices.Terrain` null).
2. Its world as mesh assets: models from glTF (above) or built in code (`MeshAssets.Add` with a `MeshAsset`; textures
   by `MeshTextureData.FromRgba`), added at its `IRunStartup`; drawn by entities with `MeshPlacement` and `DrawnMesh`,
   and registered as colliders (`MeshColliders.Add(asset, transform, tagsOf)`) from a system's `Initialize`, each
   material's surface tags given by `tagsOf`.
3. The engine's systems that need no world, with `null` where a `VoxelWorld` is optional (`MapResetSystem`,
   `DebugBallSystem`, `PlayerCameraSystem`, `MeshDrawSystem`); no `StreamingCentreSystem` or benchmark. The character
   mover reads the colliders in cells of `Engine:Physics:CollisionCentimetres`; the play area is
   `PlayArea.WithoutTerrain` (±512 m) unless the game registers its own with its world's bounds
   (`services.Replace(...)`, as the sample does), whose edge is the mover's wall.
4. Optional: `ClimbingMode` as its `IMovementMode` with the tags it climbs (and a `Climber` on its players), an
   `ILooseBodyRest` for what a rested ball becomes, an `IAtmosphereSource` for its sky and look.
5. Check it: tests that play it through `HeadlessMatch` (`tests/Itera.MeshSample.Tests`), and
   `validate.sh --binary PATH` (the vulkan skill) for its drawing.
