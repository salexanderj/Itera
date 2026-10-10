using System.Numerics;
using Itera.Engine.Rendering;

namespace Itera.Engine.Components;

/// <summary>
/// Where a mesh asset is drawn (<see cref="DrawnMesh"/>): its pose at the last two fixed steps, which the frame blends by
/// its <c>Alpha</c> (<c>MeshDrawSystem</c>), and its uniform scale. A fixed system that moves it copies
/// <see cref="Position"/> and <see cref="Orientation"/> into the previous ones first; a placement that never moves keeps
/// them equal (<see cref="At"/>).
/// </summary>
public struct MeshPlacement
{
    public Vector3 Position;
    public Vector3 PreviousPosition;
    public Quaternion Orientation;
    public Quaternion PreviousOrientation;
    public float Scale;

    /// <summary>A placement that has not moved: at <paramref name="position"/>, turned by <paramref name="orientation"/>.</summary>
    public static MeshPlacement At(Vector3 position, Quaternion orientation, float scale = 1) =>
        new()
        {
            Position = position,
            PreviousPosition = position,
            Orientation = orientation,
            PreviousOrientation = orientation,
            Scale = scale,
        };
}

/// <summary>
/// The mesh asset an entity draws where its <see cref="MeshPlacement"/> is: the asset's index in <c>MeshAssets</c> (from
/// its <c>Find</c>, looked up once), a tint its colours are multiplied by (linear RGBA bytes, red in the low byte;
/// <see cref="MeshInstances.NoTint"/> for none), and how it meets the camera and the shadows.
/// </summary>
public struct DrawnMesh
{
    public int Asset;
    public uint Tint;
    public ModelShadow Shadow;

    public static DrawnMesh Of(int asset, uint tint = MeshInstances.NoTint, ModelShadow shadow = ModelShadow.Casts) =>
        new()
        {
            Asset = asset,
            Tint = tint,
            Shadow = shadow,
        };
}

/// <summary>A mesh the console's <c>meshes place</c> or <c>meshes test</c> made, which <c>meshes clear</c> removes.</summary>
public struct ConsoleMesh;
