# Silk.NET 2.23 facts

Checked against the Silk.NET v2.23.0 source (`src/Vulkan/Silk.NET.Vulkan/Vk.cs`,
`src/Core/Silk.NET.Core/`). Re-check if `Directory.Packages.props` moves Silk.NET
to another version.

## How `Vk` finds function pointers

- `Vk.GetApi()` loads the Vulkan library right away (`libvulkan.so.1`; `vulkan-1.dll`;
  `libvulkan.dylib`, then `libMoltenVK.dylib` on macOS). It throws when none loads. `VulkanContext`
  calls it in its constructor, so a missing loader fails during DI resolution inside `Program.cs`'s
  `try` block.
- Function pointers load lazily on first call and are cached in a vtable. Lookup order:
  1. the symbol exported by the loaded library (the loader's trampolines for core functions),
  2. `vkGetDeviceProcAddr(CurrentDevice, name)`,
  3. `vkGetInstanceProcAddr(CurrentInstance, name)`.
- `Vk.CreateInstance` sets `Vk.CurrentInstance` and `Vk.CreateDevice` sets `Vk.CurrentDevice` when
  they succeed. Each (instance, device) pair gets its own vtable.
- One `Vk` object serves one instance and one device. For a second device, use
  `vk.CloneWith(instance, device)`; do not call `CreateDevice` twice on the same `Vk`.
- `vk.CurrentDevice` still holds the old handle after `DestroyDevice`. Nothing may call through
  `Vk` for device functions after that.
- The lazy loading is not documented as thread-safe. Itera keeps Vulkan calls on one thread at a time:
  the main thread during init and after shutdown, the render thread in between (`vulkan` skill,
  "Threads").

## Extension objects

- `vk.TryGetInstanceExtension(instance, out KhrSurface ext)` and
  `vk.TryGetDeviceExtension(instance, device, out KhrSwapchain ext)` return an object whose
  functions load through `vkGetInstanceProcAddr(instance)` or `vkGetDeviceProcAddr(device)`.
- They check only that some device or the instance *supports* the extension, not that it was
  *enabled*. Enable the extension in the create info first; calling a function of an extension that
  was not enabled is undefined behaviour.
- `TryGetDeviceExtension` checks support on any physical device, not the selected one. Check the
  selected device with `EnumerateDeviceExtensionProperties` (as `VulkanContext` does).
- Disposing an extension object is safe: its context (`LamdaNativeContext`) does nothing on
  dispose. Disposing `Vk` is not: its `MultiNativeContext` disposes the `DefaultNativeContext`,
  which unloads libvulkan while driver exit handlers still point into it. Never dispose `Vk`.
- Core 1.3 functions such as `CmdBeginRendering`, `CmdPipelineBarrier2`, and `QueueSubmit2` exist on
  `Vk`. The KHR forms live on extension objects (`KhrDynamicRendering`, `KhrSynchronization2`). Itera
  requires 1.3, so it always calls the core forms on `Vk`.
- Feature structs are plain structs with `Bool32` fields. `DeviceRequirements.VisitFeatures` takes
  `ref` to a field (`ref v12.ScalarBlockLayout`); `Bool32` converts to and from `bool`.

## Callbacks

- `new PfnDebugUtilsMessengerCallbackEXT(delegate)` calls `SilkMarshal.DelegateToPtr`, which
  allocates a `GCHandle` that is never freed. Build it once and reuse it.
- The delegate still has to be kept in a field; the GC handle keeps the delegate alive, but the
  field makes the lifetime visible and survives a future Silk.NET change.
- An exception thrown in a callback crosses native frames and aborts the process. Catch everything.
- A function pointer from `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` on a static
  method avoids delegate marshalling, but needs static state (for example the logger). It is an
  alternative to the current delegate-in-a-field style, not the project rule. Ask the user before switching.

## Strings

- `SilkMarshal.StringToPtr` and `StringArrayToPtr` default to `NativeStringEncoding.Ansi`. That is
  fine for extension and layer names (ASCII). Pass `NativeStringEncoding.UTF8` for any text that
  may hold non-ASCII characters, such as debug object names.
- Pair every allocation with `SilkMarshal.Free` in `finally`.
- Read `byte*` strings from Vulkan with `Marshal.PtrToStringUTF8`.

## Structs

- Object initialisers (`new FenceCreateInfo { Flags = ... }`) zero the struct and do not set
  `SType`. Set it every time. Silk's generated constructor with named arguments
  (`new FenceCreateInfo(flags: ...)`) does set `SType` by default, but the project uses
  initialisers with an explicit `SType`; keep that style.
- The `Chain` API (`Chain.Create(...)`) builds `PNext` chains in unmanaged memory, sets `SType`, and
  must be disposed. For one or two structs, stack locals and `&local` are simpler; the current code
  uses those.
- Fixed-size arrays inside structs (`DeviceName`, `ExtensionName`) are `fixed byte` fields; read
  them through a pointer to the field, within the struct's scope.
- `Bool32` converts to and from `bool` implicitly. `Vk.True` and `Vk.False` are `uint`.
- `Vk.MakeVersion` and `Vk.Version12`/`Version13` return `Version32`, which converts to `uint`.
