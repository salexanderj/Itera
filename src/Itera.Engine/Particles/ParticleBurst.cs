using System.Numerics;

namespace Itera.Engine.Particles;

/// <summary>Where a burst's particles start.</summary>
public enum ParticleShape : uint
{
    /// <summary>All at <see cref="ParticleBurst.Origin"/>.</summary>
    Point = 0,

    /// <summary>Anywhere inside a ball of <see cref="ParticleBurst.Radius"/> around the origin.</summary>
    Sphere = 1,

    /// <summary>
    /// Evenly at random along the line from the origin to <see cref="ParticleBurst.End"/> (a streak); with a recipe of
    /// <see cref="ParticleStretch.Segment"/> each particle spans the whole line instead (a beam).
    /// </summary>
    Segment = 2,

    /// <summary>Anywhere on a disc of <see cref="ParticleBurst.Radius"/> around the origin, facing along the direction.</summary>
    Disc = 3,
}

/// <summary>
/// A request for <see cref="Count"/> particles of one recipe at once, queued with
/// <see cref="ParticleEmitter.Emit"/>: an impact, a puff, one step of a streak, one frame of a beam. The GPU makes the
/// particles and draws each one's own values from the recipe's ranges. Positions are world metres.
/// </summary>
public struct ParticleBurst
{
    /// <summary>The recipe's id (<see cref="ParticleRecipes.Register"/>).</summary>
    public int Recipe;

    /// <summary>How many particles, 1 or more.</summary>
    public int Count;

    /// <summary>Where they start.</summary>
    public ParticleShape Shape;

    /// <summary>The point, the ball's or disc's centre, or the segment's first end.</summary>
    public Vector3 Origin;

    /// <summary>The segment's second end.</summary>
    public Vector3 End;

    /// <summary>The ball's or disc's radius, metres.</summary>
    public float Radius;

    /// <summary>
    /// The way they fly, spread by the recipe's <see cref="ParticleRecipe.SpreadDegrees"/>; its length scales the
    /// recipe's speed. Zero sends each one any way at the recipe's speed. For a disc, also the way it faces.
    /// </summary>
    public Vector3 Direction;

    /// <summary>A velocity added to every particle's, metres per second: the speed of what they come from.</summary>
    public Vector3 Velocity;

    /// <summary>
    /// A colour the recipe's colours are multiplied by (linear RGB and alpha), such as a material's
    /// (<see cref="ParticleColours.OfMaterial"/>) or a creature's blood. All zero leaves them as they are.
    /// </summary>
    public Vector4 Tint;

    /// <summary>
    /// The attractor they fly to (1 to <see cref="ParticleEmitter.MaxAttractors"/> - 1, set with
    /// <see cref="ParticleEmitter.SetAttractor"/>), or 0 for none.
    /// </summary>
    public int Attractor;

    /// <summary>The seed of their random values; 0 lets the emitter pick one, so two bursts at one place differ.</summary>
    public uint Seed;
}
