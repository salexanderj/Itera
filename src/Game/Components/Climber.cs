using System.Numerics;

namespace Game.Components;

/// <summary>Where a player is in a climb (<see cref="ClimbMode"/>).</summary>
public enum ClimbState : byte
{
    /// <summary>Not climbing: the engine walks it.</summary>
    Free = 0,

    /// <summary>On a wall.</summary>
    Climbing = 1,

    /// <summary>Rising past a wall's top to stand on it.</summary>
    PullingUp = 2,

    /// <summary>Moving over a wall's top edge onto it.</summary>
    PullingOver = 3,
}

/// <summary>
/// A player's climb (<see cref="ClimbMode"/>, the starter's movement mode): its state, the wall's normal across x and z
/// (eased from step to step), the ledge's top and the feet's place on it while pulling up, how long the pull has gone
/// on, how long before it may grab again, and whether Jump was held at the last step (a grab or a jump off needs a
/// fresh press). <c>PlayerSpawnSystem</c> gives the player one.
/// </summary>
public struct Climber
{
    public ClimbState State;
    public Vector2 Normal;
    public Vector3 Target;
    public float Seconds;
    public float Regrab;
    public bool JumpHeld;
}
