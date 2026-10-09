---
name: vulkan
description: Rules and a validation check for any Vulkan work in Itera. Use before reading, writing, reviewing, or debugging code under src/Itera.Engine/Rendering/, or any code that touches Silk.NET.Vulkan, Vk, a swapchain, command buffers, barriers, semaphores, fences, descriptors, pipelines, buffers, images, device memory, or shaders. Also use when a log shows a "[Vulkan]" validation message.
---

# Vulkan in Itera

Vulkan does not check arguments at run time. A wrong stage mask, a freed pointer, or a missing `SType`
gives undefined behaviour that may work on this machine (AMD RADV) and fail on NVIDIA, Intel, or
MoltenVK. The validation layer is the only safety net, so every change ends with the validation run
in step 5.

## Workflow for every Vulkan change

1. Read the files you will change in full, plus `VulkanContext.cs` for the handles they borrow.
   Read `ARCHITECTURE.md` first if the change adds a new object type or changes init order.
2. Look up each Vulkan function and struct you use in the spec (links in `reference.md`). Check
   its valid usage rules (VUIDs), not only its signature. Do not write a flag or stage mask from memory.
3. Write the code against the rules below. For synchronization, read `synchronization.md` first.
   For anything that uses Silk.NET beyond plain calls (extensions, callbacks, strings, `Vk` state),
   read `silk-net.md` first.
4. `dotnet build` (zero warnings) and `dotnet csharpier check .`.
5. Run `.claude/skills/vulkan/validate.sh` (both standard profiles in `reference.md`, "Standard validation profiles"). It runs the Debug game (which turns on core,
   synchronization, and best-practices validation in `VulkanContext`), checks the log says all three
   were on, and fails on any `[Vulkan]` Warning or Error line. A change is not done until it passes.
   Resize the window during the run when the change touches the swapchain or frame loop (pass a
   longer duration, e.g. `validate.sh 35`). `validate.sh --offscreen [seconds] [arguments]` runs without a window
   (`Engine:Window:Mode=Offscreen`), which checks every pass but the window's own presentation. The default run is 25 s: a Debug build shows the loading screen for about
   3.5 s, and the world, models, fade, and upscale are drawn only after it; a run shorter than about 10 s checks only the
   loading pass.
6. In the summary to the user, state which validation checks ran and what they reported.

## Rules

### Memory and pointers (C# side)

- Set `SType` on every Vulkan struct that has one. Silk.NET does not set it for you in object
  initialisers. The only exception is the `Chain` API, which sets it itself.
- Set `PNext` only to the address of a local or a pinned object that lives until the Vulkan call
  returns. Never store a pointer to a local in a field.
- Take the address of locals only inside the method that owns them. `&local` for a struct that a
  lambda or `async` method captures is a bug.
- Pin managed arrays with `fixed` for the whole call. `fixed` on an empty array gives a null pointer,
  which Vulkan accepts only when the matching count is 0.
- Do not `stackalloc` inside a loop (CA2014). Allocate once before the loop.
- Every `SilkMarshal.StringToPtr`, `StringArrayToPtr`, or `Allocate` is paired with
  `SilkMarshal.Free` in a `finally` block in the same method.
- Read Vulkan strings (`byte*` fields such as `DeviceName`, `ExtensionName`, `PMessage`) with
  `Marshal.PtrToStringUTF8`. Vulkan strings are UTF-8.
- Keep any managed delegate passed to Vulkan (debug callback, allocation callbacks) in a field for
  the life of the Vulkan object that uses it.
- A callback that native code calls must never throw. Wrap the body in `try`/`catch` and return
  `Vk.False`. It may run on any thread, including driver threads.

### Results and errors

- Check the `Result` of every call that returns one: `.ThrowIfFailed("vkFunctionName")`.
- Count-then-fill enumerations (`Enumerate*`, `Get*Formats`, `GetSwapchainImages`) can return
  `Result.Incomplete`. Check both calls and use the count from the second call.
- `vkAcquireNextImageKHR` and `vkQueuePresentKHR`: `Success` and `SuboptimalKhr` are success.
  After acquire returns `SuboptimalKhr` the semaphore is signalled, so the frame must still be
  submitted and presented. `ErrorOutOfDateKhr` means recreate and do not submit.
- `ErrorDeviceLost` is fatal for the device. Do not retry; let it reach the loop's Critical handler.
- `vkDeviceWaitIdle` returns a `Result`; `WaitIdle()` must not hide `ErrorDeviceLost` in the frame loop.

### Object lifetime

- Every `Create*`/`Allocate*` has a matching `Destroy*`/`Free*` in the owning class's `Dispose`.
  `Dispose` is idempotent and checks `Handle != 0` before each destroy.
