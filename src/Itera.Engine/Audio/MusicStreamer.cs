using Itera.Engine.Audio.Files;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Audio;

/// <summary>
/// The music thread's work: for each deck it follows the track the audio thread asks for (<see cref="MusicFeed.Request"/>),
/// opens its file, and decodes ahead into the deck's ring of blocks, as 16-bit samples in the file's channels and rate. A
/// looping track goes round its loop points (or whole) with no gap: the file is read up to the loop's end, and the rest of
/// that block from a second decoder of the same file waiting at the loop's start, which the two then swap. The waiting
/// decoder is brought there between blocks, a few thousand frames a round, since NVorbis seeks by decoding from the
/// file's start (<see cref="VorbisDecoder.Seek"/>); a WAV file seeks there at once. A file that fails is logged at Error
/// (once a track) and its deck told so. Runs on its own thread (<see cref="Run"/>), or one <see cref="Pump"/> at a time in
/// tests. NVorbis allocates as it decodes; the rest allocates nothing but as a track opens.
/// </summary>
internal sealed partial class MusicStreamer
{
    /// <summary>How long the thread sleeps when every ring is full, in milliseconds.</summary>
    private const int IdleMilliseconds = 10;

    /// <summary>Blocks of frames a waiting decoder moves on by a round.</summary>
    private const int PrepareBlocksPerRound = 8;

    private readonly IReadOnlyList<MusicTrack> _tracks;
    private readonly MusicFeed[] _feeds;
    private readonly Func<string, ISoundDecoder> _open;
    private readonly ILogger _logger;
    private readonly Deck[] _decks;
    private readonly bool[] _logged;
    private readonly float[] _samples = new float[MusicFeed.BlockFrames * 2];
    private readonly float[] _skipped = new float[MusicFeed.BlockFrames * 2];
    private long _decodedMicroseconds;
    private long _allocated;
    private volatile bool _stopping;

    public MusicStreamer(
        IReadOnlyList<MusicTrack> tracks,
        MusicFeed[] feeds,
        Func<string, ISoundDecoder> open,
        ILogger logger
    )
    {
        _tracks = tracks;
        _feeds = feeds;
        _open = open;
        _logger = logger;
        _decks = new Deck[feeds.Length];
        for (var i = 0; i < _decks.Length; i++)
        {
            _decks[i] = new Deck();
        }

        _logged = new bool[tracks.Count];
    }

    /// <summary>The music decoded since the start, in seconds. Any thread.</summary>
    public double DecodedSeconds => Interlocked.Read(ref _decodedMicroseconds) / 1e6;

    /// <summary>The bytes the music thread has allocated (NVorbis's, mostly). Any thread.</summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocated);

    /// <summary>
    /// The music thread: decodes until <see cref="Stop"/>, then closes the files. Should it fail, it logs why and ends:
    /// the music stops, and the game and its sounds go on.
    /// </summary>
    public void Run()
    {
        try
        {
            while (!_stopping)
            {
                var busy = Pump();
                Interlocked.Exchange(ref _allocated, GC.GetAllocatedBytesForCurrentThread());
                if (!busy)
                {
                    Thread.Sleep(IdleMilliseconds);
                }
            }
        }
        catch (Exception exception)
        {
            LogFaulted(exception);
        }
        finally
        {
            CloseFiles();
        }
    }

    /// <summary>
    /// Closes every deck's files, logging any that fails to close. <see cref="Run"/> does it as it ends; a caller that
    /// pumps the streamer itself (<see cref="Pump"/>) does it when done, so no file stays open: Windows keeps an open
    /// file from being deleted. A later round opens them again.
    /// </summary>
    public void CloseFiles()
    {
        foreach (var deck in _decks)
        {
            try
            {
                Close(deck);
            }
            catch (Exception exception)
            {
                LogFaulted(exception);
            }
        }
    }

    /// <summary>Asks <see cref="Run"/> to end after its round.</summary>
    public void Stop() => _stopping = true;

