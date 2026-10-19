# References and checked facts

## Where to look things up

- Function or struct valid usage (VUIDs): `https://registry.khronos.org/vulkan/specs/latest/man/html/<name>.html`,
  e.g. `.../vkCmdPipelineBarrier.html`. A validation message's VUID also points here.
- Full spec: https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html
- Synchronization examples (barrier stage and access pairs): https://github.com/KhronosGroup/Vulkan-Docs/wiki/Synchronization-Examples
- Vulkan Guide, synchronization: https://docs.vulkan.org/guide/latest/synchronization.html
- Vulkan Guide, swapchain semaphore reuse: https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html
- Validation layer settings (env vars such as `VK_LAYER_VALIDATE_SYNC`): https://vulkan.lunarg.com/doc/sdk/latest/linux/khronos_validation_layer.html
- Silk.NET 2.23 Vulkan source: https://github.com/dotnet/Silk.NET/tree/v2.23.0/src/Vulkan/Silk.NET.Vulkan
  (generated signatures in Vk.gen.cs, structs in `Structs/`, hand-written loading in Vk.cs).
- Local tools: `vulkaninfo --summary` (devices, layers); `vulkaninfo` full output for limits, formats,
  and features of each GPU.

Debug builds set `validate_sync` and `validate_best_practices` through `VK_EXT_layer_settings`
(`VulkanContext.CreateInstance`), so no environment variables are needed. For one-off runs of other
settings, the layer reads `VK_LAYER_<SETTING>` variables such as `VK_LAYER_VALIDATE_SYNC=1`. The older
`VK_LAYER_ENABLES=VK_VALIDATION_FEATURE_ENABLE_*` form is deprecated in layer 1.4.341 and logs a warning.

## Device setup

- Vulkan 1.3 is required. `DeviceRequirements.cs` lists the device extensions and features. `VulkanContext` checks them
  with `GetPhysicalDeviceFeatures2` during selection, gives each rejected device a reason in its log line, and enables
  them through a `PhysicalDeviceVulkan12Features` to `PhysicalDeviceVulkan13Features` chain.
- `VulkanContext.MaxBufferSize` is read from `PhysicalDeviceVulkan13Properties`. `MeshPass` sizes its heap with it.
- A selected software rasteriser (llvmpipe) logs a Warning. The debug callback checks the log level before it converts
  the message.
- `Vk.GetApi()` runs in the `VulkanContext` constructor, so a missing Vulkan loader fails during DI resolution.
  `Program.cs` resolves `GameLoop` inside its `try`, so this is still logged and exits 1.
- The `renderFinished` semaphores are destroyed after `DeviceWaitIdle` on recreate. `synchronization.md` has the known
  gap.

## Shaders

`ShaderCompiler` targets Vulkan 1.3, reads shader files with `#include`, keys the cache on the compiler settings too,
writes cache files atomically, and replaces damaged ones. `ShaderCompiler.Compile` takes defines and puts them into the
source after `#version`, so the cache key, a hash of the source, covers them.

A descriptor binding that a vertex shader reads needs `VertexBit` in its stage flags, or `vkCreateGraphicsPipelines`
reports it. `MeshPass` gives every binding the vertex and fragment stages.

`ShaderReloads` and `IReloadsShaders`: every pass compiles on the render thread, then waits for the device and makes its
pipelines again. An error keeps the old ones. Each pass clears a pipeline's handle as it destroys it.

## GPU memory

`Rendering/Memory/` holds `MemoryTypeSelector`, `BlockSuballocator`, `GpuMemoryAllocator`, `GpuBuffer`, `GpuImage`, and
`StagingBuffer`. `VulkanContext` has `Limits`, `MemoryProperties`, `GraphicsTimestampValidBits`, and `SetObjectName`
(debug names through `VK_EXT_debug_utils` when validation is on). These calls follow their spec pages:

- `vkGetBufferMemoryRequirements2` with `VkMemoryDedicatedRequirements`, and `vkAllocateMemory` with
  `VkMemoryDedicatedAllocateInfo`. The allocation size is at least the buffer's requirement; Itera passes exactly it.
- `vkBindBufferMemory`: the offset is a multiple of the alignment, and the memory type comes from `memoryTypeBits`. A
  dedicated allocation uses offset 0 and the same buffer. It is used whenever the driver requires one.
- `vkMapMemory` maps the whole allocation once.
- `VkMappedMemoryRange` for flush and invalidate: the offset is a multiple of `nonCoherentAtomSize`, and the size is a
  multiple of it or reaches the end of the memory. `VK_WHOLE_SIZE` is valid because the mapping runs to the end.

