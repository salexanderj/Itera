using Itera.Engine.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// <see cref="VulkanContext"/> before <c>Initialize</c> has selected the device: its values (limits, memory, queue
/// families, timestamp bits, name) throw rather than read as zeros; its handles stay null, which a Vulkan call refuses. The frame renderer read the storage buffer range in its constructor, which runs before the
/// device is selected, and so drew every frame at render scale 0.05 (2026-10-03 to 2026-10-04).
/// </summary>
public sealed class VulkanContextTests
{
    [Fact]
    public void The_devices_values_throw_before_the_device_is_selected()
    {
        using var context = new VulkanContext(Options.Create(new EngineOptions()), NullLogger<VulkanContext>.Instance);

        Assert.Throws<InvalidOperationException>(() => context.Limits);
        Assert.Throws<InvalidOperationException>(() => context.MemoryProperties);
        Assert.Throws<InvalidOperationException>(() => context.MaxBufferSize);
        Assert.Throws<InvalidOperationException>(() => context.GraphicsQueueFamily);
        Assert.Throws<InvalidOperationException>(() => context.PresentQueueFamily);
        Assert.Throws<InvalidOperationException>(() => context.GraphicsTimestampValidBits);
        Assert.Throws<InvalidOperationException>(() => context.DeviceName);
        // The handles stay null, so a pass's Dispose in a run that never reached the Vulkan step does not throw.
        Assert.Equal(nint.Zero, context.Device.Handle);
    }
}
