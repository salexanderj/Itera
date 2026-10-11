using BepuPhysics;

namespace Itera.Engine.Physics;

/// <summary>
/// Marks a game gives a body or static of its own (<see cref="PhysicsWorld.AddBody"/>, <see cref="PhysicsWorld.AddStatic"/>,
/// <see cref="PhysicsWorld.SetMarks(BodyHandle, PhysicsMarks)"/>), any of them together.
/// </summary>
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

    /// <summary>
    /// Reports its impacts: each pair it is in gives an <see cref="ImpactEvent"/> as it begins touching (where, how
    /// fast, which materials) and as it ends (<see cref="PhysicsWorld.ImpactEvents"/>). Bodies and statics alike.
    /// </summary>
    ContactEvents = 2,
}

/// <summary>
/// A game's settings for a body or static of its own; the default is the engine's (its material, the default layer, no
/// group, no marks).
/// </summary>
public readonly record struct PhysicsBodySettings
{
    /// <summary>A game's marks for it.</summary>
    public PhysicsMarks Marks { get; init; }

    /// <summary>Its physics material (<see cref="PhysicsMaterials"/>).</summary>
    public PhysicsMaterialId Material { get; init; }

    /// <summary>Its collision layer (<see cref="PhysicsLayers"/>).</summary>
    public PhysicsLayerId Layer { get; init; }

    /// <summary>Its group: bodies and statics of one group other than 0 never touch each other (a game's numbers).</summary>
    public int Group { get; init; }
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
        CheckLayer(settings.Layer);
        var body = Simulation.Bodies.Add(description);
        _kinds.Set(
            body,
            BodyKind.Loose,
            marks: settings.Marks,
            material: settings.Material,
            layer: settings.Layer,
            group: settings.Group
        );
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
        if (_kinds[body.Value] == BodyKind.Trigger)
        {
            RemoveTrigger(new TriggerId(body, _kinds.Born(body.Value)));
            return;
        }

        _triggers?.ForgetBody(body, _kinds.Born(body.Value), _kinds);
        _contacts?.Forget(PhysicsContacts.BodySide(body), _kinds);
        BodyRemoving?.Invoke(body);
        Simulation.Bodies.Remove(body);
        _kinds.Forget(body);
    }

    /// <summary>
    /// Adds a game's own static, which bodies of every voxel level touch (as a mesh collider), of the material
    /// <paramref name="settings"/> name. Remove it with <see cref="RemoveStatic"/>. Its shape stays the caller's. Main
    /// thread, between steps.
    /// </summary>
    public StaticHandle AddStatic(in StaticDescription description, in PhysicsBodySettings settings = default)
    {
        CheckMaterial(settings.Material);
        CheckLayer(settings.Layer);
        var handle = Simulation.Statics.Add(description);
        _kinds.SetStatic(handle, BodyKinds.AnyLevel, settings.Material, settings.Layer, settings.Group, settings.Marks);
        return handle;
    }

    /// <summary>Sets a body's marks (<see cref="PhysicsMarks"/>), from the next step on. Main thread, between steps.</summary>
    public void SetMarks(BodyHandle body, PhysicsMarks marks) => _kinds.SetMarks(body, marks);

    /// <summary>Sets a static's marks (<see cref="PhysicsMarks"/>), from the next step on. Main thread, between steps.</summary>
    public void SetMarks(StaticHandle handle, PhysicsMarks marks) => _kinds.SetMarks(handle, marks);

    /// <summary>A static's marks.</summary>
    public PhysicsMarks MarksOf(StaticHandle handle) => _kinds.Static(handle.Value).Marks;

    /// <summary>
    /// Puts <paramref name="body"/> in <paramref name="layer"/> and <paramref name="group"/> (0 for none), from the next
    /// step on, and wakes it. Main thread, between steps.
    /// </summary>
    public void SetLayer(BodyHandle body, PhysicsLayerId layer, int group = 0)
    {
        CheckLayer(layer);
        _kinds.SetLayer(body, layer, group);
        var reference = Simulation.Bodies[body];
        reference.Awake = true;
    }

    /// <summary>
    /// Puts a static in <paramref name="layer"/> and <paramref name="group"/> (0 for none), from the next step on, and
    /// wakes every sleeping body (some may rest on it). Main thread, between steps.
    /// </summary>
    public void SetLayer(StaticHandle handle, PhysicsLayerId layer, int group = 0)
    {
        CheckLayer(layer);
        _kinds.SetLayer(handle, layer, group);
        WakeAll();
    }

    /// <summary>A body's layer.</summary>
    public PhysicsLayerId LayerOf(BodyHandle body) => _kinds.Body(body.Value).Layer;

    /// <summary>A static's layer.</summary>
    public PhysicsLayerId LayerOf(StaticHandle handle) => _kinds.Static(handle.Value).Layer;

    /// <summary>A body's group (0 for none).</summary>
    public int GroupOf(BodyHandle body) => _kinds.Body(body.Value).Group;

    /// <summary>A static's group (0 for none).</summary>
    public int GroupOf(StaticHandle handle) => _kinds.Static(handle.Value).Group;

    /// <summary>
    /// Sets whether layers <paramref name="a"/> and <paramref name="b"/> touch in this match (either way round), from the
    /// next step on, and wakes every sleeping body, so none rests on what it no longer touches. The run's table
    /// (<see cref="PhysicsLayers.SetTouches"/>) is where each match starts. Main thread, between steps.
    /// </summary>
    public void SetTouches(PhysicsLayerId a, PhysicsLayerId b, bool touch)
    {
        CheckLayer(a);
        CheckLayer(b);
        PhysicsLayers.Set(_kinds.Touches, a, b, touch);
        WakeAll();
    }

    /// <summary>True when layers <paramref name="a"/> and <paramref name="b"/> touch in this match.</summary>
    public bool Touches(PhysicsLayerId a, PhysicsLayerId b) => PhysicsLayers.Touch(_kinds.Touches, a, b);

    /// <summary>The layer the characters' stand-ins are in this match (<see cref="PhysicsLayers.CharacterLayer"/>).</summary>
    public PhysicsLayerId CharacterLayer => _kinds.CharacterLayer;

    /// <summary>The layer the voxels' statics are in this match (<see cref="PhysicsLayers.VoxelLayer"/>).</summary>
    public PhysicsLayerId VoxelLayer => _kinds.VoxelLayer;

    /// <summary>The run's collision layers, if it has a registry (a test's world may have none).</summary>
    internal PhysicsLayers? Layers => _layers;

    /// <summary>True when the body's layer touches the characters' (the mover reads it, and stand-ins are made for it).</summary>
    internal bool TouchesCharacters(BodyHandle body) => _kinds.TouchesCharacters(body.Value);

    /// <summary>Throws for a layer the run did not register.</summary>
    internal void CheckLayer(PhysicsLayerId layer)
    {
        if (layer.Value >= (_layers?.Count ?? 1))
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer.Value, "No such physics layer.");
        }
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
        _contacts?.Forget(PhysicsContacts.StaticSide(handle), _kinds);
        Simulation.Statics.Remove(handle);
        _kinds.ClearStatic(handle);
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
