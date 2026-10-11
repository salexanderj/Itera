using BepuPhysics;

namespace Itera.Engine.Physics;

/// <summary>What a Bepu body stands for, which decides what it touches (<see cref="NarrowPhaseCallbacks"/>).</summary>
public enum BodyKind : byte
{
    /// <summary>A lifted piece's loose body (a thrown ball), or a game's own body: touches the voxels, bodies, and stand-ins.</summary>
    Loose = 0,

    /// <summary>A character's stand-in: touches loose bodies of lifted pieces only.</summary>
    StandIn = 1,

    /// <summary>
    /// A body cut off a character (a severed piece, a body of a ragdoll, or a client's mirror of one): touches the
    /// voxels only, not other bodies or stand-ins.
    /// </summary>
    Cut = 2,
}

/// <summary>What the engine keeps for one body, by its handle.</summary>
internal struct BodyRecord
{
    /// <summary>What the body stands for.</summary>
    public BodyKind Kind;

    /// <summary>The voxel level it collides with now (<see cref="VoxelColliders"/>).</summary>
    public byte Level;

    /// <summary>The voxel level it was made for, which it goes back to where that level is resident.</summary>
    public byte BaseLevel;

    /// <summary>A game's marks for it.</summary>
    public PhysicsMarks Marks;

    /// <summary>The ragdoll it belongs to; 0 for none.</summary>
    public int Ragdoll;

    /// <summary>Which body this is of all that took the handle: 0 for none recorded, else a count over the match.</summary>
    public long Born;
}

/// <summary>What the engine keeps for one static, by its handle.</summary>
internal struct StaticRecord
{
    /// <summary>The voxel level it is of; <see cref="BodyKinds.AnyLevel"/> for a game's, which every level touches.</summary>
    public byte Level;

    /// <summary>Which static this is of all that took the handle: 0 for none recorded, else a count over the match.</summary>
    public long Born;
}

/// <summary>
/// What the engine keeps for each Bepu body and static, by handle (<see cref="BodyRecord"/>, <see cref="StaticRecord"/>):
/// its <see cref="BodyKind"/>, the voxel level it collides with now (<see cref="VoxelColliders"/>) and the one it was
/// made for (its base level), the ragdoll it belongs to, a game's marks, and which of the bodies that took the handle it
/// is (<see cref="BodyRecord.Born"/>); a static's voxel level. A body touches only the statics of its own level. Set on the
/// main thread between steps; read by Bepu's worker threads during a step. A handle never recorded reads the default:
/// <see cref="BodyKind.Loose"/>, level 0, in no ragdoll; a static at level 0. A body or static removed through
/// <see cref="PhysicsWorld"/> (or the engine's own removals) has its record reset (<see cref="Forget"/>,
/// <see cref="ClearStatic"/>), so one that takes its handle next starts from the default, not from what was there. A
/// game's static (a mesh collider, <see cref="SetGameStatic"/>) is at <see cref="AnyLevel"/>.
/// </summary>
internal sealed class BodyKinds
{
    /// <summary>The level of a game's static, which bodies of every level touch.</summary>
    public const int AnyLevel = byte.MaxValue;

    private static readonly BodyRecord s_defaultBody;
    private static readonly StaticRecord s_defaultStatic;

    private BodyRecord[] _bodies = new BodyRecord[256];
    private StaticRecord[] _statics = new StaticRecord[256];
    private int _highestBody = -1;
    private long _born;

    /// <summary>The record of the body with handle value <paramref name="handle"/>; the default when none is kept.</summary>
    public ref readonly BodyRecord Body(int handle) =>
        ref (uint)handle < (uint)_bodies.Length ? ref _bodies[handle] : ref s_defaultBody;

    /// <summary>The record of the static with handle value <paramref name="handle"/>; the default when none is kept.</summary>
    public ref readonly StaticRecord Static(int handle) =>
        ref (uint)handle < (uint)_statics.Length ? ref _statics[handle] : ref s_defaultStatic;

    /// <summary>The kind of the body with handle value <paramref name="handle"/>.</summary>
    public BodyKind this[int handle] => Body(handle).Kind;

