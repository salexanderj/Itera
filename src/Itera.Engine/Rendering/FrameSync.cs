using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Itera.Engine.Rendering;

/// <summary>
/// Per frame slot: a command pool with one primary command buffer, an image-available semaphore, and an in-flight
/// fence. Per swapchain image: a render-finished semaphore.
/// </summary>
public sealed unsafe class FrameSync : IDisposable
{
    /// <summary>Frames the CPU may record ahead of the GPU.</summary>
    public const int FramesInFlight = 2;

    private readonly VulkanContext _context;
    private CommandPool[] _commandPools = [];
    private CommandBuffer[] _commandBuffers = [];
    private VkSemaphore[] _imageAvailable = [];
    private VkSemaphore[] _renderFinished = [];
    private Fence[] _inFlight = [];
    private bool _disposed;

    public FrameSync(VulkanContext context)
    {
        _context = context;
    }

    /// <summary>Index of the frame slot being recorded.</summary>
    public int CurrentFrame { get; private set; }

    public CommandBuffer CurrentCommandBuffer => _commandBuffers[CurrentFrame];

    public VkSemaphore CurrentImageAvailable => _imageAvailable[CurrentFrame];

    public Fence CurrentInFlightFence => _inFlight[CurrentFrame];

    /// <summary>Render-finished semaphores are per swapchain image so one is never reused before its present completes.</summary>
    public VkSemaphore RenderFinishedFor(uint imageIndex)
    {
        return _renderFinished[imageIndex];
    }

    public void Initialize(int swapchainImageCount)
    {
        var vk = _context.Vk;
        var device = _context.Device;

        // Transient: the pool is reset every frame, so the driver can use a cheaper allocation strategy.
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = _context.GraphicsQueueFamily,
        };
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        _commandPools = new CommandPool[FramesInFlight];
        _commandBuffers = new CommandBuffer[FramesInFlight];
        _imageAvailable = new VkSemaphore[FramesInFlight];
        _inFlight = new Fence[FramesInFlight];
        for (var i = 0; i < FramesInFlight; i++)
        {
            vk.CreateCommandPool(device, &poolInfo, null, out _commandPools[i]).ThrowIfFailed("vkCreateCommandPool");

            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPools[i],
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer commandBuffer;
            vk.AllocateCommandBuffers(device, &allocInfo, &commandBuffer).ThrowIfFailed("vkAllocateCommandBuffers");
            _commandBuffers[i] = commandBuffer;

            vk.CreateSemaphore(device, &semaphoreInfo, null, out _imageAvailable[i]).ThrowIfFailed("vkCreateSemaphore");
            vk.CreateFence(device, &fenceInfo, null, out _inFlight[i]).ThrowIfFailed("vkCreateFence");
        }

        CreateRenderFinished(swapchainImageCount);
    }

    /// <summary>
    /// Resets the current slot's command pool, which returns its command buffer to the initial state. Call only after
    /// waiting on the slot's fence: the GPU must be done with everything recorded from this pool.
    /// </summary>
    public void ResetCurrentCommandPool()
    {
        _context
            .Vk.ResetCommandPool(_context.Device, _commandPools[CurrentFrame], 0)
            .ThrowIfFailed("vkResetCommandPool");
    }

    /// <summary>Rebuilds the per-image semaphores after the swapchain changes. The caller must wait for device idle first.</summary>
    public void OnSwapchainRecreated(int swapchainImageCount)
    {
        DestroyRenderFinished();
        CreateRenderFinished(swapchainImageCount);
    }

    /// <summary>Moves to the next frame slot.</summary>
    public void Advance()
    {
        CurrentFrame = (CurrentFrame + 1) % FramesInFlight;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context.IsAbandoned)
        {
            return;
        }

        var vk = _context.Vk;
        var device = _context.Device;

        DestroyRenderFinished();
        foreach (var semaphore in _imageAvailable)
        {
            vk.DestroySemaphore(device, semaphore, null);
        }

        foreach (var fence in _inFlight)
        {
            vk.DestroyFence(device, fence, null);
        }

        // Destroying a pool frees the command buffers allocated from it.
        foreach (var pool in _commandPools)
        {
            vk.DestroyCommandPool(device, pool, null);
        }

        _imageAvailable = [];
        _inFlight = [];
        _commandBuffers = [];
        _commandPools = [];
    }

    private void CreateRenderFinished(int count)
    {
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        _renderFinished = new VkSemaphore[count];
        for (var i = 0; i < count; i++)
        {
            _context
                .Vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, out _renderFinished[i])
                .ThrowIfFailed("vkCreateSemaphore");
        }
    }

    private void DestroyRenderFinished()
    {
        foreach (var semaphore in _renderFinished)
        {
            _context.Vk.DestroySemaphore(_context.Device, semaphore, null);
        }

        _renderFinished = [];
    }
}
