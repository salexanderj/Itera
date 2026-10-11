using System.Numerics;

namespace Itera.Engine.Audio;

/// <summary>
/// The audio thread's music: two decks, each a source of its own (outside the voice pool) at the listener, its stereo
/// channels sent left and right as they are, fed from its <see cref="MusicFeed"/> with <see cref="QueueDepth"/> buffers.
/// It takes the main thread's commands (<see cref="MusicExchange"/>) and cross-fades: a track played starts on a free deck
/// (or the quieter one, cut), and once its first block is queued it fades in as the other fades out, each heard at
/// sin(t·π/2) of the music volume as t moves between 0 and 1 over the fade, so halfway both are at 0.71 and the power
/// stays even. A command during a fade goes on from the gains then. A track's end (one that does not loop) or its file
/// failing (what is queued plays out) is reported back (<see cref="MusicExchange.ReportEnded"/>); a failing track still
/// lets the old one fade out. A third track during a cross-fade cuts the quieter deck at once.
/// Sound off (or the music volume at 0) sets the gains to 0 and the music streams on. Audio thread; allocates nothing.
/// </summary>
internal sealed class MusicDecks : IDisposable
{
    /// <summary>The decks.</summary>
    public const int Count = 2;

    /// <summary>Blocks queued on a deck's source: about 0.34 s at 48 kHz, on top of its ring's 0.7 s.</summary>
    public const int QueueDepth = 4;

    private readonly IAudioDevice _device;
    private readonly MusicExchange _exchange;
    private readonly MusicFeed[] _feeds;
    private readonly AudioStats _stats;
    private readonly Deck[] _decks = new Deck[Count];
    private readonly uint[] _buffers = new uint[Count * QueueDepth];
    private readonly MusicCommand[] _commands = new MusicCommand[MusicExchange.MaxCommands];
    private int _main = -1;
    private int _generation;
    private float _volume = 1;
    private bool _enabled = true;
    private bool _created;
    private bool _disposed;

    public MusicDecks(IAudioDevice device, MusicExchange exchange, MusicFeed[] feeds, AudioStats stats)
    {
        _device = device;
        _exchange = exchange;
        _feeds = feeds;
        _stats = stats;
    }

    /// <summary>
    /// One round at <paramref name="now"/> (seconds, any steady clock): the commands applied, each deck fed and its
    /// fade moved on, at <paramref name="volume"/> (silent when not <paramref name="enabled"/>). Makes the sources and
    /// buffers on its first call.
    /// </summary>
    public void Pump(double now, float volume, bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_created)
        {
            Create();
        }

        _volume = volume;
        _enabled = enabled;
        var count = _exchange.Take(_commands);
        for (var i = 0; i < count; i++)
        {
            Apply(in _commands[i], now);
        }

        for (var i = 0; i < Count; i++)
        {
            Feed(i, now);
        }

