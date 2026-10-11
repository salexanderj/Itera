using System.Numerics;

namespace Itera.Engine.Audio;

/// <summary>
/// An opened audio output: the OpenAL calls <see cref="AudioService"/>'s thread makes. Buffers hold 16-bit samples, mono
/// or stereo; sources play them at a place in the world (mono) or at the listener (a stereo buffer's channels to the
/// left and right). After <see cref="AudioService"/> starts its thread, only that
/// thread calls these.
/// </summary>
public interface IAudioDevice : IDisposable
{
    /// <summary>Human readable device name, or a note that no device is active.</summary>
    string Name { get; }

    /// <summary>False for the silent fallback device.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Makes <see cref="IDisposable.Dispose"/> leave the device open: the audio thread did not stop in time and may still
    /// use it, so it is left to the OS at exit instead of closed under that thread. A device with nothing to close
    /// ignores it. Any thread.
    /// </summary>
    void LeaveOpen() { }

    /// <summary>
    /// The first error the device's calls raised since the last call of this, as the native code; 0 for none. The mixer
    /// reads it every round, so an error is counted where it happened and never left to fail a later call.
    /// </summary>
    int TakeError() => 0;

    /// <summary>
    /// Checks the output is still there and, when it went (a headset unplugged, a sound server restarted), moves the
    /// sounds to the default output. False while there is none to play on. Audio thread, about once a
    /// second.
    /// </summary>
    bool KeepConnected() => true;

    /// <summary>Makes a buffer.</summary>
    uint CreateBuffer();

    /// <summary>
    /// Fills <paramref name="buffer"/> with 16-bit <paramref name="samples"/> of <paramref name="channels"/> channels (1,
    /// or 2 interleaved, left first) at <paramref name="sampleRate"/>.
    /// </summary>
    void FillBuffer(uint buffer, ReadOnlySpan<short> samples, int channels, int sampleRate);

    /// <summary>Frees a buffer no source holds.</summary>
    void DeleteBuffer(uint buffer);

    /// <summary>Makes a source.</summary>
    uint CreateSource();

    /// <summary>Stops and frees a source.</summary>
    void DeleteSource(uint source);

    /// <summary>Places the listener (the camera): its position, the way it looks, its up, and the master gain.</summary>
    void SetListener(Vector3 position, Vector3 forward, Vector3 up, float gain);

    /// <summary>
    /// Places <paramref name="source"/> at <paramref name="position"/> with <paramref name="gain"/> and
    /// <paramref name="pitch"/>, full within <paramref name="referenceMetres"/> and falling as the reciprocal of the
    /// distance past it; or, when <paramref name="atListener"/> is true, at the listener wherever it moves, at its full
    /// gain (<paramref name="position"/> ignored), a stereo buffer's channels going to the left and right as they are.
    /// </summary>
    void SetSource(uint source, Vector3 position, float gain, float pitch, float referenceMetres, bool atListener);

    /// <summary>Plays <paramref name="buffer"/> once on <paramref name="source"/>, from its start.</summary>
    void PlayBuffer(uint source, uint buffer);

    /// <summary>Stops <paramref name="source"/> and takes every buffer off it.</summary>
    void StopSource(uint source);

    /// <summary>Whether <paramref name="source"/> is playing.</summary>
    bool IsPlaying(uint source);

    /// <summary>How many of <paramref name="source"/>'s queued buffers have played.</summary>
    int ProcessedBuffers(uint source);

    /// <summary>Takes the oldest played buffer off <paramref name="source"/>'s queue and returns it.</summary>
    uint UnqueueBuffer(uint source);

    /// <summary>Adds <paramref name="buffer"/> to the end of <paramref name="source"/>'s queue.</summary>
    void QueueBuffer(uint source, uint buffer);

    /// <summary>Starts <paramref name="source"/> (or starts it again after its queue ran dry).</summary>
    void Play(uint source);
}
