using Itera.Engine.Networking;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// <see cref="TickRing{T}"/> and <see cref="JitterPlayback"/>: the ring keeps the last 16 values by the sender's tick
/// whatever order they came in; playback starts three ticks behind, holds when ahead, and skips ahead when far behind.
/// </summary>
public sealed class TickRingTests
{
    [Fact]
    public void The_ring_keeps_the_last_sixteen_ticks()
    {
        var ring = new TickRing<int>();
        for (uint tick = 100; tick < 120; tick++)
        {
            ring.Store(tick, (int)tick * 2);
        }

        Assert.True(ring.Any);
        Assert.Equal(119u, ring.Newest);
        for (uint tick = 104; tick < 120; tick++)
        {
            Assert.True(ring.TryGet(tick, out var value));
            Assert.Equal((int)tick * 2, value);
        }

        Assert.False(ring.TryGet(103, out _));
        Assert.False(ring.TryGet(120, out _));
    }

    [Fact]
    public void Values_in_any_order_are_found_by_tick_and_older_than_the_ring_are_dropped()
    {
        var ring = new TickRing<int>();
        ring.Store(50, 5);
        ring.Store(48, 3);
        ring.Store(49, 4);
        Assert.Equal(50u, ring.Newest);
        Assert.True(ring.TryGet(48, out var value));
        Assert.Equal(3, value);

        ring.Store(30, 1);

        Assert.False(ring.TryGet(30, out _));
        Assert.Equal(50u, ring.Newest);
    }

    [Fact]
    public void The_latest_at_or_before_a_tick_is_found()
    {
        var ring = new TickRing<int>();
        Assert.False(ring.TryGetLatest(10, out _));
        ring.Store(5, 50);
        ring.Store(9, 90);

        Assert.True(ring.TryGetLatest(8, out var value, out var at));
        Assert.Equal((50, 5u), (value, at));
        Assert.True(ring.TryGetLatest(20, out value));
        Assert.Equal(90, value);
        Assert.False(ring.TryGetLatest(4, out _));
    }

    [Fact]
    public void Storing_and_reading_allocate_nothing()
    {
        var ring = new TickRing<long>();
        ring.Store(1, 1);
        var before = SystemAllocation.StartMeasurement();
        for (uint tick = 2; tick < 200; tick++)
        {
            ring.Store(tick, tick);
            ring.TryGet(tick, out _);
            ring.TryGetLatest(tick, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Playback_starts_behind_the_newest_and_follows_it()
    {
        uint play = 0;
        var playing = false;

        JitterPlayback.Advance(ref play, ref playing, 20);
        Assert.True(playing);
        Assert.Equal(20u - JitterPlayback.DelayTicks, play);

        // At the target it moves on a tick a call while the newest moves a tick a call.
        JitterPlayback.Advance(ref play, ref playing, 21);
        Assert.Equal(18u, play);
        JitterPlayback.Advance(ref play, ref playing, 22);
        Assert.Equal(19u, play);
    }

    [Fact]
    public void Playback_holds_when_ahead_and_skips_ahead_when_far_behind()
    {
        uint play = 25;
        var playing = true;

        JitterPlayback.Advance(ref play, ref playing, 26);
        Assert.Equal(25u, play);

        play = 10;
        JitterPlayback.Advance(ref play, ref playing, 10 + JitterPlayback.MaxBehindTicks);
        Assert.Equal(11u, play);
        JitterPlayback.Advance(ref play, ref playing, 40);
        Assert.Equal(37u, play);
    }

    [Fact]
    public void The_clock_moves_on_a_tick_and_resets_when_it_drifts_either_way()
    {
        uint play = 0;
        var playing = false;

        JitterPlayback.AdvanceClock(ref play, ref playing, 2);
        Assert.True(playing);
        Assert.Equal(0u, play);

        play = 30;
        JitterPlayback.AdvanceClock(ref play, ref playing, 33);
        Assert.Equal(31u, play);

        // Far behind.
        JitterPlayback.AdvanceClock(ref play, ref playing, 100);
        Assert.Equal(97u, play);

        // Far ahead.
        play = 200;
        JitterPlayback.AdvanceClock(ref play, ref playing, 100);
        Assert.Equal(97u, play);
    }
}
