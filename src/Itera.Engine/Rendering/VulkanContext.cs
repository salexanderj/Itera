using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Itera.Engine.Rendering;

/// <summary>
/// Vulkan instance, surface, physical and logical device, and queues. Everything downstream (swapchain, frame
/// sync, renderers) borrows handles from here. <see cref="DeviceRequirements"/> lists what a device must support.
/// </summary>
public sealed unsafe partial class VulkanContext : IDisposable
{
    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";
    private const string PortabilityEnumerationExtension = "VK_KHR_portability_enumeration";
    private const string PortabilitySubsetExtension = "VK_KHR_portability_subset";

    /// <summary>Swapchain images viewed in another format than they were made in (<see cref="SupportsMutableSwapchainFormat"/>).</summary>
    private const string MutableSwapchainFormatExtension = "VK_KHR_swapchain_mutable_format";
    private const string LayerSettingsExtension = "VK_EXT_layer_settings";

    private readonly RendererSettings _settings;
    private readonly string _applicationName;
    private readonly ILogger<VulkanContext> _logger;
    private readonly DebugUtilsMessengerCallbackFunctionEXT _debugCallback;
    private readonly PfnDebugUtilsMessengerCallbackEXT _debugCallbackPointer;

    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _messenger;
    private PhysicalDeviceCandidate? _selected;
    private bool _disposed;

    /// <summary>True once the selected device's limits and memory have been read.</summary>
    private bool _deviceRead;
    private PhysicalDeviceLimits _limits;
    private uint _graphicsQueueFamily;
    private uint _presentQueueFamily;
    private uint _graphicsTimestampValidBits;
    private string _deviceName = string.Empty;
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private ulong _maxBufferSize;

    public VulkanContext(IOptions<EngineOptions> options, ILogger<VulkanContext> logger)
    {
        _settings = options.Value.Renderer;
        _applicationName = options.Value.Application.Name;
        _logger = logger;
        _debugCallback = DebugCallback;
        _debugCallbackPointer = new PfnDebugUtilsMessengerCallbackEXT(_debugCallback);
        Vk = Vk.GetApi();
    }

    /// <summary>The Vulkan API entry points.</summary>
    public Vk Vk { get; }

    // The handles below are null until Initialize makes them. A Vulkan call given a null handle fails at once, unlike a
    // value read too early, and the passes' Dispose methods read Device before checking their own handles, also in a run
    // that never reached the Vulkan step; so they do not throw. The device's values further down do.

    public Instance Instance { get; private set; }

    public SurfaceKHR Surface { get; private set; }

    public PhysicalDevice PhysicalDevice { get; private set; }

    public Device Device { get; private set; }

    public Queue GraphicsQueue { get; private set; }

    public Queue PresentQueue { get; private set; }

    /// <summary>The graphics queue's family. Throws before the device is selected (family 0 is a plausible wrong answer).</summary>
    public uint GraphicsQueueFamily => _deviceRead ? _graphicsQueueFamily : throw NotSelected();

    /// <summary>The present queue's family. Throws before the device is selected.</summary>
    public uint PresentQueueFamily => _deviceRead ? _presentQueueFamily : throw NotSelected();

    /// <summary>Surface extension entry points. Available after <see cref="Initialize"/>.</summary>
    public KhrSurface KhrSurface { get; private set; } = null!;

    /// <summary>Swapchain extension entry points. Available after <see cref="Initialize"/>.</summary>
    public KhrSwapchain KhrSwapchain { get; private set; } = null!;

    /// <summary>Instance API version that was requested: <see cref="DeviceRequirements.MinimumApiVersion"/>.</summary>
    public uint ApiVersion { get; private set; }

    /// <summary>Name of the selected physical device. Throws before the device is selected.</summary>
    public string DeviceName => _deviceRead ? _deviceName : throw NotSelected();

    /// <summary>True when validation layers were requested and found.</summary>
    public bool ValidationEnabled { get; private set; }

    /// <summary>
    /// True when the device draws triangles as lines (<c>fillModeNonSolid</c>, enabled where the device has it and asked
    /// of no device): the wireframe debug view (<see cref="GpuData.ViewSwitches.Wireframe"/>). Valid after
    /// <see cref="Initialize"/>.
    /// </summary>
    public bool SupportsWireframe { get; private set; }