`GpuMemoryAllocator.CreateImage` makes an optimal-tiling image and its view in the allocator's image blocks or in a
dedicated allocation. Non-coherent memory is not chosen on the development machine, so no run there reaches
`GpuBuffer.Flush` or `Invalidate` on it.

## GPU timing

`GpuTimer` has a timestamp query pool with two queries per pass per frame slot.

- `vkCmdResetQueryPool` comes first in the command buffer, outside rendering. Queries must be reset before
  `vkCmdWriteTimestamp2`.
- Timestamps use the `PipelineStageFlags2.AllCommandsBit` stage, one stage as VUID 03859 requires.
- Results are read after the slot's fence with `64_BIT | WITH_AVAILABILITY`, and only for slots that a submitted frame
  wrote. Queries must not be read uninitialised.

## Pipelines and passes

- `PipelineFactory` builds the shader modules (length a multiple of 4), the pipeline layout (push range checked against
  `maxPushConstantsSize`), and the pipeline (`PipelineRenderingCreateInfo`, no render pass, modules destroyed after
  creation).
- A full-screen triangle (`fullscreen.vert`) with `ViewConstants` in push constants, `LoadOp.DontCare` where every pixel
  is written, and a viewport of negative height are how the full-screen passes and `MeshPass` draw.
- `DebugLinePass` draws the frame's `DebugLines` after the world pass. It has one host-visible storage buffer
  (`MemoryUsage.Upload`) with a region per frame slot, one descriptor set, and a line-list pipeline without vertex input
  (`PipelineFactory.CreateLines`). `ViewConstants` are push constants visible to the vertex stage only. The main thread
  writes the slot's region between taking the request and publishing the frame. The render thread flushes it, so the
  main thread's one Vulkan call stays `StagingBuffer.Flush`. A `MemoryBarrier2` runs before the draw, from colour
  attachment writes to colour attachment reads and writes. The attachments load (`LoadOp.Load`). The pass has a depth
  attachment that tests with GREATER_OR_EQUAL and does not write. `debug-lines.vert` reads the vertices by
  `gl_VertexIndex`, and the draw's first vertex chooses the slot.
- `DepthTarget` is a D32_SFLOAT image the size of the swapchain, made again by the render thread in
  `OnSwapchainRecreated` (device idle). `DeviceRequirements.Formats` requires D32_SFLOAT with optimal tiling as a depth
  attachment, and `DescribeCandidate` rejects a device without it. No pass samples it as a texture except the
  particles' simulate pass. `MeshPass` writes it, and the particles and lines test against it.
- Buffer copies recorded between two `MemoryBarrier2`: copy regions in one `vkCmdCopyBuffer` call must not overlap
  (VUID-vkCmdCopyBuffer-pRegions-00117). `MeshPass.RecordUploads` does this.
- Models are meshes: `MeshPass` draws them in the world pass and into the shadow cascades.
- Presentation runs on a render thread (`RenderThread`, `FrameExchange`) because a FIFO present blocks for as long as
  the window is hidden, which would stop the whole loop.
- The swapchain's recreation rules are in `SwapchainState`, with no Vulkan call, tested alone.

## Standard validation profiles

Two runs make a change to Vulkan code done. The bare game draws no debug lines or upscale, so the first run does not
reach the particle passes, screenshots, or shader reloads.

1. The bare game: `validate.sh 35 --Engine:Session:Mode=Offline --Engine:Steam:Enabled=false
   --Engine:Window:VSync=false "--Engine:Console:Run=settle 60; balls 8"` (the balls, kept as bodies by `settle 60`,
   draw models). For swapchain or frame changes, resize the window during the run from sway
   (`swaymsg '[con_id=ID] floating enable, resize set 900 600'`, then other sizes, one repeated). Each new size
   makes one recreate; the repeated one makes none.
2. Every optional pass: `validate.sh 45 --Engine:Session:Mode=Offline --Engine:Steam:Enabled=false
   --Engine:Window:VSync=false --Engine:Window:Fullscreen=false --Engine:Renderer:DynamicRenderScaling=false
   --Engine:Renderer:RenderScale=0.75
   "--Engine:Renderer:ShaderSourceDirectory=$PWD/src/Itera.Engine/Rendering/Shaders"
   "--Engine:Console:Run=settle 60; balls 8; lines on; particles stress smoke 8; screenshot; shaders reload"`. VSync and
   full screen are given on the command line, since `settings` writes settings.json.

SMAA records only when the world has one sample a pixel, and `Engine:Renderer:MsaaSamples` is 4 by default. Neither
profile sets it, so neither runs SMAA. Add `--Engine:Renderer:MsaaSamples=1` to a run of profile 2 to cover it.
