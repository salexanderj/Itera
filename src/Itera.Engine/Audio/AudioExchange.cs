using System.Numerics;

namespace Itera.Engine.Audio;

/// <summary>Where the listener is: the camera's position and the way it looks.</summary>
public readonly record struct ListenerPose(Vector3 Position, Vector3 Forward, Vector3 Up)
{
    /// <summary>At the origin, looking along -Z.</summary>
    public static ListenerPose Default => new(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
}

/// <summary>
/// How the main thread hands sound to the audio thread: one lock around fixed arrays, taken briefly by each side. The
/// main thread <see cref="Post"/>s once a main-loop iteration: its new events (added to those still waiting; past
/// <see cref="MaxEvents"/> they are dropped) and its sustained loops, listener, volume, and on or off (which replace the
/// last). The audio thread <see cref="Take"/>s them. When the match's origin moves (<see cref="Shift"/>), what waits moves
/// with it, and the move waits for the audio thread too, which moves the sounds playing. Allocates nothing.
/// </summary>
public sealed class AudioExchange
{
    /// <summary>The most events that can wait for the audio thread.</summary>
    public const int MaxEvents = 512;

    /// <summary>The most loops one iteration can sustain.</summary>
    public const int MaxLoops = 128;

    private readonly Lock _gate = new();
    private readonly SoundEvent[] _events = new SoundEvent[MaxEvents];
    private readonly SoundLoop[] _loops = new SoundLoop[MaxLoops];
    private int _eventCount;
    private int _loopCount;
    private ListenerPose _listener = ListenerPose.Default;
    private float _volume = 1;
    private bool _enabled = true;
    private Vector3 _shift;
    private long _dropped;

    /// <summary>Events dropped because the audio thread had not taken the earlier ones.</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Adds <paramref name="events"/> to those waiting, replaces the sustained loops with <paramref name="loops"/>, and
    /// sets the listener, volume, and on or off. Main thread.
    /// </summary>
    public void Post(
        ReadOnlySpan<SoundEvent> events,
        ReadOnlySpan<SoundLoop> loops,
        in ListenerPose listener,
        float volume,
        bool enabled
    )
    {
        lock (_gate)
        {
            var room = MaxEvents - _eventCount;
            var taken = Math.Min(room, events.Length);
            events[..taken].CopyTo(_events.AsSpan(_eventCount));
            _eventCount += taken;
            if (taken < events.Length)
            {
                Interlocked.Add(ref _dropped, events.Length - taken);
            }

            var loopCount = Math.Min(loops.Length, MaxLoops);
            loops[..loopCount].CopyTo(_loops);
            _loopCount = loopCount;
            _listener = listener;
            _volume = volume;
            _enabled = enabled;
        }
    }

    /// <summary>
    /// The match's origin moved by <paramref name="metres"/>: the waiting events, the loops, and the listener (local to
    /// the old origin) move the other way, and the move is kept for the audio thread's next <see cref="Take"/>, which
    /// moves the one-shots already playing. Main thread.
    /// </summary>
    public void Shift(Vector3 metres)
    {
        lock (_gate)
        {
            for (var i = 0; i < _eventCount; i++)
            {
                _events[i].Position -= metres;
            }

            for (var i = 0; i < _loopCount; i++)
            {
                _loops[i].Position -= metres;
            }

            _listener = _listener with { Position = _listener.Position - metres };
            _shift += metres;
        }
    }

    /// <summary>
    /// Moves the waiting events into <paramref name="events"/> and copies the sustained loops into
    /// <paramref name="loops"/> (both at least <see cref="MaxEvents"/> and <see cref="MaxLoops"/> long), with how far the
    /// origin moved since the last take (<paramref name="shift"/>, zero while it stays). Audio thread.
    /// </summary>
    public void Take(
        Span<SoundEvent> events,
        out int eventCount,
        Span<SoundLoop> loops,
        out int loopCount,
        out ListenerPose listener,
        out float volume,
        out bool enabled,
        out Vector3 shift
    )
    {
        lock (_gate)
        {
            shift = _shift;
            _shift = Vector3.Zero;
            _events.AsSpan(0, _eventCount).CopyTo(events);
            eventCount = _eventCount;
            _eventCount = 0;
            _loops.AsSpan(0, _loopCount).CopyTo(loops);
            loopCount = _loopCount;
            listener = _listener;
            volume = _volume;
            enabled = _enabled;
        }
    }
}
