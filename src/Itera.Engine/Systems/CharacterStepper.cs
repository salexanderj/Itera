using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Physics;

namespace Itera.Engine.Systems;

/// <summary>
/// One character's movement step, exactly as <see cref="PlayerMovementSystem"/> runs it for each character it moves: the
/// command made safe, the crouch, the limits of crouching and moving backwards, the game's <see cref="IMovementMode"/>
/// or the engine's walking, and the mover, reading and writing the entity's components. For running a character's step
/// again (a client replaying its commands after the host's correction) and for one character a system moves itself. A
/// flying character's feet follow the camera in the system alone: here a flying one ends its crouch and stops. The
/// match's scope has one (with its movement mode); a test makes its own. Main thread; no structural change.
/// </summary>
public sealed class CharacterStepper
{
    private readonly World _world;
    private readonly VoxelCharacterMover _mover;
    private readonly IMovementMode? _mode;
    private readonly PhysicsWorld? _physics;

    /// <param name="world">The ECS world the characters are in.</param>
    /// <param name="mover">Moves bodies through the voxels.</param>
    /// <param name="mode">The game's own way of moving, as <see cref="PlayerMovementSystem"/> is given it; none for the engine's alone.</param>
    /// <param name="physics">
    /// The match's physics, for its gravity's share of the Earth's and the ground's friction; none for a character's own
    /// gravity and full grip.
    /// </param>
    public CharacterStepper(
        World world,
        VoxelCharacterMover mover,
        IMovementMode? mode = null,
        PhysicsWorld? physics = null
    )
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(mover);
        _world = world;
        _mover = mover;
        _mode = mode;
        _physics = physics;
    }

    /// <summary>
    /// Runs one step of <paramref name="seconds"/> for <paramref name="entity"/> from its <see cref="PlayerCommand"/>:
    /// false, with nothing changed, when it is not alive, is <see cref="Bodiless"/>, or lacks a component
    /// <see cref="PlayerMovementSystem"/> moves by (<see cref="PlayerCommand"/>, <see cref="PlayerMode"/>,
    /// <see cref="CharacterSize"/>, <see cref="CharacterPosition"/>, <see cref="CharacterVelocity"/>,
    /// <see cref="CharacterContact"/>, <see cref="EyeSmoothing"/>, <see cref="MovementStats"/>).
    /// </summary>
    public bool Step(Entity entity, float seconds)
    {
        if (
            !_world.IsAlive(entity)
            || _world.Has<Bodiless>(entity)
            || !_world.Has<PlayerCommand>(entity)
            || !_world.Has<PlayerMode>(entity)
            || !_world.Has<CharacterSize>(entity)
            || !_world.Has<CharacterPosition>(entity)
            || !_world.Has<CharacterVelocity>(entity)
            || !_world.Has<CharacterContact>(entity)
            || !_world.Has<EyeSmoothing>(entity)
            || !_world.Has<MovementStats>(entity)
        )
        {
            return false;
        }

        var job = new PlayerMovementSystem.Move
        {
            Mover = _mover,
            Mode = _mode,
            World = _world,
            Seconds = seconds,
            GravityShare = _physics?.GravityShare ?? 1,
            Physics = _physics,
        };
        job.Update(
            entity,
            ref _world.Get<PlayerCommand>(entity),
            ref _world.Get<PlayerMode>(entity),
            ref _world.Get<CharacterSize>(entity),
            ref _world.Get<CharacterPosition>(entity),
            ref _world.Get<CharacterVelocity>(entity),
            ref _world.Get<CharacterContact>(entity),
            ref _world.Get<EyeSmoothing>(entity),
            ref _world.Get<MovementStats>(entity)
        );
        return true;
    }
}
