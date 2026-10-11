using System.Numerics;
using Silk.NET.OpenAL;

namespace Itera.Engine.Audio;

/// <summary>
/// OpenAL Soft output device and context. Every source plays up to <see cref="AudioMixer.MaxGain"/>, not OpenAL's
/// default of 1, so a gain above 1 is heard near it too. A stereo buffer plays with
/// <c>AL_SOFT_direct_channels_remix</c> (where the library has it): its left and right go to the output's left and right
/// as they are, not through the spatial mix; a mono buffer is mixed as before (a source's mode is set as a buffer plays
/// and cleared as it stops, so a reused source never keeps it). An error a call raises is kept
/// until <see cref="TakeError"/> reads it, and never fails a later buffer or source.
/// </summary>
public sealed unsafe class OpenAlAudioDevice : IAudioDevice
{
    // ALC_SOFT_loopback: a device that renders into memory, for tests of the real OpenAL path without an output.
    private const int FormatChannelsSoft = 0x1990;
    private const int FormatTypeSoft = 0x1991;
    private const int MonoSoft = 0x1500;
    private const int StereoSoft = 0x1501;
    private const int FloatSoft = 0x1406;
    private const int Frequency = 0x1007;

    // ALC_EXT_disconnect: whether the output is still there.
    private const int Connected = 0x313;

    // AL_SOFT_direct_channels_remix: a source's channels straight to the output's, remixed where they do not match.
    private const int DirectChannelsSoft = 0x1033;
    private const int RemixUnmatchedSoft = 2;

    // AL_SOFT_loop_points: the frames a looping static source repeats.
    private const int LoopPointsSoft = 0x2015;

    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly delegate* unmanaged<Device*, void*, int, void> _renderSamples;

    // ALC_SOFT_reopen_device: moves an open device, its contexts, sources, and buffers to another output.
    private readonly delegate* unmanaged<Device*, byte*, int*, byte> _reopen;
    private readonly bool _directChannels;
    private readonly bool _loopPoints;
    private readonly int _loopbackChannels;
    private Device* _device;
    private Context* _context;
    private int _error;
    private volatile bool _leftOpen;

    private OpenAlAudioDevice(
        ALContext alc,
        AL al,
        Device* device,
        Context* context,
        string name,
        delegate* unmanaged<Device*, void*, int, void> renderSamples = null,
        int loopbackChannels = 1
    )
    {
        _alc = alc;
        _al = al;
        _device = device;
        _context = context;
        _renderSamples = renderSamples;
        _reopen = alc.IsExtensionPresent(device, "ALC_SOFT_reopen_device")
            ? (delegate* unmanaged<Device*, byte*, int*, byte>)alc.GetProcAddress(device, "alcReopenDeviceSOFT")
            : null;
        _directChannels = al.IsExtensionPresent("AL_SOFT_direct_channels_remix");
        _loopPoints = al.IsExtensionPresent("AL_SOFT_loop_points");
        _loopbackChannels = loopbackChannels;
        Name = name;
    }

    public string Name { get; }

    public bool IsAvailable => true;

    public bool KeepConnected()
    {
        var connected = 1;
        _alc.GetContextProperty(_device, (GetContextInteger)Connected, 1, &connected);
        if (connected != 0)
        {
            return true;
        }

        // Null for the default output; no attributes, so the device keeps its own.
        return _reopen is not null && _reopen(_device, null, null) != 0;
    }

    public int TakeError()
    {
        Keep();
        var error = _error;
        _error = 0;
        return error;
    }

    public uint CreateBuffer()
    {
        Keep();
        var buffer = _al.GenBuffer();
        Check("alGenBuffers");
        return buffer;
    }

    public void FillBuffer(uint buffer, ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        var format = channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
        fixed (short* data = samples)
        {
            _al.BufferData(buffer, format, data, samples.Length * sizeof(short), sampleRate);
        }
    }

    public void DeleteBuffer(uint buffer) => _al.DeleteBuffer(buffer);

