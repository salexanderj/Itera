using System.Numerics;
using Itera.Engine.Audio;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// An <see cref="IAudioDevice"/> in memory: buffers keep their lengths, channels, rates, and loop points, sources play them
/// as <see cref="Advance"/> moves time on (a static buffer until its end, or round its loop while looping; a queue buffer
/// by buffer). With <see cref="RecordSamples"/> each source keeps the samples queued on it, in order. Allocates nothing once
/// its sources exist (unless recording).
/// </summary>
internal sealed class FakeAudioDevice : IAudioDevice
{
    private readonly List<float> _bufferSeconds = [0];
    private readonly List<(int Channels, int Rate)> _bufferFormats = [(0, 0)];
    private readonly List<(int Start, int End)> _bufferLoops = [(-1, -1)];
    private readonly List<Source> _sources = [null!];
    private readonly List<short[]?> _bufferSamples = [null];

    /// <summary>Keeps every buffer's samples, and each source the samples queued on it (<see cref="Source.Queued"/>).</summary>
    public bool RecordSamples { get; init; }

    public string Name => "fake";

    public bool IsAvailable => true;

    public Vector3 ListenerPosition { get; private set; }

    public float ListenerGain { get; private set; }

    /// <summary>Samples filled into buffers, summed, and the largest magnitude of any.</summary>
    public long FilledSamples { get; private set; }

    public int PeakSample { get; private set; }

    /// <summary>A running hash of every sample filled into buffers, in order: equal when the same sounds were filled.</summary>
    public ulong FilledHash { get; private set; } = 14695981039346656037;

    public int Sources => _sources.Count - 1;

    public int Buffers => _bufferSeconds.Count - 1;

    public int DeletedSources { get; private set; }

    public int DeletedBuffers { get; private set; }

    /// <summary>While set, <see cref="CreateBuffer"/> waits until it is released: an audio thread stuck in the device.</summary>
    public ManualResetEventSlim? Hold { get; init; }

    /// <summary>Set once a call waits on <see cref="Hold"/>.</summary>
    public ManualResetEventSlim Holding { get; } = new(false);

    /// <summary>True once <see cref="LeaveOpen"/> was called.</summary>
    public bool LeftOpen { get; private set; }

    public void LeaveOpen() => LeftOpen = true;

    /// <summary>Whether the output is there, as <see cref="KeepConnected"/> reports it.</summary>
    public bool Output { get; set; } = true;

    /// <summary>How many times <see cref="KeepConnected"/> was asked.</summary>
    public int ConnectionChecks { get; private set; }

    public bool KeepConnected()
    {
        ConnectionChecks++;
        return Output;
    }

    /// <summary>The error the next <see cref="TakeError"/> reports, once.</summary>
    public int PendingError { get; set; }

    public int TakeError()
    {
        var error = PendingError;
        PendingError = 0;
        return error;
    }

    public uint CreateBuffer()
    {
        if (Hold is not null)
        {
            Holding.Set();
            Hold.Wait();
        }

        _bufferSeconds.Add(0);
        _bufferFormats.Add((0, 0));
        _bufferLoops.Add((-1, -1));
        _bufferSamples.Add(null);
        return (uint)(_bufferSeconds.Count - 1);
    }

    public void FillBuffer(uint buffer, ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        _bufferSeconds[(int)buffer] = samples.Length / channels / (float)sampleRate;
        _bufferFormats[(int)buffer] = (channels, sampleRate);
        FilledSamples += samples.Length;
        var hash = FilledHash;
        foreach (var s in samples)
        {
            PeakSample = Math.Max(PeakSample, Math.Abs((int)s));
            hash = (hash ^ (ushort)s) * 1099511628211;
        }

        FilledHash = hash;
        if (RecordSamples)
        {
            _bufferSamples[(int)buffer] = samples.ToArray();
        }
    }

    public void DeleteBuffer(uint buffer) => DeletedBuffers++;

    public void SetLoopPoints(uint buffer, int startFrame, int endFrame) =>
        _bufferLoops[(int)buffer] = (startFrame, endFrame);

    /// <summary>Buffer <paramref name="buffer"/>'s loop points as set; (-1, -1) for none.</summary>
    public (int Start, int End) LoopOf(uint buffer) => _bufferLoops[(int)buffer];

    /// <summary>When set, <see cref="CreateSource"/> throws: a device failing on the audio thread.</summary>
    public bool FailSources { get; init; }

    public uint CreateSource()
    {
        if (FailSources)
        {
            throw new InvalidOperationException("OpenAL: alGenSources failed (fake).");
        }

        _sources.Add(new Source());
        return (uint)(_sources.Count - 1);
    }

