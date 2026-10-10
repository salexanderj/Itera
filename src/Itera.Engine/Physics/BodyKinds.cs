using BepuPhysics;

namespace Itera.Engine.Physics;

/// <summary>What a Bepu body stands for, which decides what it touches (<see cref="NarrowPhaseCallbacks"/>).</summary>
public enum BodyKind : byte
{
    /// <summary>A lifted piece's loose body (a thrown ball): touches the voxels, pieces, and stand-ins.</summary>
    Loose = 0,

    /// <summary>A character's stand-in: touches loose bodies of lifted pieces only.</summary>
    StandIn = 1,

    /// <summary>
    /// A body cut off a character (a severed piece, a body of a ragdoll, or a client's mirror of one): touches the
    /// voxels only, not other bodies or stand-ins.
    /// </summary>
    Cut = 2,
}

/// <summary>
/// The <see cref="BodyKind"/> of each body, by body handle, the voxel level it collides with now
/// (<see cref="VoxelColliders"/>) and the one it was made for (its base level), and the ragdoll it belongs to; and the
/// voxel level of each static, by static handle. A body touches only the statics of its own level. Set on the main thread
/// between steps; read by Bepu's worker threads during a step. Bodies never set are <see cref="BodyKind.Loose"/>, at
/// level 0, and in no ragdoll; statics never set are at level 0. A game's static (a mesh collider,
/// <see cref="SetGameStatic"/>) is at <see cref="AnyLevel"/>: every body that touches statics touches it.
/// </summary>
internal sealed class BodyKinds
{
    private BodyKind[] _kinds = new BodyKind[256];
    private byte[] _levels = new byte[256];
    private byte[] _bases = new byte[256];
    private int[] _groups = new int[256];
    private byte[] _staticLevels = new byte[256];

    /// <summary>The kind of the body with handle value <paramref name="handle"/>.</summary>
    public BodyKind this[int handle] => (uint)handle < (uint)_kinds.Length ? _kinds[handle] : BodyKind.Loose;

    /// <summary>The voxel level the body with handle value <paramref name="handle"/> collides with now.</summary>
    public int Level(int handle) => (uint)handle < (uint)_levels.Length ? _levels[handle] : 0;

    /// <summary>
    /// The voxel level the body with handle value <paramref name="handle"/> was made to collide with, which it goes back
    /// to where that level is resident (<see cref="VoxelColliders"/>).
    /// </summary>
    public int BaseLevel(int handle) => (uint)handle < (uint)_bases.Length ? _bases[handle] : 0;

    /// <summary>The ragdoll the body with handle value <paramref name="handle"/> belongs to; 0 for none.</summary>
    public int Group(int handle) => (uint)handle < (uint)_groups.Length ? _groups[handle] : 0;

    /// <summary>The level of a game's static, which bodies of every level touch.</summary>
    public const int AnyLevel = byte.MaxValue;

    /// <summary>The voxel level of the static with handle value <paramref name="handle"/>.</summary>
    public int StaticLevel(int handle) => (uint)handle < (uint)_staticLevels.Length ? _staticLevels[handle] : 0;

    /// <summary>
    /// Records a body's kind, the voxel level it collides with (its base level too), and the ragdoll it belongs to (0
    /// for none). Main thread, between steps.
    /// </summary>
    public void Set(BodyHandle body, BodyKind kind, int level = 0, int group = 0)
    {
        if (body.Value >= _kinds.Length)
        {
            var length = Math.Max(_kinds.Length * 2, body.Value + 1);
            Array.Resize(ref _kinds, length);
            Array.Resize(ref _levels, length);
            Array.Resize(ref _bases, length);
            Array.Resize(ref _groups, length);
        }

        _kinds[body.Value] = kind;
        _levels[body.Value] = (byte)level;
        _bases[body.Value] = (byte)level;
        _groups[body.Value] = group;
    }

    /// <summary>Sets the voxel level a body collides with now, its base level kept. Main thread, between steps.</summary>
    public void SetLevel(BodyHandle body, int level)
    {
        if ((uint)body.Value < (uint)_levels.Length)
        {
            _levels[body.Value] = (byte)level;
        }
    }

    /// <summary>Records a game's static, which bodies of every level touch. Main thread, between steps.</summary>
    public void SetGameStatic(StaticHandle handle) => SetStatic(handle, AnyLevel);

    /// <summary>Forgets a removed static, so a static that takes its handle next starts at level 0. Main thread.</summary>
    public void ClearStatic(StaticHandle handle)
    {
        if ((uint)handle.Value < (uint)_staticLevels.Length)
        {
            _staticLevels[handle.Value] = 0;
        }
    }

    /// <summary>Records the voxel level of a static. Main thread, between steps.</summary>
    public void SetStatic(StaticHandle handle, int level)
    {
        if (handle.Value >= _staticLevels.Length)
        {
            Array.Resize(ref _staticLevels, Math.Max(_staticLevels.Length * 2, handle.Value + 1));
        }

        _staticLevels[handle.Value] = (byte)level;
    }
}