    public uint CreateSource()
    {
        Keep();
        var source = _al.GenSource();
        Check("alGenSources");
        _al.SetSourceProperty(source, SourceFloat.RolloffFactor, 1f);
        _al.SetSourceProperty(source, SourceFloat.MaxGain, AudioMixer.MaxGain);
        Check("alSourcef(AL_MAX_GAIN)");
        return source;
    }

    public void DeleteSource(uint source)
    {
        _al.SourceStop(source);
        _al.SetSourceProperty(source, SourceInteger.Buffer, 0);
        _al.DeleteSource(source);
    }

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up, float gain)
    {
        _al.SetListenerProperty(ListenerVector3.Position, position.X, position.Y, position.Z);
        float* orientation = stackalloc float[6] { forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z };
        _al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
        _al.SetListenerProperty(ListenerFloat.Gain, gain);
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
        // Set on every call: a source played at the listener goes back into the world for its next sound.
        _al.SetSourceProperty(source, SourceBoolean.SourceRelative, atListener);
        var place = atListener ? Vector3.Zero : position;
        _al.SetSourceProperty(source, SourceVector3.Position, place.X, place.Y, place.Z);
        _al.SetSourceProperty(source, SourceFloat.Gain, gain);
        _al.SetSourceProperty(source, SourceFloat.Pitch, pitch);
        _al.SetSourceProperty(source, SourceFloat.ReferenceDistance, referenceMetres);
    }

    public void SetLoopPoints(uint buffer, int startFrame, int endFrame)
    {
        if (!_loopPoints)
        {
            // Without the extension a looping source repeats the whole buffer.
            return;
        }

        var points = stackalloc int[2] { startFrame, endFrame };
        _al.SetBufferProperty(buffer, (BufferInteger)LoopPointsSoft, points);
    }

    public void PlayBuffer(uint source, uint buffer)
    {
        Attach(source, buffer, looping: false);
        _al.SourcePlay(source);
    }

    public void PlayLooping(uint source, uint buffer, int startFrame)
    {
        Attach(source, buffer, looping: true);
        // Taken as the source starts.
        _al.SetSourceProperty(source, SourceInteger.SampleOffset, startFrame);
        _al.SourcePlay(source);
    }

    public void SetGain(uint source, float gain) => _al.SetSourceProperty(source, SourceFloat.Gain, gain);

    /// <summary>Stops <paramref name="source"/> and gives it <paramref name="buffer"/>, looping or not, its channels direct when stereo.</summary>
    private void Attach(uint source, uint buffer, bool looping)
    {
        _al.SourceStop(source);
        _al.SetSourceProperty(source, SourceBoolean.Looping, looping);
        _al.SetSourceProperty(source, SourceInteger.Buffer, buffer);
        if (_directChannels)
        {
            // A stereo buffer's channels straight to the output's; a mono one through the mix, as ever.
            _al.GetBufferProperty(buffer, GetBufferInteger.Channels, out int channels);
            _al.SetSourceProperty(source, (SourceInteger)DirectChannelsSoft, channels == 2 ? RemixUnmatchedSoft : 0);
        }
    }

    public void StopSource(uint source)
    {
        _al.SourceStop(source);
        // A looping source fed a queue would loop the queue.
        _al.SetSourceProperty(source, SourceBoolean.Looping, false);
        // Detaching takes every queued buffer off, played or not.
        _al.SetSourceProperty(source, SourceInteger.Buffer, 0);
        if (_directChannels)
        {
            _al.SetSourceProperty(source, (SourceInteger)DirectChannelsSoft, 0);
        }
    }

    public bool IsPlaying(uint source)
    {
        _al.GetSourceProperty(source, GetSourceInteger.SourceState, out var state);
        return state == (int)SourceState.Playing;
    }

    public int ProcessedBuffers(uint source)
    {
        _al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out var processed);
        return processed;
    }

    public uint UnqueueBuffer(uint source)
    {
        uint buffer = 0;
        _al.SourceUnqueueBuffers(source, 1, &buffer);
        return buffer;
    }

    public void QueueBuffer(uint source, uint buffer) => _al.SourceQueueBuffers(source, 1, &buffer);

    public void Play(uint source) => _al.SourcePlay(source);

    /// <summary>Keeps an error an earlier call raised for <see cref="TakeError"/>, so the next check sees only its own.</summary>
    private void Keep()
    {
        var error = _al.GetError();
        if (error != AudioError.NoError && _error == 0)
        {
            _error = (int)error;
        }
    }

    private void Check(string call)
    {
        var error = _al.GetError();
        if (error != AudioError.NoError)
        {
            throw new InvalidOperationException($"OpenAL: {call} failed ({error}).");
        }
    }

    /// <summary>Opens the default device. Throws when the native library or a device is missing.</summary>
    public static OpenAlAudioDevice OpenDefault()
    {
        var alc = ALContext.GetApi(soft: true);
        var device = alc.OpenDevice(string.Empty);
        if (device is null)
        {
            throw new InvalidOperationException("OpenAL could not open the default output device.");
        }

        var context = Activate(alc, device, null);
        var al = AL.GetApi(soft: true);
        // Full within a source's reference distance, then falling as the reciprocal of the distance.
        al.DistanceModel(DistanceModel.InverseDistanceClamped);
        var name = alc.GetContextProperty(device, GetContextString.DeviceSpecifier) ?? "OpenAL device";
        return new OpenAlAudioDevice(alc, al, device, context, name);
    }

    /// <summary>
    /// Opens OpenAL Soft's loopback device at <paramref name="sampleRate"/>, float, mono or with
    /// <paramref name="channels"/> 2 stereo (left and right interleaved), which renders only when asked
    /// (<see cref="RenderLoopback"/>): the real OpenAL path in tests, with no output. Null when the library lacks it.
    /// </summary>
    internal static OpenAlAudioDevice? OpenLoopback(int sampleRate, int channels = 1)
    {
        var alc = ALContext.GetApi(soft: true);
        var open = (delegate* unmanaged<byte*, Device*>)alc.GetProcAddress(null, "alcLoopbackOpenDeviceSOFT");
        var render = (delegate* unmanaged<Device*, void*, int, void>)alc.GetProcAddress(null, "alcRenderSamplesSOFT");
        if (open is null || render is null)
        {
            return null;
        }

        var device = open(null);
        if (device is null)
        {
            return null;
        }

        var attributes = stackalloc int[]
        {
            FormatChannelsSoft,
            channels == 2 ? StereoSoft : MonoSoft,
            FormatTypeSoft,
            FloatSoft,
            Frequency,
            sampleRate,
            0,
        };
        var context = Activate(alc, device, attributes);
        var al = AL.GetApi(soft: true);
        al.DistanceModel(DistanceModel.InverseDistanceClamped);
        return new OpenAlAudioDevice(alc, al, device, context, "OpenAL loopback", render, channels);
    }

    /// <summary>Mixes the next <paramref name="output"/>'s samples of a loopback device into it (its frames' channels interleaved).</summary>
    internal void RenderLoopback(Span<float> output)
    {
        if (_renderSamples is null)
        {
            throw new InvalidOperationException("Not a loopback device.");
        }

        fixed (float* samples = output)
        {
            _renderSamples(_device, samples, output.Length / _loopbackChannels);
        }
    }

    /// <summary>
    /// A context on <paramref name="device"/>, made current; the device closed and the context destroyed when either
    /// fails (a context made but not activated would be left open).
    /// </summary>
    private static Context* Activate(ALContext alc, Device* device, int* attributes)
    {
        var context = alc.CreateContext(device, attributes);
        if (context is not null && alc.MakeContextCurrent(context))
        {
            return context;
        }

        if (context is not null)
        {
            alc.DestroyContext(context);
        }

        alc.CloseDevice(device);
        throw new InvalidOperationException("OpenAL could not create or activate a context.");
    }

    public void LeaveOpen()
    {
        _leftOpen = true;
    }

    public void Dispose()
    {
        if (_leftOpen)
        {
            return;
        }

        if (_context is not null)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _context = null;
        }

        if (_device is not null)
        {
            _alc.CloseDevice(_device);
            _device = null;
        }

        // The OpenAL library stays loaded for the life of the process; unloading it at exit is unsafe.
    }
}