    public void DeleteSource(uint source) => DeletedSources++;

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up, float gain)
    {
        ListenerPosition = position;
        ListenerGain = gain;
    }

    public void SetSource(
        uint source,
        Vector3 position,
        float gain,
        float pitch,
        float referenceMetres,
        bool atListener
    )
    {
        var s = _sources[(int)source];
        s.Position = position;
        s.AtListener = atListener;
        s.Gain = gain;
        s.Pitch = pitch;
    }

    public void PlayBuffer(uint source, uint buffer)
    {
        var s = _sources[(int)source];
        s.Queue.Clear();
        s.Processed = 0;
        s.Static = buffer;
        s.Elapsed = 0;
        s.Looping = false;
        s.Playing = true;
        s.Starts++;
    }

    public void PlayLooping(uint source, uint buffer, int startFrame)
    {
        PlayBuffer(source, buffer);
        var s = _sources[(int)source];
        s.Looping = true;
        s.StartFrame = startFrame;
        s.Laps = 0;
        s.Elapsed = startFrame / (float)_bufferFormats[(int)buffer].Rate;
    }

    public void SetGain(uint source, float gain) => _sources[(int)source].Gain = gain;

    public void SetDirectChannels(uint source, bool direct) => _sources[(int)source].Direct = direct;

    public void StopSource(uint source)
    {
        var s = _sources[(int)source];
        s.Playing = false;
        s.Looping = false;
        s.Direct = false;
        s.Static = 0;
        s.Queue.Clear();
        s.Processed = 0;
    }

    public bool IsPlaying(uint source) => _sources[(int)source].Playing;

    public int ProcessedBuffers(uint source) => _sources[(int)source].Processed;

    public uint UnqueueBuffer(uint source)
    {
        var s = _sources[(int)source];
        var buffer = s.Queue[0];
        s.Queue.RemoveAt(0);
        s.Processed--;
        return buffer;
    }

    public void QueueBuffer(uint source, uint buffer)
    {
        var s = _sources[(int)source];
        s.Queue.Add(buffer);
        if (RecordSamples)
        {
            s.Queued.AddRange(_bufferSamples[(int)buffer]!);
        }
    }

    public void Play(uint source)
    {
        var s = _sources[(int)source];
        s.Playing = true;
        s.Starts++;
    }

    /// <summary>Plays every source on by <paramref name="seconds"/>.</summary>
    public void Advance(float seconds)
    {
        for (var i = 1; i < _sources.Count; i++)
        {
            var s = _sources[i];
            if (!s.Playing)
            {
                continue;
            }

            s.Elapsed += seconds * (s.Pitch > 0 ? s.Pitch : 1);
            if (s.Static != 0)
            {
                var end = _bufferSeconds[(int)s.Static];
                if (s.Looping)
                {
                    // Round the loop (its points', or the whole buffer) for as long as it plays.
                    var rate = (float)_bufferFormats[(int)s.Static].Rate;
                    var (startFrame, endFrame) = _bufferLoops[(int)s.Static];
                    var loopStart = startFrame >= 0 ? startFrame / rate : 0;
                    var loopEnd = endFrame > startFrame ? endFrame / rate : end;
                    while (s.Elapsed >= loopEnd && loopEnd > loopStart)
                    {
                        s.Elapsed -= loopEnd - loopStart;
                        s.Laps++;
                    }

                    continue;
                }

                s.Playing = s.Elapsed < end;
                continue;
            }

            // A queue plays its unprocessed buffers in order.
            while (s.Processed < s.Queue.Count && s.Elapsed >= _bufferSeconds[(int)s.Queue[s.Processed]])
            {
                s.Elapsed -= _bufferSeconds[(int)s.Queue[s.Processed]];
                s.Processed++;
            }

            if (s.Processed == s.Queue.Count)
            {
                s.Playing = false;
                s.Elapsed = 0;
            }
        }
    }

    /// <summary>Buffer <paramref name="buffer"/>'s channels, rate, and length as last filled.</summary>
    public (int Channels, int Rate, float Seconds) BufferOf(uint buffer) =>
        (_bufferFormats[(int)buffer].Channels, _bufferFormats[(int)buffer].Rate, _bufferSeconds[(int)buffer]);

    /// <summary>The sources playing now.</summary>
    public int Playing()
    {
        var count = 0;
        for (var i = 1; i < _sources.Count; i++)
        {
            count += _sources[i].Playing ? 1 : 0;
        }

        return count;
    }

    /// <summary>Source <paramref name="source"/>'s state.</summary>
    public Source Get(uint source) => _sources[(int)source];

    /// <summary>The playing source whose position is <paramref name="position"/>, or null.</summary>
    public Source? PlayingAt(Vector3 position)
    {
        for (var i = 1; i < _sources.Count; i++)
        {
            if (_sources[i].Playing && _sources[i].Position == position)
            {
                return _sources[i];
            }
        }

        return null;
    }

    public void Dispose() { }

    internal sealed class Source
    {
        public List<uint> Queue { get; } = new(8);

        public int Processed { get; set; }

        public uint Static { get; set; }

        public bool Playing { get; set; }

        public float Elapsed { get; set; }

        public Vector3 Position { get; set; }

        public bool AtListener { get; set; }

        public float Gain { get; set; }

        public float Pitch { get; set; }

        public int Starts { get; set; }

        /// <summary>True while it loops its buffer.</summary>
        public bool Looping { get; set; }

        /// <summary>The frame a loop started from.</summary>
        public int StartFrame { get; set; }

        /// <summary>Times round the loop since it started.</summary>
        public int Laps { get; set; }

        /// <summary>Seconds into its buffer.</summary>
        public float At => Elapsed;

        /// <summary>True while its stereo goes straight to the output's left and right (<see cref="SetDirectChannels"/>).</summary>
        public bool Direct { get; set; }

        /// <summary>The samples queued on it, in order, with <see cref="RecordSamples"/>.</summary>
        public List<short> Queued { get; } = [];
    }
}