    /// <summary>
    /// True when the device filters textures anisotropically (<c>samplerAnisotropy</c>, enabled where the device has it
    /// and asked of no device): the texture prototype's sampler. Valid after <see cref="Initialize"/>.
    /// </summary>
    public bool SupportsAnisotropy { get; private set; }

    /// <summary>
    /// True when the device has <c>VK_KHR_swapchain_mutable_format</c>, enabled where offered and asked of no device: a
    /// swapchain of UNORM images (a headless surface offers no sRGB format) can then be drawn into through sRGB views, as a
    /// window's is. Valid after <see cref="Initialize"/>.
    /// </summary>
    public bool SupportsMutableSwapchainFormat { get; private set; }

    /// <summary>
    /// Limits of the selected physical device: storage buffer range, alignments, timestamp period. Throws before
    /// <see cref="Initialize"/> has selected the device, so a constructor that reads it fails at the start instead of
    /// reading zeros (a renderer that did held every frame to render scale 0.05 from 2026-10-03 to 2026-10-04).
    /// </summary>
    public PhysicalDeviceLimits Limits => _deviceRead ? _limits : throw NotSelected();

    /// <summary>Memory heaps and types of the selected physical device. Throws before the device is selected.</summary>
    public PhysicalDeviceMemoryProperties MemoryProperties => _deviceRead ? _memoryProperties : throw NotSelected();

    /// <summary>
    /// Largest buffer the device can create (Vulkan 1.3 <c>maxBufferSize</c>; at least 1 GB). Throws before the device
    /// is selected.
    /// </summary>
    public ulong MaxBufferSize => _deviceRead ? _maxBufferSize : throw NotSelected();

    private static InvalidOperationException NotSelected() =>
        new(
            "The device's values (limits, memory, queue families, timestamp bits, name) are read only after "
                + "VulkanContext.Initialize has selected the device."
        );

    /// <summary>
    /// Valid bits in timestamps written on the graphics queue; 0 means that queue cannot write timestamps. Throws before
    /// the device is selected (0 would read as no timings).
    /// </summary>
    public uint GraphicsTimestampValidBits => _deviceRead ? _graphicsTimestampValidBits : throw NotSelected();

    /// <summary>
    /// True after <see cref="Abandon"/>: <see cref="WaitIdle"/> and every <c>Dispose</c> that destroys Vulkan objects
    /// (this class, <see cref="VulkanSwapchain"/>, <see cref="FrameSync"/>) then do nothing.
    /// </summary>
    public bool IsAbandoned { get; private set; }

    /// <summary>
    /// Creates every object in order. <paramref name="surfaceSource"/> comes from the window, or is headless when the
    /// game runs without one.
    /// </summary>
    public void Initialize(ISurfaceSource surfaceSource)
    {
        ArgumentNullException.ThrowIfNull(surfaceSource);
        CreateInstance(surfaceSource);
        CreateDebugMessenger();
        CreateSurface(surfaceSource);
        PickPhysicalDevice();
        CreateLogicalDevice();
    }

    /// <summary>
    /// Leaves every Vulkan object to the OS at exit. For shutdown when the render thread is stuck inside a Vulkan call
    /// (a present that blocks while the window is hidden): destroying the queue's device, the swapchain, or the surface
    /// under it is undefined behaviour. Main thread.
    /// </summary>
    public void Abandon()
    {
        IsAbandoned = true;
    }