    /// <summary>
    /// One round: each deck's request followed and its ring filled, then the waiting decoders moved on. True when it did
    /// anything.
    /// </summary>
    public bool Pump()
    {
        var busy = false;
        for (var i = 0; i < _decks.Length; i++)
        {
            busy |= Fill(i);
        }

        for (var i = 0; i < _decks.Length; i++)
        {
            busy |= Prepare(_decks[i], PrepareBlocksPerRound);
        }

        return busy;
    }

    /// <summary>Follows deck <paramref name="index"/>'s request and decodes into its ring until it is full or the track ends.</summary>
    private bool Fill(int index)
    {
        var feed = _feeds[index];
        var deck = _decks[index];
        var (generation, track) = feed.Requested;
        if (generation != deck.Generation)
        {
            Close(deck);
            deck.Generation = generation;
            deck.Track = track;
            if (track >= 0)
            {
                OpenTrack(deck);
            }
        }

        var wrote = false;
        while (deck.Track >= 0 && !deck.Done && feed.Writable is { } block)
        {
            block.Generation = deck.Generation;
            block.End = false;
            block.Failed = false;
            block.Frames = 0;
            if (!deck.Failed)
            {
                try
                {
                    Decode(deck, block);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Fail(deck, exception);
                }
            }

            if (deck.Failed)
            {
                block.Frames = 0;
                block.Failed = true;
                deck.Done = true;
            }

            feed.Commit();
            wrote = true;
        }

        return wrote;
    }

    private void OpenTrack(Deck deck)
    {
        var track = _tracks[deck.Track];
        try
        {
            deck.Decoder = _open(track.Path);
            var loop = deck.Decoder.LoopPoints;
            deck.Looping = track.Loop;
            deck.LoopStart = loop.IsSet ? loop.Start : 0;
            deck.LoopEnd = loop.IsSet ? loop.End : long.MaxValue;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail(deck, exception);
        }
    }

    /// <summary>
    /// Fills <paramref name="block"/> from the deck's decoder: a looping track read up to its loop's end, then on from the
    /// waiting decoder at the loop's start; one played once ends the block it ends in.
    /// </summary>
    private void Decode(Deck deck, MusicBlock block)
    {
        var decoder = deck.Decoder!;
        var channels = decoder.Channels;
        var frames = 0;
        while (frames < MusicFeed.BlockFrames)
        {
            var want = MusicFeed.BlockFrames - frames;
            if (deck.Looping)
            {
                want = (int)Math.Min(want, deck.LoopEnd - deck.Position);
            }

            var read = want > 0 ? deck.Decoder!.Read(_samples.AsSpan(frames * channels, want * channels)) : 0;
            frames += read;
            deck.Position += read;
            deck.SinceSeam += read;
            if (read > 0 && (!deck.Looping || deck.Position < deck.LoopEnd))
            {
                continue;
            }

            // The file's end, or the loop's: played once, a loop with nothing in it, or one whose start cannot be
            // reached again (logged) ends here.
            if (!deck.Looping || deck.SinceSeam == 0 || !Seam(deck))
            {
                block.End = true;
                deck.Done = true;
                break;
            }
        }

        var samples = _samples.AsSpan(0, frames * channels);
        var destination = block.Samples.AsSpan(0, samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            destination[i] = SoundFiles.ToInt16(samples[i]);
        }

        block.Frames = frames;
        block.Channels = channels;
        block.SampleRate = decoder.SampleRate;
        Interlocked.Add(ref _decodedMicroseconds, frames * 1_000_000L / decoder.SampleRate);
    }

    /// <summary>
    /// Goes from the loop's end to its start: the waiting decoder (brought there now if it is not yet) takes over. False,
    /// logged, when it could not be readied: the track ends at the loop's end.
    /// </summary>
    private bool Seam(Deck deck)
    {
        Prepare(deck, int.MaxValue);
        if (deck.SpareProblem is not null || deck.Spare is null || deck.SparePosition != deck.LoopStart)
        {
            Log(deck, deck.SpareProblem ?? "its loop's start cannot be reached again");
            return false;
        }

        (deck.Decoder, deck.Spare) = (deck.Spare, deck.Decoder);
        deck.Position = deck.LoopStart;
        deck.SinceSeam = 0;
        // The decoder that reached the end goes back to the start before it waits at the loop's start again.
        deck.SparePosition = -1;
        return true;
    }

