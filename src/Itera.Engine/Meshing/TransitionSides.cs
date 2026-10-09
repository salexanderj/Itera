namespace Itera.Engine.Meshing;

/// <summary>
/// Which columns round a block's column, past its x and z faces and corners, are drawn one level coarser than it, for a
/// transition mesh (<see cref="SurfaceNets.MeshTransition"/>): only those past a face of the block that is its column's
/// boundary.
/// </summary>
[Flags]
public enum TransitionSides
{
    None = 0,
    LowX = 1,
    HighX = 2,
    LowZ = 4,
    HighZ = 8,
    LowXLowZ = 16,
    HighXLowZ = 32,
    LowXHighZ = 64,
    HighXHighZ = 128,
}
