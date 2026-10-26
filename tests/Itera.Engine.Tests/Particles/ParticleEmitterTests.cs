using System.Numerics;
using Itera.Engine.Particles;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Particles;

/// <summary>
/// <see cref="ParticleEmitter"/>: bursts take the ring's slots in one piece and in order, a frame's caps drop the rest,
/// the frame stays active for the longest life among its bursts' recipes, the frame names the ring's slots that can be
/// alive, off drops everything, a frame without graphics drops its queue without counting drops, a detail recipe's
/// bursts thin by the effects' share, the recipes table checks its values, and emitting and draining allocate nothing.
/// </summary>
public sealed class ParticleEmitterTests
{
    private static ParticleEmitter Create(
        int capacity = 16_384,
        int bursts = 16,
        int spawns = 1024,
        EffectsBudget? effects = null
    )
    {
        var options = new EngineOptions();
        options.Particles.Capacity = capacity;
        options.Particles.MaxBurstsPerFrame = bursts;
        options.Particles.MaxSpawnsPerFrame = spawns;
        var emitter = new ParticleEmitter(Options.Create(options), NullLogger<ParticleEmitter>.Instance, effects);
        emitter.Recipes.Register("test", new ParticleRecipe { LifetimeMin = 1, LifetimeMax = 2 });
        return emitter;
    }

    /// <summary>A frame's step in seconds: the cap of a long frame, and exact in binary.</summary>
    private const float Step = GameClock.MaxDeltaSeconds;

    /// <summary>Drains frames of <paramref name="step"/> seconds with no bursts while they are active, and counts them.</summary>
    private static int FramesStillActive(ParticleEmitter emitter, GpuParticleBurst[] target, float step)
    {
        var frames = 0;
        while (frames < 100_000 && emitter.Drain(target, step).Active)
        {
            frames++;
        }

        return frames;
    }

    /// <summary>The later frames of <paramref name="step"/> seconds a batch of <paramref name="life"/> seconds stays in.</summary>
    private static int ExpectedFrames(float life, float step) =>
        (int)Math.Ceiling((life + ParticleEmitter.DeathMargin(life)) / step);

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
        emitter.Drain(target.AsSpan(2), Step);
        emitter.Emit(Burst(2_000));
        var third = emitter.Drain(target.AsSpan(3), Step);

        Assert.Equal(new ParticleFrame(2, 8_000, true, LiveFirst: 0, LiveCount: 8_000), first);
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

    /// <summary>
    /// A burst's frame stays active while its life and margin have not passed on the particles' clock, which adds the
    /// frames' steps: a long frame counts its capped step, not the time it took.
    /// </summary>
    [Fact]
    public void A_frame_stays_active_for_the_life_after_a_spawn_counted_in_steps()
    {
        var emitter = Create();
        var target = new GpuParticleBurst[16];

        var before = emitter.Drain(target, Step).Active;
        emitter.Emit(Burst(5));
        emitter.Drain(target, Step);
        var capped = FramesStillActive(emitter, target, Step);
        emitter.Emit(Burst(5));
        emitter.Drain(target, Step / 2);
        var half = FramesStillActive(emitter, target, Step / 2);

        // 2 s, a quarter second, and a 128th of 2 s: 2.265625 s.
        Assert.False(before);
        Assert.Equal((10, 19), (capped, half));
    }

    /// <summary>
    /// A short recipe's burst keeps the frame active for its own life, though a longer recipe is registered; a burst of
    /// the longer one extends the time.
    /// </summary>
    [Fact]
    public void The_active_time_follows_the_recipes_of_the_bursts_made()
    {
        var emitter = Create();
        var smoke = emitter.Recipes.Register("smoke", new ParticleRecipe { LifetimeMin = 4, LifetimeMax = 8 });
        var target = new GpuParticleBurst[16];

        emitter.Emit(Burst(5));
        emitter.Drain(target, Step);
        var flash = FramesStillActive(emitter, target, Step);
        emitter.Emit(Burst(5));
        emitter.Drain(target, Step);
        emitter.Emit(Burst(5) with { Recipe = smoke });
        emitter.Drain(target, Step);
        var smoked = FramesStillActive(emitter, target, Step);

        Assert.Equal(ExpectedFrames(2, Step), flash);
        Assert.Equal(ExpectedFrames(8, Step), smoked);
    }

