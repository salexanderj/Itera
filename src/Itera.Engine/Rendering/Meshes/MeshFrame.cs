namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// What <see cref="WorldMeshes"/> wrote into a frame slot of <see cref="MeshPass"/> for one frame: the
/// <see cref="GpuData.MeshDraw"/> records (each block drawn in any list has one), the commands of the camera's list and
/// of the near and far shadow cascades' lists, whether the cascades are drawn (the view's shadows are on), the
/// models' three lists, and whether a new sky field waits in the slot's staging region at <c>SkyUploadOffset</c>
/// (<see cref="SkyField"/>), to be copied into the field's image before anything reads it.
/// </summary>
public readonly record struct MeshFrame(
    int Records,
    int Draws,
    int NearShadows,
    int FarShadows,
    bool Shadows,
    MeshModelList ModelCamera = default,
    MeshModelList ModelNear = default,
    MeshModelList ModelFar = default,
    bool SkyUploaded = false,
    ulong SkyUploadOffset = 0
)
{
    /// <summary>The models' list <paramref name="list"/>: 0 the camera's, 1 the near cascade's, 2 the far one's.</summary>
    public MeshModelList ModelList(int list) =>
        list switch
        {
            0 => ModelCamera,
            1 => ModelNear,
            _ => ModelFar,
        };
}

/// <summary>
/// One list of the models' draws in a frame slot: its <see cref="GpuData.MeshModelDraw"/> records and its commands, one
/// per block of a model level, whose instances are that block's records.
/// </summary>
public readonly record struct MeshModelList(int Records, int Commands);