        for (var i = 0; i < Count; i++)
        {
            Fade(i, now);
            ref readonly var deck = ref _decks[i];
            _stats.SetMusicDeck(i, deck.State == DeckState.Idle ? -1 : deck.Track, deck.Gain);
        }
    }

    private void Create()
    {
        _created = true;
        for (var i = 0; i < Count; i++)
        {
            _decks[i].Source = _device.CreateSource();
            _decks[i].Track = -1;
        }

        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i] = _device.CreateBuffer();
        }
    }

    private void Apply(in MusicCommand command, double now)
    {
        if (command.Kind == MusicCommandKind.Stop)
        {
            for (var i = 0; i < Count; i++)
            {
                ref var deck = ref _decks[i];
                if (deck.State == DeckState.Waiting)
                {
                    // Not heard yet.
                    Cut(i);
                }
                else if (deck.State != DeckState.Idle && deck.Direction >= 0)
                {
                    FadeOut(ref deck, command.FadeSeconds, now);
                }
                else if (deck.Direction < 0)
                {
                    // Fading out already: no slower than the stop asks.
                    deck.FadeSeconds = Math.Min(deck.FadeSeconds, command.FadeSeconds);
                }
            }

            _main = -1;
            return;
        }

        var same = command.Restart ? -1 : Holding(command.Track);
        if (same >= 0)
        {
            ref var back = ref _decks[same];
            back.Play = command.Play;
            if (same == _main)
            {
                return;
            }

            // Fading out, or to: back in from where it is, and the one that took its place out.
            back.PendingOut = false;
            back.Direction = 1;
            back.FadeSeconds = command.FadeSeconds;
            back.LastPump = now;
            if (_main >= 0)
            {
                if (_decks[_main].State == DeckState.Waiting)
                {
                    Cut(_main);
                }
                else
                {
                    FadeOut(ref _decks[_main], command.FadeSeconds, now);
                }
            }

            _main = same;
            return;
        }

        var index = FreeDeck();
        for (var i = 0; i < Count; i++)
        {
            ref var other = ref _decks[i];
            if (i == index || other.State == DeckState.Idle || other.Direction < 0)
            {
                // Itself, silent, or fading out already at its own pace.
                continue;
            }

            if (other.State == DeckState.Waiting)
            {
                Cut(i);
            }
            else
            {
                // Out once the new track is heard, over the new track's fade; a fade in goes on meanwhile.
                other.PendingOut = true;
                other.OutSeconds = command.FadeSeconds;
            }
        }

        Start(index, in command);
        _main = index;
    }

    /// <summary>
    /// The deck playing (or about to) <paramref name="track"/>: the main one, else the louder; -1 for none. A track
    /// draining its end is not counted.
    /// </summary>
    private int Holding(int track)
    {
        if (_main >= 0 && _decks[_main].Track == track && _decks[_main].State != DeckState.Draining)
        {
            return _main;
        }

        var found = -1;
        for (var i = 0; i < Count; i++)
        {
            ref readonly var deck = ref _decks[i];
            if (
                deck.State is DeckState.Waiting or DeckState.Playing
                && deck.Track == track
                && (found < 0 || deck.Phase > _decks[found].Phase)
            )
            {
                found = i;
            }
        }

        return found;
    }

    /// <summary>A silent deck, or else the quieter, cut.</summary>
    private int FreeDeck()
    {
        var quietest = 0;
        for (var i = 0; i < Count; i++)
        {
            if (_decks[i].State == DeckState.Idle)
            {
                return i;
            }

            if (_decks[i].Phase < _decks[quietest].Phase)
            {
                quietest = i;
            }
        }

        Cut(quietest);
        return quietest;
    }

    private void Start(int index, in MusicCommand command)
    {
        ref var deck = ref _decks[index];
        deck.State = DeckState.Waiting;
        deck.Track = command.Track;
        deck.Play = command.Play;
        deck.Generation = ++_generation;
        deck.Phase = 0;
        deck.Direction = 0;
        deck.PendingOut = false;
        deck.FadeSeconds = command.FadeSeconds;
        deck.Started = false;
        deck.Gain = 0;
        _device.SetSource(deck.Source, Vector3.Zero, 0, 1, 1, atListener: true);
        _feeds[index].Request(deck.Generation, command.Track);
    }

    private static void FadeOut(ref Deck deck, float seconds, double now)
    {
        deck.Direction = -1;
        deck.PendingOut = false;
        deck.FadeSeconds = seconds;
        deck.LastPump = now;
    }

    /// <summary>Stops deck <paramref name="index"/> at once and asks its feed for nothing more.</summary>
    private void Cut(int index)
    {
        ref var deck = ref _decks[index];
        _device.StopSource(deck.Source);
        deck.State = DeckState.Idle;
        deck.Track = -1;
        deck.Direction = 0;
        deck.PendingOut = false;
        deck.Phase = 0;
        deck.Gain = 0;
        deck.Queued = 0;
        deck.Next = 0;
        deck.Started = false;
        deck.Generation = ++_generation;
        _feeds[index].Request(deck.Generation, -1);
        if (_main == index)
        {
            _main = -1;
        }
    }

    /// <summary>
    /// Takes the played buffers off deck <paramref name="index"/>'s source and queues the next blocks of its track; the
    /// first starts the cross-fade. Blocks of an older request are passed over.
    /// </summary>
    private void Feed(int index, double now)
    {
        ref var deck = ref _decks[index];
        var feed = _feeds[index];
        if (deck.State == DeckState.Idle)
        {
            while (feed.Readable is not null)
            {
                feed.Release();
            }

            return;
        }

        for (var processed = _device.ProcessedBuffers(deck.Source); processed > 0; processed--)
        {
            _device.UnqueueBuffer(deck.Source);
            deck.Queued--;
        }

        while (deck.State is DeckState.Waiting or DeckState.Playing && deck.Queued < QueueDepth)
        {
            if (feed.Readable is not { } block)
            {
                break;
            }

            if (block.Generation != deck.Generation)
            {
                feed.Release();
                continue;
            }

            if (block.Failed)
            {
                feed.Release();
                if (deck.State == DeckState.Waiting)
                {
                    // Never heard.
                    Ended(index, now);
                    return;
                }

                // What is queued plays out, then the deck ends as at a track's end.
                deck.State = DeckState.Draining;
                break;
            }

            if (block.Frames > 0)
            {
                if (deck.State == DeckState.Waiting)
                {
                    _device.SetDirectChannels(deck.Source, block.Channels == 2);
                    Begin(index, now);
                }

                var buffer = _buffers[index * QueueDepth + deck.Next];
                deck.Next = (deck.Next + 1) % QueueDepth;
                _device.FillBuffer(
                    buffer,
                    block.Samples.AsSpan(0, block.Frames * block.Channels),
                    block.Channels,
                    block.SampleRate
                );
                _device.QueueBuffer(deck.Source, buffer);
                deck.Queued++;
            }

            var end = block.End;
            feed.Release();
            if (end)
            {
                deck.State = deck.State == DeckState.Waiting ? DeckState.Idle : DeckState.Draining;
                break;
            }
        }

        if (deck.State == DeckState.Idle || (deck.State == DeckState.Draining && deck.Queued == 0))
        {
            // Played out (or a track with no sound at all).
            Ended(index, now);
            return;
        }

        if (deck.State != DeckState.Waiting && deck.Queued > 0 && !_device.IsPlaying(deck.Source))
        {
            if (deck.Started)
            {
                _stats.CountMusicUnderrun();
            }

            deck.Started = true;
            _device.Play(deck.Source);
        }
    }

    /// <summary>
    /// Deck <paramref name="index"/>'s track is heard: it fades in (or is full at once with no fade), and the decks that
    /// waited for it fade out.
    /// </summary>
    private void Begin(int index, double now)
    {
        ref var deck = ref _decks[index];
        deck.State = DeckState.Playing;
        deck.Direction = (sbyte)(deck.FadeSeconds > 0 ? 1 : 0);
        deck.Phase = deck.FadeSeconds > 0 ? 0 : 1;
        deck.LastPump = now;
        if (index == _main)
        {
            FadeOutWaiting(now);
        }
    }

    /// <summary>The decks waiting for a new track to be heard fade out now.</summary>
    private void FadeOutWaiting(double now)
    {
        for (var i = 0; i < Count; i++)
        {
            ref var other = ref _decks[i];
            if (other.PendingOut)
            {
                FadeOut(ref other, other.OutSeconds, now);
            }
        }
    }

    /// <summary>
    /// Deck <paramref name="index"/>'s track ended by itself (its end, or its file failed): reported, the deck freed, and
    /// when it was the one to be heard, the decks waiting for it fade out.
    /// </summary>
    private void Ended(int index, double now)
    {
        var play = _decks[index].Play;
        var main = index == _main;
        Cut(index);
        _exchange.ReportEnded(play);
        if (main)
        {
            FadeOutWaiting(now);
        }
    }

    /// <summary>Moves deck <paramref name="index"/>'s fade on by the time since the last round and sets its gain; one faded out stops.</summary>
    private void Fade(int index, double now)
    {
        ref var deck = ref _decks[index];
        if (deck.State == DeckState.Idle)
        {
            return;
        }

        var elapsed = (float)(now - deck.LastPump);
        deck.LastPump = now;
        if (deck.Direction != 0)
        {
            var step = deck.FadeSeconds > 0 ? elapsed / deck.FadeSeconds : 1;
            deck.Phase = Math.Clamp(deck.Phase + deck.Direction * step, 0, 1);
            if (deck.Direction > 0 && deck.Phase >= 1)
            {
                deck.Direction = 0;
            }
            else if (deck.Direction < 0 && deck.Phase <= 0)
            {
                Cut(index);
                return;
            }
        }

        var gain = _enabled ? _volume * MathF.Sin(deck.Phase * MathF.PI / 2) : 0;
        if (gain != deck.Gain)
        {
            _device.SetGain(deck.Source, gain);
            deck.Gain = gain;
        }
    }

    /// <summary>Stops the decks and frees their sources and buffers. Audio thread, or any thread once it has ended.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_created)
        {
            return;
        }

        foreach (ref readonly var deck in _decks.AsSpan())
        {
            _device.StopSource(deck.Source);
            _device.DeleteSource(deck.Source);
        }

        foreach (var buffer in _buffers)
        {
            _device.DeleteBuffer(buffer);
        }
    }

    private enum DeckState : byte
    {
        /// <summary>Silent, its source stopped.</summary>
        Idle,

        /// <summary>Its track asked for, its first block not yet come.</summary>
        Waiting,

        /// <summary>Heard.</summary>
        Playing,

        /// <summary>Its track's last block queued: it plays out, then ends.</summary>
        Draining,
    }

    private struct Deck
    {
        public uint Source;
        public DeckState State;
        public int Track;
        public int Play;
        public int Generation;

        /// <summary>Where its fade is, 0 (silent) to 1 (full): heard at sin(Phase·π/2) of the music volume.</summary>
        public float Phase;

        /// <summary>+1 fading in, -1 fading out, 0 steady.</summary>
        public sbyte Direction;

        /// <summary>Fades out over <see cref="OutSeconds"/> once the track to be heard has started.</summary>
        public bool PendingOut;

        public float OutSeconds;

        public float FadeSeconds;
        public double LastPump;
        public int Queued;
        public int Next;
        public bool Started;

        /// <summary>The gain last given its source (0 as it starts).</summary>
        public float Gain;
    }
}
