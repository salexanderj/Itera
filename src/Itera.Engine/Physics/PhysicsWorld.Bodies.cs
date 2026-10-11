using BepuPhysics;

namespace Itera.Engine.Physics;

/// <summary>Marks a game gives a body of its own (<see cref="PhysicsWorld.AddBody"/>), any of them together.</summary>
[Flags]
public enum PhysicsMarks : byte
{
    None = 0,

    /// <summary>
    /// Never frozen where the voxel world round it is not loaded (<see cref="VoxelColliders"/>): it goes on moving, and
    /// meets the voxels only where they are loaded. For a body that keeps going far from every player (a projectile, a
    /// vehicle the game streams the world round itself).
    /// </summary>
    KeepsMovingUnloaded = 1,
}

/// <summary>A game's settings for a body or static of its own; the default is the engine's (its material, no marks).</summary>
public readonly record struct PhysicsBodySettings
{
    /// <summary>A game's marks for it.</summary>
    public PhysicsMarks Marks { get; init; }

    /// <summary>Its physics material (<see cref="PhysicsMaterials"/>).</summary>
    public PhysicsMaterialId Material { get; init; }
}

public sealed partial class PhysicsWorld
{
    /// <summary>
    /// Called with a body about to be removed through <see cref="RemoveBody"/> (the voxel colliders forget a frozen body's
    /// kept state, <see cref="VoxelColliders.Forget"/>). Set once, by the match's voxel colliders.
    /// </summary>
    internal Action<BodyHandle>? BodyRemoving { get; set; }

    /// <summary>
    /// Adds a game's own body, recorded as the engine's loose bodies are (<see cref="BodyKind.Loose"/>, voxel level 0):
    /// it touches the voxels, the game's statics, other bodies, and characters' stand-ins; where the voxel world round
    /// it is not loaded it is frozen until it is, unless <see cref="PhysicsMarks.KeepsMovingUnloaded"/>; it moves with
    /// the match's origin. Remove it with <see cref="RemoveBody"/> (not <c>Simulation.Bodies.Remove</c>, which would leave
    /// its record to the next body that takes its handle). It falls by <paramref name="gravityScale"/> of the match's
    /// gravity (<see cref="SetGravityScale"/>). Its shape stays the caller's. Main thread, between steps.
    /// </summary>
    public BodyHandle AddBody(
        in BodyDescription description,
        in PhysicsBodySettings settings = default,
        float gravityScale = 1
    )
    {
        if (!float.IsFinite(gravityScale))
        {
            throw new ArgumentOutOfRangeException(nameof(gravityScale), gravityScale, "A gravity scale is finite.");
        }

        CheckMaterial(settings.Material);
        var body = Simulation.Bodies.Add(description);
        _kinds.Set(body, BodyKind.Loose, marks: settings.Marks, material: settings.Material);
        if (gravityScale != 1)
        {
            _kinds.SetGravityScale(body, gravityScale);
        }

        return body;
    }

    /// <summary>
    /// Removes a body this world recorded (a game's, <see cref="AddBody"/>, or the engine's), forgetting its record and any
    /// state the voxel colliders kept for it, so a body that takes its handle next starts afresh. Bepu removes its joints
    /// with it; its shape stays the caller's. Main thread, between steps.
    /// </summary>
    public void RemoveBody(BodyHandle body)
    {
        BodyRemoving?.Invoke(body);
        Simulation.Bodies.Remove(body);
        _kinds.Forget(body);
        _contacts?.Forget(PhysicsContacts.BodySide(body));
    }

    /// <summary>
    /// Adds a game's own static, which bodies of every voxel level touch (as a mesh collider), of the material
    /// <paramref name="settings"/> name. Remove it with <see cref="RemoveStatic"/>. Its shape stays the caller's. Main
    /// thread, between steps.
    /// </summary>
    public StaticHandle AddStatic(in StaticDescription description, in PhysicsBodySettings settings = default)
    {
        CheckMaterial(settings.Material);
        var handle = Simulation.Statics.Add(description);
        _kinds.SetStatic(handle, BodyKinds.AnyLevel, settings.Material);
        return handle;
    }

    /// <summary>Sets <paramref name="body"/>'s physics material, from the next step on. Main thread, between steps.</summary>
    public void SetMaterial(BodyHandle body, PhysicsMaterialId material)
    {
        CheckMaterial(material);
        _kinds.SetMaterial(body, material);
    }

    /// <summary>Sets a static's physics material, from the next step on. Main thread, between steps.</summary>
    public void SetMaterial(StaticHandle handle, PhysicsMaterialId material)
    {
        CheckMaterial(material);
        _kinds.SetMaterial(handle, material);
    }

    /// <summary>A body's physics material.</summary>
    public PhysicsMaterialId MaterialOf(BodyHandle body) => _kinds.Body(body.Value).Material;

    /// <summary>A static's physics material.</summary>
    public PhysicsMaterialId MaterialOf(StaticHandle handle) => _kinds.Static(handle.Value).Material;

    /// <summary>Throws for a material the run did not register.</summary>
    internal void CheckMaterial(PhysicsMaterialId material)
    {
        if (material.Value >= _kinds.Contacts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(material), material.Value, "No such physics material.");
        }
    }

    /// <summary>Removes a static, forgetting its record. Its shape stays the caller's. Main thread, between steps.</summary>
    public void RemoveStatic(StaticHandle handle)
    {
        Simulation.Statics.Remove(handle);
        _kinds.ClearStatic(handle);
        _contacts?.Forget(PhysicsContacts.StaticSide(handle));
    }

    /// <summary>
    /// Which of the bodies that took <paramref name="body"/>'s handle it is: a number that changes whenever a new body
    /// takes the handle (0 for one this world did not record). Keep it with the handle, and test it with
    /// <see cref="Holds"/>.
    /// </summary>
    public long BornOf(BodyHandle body) => _kinds.Born(body.Value);

    /// <summary>True while the body <paramref name="born"/> named (<see cref="BornOf"/>) is still the one at <paramref name="body"/>.</summary>
    public bool Holds(BodyHandle body, long born) =>
        born != 0 && Simulation.Bodies.BodyExists(body) && _kinds.Born(body.Value) == born;

    /// <summary>A game's marks for a body (<see cref="PhysicsBodySettings.Marks"/>).</summary>
    public PhysicsMarks MarksOf(BodyHandle body) => _kinds.Marks(body.Value);
}
