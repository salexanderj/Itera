# Skills: dated records

Dated accounts and measurements taken out of the `ecs`, `vulkan`, and `ecs-vulkan` skills when they were brought to the
docs rules. Each passage is verbatim, with the file it came from. What was still true went back into the skill as
undated rules and reference. The record is written once and only added to.

## ECS skill: reference.md, audit records

From `.claude/skills/ecs/reference.md`, lines 30 to 96 (from "Audit record, 2026-09-22" to the end).

## Audit record, 2026-09-22

Audit of the ECS code at commit `6a57524` of the Zorf TD archive (`salexanderj/ZorfTD-archive`) against the rules in
`SKILL.md`.

### Fixed

- `HeartbeatSystem.Update` passed `World.Query` a lambda that captured a local (`logger`). That
  allocated 88 bytes every fixed tick (about 5 KB/s at 60 Hz). It now runs an `IForEach` struct with
  `InlineQuery`, and `HeartbeatSystemTests.Update_allocates_nothing` measures 0 bytes. The test fails
  with 88,000 bytes over 1000 updates against the old code.
- `Arch.System.SourceGenerator` 2.1.0 was referenced by Itera.Engine with no `[Query]` methods. Its
  output fails this repo's build (see `arch-notes.md`). Removed from `Itera.Engine.csproj` and
  `Directory.Packages.props`.
- Added `EcsChecks` and `CheckedWorldExtensions` (Debug-only precondition checks) with tests in
  `tests/Itera.Engine.Tests/Ecs/`, and the `SystemAllocation` helper (`tests/Shared/`).

### Open

- Systems are disposed twice: by the DI container and by `Group<T>.Dispose` from `EcsWorld.Dispose`.
  Harmless while `BaseSystem.Dispose` is empty; the rule that `Dispose` is idempotent covers it.
- System run order is the registration order in the game's composition (`AddGame`), with a comment on each
  registration saying why it sits there.
- No deferred-destruction tag, cleanup system, or stable network id component exists yet
  (`design.md` marks both as open design choices).
- Frame systems receive the frame delta but not `FixedStepAccumulator.Alpha`, which interpolation
  needs. (Closed 2026-09-22: systems receive `FixedStepTime` and `FrameTime`.)
- `Directory.Packages.props` pins `Arch.LowLevel` 1.1.5, but no project references it and transitive
  pinning is off, so the pin has no effect. Arch 2.1.0 depends on 1.1.5 anyway. (Closed 2026-09-22: pin removed.)

### Confirmed sound

- `EcsWorld` creates the `World` in its constructor. That is required: the system factories in
  a game's composition read `EcsWorld.World` before `EcsWorld.Initialize` runs.
- `EcsWorld.Dispose` is idempotent, disposes both groups, then destroys the world. It runs after
  physics shutdown, and no system touches physics.
- `EcsWorld.FixedUpdate` and `Update` run `BeforeUpdate`, `Update`, and `AfterUpdate` for their group,
  on the main thread.
- `HeartbeatSystem` builds its `QueryDescription` once in a `static readonly` field with one `WithAll`,
  and its component types are first used on the main thread.
- `probe.sh` reported every checked behaviour unchanged for Arch 2.1.0 and Arch.System 1.1.0.

## Update, 2026-09-22 (pre-feature review)

- `IFixedUpdateSystem` is `ISystem<FixedStepTime>` and `IFrameUpdateSystem` is `ISystem<FrameTime>`, so
  fixed systems see the tick number and frame systems see `Alpha`. `HeartbeatSystem` and its tests
  moved to `BaseSystem<World, FixedStepTime>`; `SystemAllocation` is generic over the time type.
- `SystemAllocation` and `ArchCollectionDefinition` live once, in `tests/Shared/`, compiled into both
  test projects. (2026-10-04: `ArchCollectionDefinition` was replaced by `ArchStartup`; test classes that use Arch
  now run in parallel.)
- ECS now initialises after physics, network, and UI, and shuts down before them (one ordered step
  list in `GameLoop`).
- Still open: the deferred-destruction tag and cleanup system, the stable network id component, and
  systems being disposed twice (harmless).

## Since 2026-09-25

The game the engine was built with added its own components and systems (characters, loose bodies, walking, world
generation, tools, cameras) in its own folders; that record is in the Zorf TD archive. In 2026-10 the general ones
moved into the engine (`src/Itera.Engine/Components/`, `Systems/`; `.claude/rules/systems.md`), each with a behaviour
test and a zero-allocation test in `tests/Itera.Engine.Tests/Systems/`.