    /// <summary>The voxel level the body with handle value <paramref name="handle"/> collides with now.</summary>
    public int Level(int handle) => Body(handle).Level;

    /// <summary>
    /// The voxel level the body with handle value <paramref name="handle"/> was made to collide with, which it goes back
    /// to where that level is resident (<see cref="VoxelColliders"/>).
    /// </summary>
    public int BaseLevel(int handle) => Body(handle).BaseLevel;

    /// <summary>The ragdoll the body with handle value <paramref name="handle"/> belongs to; 0 for none.</summary>
    public int Ragdoll(int handle) => Body(handle).Ragdoll;

    /// <summary>A game's marks for the body with handle value <paramref name="handle"/>.</summary>
    public PhysicsMarks Marks(int handle) => Body(handle).Marks;

    /// <summary>Which body this is of all that took the handle (0 for none recorded).</summary>
    public long Born(int handle) => Body(handle).Born;

    /// <summary>The voxel level of the static with handle value <paramref name="handle"/>.</summary>
    public int StaticLevel(int handle) => Static(handle).Level;

    /// <summary>
    /// Records a body anew: its kind, the voxel level it collides with (its base level too), the ragdoll it belongs to (0
    /// for none), and a game's marks; the rest from the default. Main thread, between steps.
    /// </summary>
    public void Set(
        BodyHandle body,
        BodyKind kind,
        int level = 0,
        int ragdoll = 0,
        PhysicsMarks marks = PhysicsMarks.None
    )
    {
        ref var record = ref BodySlot(body.Value);
        record = new BodyRecord
        {
            Kind = kind,
            Level = (byte)level,
            BaseLevel = (byte)level,
            Ragdoll = ragdoll,
            Marks = marks,
            Born = ++_born,
        };
    }

    /// <summary>Sets the voxel level a body collides with now, its base level kept. Main thread, between steps.</summary>
    public void SetLevel(BodyHandle body, int level)
    {
        if ((uint)body.Value < (uint)_bodies.Length)
        {
            _bodies[body.Value].Level = (byte)level;
        }
    }

    /// <summary>Forgets a removed body: a body that takes its handle next starts from the default. Main thread.</summary>
    public void Forget(BodyHandle body)
    {
        if ((uint)body.Value < (uint)_bodies.Length)
        {
            _bodies[body.Value] = default;
        }
    }

    /// <summary>Records a game's static, which bodies of every level touch. Main thread, between steps.</summary>
    public void SetGameStatic(StaticHandle handle) => SetStatic(handle, AnyLevel);

    /// <summary>Forgets a removed static, so a static that takes its handle next starts at level 0. Main thread.</summary>
    public void ClearStatic(StaticHandle handle)
    {
        if ((uint)handle.Value < (uint)_statics.Length)
        {
            _statics[handle.Value] = default;
        }
    }

    /// <summary>Records the voxel level of a static anew. Main thread, between steps.</summary>
    public void SetStatic(StaticHandle handle, int level)
    {
        if (handle.Value >= _statics.Length)
        {
            Array.Resize(ref _statics, Math.Max(_statics.Length * 2, handle.Value + 1));
        }

        _statics[handle.Value] = new StaticRecord { Level = (byte)level, Born = ++_born };
    }

    /// <summary>
    /// The handle of a body recorded and not forgotten (<see cref="BodyRecord.Born"/> not 0) that is not in
    /// <paramref name="bodies"/>, or -1: a body removed without its record being forgotten. A check after each step in
    /// Debug builds.
    /// </summary>
    public int FirstStale(Bodies bodies)
    {
        for (var handle = 0; handle <= _highestBody; handle++)
        {
            if (_bodies[handle].Born != 0 && !bodies.BodyExists(new BodyHandle(handle)))
            {
                return handle;
            }
        }

        return -1;
    }

    private ref BodyRecord BodySlot(int handle)
    {
        if (handle >= _bodies.Length)
        {
            Array.Resize(ref _bodies, Math.Max(_bodies.Length * 2, handle + 1));
        }

        _highestBody = Math.Max(_highestBody, handle);
        return ref _bodies[handle];
    }
}
