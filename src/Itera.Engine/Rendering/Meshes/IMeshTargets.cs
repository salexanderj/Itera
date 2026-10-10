using Itera.Engine.Rendering.GpuData;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// Where <see cref="WorldMeshes"/> writes a frame's draws and <see cref="MeshView"/> its lighting record and lights, and
/// how large the heap they fill is: <see cref="MeshPass"/>, or memory of a test's own.
/// </summary>
internal interface IMeshDrawTarget
{
    /// <summary>The heap's size in bytes, a whole number of granules.</summary>
    ulong HeapBytes { get; }

    /// <summary>The draws of a frame slot, <see cref="MeshPass.MaxRecords"/> long.</summary>
    Span<MeshDraw> DrawsOf(int slot);

    /// <summary>
    /// The indirect commands of list <paramref name="list"/> of a frame slot (0 the camera's, 1 and 2 the near and far
    /// shadow cascades'), <see cref="MeshPass.MaxDraws"/> long.
    /// </summary>
    Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list);

    /// <summary>The lighting record of a frame slot.</summary>
    ref MeshLighting LightingOf(int slot);

    /// <summary>The point lights of a frame slot, <see cref="MeshPass.MostLights"/> long.</summary>
    Span<MeshLight> LightsOf(int slot);

    /// <summary>
    /// The model draws of list <paramref name="list"/> of a frame slot, <see cref="MeshPass.MaxModelRecords"/> long; a
    /// command's first instance is <see cref="MeshPass.ModelRecordBase"/> plus its first draw's place in it.
    /// </summary>
    Span<MeshModelDraw> ModelDrawsOf(int slot, int list);

    /// <summary>The model commands of list <paramref name="list"/> of a frame slot, <see cref="MeshPass.MaxModelCommands"/> long.</summary>
    Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list);
}

/// <summary>
/// The staging memory a frame's uploads go through: <see cref="Memory.StagingBuffer"/>, or memory of a test's own.
/// </summary>
internal unsafe interface IStagingRegions
{
    /// <summary>Bytes in each slot's region.</summary>
    ulong RegionBytes { get; }

    /// <summary>The offset of a slot's region in the staging buffer, for the copy regions.</summary>
    ulong RegionOffset(int slot);

    /// <summary>The mapped start of a slot's region.</summary>
    byte* Region(int slot);

    /// <summary>Makes the first <paramref name="bytes"/> of a slot's region visible to the GPU.</summary>
    void Flush(int slot, ulong bytes);
}
