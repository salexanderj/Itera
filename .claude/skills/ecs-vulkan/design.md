# ECS data on the GPU: design for Itera

How entity data should reach the GPU. Read `ARCHITECTURE.md` sections 4 and 8 and the `ecs` skill's
`design.md` ("Rendering and UI") first. Points marked "open" are design choices that have not been made;
ask the user before making them.

## What reaches the GPU from entities

- Voxel bricks are not entities and do not take this path (the `ecs` skill's `design.md`). Their upload path (`WorldMeshes`)
  uses the frame order and staging design here. It runs on the main thread after the frame group, writes the request
  slot's staging region in address order, and hands the copy count to the render thread in `RenderFrame`, which records
  the copies.
- Drawn entities are players, enemies, towers, projectiles, pickups, and effects. Characters and loose bodies are
  voxel models (`VoxelModels`) drawn as meshes (ARCHITECTURE.md section 8 has the decisions). Each level of each model
  is meshed in 32-voxel blocks and kept in the mesh heap.
- Each frame's per-model draw data (`MeshModelDraw`) goes into a host-visible storage buffer with a region per frame
  slot. There is one indexed indirect command per block of a model level, in three lists: the camera's and the two shadow
  cascades'. Other kinds may still take instanced meshes or sprites. The flow below holds for each of them.
- The model path: frame systems (`LooseBodyModelSystem`, and a game's own) add each model to `ModelInstances`, an
  engine-owned CPU array. After the frame group, `WorldMeshes.Upload` in `GameLoop` culls the models to the view and to
  each cascade on the CPU, groups them by model level, and writes the slot's draws and commands in address order. It
  leaves the counts in `WorldMeshes.Frame`, which goes to the render thread in `RenderFrame`.
- This is option B of choice 1 below for this data (one extra pass over a CPU array). Culling and ordering read a CPU
  array, never mapped memory. There are no stable GPU slots: the draw data is written in full every frame.

## The frame, as intended

1. Fixed steps: simulation systems write components. Entities that move keep last step's transform (a
   component copied at the start of each step), so frames can blend between steps. Not built yet.
2. Frame group: presentation systems compute the transforms to draw, blending with `FrameTime.Alpha`,
   which every frame system receives.
3. Extract step: the one place that reads the world for rendering. For each kind, it writes the instance
   structs and their count, and records the `Entity` at each position.
4. Renderer (render thread): after `FrameExchange.Publish`, `FrameRenderer.Present` records bind and
   draw commands with the counts, submits, and presents. The slot's fence wait and acquire happened before
   the render thread asked for the frame, so steps 2 and 3 run only when a frame is requested.

## Open design choices

1. **Fence order. Decided: A.** The render thread waits on the slot's fence before it asks the
   main thread for a frame, so the extract step writes the slot's mapped region directly (`SKILL.md`,
   "Frame order"). The options were:
   - A: `BeginFrame` waits on the slot's fence, and `GameLoop` calls it before the ECS frame group. The extract step
     then writes straight into the slot's mapped region. The CPU waits for frame N-2 before the frame systems run
     rather than after; with two frames in flight this seldom blocks.
   - B: the extract step writes into a renderer-owned CPU array (allocated once with `NativeMemory`), and the
     render step copies it into the mapped region after the wait. This costs one extra copy per frame
     (10,000 instances of 64 bytes is 640 KB).
   Both suit Itera's entity counts. A has fewer moving parts; B keeps frame sync inside the renderer.
2. **Where the extract step runs.** Either the last frame system (registered last, so it sees the
   presentation systems' output), or a renderer method that receives the `World`. A frame system keeps
   Arch out of `Rendering/`. A renderer method keeps the frame order in one class. Decided for the camera: a frame system
   (`CameraExtractSystem`) writes `RenderView`, and `GameLoop` turns it into `ViewConstants`. Frame systems run after
   it: the loose bodies' models, the atmosphere, and a game's effects and HUD readers (the game's `SystemOrderTests`
   lists them). They read the camera it wrote or write other frame data, and none moves the camera. Voxel uploads are
   not entity data; they run in `GameLoop` after the frame group. Model draw data: a frame system registered after
   `CameraExtractSystem` (`LooseBodyModelSystem`) writes the engine's `ModelInstances`, and `GameLoop` runs
   `WorldMeshes.Upload` after the group.
3. **Stable GPU slots.** Needed only for GPU data that must persist across frames: last frame's transform
   on the GPU (motion vectors), GPU-side simulation such as particles, or per-entity GPU state. Then:
   - a `GpuSlot { int Index; }` component, set when the entity gets its render components;
   - a free list owned by the renderer;
   - release in the deferred-destroy cleanup system before `World.Destroy` (the `Dead` tag and cleanup
     system are an open item in the `ecs` skill; Arch's destroy events never fire);
   - reuse of a released slot only after `FramesInFlight` frames, because frames in flight still read it.
4. **Buffer growth.** Allocate the larger buffer, point new frames at it, and destroy the old one once no
   frame in flight uses it (after that slot's fence, or `FramesInFlight` frames later). Two ways to point a
   shader at the new buffer:
   - a descriptor set per frame slot, updated only after that slot's fence;
   - the buffer's device address in push constants. `bufferDeviceAddress` is true on RADV; MoltenVK
     reports it only on some Macs (macOS 13 and later).
5. **Culling.** Frustum culling on the CPU, inside the gather, is enough for hundreds to thousands of entities. Built
   for models and the world's blocks (`WorldMeshes.Draws.cs`, `WorldMeshes.Models.cs`: each bounding sphere against the
   view's frustum and each shadow cascade's slice). A compute pass that culls
   and writes `instanceCount` into indirect commands is a later option. It needs a buffer barrier from compute shader
   writes (`PipelineStageFlags2.ComputeShaderBit` with `AccessFlags2.ShaderWriteBit`) to indirect reads
   (`PipelineStageFlags2.DrawIndirectBit` with `AccessFlags2.IndirectCommandReadBit`).
6. **Render thread. Decided and built.** The render thread pulls one frame at a time
   (`FrameExchange`): it asks after its fence wait and acquire, and the main thread answers. Because the
   request follows the fence wait, no extra copy or extra buffer is needed, and the hand-off adds no frame
   of latency. The main thread does not simulate frame N+1 while the render thread records frame N; if
   profiling ever calls for that overlap, the extract step would need one more buffer per slot.

## Sizes to expect

- A chunk is at least 16 KB and holds at least 100 entities. A (Position 12 B, Tint 4 B) archetype holds 585
  entities per chunk (probe). A 64-byte instance struct fills about 6 KB per chunk.
- 10,000 entities of 64 bytes are 640 KB per frame, or about 92 MB/s at 144 frames per second. That is far
  below PCIe or resizable-BAR bandwidth. Rewriting all instance data every frame is the default.

## How other engines do it

- Bevy: an extract step copies render data from the main world into a separate render world once per
  frame. With pipelined rendering, frame N renders while frame N+1 simulates. Itera's extract step has the
  same shape without a second world.
- Unity Entities Graphics: instance data stays on the GPU, and only chunks whose change version moved are
  uploaded (DidChange). Arch 2.1.0 has no change versions, so this model would need a dirty field kept
  by hand. It is not worth that at Itera's entity counts.
- Vulkan Guide and vendor guides: record command buffers every frame; keep static data in buffers. Itera
  follows this; see `SKILL.md`, "Command buffers".
