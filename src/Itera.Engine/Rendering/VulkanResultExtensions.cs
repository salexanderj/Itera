using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>Helpers for Vulkan result codes (a game's own passes check theirs with them too).</summary>
public static class VulkanResultExtensions
{
    /// <summary>Throws when the result is not <see cref="Result.Success"/>.</summary>
    public static void ThrowIfFailed(this Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new VulkanException(operation, result);
        }
    }
}

/// <summary>A Vulkan call returned an error code.</summary>
public sealed class VulkanException : Exception
{
    public VulkanException(string operation, Result result)
        : base($"{operation} failed with {result}.")
    {
        Operation = operation;
        Result = result;
    }

    public VulkanException() { }

    public VulkanException(string message)
        : base(message) { }

    public VulkanException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Name of the call that failed.</summary>
    public string Operation { get; } = string.Empty;

    /// <summary>The returned code.</summary>
    public Result Result { get; }
}
