using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;

namespace Itera.Engine.Systems;

/// <summary>
/// A game's own way for players to move (climbing, a wall run), which <see cref="PlayerMovementSystem"/> offers each
/// walking player's step to before its own steering. It either leaves the step to the engine (walking, jumping, gravity)
/// or takes it: it gives the velocity (gravity included or not, as it wants) and the step height, and the engine still
/// moves the body through the character mover and keeps its contact, velocity, and eye as for any step. Its state per
/// player goes in a component of the game's, read and set through <see cref="MovementModeInput.World"/> on
/// <see cref="MovementModeInput.Entity"/>; it runs inside the system's query, so it changes no entity's components'
/// set (no add, remove, or destroy). A game registers one in the match's scope. Main thread; allocates nothing per
/// step.
/// </summary>
public interface IMovementMode
{
    /// <summary>
    /// True to take <paramref name="input"/>'s step with <paramref name="move"/> (its velocity and step height, which
    /// start as the body's velocity and step); false to leave it to the engine's walking.
    /// </summary>
    bool Steer(in MovementModeInput input, ref MovementModeMove move);
}

/// <summary>What a movement mode knows of a player's step (<see cref="IMovementMode"/>).</summary>
/// <param name="Entity">The player.</param>
/// <param name="World">The match's ECS world, for the game's own component of the player.</param>
/// <param name="Command">
/// What the player asks this step, made safe (<see cref="PlayerMovementSystem.Sanitize"/>) and limited as the engine's
/// walking limits it: a crouched command without Jump or Sprint and held to a crouch's pace, a backward one held back.
/// </param>
/// <param name="Asked">
/// The same command as the player asked it, made safe but not limited: what a mode reads to tell a crouch pressed while
/// sprinting (a slide), or Jump pressed while crouched (a jump out of a slide), from a plain crouch.
/// </param>
/// <param name="Size">The body's size.</param>
/// <param name="Feet">Where its feet are, in metres.</param>
/// <param name="Velocity">Its velocity after the last step, in metres per second.</param>
/// <param name="Contact">What the last step's move met.</param>
/// <param name="Stats">Its movement stats.</param>
/// <param name="Seconds">The step's length.</param>
public readonly record struct MovementModeInput(
    Entity Entity,
    World World,
    PlayerCommand Command,
    PlayerCommand Asked,
    CharacterSize Size,
    Vector3 Feet,
    Vector3 Velocity,
    CharacterContact Contact,
    MovementStats Stats,
    float Seconds
);

/// <summary>The step a movement mode takes (<see cref="IMovementMode"/>).</summary>
public struct MovementModeMove
{
    /// <summary>The velocity to move by this step, in metres per second.</summary>
    public Vector3 Velocity;

    /// <summary>The highest ledge the body steps up onto this step, in metres; 0 for none (and no keeping to the ground).</summary>
    public float StepMetres;

    /// <summary>True when the step is a jump: kept in the contact (<see cref="CharacterContact.Jumped"/>), as the engine's own.</summary>
    public bool Jumped;

    /// <summary>
    /// True when the body steps up onto a ledge within <see cref="StepMetres"/> in the air too, as it does walking: a jump
    /// whose feet come that near a wall's top lands on it (<see cref="Physics.CharacterShape.StepsInAir"/>). False (the
    /// engine's own walking): only from the ground.
    /// </summary>
    public bool StepsInAir;
}
