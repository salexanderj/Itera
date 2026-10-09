# Stride engine: reference notes

- Source: https://github.com/stride3d/stride (MIT licence). Website: https://www.stride3d.net.
- Read at `master` commit `2977b5bdd81400466b826921ea16254629cb1060` (2026-09-22). Paths below are
  relative to the repository root at that commit. To fetch one:
  `gh api "repos/stride3d/stride/contents/<path>?ref=2977b5bdd81400466b826921ea16254629cb1060" -H "Accept: application/vnd.github.raw"`

Stride is one of the few mature game engines written entirely in C#/.NET. These notes record how it solves
problems that Itera also has. Stride is a reference, not a template: Itera's design follows
`ARCHITECTURE.md`, and a Stride approach enters Itera only as a normal decision recorded there. Code copied
from Stride would have to keep its MIT copyright notice. No code has been copied.

## What Stride is

- A general engine with an editor (Game Studio) and an asset pipeline. Graphics backends: Direct3D 11,
  Direct3D 12, and Vulkan (through MoltenVK on macOS and iOS). It also targets Android.
- Entities hold components; `EntityProcessor`s process them. It is not an archetype ECS
  (`sources/engine/Stride.Engine/Engine/EntityProcessor.cs`).
- Its own maths library, and SDSL shaders (a superset of HLSL) compiled to SPIR-V by its own C# compiler
  (`sources/shaders/`).
- Bullet and Bepu physics; navigation meshes from DotRecast.
- No multiplayer networking and no runtime mod loading. Extensions are NuGet packages referenced at build time.
- After each build, `sources/core/Stride.Core.AssemblyProcessor/` rewrites the IL (with Mono.Cecil) for
  serialization and pooled delegates. Itera uses source generators and no IL rewriting.
- `sources/engine/Stride.Voxels/` is voxel cone-traced global illumination for triangle scenes, not voxel
  terrain.

| Area | Stride | Itera |
|---|---|---|
| Vulkan bindings | Vortice.Vulkan in the Vulkan backend; Silk.NET 2.23 for SDL, Shaderc, SPIRV-Cross, Direct3D | Silk.NET 2.23 only |
| Window and input | SDL2, forced onto X11 on Linux as a Wayland workaround (`sources/engine/Stride.Graphics/SDL/Window.cs`) | GLFW on native Wayland |
| Maths | Own types, converted with `Unsafe.As` at the Bepu boundary (`sources/engine/Stride.BepuPhysics/Stride.BepuPhysics/Definitions/BepuAndStrideExtensions.cs`) | `System.Numerics` |
| Threads | `RawTick` calls Update, then Draw, on one thread; parallel loops inside each (`sources/engine/Stride.Games/GameBase.cs`) | Main thread plus a render thread |
| GPU memory | One `vkAllocateMemory` per resource | Own block sub-allocator (planned) |
| Shaders | SDSL, own compiler | GLSL with Shaderc |
| Character | Bepu dynamic character | Kinematic controller (planned) |

## Vulkan and GPU code

- Stride has the same Vulkan minimums as Itera: 1.3, dynamic rendering, and synchronization2. It also
  requires timeline semaphores (Vulkan 1.2 core). It uses one queue, from queue family 0
  (`sources/engine/Stride.Graphics/Vulkan/GraphicsDevice.Vulkan.cs`).
- Three timeline semaphores serve as counters ("fence values") for frames, command lists, and copies. A
  freed resource is queued with the counter value after which the GPU no longer uses it
  (`NativeResourceCollector`). Descriptor pools and command buffers are reused the same way. Option for
  Itera: one timeline semaphore as the frame counter for deferred destruction and staging reuse. That adds
  `timelineSemaphore` to `DeviceRequirements`. Decide when the frame loop's deferred destruction next changes.
- Each buffer or texture gets its own `vkAllocateMemory`, from the first memory type with the right flags
  (`sources/engine/Stride.Graphics/Vulkan/GraphicsResource.Vulkan.cs`). Uploads take space from a 4 MB
  host-visible buffer in order. When it is full, a new buffer replaces it; a TODO says old ones should be
  recycled. Itera's own sub-allocator avoids both problems (`MaxMemoryAllocationCount` can be as low as 4096).
- A new buffer receives its data through a separate copy command buffer, submitted at once. The next
  frame's submit waits on the copy counter (`sources/engine/Stride.Graphics/Vulkan/Buffer.Vulkan.cs`).