    /// <summary>
    /// Names a Vulkan object so validation messages mention it. Does nothing without validation. Call from the thread
    /// that owns Vulkan at the time (the main thread before the render thread starts).
    /// </summary>
    public void SetObjectName(ObjectType type, ulong handle, string name)
    {
        if (_debugUtils is null || Device.Handle == 0)
        {
            return;
        }

        var text = (byte*)SilkMarshal.StringToPtr(name, NativeStringEncoding.UTF8);
        try
        {
            var info = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = type,
                ObjectHandle = handle,
                PObjectName = text,
            };
            _debugUtils.SetDebugUtilsObjectName(Device, &info).ThrowIfFailed("vkSetDebugUtilsObjectNameEXT");
        }
        finally
        {
            SilkMarshal.Free((nint)text);
        }
    }

    /// <summary>Blocks until the device is idle. Call before destroying anything the GPU may still use.</summary>
    public void WaitIdle()
    {
        if (Device.Handle != 0 && !IsAbandoned)
        {
            Vk.DeviceWaitIdle(Device).ThrowIfFailed("vkDeviceWaitIdle");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsAbandoned)
        {
            return;
        }

        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);
            KhrSwapchain?.Dispose();
            Vk.DestroyDevice(Device, null);
            Device = default;
        }

        if (Surface.Handle != 0)
        {
            KhrSurface.DestroySurface(Instance, Surface, null);
            Surface = default;
        }

        KhrSurface?.Dispose();

        if (_messenger.Handle != 0 && _debugUtils is not null)
        {
            _debugUtils.DestroyDebugUtilsMessenger(Instance, _messenger, null);
            _messenger = default;
        }

        _debugUtils?.Dispose();

        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }

        // The Vk container is deliberately not disposed: disposing it dlcloses libvulkan while driver
        // atexit handlers still point into it, which crashes at process exit.
    }

    private static string ReadString(byte* value)
    {
        return Marshal.PtrToStringUTF8((nint)value) ?? string.Empty;
    }

    private bool ShouldEnableValidation()
    {
        if (_settings.Validation is { } explicitValue)
        {
            return explicitValue;
        }

#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private HashSet<string> EnumerateInstanceLayers()
    {
        uint count = 0;
        Vk.EnumerateInstanceLayerProperties(&count, null).ThrowIfFailed("vkEnumerateInstanceLayerProperties");
        var properties = new LayerProperties[count];
        fixed (LayerProperties* p = properties)
        {
            Vk.EnumerateInstanceLayerProperties(&count, p).ThrowIfFailed("vkEnumerateInstanceLayerProperties");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var layer = properties[i];
            names.Add(ReadString(layer.LayerName));
        }

        return names;
    }

    /// <summary>Instance extensions from the loader and implicit layers, or only those of <paramref name="layerName"/>.</summary>
    private HashSet<string> EnumerateInstanceExtensions(string? layerName = null)
    {
        var layer = layerName is null ? null : (byte*)SilkMarshal.StringToPtr(layerName);
        ExtensionProperties[] properties;
        uint count = 0;
        try
        {
            Vk.EnumerateInstanceExtensionProperties(layer, &count, null)
                .ThrowIfFailed("vkEnumerateInstanceExtensionProperties");
            properties = new ExtensionProperties[count];
            fixed (ExtensionProperties* p = properties)
            {
                Vk.EnumerateInstanceExtensionProperties(layer, &count, p)
                    .ThrowIfFailed("vkEnumerateInstanceExtensionProperties");
            }
        }
        finally
        {
            SilkMarshal.Free((nint)layer);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var extension = properties[i];
            names.Add(ReadString(extension.ExtensionName));
        }

        return names;
    }

    private HashSet<string> EnumerateDeviceExtensions(PhysicalDevice device)
    {
        uint count = 0;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null)
            .ThrowIfFailed("vkEnumerateDeviceExtensionProperties");
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = properties)
        {
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, p)
                .ThrowIfFailed("vkEnumerateDeviceExtensionProperties");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var extension = properties[i];
            names.Add(ReadString(extension.ExtensionName));
        }

        return names;
    }

    private void CreateInstance(ISurfaceSource surfaceSource)
    {
        uint supportedVersion = Vk.Version10;
        Vk.EnumerateInstanceVersion(&supportedVersion).ThrowIfFailed("vkEnumerateInstanceVersion");
        if (supportedVersion < DeviceRequirements.MinimumApiVersion)
        {
            throw new VulkanException(
                $"The Vulkan loader supports API {FormatVersion(supportedVersion)}; Itera needs "
                    + $"{FormatVersion(DeviceRequirements.MinimumApiVersion)}. Update the graphics driver."
            );
        }

        ApiVersion = DeviceRequirements.MinimumApiVersion;

        var availableLayers = EnumerateInstanceLayers();
        var availableExtensions = EnumerateInstanceExtensions();

        var extensions = new List<string>(surfaceSource.InstanceExtensions);
        foreach (var extension in extensions)
        {
            if (!availableExtensions.Contains(extension))
            {
                throw new VulkanException(
                    $"The Vulkan loader does not offer {extension}, which the surface needs"
                        + (extension == ExtHeadlessSurface.ExtensionName ? " (Engine:Window:Mode=Offscreen)." : ".")
                );
            }
        }

        var layers = new List<string>();
        var layerSettingsAvailable = false;
        var wantValidation = ShouldEnableValidation();
        if (wantValidation)
        {
            if (
                availableLayers.Contains(ValidationLayerName)
                && availableExtensions.Contains(ExtDebugUtils.ExtensionName)
            )
            {
                layers.Add(ValidationLayerName);
                extensions.Add(ExtDebugUtils.ExtensionName);
                ValidationEnabled = true;

                if (EnumerateInstanceExtensions(ValidationLayerName).Contains(LayerSettingsExtension))
                {
                    extensions.Add(LayerSettingsExtension);
                    layerSettingsAvailable = true;
                }
            }
            else
            {
                LogValidationUnavailable();
            }
        }

        var flags = InstanceCreateFlags.None;
        if (OperatingSystem.IsMacOS() && availableExtensions.Contains(PortabilityEnumerationExtension))
        {
            extensions.Add(PortabilityEnumerationExtension);
            flags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
        }

        var appName = (byte*)SilkMarshal.StringToPtr(_applicationName);
        var engineName = (byte*)SilkMarshal.StringToPtr("Itera Engine");
        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        var layerNames = (byte**)SilkMarshal.StringArrayToPtr(layers);
        var validationLayerName = (byte*)SilkMarshal.StringToPtr(ValidationLayerName);
        var syncSettingName = (byte*)SilkMarshal.StringToPtr("validate_sync");
        var bestPracticesSettingName = (byte*)SilkMarshal.StringToPtr("validate_best_practices");
        var extraChecks = new List<string>();

        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = Vk.MakeVersion(0, 1, 0),
                PEngineName = engineName,
                EngineVersion = Vk.MakeVersion(0, 1, 0),
                ApiVersion = ApiVersion,
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                Flags = flags,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = extensionNames,
                EnabledLayerCount = (uint)layers.Count,
                PpEnabledLayerNames = layerNames,
            };

            var debugInfo = BuildDebugMessengerCreateInfo();
            if (ValidationEnabled)
            {
                createInfo.PNext = &debugInfo;
            }

            // Extra validation checks go through VK_EXT_layer_settings, which replaces VkValidationFeaturesEXT.
            var enabledValue = Vk.True;
            var settings = stackalloc LayerSettingEXT[2];
            uint settingCount = 0;
            if (layerSettingsAvailable && _settings.SyncValidation)
            {
                settings[settingCount++] = BoolLayerSetting(validationLayerName, syncSettingName, &enabledValue);
                extraChecks.Add("sync");
            }

            if (layerSettingsAvailable && _settings.BestPracticesValidation)
            {
                settings[settingCount++] = BoolLayerSetting(
                    validationLayerName,
                    bestPracticesSettingName,
                    &enabledValue
                );
                extraChecks.Add("best practices");
            }

            var layerSettingsInfo = new LayerSettingsCreateInfoEXT
            {
                SType = StructureType.LayerSettingsCreateInfoExt,
                SettingCount = settingCount,
                PSettings = settings,
            };
            if (ValidationEnabled && layerSettingsAvailable)
            {
                debugInfo.PNext = &layerSettingsInfo;
            }

            Vk.CreateInstance(&createInfo, null, out var instance).ThrowIfFailed("vkCreateInstance");
            Instance = instance;
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            SilkMarshal.Free((nint)engineName);
            SilkMarshal.Free((nint)extensionNames);
            SilkMarshal.Free((nint)layerNames);
            SilkMarshal.Free((nint)validationLayerName);
            SilkMarshal.Free((nint)syncSettingName);
            SilkMarshal.Free((nint)bestPracticesSettingName);
        }

        var extensionList = string.Join(", ", extensions);
        var validationState = ValidationEnabled
            ? (extraChecks.Count > 0 ? $"on ({string.Join(", ", extraChecks)})" : "on")
            : (wantValidation ? "unavailable" : "off");
        var version = FormatVersion(ApiVersion);
        LogInstanceCreated(version, validationState, extensionList);
    }

    private static string FormatVersion(uint version)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{version >> 22}.{(version >> 12) & 0x3FF}");
    }

    private static LayerSettingEXT BoolLayerSetting(byte* layerName, byte* settingName, uint* value)
    {
        return new LayerSettingEXT
        {
            PLayerName = layerName,
            PSettingName = settingName,
            Type = LayerSettingTypeEXT.Bool32Ext,
            ValueCount = 1,
            PValues = value,
        };
    }

    private DebugUtilsMessengerCreateInfoEXT BuildDebugMessengerCreateInfo()
    {
        return new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity =
                DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
                | DebugUtilsMessageSeverityFlagsEXT.InfoBitExt
                | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType =
                DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = _debugCallbackPointer,
        };
    }

    private void CreateDebugMessenger()
    {
        if (!ValidationEnabled)
        {
            return;
        }

        if (!Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils))
        {
            LogValidationUnavailable();
            ValidationEnabled = false;
            return;
        }

        _debugUtils = debugUtils;
        var createInfo = BuildDebugMessengerCreateInfo();
        _debugUtils
            .CreateDebugUtilsMessenger(Instance, &createInfo, null, out var messenger)
            .ThrowIfFailed("vkCreateDebugUtilsMessengerEXT");
        _messenger = messenger;
    }

    private uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData
    )
    {
        // Native code calls this, possibly on a driver thread. An exception escaping here aborts the process.
        try
        {
            // The level is checked before the message is converted, so loader chatter costs nothing when the
            // level is off.
            var level = MapSeverity(severity, type);
            if (_logger.IsEnabled(level))
            {
                var message = ReadString(data->PMessage);
                LogValidationMessage(level, message);
            }
        }
        catch (Exception)
        {
            // Nothing safe is left to report to.
        }

        return Vk.False;
    }

    /// <summary>
    /// Errors and warnings keep their level. Info messages from validation and performance checks are Debug.
    /// Verbose messages and the loader's general-purpose chatter (manifest scanning, layer discovery) are Trace.
    /// </summary>
    private static LogLevel MapSeverity(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type)
    {
        if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
        {
            return LogLevel.Error;
        }

        if ((severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
        {
            return LogLevel.Warning;
        }

        var checks = DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt;
        if ((severity & DebugUtilsMessageSeverityFlagsEXT.InfoBitExt) != 0 && (type & checks) != 0)
        {
            return LogLevel.Debug;
        }

        return LogLevel.Trace;
    }

    private void CreateSurface(ISurfaceSource surfaceSource)
    {
        // Load the extension first so Dispose can always destroy a surface that exists.
        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface khrSurface))
        {
            throw new VulkanException("VK_KHR_surface is not available.");
        }

        KhrSurface = khrSurface;
        Surface = surfaceSource.Create(Vk, Instance);
    }

    private void PickPhysicalDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, &count, null).ThrowIfFailed("vkEnumeratePhysicalDevices");
        if (count == 0)
        {
            throw new VulkanException("No Vulkan physical devices were found.");
        }

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
        {
            Vk.EnumeratePhysicalDevices(Instance, &count, p).ThrowIfFailed("vkEnumeratePhysicalDevices");
        }

        var bestScore = long.MinValue;
        PhysicalDeviceCandidate? best = null;

        for (var i = 0; i < count; i++)
        {
            var candidate = DescribeCandidate(devices[i]);
            LogPhysicalDevice(
                i,
                candidate.Name,
                candidate.Type,
                candidate.ApiMajor,
                candidate.ApiMinor,
                candidate.DriverVersion,
                candidate.Suitable ? "suitable" : candidate.Reason
            );

            if (!candidate.Suitable)
            {
                continue;
            }

            var score = DeviceScore(candidate.Type, candidate.Name, _settings.PreferredDevice);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is null)
        {
            throw new VulkanException(
                "No physical device meets Itera's requirements; the physical device lines above give each device's reason."
            );
        }

        _selected = best;
        PhysicalDevice = best.Device;
        _deviceName = best.Name;
        _graphicsQueueFamily = best.GraphicsFamily;
        _presentQueueFamily = best.PresentFamily;
        _graphicsTimestampValidBits = best.GraphicsTimestampValidBits;
        var properties13 = new PhysicalDeviceVulkan13Properties
        {
            SType = StructureType.PhysicalDeviceVulkan13Properties,
        };
        var properties2 = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &properties13,
        };
        Vk.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        _limits = properties2.Properties.Limits;
        _maxBufferSize = properties13.MaxBufferSize;
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memory);
        _memoryProperties = memory;
        _deviceRead = true;

        LogSelectedDevice(best.Name, best.Type, best.ApiMajor, best.ApiMinor);
        if (best.Type == PhysicalDeviceType.Cpu)
        {
            LogSoftwareDevice(best.Name);
        }

        LogDeviceLimits(
            MaxBufferSize,
            Limits.MaxStorageBufferRange,
            Limits.MinStorageBufferOffsetAlignment,
            Limits.NonCoherentAtomSize,
            Limits.MaxPushConstantsSize,
            Limits.TimestampPeriod,
            GraphicsTimestampValidBits
        );
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var memoryDescription = DescribeMemory(memory);
            LogMemory(memoryDescription);
        }
    }

    /// <summary>One line listing the heaps and the memory types in each, for the log.</summary>
    private static string DescribeMemory(PhysicalDeviceMemoryProperties memory)
    {
        var parts = new List<string>();
        for (var heap = 0; heap < memory.MemoryHeapCount; heap++)
        {
            var types = new List<string>();
            for (var type = 0; type < memory.MemoryTypeCount; type++)
            {
                if (memory.MemoryTypes[type].HeapIndex == heap)
                {
                    types.Add(
                        string.Create(CultureInfo.InvariantCulture, $"{type} {memory.MemoryTypes[type].PropertyFlags}")
                    );
                }
            }

            var heapInfo = memory.MemoryHeaps[heap];
            parts.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"heap {heap} {heapInfo.Size / (1024 * 1024)} MB {heapInfo.Flags}: [{string.Join("; ", types)}]"
                )
            );
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// How much a suitable device is preferred: discrete over integrated over virtual over the rest (a CPU), and any
    /// whose name holds <paramref name="preferred"/> (<c>Engine:Renderer:PreferredDevice</c>, any case) over all of them.
    /// The first device of the highest score wins.
    /// </summary>
    internal static long DeviceScore(PhysicalDeviceType type, string name, string? preferred)
    {
        var score = type switch
        {
            PhysicalDeviceType.DiscreteGpu => 1000L,
            PhysicalDeviceType.IntegratedGpu => 100L,
            PhysicalDeviceType.VirtualGpu => 10L,
            _ => 1L,
        };
        if (!string.IsNullOrWhiteSpace(preferred) && name.Contains(preferred, StringComparison.OrdinalIgnoreCase))
        {
            score += 10_000L;
        }

        return score;
    }

    private PhysicalDeviceCandidate DescribeCandidate(PhysicalDevice device)
    {
        Vk.GetPhysicalDeviceProperties(device, out var properties);
        var name = ReadString(properties.DeviceName);
        var apiMajor = (int)(properties.ApiVersion >> 22);
        var apiMinor = (int)((properties.ApiVersion >> 12) & 0x3FF);

        var candidate = new PhysicalDeviceCandidate
        {
            Device = device,
            Name = name,
            Type = properties.DeviceType,
            ApiMajor = apiMajor,
            ApiMinor = apiMinor,
            DriverVersion = properties.DriverVersion,
        };

        if (properties.ApiVersion < DeviceRequirements.MinimumApiVersion)
        {
            candidate.Reason = $"API below {FormatVersion(DeviceRequirements.MinimumApiVersion)}";
            return candidate;
        }

        var extensions = EnumerateDeviceExtensions(device);
        candidate.HasPortabilitySubset = extensions.Contains(PortabilitySubsetExtension);
        candidate.HasMutableSwapchainFormat = extensions.Contains(MutableSwapchainFormatExtension);
        foreach (var required in DeviceRequirements.Extensions)
        {
            if (!extensions.Contains(required))
            {
                candidate.Reason = $"no {required}";
                return candidate;
            }
        }

        // The device and the instance are both 1.3 or later here, so both feature structs are valid to query.
        var features13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var features12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &features13,
        };
        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features12,
        };
        Vk.GetPhysicalDeviceFeatures2(device, &features);
        candidate.HasFillModeNonSolid = features.Features.FillModeNonSolid;
        candidate.HasSamplerAnisotropy = features.Features.SamplerAnisotropy;
        if (DeviceRequirements.FindMissingFeature(features.Features, features12, features13) is { } missingFeature)
        {
            candidate.Reason = $"no {missingFeature}";
            return candidate;
        }

        foreach (var (format, needed, use) in DeviceRequirements.Formats)
        {
            Vk.GetPhysicalDeviceFormatProperties(device, format, out var formatProperties);
            if ((formatProperties.OptimalTilingFeatures & needed) != needed)
            {
                candidate.Reason = $"no {use}";
                return candidate;
            }
        }

        uint familyCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* p = families)
        {
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, p);
        }

        uint? graphics = null;
        uint? present = null;
        for (uint i = 0; i < familyCount; i++)
        {
            var hasGraphics = families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit);
            KhrSurface
                .GetPhysicalDeviceSurfaceSupport(device, i, Surface, out var supported)
                .ThrowIfFailed("vkGetPhysicalDeviceSurfaceSupportKHR");
            var canPresent = supported == Vk.True;

            if (hasGraphics && canPresent)
            {
                graphics = i;
                present = i;
                break;
            }

            graphics ??= hasGraphics ? i : null;
            present ??= canPresent ? i : null;
        }

        if (graphics is null || present is null)
        {
            candidate.Reason = "no graphics+present queue";
            return candidate;
        }

        uint formatCount = 0;
        KhrSurface
            .GetPhysicalDeviceSurfaceFormats(device, Surface, &formatCount, null)
            .ThrowIfFailed("vkGetPhysicalDeviceSurfaceFormatsKHR");
        uint presentModeCount = 0;
        KhrSurface
            .GetPhysicalDeviceSurfacePresentModes(device, Surface, &presentModeCount, null)
            .ThrowIfFailed("vkGetPhysicalDeviceSurfacePresentModesKHR");
        if (formatCount == 0 || presentModeCount == 0)
        {
            candidate.Reason = "no surface formats or present modes";
            return candidate;
        }

        candidate.GraphicsFamily = graphics.Value;
        candidate.PresentFamily = present.Value;
        candidate.GraphicsTimestampValidBits = families[graphics.Value].TimestampValidBits;
        candidate.Suitable = true;
        return candidate;
    }

    private void CreateLogicalDevice()
    {
        var selected = _selected ?? throw new InvalidOperationException("No physical device has been selected.");
        var enabledExtensions = new List<string>(DeviceRequirements.Extensions);
        if (selected.HasPortabilitySubset)
        {
            enabledExtensions.Add(PortabilitySubsetExtension);
        }

        // Optional: a surface without an sRGB format (a headless one) is viewed as sRGB (VulkanSwapchain).
        if (selected.HasMutableSwapchainFormat)
        {
            enabledExtensions.Add(MutableSwapchainFormatExtension);
            SupportsMutableSwapchainFormat = true;
        }

        var uniqueFamilies =
            GraphicsQueueFamily == PresentQueueFamily
                ? new[] { GraphicsQueueFamily }
                : new[] { GraphicsQueueFamily, PresentQueueFamily };

        var priority = 1f;
        var queueInfos = stackalloc DeviceQueueCreateInfo[uniqueFamilies.Length];
        for (var i = 0; i < uniqueFamilies.Length; i++)
        {
            queueInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(enabledExtensions);
        try
        {
            var features13 = new PhysicalDeviceVulkan13Features
            {
                SType = StructureType.PhysicalDeviceVulkan13Features,
            };
            var features12 = new PhysicalDeviceVulkan12Features
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = &features13,
            };
            // Core features go in a VkPhysicalDeviceFeatures2 at the head of the chain; pEnabledFeatures stays null,
            // as the spec requires when that struct is chained.
            var features2 = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &features12,
            };
            DeviceRequirements.EnableFeatures(ref features2.Features, ref features12, ref features13);
            // Optional: the wireframe debug view, where the device has it.
            features2.Features.FillModeNonSolid = selected.HasFillModeNonSolid;
            SupportsWireframe = selected.HasFillModeNonSolid;
            // Optional: anisotropic texture filtering, where the device has it.
            features2.Features.SamplerAnisotropy = selected.HasSamplerAnisotropy;
            SupportsAnisotropy = selected.HasSamplerAnisotropy;

            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features2,
                QueueCreateInfoCount = (uint)uniqueFamilies.Length,
                PQueueCreateInfos = queueInfos,
                EnabledExtensionCount = (uint)enabledExtensions.Count,
                PpEnabledExtensionNames = extensionNames,
            };

            Vk.CreateDevice(PhysicalDevice, &createInfo, null, out var device).ThrowIfFailed("vkCreateDevice");
            Device = device;
        }
        finally
        {
            SilkMarshal.Free((nint)extensionNames);
        }

        Vk.GetDeviceQueue(Device, GraphicsQueueFamily, 0, out var graphicsQueue);
        Vk.GetDeviceQueue(Device, PresentQueueFamily, 0, out var presentQueue);
        GraphicsQueue = graphicsQueue;
        PresentQueue = presentQueue;

        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain khrSwapchain))
        {
            throw new VulkanException("VK_KHR_swapchain entry points were not found.");
        }

        KhrSwapchain = khrSwapchain;

        var enabledList = string.Join(", ", enabledExtensions);
        LogDeviceCreated(enabledList, GraphicsQueueFamily, PresentQueueFamily);
    }

    private sealed class PhysicalDeviceCandidate
    {
        public PhysicalDevice Device { get; init; }

        public string Name { get; init; } = string.Empty;

        public PhysicalDeviceType Type { get; init; }

        public int ApiMajor { get; init; }

        public int ApiMinor { get; init; }

        public uint DriverVersion { get; init; }

        public bool Suitable { get; set; }

        public string Reason { get; set; } = string.Empty;

        public uint GraphicsFamily { get; set; }

        public uint PresentFamily { get; set; }

        public bool HasPortabilitySubset { get; set; }

        public bool HasMutableSwapchainFormat { get; set; }

        public bool HasFillModeNonSolid { get; set; }

        public bool HasSamplerAnisotropy { get; set; }

        public uint GraphicsTimestampValidBits { get; set; }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Vulkan validation was requested but VK_LAYER_KHRONOS_validation or VK_EXT_debug_utils is not installed; continuing without it"
    )]
    private partial void LogValidationUnavailable();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Vulkan instance created: API {Version}, validation {Validation}, extensions [{Extensions}]"
    )]
    private partial void LogInstanceCreated(string version, string validation, string extensions);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Physical device {Index}: {Name} ({Type}, API {Major}.{Minor}, driver {Driver}) - {Status}"
    )]
    private partial void LogPhysicalDevice(
        int index,
        string name,
        PhysicalDeviceType type,
        int major,
        int minor,
        uint driver,
        string status
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Selected physical device: {Name} [{Type}], API {Major}.{Minor}"
    )]
    private partial void LogSelectedDevice(string name, PhysicalDeviceType type, int major, int minor);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Name} is a software rasteriser; expect very low frame rates. Check the GPU driver, or pick a device with Engine:Renderer:PreferredDevice"
    )]
    private partial void LogSoftwareDevice(string name);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Device limits: maxBufferSize {MaxBufferSize}, maxStorageBufferRange {MaxStorageBufferRange}, minStorageBufferOffsetAlignment {StorageOffsetAlignment}, nonCoherentAtomSize {NonCoherentAtomSize}, maxPushConstantsSize {MaxPushConstantsSize}, timestampPeriod {TimestampPeriod} ns, graphics queue timestamp bits {TimestampBits}"
    )]
    private partial void LogDeviceLimits(
        ulong maxBufferSize,
        uint maxStorageBufferRange,
        ulong storageOffsetAlignment,
        ulong nonCoherentAtomSize,
        uint maxPushConstantsSize,
        float timestampPeriod,
        uint timestampBits
    );

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device memory: {Memory}")]
    private partial void LogMemory(string memory);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Logical device created: extensions [{Extensions}], graphics queue family {Graphics}, present queue family {Present}"
    )]
    private partial void LogDeviceCreated(string extensions, uint graphics, uint present);

    [LoggerMessage(Message = "[Vulkan] {Message}")]
    private partial void LogValidationMessage(LogLevel level, string message);
}
