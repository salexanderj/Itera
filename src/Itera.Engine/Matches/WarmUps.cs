namespace Itera.Engine.Matches;

/// <summary>
/// What this run has warmed up already, so a later match does not do it again: the voxel edit code, compiled by the
/// edit warm-up (<see cref="Voxels.VoxelWorld.StartEditWarmUp"/>; a second warm-up of code already optimised would
/// gain nothing and hold the loading screen until the process stopped compiling), Bepu's code for the engine's bodies
/// and joints (<see cref="Physics.PhysicsWorld.StartWarmUp"/>), and the game's cutting code. Run-wide.
/// </summary>
public sealed class WarmUps
{
    private volatile bool _edits;
    private volatile bool _physics;
    private volatile bool _cuts;

    /// <summary>True once an edit warm-up ran to its end (optimised code, or its time limit). Set by the warm-up's thread.</summary>
    public bool Edits
    {
        get => _edits;
        set => _edits = value;
    }

    /// <summary>True once a physics warm-up ran to its end. Set by the warm-up's thread.</summary>
    public bool Physics
    {
        get => _physics;
        set => _physics = value;
    }

    /// <summary>True once the game's cut warm-up has run.</summary>
    public bool Cuts
    {
        get => _cuts;
        set => _cuts = value;
    }
}
