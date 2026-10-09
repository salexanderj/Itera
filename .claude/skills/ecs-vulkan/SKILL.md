---
name: ecs-vulkan
description: Rules and checks for moving ECS data to the GPU in Itera (Arch 2.1.0 component data into Vulkan buffers through Silk.NET 2.23). Use before writing or reviewing code that copies component data into mapped or staging memory, defines a C# struct that a shader reads (instance data, push constants, uniform or storage buffer contents), records draws or dispatches whose counts or data come from entities, maps GPU results back to entities, or changes code under src/Itera.Engine/Rendering/GpuData/. Load the ecs and vulkan skills as well.
---

# ECS data on the GPU in Itera

This skill covers the code where Arch and Vulkan meet: copying component data into GPU memory, C# structs
that shaders read, and the command buffers that draw entities. The `ecs` and `vulkan` skills still apply in
full; load both. This file does not repeat them. Rules marked "probe" are checked by `probe/Program.cs`.
Device facts marked "RADV" come from `vulkaninfo` on this machine and "MoltenVK" from the MoltenVK 1.4.0
source that Silk.NET 2.23 ships (`notes.md` has the details and line numbers).

What the research found, in short:

- The GPU cannot read Arch chunk memory in place. Every upload is a copy from component arrays into
  memory the GPU can read.
- A C# struct and a GLSL block agree when the shader uses `layout(scalar)` and the struct uses only the
  field types listed below. Scalar layout was chosen on 2026-09-22. A layout test checks each struct
  against the compiled SPIR-V.
- Command buffers recorded once per entity, chunk, or archetype and reused do not fit Arch: entities move
  between chunks and their number changes every tick. The frame's command buffer is recorded again every
  frame. The GPU buffers are what persist, and draws read their counts and instance data from them.

## Workflow for every change

1. Load the `ecs` and `vulkan` skills too. Read the files you will change in full, plus `GameLoop.Loop` (the
   frame order), `FrameRenderer`, `FrameSync`, and `EcsWorld`. Read `design.md` before adding a GPU buffer that entity data
   fills. Read `notes.md` before relying on a layout, memory, or device fact that is not stated below.
2. For each struct a shader reads, write down the GLSL block that reads it (set and binding, or push
   constants) and the memory it lives in (one copy per frame in flight, or persistent).
3. For each upload, write down which system or renderer method writes it, when in the frame it runs,
   which frame slot it writes, and what limits the number of entries.
4. Write the code against the rules below.
5. Add a layout test for each struct a shader reads, in `tests/Itera.Engine.Tests/Rendering/`. Compile the
   real shader file with `ShaderCompiler.Compile` (or inline GLSL with `CompileSource`), then `Assert.Empty(GpuLayout.ArrayElementDifferences<T>(spirv, set, binding))`
   or `Assert.Empty(GpuLayout.PushConstantDifferences<T>(spirv))`. `GpuLayoutTests` shows the pattern.
   Code that writes GPU data every frame gets a zero-allocation test (`SystemAllocation` for systems; see
   the `ecs` skill).
6. `dotnet build` (zero warnings), `dotnet test`, `dotnet csharpier check .`, then
   `.claude/skills/vulkan/validate.sh` for any change to Vulkan code.
7. After changing the .NET SDK, Arch, or Silk.NET version, run `.claude/skills/ecs-vulkan/probe.sh`
   (without bash: `dotnet run -c Release --project .claude/skills/ecs-vulkan/probe`). A `CHANGED` line
   means a rule below may be wrong: re-check that fact and update `notes.md` and this file first.
8. In the summary to the user, state the layout test results, the allocation results, and what `validate.sh`
   reported.

## Rules

### Who touches what

- Components hold no Vulkan handles, pointers, or mapped addresses. They hold small values: a mesh or
  material index, or a GPU slot index (see `design.md`).
- ECS systems make no Vulkan calls. The renderer makes no structural changes to the world. Data moves one
  way: fixed systems (simulation), then frame systems (presentation), then renderer-owned memory, then the GPU.
