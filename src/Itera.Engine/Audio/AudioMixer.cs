using System.Numerics;
using Itera.Engine.Audio.Synthesis;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Audio;

/// <summary>
/// The audio thread's work, one <see cref="Pump"/> at a time: takes what the main thread posted
/// (<see cref="AudioExchange"/>), places the listener, starts one-shots, keeps sustained streams going block by block,
/// frees voices whose sounds ended, and puts each one-shot recipe the <see cref="RecipeRenderer"/> finished into buffers
/// (one a round, after the streams are fed). The render thread renders them, so sounds play while recipes render; a
/// one-shot whose recipe is not in buffers yet is dropped as late. Tests
/// may have it render one recipe a round itself. A pool of <see cref="Voices"/> sources plays everything. A sound too
/// quiet where the listener is (under <see cref="CullGain"/>) is never started; when every voice plays, a louder sound
/// takes the quietest one-shot's voice (Farnell's point that sound, like pictures, needs a budget and a level of
/// detail). A stream recipe whose generator throws is ended and not started again; the rest play on. Every call to <see cref="IAudioDevice"/> comes from here, on one thread. After the
/// recipes are in buffers it allocates nothing, except the first generator of each stream recipe beyond those made.
/// </summary>
public sealed partial class AudioMixer : IDisposable
{
    /// <summary>A stream's block, in samples at the mixer's rate: about 11 ms at 48 kHz.</summary>
    public const int BlockSamples = 512;

    /// <summary>Blocks queued ahead on each stream: about 43 ms at 48 kHz, room for the thread's waits.</summary>
    public const int QueueDepth = 4;

    /// <summary>
    /// The least gain at the listener (the recipe's and the event's gain, over distance) worth starting: about -54 dB.
    /// </summary>
    public const float CullGain = 0.002f;

    /// <summary>
    /// The loudest a sound plays at the listener, the gain after distance: each source's AL_MAX_GAIN, and the most
    /// <see cref="Loudness"/> gives, so culling and taking voices rank sounds as they are heard. A recipe's gain is at
    /// most 4 (<see cref="SoundRecipes"/>).
    /// </summary>
    public const float MaxGain = 4;

    /// <summary>How often the mixer checks that the device still has an output, in seconds.</summary>
    public const double ConnectionCheckSeconds = 1;

    /// <summary>How long a stream the game stopped sustaining takes to fade out, in seconds.</summary>
    public const float ReleaseSeconds = 0.08f;

    private readonly IAudioDevice _device;
    private readonly SoundRecipes _recipes;
    private readonly AudioStats _stats;
    private readonly AudioExchange _exchange;
    private readonly ILogger _logger;
    private readonly RecipeRenderer _renderer;
    private readonly bool _renderOnPump;
    private readonly bool[] _failed = new bool[SoundRecipes.MaxRecipes];
    private readonly bool[] _brokenStreams = new bool[SoundRecipes.MaxRecipes];
    private readonly int _sampleRate;
    private readonly Voice[] _voices;
    private readonly uint[] _streamBuffers;
    private readonly SoundEvent[] _events = new SoundEvent[AudioExchange.MaxEvents];
    private readonly SoundLoop[] _loops = new SoundLoop[AudioExchange.MaxLoops];
    private readonly Bank?[] _banks = new Bank?[SoundRecipes.MaxRecipes];
    private readonly List<SoundGenerator>?[] _idleGenerators = new List<SoundGenerator>?[SoundRecipes.MaxRecipes];
    private readonly float[] _block = new float[BlockSamples];
    private readonly short[] _pcm = new short[BlockSamples];
    private SoundRandom _random = new(0x2545_F491);
    private ListenerPose _listener = ListenerPose.Default;
    private int _streamsPrepared;
    private bool _errorLogged;
    private double _nextConnectionCheck;
    private bool _created;
    private bool _disposed;