- Destroy in reverse creation order. Children before parents: views before images, images and
  buffers before their memory, everything device-level before the device, surface and debug
  messenger before the instance.
- Never destroy or reset an object that a submitted command buffer still uses. Wait on that
  frame's fence (preferred) or `WaitIdle()` (for swapchain recreate and shutdown only).
- Objects that live per frame in flight are indexed by `FrameSync.CurrentFrame`
  (`FrameRenderer.CurrentSlot`). Objects tied to a swapchain image are indexed by the acquired image
  index. Do not mix the two.
- Do not dispose `Vk` (see `silk-net.md`). Disposing Silk extension objects (`KhrSwapchain` etc.)
  is safe.

### Device setup

- Itera requires Vulkan 1.3 on the loader and the device (decided 2026-09-22). There is no
  1.2 fallback and no KHR-extension path for anything that is core in 1.3.
- Every device extension and feature the engine uses is listed in `DeviceRequirements.cs`: add it to
  `Extensions` or as one `Feature(...)` line in `VisitFeatures`. Device selection then checks it
  (`EnumerateDeviceExtensionProperties`, `GetPhysicalDeviceFeatures2`) and device creation enables it.
  Enabling a feature the device does not report fails `vkCreateDevice`. Features from 1.1 or 1.4, or
  from an extension, need their struct added to the chain in both `DescribeCandidate` and
  `CreateLogicalDevice`.
- Queue family indices come from `VulkanContext`. When graphics and present families differ, images
  and buffers shared between them need `SharingMode.Concurrent` or explicit ownership transfers.
- Read the device's values (`VulkanContext.Limits`, `MemoryProperties`, `MaxBufferSize`, the queue families,
  `GraphicsTimestampValidBits`, `DeviceName`) in `Initialize` or later, never in a constructor: DI builds the renderer
  before the device is selected, and these throw until then (`SystemOrderTests` resolves the real `GameLoop`, so a
  constructor read fails it). A constructor read of the storage buffer range (as zeros, before the guard) drew every
  frame at render scale 0.05 for a day. The handles (`Device`, the queues) stay null until then and do not throw:
  a Vulkan call refuses a null handle, and `Dispose` methods read `Device` in runs that never reached the Vulkan step.

### Swapchain

- Choose every swapchain parameter from what the surface reports: format, colour space, present
  mode, image count (`MaxImageCount == 0` means no limit), extent (`CurrentExtent.Width ==
  uint.MaxValue` means the app chooses), pre-transform, and composite alpha
  (`SupportedCompositeAlpha`).
- Add an image usage flag only if `SupportedUsageFlags` has it.
- A zero-sized extent (minimised window) means no swapchain. Skip rendering; do not create one.
- Recreate: `WaitIdle()`, destroy views, create the new swapchain with the old one as
  `OldSwapchain`, destroy the old one, rebuild per-image objects (`VulkanSwapchain.Recreate`, then
  `FrameSync.OnSwapchainRecreated`).

### Commands and rendering

- `FrameRenderer` owns the frame: fence wait, acquire, swapchain barriers, submit, present, and
  recreate. A pass (`MeshPass` today) only records into the command buffer and target it is given.
- Create pipelines with `Pipelines/PipelineFactory` (dynamic rendering, dynamic viewport and scissor). The
  viewport has a negative height (`Y = height`, `Height = -height`) so +y in clip space is up. A pass whose
  pipeline depends on the swapchain format recreates it in `OnSwapchainRecreated` (render thread, device idle).
- Time a pass with `GpuTimer.Begin`/`End` around it (`GpuPass` in `Diagnostics/GpuTimings.cs` names it;
  add a value and raise `GpuTimings.PassCount` for a new pass).
- Threads: `RenderThread` runs `FrameRenderer`. After `RenderThread.Start`, only the render thread uses
  the queues, the swapchain, `FrameSync`, and the command pools, and the main thread makes no Vulkan call
  until `RenderThread.Stop` has ended the thread, with one exception on the memory of the slot it builds:
  `StagingBuffer.Flush` (`vkFlushMappedMemoryRanges`). The spec gives it no externally synchronized
  parameter, and it returns early on coherent memory, so `validate.sh` on this machine never runs it. Add no
  other. Vulkan objects the main thread needs (buffers,
  images, pipelines) are created before the render thread starts, or by the render thread. Data reaches
  the render thread only through `FrameExchange` (`RenderFrame`). The render thread never calls GLFW:
  the main thread passes the framebuffer size (`FrameRenderer.SetFramebufferSize`). The validation
  layer's thread-safety check (on by default) reports two threads using an externally synchronized
  object at once, so `validate.sh` covers this rule.
- Rendering uses dynamic rendering (`Vk.CmdBeginRendering`), never `RenderPass`/`Framebuffer` objects.
- Barriers use `CmdPipelineBarrier2` and submits use `QueueSubmit2` (synchronization2). Never add the
  older `CmdPipelineBarrier` or `QueueSubmit`. Recipes are in `synchronization.md`.
