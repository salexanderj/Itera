# Checked facts for ECS data on the GPU

Checked on 2026-09-22. "Probe" means `probe/Program.cs` checks it (run in Release on .NET 10.0.12, Arch
2.1.0, Silk.NET 2.23.0). Arch paths are relative to `github.com/genaray/Arch` at tag `v2.1.0`.

## Arch chunk memory

- `Chunk` (`src/Arch/Core/Chunk.cs:162`) holds `Entity[] Entities` and `Array[] Components`, one array per
  component type. Each array comes from `ArrayRegistry.GetArray`, whose factory is
  `capacity => new T[capacity]` (`src/Arch/Core/ComponentRegistry.cs:374`). The factory is private; user
  code cannot change how Arch allocates.
- The arrays are ordinary managed arrays. With 16 KB chunks they are small-object-heap arrays that the GC
  compacts. Probe: after a forced compacting collection, the address of a chunk's `Position[]` changed.
- `Chunk.GetSpan<T>()` returns a span over `Capacity` elements (`Chunk.cs:425`); only the first `Count`
  hold entities. `GetArray<T>()` returns the whole array. Probe: the array is exactly `T[]` with length
  equal to the chunk's capacity.
- Chunk capacity is `chunkBytes / (sizeof(Entity) + sum of component sizes)`
  (`src/Arch/Core/Archetype.cs:818`), with at least 16 KB and 100 entities per chunk (`arch-notes.md` in the
  `ecs` skill). Probe: a (Position 12 B, Tint 4 B) archetype holds 585 entities per chunk (16384 / 28).
- `World.InlineQuery<TJob, T0...>(in description, ref TJob job)` (`src/Arch/Templates/World.InlineQuery.cs:8`)
  calls `job.Update` through the `ref`, so fields the job changes (a count) are visible after the call.
  Probe: a gather of 1000 entities returned 1000 and allocated 0 bytes.
- A chunk loop that copies `chunk.GetSpan<T>()[..chunk.Count]` into native memory allocated 0 bytes over
  1000 calls (probe).
- `World.CountEntities(in description)` (`World.cs:408`) adds `archetype.EntityCount` over the matching
  archetypes. It does not visit entities.
- Arch 2.1.0 has no change tracking. The source has no per-chunk version, changed flag, or `DidChange`
  equivalent (searched `src/Arch` for these on 2026-09-22).

## Zero-copy import is not possible

- `VK_EXT_external_memory_host` lets Vulkan use host memory in place. RADV reports it with
  `minImportedHostPointerAlignment = 4096`; MoltenVK 1.4.0 lists it (`MVKExtensions.def`). The pointer and
  size must be multiples of that alignment, and the memory must stay valid while imported.
- Chunk arrays are neither page-aligned nor pinned, so this does not apply. A pinned copy of the data is
  already a copy, so the import saves nothing over writing to mapped memory.

## .NET struct layout

- C# applies `LayoutKind.Sequential` to structs by default. For blittable types this controls the layout
  in managed memory; for non-blittable types it controls only the marshalled layout
  (`StructLayoutAttribute` docs, Microsoft Learn).
- `bool` and `char` are not blittable. Probe: a struct with one `bool` is 1 byte (`Unsafe.SizeOf`) but 4
  bytes marshalled (`Marshal.SizeOf`). A (bool, long, bool) struct stayed sequential (0/8/16) in .NET 10;
  dotnet/runtime issue 44579 shows auto layout appears when a non-blittable field meets an
  explicit-layout field. The docs promise nothing for non-blittable types, so the rule forbids `bool`.
- Sizes (probe): `Vector2` 8, `Vector3` 12, `Vector4` 16, `Quaternion` 16, `Matrix4x4` 64.
- Alignment (probe): `Vector3` and `Vector4` fields align to 4 bytes ((float, Vector3, float) is 0/4/16,
  size 20; `Vector4` after a float is at 4). `Vector128<float>` aligns to 16 (at 16 after a float).
- `StructLayout.Pack` is not honoured for managed layout even for blittable types (dotnet/runtime issue
  12342). Not needed with scalar layout.
- `GpuLayout` reads C# offsets with `Marshal.OffsetOf`. That equals the managed offset because it accepts
  only blittable leaf types.

## Matrices

- `System.Numerics.Matrix4x4` fields are `M11..M44`, stored row by row: `M12` at 4, `M21` at 16, `M41` at 48
  (probe). It uses row vectors: `Vector4.Transform(v, M)` computes `v * M`, and translation sits in
  `M41..M43`. Compose as `model * view * projection`.
- Silk.NET.Maths `Matrix4X4<float>` stores `Row1..Row4` at 0/16/32/48, and `CreateTranslation` puts the
  translation in `Row4` (probe). Same layout as `Matrix4x4`.
- GLSL `mat4` in a block defaults to column-major with `MatrixStride` 16 (probe, SPIR-V `ColMajor`).
  Column `c` is floats `4c..4c+3`. Reading the raw `Matrix4x4` bytes that way gives the transpose of the
  C# matrix. The transpose of a row-vector matrix is the column-vector matrix, so GLSL `M * v` equals C#
  `v * M`. Probe: equal to 1e-4; the transposed upload gives a different result.
- GLSL allows `row_major` and `column_major` on a block or block member, not on members of a plain struct
  (glslang: "cannot use layout qualifiers on structure members"). A struct inside a `row_major` block
  inherits it.