    public AudioMixer(
        IAudioDevice device,
        SoundRecipes recipes,
        AudioStats stats,
        AudioExchange exchange,
        int voices,
        int sampleRate,
        ILogger logger,
        SoundCache? cache = null,
        bool renderOnPump = true
    )
    {
        _device = device;
        _renderer = new RecipeRenderer(recipes, sampleRate, cache, logger);
        _renderOnPump = renderOnPump;
        _recipes = recipes;
        _stats = stats;
        _exchange = exchange;
        _logger = logger;
        _sampleRate = sampleRate;
        _voices = new Voice[voices];
        _streamBuffers = new uint[voices * QueueDepth];
    }

    /// <summary>How many sources the pool holds.</summary>
    public int Voices => _voices.Length;

    /// <summary>What renders the one-shot recipes; its <see cref="RecipeRenderer.Run"/> is the render thread's work.</summary>
    internal RecipeRenderer Renderer => _renderer;

    /// <summary>
    /// One round of the audio thread's work at <paramref name="nowSeconds"/> (any steady clock). Makes the sources and
    /// stream buffers on its first call.
    /// </summary>
    public void Pump(double nowSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_created)
        {
            CreateVoices();
        }

        if (_renderOnPump)
        {
            _renderer.RenderNext();
        }

        if (nowSeconds >= _nextConnectionCheck)
        {
            _nextConnectionCheck = nowSeconds + ConnectionCheckSeconds;
            KeepConnected();
        }

        PrepareStreams();
        _exchange.Take(
            _events,
            out var eventCount,
            _loops,
            out var loopCount,
            out _listener,
            out var volume,
            out var enabled,
            out var shift
        );
        _device.SetListener(_listener.Position, _listener.Forward, _listener.Up, volume);
        if (shift != Vector3.Zero)
        {
            MoveVoices(shift);
        }

        if (!enabled)
        {
            StopAll();
            TakeRendered();
            TakeDeviceError();
            _stats.SetPlaying(0, 0);
            return;
        }

        EndFinishedOneShots();
        SustainLoops(loopCount, nowSeconds);
        for (var i = 0; i < eventCount; i++)
        {
            StartOneShot(in _events[i], nowSeconds);
        }

