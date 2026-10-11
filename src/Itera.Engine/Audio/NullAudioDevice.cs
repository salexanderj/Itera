using System.Numerics;

namespace Itera.Engine.Audio;

/// <summary>
/// Silent fallback used when OpenAL cannot be loaded or no output device exists. <see cref="AudioService"/> starts no
/// thread for it; its calls do nothing.
/// </summary>
public sealed class NullAudioDevice : IAudioDevice
{
    public string Name => "none (silent)";

    public bool IsAvailable => false;

    public uint CreateBuffer() => 0;

    public void FillBuffer(uint buffer, ReadOnlySpan<short> samples, int channels, int sampleRate) { }

    public void SetLoopPoints(uint buffer, int startFrame, int endFrame) { }

    public void PlayLooping(uint source, uint buffer, int startFrame) { }

    public void SetGain(uint source, float gain) { }

    public void SetDirectChannels(uint source, bool direct) { }

    public void DeleteBuffer(uint buffer) { }

    public uint CreateSource() => 0;

    public void DeleteSource(uint source) { }

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up, float gain) { }

    public void SetSource(
        uint source,
        Vector3 position,
        float gain,
        float pitch,
        float referenceMetres,
        bool atListener
    ) { }

    public void PlayBuffer(uint source, uint buffer) { }

    public void StopSource(uint source) { }

    public bool IsPlaying(uint source) => false;

    public int ProcessedBuffers(uint source) => 0;

    public uint UnqueueBuffer(uint source) => 0;

    public void QueueBuffer(uint source, uint buffer) { }

    public void Play(uint source) { }

    public void Dispose() { }
}
