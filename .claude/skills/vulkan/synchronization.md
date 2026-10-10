# Synchronization

Read this before changing `FrameSync`, `FrameRenderer`, a pass such as `MeshPass`, the swapchain, or before
adding any barrier, semaphore, fence, or queue submit.

The codebase uses synchronization2 only (decided 2026-09-22): `CmdPipelineBarrier2` with
`ImageMemoryBarrier2`/`BufferMemoryBarrier2`/`MemoryBarrier2`, and `QueueSubmit2` with
`SemaphoreSubmitInfo`. Both are core in Vulkan 1.3, which `DeviceRequirements` requires. Do not add
`CmdPipelineBarrier` or `QueueSubmit` calls.

## The frame loop as it stands

`FrameSync.FramesInFlight = 2`. Per frame slot: one command pool (`TransientBit`) with one primary
command buffer, one `imageAvailable` semaphore, one `inFlight` fence (created signalled). Per swapchain
image: one `renderFinished` semaphore.

All of this runs on the render thread (`RenderThread.Run`); the main thread makes no Vulkan call while
it runs, except the flush of the staging region it wrote (`StagingBuffer.Flush`; SKILL.md, "Threads"). Each frame:

1. `FrameRenderer.BeginFrame`: recreate the swapchain if a resize was flagged (`WaitIdle`,
   `VulkanSwapchain.Recreate`, `FrameSync.OnSwapchainRecreated`). Return false when the swapchain is
   zero-sized; the render thread then waits for a resize (100 ms at a time, so it notices stop).
2. Wait on `inFlight[slot]`. After this the slot's command pool, its command buffer,
   `imageAvailable[slot]`, and any memory that belongs to the slot are free to reuse, and the slot's
   timestamp queries from its last submitted frame are complete: `GpuTimer.Collect` reads them.
3. Reset the slot's command pool (`vkResetCommandPool`).
4. `FrameRenderer.TryAcquire`: acquire with `imageAvailable[slot]`. On `ErrorOutOfDateKhr`: flag a
   recreate and return *without* resetting the fence (a reset fence with no submit deadlocks the next
   wait) and without advancing the slot.
