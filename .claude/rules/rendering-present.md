---
paths:
  - "src/Itera.Engine/Windowing/**"
  - "src/Itera.Engine/Rendering/{SurfaceSource,VulkanSwapchain,SwapchainState,FrameImages,RenderScale,DynamicRenderScale}.cs"
  - "src/Itera.Engine/Rendering/EffectsBudget.cs"
  - "src/Itera.Engine/Rendering/{UpscalePass,SmaaPass,SmaaTextures,DebugLines,DebugLinePass}.cs"
  - "src/Itera.Engine/Rendering/{Screenshots,ScreenshotPass,ScreenshotEncoder}.cs"
  - "src/Itera.Engine/Rendering/Smaa/**"
  - "src/Itera.Engine/Rendering/GpuData/{SmaaConstants,UpscaleConstants,DebugLineVertex}.cs"
  - "src/Itera.Engine/Rendering/Shaders/{fsr-easu.frag,fsr-rcas.frag,ffx_a.glsl,ffx_fsr1.glsl,fullscreen.vert,debug-lines.frag,debug-lines.vert,mesh-wire.frag}"
  - "src/Itera.Engine/Rendering/Shaders/smaa*"
  - "tests/Itera.Engine.Tests/Rendering/{DynamicRenderScaleTests,SmaaPassTests,SmaaTexturesTests,UpscalePassTests,SwapchainStateTests,DebugLinesTests,ScreenshotTests}.cs"
---

## The window, the surface, scaling, and anti-aliasing

Related files: `rendering.md` (the core), `rendering-light.md` (the world pass that these follow), and
`docs/anti-aliasing.md` (the research behind SMAA before FSR 1).

### The window and the surface

- `WindowService` calls `Window.PrioritizeGlfw()` before it makes the window, so Silk.NET picks GLFW where SDL is
  installed too.
- Full screen covers the screen `Engine:Window:Screen` names, or the main one when it names none or one not connected.
  Silk.NET's full screen always takes the main screen, so `WindowService` then moves the window with `IWindow.Monitor`.
  On Wayland both requests reach the compositor before the first frame, so the window first shows on the named screen.
- `WindowService.Screens` holds the connected screens' names, the main one first, read with `RefreshRate`. Names match
  by ordinal comparison, and a repeated name finds the first screen of that name.
- The compiled shaders are cached in the data folder's `ShaderCache`.
  - A cache load sets the file's write time to now when it is over a day old, since last access times are not kept on
    many mounts.
  - Once the loop has started, `ShaderCompiler.StartPruning` deletes the cache's `.spv` files not written for
    `ShaderCompiler.CacheMaxAge` (30 days), on a thread-pool task. It touches no other file.
- Without a window (`Engine:Window:Mode=Offscreen`, `WindowService.Offscreen`) the surface is headless
  (`HeadlessSurfaceSource`, `VK_EXT_headless_surface`). `WindowSurfaceSource` wraps GLFW's for a window. Both are
  `ISurfaceSource`, in `SurfaceSource.cs`.
- Mesa's headless surface offers only UNORM formats. Where the device has `VK_KHR_swapchain_mutable_format`
  (`VulkanContext.SupportsMutableSwapchainFormat`; enabled where offered, asked of no device) the swapchain is made
  mutable and its images are viewed through their sRGB twin (`VulkanSwapchain.SrgbTwin`). `ImageFormat` is the twin, so
  every pass draws as it does into a window's, and screenshots match a window's.
- Nothing paces a headless present, so the render thread starts frames at most `Engine:Window:OffscreenFps` a second
  (`RenderThread`'s frame cap). The world uploads a share each frame, so a low cap slows loading.

### Render scale and FSR 1

A render scale below 1 draws the world, models, particles, and debug lines into a smaller image. AMD's FSR 1
(`UpscalePass`: EASU, then RCAS; `fsr-easu.frag` and `fsr-rcas.frag` over `ffx_a.glsl` and `ffx_fsr1.glsl`) upscales it
before the UI.

- `DynamicRenderScale` (`Engine:Renderer:DynamicRenderScaling`) sets the scale from the GPU's time per frame, so the
  frame rate holds the lower of `Engine:Renderer:DynamicScalingThreshold` and the screen's refresh rate
  (`WindowService.RefreshRate`). At scale 1 with time to spare it stays at 1.
- Each frame's GPU time is split into a fixed part, the passes in `DynamicRenderScale.FixedPasses`, and a rest that
  follows the square of the scale. `GpuTimings.LastFrame` gives each pass's time in the last frame. `GpuTimer.Collect`
  records the whole frame last, after its passes.
- A step is taken every few frames. It predicts the worst frame as the mean fixed part plus the largest pixel part.
  When that is over the aim, the scale drops at once to where it meets the aim. A frame slow in its fixed passes alone
  does not move the scale. When the mean is well under the aim, the scale rises by a small amount at most.
- With dynamic scaling off, the scale is the settings' Render scale (`RenderView.FixedRenderScale`, from
  `Engine:Renderer:RenderScale`). The console's `scale` sets one for the run (`scale auto` gives it back).

