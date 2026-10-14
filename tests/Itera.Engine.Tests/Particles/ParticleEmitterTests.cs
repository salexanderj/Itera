using System.Numerics;
using Itera.Engine.Particles;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Particles;

/// <summary>
/// <see cref="ParticleEmitter"/>: bursts take the ring's slots in one piece and in order, a frame's caps drop the rest,
/// the frame stays active for the longest life, off drops everything, a frame without graphics drops its queue without
/// counting drops, the recipes table checks its values, and emitting and draining allocate nothing.
/// </summary>
public sealed class ParticleEmitterTests
{
    private static ParticleEmitter Create(int capacity = 16_384, int bursts = 16, int spawns = 1024)
    {
        var options = new EngineOptions();
        options.Particles.Capacity = capacity;
        options.Particles.MaxBurstsPerFrame = bursts;
        options.Particles.MaxSpawnsPerFrame = spawns;
        var emitter = new ParticleEmitter(Options.Create(options), NullLogger<ParticleEmitter>.Instance);
        emitter.Recipes.Register("test", new ParticleRecipe { LifetimeMin = 1, LifetimeMax = 2 });
        return emitter;
    }

    private static ParticleBurst Burst(int count) =>
        new()
        {
            Recipe = 0,
            Count = count,
            Origin = new Vector3(1, 2, 3),
            Tint = default,
        };

    [Fact]
    public void Bursts_take_the_next_slots_and_start_again_at_zero_rather_than_wrap()
    {
        var emitter = Create(capacity: 16_384, spawns: 8_192);
        var target = new GpuParticleBurst[16];

        emitter.Emit(Burst(6_000));
        emitter.Emit(Burst(2_000));
        var first = emitter.Drain(target, 0);
        emitter.Emit(Burst(8_000));
        emitter.Drain(target.AsSpan(2), 0.1);
        emitter.Emit(Burst(2_000));
        var third = emitter.Drain(target.AsSpan(3), 0.2);

        Assert.Equal(new ParticleFrame(2, 8_000, true), first);
        Assert.Equal((0u, 0u), (target[0].First, target[0].Prefix));
        Assert.Equal((6_000u, 6_000u), (target[1].First, target[1].Prefix));
        Assert.Equal(8_000u, target[2].First);
        Assert.Equal(1, third.Bursts);
        // 16,000 + 2,000 passes 16,384: the burst starts at slot 0 in one piece.
        Assert.Equal(0u, target[3].First);
        Assert.Equal(uint.MaxValue, target[0].Colour);
        Assert.NotEqual(target[0].Seed, target[1].Seed);
        Assert.Equal(0, emitter.QueuedBursts);
    }

    /// <summary>
    /// A frame spawns at most half the pool, so two of its bursts never take the same slots of the ring
    /// (with the option at the pool's size, a burst that started again at slot 0 could
    /// overwrite one of the same frame).
    /// </summary>
    [Fact]
    public void A_frame_spawns_at_most_half_the_pool()
    {
        var emitter = Create(capacity: 16_384, spawns: 16_384);
        var target = new GpuParticleBurst[16];

        Assert.Equal(8_192, emitter.MaxSpawns);
        emitter.Emit(Burst(6_000));
        emitter.Emit(Burst(6_000));
        var frame = emitter.Drain(target, 0);

        Assert.Equal(1, frame.Bursts);
        Assert.Equal(6_000, frame.Spawns);
    }

    [Fact]
    public void A_frame_takes_at_most_its_bursts_and_particles()
    {
        var emitter = Create(bursts: 16, spawns: 1024);

        for (var i = 0; i < 20; i++)
        {
            emitter.Emit(Burst(10));
        }

        emitter.Emit(Burst(900));
        Assert.Equal(16, emitter.QueuedBursts);
        Assert.Equal(160, emitter.QueuedSpawns);
        Assert.Equal(5, emitter.DroppedBursts);

        var frame = emitter.Drain(new GpuParticleBurst[16], 0);
        Assert.Equal(160, frame.Spawns);
        emitter.Emit(Burst(900));
        emitter.Emit(Burst(200));
        Assert.Equal(900, emitter.QueuedSpawns);
        Assert.Equal(6, emitter.DroppedBursts);
    }

    [Fact]
    public void A_frame_stays_active_for_the_longest_life_after_a_spawn()
    {
        var emitter = Create();
        var target = new GpuParticleBurst[16];

        Assert.False(emitter.Drain(target, 0).Active);
        emitter.Emit(Burst(5));
        Assert.True(emitter.Drain(target, 10).Active);
        Assert.True(emitter.Drain(target, 11.9).Active);
        Assert.False(emitter.Drain(target, 12.2).Active);
    }

