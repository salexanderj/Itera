---
paths:
  - "src/Itera.Engine/Meshing/**"
  - "src/Itera.Engine/Rendering/Meshes/{WorldMeshes,WorldMeshes.Draws,WorldMeshes.Models,WorldMeshes.Objects,WorldMeshes.Stitches}.cs"
  - "src/Itera.Engine/Rendering/Meshes/{MeshColumn,MeshWorkers,LevelBalance,MeshFrame,IMeshTargets}.cs"
  - "src/Itera.Engine/Rendering/{IWorldOnGpu,NoGpuWorld,ModelInstances}.cs"
  - "src/Itera.Engine/Rendering/Voxels/StagingWriter.cs"
  - "src/Itera.Engine/Rendering/Memory/GpuRangeHeap.cs"
  - "src/Itera.Engine/Rendering/GpuData/{MeshVertex,MeshDraw,MeshModelDraw}.cs"
  - "src/Itera.Engine/Rendering/Shaders/{mesh.vert,mesh-data.glsl,mesh-fade.glsl}"
  - "tests/Itera.Engine.Tests/Meshing/**"
  - "tests/Itera.Engine.Tests/Rendering/Meshes/{WorldMeshes,WorldMeshesObjects,MeshColumn,LevelBalance}Tests.cs"
  - "tests/Itera.Engine.Tests/Rendering/Memory/GpuRangeHeapTests.cs"
---

## The world's meshes

The renderer draws the resident voxels as triangle meshes. `WorldMeshes` (`Rendering/Meshes/`) makes them, `SurfaceNets`
(`src/Itera.Engine/Meshing/`) meshes one block on a worker thread, and `MeshPass` draws them (`rendering-light.md`).
The voxel models are meshes too. Related files: `rendering.md` (the core), `rendering-light.md` (shadows, the main
pass, lights), `rendering-materials.md` (how a surface is coloured), `objects.md` (voxel objects).

### Blocks

- `WorldMeshes` follows the match's world. In a match without voxel terrain, `BeginMatch` gets a null world and it
  holds the voxel models alone: no columns, no sky field, no stitches.
- A block is a 32-voxel cell of a chunk. A column keeps blocks only in the runs of layers that can hold a surface
  (`MeshColumn`, from `VoxelWorld.CollectSurfaceLayers`).
- An edit that changes a layer of one fill (`VoxelChangeKind.ColumnLayersChanged`) makes the column's runs again.
- The main thread reads a block with a 2-voxel apron (`VoxelWorld.TryReadBlock`, `BlockMayHaveSurface`,
  `VoxelWorld.Blocks.cs`: materials and densities). `MeshWorkers` threads mesh it.
- `WorldMeshes` follows the change list (columns, bricks, and chunks changed). It meshes a column only while the column
  can be drawn (`IsWanted`), nearest the view first, and a column's blocks nearest first (`MeshColumn.DirtyNearestFirst`).
- Blocks that edits mark count towards the edits' latency (`MeshBlock.Edited`). Streaming blocks do not. A Debug line
  says when all edited blocks are meshed and staged.

### The mesher

`SurfaceNets` is naive Surface Nets with one vertex per cell.

- The vertex lies at the mean of the points where the density (`VoxelDensity`) crosses one half along the cell's
  crossing edges.
