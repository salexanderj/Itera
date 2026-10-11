using System.Numerics;
using Itera.Engine.Audio;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// <see cref="AudioExchange"/> (its limits had no test): events add up until the audio
/// thread takes them, past <see cref="AudioExchange.MaxEvents"/> they are dropped and counted; loops, the listener, the
/// volume, and on or off replace the last; a take empties the events and keeps the loops; past
/// <see cref="AudioExchange.MaxLoops"/> loops the rest are left out.
/// </summary>
public sealed class AudioExchangeTests
{
    private readonly AudioExchange _exchange = new();
    private readonly SoundEvent[] _events = new SoundEvent[AudioExchange.MaxEvents];
    private readonly SoundLoop[] _loops = new SoundLoop[AudioExchange.MaxLoops];

    [Fact]
    public void Events_add_up_until_taken_and_past_the_most_are_dropped_and_counted()
    {
        var some = new SoundEvent[400];
        _exchange.Post(some, [], ListenerPose.Default, 1, true);
        _exchange.Post(some, [], ListenerPose.Default, 1, true);

        var (events, _) = Take();
        Assert.Equal(AudioExchange.MaxEvents, events);
        Assert.Equal(800 - AudioExchange.MaxEvents, _exchange.DroppedEvents);
        Assert.Equal(0, Take().Events);
    }

    [Fact]
    public void Loops_the_listener_and_the_volume_replace_the_last_and_stay_after_a_take()
    {
        var listener = new ListenerPose(new Vector3(1, 2, 3), Vector3.UnitX, Vector3.UnitY);
        _exchange.Post([], [new SoundLoop { Key = 1 }, new SoundLoop { Key = 2 }], ListenerPose.Default, 1, true);
        _exchange.Post([], [new SoundLoop { Key = 3 }], in listener, 0.5f, false);

        _exchange.Take(_events, out _, _loops, out var loops, out var taken, out var volume, out var enabled, out _);
        Assert.Equal(1, loops);
        Assert.Equal(3, _loops[0].Key);
        Assert.Equal(listener, taken);
        Assert.Equal(0.5f, volume);
        Assert.False(enabled);
        Assert.Equal(1, Take().Loops);
    }

    [Fact]
    public void Past_the_most_loops_the_rest_are_left_out()
    {
        _exchange.Post([], new SoundLoop[AudioExchange.MaxLoops + 5], ListenerPose.Default, 1, true);

        Assert.Equal(AudioExchange.MaxLoops, Take().Loops);
    }

    private (int Events, int Loops) Take()
    {
        _exchange.Take(_events, out var events, _loops, out var loops, out _, out _, out _, out _);
        return (events, loops);
    }
}