- Record barriers for every image layout change and every hazard between commands. The previous
  layout must be the real one; use `Undefined` only when the old contents may be discarded.
- Per-frame command buffers come from the frame slot's pool in `FrameSync`; `BeginFrame` resets the
  pool after the fence wait. Use `CommandBufferUsageFlags.OneTimeSubmitBit`.
- Per-frame work must not allocate managed memory or log above Trace.

### Shaders and pipelines

- Shader sources are files in `src/Itera.Engine/Rendering/Shaders/` (`.vert`, `.frag`, `.comp`; `.glsl`
  for files that are only included), copied to `Shaders/` in the output. `ShaderCompiler.Compile(path)`
  expands `#include "file"` and returns SPIR-V for Vulkan 1.3 as a `byte[]`; `CompileSource` compiles
  inline text (tests). Check `Length % 4 == 0` before `CreateShaderModule`; `CodeSize` is in bytes and
  `PCode` is `uint*` (pin the array and cast).
- A change to the compiler's options (target, optimisation level) must also change
  `ShaderCompiler.CompilerIdentity`, which is part of the cache key. Bump `CacheFormat` if unsure.
- Push constant ranges, descriptor set layouts, and vertex input must match the shader exactly.
  The validation layer reports mismatches; do not ignore them.
- A buffer or push constant block that mirrors a C# struct follows the `ecs-vulkan` skill:
  `layout(scalar)` and a `GpuLayout` test.
- Pipelines use dynamic viewport and scissor so they survive swapchain resize.

### Memory allocation

- Itera has its own block sub-allocator in `Rendering/Memory/` (decided 2026-09-22; built
  2026-09-23). No VMA binding. Create buffers with `GpuMemoryAllocator.CreateBuffer(size, usage,
  MemoryUsage, name)`; never call `vkAllocateMemory` elsewhere. `MaxMemoryAllocationCount` can be as low
  as 4096.
- The allocator picks the memory type (`MemoryTypeSelector`: `DeviceLocal`, `Upload`, `Readback`, with a
  fallback to the next acceptable type when a heap is full), takes 64 MB blocks (an eighth of a smaller
  heap), keeps buffers and optimal-tiling images in separate blocks so `BufferImageGranularity` never
  applies, and gives resources of half a block or more, or that the driver asks for, a dedicated
  allocation. It never picks a type that needs a device feature (AMD device-coherent, protected, lazily
  allocated).
- Host-visible memory is mapped once and stays mapped (`GpuBuffer.Mapped`). For non-coherent memory, call
  `GpuBuffer.Flush` after CPU writes and `Invalidate` before CPU reads; the allocator rounds to
  `NonCoherentAtomSize`. Respect `MinStorageBufferOffsetAlignment` and `MinUniformBufferOffsetAlignment`
  when binding a range at an offset; `VulkanContext.Limits` has them.
- Staging for copies: `StagingBuffer`, one region per frame slot, written by the main thread for
  `request.Slot` only.
- Do not write to mapped memory the GPU is still reading. Use per-frame-in-flight copies.
- Buffers filled from entity data also follow the `ecs-vulkan` skill (mapping, write-only access, and
  the frame order).

## Silk.NET call style

- Prefer the `out` overloads for single handles: `vk.CreateFence(device, &info, null, out var fence)`.
- Pass `null` for `AllocationCallbacks*`.
- Handles are structs; the null handle is `default` and its `Handle` is 0.
- Flag enums: combine with `|`; test with `(flags & X) != 0` in hot paths.
- The project uses `ThrowIfFailed` from `VulkanResultExtensions.cs` and `VulkanException`.

## Enforcement

`.claude/hooks/skill_guard.py` runs before every Edit, Write, NotebookEdit, and Bash call. It
denies file writes under `src/Itera.Engine/Rendering/`, writes to `.cs` files that use or add
`Silk.NET.Vulkan`, and Bash commands that contain `Itera.Engine/Rendering`, until the session
transcript shows this skill was loaded (a `Skill` call for `vulkan`, or a typed `/vulkan`, after the
last `/clear`). A denied call names the fix: load this skill, then retry. The hook does not see Bash
commands that write Vulkan code elsewhere without naming the Rendering folder. The same hook guards ECS
work for the `ecs` skill and ECS-to-GPU work for the `ecs-vulkan` skill.

## Files in this skill

- `silk-net.md`: Silk.NET 2.23 behaviour checked against its source.
- `synchronization.md`: the frame loop, semaphore and fence rules, and barrier recipes.
- `reference.md`: spec and guide links, plus the audit record of the current code.
- `validate.sh`: the validation run for step 5.