        FeedStreams();
        // After the streams are fed: a recipe's buffers take a few milliseconds, and a stream's queue holds about 43.
        TakeRendered();
        TakeDeviceError();
        CountPlaying();
    }

    /// <summary>
    /// Puts the next recipe the renderer finished into buffers; a failed one is counted and stays silent. One a round.
    /// </summary>
    private void TakeRendered()
    {
        if (!_renderer.TryTake(out var rendered))
        {
            return;
        }

        if (rendered.Failed)
        {
            _failed[rendered.Id] = true;
            _stats.CountRendered(0);
            return;
        }

        var bank = MakeBank((OneShotRecipe)_recipes[rendered.Id], rendered.Lengths, rendered.Samples);
        _banks[rendered.Id] = bank;
        _stats.CountRendered(bank.Bytes);
    }

    /// <summary>
    /// Asks the device to keep an output, moving to the default one when its own went, and logs the
    /// change: lost, and back.
    /// </summary>
    private void KeepConnected()
    {
        var connected = _device.KeepConnected();
        if (connected == _stats.Connected)
        {
            return;
        }

        _stats.SetConnected(connected);
        if (connected)
        {
            LogOutputBack(_device.Name);
        }
        else
        {
            LogOutputLost(_device.Name);
        }
    }

    /// <summary>Counts an error the device's calls raised this round; the first is logged.</summary>
    private void TakeDeviceError()
    {
        var error = _device.TakeError();
        if (error == 0)
        {
            return;
        }

        _stats.CountDeviceError();
        if (!_errorLogged)
        {
            _errorLogged = true;
            LogDeviceError(error);
        }
    }

    private void CreateVoices()
    {
        _created = true;
        for (var i = 0; i < _voices.Length; i++)
        {
            _voices[i].Source = _device.CreateSource();
        }

        for (var i = 0; i < _streamBuffers.Length; i++)
        {
            _streamBuffers[i] = _device.CreateBuffer();
        }
    }

    /// <summary>Prepares each stream recipe registered since the last round; one whose generator throws is broken.</summary>
    private void PrepareStreams()
    {
        var count = _recipes.Count;
        while (_streamsPrepared < count)
        {
            var id = _streamsPrepared++;
            if (_recipes[id] is StreamRecipe stream)
            {
                PrepareStream(id, stream);
            }
        }
    }

    /// <summary>
    /// Makes a stream recipe's first generator and runs it for a block, so the first time the game sustains it neither
    /// allocates it nor runs its code for the first time. It waits, idle, for that stream.
    /// </summary>
    private void PrepareStream(int id, StreamRecipe recipe)
    {
        try
        {
            var generator = recipe.Create(_sampleRate);
            generator.Reset(_random.NextUInt());
            generator.Render(_block, 1);
            (_idleGenerators[id] ??= []).Add(generator);
        }
        catch (Exception exception)
        {
            BreakStream(id, exception);
        }
    }

    /// <summary>A stream recipe that threw: never started again, the other sounds play on.</summary>
    private void BreakStream(int id, Exception exception)
    {
        _brokenStreams[id] = true;
        LogStreamFailed(exception, _recipes.NameOf(id));
    }

    /// <summary>A recipe's variations, <paramref name="lengths"/> long in <paramref name="pcm"/>, put into buffers.</summary>
    private Bank MakeBank(OneShotRecipe recipe, int[] lengths, short[] pcm)
    {
        var total = recipe.Levels * recipe.VariationsPerLevel;
        var bank = new Bank(new uint[total], new float[total], new int[recipe.Levels]);
        Array.Fill(bank.Last, -1);
        var offset = 0;
        for (var index = 0; index < total; index++)
        {
            var length = lengths[index];
            var buffer = _device.CreateBuffer();
            _device.FillBuffer(buffer, pcm.AsSpan(offset, length), _sampleRate);
            bank.Buffers[index] = buffer;
            bank.Seconds[index] = length / (float)_sampleRate;
            bank.Bytes += length * sizeof(short);
            offset += length;
        }

        return bank;
    }

    private void EndFinishedOneShots()
    {
        for (var i = 0; i < _voices.Length; i++)
        {
            ref var voice = ref _voices[i];
            if (voice.Kind == VoiceKind.OneShot && !_device.IsPlaying(voice.Source))
            {
                _device.StopSource(voice.Source);
                voice.Kind = VoiceKind.Free;
            }
        }
    }

    private void StartOneShot(in SoundEvent sound, double now)
    {
        if ((uint)sound.Recipe >= (uint)_recipes.Count || _recipes[sound.Recipe] is not OneShotRecipe)
        {
            return;
        }

        if (_banks[sound.Recipe] is not { } bank)
        {
            // Its recipe is still rendering: too late to play once it is.
            if (!_failed[sound.Recipe])
            {
                _stats.CountLate(1);
            }

            return;
        }

        var recipe = (OneShotRecipe)_recipes[sound.Recipe];
        var gain = recipe.Gain * (sound.Gain > 0 ? sound.Gain : 1);
        var reference = recipe.ReferenceMetres * (sound.Reach > 0 ? Math.Min(sound.Reach, 100) : 1);
        var loudness = sound.AtListener ? MathF.Min(MaxGain, gain) : Loudness(gain, reference, sound.Position);
        if (loudness < CullGain)
        {
            _stats.CountCulled();
            return;
        }

        var index = FindVoice(loudness, now);
        if (index < 0)
        {
            _stats.CountRefused();
            return;
        }

        var level = (int)MathF.Round(Math.Clamp(sound.Level, 0, 1) * (recipe.Levels - 1));
        var variation = PickVariation(bank, level, recipe.VariationsPerLevel);
        var slot = level * recipe.VariationsPerLevel + variation;
        var pitch = sound.Pitch > 0 ? Math.Clamp(sound.Pitch, 0.25f, 4f) : 1;
        ref var voice = ref _voices[index];
        voice.Kind = VoiceKind.OneShot;
        voice.Recipe = sound.Recipe;
        voice.Loudness = loudness;
        voice.Start = now;
        voice.Seconds = bank.Seconds[slot] / pitch;
        voice.Position = sound.Position;
        voice.Gain = gain;
        voice.Pitch = pitch;
        voice.Reference = reference;
        voice.AtListener = sound.AtListener;
        _device.SetSource(voice.Source, sound.Position, gain, pitch, reference, sound.AtListener);
        _device.PlayBuffer(voice.Source, bank.Buffers[slot]);
        _stats.CountPlayed();
    }

    /// <summary>
    /// The match's origin moved by <paramref name="shift"/>: each sound playing in the world (a one-shot not at the
    /// listener, or a stream, sustained or fading after its last round) moves the other way, so it is heard from where it
    /// was. A stream still sustained takes its new place again this round.
    /// </summary>
    private void MoveVoices(Vector3 shift)
    {
        for (var i = 0; i < _voices.Length; i++)
        {
            ref var voice = ref _voices[i];
            if (voice.Kind != VoiceKind.Free && !voice.AtListener)
            {
                voice.Position -= shift;
                _device.SetSource(voice.Source, voice.Position, voice.Gain, voice.Pitch, voice.Reference, false);
            }
        }
    }

    private int PickVariation(Bank bank, int level, int variations)
    {
        if (variations == 1)
        {
            return 0;
        }

        // Any but the one played last at this level.
        var last = bank.Last[level];
        var pick = (int)(_random.Next() * (last < 0 ? variations : variations - 1));
        if (last >= 0 && pick >= last)
        {
            pick++;
        }

        pick = Math.Min(pick, variations - 1);
        bank.Last[level] = pick;
        return pick;
    }

    /// <summary>
    /// A free voice, or else the one-shot now quietest if quieter than <paramref name="loudness"/>, stopped; -1 when
    /// every voice plays something louder.
    /// </summary>
    private int FindVoice(float loudness, double now)
    {
        var quietest = -1;
        var quietestLoudness = loudness;
        for (var i = 0; i < _voices.Length; i++)
        {
            ref var voice = ref _voices[i];
            if (voice.Kind == VoiceKind.Free)
            {
                return i;
            }

            if (voice.Kind != VoiceKind.OneShot)
            {
                continue;
            }

            // A one-shot's loudness falls roughly as it plays out.
            var left = voice.Seconds > 0 ? 1 - (float)((now - voice.Start) / voice.Seconds) : 0;
            var current = voice.Loudness * Math.Clamp(left, 0, 1);
            if (current < quietestLoudness)
            {
                quietest = i;
                quietestLoudness = current;
            }
        }

        if (quietest >= 0)
        {
            _device.StopSource(_voices[quietest].Source);
            _voices[quietest].Kind = VoiceKind.Free;
            _stats.CountStolen();
        }

        return quietest;
    }

    /// <summary>Matches the sustained loops to the playing streams: updates them, starts new ones, releases the rest.</summary>
    private void SustainLoops(int loopCount, double now)
    {
        for (var i = 0; i < _voices.Length; i++)
        {
            _voices[i].Seen = false;
        }

        for (var l = 0; l < loopCount; l++)
        {
            ref readonly var loop = ref _loops[l];
            if (
                (uint)loop.Recipe >= (uint)_recipes.Count
                || _recipes[loop.Recipe] is not StreamRecipe recipe
                || _brokenStreams[loop.Recipe]
            )
            {
                continue;
            }

            var gain = recipe.Gain * (loop.Gain > 0 ? loop.Gain : 1);
            var index = FindStream(loop.Recipe, loop.Key);
            if (index < 0)
            {
                var loudness = Loudness(gain, recipe.ReferenceMetres, loop.Position);
                if (loudness < CullGain)
                {
                    continue;
                }

                index = FindVoice(loudness, now);
                if (index < 0)
                {
                    continue;
                }

                StartStream(index, recipe, in loop);
            }

            ref var voice = ref _voices[index];
            voice.Seen = true;
            voice.Releasing = false;
            voice.Level = Math.Clamp(loop.Level, 0, 1);
            voice.Loudness = Loudness(gain, recipe.ReferenceMetres, loop.Position);
            voice.Position = loop.Position;
            voice.Gain = gain;
            voice.Pitch = 1;
            voice.Reference = recipe.ReferenceMetres;
            voice.AtListener = false;
            _device.SetSource(voice.Source, loop.Position, gain, 1, recipe.ReferenceMetres, atListener: false);
        }

        for (var i = 0; i < _voices.Length; i++)
        {
            ref var voice = ref _voices[i];
            if (voice.Kind == VoiceKind.Stream && !voice.Seen)
            {
                voice.Releasing = true;
            }
        }
    }

    private int FindStream(int recipe, int key)
    {
        for (var i = 0; i < _voices.Length; i++)
        {
            ref readonly var voice = ref _voices[i];
            if (voice.Kind == VoiceKind.Stream && voice.Recipe == recipe && voice.Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private void StartStream(int index, StreamRecipe recipe, in SoundLoop loop)
    {
        var idle = _idleGenerators[loop.Recipe];
        SoundGenerator generator;
        if (idle is { Count: > 0 })
        {
            generator = idle[^1];
            idle.RemoveAt(idle.Count - 1);
        }
        else
        {
            generator = recipe.Create(_sampleRate);
        }

        generator.Reset(_random.NextUInt());
        ref var voice = ref _voices[index];
        voice.Kind = VoiceKind.Stream;
        voice.Recipe = loop.Recipe;
        voice.Key = loop.Key;
        voice.Generator = generator;
        voice.Queued = 0;
        voice.Fade = 1;
        voice.Silent = false;
        _stats.CountPlayed();
    }

    /// <summary>Refills each stream's queue with new blocks, restarts one that ran dry, and ends one that faded out.</summary>
    private void FeedStreams()
    {
        var blockSeconds = BlockSamples / (float)_sampleRate;
        for (var i = 0; i < _voices.Length; i++)
        {
            ref var voice = ref _voices[i];
            if (voice.Kind != VoiceKind.Stream)
            {
                continue;
            }

            for (var processed = _device.ProcessedBuffers(voice.Source); processed > 0; processed--)
            {
                _device.UnqueueBuffer(voice.Source);
                voice.Queued--;
            }

            if (voice.Silent)
            {
                if (voice.Queued <= 0 || !_device.IsPlaying(voice.Source))
                {
                    EndStream(ref voice);
                }

                continue;
            }

            while (voice.Queued < QueueDepth)
            {
                var from = voice.Fade;
                var to = voice.Releasing ? Math.Max(0, from - blockSeconds / ReleaseSeconds) : 1;
                var block = _block.AsSpan();
                if (!TryRender(ref voice, block))
                {
                    break;
                }

                for (var s = 0; s < block.Length; s++)
                {
                    block[s] *= from + (to - from) * (s + 1) / block.Length;
                }

                voice.Fade = to;
                Dsp.ToPcm16(block, _pcm);
                // The voice's buffers take turns: the one queued QueueDepth blocks ago has played.
                var buffer = _streamBuffers[i * QueueDepth + voice.Next];
                voice.Next = (voice.Next + 1) % QueueDepth;
                _device.FillBuffer(buffer, _pcm, _sampleRate);
                _device.QueueBuffer(voice.Source, buffer);
                voice.Queued++;
                if (to <= 0)
                {
                    voice.Silent = true;
                    break;
                }
            }

            if (!_device.IsPlaying(voice.Source) && voice.Queued > 0)
            {
                if (voice.Started)
                {
                    _stats.CountUnderrun();
                }

                voice.Started = true;
                _device.Play(voice.Source);
            }
        }
    }

    /// <summary>
    /// The voice's next block from its generator; false when the generator threw, which breaks its recipe and ends the
    /// voice without keeping the generator.
    /// </summary>
    private bool TryRender(ref Voice voice, Span<float> block)
    {
        try
        {
            voice.Generator!.Render(block, voice.Level);
            return true;
        }
        catch (Exception exception)
        {
            BreakStream(voice.Recipe, exception);
            _device.StopSource(voice.Source);
            voice.Generator = null;
            voice.Kind = VoiceKind.Free;
            voice.Queued = 0;
            voice.Next = 0;
            voice.Started = false;
            voice.Silent = false;
            return false;
        }
    }

    private void EndStream(ref Voice voice)
    {
        _device.StopSource(voice.Source);
        (_idleGenerators[voice.Recipe] ??= []).Add(voice.Generator!);
        voice.Generator = null;
        voice.Kind = VoiceKind.Free;
        voice.Queued = 0;
        voice.Next = 0;
        voice.Started = false;
        voice.Silent = false;
    }

    private void StopAll()
    {
        for (var i = 0; i < _voices.Length; i++)
        {
            ref var voice = ref _voices[i];
            if (voice.Kind == VoiceKind.Stream)
            {
                EndStream(ref voice);
            }
            else if (voice.Kind == VoiceKind.OneShot)
            {
                _device.StopSource(voice.Source);
                voice.Kind = VoiceKind.Free;
            }
        }
    }

    private void CountPlaying()
    {
        var oneShots = 0;
        var streams = 0;
        foreach (ref readonly var voice in _voices.AsSpan())
        {
            if (voice.Kind == VoiceKind.OneShot)
            {
                oneShots++;
            }
            else if (voice.Kind == VoiceKind.Stream)
            {
                streams++;
            }
        }

        _stats.SetPlaying(oneShots, streams);
    }

    /// <summary>
    /// The gain at the listener of a sound of <paramref name="gain"/> at <paramref name="position"/>, at most
    /// <see cref="MaxGain"/> as OpenAL plays it.
    /// </summary>
    private float Loudness(float gain, float referenceMetres, Vector3 position)
    {
        var distance = Vector3.Distance(position, _listener.Position);
        return MathF.Min(MaxGain, gain * referenceMetres / MathF.Max(referenceMetres, distance));
    }

    /// <summary>Stops every sound and frees the sources and buffers. Audio thread, or any thread once it has ended.</summary>
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

        StopAll();
        foreach (ref readonly var voice in _voices.AsSpan())
        {
            _device.DeleteSource(voice.Source);
        }

        foreach (var buffer in _streamBuffers)
        {
            _device.DeleteBuffer(buffer);
        }

        foreach (var bank in _banks)
        {
            if (bank is not null)
            {
                foreach (var buffer in bank.Buffers)
                {
                    _device.DeleteBuffer(buffer);
                }
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Audio: the output of {Device} went; trying the default output each second, silent meanwhile"
    )]
    private partial void LogOutputLost(string device);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio: sound plays again on {Device}")]
    private partial void LogOutputBack(string device);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audio: sound stream {Name} failed; it is not played again")]
    private partial void LogStreamFailed(Exception exception, string name);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Audio: the audio device reported error {Error} (the first; the stats count the rest)"
    )]
    private partial void LogDeviceError(int error);

    private enum VoiceKind : byte
    {
        Free,
        OneShot,
        Stream,
    }

    private struct Voice
    {
        public uint Source;
        public VoiceKind Kind;
        public int Recipe;
        public int Key;
        public float Loudness;
        public double Start;
        public float Seconds;
        public float Level;
        public float Fade;
        public bool Seen;
        public bool Releasing;
        public bool Silent;
        public bool Started;

        // A sound's place and what its source was set with, to set it again when the origin moves.
        public Vector3 Position;
        public float Gain;
        public float Pitch;
        public float Reference;
        public bool AtListener;
        public int Queued;
        public int Next;
        public SoundGenerator? Generator;
    }

    /// <summary>A one-shot recipe's rendered variations, level by level, and the one each level played last.</summary>
    private sealed class Bank(uint[] buffers, float[] seconds, int[] last)
    {
        public uint[] Buffers { get; } = buffers;

        public float[] Seconds { get; } = seconds;

        public int[] Last { get; } = last;

        public long Bytes { get; set; }
    }
}
