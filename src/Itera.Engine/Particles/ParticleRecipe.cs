using System.Numerics;

namespace Itera.Engine.Particles;

/// <summary>How a particle is drawn.</summary>
public enum ParticleDraw : uint
{
    /// <summary>A small lit cube that tumbles, tested against and writing the depth image: debris, drops, embers.</summary>
    Cube = 0,

    /// <summary>A soft round quad facing the camera, blended over the picture: glow, sparks, smoke, dust, mist.</summary>
    Sprite = 1,
}

/// <summary>What a particle does when it meets a solid voxel of the world.</summary>
public enum ParticleCollision : uint
{
    /// <summary>It passes through.</summary>
    None = 0,

    /// <summary>It bounces off the face it hit; once it moves slowly on a floor, it rests there.</summary>
    Bounce = 1,

    /// <summary>It stops where it hit and stays there until it dies.</summary>
    Stick = 2,

    /// <summary>It dies.</summary>
    Die = 3,
}

/// <summary>How a sprite is stretched.</summary>
public enum ParticleStretch : uint
{
    /// <summary>Round.</summary>
    None = 0,

    /// <summary>Along its velocity on screen, longer the faster it moves (<see cref="ParticleRecipe.StretchSeconds"/>).</summary>
    Velocity = 1,

    /// <summary>From its burst's <see cref="ParticleBurst.Origin"/> to its <see cref="ParticleBurst.End"/>: a beam.</summary>
    Segment = 2,
}

/// <summary>Switches of a <see cref="ParticleRecipe"/>.</summary>
[Flags]
public enum ParticleTraits : uint
{
    None = 0,

    /// <summary>
    /// Lit by the sun and the sky, as the world is. Without it the particle gives
    /// off its own light (sparks, glow).
    /// </summary>
    Lit = 1,

    /// <summary>Fades out over the last part of its life (a sprite's opacity, a cube's size).</summary>
    FadeOut = 2,
}

/// <summary>
/// One kind of particle: how its particles start (speed, spread, life, size), move (gravity, drag, collision), and
/// look (cube or sprite, colours, glow, stretch). Registered by name in <see cref="ParticleRecipes"/>; a
/// <see cref="ParticleBurst"/> names one by its id. Every burst of a recipe draws its particles' own values from these
/// ranges at random. Colours are linear RGB with alpha, each 0 to 1. Units are metres and seconds.
/// </summary>
public struct ParticleRecipe
{
    public ParticleRecipe() { }

    /// <summary>Cube or sprite.</summary>
    public ParticleDraw Draw = ParticleDraw.Sprite;

    /// <summary>What happens where it meets the world.</summary>
    public ParticleCollision Collision = ParticleCollision.None;

    /// <summary>How a sprite is stretched; cubes ignore it.</summary>
    public ParticleStretch Stretch = ParticleStretch.None;

    /// <summary>Switches.</summary>
    public ParticleTraits Traits = ParticleTraits.FadeOut;

    /// <summary>The colour at birth; alpha is a sprite's opacity.</summary>
    public Vector4 ColourStart = Vector4.One;

    /// <summary>The colour at death, blended from <see cref="ColourStart"/> over its life.</summary>
    public Vector4 ColourEnd = Vector4.One;

    /// <summary>Light it gives off as a multiple of its colour; 0 for none. Unlit particles show their colour times 1 + this.</summary>
    public float Glow;

    /// <summary>
    /// A sprite's blending: 0 adds its light to the picture (glow, sparks), 1 covers what is behind it by its opacity
    /// (smoke, dust, blood mist); between mixes the two.
    /// </summary>
    public float Cover;

    /// <summary>Slowest start speed, metres per second, along its burst's direction (spread by <see cref="SpreadDegrees"/>).</summary>
    public float SpeedMin;

    /// <summary>Fastest start speed, metres per second.</summary>
    public float SpeedMax;

    /// <summary>
    /// The largest angle between a particle's start direction and its burst's direction, degrees (0 to 180; 180 is
    /// any direction).
    /// </summary>
    public float SpreadDegrees = 180;

    /// <summary>Shortest life, seconds.</summary>
    public float LifetimeMin = 1;

    /// <summary>Longest life, seconds.</summary>
    public float LifetimeMax = 1;

    /// <summary>Size at birth, metres: a cube's edge, a sprite's radius (a segment's half width).</summary>
    public float SizeStart = 0.05f;

    /// <summary>Size at death, metres, blended from <see cref="SizeStart"/> over its life.</summary>
    public float SizeEnd = 0.05f;

    /// <summary>How far one particle's size may differ from the recipe's, as a share (0 to 1).</summary>
    public float SizeJitter;

    /// <summary>How far one particle's colour may be lighter or darker than the recipe's, as a share (0 to 1).</summary>
    public float ColourJitter;

    /// <summary>
    /// Gravity as a multiple of the match's (<c>PhysicsWorld.Gravity</c>, 9.81 m/s² down by default); negative rises
    /// (smoke). A particle rests on a surface that faces against the match's gravity.
    /// </summary>
    public float Gravity;

    /// <summary>How fast its speed falls away, per second: the velocity is multiplied by exp(-drag * seconds).</summary>
    public float Drag;

    /// <summary>The share of its speed into a face it keeps after a bounce, 0 to 1.</summary>
    public float Restitution = 0.3f;

    /// <summary>The share of its speed along a face it loses in a bounce, 0 to 1.</summary>
    public float Friction = 0.4f;

    /// <summary>A cube's fastest turn, radians per second; each cube's turn slows to a stop over about a second.</summary>
    public float Spin;

    /// <summary>With <see cref="ParticleStretch.Velocity"/>: seconds of its movement the sprite covers behind it.</summary>
    public float StretchSeconds = 0.03f;

    /// <summary>
    /// With an attractor (<see cref="ParticleBurst.Attractor"/>): how fast it turns towards it, per second (its
    /// velocity moves this share of the way to the one that meets it, each second). It dies within 10 cm of it.
    /// </summary>
    public float Attraction = 4;

    /// <summary>The share of its life over which a sprite fades in, 0 to 1.</summary>
    public float FadeIn = 0.05f;

    /// <summary>The share of its life, at the end, over which it fades out with <see cref="ParticleTraits.FadeOut"/>.</summary>
    public float FadeOutShare = 0.4f;
}