    /// <summary>
    /// Frames under the loading screen neither make nor move particles: the particles' clock stops, the frame's bursts
    /// get no slots that can be alive, and the particles made before wait for the world to show again.
    /// </summary>
    [Fact]
    public void Frames_that_do_not_draw_the_world_leave_the_particles_waiting()
    {
        var emitter = Create();
        var target = new GpuParticleBurst[16];

        emitter.Emit(Burst(5));
        var covered = emitter.Drain(target, Step, worldDrawn: false);
        emitter.Emit(Burst(5));
        emitter.Drain(target, Step);
        var waiting = true;
        for (var i = 0; i < 100; i++)
        {
            var frame = emitter.Drain(target, Step, worldDrawn: false);
            waiting &= frame is { Active: true, LiveFirst: 5, LiveCount: 5 };
        }

        var after = FramesStillActive(emitter, target, Step);

        Assert.Equal((1, 5, false, 0), (covered.Bursts, covered.Spawns, covered.Active, covered.LiveCount));
        Assert.True(waiting);
        Assert.Equal(ExpectedFrames(2, Step), after);
    }

    [Fact]
    public void The_live_slots_are_those_made_within_their_lives()
    {
        var emitter = Create();
        var target = new GpuParticleBurst[16];

        // Made with the clock at 0.25, 0.5, and 0.75 s; each lives 2.265625 s with its margin.
        emitter.Emit(Burst(100));
        emitter.Drain(target, Step);
        emitter.Emit(Burst(50));
        emitter.Drain(target, Step);
        emitter.Emit(Burst(30));
        var all = emitter.Drain(target, Step);
        for (var i = 0; i < 7; i++)
        {
            emitter.Drain(target, Step);
        }

        var lastAll = emitter.Drain(target, Step);
        var firstDead = emitter.Drain(target, Step);
        var secondDead = emitter.Drain(target, Step);
        var none = emitter.Drain(target, Step);

        // The clock before these frames: 2.5, 2.75, 3, and 3.25 s.
        Assert.Equal((0, 180), (all.LiveFirst, all.LiveCount));
        Assert.Equal((0, 180), (lastAll.LiveFirst, lastAll.LiveCount));
        Assert.Equal((100, 80), (firstDead.LiveFirst, firstDead.LiveCount));
        Assert.Equal((150, 30), (secondDead.LiveFirst, secondDead.LiveCount));
        Assert.Equal((false, 0, 0), (none.Active, none.LiveFirst, none.LiveCount));
    }

    [Fact]
    public void The_live_slots_wrap_round_the_pools_end()
    {
        var emitter = Create(capacity: 1024, spawns: 512);
        var target = new GpuParticleBurst[16];
        for (var i = 0; i < 4; i++)
        {
            emitter.Emit(Burst(256));
            emitter.Drain(target, Step);
        }

        for (var i = 0; i < 7; i++)
        {
            emitter.Drain(target, Step);
        }

        // The first frame's slots, 0 to 255, have died and are taken again: the pool is full of living particles.
        emitter.Emit(Burst(256));
        var full = emitter.Drain(target, Step);
        var wrapped = emitter.Drain(target, Step);

        Assert.Equal((0, 1024), (full.LiveFirst, full.LiveCount));
        // The second frame's slots have died too: 512 to 1023, then 0 to 255.
        Assert.Equal((512, 768), (wrapped.LiveFirst, wrapped.LiveCount));
    }

    [Fact]
    public void Overwriting_living_particles_makes_the_whole_pool_live()
    {
        var emitter = Create(capacity: 1024, spawns: 512);
        var target = new GpuParticleBurst[16];
        for (var i = 0; i < 2; i++)
        {
            emitter.Emit(Burst(500));
            emitter.Drain(target, Step);
        }

        var before = emitter.Drain(target, Step);
        emitter.Emit(Burst(500));
        var overwritten = emitter.Drain(target, Step);

        Assert.Equal((0, 1000), (before.LiveFirst, before.LiveCount));
        Assert.Equal((0, 1024), (overwritten.LiveFirst, overwritten.LiveCount));
    }