- Binding is Direct3D 11 style: descriptor sets are allocated and written during recording, from pools
  reused by counter value (`sources/engine/Stride.Graphics/Vulkan/CommandList.Vulkan.cs`). Pipelines are
  created with `VkPipelineCache.Null`, so no pipeline cache is kept on disk
  (`sources/engine/Stride.Graphics/Vulkan/PipelineState.Vulkan.cs`). Itera has few pipelines and can keep
  long-lived descriptor sets instead.
- GPU timing uses a timestamp query pool read with 64-bit `vkGetQueryPoolResults`
  (`sources/engine/Stride.Graphics/Vulkan/QueryPool.Vulkan.cs`). Ticks per second are
  `1e9 / timestampPeriod` (`GraphicsDevice.Vulkan.cs`).
- The present mode is FIFO. When the game turns vsync off, Stride prefers Mailbox, then Immediate
  (`sources/engine/Stride.Graphics/Vulkan/SwapChainGraphicsPresenter.Vulkan.cs`).
- Rendering runs in phases: Collect, Extract, Prepare, Draw. Extract "should be mostly copies" so it does
  not hold up the simulation; Extract and Draw recording both run in parallel
  (`sources/engine/Stride.Rendering/Rendering/RenderSystem.cs`). In Itera, the frame build on the main
  thread has the Extract role.
- The global illumination voxels live in clipmaps: nested volumes centred on the camera, where each
  level's voxels are twice the size of the level inside it. Each level's origin snaps to multiples of its
  own voxel size, and by default only one level is updated per frame, in turn
  (`sources/engine/Stride.Voxels/Voxels/Voxelization/VoxelStorage/VoxelStorageClipmaps.cs`). Both rules
  fit Itera's coarse rings: snap each ring's centre to its own grid, and spread ring updates over frames.
- Stride's cone marching, which reads coarser data as a ray widens, was cut from these notes when the raymarcher left
  main on 2026-10-06; the branch `raymarcher` keeps it.
- Mouse lock uses SDL's relative mouse mode. On unlock, the cursor goes back to where it was captured
  (`sources/engine/Stride.Input/SDL/MouseSDL.cs`). Because Stride runs through XWayland on Linux, it says
  nothing about pointer lock on native Wayland. Itera still has to test GLFW's disabled cursor on Wayland.

## Physics and characters

- Stride uses BepuPhysics `2.5.0-beta.28`, the same prerelease line as Itera's beta.29
  (`sources/Directory.Packages.props`), and `SolveDescription(8, 1)`, the same as Itera
  (`sources/engine/Stride.BepuPhysics/Stride.BepuPhysics/BepuSimulation.cs`).
- The simulation has its own step accumulator and interpolates body poses between the previous and the
  current step. Itera's `Alpha` has the same purpose.
- Soft start: for the first second after the simulation starts, or after `ResetSoftStart()`, the solver
  runs four times the substeps so resting stacks settle.
- As read, the step cap is `stepCount < MaxStepPerFrame || MaxStepPerFrame != -1`. That is always true
  for the default of 3, so the cap does not limit steps. Itera's `FixedStepAccumulator` has tests for its
  cap.
- `DispatcherWrapper` (in `BepuSimulation.cs`) runs Bepu's workers on Stride's own thread pool, so physics
  and engine jobs share one set of threads. Itera runs Bepu's `ThreadDispatcher`, `VoxelWorkers`, the
  render thread, and the main thread. Check for too many busy threads when physics grows.
- Collision filtering: 32 layers and a matrix, checked in `AllowContactGeneration`
  (`sources/engine/Stride.BepuPhysics/Stride.BepuPhysics/Definitions/StrideNarrowPhaseCallbacks.cs`).
- Compound colliders always use `Compound`: `IsBig => false`, with an open `#warning` about when to use
  `BigCompound` (`sources/engine/Stride.BepuPhysics/Stride.BepuPhysics/Definitions/Colliders/CompoundCollider.cs`).
  Itera must make this choice for its per-chunk static compounds.
- Characters are dynamic bodies driven by a `CharacterMotionConstraint`, the design of the character
  controller in Bepu's demos (`Demos/Demos/Characters/` in https://github.com/bepu/bepuphysics2, same type
  names), with air control
  (`sources/engine/Stride.BepuPhysics/Stride.BepuPhysics/CharacterComponent.cs`,
  `sources/engine/Stride.BepuPhysics/Stride.BepuPhysics/Systems/Characters/CharacterControllers.cs`).
  Itera's character mover is its own and reads the voxels directly (`ARCHITECTURE.md`, "The character mover"). Revisit the choice only if the mover changes.

