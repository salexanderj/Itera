using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Particles;

/// <summary>
/// The particle counts of the last frame the GPU finished: read back by the render thread after a slot's fence
/// (<c>ParticlePass.Collect</c>) and read by the main thread for the stats line and the console's <c>particles</c>.
/// Any thread; each value is read and written whole.
/// </summary>
public sealed class ParticleStats
{
    private int _living;
    private int _cubes;
    private int _sprites;
    private long _overwritten;

    /// <summary>Particles alive after the last finished frame.</summary>
    public int Living => Volatile.Read(ref _living);

    /// <summary>Cubes it drew.</summary>
    public int Cubes => Volatile.Read(ref _cubes);

    /// <summary>Sprites it drew.</summary>
    public int Sprites => Volatile.Read(ref _sprites);

    /// <summary>Living particles overwritten by new ones since the start, because the pool was full.</summary>
    public long Overwritten => Interlocked.Read(ref _overwritten);

    /// <summary>Takes a finished frame's counters. Render thread.</summary>
    public void Record(in ParticleCounters counters)
    {
        Volatile.Write(ref _living, (int)counters.Living);
        Volatile.Write(ref _cubes, (int)counters.CubeInstanceCount);
        Volatile.Write(ref _sprites, (int)counters.SpriteInstanceCount);
        Interlocked.Add(ref _overwritten, counters.Overwritten);
    }

    /// <summary>Records that no particles are alive: the frames stopped simulating them.</summary>
    public void Clear()
    {
        Volatile.Write(ref _living, 0);
        Volatile.Write(ref _cubes, 0);
        Volatile.Write(ref _sprites, 0);
    }
}