    /// <summary>
    /// More frames than the record holds: the oldest batches become one, which keeps the later death. A long-lived
    /// burst in the second frame stays among the live slots after every short one has died.
    /// </summary>
    [Fact]
    public void Joining_the_oldest_batches_keeps_every_living_slot()
    {
        var emitter = Create(capacity: 16_384);
        var smoke = emitter.Recipes.Register("smoke", new ParticleRecipe { LifetimeMin = 9, LifetimeMax = 10 });
        var target = new GpuParticleBurst[16];
        var frames = ParticleEmitter.MaxBatches + 50;
        for (var i = 0; i < frames; i++)
        {
            emitter.Emit(i == 1 ? Burst(10) with { Recipe = smoke } : Burst(10));
            emitter.Drain(target, 0.001f);
        }

        // 4.5 s later on the particles' clock, every 2 s burst has died and the 10 s one has not.
        ParticleFrame later = default;
        for (var i = 0; i < 18; i++)
        {
            later = emitter.Drain(target, Step);
        }

        Assert.True(later.Active);
        Assert.True(later.LiveFirst <= 10 && later.LiveFirst + later.LiveCount >= 20, $"{later}");
        Assert.True(later.LiveFirst + later.LiveCount <= frames * 10, $"{later}");
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
        var cleared = emitter.Drain(target, Step);
        var next = emitter.Drain(target, Step);

        // Only the burst queued after the clear is made, from slot 0; the frame is active for its life alone, and the
        // 5,000 made before the clear are not among the live slots.
        Assert.Equal(new ParticleFrame(1, 7, true, Clear: true, LiveFirst: 0, LiveCount: 7), cleared);
        Assert.Equal(0u, target[0].First);
        Assert.Equal(0, emitter.StressBursts);
        Assert.False(next.Clear);
        Assert.Equal(ExpectedFrames(2, Step) - 1, FramesStillActive(emitter, target, Step));
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
        Assert.Equal(new ParticleFrame(1, 7, true, LiveCount: 7), emitter.Drain(target, 0));
    }

    [Fact]
    public void A_clear_with_nothing_after_it_still_reaches_the_next_frame()
    {
        var emitter = Create();
        var target = new GpuParticleBurst[16];
        emitter.Emit(Burst(5));
        emitter.Drain(target, 0);

        emitter.Clear();

        Assert.Equal(new ParticleFrame(0, 0, false, Clear: true), emitter.Drain(target, Step));
    }

    /// <summary>Off simulates nothing, so the particles alive wait, and move again from where they were when on.</summary>
    [Fact]
    public void Off_drops_bursts_and_pauses_the_particles()
    {
        var emitter = Create();
        var target = new GpuParticleBurst[16];
        emitter.Emit(Burst(5));
        emitter.Drain(target, Step);

        emitter.Enabled = false;
        emitter.Emit(Burst(5));
        var queued = emitter.QueuedBursts;
        var off = emitter.Drain(target, Step);
        for (var i = 0; i < 100; i++)
        {
            emitter.Drain(target, Step);
        }

        emitter.Enabled = true;

        Assert.Equal(0, queued);
        Assert.Equal(default, off);
        Assert.Equal(ExpectedFrames(2, Step), FramesStillActive(emitter, target, Step));
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
    public void A_detail_recipes_bursts_make_the_effects_share_of_their_particles_and_others_all()
    {
        var effects = new EffectsBudget();
        var emitter = Create(effects: effects);
        var sparks = emitter.Recipes.Register("sparks", new ParticleRecipe { Detail = true });
        var target = new GpuParticleBurst[16];

        // At the full share every particle is made.
        emitter.Emit(Burst(40) with { Recipe = sparks });
        Assert.Equal(40, emitter.Drain(target, Step).Spawns);

        // At a quarter: a detail burst of 40 makes 10, rounded; one of 1 still makes 1; a plain burst makes all 40.
        effects.Share = 0.25f;
        emitter.Emit(Burst(40) with { Recipe = sparks });
        emitter.Emit(Burst(1) with { Recipe = sparks });
        emitter.Emit(Burst(40));
        emitter.Emit(Burst(6) with { Recipe = sparks });
        var frame = emitter.Drain(target, Step);

        Assert.Equal(10 + 1 + 40 + 2, frame.Spawns);
        Assert.Equal([10u, 1u, 40u, 2u], target[..4].ToArray().Select(b => b.Count).ToArray());
        // The thinned bursts take only the slots they fill.
        Assert.Equal([40u, 50u, 51u, 91u], target[..4].ToArray().Select(b => b.First).ToArray());
        Assert.Equal([0u, 10u, 11u, 51u], target[..4].ToArray().Select(b => b.Prefix).ToArray());
    }

    [Fact]
    public void Emitting_and_draining_allocate_nothing()
    {
        var emitter = Create(bursts: 256, spawns: 16_384, effects: new EffectsBudget { Ceiling = 0.5f });
        var target = new GpuParticleBurst[256];
        var burst = Burst(10);
        var detail = Burst(10) with
        {
            Recipe = emitter.Recipes.Register("detail", new ParticleRecipe { Detail = true }),
        };
        emitter.Emit(in burst);
        emitter.Emit(in detail);
        emitter.Drain(target, 0);

        // Frames 5 ms apart for 3 s: more batches live than the record holds, so they join, and the oldest die.
        var start = SystemAllocation.StartMeasurement();
        for (var frame = 0; frame < 600; frame++)
        {
            for (var i = 0; i < 20; i++)
            {
                emitter.Emit(in burst);
                emitter.Emit(in detail);
            }

            emitter.Drain(target, 0.005f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