## Update, 2026-09-22 (render thread)

Drawing moved to a render thread. ECS stays on the main thread. Frame systems run once per frame the
render thread asks for, so they pause while presentation blocks; fixed systems never pause.


## ECS skill: arch-notes.md, dated passages

From `.claude/skills/ecs/arch-notes.md`.

- Line 3: "Checked on 2026-09-22 against the source the NuGet packages were built from, and by running `probe/Program.cs` in Release. Paths are relative to each repository."
- Lines 69 to 70, the end of the component registry note: "in a full run of 2026-10-04 no type was registered after that (183 in the engine's tests, 503 in the game's)."
- Lines 129 to 131: "Recording and playing back an `Add` and a `Remove` of a tag allocated about 1.8 KB each time, after warm-up (measured 2026-09-27 in `WalkerSystemTests`, not a probe). `World.Add`/`World.Remove` on entities collected during the query and changed after it allocated nothing once the move between the two archetypes had happened once."
- Line 156: "Recorded so it is not re-added by mistake. Tested in a scratch copy of the repo on 2026-09-22."
- Lines 169 to 171: "Latest on NuGet on 2026-09-22: Arch 2.1.0, Arch.System 1.1.0, Arch.System.SourceGenerator 2.1.0."

## Vulkan skill: reference.md, audit and review records

From `.claude/skills/vulkan/reference.md`, lines 22 to 253 (from "Audit of the scaffold, 2026-09-22" to the end). The skill keeps the facts that still hold, undated, in its `reference.md`.

## Audit of the scaffold, 2026-09-22

Files: `VulkanContext.cs`, `VulkanSwapchain.cs`, `FrameSync.cs`, `ClearRenderer.cs` (since renamed
`FrameRenderer`, see the next section), `VulkanResultExtensions.cs`. Checked by reading against the spec and the Silk.NET source, and by
running with core + sync + best-practices validation on the RX 5700 XT (RADV, Wayland) with one
swapchain recreate.

Fixed on 2026-09-22 (each verified with `validate.sh`, core + sync + best practices, zero messages):

- `ClearRenderer.Record`: the acquire layout transition used `srcStage = TopOfPipeBit`, so it did not
  wait for the acquire semaphore. Sync validation reported WRITE_AFTER_READ hazards every frame.
  Now `ColorAttachmentOutputBit`.
- `VulkanSwapchain`: composite alpha is chosen from `SupportedCompositeAlpha` (it was always
  `Opaque`); `R8G8B8A8Srgb` is a second sRGB choice before falling back to `formats[0]`; recreate
  passes `OldSwapchain` and destroys the old swapchain afterwards; enumeration results are checked.
- `VulkanContext`: device selection requires device API 1.2 (the fallback enables only
  `VK_KHR_dynamic_rendering`, whose dependencies are core in 1.2); `DebugCallback` catches all
  exceptions; strings are read with `PtrToStringUTF8`; the debug callback pointer is built once;
  `KhrSurface` loads before the surface is created; results of `EnumerateInstanceVersion`,
  `GetPhysicalDeviceSurfaceSupport`, the surface format and present mode counts, and
  `DeviceWaitIdle` in `WaitIdle()` are checked.
- Debug builds enable synchronization and best-practices validation in code through
  `VK_EXT_layer_settings` (options `Engine:Renderer:SyncValidation`, `BestPracticesValidation`).

Still open:

- `renderFinished` semaphores are destroyed after `DeviceWaitIdle` on recreate; see the known gap
  in `synchronization.md`.
- `Vk.GetApi()` runs in the `VulkanContext` constructor, so a missing Vulkan loader fails during DI
  resolution. `Program.cs` resolves `GameLoop` inside its `try`, so this is still logged and exits 1.

Confirmed sound before the fixes: instance and device create info (`SType`, counts, string lifetime
in `finally`), feature chain choice between `PhysicalDeviceVulkan13Features` and
`PhysicalDeviceDynamicRenderingFeatures`, portability enumeration and subset on macOS, debug
messenger attached to instance creation via `PNext`, queue family selection and concurrent sharing,
per-image render-finished semaphores, fence wait/reset order and the out-of-date early return,
zero-extent handling, reverse-order destruction, idempotent `Dispose`, and disposing Silk extension
objects but not `Vk`.

