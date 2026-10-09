using System.Numerics;

namespace Itera.Engine.Particles;

/// <summary>
/// General particle effects a game may use as they are: the recipes <c>flare</c>, <c>sparks</c>, <c>embers</c>,
/// <c>debris</c>, <c>smoke</c>, <c>dust-puff</c>, and <c>grit</c>, registered by name when it is made (the console's
/// <c>particles test NAME</c> uses the names), and the helpers to emit them. A game makes one when it wants it; the
/// engine does not. Rates are per second, so an effect made every frame looks the same at any frame rate: the count of a
/// frame is the rate times the frame's time, rounded up or down at random (<see cref="Count"/>). Main thread; allocates
/// nothing after construction.
/// </summary>
public sealed class StockEffects
{
    /// <summary>What dust from a landing or a hit pales towards.</summary>
    public static readonly Vector3 PaleDust = new(0.85f, 0.82f, 0.78f);

    private readonly ParticleEmitter _emitter;
    private uint _random = 0x2545_F491;

    /// <param name="emitter">The particles; the recipes are registered in its table.</param>
    public StockEffects(ParticleEmitter emitter)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        _emitter = emitter;
        var recipes = emitter.Recipes;
        Flare = recipes.Register(
            "flare",
            new ParticleRecipe
            {
                ColourStart = new Vector4(1f, 0.6f, 0.3f, 0.8f),
                ColourEnd = new Vector4(1f, 0.25f, 0.08f, 0.4f),
                Glow = 2,
                LifetimeMin = 0.04f,
                LifetimeMax = 0.08f,
                SizeStart = 0.07f,
                SizeEnd = 0.03f,
                SizeJitter = 0.3f,
                FadeIn = 0,
            }
        );
        Sparks = recipes.Register(
            "sparks",
            new ParticleRecipe
            {
                Collision = ParticleCollision.Bounce,
                Stretch = ParticleStretch.Velocity,
                ColourStart = new Vector4(1f, 0.85f, 0.45f, 1),
                ColourEnd = new Vector4(1f, 0.3f, 0.08f, 1),
                Glow = 3,
                SpeedMin = 2,
                SpeedMax = 6,
                SpreadDegrees = 75,
                LifetimeMin = 0.2f,
                LifetimeMax = 0.55f,
                SizeStart = 0.006f,
                SizeEnd = 0.003f,
                Gravity = 1,
                Drag = 1,
                Restitution = 0.4f,
                Friction = 0.3f,
                StretchSeconds = 0.035f,
                FadeIn = 0,
            }
        );
        Embers = recipes.Register(
            "embers",
            new ParticleRecipe
            {
                Draw = ParticleDraw.Cube,
                Collision = ParticleCollision.Bounce,
                ColourStart = new Vector4(1f, 0.55f, 0.15f, 1),
                ColourEnd = new Vector4(0.35f, 0.06f, 0.02f, 1),
                Glow = 2,
                SpeedMin = 0.5f,
                SpeedMax = 2,
                SpreadDegrees = 60,
                LifetimeMin = 0.6f,
                LifetimeMax = 1.3f,
                SizeStart = 0.008f,
                SizeEnd = 0.006f,
                SizeJitter = 0.4f,
                Gravity = 0.7f,
                Drag = 0.6f,
                Spin = 12,
            }
        );
        Debris = recipes.Register(
            "debris",
            new ParticleRecipe
            {
                Draw = ParticleDraw.Cube,
                Collision = ParticleCollision.Bounce,
                Traits = ParticleTraits.Lit | ParticleTraits.FadeOut,
                SpeedMin = 1.5f,
                SpeedMax = 4,
                SpreadDegrees = 55,
                LifetimeMin = 2.5f,
                LifetimeMax = 4,
                SizeStart = 0.02f,
                SizeEnd = 0.02f,
                SizeJitter = 0.5f,
                ColourJitter = 0.25f,
                Gravity = 1,
                Drag = 0.2f,
                Restitution = 0.3f,
                Friction = 0.5f,
                Spin = 15,
                FadeOutShare = 0.2f,
            }
        );
        Smoke = recipes.Register(
            "smoke",
            new ParticleRecipe
            {
                Traits = ParticleTraits.Lit | ParticleTraits.FadeOut,
                Cover = 1,
                ColourStart = new Vector4(0.32f, 0.3f, 0.28f, 0.35f),
                ColourEnd = new Vector4(0.45f, 0.44f, 0.42f, 0.2f),
                SpeedMin = 0.2f,
                SpeedMax = 0.6f,
                SpreadDegrees = 40,
                LifetimeMin = 1,
                LifetimeMax = 2,
                SizeStart = 0.06f,
                SizeEnd = 0.3f,
                SizeJitter = 0.3f,
                Gravity = -0.04f,
                Drag = 1.5f,
                FadeIn = 0.1f,
                FadeOutShare = 0.6f,
            }
        );
        DustPuff = recipes.Register(
            "dust-puff",
            new ParticleRecipe
            {
                Traits = ParticleTraits.Lit | ParticleTraits.FadeOut,
                Cover = 0.9f,
                ColourStart = new Vector4(1, 1, 1, 0.5f),
                ColourEnd = new Vector4(1, 1, 1, 0.3f),
                SpeedMin = 0.4f,
                SpeedMax = 1.4f,
                SpreadDegrees = 80,
                LifetimeMin = 0.8f,
                LifetimeMax = 1.6f,
                SizeStart = 0.08f,
                SizeEnd = 0.35f,
                SizeJitter = 0.3f,
                Gravity = -0.02f,
                Drag = 2.5f,
                FadeIn = 0.1f,
                FadeOutShare = 0.6f,
            }
        );
        Grit = recipes.Register(
            "grit",
            new ParticleRecipe
            {
                Draw = ParticleDraw.Cube,
                Collision = ParticleCollision.Bounce,
                Traits = ParticleTraits.Lit | ParticleTraits.FadeOut,
                SpeedMin = 0.5f,
                SpeedMax = 2,
                SpreadDegrees = 70,
                LifetimeMin = 1.5f,
                LifetimeMax = 2.5f,
                SizeStart = 0.012f,
                SizeEnd = 0.012f,
                SizeJitter = 0.5f,
                ColourJitter = 0.25f,
                Gravity = 1,
                Drag = 0.3f,
                Restitution = 0.25f,
                Friction = 0.6f,
                Spin = 15,
                FadeOutShare = 0.25f,
            }
        );
    }

    /// <summary>A short bright flash.</summary>
    public int Flare { get; }

    /// <summary>Glowing streaks that bounce.</summary>
    public int Sparks { get; }

    /// <summary>Glowing specks that cool as they fall.</summary>
    public int Embers { get; }

    /// <summary>Lit chips that bounce and lie a moment, of the burst's tint.</summary>
    public int Debris { get; }

    /// <summary>A dark cloud that grows and rises.</summary>
    public int Smoke { get; }

    /// <summary>A pale cloud that spreads low, of the burst's tint.</summary>
    public int DustPuff { get; }

    /// <summary>Small lit grains that bounce, of the burst's tint.</summary>
    public int Grit { get; }

    /// <summary>
    /// Dust where something landed at <paramref name="point"/> on ground of colour <paramref name="colour"/>, at
    /// <paramref name="speed"/> metres per second: puffs spreading low over the ground and grit, more for a harder
    /// landing, from a footprint of <paramref name="radius"/> metres.
    /// </summary>
    public void Dust(Vector3 point, Vector4 colour, float speed, float radius)
    {
        var strength = Math.Clamp((speed - 1.5f) / 6, 0.1f, 1);
        var puffs = 3 + (int)(10 * strength);
        // Dust is paler than the ground it rises from: fine grains, and lit from all sides in the air.
        var pale = Paler(colour, PaleDust, 0.4f);
        Emit(DustPuff, puffs, ParticleShape.Disc, point, default, radius, Vector3.UnitY * (0.4f + strength), pale);
        Emit(
            Grit,
            4 + (int)(20 * strength),
            ParticleShape.Disc,
            point,
            default,
            radius,
            Vector3.UnitY * strength,
            colour
        );
    }

    /// <summary>
    /// <paramref name="colour"/> moved <paramref name="share"/> of the way to <paramref name="towards"/>, opaque: dust is
    /// paler than what it rises from.
    /// </summary>
    public static Vector4 Paler(Vector4 colour, Vector3 towards, float share) =>
        new(Vector3.Lerp(new Vector3(colour.X, colour.Y, colour.Z), towards, share), 1);

    /// <summary>
    /// <paramref name="recipe"/> with its sizes times <paramref name="size"/>, its speeds times <paramref name="speed"/>,
    /// and its lifetimes times <paramref name="life"/>.
    /// </summary>
    public static ParticleRecipe Scaled(in ParticleRecipe recipe, float size, float speed, float life)
    {
        var scaled = recipe;
        scaled.SizeStart *= size;
        scaled.SizeEnd *= size;
        scaled.SpeedMin *= speed;
        scaled.SpeedMax *= speed;
        scaled.LifetimeMin *= life;
        scaled.LifetimeMax *= life;
        return scaled;
    }

    /// <summary>A frame's share of <paramref name="perSecond"/>, rounded up or down at random so the rate holds on average.</summary>
    public int Count(float perSecond, float seconds)
    {
        var exact = perSecond * seconds;
        var whole = (int)exact;
        _random ^= _random << 13;
        _random ^= _random >> 17;
        _random ^= _random << 5;
        return whole + ((_random >> 8) * (1f / 16_777_216f) < exact - whole ? 1 : 0);
    }

    /// <summary>Queues <paramref name="count"/> particles of <paramref name="recipe"/>; nothing for a count of 0 or less.</summary>
    public void Emit(
        int recipe,
        int count,
        ParticleShape shape,
        Vector3 origin,
        Vector3 end,
        float radius,
        Vector3 direction,
        Vector4 tint
    )
    {
        if (count <= 0)
        {
            return;
        }

        _emitter.Emit(
            new ParticleBurst
            {
                Recipe = recipe,
                Count = count,
                Shape = shape,
                Origin = origin,
                End = end,
                Radius = radius,
                Direction = direction,
                Tint = tint,
            }
        );
    }
}
