namespace Itera.Engine.Audio;

/// <summary>A block of decoded music in a <see cref="MusicFeed"/>.</summary>
internal sealed class MusicBlock
{
    /// <summary>The samples, the channels interleaved; <see cref="Frames"/> times <see cref="Channels"/> of them hold this block's.</summary>
    public short[] Samples { get; } = new short[MusicFeed.BlockFrames * 2];

    /// <summary>The frames the block holds (fewer than <see cref="MusicFeed.BlockFrames"/> only at a track's end).</summary>
    public int Frames;

    /// <summary>1 or 2.</summary>
    public int Channels;

    /// <summary>Frames a second.</summary>
    public int SampleRate;

    /// <summary>The request it was decoded for (<see cref="MusicFeed.Request"/>).</summary>
    public int Generation;

    /// <summary>The track ends after this block (one played once).</summary>
    public bool End;

    /// <summary>The track's file failed: nothing more comes for this request.</summary>
    public bool Failed;
}

/// <summary>
/// One music deck's sound on its way from the music thread (<see cref="MusicStreamer"/>) to the audio thread
/// (<see cref="MusicDecks"/>): a ring of <see cref="Blocks"/> blocks of <see cref="BlockFrames"/> frames of 16-bit
/// samples, written by the music thread alone and read by the audio thread alone, each tagged with the generation of the
/// request it was decoded for; and that request (a track and a generation), set by the audio thread. A block of an older
/// generation is passed over. At 48 kHz the ring holds about 0.7 s. Allocates nothing once made.
/// </summary>
internal sealed class MusicFeed
{
    /// <summary>The blocks the ring holds.</summary>
    public const int Blocks = 8;

    /// <summary>The frames a block holds.</summary>
    public const int BlockFrames = 4096;

    private readonly MusicBlock[] _ring = new MusicBlock[Blocks];
    private long _request = Pack(0, -1);
    private int _written;
    private int _read;

    public MusicFeed()
    {
        for (var i = 0; i < Blocks; i++)
        {
            _ring[i] = new MusicBlock();
        }
    }

    /// <summary>Asks for <paramref name="track"/> (-1: none) from its start, its blocks tagged <paramref name="generation"/>. Audio thread.</summary>
    public void Request(int generation, int track) => Interlocked.Exchange(ref _request, Pack(generation, track));

    /// <summary>The request the audio thread made last. Any thread.</summary>
    public (int Generation, int Track) Requested
    {
        get
        {
            var request = Interlocked.Read(ref _request);
            return ((int)(request >> 32), (int)request);
        }
    }

    /// <summary>The next block to write, or null while the ring is full. Music thread.</summary>
    public MusicBlock? Writable => _written - Volatile.Read(ref _read) < Blocks ? _ring[(uint)_written % Blocks] : null;

    /// <summary>Hands the block <see cref="Writable"/> gave to the audio thread. Music thread.</summary>
    public void Commit() => Volatile.Write(ref _written, _written + 1);

    /// <summary>The oldest block written and not yet read, or null. Audio thread.</summary>
    public MusicBlock? Readable => Volatile.Read(ref _written) - _read > 0 ? _ring[(uint)_read % Blocks] : null;

    /// <summary>Gives the block <see cref="Readable"/> gave back to the music thread. Audio thread.</summary>
    public void Release() => Volatile.Write(ref _read, _read + 1);

    private static long Pack(int generation, int track) => ((long)generation << 32) | (uint)track;
}
