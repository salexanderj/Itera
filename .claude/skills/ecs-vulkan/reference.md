# References and checked facts

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

## Checked facts about the current code

- `GpuLayout` (`tests/Shared/GpuLayout.cs`) compares a C# struct with the block layout in compiled SPIR-V (set and
  binding, or push constants). It checks array stride, member types, matrix order, and the 128-byte push constant limit.
  `GpuLayoutTests` covers a match, std430 offsets and stride, a row-major matrix, a type mismatch, a `bool` field, a
  missing binding, and scalar push constants.
- `probe/` re-checks the facts in `notes.md` marked "probe".
- `ViewConstants` is the push constant block of `view.glsl` (`layout(push_constant, scalar)`). It is 128 bytes: the
  fields' 116 plus three origin integers. Its on/off switches share one word (`ViewSwitches`). `ViewConstantsTests`
  compiles every shader that reads it and checks each with `GpuLayout.PushConstantDifferences`.
- `ViewConstants.Create` builds it on the main thread for each frame in `GameLoop`, from `RenderView` and the request's
  image size. It travels in `RenderFrame.View`, and the render thread pushes it. No entity data reaches the GPU through
  it.
- Where a shader reads a buffer at hand-written offsets, a test compares the shader's constants with the C# ones.
- The slot comes from `RenderFrameRequest.Slot`, valid on the main thread between `TryTakeRequest` and `Publish`.
  Frame systems run only when the render thread asks for a frame.
- Frame order: `FrameRenderer.BeginFrame` waits on the slot's fence before the ECS frame group (option A of
  `design.md`, choice 1). `DeviceRequirements` requires and enables `scalarBlockLayout`. Frame systems receive
  `FrameTime`, including `Alpha`.

### The mesh path

Characters and loose bodies are meshes, drawn by `WorldMeshes` (`Rendering/Meshes/WorldMeshes.Models.cs`) through
`MeshPass`.

- A frame system (`LooseBodyModelSystem`, and a game's own) adds each model to `ModelInstances`, a CPU array. Each entry
  is a `ModelDraw`: model handle, level, corner, turn, tint, and shadow mode. The array holds at most
  `ModelInstances.MaxModels`, and more are counted in `Dropped`.
- `GameLoop` clears the array before the frame group. After the group, it calls `WorldMeshes.Upload(slot, now, view,
  ModelInstances.Draws)` on the main thread.
- `Upload` meshes models in blocks of 32 voxels on `MeshWorkers` threads into ranges of the mesh heap. It writes the
  slot's draws.
- The draws in view, or casting into a shadow cascade, are grouped by model level with a counting sort. Each block of a
  level is one indexed indirect command, whose instances are that level's `MeshModelDraw` records. There are three
  lists: the camera's, the near cascade's, and the far cascade's.
- The records and commands go into host-visible buffers with a region per frame slot (`MeshPass.ModelDrawsOf`,
  `ModelCommandsOf`). The render thread flushes them when the memory is not coherent (`MeshPass.FlushSlot`).
- Meshes reach the GPU through the slot's staging region (`StagingWriter`) and one `vkCmdCopyBuffer` that the render
  thread records (`MeshPass.RecordUploads`). The copy count travels in `RenderFrame.MeshCopies`. `RenderFrame.Meshes`
  (a `MeshFrame`) carries the counts of draws and commands.
- `MeshPassTests` checks `MeshModelDraw` against `mesh.vert` and `mesh-shadow.vert` compiled with `MESH_MODEL` with
  `GpuLayout`. `LooseBodyModelSystemTests` checks that `ModelInstances.Add` allocates nothing.
  `WorldMeshesTests.An_upload_allocates_nothing_once_up_to_date` covers `Upload`.
- No stable GPU slots: the draws are written in full every frame, as `design.md` says.
