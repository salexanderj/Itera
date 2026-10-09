namespace Itera.Engine.Matches;

/// <summary>
/// What this run has warmed up already, so a later match does not do it again: the voxel edit code, compiled by the
/// edit warm-up (<see cref="Voxels.VoxelWorld.StartEditWarmUp"/>; a second warm-up of code already optimised would
/// never see its rounds speed up and would run its whole 10 s), and the game's cutting code. Run-wide.
/// </summary>
public sealed class WarmUps
{
    private volatile bool _edits;
    private volatile bool _cuts;

    /// <summary>True once an edit warm-up ran to its end (optimised code, or its time limit). Set by the warm-up's thread.</summary>
    public bool Edits
    {
        get => _edits;
        set => _edits = value;
    }

    /// <summary>True once the game's cut warm-up has run.</summary>
    public bool Cuts
    {
        get => _cuts;
        set => _cuts = value;
    }
}