- The normal comes from a 4x4x4 gradient of the densities.
- The openness comes from the mean density round each corner of the cell, blended to the vertex's place.
- A model's block uses its materials' default densities unless the model keeps densities (an object kind's).
- The material is the one most of the cell's surface corners have. A solid corner beside an air corner counts, so a
  one-voxel skin shows.
- A block owns the edges whose lower voxel is in it, so neighbouring blocks meet exactly.
- Each mesh has a palette (`MeshBuffers.Palette`) of its surface's eight most common materials. Each vertex's
  `Weights` say how many of its cell's surface corners are each, four bits a slot. A material past the eighth counts
  as the vertex's own. The `meshes` line counts the meshes that had more.
- `mesh.vert` passes the weights and the palette (`meshPalette`, the words before the first vertex) for the world's
  blocks. `mesh.frag` draws the one of the two heaviest materials whose share plus its height is greater
  (`rendering-materials.md`). The models keep their vertices' material.

### The heap

- Finished meshes go through the slot's staging (`StagingWriter`) into the heap of `MeshPass`
  (`Engine:Renderer:MeshMegabytes`, lowered to the device's limits; `GpuRangeHeap` in `Rendering/Memory/`).
- A mesh's range holds its palette's two words, then its vertices (`MeshVertex`, 16 bytes), then its 16-bit indices.
  The materials and the tint map sit at fixed places at the heap's start.
- A full heap first frees the meshes of wanted columns that neither the camera's pass nor the far cascade's drew or
  would have drawn in the last frame and that are not being meshed, farthest first (`Evict`: those a coarser drawn
  column covers, then those finer drawn columns cover). Those columns stay wanted and are meshed again when the view
  needs them (`MeshColumn.Evicted`, `NeededUpload`).
- A block that still finds no room keeps the mesh it had and no longer counts as pending. It is read again at most
  once a second after a range has been freed (`RetryStarved`).
- The `meshes` line reports what columns the last frame did not draw hold, what was freed, and the frames that drew
  wanted columns not meshed yet.

### Levels

Each frame `WorldMeshes` picks a level for each column.

- A column's nearest part is that of its box: its square, and its height from its first layer with blocks to its last.
  A camera high over the ground meshes no fine level under it.
- A coarse column splits into its four finer ones when its nearest part is within the finer level's reach times
  `Engine:Renderer:MeshReachShare`. All four must be meshed, and, at the hand-over level of the voxel objects, their
  objects ready (`objects.md`). The column stays split while it is not meshed itself and they are.
- A finer column past the map's edge or past the view's end draws nothing and keeps no coarse column from splitting
  (`WorldMeshes.DrawsNothing`).
- `Engine:Renderer:MeshFinestLevel` is the finest level.
- Neighbouring columns, corners included, stay within one level of each other in the camera's pass (`LevelBalance`).
  It keeps the drawn columns themselves and finds the one over a finest column level by level.
  A column beside one two levels finer splits where its finer columns are meshed. The `meshes` line counts those left.
- The far shadow cascade's pass is not balanced: it draws plain meshes and needs no stitched seams
  (`WorldMeshes.LastCameraColumns` and `LastFarColumns` count each pass's columns).

### Draws

`WorldMeshes` culls blocks to the frustum and writes a `MeshDraw` and an indexed indirect command per block, with
`firstInstance` naming the draw. Each list is written for one view: the camera, a shadow cascade, or an image view.

- Every list drawn in `MeshPass` shares the frame's view (`MeshView`, `rendering-light.md`).
- The camera's commands are sorted nearest first (`WriteCameraNearestFirst`), so the depth test rejects hidden
  leaves before they are shaded. The shadow lists stay unsorted.
- `MeshFrame` holds what `WorldMeshes` wrote into a frame slot: the draw records, each list's commands, and whether
  the cascades are drawn. `IMeshDrawTarget` and `IStagingRegions` (in `IMeshTargets.cs`) are the interfaces tests replace.
- `WorldMeshes.CameraCommands` and `CameraKeys` keep the camera's commands and their sort keys in CPU memory until the
  next upload. A game's pass reads them there, never from the slot's mapped list. `FinestBlockMetres` is the finest
  level's block width, for finding blocks near a point by their keys (`rendering-grass.md`).
- The shadow lists and the sky field are in `rendering-light.md`.
- `WorldMeshes` also prepares the palette, counts each model as uploaded at the next frame, removes retired models,
  and answers `IWorldOnGpu` (`IsUpToDate`, `Uploads`), which the world loading task and the benchmark wait on.

### Stitched seams

`SurfaceNets.MeshTransition` stitches the seams between levels.

- It meshes a block from its own voxels and the block of the coarser level that holds it (block `b >> 1`, at
  `(b & 1) * 16` of its voxels). Its cells that straddle the coarser column's boundary take the coarser level's
  vertices. At its high x and z faces it takes the coarser level's quads across them.
- A stitched mesh has its own heap range (`MeshColumn.Stitches`, `MeshStitch`), made on a column's first need.
- The camera and the near cascade draw it while three things match what it was read with: the sides
  (`LevelBalance.SidesOf`, masked per block by `MeshColumn.BlockSides`), the block's version, and the changes to the
  coarser block (`MeshStitch.CoarseChanges`, raised in `CoarseChanged` with that block's version, apron included).
- Otherwise the plain mesh is drawn and the block asks for a stitch. The next frame reads the nearest asked for after
  the plain jobs, from a pool of stitch jobs.
- Stitched meshes are not pending and do not count towards a column's being meshed. The far cascade draws plain meshes.
- The seams are not exact: a few open edges remain. A change to the mesher or the levels keeps `SurfaceNetsTransitionTests` passing.

### The models

`VoxelModels` (loose bodies, and a game's bodies, parts, and items) are meshes too (`WorldMeshes.Models.cs`).

- Each level of each model is cut into 32-voxel blocks starting one voxel before its box (`ModelBlockShift`: a block
  meshes only the faces whose lower voxel is in it).
- A block is read with a 2-voxel apron of air from the decoded level (`ReadModelBlock`, with densities where the model
  keeps them: `VoxelModels.HasDensities`). A part of an object owns only its own voxels' faces (`VoxelModels.SetOwned`).
- The same workers mesh model blocks before the world's blocks. The mesh heap keeps a range per block.
- A model counts as uploaded once all its blocks are in (`VoxelModels.SetUploaded`). Cuts, loose bodies, and loading
  tasks wait for that.
- A replaced model (a wound) draws its old meshes until the new ones are all in (`VoxelModels.Revision`).
- A model that shares a base model's meshes (`VoxelModels.ShareBlocks`: a damaged object's part shares its kind's
  part) meshes only the blocks it marks, once the base is uploaded. It draws the base's meshes for the rest
  (`ModelBlock.Shared`, never freed with it).
- A removed model's ranges go the next frame. Retired models stay drawn until no world block is pending. A frame
  without graphics (`GameLoop`'s or `HeadlessMatch`'s) removes them after it.
- Each frame `GameLoop` hands `ModelInstances` to `WorldMeshes.Upload`. It keeps the draws (and the retired models)
  whose bounding sphere is in view, or casts into a cascade as the world's blocks do.
- A draw given `ModelInstances.LevelBySize` takes its level by its voxels' size on the drawn image, as the voxel
  objects do (`objects-copies.md`), with no fade between levels.
- The far cascade casts a model's coarser level where its voxels are narrower than the cascade's texels
  (`MeshView.FarTexelMetres`).
- `ModelShadow` on each draw says what the draw does (`Casts`). `None` draws for the viewer alone (first-person arms
  and what they hold). `Only` casts a shadow without being drawn (the local player's own body in first person).
  `Hidden` is neither: `ModelInstances` and `MeshInstances` leave it out.
- Draws are grouped by model level with a counting sort. Each block of a level is one indexed indirect command whose
  instances are that level's draws (`MeshModelDraw`: corner, turn, voxel size, the block's vertex base and place, tint),
  in three lists like the world's.
- A draw's block goes into a list only where the list meets the block's sphere (`WorldMeshes.ListMeets`). A command
  that keeps no instance is left out.
- `mesh.vert` and `mesh-shadow.vert` compiled with `MESH_MODEL` place and turn them. `mesh.frag` takes the draw's
  tint in place of the tint map's. A model draw with a tint below 0 (an object's part) is shaded as the world's
  surfaces.
- The camera's list writes the draws that show every pixel first and the fading ones (a voxel object's) last
  (`MeshModelList.Faded`). `MeshPass` draws the first before the world's blocks and the fading ones after them. The fading draws use `mesh.vert` and `mesh.frag` compiled with `MESH_FADE` too. They draw
  only the pixels whose screen-space dither threshold lies in the draw's share (`MeshModelDraw.Shown`, 0 for all,
  `mesh-fade.glsl`). The wireframe's model draws all take the fade.
- A shader that may discard keeps the depth test from running before it shades, so only the fading draws use one.
- The model draws in the main pass are timed as `Models`, part of `Voxels`.

### Under a moved origin

`WorldMeshes` writes each block's corner local: `(block * blockCm - originCm) / 100` in doubles. It chooses, orders,
and frees columns from the view's absolute place (`_wantedAt` and `_orderedAt` are absolute), so a move of the origin
starts no sort or remeshing. The sky field and the shadow cascades are absolute too (`rendering-light.md`).

### Tests

`SurfaceNetsTests`, `SurfaceNetsTransitionTests`, `MeshVertexTests`, `MeshColumnTests`, `LevelBalanceTests`, and
`GpuRangeHeapTests`. `WorldMeshesTests`, `WorldMeshesLevelsTests`, and `WorldMeshesModelsTests` (with
`WorldMeshesObjectsTests.cs`) share `WorldMeshesTestBase`.