Effects give way before the scale (`EffectsBudget`, run-wide):

- `EffectsBudget.Share` is the share of cosmetic effects drawn, from `EffectsBudget.MinimumShare` to `Ceiling`, the
  settings' Effects detail.
- Over the aim, a step lowers the share by `EffectsBudget.LowerStep` and holds the scale. Only at the least share does
  the scale drop.
- Under the raise line, a step raises the scale first. At scale 1 it raises the share by `EffectsBudget.RaiseStep`.
- A change of either waits the settle frames. Off, overridden, or unmeasured, the share is its ceiling.
- Detail particle recipes thin by the share (`particles.md`), and a game's grass source may (`rendering-grass.md`).
- SMAA runs before FSR 1.

FSR 1's source and upscaled images are the screen's size, so a new render scale makes no new image. The picture fills
the source's top-left part, the drawn size. `EasuConstants.Create` works out EASU's constants on the CPU once a frame,
with the drawn size as the input viewport and the source's size as the input size. Near the picture's right and bottom
edges, `fsr-easu.frag` reads texels clamped to the picture in place of its gathers.

### Anti-aliasing

The world is drawn with `Engine:Renderer:MsaaSamples` samples a pixel. With one, SMAA 1x runs instead when
`Engine:Renderer:AntiAliasing` is on (`SmaaPass`; the console's `aa` for the run).

- The settings' Anti-aliasing (`AntiAliasMode`: Off, SMAA, MSAA 2x, 4x, 8x; `PlayerSettings.SetAntiAliasMode`) sets
  both.
- A change of the samples raises `PlayerSettings.SamplesChanged`. The render thread makes the swapchain again with
  the device idle. `MeshPass.OnSwapchainRecreated` makes its pipelines and multisampled images with the samples asked
  for (`RequestSamples`), the most the device offers up to them.
- SMAA's shaders are `smaa.glsl` (SMAA.hlsl unchanged, MIT), `smaa-common.glsl`, `smaa-edges.frag`,
  `smaa-weights.frag`, and `smaa-blend.frag`.
- With SMAA, the world, models, particles, and lines draw into a scene image of the drawn size.
  - The edges pass finds edges by luma in its sRGB-encoded values (a UNORM view).
  - The weights pass works out the blend weights with SMAA's lookup textures (`SmaaTextures`, `Smaa/AreaTex.bin` and
    `SearchTex.bin`, copied to the GPU in the first anti-aliased frame).
  - The blend pass blends in linear light into the swapchain image (scale 1) or FSR 1's source.
- The console's `aa edges` and `aa weights` show what SMAA found.
- `FrameImages` keeps SMAA's drawn-size images and the upscale's images until no frame in flight uses them.

### Debug views

- The console's `lines` draws the bodies' outlines, stand-ins, and collision cells (`DebugLines`, `DebugLinePass`).
- The console's `view` sets the run's debug views (`RenderView.DebugViews`; the switches `Wireframe`, `Levels`, and
  `Plain`; not saved).
  - `wire` draws the camera's world and model commands again as lines over what they drew (`MeshPass`'s wire
    pipelines, `mesh-wire.frag`). It is made only where the device draws triangles as lines
    (`VulkanContext.SupportsWireframe`).
  - `levels` tints each surface by its voxels' size. `mesh.vert` passes the draw's level, log2 of its voxel's
    centimetres, in bits 8 to 15 of the material output. `mesh.frag` mixes in a colour for each size. The 10, 20, 40,
    and 80 cm levels take the colours of the 8, 16, 32, and 64 cm ones.
  - `plain` draws every block of the world from its plain mesh, with the seams between levels open
    (`ViewSwitches.Plain`, read by `WorldMeshes` alone).

### Screenshots

`Screenshots` queues a screenshot, `ScreenshotPass` copies the swapchain image on the render thread, and
`ScreenshotEncoder` writes the PNG on a worker.

### Tests

`DynamicRenderScaleTests`, `SmaaPassTests`, `SmaaTexturesTests`, `UpscalePassTests`, `SwapchainStateTests`,
`DebugLinesTests`, and `ScreenshotTests`.
