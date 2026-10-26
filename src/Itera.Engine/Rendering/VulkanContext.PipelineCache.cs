using Itera.Engine.Rendering.Pipelines;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// The run's pipeline cache: <see cref="PipelineFactory"/> makes every pipeline through it, and it is kept between runs
/// in the data folder (<see cref="PipelineCacheFile"/>).
/// </summary>
public sealed unsafe partial class VulkanContext
{
    private readonly string _pipelineCachePath;
    private uint _vendorId;
    private uint _deviceId;
    private readonly byte[] _pipelineCacheUuid = new byte[PipelineCacheFile.UuidBytes];
    private nuint _pipelineCacheLoadedBytes;

    /// <summary>
    /// The pipeline cache every pipeline is made with, from the device's step to its end; null before, or when the driver
    /// made none. Pipelines are
    /// made on the main thread before the render thread starts, and on the render thread after (reloads, a change of
    /// the samples, the bake), so no two threads use it at once.
    /// </summary>
    public PipelineCache PipelineCache { get; private set; }

    /// <summary>
    /// Makes the pipeline cache, filled from the data folder's file when its header names this device; otherwise empty.
    /// A file that cannot be read or is refused leaves the cache empty, and a cache the driver will not make leaves none
    /// (pipelines are then made without one); the run goes on.
    /// </summary>
    private void CreatePipelineCache()
    {
        var data = PipelineCacheFile.Read(_pipelineCachePath, out var problem);
        if (problem is not null)
        {
            LogPipelineCacheUnavailable(_pipelineCachePath, problem);
        }
        else if (data is null)
        {
            LogPipelineCacheMissing(_pipelineCachePath);
        }
        else if (!PipelineCacheFile.HeaderMatches(data, _vendorId, _deviceId, _pipelineCacheUuid))
        {
            LogPipelineCacheRefused(_pipelineCachePath, data.Length);
            data = null;
        }

        if (data is not null && TryCreatePipelineCache(data) is { } filled)
        {
            PipelineCache = filled;
            _pipelineCacheLoadedBytes = (nuint)data.Length;
            LogPipelineCacheLoaded(data.Length, _pipelineCachePath);
        }
        else
        {
            PipelineCache = TryCreatePipelineCache([]) ?? default;
            _pipelineCacheLoadedBytes = 0;
        }

        if (PipelineCache.Handle != 0)
        {
            SetObjectName(ObjectType.PipelineCache, PipelineCache.Handle, "pipeline cache");
        }
    }

    /// <summary>A pipeline cache holding <paramref name="data"/>, or null when the driver refuses it.</summary>
    private PipelineCache? TryCreatePipelineCache(byte[] data)
    {
        fixed (byte* bytes = data)
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)data.Length,
                PInitialData = data.Length > 0 ? bytes : null,
            };
            var result = Vk.CreatePipelineCache(Device, &info, null, out var cache);
            if (result == Result.Success)
            {
                return cache;
            }

            LogPipelineCacheNotCreated(data.Length, result);
            return null;
        }
    }

    /// <summary>
    /// Writes the cache's data to the data folder's file when it grew since the run began, then destroys the cache.
    /// Before the device is destroyed; no thread makes pipelines any more. A failure is logged and the shutdown goes on.
    /// </summary>
    private void SaveAndDestroyPipelineCache()
    {
        if (PipelineCache.Handle == 0)
        {
            return;
        }

        try
        {
            SavePipelineCache();
        }
        finally
        {
            Vk.DestroyPipelineCache(Device, PipelineCache, null);
            PipelineCache = default;
        }
    }

    private void SavePipelineCache()
    {
        nuint size = 0;
        var result = Vk.GetPipelineCacheData(Device, PipelineCache, &size, null);
        if (result != Result.Success)
        {
            LogPipelineCacheNotRead(result);
            return;
        }

        if (size <= _pipelineCacheLoadedBytes)
        {
            LogPipelineCacheUnchanged((long)size);
            return;
        }

        var data = new byte[(int)size];
        fixed (byte* bytes = data)
        {
            result = Vk.GetPipelineCacheData(Device, PipelineCache, &size, bytes);
        }

        // Incomplete would mean the cache grew between the two calls, which nothing does here: the data is not written.
        if (result != Result.Success)
        {
            LogPipelineCacheNotRead(result);
            return;
        }

        if (PipelineCacheFile.Write(_pipelineCachePath, data.AsSpan(0, (int)size)) is { } problem)
        {
            LogPipelineCacheUnavailable(_pipelineCachePath, problem);
            return;
        }

        LogPipelineCacheSaved((long)size, _pipelineCachePath);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pipeline cache: no file at {Path}; starting empty")]
    private partial void LogPipelineCacheMissing(string path);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Pipeline cache: {Path} ({Bytes} bytes) was made by another device or driver; starting empty"
    )]
    private partial void LogPipelineCacheRefused(string path, int bytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pipeline cache: loaded {Bytes} bytes from {Path}")]
    private partial void LogPipelineCacheLoaded(int bytes, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline cache: {Path} could not be read or written: {Reason}")]
    private partial void LogPipelineCacheUnavailable(string path, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Pipeline cache: vkCreatePipelineCache with {Bytes} bytes returned {Result}"
    )]
    private partial void LogPipelineCacheNotCreated(int bytes, Result result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline cache: vkGetPipelineCacheData returned {Result}")]
    private partial void LogPipelineCacheNotRead(Result result);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Pipeline cache: {Bytes} bytes, no larger than the file; not written"
    )]
    private partial void LogPipelineCacheUnchanged(long bytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pipeline cache: wrote {Bytes} bytes to {Path}")]
    private partial void LogPipelineCacheSaved(long bytes, string path);
}