    [Fact]
    public void Clear_drops_the_queue_stops_the_stress_test_and_tells_the_next_frame_once()
    {
        var emitter = Create(spawns: 16_384);
        var target = new GpuParticleBurst[16];
        emitter.Emit(Burst(5_000));
        emitter.Drain(target, 0);
        emitter.Emit(Burst(5));
        emitter.StressBursts = 2;

        emitter.Clear();
        emitter.Emit(Burst(7));
        var cleared = emitter.Drain(target, 0.5);
        var next = emitter.Drain(target, 0.6);

        // Only the burst queued after the clear is made, from slot 0; the frame is active for its life alone.
        Assert.Equal(new ParticleFrame(1, 7, true, Clear: true), cleared);
        Assert.Equal(0u, target[0].First);
        Assert.Equal(0, emitter.StressBursts);
        Assert.False(next.Clear);
        Assert.False(emitter.Drain(target, 2.7).Active);
    }

    [Fact]
    public void Discard_drops_the_queue_without_counting_drops_or_clearing_the_pool()
    {
        var emitter = Create(bursts: 16);
        var target = new GpuParticleBurst[16];
        for (var i = 0; i < 40; i++)
        {
            emitter.Emit(Burst(5));
            emitter.Discard();
        }

        var discarded = (emitter.QueuedBursts, emitter.QueuedSpawns, emitter.DroppedBursts);
        emitter.Emit(Burst(7));

        // A frame without graphics drops each frame's bursts, so the queue never fills; one that draws makes the rest.
        Assert.Equal((0, 0, 0L), discarded);
        Assert.Equal(new ParticleFrame(1, 7, true), emitter.Drain(target, 0));
    }

    [Fact]
    public void A_clear_with_nothing_after_it_still_reaches_the_next_frame()
    {
        var emitter = Create();
        var target = new GpuParticleBurst[16];
        emitter.Emit(Burst(5));
        emitter.Drain(target, 0);

        emitter.Clear();

        Assert.Equal(new ParticleFrame(0, 0, false, Clear: true), emitter.Drain(target, 0.1));
    }

    [Fact]
    public void Off_drops_bursts_and_ends_the_active_time()
    {
        var emitter = Create();
        var target = new GpuParticleBurst[16];
        emitter.Emit(Burst(5));
        emitter.Drain(target, 0);

        emitter.Enabled = false;
        emitter.Emit(Burst(5));

        Assert.Equal(0, emitter.QueuedBursts);
        Assert.Equal(default, emitter.Drain(target, 0.5));
    }

    [Fact]
    public void Tint_attractor_and_shape_are_packed()
    {
        var emitter = Create();
        emitter.SetAttractor(5, new Vector3(1, 2, 3));
        var burst = Burst(3);
        burst.Tint = new Vector4(1, 0, 0, 1);
        burst.Attractor = 5;
        burst.Shape = ParticleShape.Disc;
        burst.Seed = 77;
        emitter.Emit(burst);
        var target = new GpuParticleBurst[1];

        emitter.Drain(target, 0);

        Assert.Equal(0xFF0000FFu, target[0].Colour);
        Assert.Equal(0u | 3u << 8 | 5u << 12, target[0].Packed);
        Assert.Equal(77u, target[0].Seed);
        Assert.Equal(new Vector4(1, 2, 3, 1), emitter.Attractors[5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => emitter.SetAttractor(0, Vector3.Zero));
    }

    [Fact]
    public void An_unknown_recipe_and_bad_values_are_refused()
    {
        var emitter = Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => emitter.Emit(new ParticleBurst { Recipe = 1, Count = 1 }));
        Assert.Throws<ArgumentException>(() =>
            emitter.Recipes.Register("bad", new ParticleRecipe { LifetimeMin = 2, LifetimeMax = 1 })
        );
        Assert.Throws<ArgumentException>(() =>
            emitter.Recipes.Register("bad", new ParticleRecipe { SpreadDegrees = 200 })
        );
        Assert.Equal(0, emitter.Recipes.Register("test", new ParticleRecipe { LifetimeMax = 5 }));
        Assert.Equal(5, emitter.Recipes.LongestLifetime);
    }

    [Fact]
    public void Emitting_and_draining_allocate_nothing()
    {
        var emitter = Create(bursts: 256, spawns: 16_384);
        var target = new GpuParticleBurst[256];
        var burst = Burst(10);
        emitter.Emit(in burst);
        emitter.Drain(target, 0);

        var start = SystemAllocation.StartMeasurement();
        for (var frame = 0; frame < 100; frame++)
        {
            for (var i = 0; i < 20; i++)
            {
                emitter.Emit(in burst);
            }

            emitter.Drain(target, frame * 0.016);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