## Pre-feature review, 2026-09-22

The review before feature work changed the Vulkan code as follows. Each change was checked with
`validate.sh` (core + sync + best practices, zero messages), including a swapchain recreate after the
compositor resized the window.

- Vulkan 1.3 is required. `DeviceRequirements.cs` lists the device extensions and features
  (`dynamicRendering`, `synchronization2`, `scalarBlockLayout`); `VulkanContext` checks them with
  `GetPhysicalDeviceFeatures2` during selection, gives each rejected device a reason in its log line,
  and enables them through a `Vulkan12Features` -> `Vulkan13Features` chain. The list now also holds
  `multiDrawIndirect` and `drawIndirectFirstInstance` (core 1.0 features, for `MeshPass`'s indirect draws);
  `fragmentStoresAndAtomics` left with the raymarcher (2026-10-06). The 1.2 +
  `VK_KHR_dynamic_rendering` fallback and `VulkanContext.KhrDynamicRendering` are gone.
- `ClearRenderer` became `FrameRenderer` (the frame: fence, acquire, barriers, submit, present,
  recreate) and `ClearPass` (records the clear; it later became `SkyPass`, now gone). `BeginFrame` runs first in the loop iteration, so the
  fence wait happens before input is read, and frame systems may write the slot's memory.
- `FrameSync` has one transient command pool per frame slot, reset with `vkResetCommandPool` after the
  fence wait. `VulkanContext.CommandPool` is gone.
- Barriers and submits use synchronization2. The acquire barrier's source stage was set back to
  `TopOfPipe` once to confirm `validate.sh` still fails (10 WRITE_AFTER_READ lines), then restored.
- A selected software rasteriser (llvmpipe) logs a Warning. The debug callback checks the log level
  before converting the message.
- `ShaderCompiler` targets Vulkan 1.3 (it defaulted to Vulkan 1.0), reads shader files with `#include`,
  keys the cache on the compiler settings too, writes cache files atomically, and replaces damaged ones.

Still open: the `renderFinished` recreate gap (`synchronization.md`), and `Vk.GetApi()` in the
`VulkanContext` constructor (unchanged, see above).

## GPU memory, 2026-09-23

Roadmap item 2, stage 1: `Rendering/Memory/` (`MemoryTypeSelector`, `BlockSuballocator`,
`GpuMemoryAllocator`, `GpuBuffer`, `StagingBuffer`), a "gpu memory" init step after "frame sync", and
`VulkanContext.Limits`, `MemoryProperties`, `GraphicsTimestampValidBits`, and `SetObjectName` (debug names
through `VK_EXT_debug_utils` when validation is on). Calls checked against their spec pages:
`vkGetBufferMemoryRequirements2` with `VkMemoryDedicatedRequirements`, `vkAllocateMemory` with
`VkMemoryDedicatedAllocateInfo` (allocation size at least the buffer's requirement; Itera passes exactly it),
`vkBindBufferMemory` (offset a multiple of the alignment, memory type from `memoryTypeBits`, offset 0 and
the same buffer for a dedicated allocation, which is used whenever the driver requires one), `vkMapMemory`
of the whole allocation once, and `VkMappedMemoryRange` for flush and invalidate (offset a multiple of
`nonCoherentAtomSize`; size a multiple of it or reaching the end of the memory; `VK_WHOLE_SIZE` is valid
because the mapping runs to the end of the memory). Pages read on docs.vulkan.org/refpages on 2026-09-23. `validate.sh` (core + sync +
best practices): PASS with the 32 MB staging buffer as a dedicated allocation, and again with
`UploadMegabytesPerFrame=1` so it came from a 64 MB block. On the RX 5700 XT, staging lands in memory type 2
(host-visible, coherent system memory). Images (`ResourceKind.Optimal`: the depth image, the shadow cascades, the
multisampled images, the sky field, SMAA's and the upscale's) and readback memory (the particles' counters,
screenshots) came into use later. Non-coherent memory is still not chosen on this machine, so no run here reaches
`GpuBuffer.Flush` or `Invalidate` on it.

## Pipelines and GPU timing, 2026-09-23

Roadmap item 2, stage 4. It began as a sky pass (`SkyPass`, `sky.frag`, now gone; `MeshPass` draws the sky with
`world-sky.frag`). One full-screen triangle (`fullscreen.vert`) with `ViewConstants` in push constants, `LoadOp.DontCare`
where every pixel is written, and a viewport of negative height are still how the full-screen passes and `MeshPass` draw.
`PipelineFactory` builds the shader modules (length a multiple of 4), the pipeline layout (push range checked against
`maxPushConstantsSize`), and the pipeline (`PipelineRenderingCreateInfo`, no render pass, modules destroyed after
creation). `GpuTimer`: a timestamp query pool with two queries per pass
per frame slot; `vkCmdResetQueryPool` first in the command buffer (outside rendering; queries must be reset
before `vkCmdWriteTimestamp2`), timestamps at `ALL_COMMANDS` (one stage, as VUID 03859 requires), results
read after the slot's fence with `64_BIT | WITH_AVAILABILITY` and only for slots a submitted frame wrote
(queries must not be read uninitialised). Spec pages read on docs.vulkan.org/refpages: `vkCmdWriteTimestamp2`,
`vkGetQueryPoolResults`, `vkCmdResetQueryPool`, `VkViewport`. `vkCreateGraphicsPipelines`,
`VkPipelineRenderingCreateInfo`, `vkCmdPushConstants`, and `vkCmdBeginRendering` were written from memory
and are checked only by the validation runs below. `validate.sh` (core + sync + best practices):
PASS at 1280x720, and PASS through four swapchain recreates (850x1390, 956x513, 1496x823) made with
`swaymsg ... resize set` during the run.

## The voxel raymarcher, 2026-09-23 to 2026-10-06

The raymarcher left main on 2026-10-06: `VoxelPass`, `ModelPass`, `voxels.frag` and its other shaders, `GpuRayTracer`,
the centre-ray check, frame blending, smooth normals, texture filtering, the depth copy, and the model shadow map. The
Zorf TD archive's (`salexanderj/ZorfTD-archive`) branch `raymarcher` and tag `raymarcher-final` keep it, and
`docs/rendering-redesign.md` has the reasons. This record's entries about it went with it: stages 5 to 8, the traversal
fixes, the register counts, and the centre-ray check. Two things from that work still apply:
`VulkanContext.MaxBufferSize` (read from `PhysicalDeviceVulkan13Properties`; `MeshPass` sizes its heap with it), and
buffer copies recorded between two `MemoryBarrier2` (copy regions in one `vkCmdCopyBuffer` call must not overlap,
VUID-vkCmdCopyBuffer-pRegions-00117; `MeshPass.RecordUploads` does this).

## Render thread, 2026-09-22

Presentation moved to a render thread (`RenderThread`, `FrameExchange`): on Sway, a FIFO present blocked
for as long as the window was hidden, which stopped the whole loop. Checked with `validate.sh` (core,
including thread safety, + sync + best practices): PASS through two resizes and a 10 s hide. During the
hide, fixed steps ran every second with none dropped, and the render thread logged a 10 s presentation
gap on return. Steady state: 240 frames per second on the 240 Hz monitor, 0 bytes per frame allocated on
the render thread. Quitting while the window was hidden: the render thread did not stop within 2 s,
so the window and Vulkan teardown were skipped (Warnings in the log), every other step ran, and the
process exited with code 0 after 2.1 s without crashing.

## Debug lines, 2026-09-25

Roadmap item 4, part B. `DebugLinePass` draws the frame's `DebugLines` after the world pass: one host-visible storage
buffer (`MemoryUsage.Upload`) with a region per frame slot, one descriptor set, a line-list pipeline without vertex
input (`PipelineFactory.CreateLines`), and `ViewConstants` as push constants visible to the vertex stage only (its own
layout). The main thread writes the slot's region between taking the request and publishing the frame; the render
thread flushes it (a no-op on coherent memory), so the main thread's one Vulkan call stays the one named in the
`vulkan` skill (`StagingBuffer.Flush`). Before the draw, a `MemoryBarrier2` from colour attachment writes to colour
attachment reads and writes; the attachment loads (`LoadOp.Load`), since the world pass leaves the image drawn. `debug-lines.vert` reads the vertices by
`gl_VertexIndex`, the draw's first vertex choosing the slot. `DebugLineVertex` and the push block have `GpuLayout`
checks. `validate.sh 10` (core + sync + best practices): PASS; a validated Debug run with the lines on and two balls
thrown (clicks sent with `swaymsg seat - cursor press`) drew lines in 2,301 frames at 0.007 ms each with no validation
message. The pass also has a depth attachment (GREATER_OR_EQUAL, no writes, the depth image loaded).

## Depth image, 2026-09-25

Roadmap item 4, part C. New Vulkan objects, created before the render thread starts:
- `GpuImage` (`GpuMemoryAllocator.CreateImage`): an optimal-tiling image and its view, in the allocator's image blocks
  or a dedicated allocation (`Allocate` and `TryAllocateDedicated` take the image for `MemoryDedicatedAllocateInfo`).
- `DepthTarget`: a D32_SFLOAT image the size of the swapchain, made again by the render thread in
  `OnSwapchainRecreated` (device idle). `DeviceRequirements.Formats` requires D32_SFLOAT with optimal tiling as a depth
  attachment, and `DescribeCandidate` rejects a device without it. The image is an attachment only: no pass samples it.
  `MeshPass` writes it, and the particles and lines test against it; `DepthTarget.RecordFrameStart` holds its barrier
  (`synchronization.md`).
- `ShaderCompiler.Compile` takes defines and puts them into the source after `#version`, so the cache key, a hash of the
  source, covers them (`MeshPass` compiles `mesh.vert` and `mesh-shadow.vert` a second time with `MESH_MODEL`).

A lesson from the first validated run with shadows: a descriptor binding that a vertex shader reads needs `VertexBit` in
its stage flags, or `vkCreateGraphicsPipelines` reports it. `MeshPass` gives every binding the vertex and fragment stages.

The model shadow map (a 2048x2048 depth image, a comparison sampler, a `ModelShadowView` per slot) and the models' pass
(`ModelPass`, three triangle pipelines over `ModelInstance` records) went with the raymarcher on 2026-10-06. Models are meshes now:
`MeshPass` draws them in the world pass and into the shadow cascades.

## The mesh pass, 2026-10-06

Written from the code at the Zorf TD archive's commit `2ffe140`, the first without the raymarcher; no validation run of
its own is recorded here, so run both standard profiles below after changing it. `MeshPass` (`Rendering/Meshes/`) owns,
all made in `Initialize` before the render thread starts:
- The mesh heap: one device-local buffer of words (storage, index, and transfer destination usage) that `WorldMeshes`
  fills through staging copies. Its size comes from `Engine:Renderer:MeshMegabytes`, limited by the device's
  `MaxStorageBufferRange` and `MaxBufferSize` and by half its largest device-local heap, in whole granules
  (`GpuRangeHeap`). It holds the materials and the tint map at fixed offsets (multiples of 256 bytes), then each block's
  vertices and 16-bit indices.
- Host-visible (`MemoryUsage.Upload`) buffers with one region per frame slot. The main thread writes them between taking
  the request and publishing the frame (`WorldMeshes.Upload`), and the render thread flushes the used part in
  `FlushSlot`. They hold the draws (`MeshDraw`), three lists of indexed indirect commands (the camera's and the two
  cascades'), the cascades' record (`MeshLighting`), the models' draws (`MeshModelDraw`) and commands, and the point
  lights (`MeshLight`).
- The shadow cascades: a D32_SFLOAT image of two layers (three with `Engine:Renderer:DistantShadowMetres`), `Engine:Renderer:ShadowMapTexels` texels square, one view per
  layer, and a comparison sampler (`CompareEnable`, `LessOrEqual`, a white border; linear filtering only when the format
  reports `SampledImageFilterLinearBit`).
- The sky field: an R8 3D image and a linear sampler with a white border, filled from staging by `RecordSkyField`.
- With `Engine:Renderer:MsaaSamples` above 1, and as many samples as the device offers for colour and depth together
  (`ChooseSamples`): multisampled colour and depth images of the swapchain's size, made again when its size or format
  changes. Rendering resolves them into the target (average) and the depth image (sample 0).
- One descriptor set (bindings 0 to 17: storage buffers, the cascades, the sky field, the material texture sets, the look at 17; `.claude/rules/rendering.md`) visible to the vertex and fragment stages; `ViewConstants` as push constants for the world draws and `MeshShadowConstants` for the cascades; and
  five pipelines (world meshes, sky, shadow cascades, model meshes, model shadow cascades). The sky draws with depth
  `Equal` to the cleared 0 of the reverse depth, the meshes with `Greater`, writing.

`FrameRenderer.Record` calls it in the order `synchronization.md` lists. Blocks and models are drawn with
`vkCmdDrawIndexedIndirect`, so the device must have `multiDrawIndirect` and `drawIndirectFirstInstance`. `MeshPassTests`
checks `MeshDraw`, `MeshMaterial`, `MeshLighting`, `MeshLight`, `MeshModelDraw`, `MeshShadowConstants`, and
`ViewConstants` against the compiled shaders with `GpuLayout`.

## Passes added from 2026-09-27 to 2026-09-29

Recorded on 2026-10-04 (audit of 2026-10-03: this record had stopped at 2026-09-25). Each came with a `validate.sh`
run that passed when it was built; the second standard profile below runs them all.

- The voxel pass's compute split (2026-09-27) left with the raymarcher on 2026-10-06.
- The UI (`UiPass`, 2026-09-27): the changed rows copied from the slot's upload region into a storage buffer between two
  barriers, then a full-screen draw that blends it.
- Render scale and FSR 1 (`UpscalePass`, `RenderScale`, 2026-09-27): the world drawn into smaller images, then EASU and
  RCAS into the swapchain image; its images are freed without a device wait (the archive's 7befbda, 2026-09-29).
- Particles (`ParticlePass`, 2026-09-28): counters reset, spawn, and simulate compute passes; the cubes and sprites
  read the particles as storage in the vertex shader and draw indirectly; the counters are copied for the host.
- The loading screen (`LoadingPass`, 2026-09-28): a full-screen draw instead of the world while it shows.
- Screenshots (`ScreenshotPass`, 2026-09-29): a copy of the swapchain image into a readback buffer (the swapchain has
  `TransferSrc` usage where the surface offers it), read after the slot's fence.
- Shader reloads (`ShaderReloads`, `IReloadsShaders`, 2026-09-29): every pass compiles on the render thread, then waits
  for the device and makes its pipelines again; an error keeps the old ones. Since 2026-10-03 each pass clears a
  pipeline's handle as it destroys it.
- The swapchain's recreation rules (`SwapchainState`, 2026-10-03): one set of rules with no Vulkan call, tested alone.

## Standard validation profiles, 2026-10-03

Two runs make a change to Vulkan code done (before these, no recorded run reached the particle passes,
screenshots, or shader reloads, and the bare game draws no debug lines or upscale):

1. The bare game: `validate.sh 35 --Engine:Session:Mode=Offline --Engine:Steam:Enabled=false
   --Engine:Window:VSync=false "--Engine:Console:Run=settle 60; balls 8"` (the balls, kept as bodies by `settle 60`, draw models). For swapchain or frame changes,
   resize the window during the run from sway (`swaymsg '[con_id=ID] floating enable, resize set 900 600'`, then
   other sizes, one repeated); on 2026-10-03, six resizes made six recreates for six sizes, the repeated one none.
2. Every optional pass: `validate.sh 45 --Engine:Session:Mode=Offline --Engine:Steam:Enabled=false
   --Engine:Window:VSync=false --Engine:Window:Fullscreen=false --Engine:Renderer:DynamicRenderScaling=false
   --Engine:Renderer:RenderScale=0.75
   "--Engine:Renderer:ShaderSourceDirectory=$PWD/src/Itera.Engine/Rendering/Shaders"
   "--Engine:Console:Run=settle 60; balls 8; lines on; particles stress smoke 8; screenshot; shaders reload"`. VSync and full screen are given
   on the command line, since `settings` writes settings.json. On 2026-10-09, on the starter game, it passed with no
   validation message: 20 pipelines made again in 14 ms, a screenshot written, and in its last 10 s window 1,401
   frames with debug lines, models, the upscale, and particles moved and drawn.

SMAA records only when the world has one sample a pixel, and `Engine:Renderer:MsaaSamples` is 4 by default. Neither
profile above sets it, so neither runs SMAA; add `--Engine:Renderer:MsaaSamples=1` to a run of profile 2 to cover it.

## Vulkan skill: SKILL.md, synchronization.md, and silk-net.md, dated passages

From `.claude/skills/vulkan/SKILL.md`:

- "Itera requires Vulkan 1.3 on the loader and the device (decided 2026-09-22)."
- "Itera has its own block sub-allocator in `Rendering/Memory/` (decided 2026-09-22; built 2026-09-23)."
- "A constructor read of the storage buffer range (as zeros, before the guard) drew every frame at render scale 0.05 for a day."
- "Objects that live per frame in flight are indexed by `FrameSync.CurrentFrame` (`FrameRenderer.CurrentSlot`)." (`FrameRenderer.CurrentSlot` is gone.)

From `.claude/skills/vulkan/synchronization.md`:

- "The codebase uses synchronization2 only (decided 2026-09-22)"
- "the clear pass had this bug until 2026-09-22."
- "(without the wait, validation reported 16 objects destroyed in use and the process crashed, 2026-09-27)"

From `.claude/skills/vulkan/silk-net.md`: "Checked against the Silk.NET v2.23.0 source (`src/Vulkan/Silk.NET.Vulkan/Vk.cs`, `src/Core/Silk.NET.Core/`) on 2026-09-22."

## ECS-to-GPU skill: reference.md, audit records

From `.claude/skills/ecs-vulkan/reference.md`, lines 45 to 134 (from "Audit record, 2026-09-22" to the end).

## Audit record, 2026-09-22

Audit of the code at commit `336006e` of the Zorf TD archive (`salexanderj/ZorfTD-archive`) for the ECS-to-GPU path.

### Added

- `tests/Shared/GpuLayout.cs` (first in `tests/Itera.Engine.Tests/Rendering/`): compares a C# struct with the block layout in compiled
  SPIR-V (set and binding, or push constants), including array stride, member types, matrix order, and the
  128-byte push constant limit. `GpuLayoutTests` covers a match, std430 offsets and stride, a row-major
  matrix, a type mismatch, a `bool` field, a missing binding, and scalar push constants.
- `probe/`: re-checks the facts in `notes.md` marked "probe" (23 checks).

### Open

- No code moves entity data to the GPU yet. The renderer only clears the screen.
- Frame order: the ECS frame group runs before `DrawFrame` waits on the slot's fence (`SKILL.md`, "Frame
  order"; `design.md`, choice 1).
- `VulkanContext` does not request `scalarBlockLayout`. The first shader with `layout(scalar)` must add it
  to device selection and device creation.
- Frame systems do not receive `FixedStepAccumulator.Alpha` (also open in the `ecs` skill).
- `src/Itera.Engine/Rendering/GpuData/` does not exist yet. Create it with the first struct a shader reads.

### Confirmed sound

- No component holds a Vulkan handle, pointer, or mapped address. No ECS system calls Vulkan, and the
  renderer does not read the world.
- `ClearRenderer` records its command buffer every frame with `OneTimeSubmitBit` and resets it before
  recording, which matches the command buffer rules.

## Update, 2026-09-22 (pre-feature review)

Closed open items:

- Frame order: `FrameRenderer.BeginFrame` waits on the slot's fence before the ECS frame group (option A).
- `DeviceRequirements` requires and enables `scalarBlockLayout`.
- Frame systems receive `FrameTime`, including `Alpha`.
- `ShaderCompiler` targets Vulkan 1.3; `GpuLayoutTests` pass with it.

Still open: `src/Itera.Engine/Rendering/GpuData/` does not exist yet, no code moves entity data to the GPU,
and where the extract step runs (`design.md`, choice 2) is undecided.

## Update, 2026-09-23 (first GPU struct)

- `src/Itera.Engine/Rendering/GpuData/` exists: `ViewConstants` is the push constant block of `view.glsl`
  (`layout(push_constant, scalar)`). It began as six `Vector3` and a `uint` (76 bytes) and has changed with the
  renderer since: it is 116 bytes now, and its on/off switches share one word (`ViewSwitches`). `ViewConstantsTests`
  compiles every shader that reads it (`mesh.vert`, `mesh.frag`, `world-sky.frag`, `debug-lines.vert`, the particle
  cubes and sprites) and checks each with `GpuLayout.PushConstantDifferences` (no differences).
- It is built on the main thread for each frame (`ViewConstants.Create` in `GameLoop`, from `RenderView` and
  the request's image size) and travels in `RenderFrame.View`; the render thread pushes it. No entity data
  reaches the GPU through it.

## Update, 2026-09-23 (the raymarcher's structs, gone 2026-10-06)

- The voxel raymarcher added GPU structs for its centre-ray check (`VoxelProbe`), its materials (`VoxelMaterial`), its
  heap and window format (`VoxelGpuFormat`, `VoxelGpuLayout`: `uint` arrays read at hand-written offsets, with a test
  that compared the shader's `#define`s with the C# constants), its light history, and more `ViewConstants` fields (view
  distance, debug picture, shadows, history). All of it left with the raymarcher on 2026-10-06. The rule it showed
  still holds: where a shader reads a buffer at hand-written offsets, a test compares the shader's constants with the
  C# ones.

## Update, 2026-09-22 (render thread)

- Design choice 6 is decided and built: `RenderThread` and `FrameExchange`. `FrameRenderer.CurrentSlot` is
  gone; the slot comes from `RenderFrameRequest.Slot`, valid on the main thread between `TryTakeRequest`
  and `Publish`.
- Frame systems run only when the render thread asks for a frame.

## Update, 2026-10-06 (the mesh path)

Written from the code at the Zorf TD archive's commit `2ffe140`. Characters and loose bodies are meshes, drawn by
`WorldMeshes` (`Rendering/Meshes/WorldMeshes.Models.cs`) through `MeshPass`; `ModelPass`, `ModelInstance`,
`ModelShadowView`, and `ModelInstanceBuilder` are gone.
- A frame system (`LooseBodyModelSystem`, and a game's own) adds each model to `ModelInstances`, a CPU array (`ModelDraw`: model handle,
  level, corner, turn, tint, shadow mode; at most `ModelInstances.MaxModels`, 32,768; more are counted in `Dropped`).
  `GameLoop` clears it before the frame group and, after it, calls `WorldMeshes.Upload(slot, now, view,
  ModelInstances.Draws)` on the main thread.
- `Upload` meshes models in blocks of 32 voxels on `MeshWorkers` threads into ranges of the mesh heap, and writes the
  slot's draws: the draws in view, or casting into a shadow cascade, are grouped by model level with a counting sort,
  and each block of a level is one indexed indirect command whose instances are that level's `MeshModelDraw` records,
  in three lists (the camera's, the near cascade's, the far cascade's). The records and commands go into host-visible
  buffers with a region per frame slot (`MeshPass.ModelDrawsOf`, `ModelCommandsOf`); the render thread flushes them when
  the memory is not coherent (`MeshPass.FlushSlot`).
- Meshes reach the GPU through the slot's staging region (`StagingWriter`) and one `vkCmdCopyBuffer` the render thread
  records (`MeshPass.RecordUploads`); the copy count travels in `RenderFrame.MeshCopies`, and `RenderFrame.Meshes` (a `MeshFrame`)
  carries the counts of draws and commands.
- `GpuLayout` checks `MeshModelDraw` against `mesh.vert` and `mesh-shadow.vert` compiled with `MESH_MODEL`
  (`MeshPassTests`). `ModelInstances.Add` allocates nothing (`LooseBodyModelSystemTests`: 0 bytes), and
  `WorldMeshesTests.An_upload_allocates_nothing_once_up_to_date` covers `Upload`.
- No stable GPU slots: the draws are written in full every frame, as the design says.

## ECS-to-GPU skill: SKILL.md, design.md, and notes.md, dated passages

From `.claude/skills/ecs-vulkan/SKILL.md`:

- "Scalar layout was chosen on 2026-09-22."
- "`DeviceRequirements` requires and enables it (since 2026-09-22)"
- "Option A of `design.md` choice 1 and a render thread (choice 6) were chosen on 2026-09-22."
- The paragraph "What the research found, in short", dropped because the rules below it state each point.

From `.claude/skills/ecs-vulkan/design.md`:

- "(2026-09-23, as the raymarcher's `VoxelGpuUploader`; `WorldMeshes` took its place on 2026-10-06)"
- "(built 2026-09-25 as raymarched models; drawn as meshes since the raster pipeline became the only one; the decisions in ARCHITECTURE.md section 8)"
- "**Fence order. Decided 2026-09-22: A.**" and "**Render thread. Decided 2026-09-22 and built.**"
- "Decided for the camera on 2026-09-23" and "Model draw data (2026-09-25) ... (`ModelPass.Write` until 2026-10-06)."

From `.claude/skills/ecs-vulkan/notes.md`:

- "Checked on 2026-09-22."
- "(searched `src/Arch` for these on 2026-09-22)"
- "Compiled through Silk.NET.Shaderc 2.23.0 with `ShaderCompiler`'s options at the time: `OptimizationLevel.Performance`, default target environment (Vulkan 1.0). Since 2026-09-22 `ShaderCompiler` targets Vulkan 1.3; the layout tests (`GpuLayoutTests`) and `probe.sh` (23 checks, now compiling with the same target) pass unchanged."
- "RADV: AMD Radeon RX 5700 XT, Mesa 26.0.8, `vulkaninfo` on 2026-09-22."