## Tests and CI

- Rendering tests compare screenshots with stored "gold" images (`tests/GPU-TESTING.md`,
  `sources/engine/Stride.Graphics.Regression/`). CI renders with Lavapipe, Mesa's CPU Vulkan driver.
  Stride builds its own Lavapipe from Mesa source (`.github/workflows/dep-lavapipe.yml`), so local and CI
  images match; gold images must come from that same build.
- The comparison takes the largest channel difference of each pixel. By default no pixel may differ by 3
  or more. A `thresholds.jsonc` file can allow a number of pixels per difference range, per image,
  platform, API, or device. PSNR is reported too
  (`sources/engine/Stride.Graphics.Regression/ImageThreshold.cs`, `ImageTester.cs`).
- Each test asserts zero validation errors and warnings. An attribute allows a named false positive
  (`sources/engine/Stride.Graphics.Regression/AllowGpuValidationErrorAttribute.cs`).
- Linux CI (`.github/workflows/test-linux-game.yml`) runs on ubuntu-24.04 with Xvfb, because Lavapipe
  offers `VK_KHR_swapchain` only with a display and Stride's device requires it. `VK_DRIVER_FILES` points
  the loader at the Lavapipe ICD, validation layers come from apt, and `DOTNET_DbgEnableMiniDump=1` makes
  a native crash leave a dump.
- macOS CI runs on a real GPU through MoltenVK on `macos-26` runners, with validation layers from Homebrew
  (`.github/workflows/test-macos-game.yml`).
- For Itera: Stride's CI shows that Lavapipe supports Vulkan 1.3, dynamic rendering, and
  synchronization2. Itera also requires `scalarBlockLayout`; that is not verified on Lavapipe. A test
  device without a swapchain would not need Xvfb. The minidump variables apply to the "native crashes
  cannot be caught" note in `ARCHITECTURE.md` section 4.

## macOS (`ARCHITECTURE.md` section 8)

- A bundled MoltenVK loaded directly, without the Vulkan loader, ignores validation layers. Stride uses
  Homebrew's loader when one is installed and falls back to the bundled MoltenVK
  (`sources/engine/Stride.Graphics/Vulkan/GraphicsAdapterFactory.Vulkan.cs`).
- Stride writes an ICD JSON file with an absolute library path and sets `VK_DRIVER_FILES`. On Linux and
  macOS, `Environment.SetEnvironmentVariable` does not change what libc `getenv` returns, and the loader
  reads `getenv`, so Stride also calls `setenv` (`sources/engine/Stride.Graphics/Vulkan/BundledMoltenVK.cs`).
- When the device offers `VK_KHR_portability_subset`, Stride passes the portability features back to
  `vkCreateDevice` as the device reported them (`GraphicsDevice.Vulkan.cs`).
- For Itera: `Vk.GetApi()` tries `libvulkan.dylib`, then `libMoltenVK.dylib`. Without an installed
  loader, a Debug run on a Mac probably has no validation. This is unverified; check it, and the
  portability features, on the first Mac run.

## Other notes

- Texture streaming aims at a 512 MB budget, loads at most 8 textures at once, and updates every 33 ms.
  Unused textures are unloaded after 8 s, and only when over budget
  (`sources/engine/Stride.Rendering/Streaming/StreamingManager.cs`). Itera unloads columns by distance;
  keeping them while under budget is an option.
- The profiler collects named scopes per thread (`sources/core/Stride.Core/Diagnostics/Profiler.cs`).
  `ChromeTracingProfileWriter.cs` in the same folder writes them as Chrome trace JSON, which Perfetto
  opens, with GPU times on their own row. This is one possible output for Itera's GPU timing.
- `Dispatcher.ForBatched` takes a struct job (`IBatchJob`), so it allocates nothing, and the calling thread
  helps while it waits (`sources/core/Stride.Core/Threading/Dispatcher.cs`). This fits Itera's
  zero-allocation rule without IL rewriting.
- The game loop (`sources/engine/Stride.Games/GameBase.cs`) has fixed steps off by default and counts time
  in integer ticks. A frame time within 1/64 of the target counts as the target (the XNA rule against
  vsync jitter). While the window is minimised or unfocused, updates drop to 15 per second.
- Audio has its own native layer with OpenAL, XAudio2, OpenSL ES, and AVFoundation backends, plus HRTF
  (`sources/engine/Stride.Audio/Native/`).