- `Matrix4x4.CreatePerspectiveFieldOfView` (right-handed) maps view depth -near to 0 and -far to 1, which
  is Vulkan's range. A point above the camera gets positive clip Y, while Vulkan's clip Y points down
  (probe). A negative viewport height flips Y (core in Vulkan 1.1, `VK_KHR_maintenance1`).

## glslang buffer layouts

Compiled through Silk.NET.Shaderc 2.23.0 with `ShaderCompiler`'s options at the time: `OptimizationLevel.Performance`,
default target environment (Vulkan 1.0). Since 2026-09-22 `ShaderCompiler` targets Vulkan 1.3; the layout
tests (`GpuLayoutTests`) and `probe.sh` (23 checks, now compiling with the same target) pass unchanged. Offsets and strides were read from the SPIR-V `Offset`, `ArrayStride`, and
`MatrixStride` decorations.

| Declaration | scalar | std430 | std140 |
|---|---|---|---|
| struct (float, vec3, float) | 0/4/16 | 0/16/28 | |
| vec3 array stride | 12 | 16 | |
| float[4] stride | | | 16 |
| push constants (float, vec3), no qualifier | | 0/16 (the default) | |
| push constants (float, vec3), `layout(push_constant, scalar)` | 0/4 | | |
| bool member followed by float | | 0/4 | |

- Spec definitions (Vulkan spec, "Offset and Stride Assignment"): scalar alignment of a vector is its
  component's; base alignment of a three- or four-component vector is four times its component's (16 for
  vec3); extended alignment rounds arrays and structs up to 16. std430 uses base alignment and std140
  uses extended alignment. Scalar layout applies only when the `scalarBlockLayout` feature is enabled.
- With `layout(offset = 4)` on a std430 vec3, glslang reports "must be a multiple of the member's
  alignment". With a vec3 at 0 and a float at `offset = 8` (overlapping), it compiled without an error
  (probe).
- At `OptimizationLevel.Performance`, no `OpName` or `OpMemberName` remains in the SPIR-V (probe). Blocks
  are found by set and binding, and members by position.
- `layout(scalar)` compiles with Shaderc's default target environment and with the Vulkan 1.3 target.
  Nothing in the compile step checks that the device supports it; the validation layer does, when the
  shader module is created.

## Device features and limits

RADV: AMD Radeon RX 5700 XT, Mesa 26.0.8, `vulkaninfo` on 2026-09-22. MoltenVK: v1.4.0 source
(`MoltenVK/MoltenVK/GPUObjects/MVKDevice.mm` line numbers at tag `v1.4.0`), the version inside
Silk.NET.MoltenVK.Native 2.23.0 (the dylib reports 1.4.0).

| Feature or limit | RADV | MoltenVK 1.4.0 |
|---|---|---|
| `scalarBlockLayout` | true | true (line 223) |
| `drawIndirectCount` | true | false (line 3107); `VK_KHR_draw_indirect_count` not available |
| `multiDrawIndirect` | true | true (line 3005) |
| `drawIndirectFirstInstance` | true | only with Metal indirect and base-instance drawing (line 3013) |
| `bufferDeviceAddress` | true | `mvkSupportsBufferDeviceAddress()` (line 230); extension needs macOS 13 |
| `descriptorIndexing` | true | only with Metal arrays of textures and samplers (line 3108) |
| `descriptorBindingUpdateUnusedWhilePending` | true | true (line 218) |
| `timelineSemaphore`, `synchronization2` | true | true (lines 229, 254) |
| `maxPushConstantsSize` | 256 | 4096 (line 3217) |
| `minStorageBufferOffsetAlignment` | 4 | 16 (line 3221) |
| `nonCoherentAtomSize` | 64 | Metal buffer alignment (line 3223) |
| `VK_EXT_external_memory_host` | yes, alignment 4096 | listed |
| Host-visible VRAM | whole 8 GB heap is device-local + host-visible + host-coherent | unified memory: the shared type is device-local (line 3707) |

The spec's minimum for `maxPushConstantsSize` is 128. Windows drivers and other Linux GPUs were not
checked; read the values at run time.

## Command buffers (spec and vendor guidance)

- A command buffer that is not pending becomes invalid when "any memory or object ... that may be accessed
  when the command buffer is accessed ... is destroyed or freed" (spec, command buffer lifecycle).
- `vkUpdateDescriptorSets`: a command buffer in the recording or executable state that binds the updated
  set becomes invalid, unless every updated binding was created with `UPDATE_AFTER_BIND` or
  `UPDATE_UNUSED_WHILE_PENDING`. Bindings without those flags must not be updated while a pending command
  buffer uses the set.
- `SIMULTANEOUS_USE_BIT` is needed to submit a command buffer that is still pending. Vulkan Samples
  ("Command buffer usage") and NVIDIA ("Vulkan Dos and Don'ts") say it may reduce GPU performance; the
  sample recommends `ONE_TIME_SUBMIT` and resetting pools. The Vulkan Guide's common pitfalls page says
  reuse rarely brings the expected gain and is hard to keep correct when objects come and go.
- `vkCmdUpdateBuffer` copies `pData` into the command buffer when it is recorded: at most 65536 bytes, size
  and offset multiples of 4, outside a render pass instance. The spec says to use it only for small data.
  Pinning the source with `fixed` for the call is enough, because the copy happens during the call.
