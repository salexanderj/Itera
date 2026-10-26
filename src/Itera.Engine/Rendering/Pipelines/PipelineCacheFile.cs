using System.Buffers.Binary;

namespace Itera.Engine.Rendering.Pipelines;

/// <summary>
/// The file that keeps the run's pipeline cache (<see cref="VulkanContext.PipelineCache"/>) between runs:
/// <see cref="FileName"/> in the data folder. It holds what <c>vkGetPipelineCacheData</c> returned, which starts with
/// Vulkan's <c>VkPipelineCacheHeaderVersionOne</c>. A file is used only when that header names this device
/// (<see cref="HeaderMatches"/>). Reads and writes never throw for I/O: they return the problem, which the caller logs.
/// </summary>
internal static class PipelineCacheFile
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "PipelineCache.bin";

    /// <summary>
    /// The size of <c>VkPipelineCacheHeaderVersionOne</c>: the header's size, its version, the vendor id, the device id,
    /// and the 16-byte <c>pipelineCacheUUID</c>, each integer with its least significant byte first.
    /// </summary>
    public const int HeaderBytes = 32;

    /// <summary><c>VK_PIPELINE_CACHE_HEADER_VERSION_ONE</c>.</summary>
    public const uint HeaderVersionOne = 1;

    /// <summary>The size of <c>pipelineCacheUUID</c>.</summary>
    public const int UuidBytes = 16;

    /// <summary>
    /// True when <paramref name="data"/> starts with a version-one header of at least <see cref="HeaderBytes"/> that fits
    /// in it, for the device of <paramref name="vendorId"/>, <paramref name="deviceId"/>, and <paramref name="uuid"/>
    /// (<c>VkPhysicalDeviceProperties</c>).
    /// </summary>
    public static bool HeaderMatches(ReadOnlySpan<byte> data, uint vendorId, uint deviceId, ReadOnlySpan<byte> uuid)
    {
        if (data.Length < HeaderBytes || uuid.Length != UuidBytes)
        {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return headerSize >= HeaderBytes
            && headerSize <= (uint)data.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == HeaderVersionOne
            && BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) == vendorId
            && BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) == deviceId
            && data.Slice(16, UuidBytes).SequenceEqual(uuid);
    }

    /// <summary>
    /// The file's bytes, or null when there is no file or it cannot be read; <paramref name="problem"/> then says why
    /// (null when there is simply no file).
    /// </summary>
    public static byte[]? Read(string path, out string? problem)
    {
        problem = null;
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="data"/> to a temporary file and renames it over <paramref name="path"/>, as the shader cache
    /// writes its entries: a crash or a second game writing at once never leaves a partial file under the name. Makes the
    /// folder when it is missing. Returns why it failed, or null.
    /// </summary>
    public static string? Write(string path, ReadOnlySpan<byte> data)
    {
        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            using (var stream = File.Create(temporaryPath))
            {
                stream.Write(data);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
            {
                // The temporary file stays behind; it is never read.
            }

            return ex.Message;
        }
    }
}
