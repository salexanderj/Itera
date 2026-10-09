namespace Itera.Engine.Audio;

/// <summary>
/// What the audio thread has done, for the console's <c>sound</c> and the log. The audio thread writes; any thread
/// reads (each value on its own, so a reading may mix two moments).
/// </summary>
public sealed class AudioStats
{
    private int _oneShots;
    private int _streams;
    private long _played;
    private long _culled;
    private long _stolen;
    private long _refused;
    private long _underruns;
    private long _late;
    private long _deviceErrors;
    private volatile bool _connected = true;
    private int _rendered;
    private long _renderedBytes;

    /// <summary>One-shot sounds playing.</summary>
    public int OneShots => Volatile.Read(ref _oneShots);

    /// <summary>Streams playing (fading out included).</summary>
    public int Streams => Volatile.Read(ref _streams);

    /// <summary>Sounds started since the start.</summary>
    public long Played => Interlocked.Read(ref _played);

    /// <summary>Sounds not started because they would be too quiet where the listener is.</summary>
    public long Culled => Interlocked.Read(ref _culled);

    /// <summary>Quieter sounds stopped to make room for louder ones.</summary>
    public long Stolen => Interlocked.Read(ref _stolen);

    /// <summary>Sounds not started because every voice played something louder.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Times a stream's queue ran dry before the audio thread refilled it.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>One-shots dropped because their recipe was still rendering (at start).</summary>
    public long Late => Interlocked.Read(ref _late);

    /// <summary>False while the audio device has lost its output and no other was found.</summary>
    public bool Connected => _connected;

    /// <summary>Rounds in which the audio device reported an error.</summary>
    public long DeviceErrors => Interlocked.Read(ref _deviceErrors);

    /// <summary>One-shot recipes whose variations are in buffers, failed ones included.</summary>
    public int RenderedRecipes => Volatile.Read(ref _rendered);

    /// <summary>Bytes of rendered variations in OpenAL buffers.</summary>
    public long RenderedBytes => Interlocked.Read(ref _renderedBytes);

    internal void SetPlaying(int oneShots, int streams)
    {
        Volatile.Write(ref _oneShots, oneShots);
        Volatile.Write(ref _streams, streams);
    }

    internal void CountPlayed() => Interlocked.Increment(ref _played);

    internal void CountCulled() => Interlocked.Increment(ref _culled);

    internal void CountStolen() => Interlocked.Increment(ref _stolen);

    internal void CountRefused() => Interlocked.Increment(ref _refused);

    internal void CountUnderrun() => Interlocked.Increment(ref _underruns);

    internal void CountLate(int sounds) => Interlocked.Add(ref _late, sounds);

    internal void CountDeviceError() => Interlocked.Increment(ref _deviceErrors);

    internal void SetConnected(bool connected) => _connected = connected;

    internal void CountRendered(long bytes)
    {
        Interlocked.Increment(ref _rendered);
        Interlocked.Add(ref _renderedBytes, bytes);
    }
}
