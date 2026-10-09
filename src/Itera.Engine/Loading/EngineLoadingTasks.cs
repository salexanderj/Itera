using Itera.Engine.Audio;
using Itera.Engine.Navigation;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;

namespace Itera.Engine.Loading;

/// <summary>
/// The one-shot sounds rendered into buffers, or read from the sound cache (<see cref="AudioService.AllRendered"/>). Done
/// at once with no audio thread.
/// </summary>
public sealed class SoundsLoadingTask(AudioService audio) : ILoadingTask
{
    public string Name => "Sounds";

    public LoadingProgress Progress
    {
        get
        {
            var total = audio.Recipes.OneShotCount;
            var fraction = total == 0 ? 0 : audio.Stats.RenderedRecipes / (float)total;
            return LoadingProgress.Part(audio.AllRendered, fraction);
        }
    }
}

/// <summary>
/// Every column the streaming centres want, at every level, resident and on the GPU (but those parked because a column
/// nearer the view holds their window slot): the whole view around the player. The GPU part counts only once an upload
/// has run after the world was complete, since an upload earlier in the iteration has not seen the columns installed
/// since.
/// </summary>
public sealed class WorldLoadingTask(VoxelWorld world, IWorldOnGpu uploader) : ILoadingTask
{
    private long _residentAtUpload = -1;

    public string Name => "World";

    public LoadingProgress Progress
    {
        get
        {
            if (!world.WantedColumnsResident)
            {
                _residentAtUpload = -1;
                return LoadingProgress.Part(false, world.WantedFraction);
            }

            if (_residentAtUpload < 0)
            {
                _residentAtUpload = uploader.Uploads;
            }

            return LoadingProgress.Part(uploader.Uploads > _residentAtUpload && uploader.IsUpToDate, 0.99f);
        }
    }
}

/// <summary>
/// The navigation grid and every flow field built and taken by the main thread, and after a reset of the world built
/// again from the layout. Done at once where there is no grid (a session client) and when the build failed (logged at
/// Error).
/// </summary>
public sealed class NavigationLoadingTask(NavigationService navigation) : ILoadingTask
{
    public string Name => "Navigation";

    public LoadingProgress Progress =>
        LoadingProgress.When((navigation.Ready && !navigation.Refilling) || !navigation.Enabled || navigation.Failed);
}

/// <summary>
/// The edit warm-up (<see cref="VoxelWorld.StartEditWarmUp"/>): the edit code compiled and optimised, so the first large
/// dig does not stall. It stops by itself after at most 10 s.
/// </summary>
public sealed class EditWarmUpLoadingTask(VoxelWorld world) : ILoadingTask
{
    public string Name => "Warm-up";

    public LoadingProgress Progress => LoadingProgress.When(world.EditWarmUpDone);
}
