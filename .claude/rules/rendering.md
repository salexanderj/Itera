---
paths:
  - "src/Itera.Engine/Rendering/**"
  - "tests/Itera.Engine.Tests/Rendering/**"
---

## Rendering

The engine opens a Vulkan window and draws the resident voxels, the voxel models, and the mesh assets as triangle meshes
through a raster pipeline. Its decisions are in `docs/rendering-redesign.md`, what a game can change in
`docs/rendering-extension.md`, and how the frame is put together in `ARCHITECTURE.md`. The Vulkan rules are the `vulkan`
skill's, and the rules for data that shaders read are the `ecs-vulkan` skill's.

### The rendering rules files

This file is the core. The sub-areas have their own files, each loaded with its own code:

- `rendering-world.md`: `WorldMeshes`, the mesher, the heap, the levels, the stitched seams, and the voxel models.
- `rendering-light.md`: the frame's view, the shadow cascades, the main pass, the sky field, and the point lights.
- `rendering-look.md`: the look, the sun, and the atmosphere.
- `rendering-materials.md`: the world's materials, the texture sets, the tint map, and baking.
- `rendering-mesh-assets.md`: drawing the mesh assets, their skinning, and blended materials. The assets themselves are
  in `mesh-assets.md`.
- `rendering-passes.md`: the render passes a game adds.
- `rendering-grass.md`: the grass a game may opt into.
- `rendering-views.md`: image views.
- `rendering-present.md`: the window, the surface, render scale, anti-aliasing, and the debug views.
- `particles.md`: particles.

### The camera's lens

`CameraLens` is the active camera's component. `CameraExtractSystem` passes it on in `CameraView`.

