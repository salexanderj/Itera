namespace Itera.Engine.Rendering;

/// <summary>
/// The world's GPU side in a run without graphics (<c>Engine:Window:Mode=None</c>): nothing is uploaded, so it is always
/// up to date, and each frame the loop builds counts as an upload, since the loading screen's World task waits for one
/// after the world is resident (<c>WorldLoadingTask</c>).
/// </summary>
public sealed class NoGpuWorld : IWorldOnGpu
{
    public bool IsUpToDate => true;

    public long Uploads { get; private set; }

    /// <summary>A frame was built: what an upload would have seen, it saw.</summary>
    public void FrameBuilt() => Uploads++;
}
