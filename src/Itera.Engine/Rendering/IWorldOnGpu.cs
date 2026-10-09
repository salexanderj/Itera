namespace Itera.Engine.Rendering;

/// <summary>
/// The match's world as the GPU has it, for what waits until the picture shows all of it: the loading screen's world
/// task and the benchmark. <see cref="Meshes.WorldMeshes"/> provides it; a test can give its own. Main thread.
/// </summary>
public interface IWorldOnGpu
{
    /// <summary>
    /// True when the last upload left nothing waiting: after that frame's copies the GPU holds every resident column and
    /// every change of the match's world.
    /// </summary>
    bool IsUpToDate { get; }

    /// <summary>How many uploads have run: an upload after a given moment has seen the world as it was then.</summary>
    long Uploads { get; }
}