- Fixed systems never write GPU data. They run zero to eight times per frame.
- Structs that shaders read live in `src/Itera.Engine/Rendering/GpuData/` (namespace
  `Itera.Engine.Rendering.GpuData`). Create the folder with the first such type. The code that fills
  renderer-owned memory from entities lives there too, or uses types from there (for example a frame
  system in `src/Itera.Engine/Systems/` or a game's `Systems/`; `design.md`, choice 2). The hook requires this skill for both.
- All reads of the world for rendering happen in one step per frame, on the main thread, after the ECS
  frame group. The render thread never reads the world: it gets `RenderFrame` through `FrameExchange` and
  whatever the main thread wrote into the slot's memory.

### Chunk memory

- A chunk stores each component type in its own managed array, created with `new T[capacity]`
  (`ArrayRegistry`, `src/Arch/Core/ComponentRegistry.cs:374`). The arrays are not pinned, and a compacting
  GC moves them (probe). Never keep a pointer into chunk memory beyond the statement that uses it. Use
  `fixed` only around one native call.
- The GPU cannot read chunk memory directly. `VK_EXT_external_memory_host` needs pointers aligned to
  `minImportedHostPointerAlignment` (4096 on RADV) that stay valid while imported. Chunk arrays meet
  neither condition, and Arch offers no way to change how it allocates them. Every upload copies.
- Copy one component type with a chunk loop:
  `chunk.GetSpan<T>()[..chunk.Count].CopyTo(destination[written..])`. Slice to `Count`; `GetSpan`
  covers the chunk's capacity. Probe: 1000 entities copied correctly, 0 bytes allocated.
- Build a GPU struct from several components with an `IForEach` struct that holds the destination
  pointer, a count, and the capacity, run with `World.InlineQuery`. `InlineQuery` takes the job by `ref`,
  so the count is correct after the call (probe: 0 bytes allocated).
- Size the upload first with `World.CountEntities(in description)`, which adds up archetype counts. Check
  every write against the capacity. When there are more entities than room, write up to the capacity, log
  a Warning once, and grow the buffer outside the frame (`design.md`).
- An entity's position in an upload is valid for that frame only. Iteration order changes on every
  destroy and structural change (`ecs` skill). Data that must follow one entity across frames needs a slot
  index stored in a component (`design.md`).
- Arch 2.1.0 has no change tracking: no per-chunk versions and no changed flags. Uploading only the chunks
  that changed is not possible without adding a flag yourself. By default, write the frame's instance data
  in full every frame. Measure before adding dirty tracking.

### Structs that shaders read

- Such a struct has only public fields of these types: `float`, `int`, `uint`, enums based on `int` or
  `uint`, `System.Numerics.Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Matrix4x4`, and other structs
  that follow this rule. `GpuLayout` rejects every other type.
  - No `bool`: it is 1 byte in .NET (probe) and not blittable, and 4 bytes in GLSL (probe). Use `uint`.
  - No `char` or reference types.
  - No `double`, `long`, `Half`, or 8-bit and 16-bit integers without the user's approval; the shader side
    needs device features for them.
  - No `Vector128<T>` or `Vector<T>`: .NET aligns `Vector128<float>` to 16 bytes (probe).
  - No `[StructLayout(Pack = ...)]` and no `LayoutKind.Explicit`; scalar layout makes them unnecessary.
- Every buffer and push constant block that mirrors a C# struct uses `layout(scalar)`, with
  `#extension GL_EXT_scalar_block_layout : require`. Scalar offsets equal .NET's offsets for the types
  above. Probe: (float, vec3, float) is at 0/4/16 in both, and a vec3 array has stride 12. std430 gives
  0/16/28 and stride 16, and push constants default to std430 (vec3 after a float at 16).
- `scalarBlockLayout` is a Vulkan 1.2 feature that devices may leave out. RADV and MoltenVK 1.4.0 report
  it. `DeviceRequirements` requires and enables it (since 2026-09-22), so device selection rejects a
  device without it. The validation layer checks shaders against the enabled features.
- Uniform blocks default to std140, where a `float[4]` has stride 16 (probe). Prefer storage buffers. A
  uniform block that mirrors a C# struct also uses `layout(scalar)`.
- Declare GLSL members in the same order as the C# fields; `GpuLayout` compares them by position, because
  the project's optimisation level strips member names from the SPIR-V (probe).
- Never write `layout(offset = N)`. glslang rejects an offset that breaks alignment, but it accepted two
  members that overlap (probe). The layout test is the check.
- Push constants stay within 128 bytes, the size every device must support (RADV 256, MoltenVK 4096).
  `GpuLayout.PushConstantDifferences` enforces it.
- Matrices: `System.Numerics.Matrix4x4` and Silk.NET.Maths `Matrix4X4<float>` store one row after another,
  with the translation in the fourth row (probe). Upload them unchanged. Compose on the CPU as
  `model * view * projection`, and multiply in GLSL as `M * v` with the default column-major `mat4`. Do not
  transpose and do not use `row_major`. Probe: the raw bytes read as a column-major `mat4` give the same
  result as `Vector4.Transform(v, M)`; the transposed matrix does not. `GpuLayout` reports row-major
  members.
- `Matrix4x4.CreatePerspectiveFieldOfView` maps near to depth 0 and far to 1, which matches Vulkan. It
  keeps +Y up, while Vulkan's clip space has +Y down (probe). Flip Y with a negative viewport height
  (`Y = height`, `Height = -height`, core since Vulkan 1.1), not by changing the matrix.

### Memory the CPU writes

These add to the `vulkan` skill's "Memory allocation" rules.

- Choose memory types from the flags the device reports. This machine exposes all 8 GB of VRAM as
  device-local, host-visible, and host-coherent (resizable BAR, RADV). Other PCs may expose 256 MB of that
  kind or none, and MoltenVK on Apple Silicon has one shared heap. Code must also work with plain
  host-visible, host-coherent system memory.
- Map memory once when it is allocated and keep it mapped until it is freed. A `VkDeviceMemory` can be
  mapped only once at a time.
- Only write to mapped memory; never read it. Host-visible device memory is usually uncached, so reads are
  very slow. That rules out `+=`, `ref` read-modify-write, and sorting in place. Write each value once, in
  address order. Build values in locals or a CPU array first. The one exception is memory made for reading
  back (`MemoryUsage.Readback`, host-cached where the device has it): after the slot's fence and
  `GpuBuffer.Invalidate`, read a few bytes once per frame on the render thread, as the particles' counters are
  (`ParticlePass.Collect`). The GPU write needs a barrier to Host/HostRead at the end of its frame.
- Memory that is not host-coherent needs `vkFlushMappedMemoryRanges` after writing, with offset and size
  rounded to `nonCoherentAtomSize` (64 on RADV).
- A storage buffer range bound at an offset needs the offset to be a multiple of
  `minStorageBufferOffsetAlignment` (4 on RADV, 16 on MoltenVK). Read the limit; do not hard-code it.
- Keep one region per frame in flight (`FrameSync.FramesInFlight`), indexed by the slot of the frame's
  request (`RenderFrameRequest.Slot`). The CPU writes a region only after that slot's fence wait, which the
  render thread does before it sends the request.

### Frame order

Option A of `design.md` choice 1 and a render thread (choice 6) were chosen on 2026-09-22.
The render thread waits on the slot's fence and acquires an image, then sends a `RenderFrameRequest`
through `FrameExchange`. The main thread takes it (`TryTakeRequest`), runs the ECS frame group, mods, and
UI, and publishes the frame (`Publish`). Between those two calls, frame systems and the extract step may
write the mapped region of `request.Slot` directly; after `Publish`, the render thread owns it until the
next request for that slot. Fixed systems still never write GPU data (see "Who touches what"). While no
frame is requested (presentation blocked, window hidden), frame systems do not run.

### Command buffers

- Record the frame's command buffer again every frame, with `OneTimeSubmitBit` (`vulkan` skill). Do not
  record command buffers per entity, per chunk, or per archetype for reuse, and do not use
  `SimultaneousUseBit`. The reasons:
  - Entities move between chunks and change order on every destroy.
  - A recorded command buffer becomes invalid when an object it uses is destroyed, or when a descriptor
    set it binds is updated, unless the updated bindings have `UPDATE_AFTER_BIND` or
    `UPDATE_UNUSED_WHILE_PENDING` (spec).
  - The frame's command buffer also holds the swapchain image barriers and image view, which change
    every frame.
  - Khronos, Arm, and NVIDIA guidance is to record again and to avoid `SIMULTANEOUS_USE`.
- Keep the command sequence the same from frame to frame: bind the pipeline, bind the buffers, draw.
  Entity changes change buffer contents and counts, not commands.
- A draw's instance count comes from the upload count on the CPU (`vkCmdDraw` with `instanceCount`), or
  from an indirect command in a buffer (`vkCmdDrawIndirect`, `vkCmdDrawIndexedIndirect`).
- Do not use `vkCmdDrawIndirectCount`. MoltenVK 1.4.0 reports `drawIndirectCount = false` and does not
  offer `VK_KHR_draw_indirect_count`.
- For several kinds of mesh, use one indirect command per kind with a fixed command count, and
  `instanceCount = 0` for kinds that have no entities. This needs `multiDrawIndirect` (RADV and MoltenVK
  report it). A `firstInstance` other than 0 needs `drawIndirectFirstInstance`, which MoltenVK reports only
  when Metal supports base-instance drawing. Check it at device selection.
- Secondary command buffers are not used. Adding them (to record on worker threads) is the user's decision.
  With dynamic rendering they need `RenderingFlags.ContentsSecondaryCommandBuffersBit` and
  `CommandBufferInheritanceRenderingInfo` with matching formats. They inherit no state, so each one sets
  its own pipeline, viewport, scissor, and descriptor sets.

### GPU results back to entities

- For each frame slot, keep a CPU array of the `Entity` written at each upload position. Read a GPU result
  that names an upload position (picking, occlusion, readback) only after that slot's fence. Map it
  through that frame's array, then check `World.IsAlive`: the entity may have been destroyed since.

### Performance

- Upload code allocates nothing per frame. The probe shows the chunk copy and the gather allocate 0 bytes;
  keep the code in those shapes.
- No Vulkan call per entity. One copy per chunk or one gather per query, and one draw per mesh kind.
- Per-entity logging is Trace (CLAUDE.md).
- Reading the world and writing slot memory run on the main thread; recording, submitting, and presenting
  run on the render thread (`vulkan` skill, "Threads"). No Vulkan call from a frame system: while the
  render thread runs, the main thread makes none but the staging flush that the `vulkan` skill's "Threads"
  rule names.

## Enforcement

`.claude/hooks/skill_guard.py` denies file writes under `src/Itera.Engine/Rendering/GpuData/`, writes to
`.cs` files that use the `Itera.Engine.Rendering.GpuData` namespace, writes to `.cs` files that use both
Arch and `Silk.NET.Vulkan`, and Bash commands that name the folder, until this skill is loaded in the
session. The same call often also needs the `ecs` or `vulkan` skill; the hook names every missing skill.

## Files in this skill

- `notes.md`: checked facts about Arch chunk memory, .NET struct and matrix layout, glslang buffer layouts,
  device features and limits on RADV and MoltenVK 1.4.0, and the command buffer rules from the spec.
- `design.md`: how entity data should reach the GPU in Itera, sizes to expect, and the open design choices.
- `reference.md`: sources, and the audit record of the current code.
- `probe.sh` and `probe/`: re-check the facts marked "probe".
