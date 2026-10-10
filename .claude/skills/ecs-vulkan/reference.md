# References and audit record

## Sources

Vulkan specification and guides:

- Command buffer lifecycle (when a command buffer becomes invalid, `SIMULTANEOUS_USE`):
  https://docs.vulkan.org/spec/latest/chapters/cmdbuffers.html
- `vkUpdateDescriptorSets` (updates invalidate command buffers that bind the set):
  https://docs.vulkan.org/refpages/latest/refpages/source/vkUpdateDescriptorSets.html
- Offset and stride assignment (scalar, base, and extended alignment):
  https://docs.vulkan.org/spec/latest/chapters/interfaces.html
- `vkCmdUpdateBuffer` limits: https://docs.vulkan.org/refpages/latest/refpages/source/vkCmdUpdateBuffer.html
- Vulkan Guide, common pitfalls (record command buffers every frame):
  https://docs.vulkan.org/guide/latest/common_pitfalls.html
- Vulkan Samples, command buffer usage (pool resets, `ONE_TIME_SUBMIT`, avoid `SIMULTANEOUS_USE`):
  https://docs.vulkan.org/samples/latest/samples/performance/command_buffer_usage/README.html
- NVIDIA, Vulkan Dos and Don'ts: https://developer.nvidia.com/blog/vulkan-dos-donts

MoltenVK (the version Silk.NET.MoltenVK.Native 2.23.0 ships):

- Device features and limits: https://github.com/KhronosGroup/MoltenVK/blob/v1.4.0/MoltenVK/MoltenVK/GPUObjects/MVKDevice.mm
- Extensions and the OS versions they need: https://github.com/KhronosGroup/MoltenVK/blob/v1.4.0/MoltenVK/MoltenVK/Layers/MVKExtensions.def
- The package's nuspec names commit `94605142`, which is not in the upstream repository; the dylib reports
  version 1.4.0, so the `v1.4.0` tag was read.

.NET:

- `StructLayoutAttribute` (sequential layout for blittable and non-blittable types):
  https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.structlayoutattribute
- Auto layout when a non-blittable field meets an explicit-layout field: https://github.com/dotnet/runtime/issues/44579
- `Pack` not honoured for managed layout: https://github.com/dotnet/runtime/issues/12342

Arch: see the `ecs` skill's `reference.md`. Chunk and query source lines are in `notes.md`.

Other engines:

- Bevy render stages and the extract step: https://bevy-cheatbook.github.io/gpu/stages.html
- Unity Entities Graphics changelog, 0.4.0 (Hybrid Renderer V2): "GPU persistent data model ... Use
  `chunk.DidChange<T>` to delta update only changed data."
  https://github.com/needle-mirror/com.unity.entities.graphics/blob/master/CHANGELOG.md

Local tools: `vulkaninfo` (features, limits, memory types of each GPU), and `probe.sh`.

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