5. Reset the fence only after acquire succeeded. From here a present must follow.
6. `FrameExchange.Request(slot, extent)` wakes the main thread, which builds the frame (frame systems may
   write the slot's memory) and publishes it; the render thread waits for it. If stop arrives while it
   waits, it goes on with the last frame, so the acquired image is still presented.
7. `FrameRenderer.Present` calls `Record`, which records, in this order:
   - the reset of the slot's timestamp queries (outside rendering) and a first timestamp;
   - the mesh copies from the slot's staging region into the mesh heap between their two barriers (only for a frame
     received fresh, and only when it has copies), the mesh assets' uploads in the first frame
     (`MeshAssetPass.RecordUploads`, under the loading screen too), the image views' shown images cleared in the first
     frame (`RenderViewPass.RecordFirstFrame`), then the sky field's copy into its image (also only for a fresh frame,
     under the loading screen too);
   - the acquire barrier;
   - unless the loading screen covers the picture, `RecordWorld`: the depth image's frame-start barrier, then
     `MeshPass.Record`, which first flushes the slot's mapped draw regions (when memory is not coherent), copies the
     material texture sets in the first frame and records an asked-for bake (`RecordTextures`, `RecordBake`), sorts the
     lights into clusters, skins the posed mesh assets (`MeshAssetPass.RecordSkinning`), records a game's `Compute`
     passes (`RenderPasses`, which adds no barrier after them), draws the two shadow cascades into a depth array, then
     in one rendering the world (blocks, models, mesh assets, a game's `Opaque` passes, the sky, the blended mesh
     assets, a game's `Transparent` passes) into the target, resolving the multisampled images into it and the depth
     image when `Engine:Renderer:MsaaSamples` is above 1; then, in a fresh frame with an image view due, each due view
     (`RenderViewPass`: its clusters, its world, models, mesh assets, sky, and blended assets into its own colour and
     depth, then a copy of the colour into its shown image between barriers that take that image from and back to the
     shader-read layout); then, while particles can be alive and the frame is fresh, the particles' compute passes
     (counters reset, spawn, simulate against the depth image handed to compute and back, the counters copied for the
     host); then the particles' cubes and sprites, the debug lines, a game's `Scene` passes (after a barrier from the
     earlier colour and depth writes), SMAA's edges, weights, and blend (only with one sample a pixel; the first such
     frame also copies SMAA's lookup textures), below render scale 1 the upscale, and a game's `Screen` passes. Each
     draws into the swapchain image, SMAA's scene image, or the upscale's source image, as `RecordWorld` chooses;
   - the loading screen's draw while it shows or fades;
   - a screenshot's copy when one is asked for without the UI, the UI (its changed rows copied, then the draw), the
     present barrier (a screenshot with the UI copies here), and a last timestamp.

   Each pass sits between its own two timestamps (`GpuPass`), written at `ALL_COMMANDS`, so each waits for the commands
   before it.
8. `QueueSubmit2`: wait `imageAvailable[slot]` at `ColorAttachmentOutput`, signal
   `renderFinished[imageIndex]` at `ColorAttachmentOutput`, fence `inFlight[slot]`.
9. Present waiting on `renderFinished[imageIndex]`, then advance the slot.

Why the request comes after the fence wait and acquire: those are where the render thread blocks under
FIFO, so the main thread builds each frame from input read just before recording, and the slot is free
for frame systems to write its mapped memory directly (ecs-vulkan skill). When present blocks (a hidden
window on Wayland), no request comes and the main thread keeps simulating.

Why `renderFinished` is per image: nothing tells the app when present has finished waiting on a
semaphore. The same image index cannot be acquired again until its previous present is done, so a
semaphore tied to the image index is safe to signal again. A per-frame-slot semaphore is not, and
the validation layer reports it (VUID-vkQueueSubmit-pSignalSemaphores-00067).

Known gap: `vkDeviceWaitIdle` before swapchain recreate does not formally cover the present
engine's semaphore waits. `VK_EXT_swapchain_maintenance1` present fences close this. It has not
caused validation errors here; revisit when that extension is added.

## Rules

- A semaphore wait only covers the stages in its `SemaphoreSubmitInfo.StageMask`. Any barrier that
  touches the acquired image must have that stage in its `SrcStageMask`, so the barrier chains after
  the wait. The swapchain acquire transition therefore uses
  `SrcStageMask = ColorAttachmentOutputBit, SrcAccessMask = None` (matching the submit's wait stage),
  not `TopOfPipeBit`. With `TopOfPipeBit` the layout transition can run before the presentation engine
  releases the image. Synchronization validation reports this as a WRITE_AFTER_READ hazard against
  `vkAcquireNextImageKHR`; the clear pass had this bug until 2026-09-22.
- Present needs the image in `PresentSrcKhr`. In synchronization2 the semaphore signal only covers the
  stages in its `StageMask`, so the present barrier uses `DstStageMask = ColorAttachmentOutputBit,
  DstAccessMask = None`, and the signal uses `StageMask = ColorAttachmentOutputBit`. The layout
  transition then chains into the signal that present waits on. Name the exact stages rather than
  `AllCommands`, which makes every later command wait.
- One fence per submit that the CPU waits on. Never reset a fence that has no pending submit.
- Timeouts are `ulong.MaxValue` in the frame loop. A timeout result elsewhere is an error.
- The command pools in `FrameSync` are for the render thread only. Command pools are not thread-safe;
  another thread that records needs its own pool. Reset a slot's pool only after waiting on that slot's
  fence (`FrameRenderer.BeginFrame` does this).
- Queue submits, presents, and `vkDeviceWaitIdle` are externally synchronized with every use of the
  queue: render thread only while it runs. At shutdown the main thread waits for the device only after
  the render thread has stopped (`GameLoop.StopRenderThread`), and before any shutdown step destroys an
  object, also when the render thread ended with an exception: frames it submitted before the fault may still
  run (without the wait, validation reported 16 objects destroyed in use and the process crashed, 2026-09-27).
- Upload work (staging copies) submitted on the graphics queue needs either a fence the CPU waits on
  before freeing the staging buffer, or to ride in the frame's submit.

## Barrier recipes (synchronization2)

Names are Silk.NET's `PipelineStageFlags2` and `AccessFlags2` members without the `Bit` suffix.

| Transition | old -> new layout | SrcStage / SrcAccess | DstStage / DstAccess |
|---|---|---|---|
| Swapchain image, after acquire, to render | Undefined -> ColorAttachmentOptimal | ColorAttachmentOutput / None | ColorAttachmentOutput / ColorAttachmentWrite |
| Swapchain image, after render, to present | ColorAttachmentOptimal -> PresentSrcKhr | ColorAttachmentOutput / ColorAttachmentWrite | ColorAttachmentOutput / None (the signal's stage) |
| Depth image, each frame, discard (`DepthTarget.RecordFrameStart`) | Undefined -> DepthAttachmentOptimal | EarlyFragmentTests \| LateFragmentTests / DepthStencilAttachmentWrite | EarlyFragmentTests \| LateFragmentTests / DepthStencilAttachmentRead \| DepthStencilAttachmentWrite |
| The same, when the multisampled world resolves into the depth image | the same | the same, with ColorAttachmentOutput added to both stages and ColorAttachmentWrite to the source access (the resolve counts as a colour write); then a second memory barrier, ColorAttachmentOutput / None -> ColorAttachmentOutput / ColorAttachmentWrite | the same |
| Upload target, before copy | Undefined -> TransferDstOptimal | None / None | Copy / TransferWrite |
| Upload target, after copy, for sampling | TransferDstOptimal -> ShaderReadOnlyOptimal | Copy / TransferWrite | FragmentShader (add VertexShader if read there) / ShaderSampledRead |
| Colour target, rendered then sampled | ColorAttachmentOptimal -> ShaderReadOnlyOptimal | ColorAttachmentOutput / ColorAttachmentWrite | FragmentShader / ShaderSampledRead |
| Mesh heap, before the frame's copies | (memory barrier) | IndexInput \| VertexShader \| FragmentShader \| Copy / TransferWrite | Copy / TransferWrite |
| Mesh heap, after the copies | (memory barrier) | Copy / TransferWrite | IndexInput \| VertexShader \| FragmentShader / IndexRead \| ShaderStorageRead |
| Sky field image, before its copy or clear | Undefined -> TransferDstOptimal | FragmentShader \| ComputeShader / None | AllTransfer / TransferWrite |
| Sky field image, after, for `mesh.frag` and the particles' simulate pass | TransferDstOptimal -> ShaderReadOnlyOptimal | AllTransfer / TransferWrite | FragmentShader \| ComputeShader / ShaderSampledRead |
| Shadow cascades (both layers), before they are drawn | Undefined -> DepthAttachmentOptimal | FragmentShader \| ComputeShader / None | EarlyFragmentTests \| LateFragmentTests / DepthStencilAttachmentRead \| DepthStencilAttachmentWrite |
| Shadow cascades, after, for `mesh.frag` and the particles' simulate pass | DepthAttachmentOptimal -> ShaderReadOnlyOptimal | EarlyFragmentTests \| LateFragmentTests / DepthStencilAttachmentWrite | FragmentShader \| ComputeShader / ShaderSampledRead |
| Multisampled colour and depth images, before the frame's draws (their contents go) | Undefined -> ColorAttachmentOptimal and Undefined -> DepthAttachmentOptimal | colour: ColorAttachmentOutput / ColorAttachmentWrite; depth: EarlyFragmentTests \| LateFragmentTests \| ColorAttachmentOutput (the last resolve read the samples there) / DepthStencilAttachmentWrite | colour: ColorAttachmentOutput / ColorAttachmentWrite; depth: EarlyFragmentTests \| LateFragmentTests / DepthStencilAttachmentRead \| DepthStencilAttachmentWrite |
| Depth image, world pass to the particles' simulate pass (`DepthTarget.RecordToSampled`) | DepthAttachmentOptimal -> DepthReadOnlyOptimal | EarlyFragmentTests \| LateFragmentTests (\| ColorAttachmentOutput after a resolve, whose ColorAttachmentWrite a memory barrier on that stage makes available first) / DepthStencilAttachmentWrite | ComputeShader / ShaderSampledRead |
| Depth image, back from the simulate pass (`DepthTarget.RecordToAttachment`) | DepthReadOnlyOptimal -> DepthAttachmentOptimal | ComputeShader / None | EarlyFragmentTests \| LateFragmentTests / DepthStencilAttachmentRead \| DepthStencilAttachmentWrite |
| Colour and depth, world pass to particles or lines | (memory barrier) | ColorAttachmentOutput \| EarlyFragmentTests \| LateFragmentTests / ColorAttachmentWrite \| DepthStencilAttachmentWrite | the same stages / ColorAttachmentRead \| ColorAttachmentWrite \| DepthStencilAttachmentRead (\| DepthStencilAttachmentWrite for particles) |
| Particles, written by compute, read by the vertex shader (storage, no vertex attributes), the indirect draws, and the counters' copy | (memory barrier) | ComputeShader / ShaderStorageWrite | VertexShader \| DrawIndirect \| Copy / ShaderStorageRead \| IndirectCommandRead \| TransferRead |
| Particle counters, copied, for the host | (memory barrier) | Copy / TransferWrite | Host / HostRead |

The two upload-target rows are SMAA's lookup textures (`SmaaPass`), copied once in the first anti-aliased frame from a
host-visible buffer the main thread filled before the render thread started. Every row is in use in some
configuration. With the default four samples a pixel, the standard `validate.sh` profiles (`reference.md`) reach every
row but those two upload rows and the depth row without a resolve; `--Engine:Renderer:MsaaSamples=1` reaches them. The
colour-target row is the upscale's source, which EASU samples below
render scale 1, and SMAA's scene, edges, and weights images. Barriers the table does not list are in their passes' files: the upscale's
discards to the colour attachment layout (`UpscalePass` and `SmaaPass`: Undefined to ColorAttachmentOptimal after
earlier frames' fragment reads), the screenshot's copy of the swapchain image (`ScreenshotPass`: ColorAttachmentOptimal to
TransferSrcOptimal and on, then Copy / TransferWrite to Host / HostRead), the UI's (`UiPass`), and the loading
screen's (`LoadingPass`). The particles' barriers before the compute passes (last frame's reads before this frame's
writes, the clear and fill before compute, spawn before simulate) are in `ParticlePass.RecordSimulate`.
The first mesh heap row is an execution dependency on earlier frames' reads of the heap (a write after read needs no
access mask, as the Khronos examples show) plus earlier copies' writes; it is what lets the upload reuse a heap range
freed in the same frame. Host writes to staging need no barrier: the queue submit makes them visible (host write
ordering guarantee), after `StagingBuffer.Flush` for non-coherent memory. The same holds for the buffers the main
thread writes each frame in one region per slot (the mesh draws, commands, lighting, and lights, the debug lines, the
particles' recipes): the render thread flushes the ranges it uses when the memory is not coherent, and no barrier
follows. Check each other row against the Khronos synchronization examples (sync2 section) and the spec before first use.

A source of `None` is correct only when nothing earlier in the queue (and no semaphore wait) touches the
resource. For the depth image, the source stage waits on the previous frame's depth writes, because the
same depth image is shared by frames in flight.
