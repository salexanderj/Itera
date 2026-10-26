---
paths:
  - "src/Itera.Engine/Rendering/{RenderPasses,RenderPasses.Picture}.cs"
  - "src/Itera.Engine/Rendering/Pipelines/**"
  - "samples/Itera.MeshSample/{Rendering,Shaders}/**"
  - "samples/Itera.MeshSample/AimHighlight.cs"
  - "samples/Itera.MeshSample/Systems/AimHighlightSystem.cs"
  - "tests/Itera.Engine.Tests/Rendering/RenderPassesTests.cs"
---

## Passes a game adds

A game adds its own GPU work as render passes (`IRenderPass`, in `RenderPasses.cs`). Related files: `rendering.md` (the
core), `rendering-views.md` (a game's image views), `rendering-light.md` (the main pass the stages sit in), and
`docs/rendering-extension.md` (the choices offered and why). The mesh sample's `AimHighlightPass` is the example
(its README).

### Registering

- A game registers a pass in its composition: `services.AddSingleton<IRenderPass, MyPass>()`.
- At most `GpuTimings.MostGamePasses` passes may exist, and each is named apart. Otherwise the run's "render passes" step
  stops the run.
- Each pass records at one stage of the frame (`RenderStage`).

### The stages

- `Compute`: after the mesh pass's light clusters and skinning, before the shadow maps, outside any rendering. No
  barrier follows it: a pass whose results a later stage reads records its own. It may read the mesh heap: the heap's
  upload barriers name the compute stage. The camera's blocks are `WorldMeshes.CameraCommands` (`rendering-world.md`).
- `Opaque`: inside the world's multisampled rendering (`MeshPass`'s colour format and samples; its depth is reversed
  and compared Greater), after the engine's opaque draws and before the sky. These surfaces write depth.
- `Transparent`: inside that rendering, after the sky and the engine's blended mesh assets. These surfaces blend and
  write no depth. They draw after the engine's blended ones and are not sorted among them.
- `Scene`: after the resolve, the particles, and the debug lines, on the drawn image (one sample a pixel, at the render
  scale's size) and the resolved depth, before SMAA and the upscale.
- `Screen`: after the upscale, on the swapchain image at the screen's size, colour only, before the UI.

For `Scene` and `Screen`, `RenderPasses` begins the rendering itself, after a barrier from the frame's earlier colour
and depth writes. The attachments are loaded and stored. The viewport (+y up) and scissor are set. Inside the world's
rendering they are the mesh pass's. The mesh pass sets its viewport, scissor, set, and constants again after the
`Opaque` stage.

### What the engine calls

- `Initialize(RenderPassSetup)` once, at the run's "render passes" step, after "mesh assets on the gpu" and before the
  render thread. It never runs in a run without graphics.
- `Write(slot, RenderPassFrame)` on the main thread after the frame systems, in each frame that has a match's world.
  It gets the frame's time and view. The pass writes its slot's region of its host-visible memory, write only, and
  flushes it when it records. The menu's and a match's first frames have none.
- `Record(RenderPassContext)` on the render thread at its stage, in each frame the loading screen does not cover.
  `Compute` is not recorded under it either. The context holds the command buffer, the frame slot, whether the frame
  is fresh, the view constants with the stage's size, the extent, and `MeshPass`'s set.
- `OnTargetsChanged` when the swapchain is made again (the samples or format may differ; the device is idle).
- The shader reloads (`IReloadsShaders`).
- `Dispose`, perhaps twice and perhaps without `Initialize`. A pass's constructor therefore makes no Vulkan object.
  After the engine gave up on a stuck device (`VulkanContext.IsAbandoned`), `Dispose` destroys nothing.

A game's passes draw into the frame alone, not into image views (`rendering-views.md`).

### Reading the picture

A `Screen` pass may read the picture under it, for a blur or a change of colour over the whole screen.

- The pass answers `ReadsPicture(slot)` (false by default) from what it wrote in that slot's `Write`. The engine asks
  only in frames whose passes were written, so never in the menu's.
- In a frame where one pass says yes, the engine copies the swapchain image into an image of the screen's size and the
  swapchain's format, before the stage's rendering (`RenderPasses.Picture.cs`). The swapchain image goes to
  `TransferSrcOptimal` and back. The picture goes from `Undefined` to `TransferDstOptimal`, then to
  `ShaderReadOnlyOptimal` for the fragment stage.
- `RenderPassContext.Picture` is a set that samples it: binding 0, a combined image sampler, linear, clamped to the
  edge. Its layout is `RenderPassSetup.PictureSetLayout`, given at the `Screen` stage alone. Otherwise its handle is
  0 and the pass draws nothing that needs it.
- The copy is timed with the stage's first pass. Nothing is copied while no pass reads it.
- The image is made on the render thread the first time it is asked for, and again for a new screen size. It is freed
  once no pass has read it for `RenderPasses.PictureFramesBeforeFree` frames (`FrameImages`, by the frame's fence
  slot).
- Where the surface's images cannot be copied from (`VulkanSwapchain.CanCopyImages`), or an image of their format
  cannot be copied into and sampled, a Warning says so at the "render passes" step and no pass gets one.

### What `RenderPassSetup` gives

- The device, the allocator, the shader compiler, the stage's colour and depth formats and samples, and `MeshPass`'s
  set and its layout. A pipeline whose set 0 is that set reads the look, the lighting, and the shadow maps.
- `CreateLayout`.
- `CreateTriangles`: a pipeline for the stage's targets with no vertex input, with depth compared Greater when tested.
  Its colour is blended as `ColorBlend` says: `None`, `Premultiplied`, `Alpha`, or `Additive`.
- `CreateCompute`.
- `CreateBufferSet`: a descriptor set of storage buffers (`RenderPassBufferSet`).
- `PipelineFactory` and `VulkanResultExtensions` are public for more.

### A game's shaders

- A game's shaders are in its own `Shaders/` folder. An item in its project copies them to `Shaders/Game/` beside the
  engine's. The item is `<None Include="Shaders\**" Link="Shaders\Game\%(RecursiveDir)%(Filename)%(Extension)"
  CopyToOutputDirectory="PreserveNewest" />`, after `<None Remove="Shaders\**" />`. The mesh sample's
  `Itera.MeshSample.csproj` has it.
- The compiler is given the names as `Game/NAME`.
- A game's shader includes the engine's: `#include "../view.glsl"`. `viewClip` projects as every pass does.
- The compiler reads the `Game/` paths from `Engine:Renderer:GameShaderSourceDirectory` when it is set (the game's
  folder in the repository, for `shaders reload`; `game.py start` gives it). Otherwise it reads `Shaders/Game/` beside
  the executable. It does so also while `ShaderSourceDirectory` points the engine's at the repository
  (`ShaderSource`, `ShaderRoots`).

### Timing

Each pass is timed with its own pair of queries. Their sum is `GpuPass.Game`. The time of the `Compute`, `Opaque`, and
`Transparent` stages is also part of the world's, `Voxels`. Each pass has a Debug line and an entry in the control
port's `gpu` reply (`Game NAME`).

### Tests

`RenderPassesTests`.
