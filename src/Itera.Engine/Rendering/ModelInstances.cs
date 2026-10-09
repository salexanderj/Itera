using System.Numerics;

namespace Itera.Engine.Rendering;

/// <summary>
/// The models to draw in the frame being built (<see cref="Itera.Engine.Voxels.VoxelModels"/>): frame systems
/// <see cref="Add"/> them, <c>WorldMeshes.Upload</c> turns them into draws after the frame group, and
/// <c>GameLoop</c> clears the list before the next frame's systems run. Main thread; allocates nothing.
/// </summary>
public sealed class ModelInstances
{
    /// <summary>Models a frame can draw; more are dropped (<see cref="Dropped"/>).</summary>
    public const int MaxModels = 32_768;

    private readonly ModelDraw[] _draws = new ModelDraw[MaxModels];

    /// <summary>Models added this frame, up to <see cref="MaxModels"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Models added this frame past <see cref="MaxModels"/>, which are not drawn.</summary>
    public int Dropped { get; private set; }

    /// <summary>The models added this frame.</summary>
    public ReadOnlySpan<ModelDraw> Draws => _draws.AsSpan(0, Count);

    /// <summary>
    /// Draws level <paramref name="level"/> of model <paramref name="model"/> (or its finest level when it has fewer)
    /// with the corner of its box, its tree's voxel (0, 0, 0), at <paramref name="corner"/> in world metres, turned by
    /// <paramref name="turn"/> from the model's axes to the world's, blended <paramref name="tint"/> of the way from
    /// its materials' base to their tint colours; <paramref name="shadow"/> says whether it is drawn and casts the sun's
    /// shadow.
    /// </summary>
    public void Add(
        int model,
        int level,
        Vector3 corner,
        Quaternion turn,
        float tint,
        ModelShadow shadow = ModelShadow.Casts
    )
    {
        if (Count == MaxModels)
        {
            Dropped++;
            return;
        }

        _draws[Count++] = new ModelDraw(model, level, corner, turn, tint, shadow);
    }

    /// <summary>Empties the list for the next frame.</summary>
    public void Clear()
    {
        Count = 0;
        Dropped = 0;
    }
}

/// <summary>A model to draw (<see cref="ModelInstances.Add"/>).</summary>
/// <param name="Model">The model's handle in <see cref="Itera.Engine.Voxels.VoxelModels"/>.</param>
/// <param name="Level">The level of detail to draw, clamped to the model's.</param>
/// <param name="Corner">The corner of the model's box in world metres.</param>
/// <param name="Turn">The turn from the model's axes to the world's.</param>
/// <param name="Tint">How far its materials are blended from their base to their tint colour, 0 to 1.</param>
/// <param name="Shadow">Whether it is drawn and whether it casts the sun's shadow.</param>
public readonly record struct ModelDraw(
    int Model,
    int Level,
    Vector3 Corner,
    Quaternion Turn,
    float Tint,
    ModelShadow Shadow = ModelShadow.Casts
);

/// <summary>How a model meets the camera and the sun's shadow maps (<see cref="ModelInstances.Add"/>).</summary>
public enum ModelShadow
{
    /// <summary>Drawn, and casts the sun's shadow.</summary>
    Casts,

    /// <summary>Drawn, and casts no shadow: the first-person arms and what they hold.</summary>
    None,

    /// <summary>Not drawn, and casts the sun's shadow: the local player's own body in first person.</summary>
    Only,
}