- A perspective lens has a vertical field of view (the player's setting draws in its place) from its near plane on
  (`ViewConstants.Near`). It has no far plane: the world reaches the view distance (`Engine:Renderer:ViewDistanceMetres`).
- An orthographic lens (`CameraLens.Orthographic`, `ViewSwitches.Orthographic`) is an image `HeightMetres` high, its
  width by the aspect ratio, of parallel rays along the view. It is round the camera's position, which is the middle
  of what it shows. It runs from its near to its far along the view (`ViewConstants.Near` and `Far`; negative is behind
  the position).
- Every pass projects through `view.glsl`'s `viewClip`. For an orthographic view, w is 1 and the depth falls evenly
  from 1 at the near end to 0 at the far one. `viewDepthOf` reads a stored depth back. `ViewConstants.Clip` and
  `DepthOf` mirror both on the CPU.
- A shader asks `view.glsl` for what else differs: the sky's ray (`viewRay`, the view's direction at every pixel), the
  ray that meets a point (`viewRayTo`: the fog's sky colour behind it, what faces the viewer), and the depth that
  chooses a shadow cascade (`viewCascadeDepth`). A new pass does the same, never its own projection.
- An orthographic view is measured from its middle as a perspective view is from its eye. This holds for its fog, the
  terrain's levels, the lights gathered, its light clusters (even slices across the range round it), and its two
  shadow cascades. The cascades lie round it both ways along the view, within its box, chosen by how far before or
  behind it a point lies (`MeshView`'s slices; `ShadowCascades.Fit` with a box's corners).
- `MeshView` culls to its box (parallel side planes). It sorts nearest first (`NearFirstKey`: the squared distance from
  the eye, or an orthographic view's depth past its near end). The world's camera commands sort that way. The blended
  mesh assets sort back to front.
- The particles read its depth image and are culled to its box (`ParticleConstants`' near, far, and projection).
- The console's run-only `lens ortho HEIGHT [NEAR FAR]` draws the camera through an orthographic lens
  (`RenderView.LensOverride` and `DrawnCamera`; by default the view distance each way). `lens off` draws through its
  own.
- The console's `viewpoint x,y,z,yaw,pitch` draws the frames from that place in the world for the match
  (`RenderView.CameraOverride`, a `CameraPlace`), through the camera's lens. `RenderView.Camera` stays the camera's
  own, so the listener and the streaming follow it. `ShiftCamera` moves the override with the origin, and
  `ResetAfterMatch` clears it.
- `RenderView.CreateView` makes the frame's view constants. `RenderView.TryFrameView` gives them to the frame systems
  (`rendering-mesh-assets.md`).

### Under a moved origin

Everything the renderer draws is local to the match's origin (`WorldOrigin`, CLAUDE.md's Units), as the camera is.

- The frame's view constants carry the origin in absolute centimetres (`ViewConstants.OriginX` to `OriginZ`,
  `viewOrigin` in `view.glsl`). `GameLoop` sets them from the match's (zero without one) and `RenderViewPass` sets
  them for each image view.
- The camera a frame system last set (`RenderView.Camera`) moves with the origin, for what reads it before the next
  frame.
- A shader that draws by a surface's place in the world takes the origin back. `mesh.frag`'s whole centimetres (the
  surfaces' noise and strata) are the local ones plus the origin. The tint map is read from those
  (`meshTintAt(whole, part)`, its corner taken off in integers). The texture sets' tiles take the origin's remainder
  by the tile's width in integers (`tilePoint`).
- On the CPU, a position kept across frames is absolute or is moved with the origin. The world's meshes are in
  `rendering-world.md`, the sky field and the cascades in `rendering-light.md`.
- The console's run-only `origin move` checks it. The particles and the sound follow the origin too (`particles.md`,
  `audio.md`).

### Conventions

- Shader sources are files in `src/Itera.Engine/Rendering/Shaders/` (`.vert`, `.frag`, `.comp`, and `.glsl` for
  include-only files), copied to `Shaders/` in the output and compiled with `ShaderCompiler.Compile`. The `Game/`
  folder of the shader tree is the game's (`ShaderCompiler.GameFolder`). The engine's shaders use no folder of that name.
- Structs that shaders read use GLSL `layout(scalar)` and have a `GpuLayout` test. Matrices go to the GPU unchanged and
  are used as `M * v` in GLSL.
- The view's push constants (`ViewConstants`) are 128 bytes, all the spec guarantees. The last three integers are the
  match's origin (`viewOrigin` in `view.glsl`).
- The view's on/off switches share one word's bits (`ViewSwitches`, `viewSwitch` in `view.glsl`, which
  `ViewConstantsTests` checks). A new switch takes a bit there. A new value goes to a per-slot record, as the look's
  does (`rendering-look.md`).
- A pass implements `IReloadsShaders` and makes its pipelines again on a shader reload.
- Per-frame code allocates no managed memory and logs only at Trace.

### Requirements

- Vulkan 1.3 is required on the loader and the device. There is no 1.2 fallback.
- `DeviceRequirements` requires `dynamicRendering`, `synchronization2`, `scalarBlockLayout`, and `multiDrawIndirect` with
  `drawIndirectFirstInstance` (the world's indirect draws, whose first instance names each block's draw).
- It also requires `shaderSampledImageArrayDynamicIndexing` with `shaderSampledImageArrayNonUniformIndexing` (the mesh
  assets' texture array, indexed per pixel by material), and `D32_SFLOAT` with optimal tiling as a depth attachment.
- It enables `fillModeNonSolid` where the device has it (the wireframe debug view, `VulkanContext.SupportsWireframe`)
  and asks it of no device.
- Rendering uses dynamic rendering, with no render pass or framebuffer objects. Barriers use `CmdPipelineBarrier2` and
  submits use `QueueSubmit2`.

### Files in `src/Itera.Engine`

- `Rendering/`:
  - Vulkan bootstrap: `VulkanContext`, `DeviceRequirements`.
  - The frame: `FrameRenderer`, `RenderThread`, `FrameExchange`, `FrameSync`, `GpuTimer`.
  - The passes: `UiPass`, `LoadingPass`, `ParticlePass`, `DepthTarget`, `ModelInstances`, `MeshInstances`.
  - Their sub-areas' files are named in the rules files above.
- `Rendering/Memory/`: GPU memory (own block sub-allocator, `GpuBuffer`, `GpuImage`, per-slot `StagingBuffer`, and
  `GpuRangeHeap`, the mesh heap's ranges).
- `Rendering/Pipelines/`, `Rendering/GpuData/` (structs that shaders read), and `Rendering/Shaders/` (GLSL files, the
  shader compiler, and the reloads: `ShaderReloads`, `IReloadsShaders`, which every pass implements).
- `Rendering/Voxels/`: the palette and its looks (`IVoxelPalette`, `VoxelLook`), the tint map (`IVoxelTintMap`),
  `VoxelTintSampler`, and `StagingWriter`.
- `Rendering/Materials/`, `Rendering/Meshes/`, `Rendering/Smaa/`: see their rules files.
- `src/Itera.Engine/Meshing/`: the CPU mesher (`SurfaceNets`, `MeshBuffers`).
- `src/Itera.Engine/Windowing/`: `WindowService`.

### Tests

`GpuLayoutTests`, `ViewConstantsTests`, `VulkanContextTests`,
`DeviceChoiceTests`, `RenderThreadTests`, `FrameExchangeTests`, `ShaderCompilerTests`, `ShaderReloadTests`,
`GpuTimerParseTests`, `UiPassTests`, `UiBandsTests`, `LoadingPassTests`, `GpuMemoryAllocatorTests`, `BlockSuballocatorTests`,
`PipelineCacheFileTests`, and `MemoryTypeSelectorTests`.