    /// <summary>
    /// Moves a looping deck's waiting decoder towards the loop's start by at most <paramref name="blocks"/> blocks of
    /// frames (opening it first). True when it did anything. A failure is kept for the loop's end
    /// (<see cref="Deck.SpareProblem"/>): the track plays on until then.
    /// </summary>
    private bool Prepare(Deck deck, int blocks)
    {
        if (!deck.Looping || deck.Done || deck.Failed || deck.Decoder is null || deck.SpareProblem is not null)
        {
            return false;
        }

        try
        {
            var opened = false;
            if (deck.Spare is null)
            {
                deck.Spare = _open(_tracks[deck.Track].Path);
                deck.SparePosition = 0;
                opened = true;
            }

            if (deck.SparePosition < 0)
            {
                deck.Spare.Seek(0);
                deck.SparePosition = 0;
            }

            if (deck.SparePosition == deck.LoopStart)
            {
                return opened;
            }

            if (deck.Spare is WavDecoder)
            {
                deck.Spare.Seek(deck.LoopStart);
                deck.SparePosition = deck.LoopStart;
                return true;
            }

            var channels = deck.Spare.Channels;
            for (var i = 0; i < blocks && deck.SparePosition < deck.LoopStart; i++)
            {
                var want = (int)Math.Min(MusicFeed.BlockFrames, deck.LoopStart - deck.SparePosition);
                var read = deck.Spare.Read(_skipped.AsSpan(0, want * channels));
                if (read == 0)
                {
                    // The loop's start lies past the sound's end: the loop goes round the whole sound instead.
                    deck.LoopStart = 0;
                    deck.Spare.Seek(0);
                    deck.SparePosition = 0;
                    break;
                }

                deck.SparePosition += read;
            }

            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            deck.SpareProblem = Reason(exception);
            return true;
        }
    }

    /// <summary>The deck's file failed: logged, its deck told.</summary>
    private void Fail(Deck deck, Exception exception)
    {
        deck.Failed = true;
        Log(deck, Reason(exception));
    }

    /// <summary>Logs at Error why the deck's track stops: once a track, and not when its header failed at the start.</summary>
    private void Log(Deck deck, string reason)
    {
        var track = _tracks[deck.Track];
        if (_logged[deck.Track] || track.Error is not null)
        {
            return;
        }

        _logged[deck.Track] = true;
        LogTrackFailed(track.Name, track.Path, reason);
    }

    private static string Reason(Exception exception) =>
        exception is SoundFileException ? exception.Message : exception.ToString();

    private static void Close(Deck deck)
    {
        deck.Decoder?.Dispose();
        deck.Spare?.Dispose();
        deck.Decoder = null;
        deck.Spare = null;
        deck.Track = -1;
        deck.Position = 0;
        deck.SparePosition = -1;
        deck.SinceSeam = 0;
        deck.Looping = false;
        deck.Done = false;
        deck.Failed = false;
        deck.SpareProblem = null;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Audio: music track {Name}: the file {Path} cannot be read ({Reason}); it stops"
    )]
    private partial void LogTrackFailed(string name, string path, string reason);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Audio: the music thread failed; the music stops and the game goes on"
    )]
    private partial void LogFaulted(Exception exception);

    /// <summary>What the music thread keeps for one deck.</summary>
    private sealed class Deck
    {
        public int Generation;
        public int Track = -1;
        public ISoundDecoder? Decoder;

        /// <summary>A looping track's second decoder, waiting at the loop's start (at <see cref="SparePosition"/>; -1: to go back to the start first).</summary>
        public ISoundDecoder? Spare;

        public long Position;
        public long SparePosition = -1;
        public long LoopStart;
        public long LoopEnd;

        /// <summary>Frames read since the last seam: none means the loop holds nothing.</summary>
        public long SinceSeam;

        public bool Looping;

        /// <summary>Why the waiting decoder could not be readied, for the loop's end; null for nothing.</summary>
        public string? SpareProblem;

        /// <summary>No more blocks for this request: its track ended or failed.</summary>
        public bool Done;

        public bool Failed;
    }
}
