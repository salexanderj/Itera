# References and audit record

## Where to look things up

- Function or struct valid usage (VUIDs): `https://registry.khronos.org/vulkan/specs/latest/man/html/<name>.html`,
  e.g. `.../vkCmdPipelineBarrier.html`. A validation message's VUID also points here.
- Full spec: https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html
- Synchronization examples (barrier stage and access pairs): https://github.com/KhronosGroup/Vulkan-Docs/wiki/Synchronization-Examples
- Vulkan Guide, synchronization: https://docs.vulkan.org/guide/latest/synchronization.html
- Vulkan Guide, swapchain semaphore reuse: https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html
- Validation layer settings (env vars such as `VK_LAYER_VALIDATE_SYNC`): https://vulkan.lunarg.com/doc/sdk/latest/linux/khronos_validation_layer.html
- Silk.NET 2.23 Vulkan source: https://github.com/dotnet/Silk.NET/tree/v2.23.0/src/Vulkan/Silk.NET.Vulkan
  (generated signatures in `Vk.gen.cs`, structs in `Structs/`, hand-written loading in `Vk.cs`).
- Local tools: `vulkaninfo --summary` (devices, layers); `vulkaninfo` full output for limits, formats,
  and features of each GPU.

Debug builds set `validate_sync` and `validate_best_practices` through `VK_EXT_layer_settings`
(`VulkanContext.CreateInstance`), so no environment variables are needed. For one-off runs of other
settings, the layer reads `VK_LAYER_<SETTING>` variables such as `VK_LAYER_VALIDATE_SYNC=1`. The older
`VK_LAYER_ENABLES=VK_VALIDATION_FEATURE_ENABLE_*` form is deprecated in layer 1.4.341 and logs a warning.

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
- The shadow cascades: a D32_SFLOAT image of two layers, `Engine:Renderer:ShadowMapTexels` texels square, one view per
  layer, and a comparison sampler (`CompareEnable`, `LessOrEqual`, a white border; linear filtering only when the format
  reports `SampledImageFilterLinearBit`).
- The sky field: an R8 3D image and a linear sampler with a white border, filled from staging by `RecordSkyField`.
- With `Engine:Renderer:MsaaSamples` above 1, and as many samples as the device offers for colour and depth together
  (`ChooseSamples`): multisampled colour and depth images of the swapchain's size, made again when its size or format
  changes. Rendering resolves them into the target (average) and the depth image (sample 0).
- One descriptor set of nine bindings (seven storage buffers, the cascades, the sky field) visible to the vertex and
  fragment stages; `ViewConstants` as push constants for the world draws and `MeshShadowConstants` for the cascades; and
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
