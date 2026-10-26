using System.Numerics;
using Itera.Engine.Particles;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Particles;

/// <summary>
/// <see cref="StockEffects"/>: each recipe is registered once under its name, and emitting each allocates nothing.
/// </summary>
public sealed class StockEffectsTests
{
    private static readonly string[] s_names = ["flare", "sparks", "embers", "debris", "smoke", "dust-puff", "grit"];

    private static ParticleEmitter CreateEmitter()
    {
        var options = new EngineOptions();
        options.Particles.MaxBurstsPerFrame = 64;
        return new ParticleEmitter(Options.Create(options), NullLogger<ParticleEmitter>.Instance);
    }

    private static int[] Ids(StockEffects effects) =>
        [effects.Flare, effects.Sparks, effects.Embers, effects.Debris, effects.Smoke, effects.DustPuff, effects.Grit];

    [Fact]
    public void Every_recipe_is_registered_once_under_its_name()
    {
        var emitter = CreateEmitter();
        var effects = new StockEffects(emitter);

        Assert.Equal(s_names.Length, emitter.Recipes.Count);
        var ids = Ids(effects);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        for (var i = 0; i < s_names.Length; i++)
        {
            Assert.True(emitter.Recipes.TryFind(s_names[i], out var id), s_names[i]);
            Assert.Equal(ids[i], id);
            Assert.Equal(s_names[i], emitter.Recipes.NameOf(id));
        }
    }

    [Fact]
    public void Recipes_follow_those_already_in_the_table()
    {
        var emitter = CreateEmitter();
        var other = emitter.Recipes.Register("other", new ParticleRecipe { LifetimeMin = 1, LifetimeMax = 1 });

        var effects = new StockEffects(emitter);

        Assert.Equal(0, other);
        Assert.Equal(1, effects.Flare);
        Assert.Equal(s_names.Length + 1, emitter.Recipes.Count);
    }

    [Fact]
    public void Every_recipe_emits_without_allocating()
    {
        var emitter = CreateEmitter();
        var effects = new StockEffects(emitter);
        var target = new GpuParticleBurst[64];
        var ids = Ids(effects);
        var tint = new Vector4(0.4f, 0.3f, 0.2f, 1);
        EmitAll(effects, ids, tint);
        emitter.Drain(target, 0);

        var before = SystemAllocation.StartMeasurement();
        for (var frame = 0; frame < 100; frame++)
        {
            EmitAll(effects, ids, tint);
            effects.Dust(new Vector3(1, 2, 3), tint, 5, 0.3f);
            emitter.Drain(target, 0.016f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Emitting_queues_a_burst_of_the_recipe_and_nothing_for_a_count_of_zero()
    {
        var emitter = CreateEmitter();
        var effects = new StockEffects(emitter);

        effects.Emit(effects.Sparks, 0, ParticleShape.Point, Vector3.Zero, default, 0, Vector3.UnitY, default);
        Assert.Equal(0, emitter.QueuedBursts);

        effects.Emit(effects.Sparks, 12, ParticleShape.Point, Vector3.Zero, default, 0, Vector3.UnitY, default);
        Assert.Equal(1, emitter.QueuedBursts);
        Assert.Equal(12, emitter.QueuedSpawns);
    }

    [Fact]
    public void A_count_keeps_its_rate_on_average()
    {
        var effects = new StockEffects(CreateEmitter());
        var total = 0;
        for (var i = 0; i < 10_000; i++)
        {
            total += effects.Count(30, 1 / 60f);
        }

        // 30 a second at 60 frames a second is half a particle a frame.
        Assert.InRange(total, 4_700, 5_300);
    }

    [Fact]
    public void Scaled_changes_sizes_speeds_and_lifetimes_alone()
    {
        var recipe = new ParticleRecipe
        {
            SizeStart = 1,
            SizeEnd = 2,
            SpeedMin = 3,
            SpeedMax = 4,
            LifetimeMin = 5,
            LifetimeMax = 6,
            Glow = 7,
        };

        var scaled = StockEffects.Scaled(recipe, 2, 3, 4);

        Assert.Equal((2f, 4f), (scaled.SizeStart, scaled.SizeEnd));
        Assert.Equal((9f, 12f), (scaled.SpeedMin, scaled.SpeedMax));
        Assert.Equal((20f, 24f), (scaled.LifetimeMin, scaled.LifetimeMax));
        Assert.Equal(7f, scaled.Glow);
    }

    private static void EmitAll(StockEffects effects, int[] ids, Vector4 tint)
    {
        foreach (var id in ids)
        {
            var count = effects.Count(40, 1 / 60f) + 1;
            effects.Emit(id, count, ParticleShape.Sphere, Vector3.One, default, 0.1f, Vector3.UnitY, tint);
        }
    }
}
