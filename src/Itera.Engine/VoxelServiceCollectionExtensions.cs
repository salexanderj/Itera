using Itera.Engine.Loading;
using Itera.Engine.Navigation;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Itera.Engine;

/// <summary>Registers the voxel terrain, for a game that has one.</summary>
public static class VoxelServiceCollectionExtensions
{
    /// <summary>
    /// Adds the voxel terrain to the engine (<see cref="EngineServiceCollectionExtensions.AddIteraEngine"/>), for a game
    /// that registers an <see cref="IChunkGenerator"/>: each match's <see cref="VoxelWorld"/>, the colliders loose bodies
    /// meet it through (<see cref="VoxelColliders"/>), navigation over it, the play area round its grid, the benchmark's
    /// view of it, and its loading tasks (World, Navigation, and Warm-up, after the engine's Sounds and before the
    /// game's own, so a game calls this before it adds those). A second call adds nothing.
    /// </summary>
    public static IServiceCollection AddIteraVoxels(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(VoxelWorld)))
        {
            return services;
        }

        services.TryAddScoped<VoxelWorld>();
        services.TryAddScoped<VoxelColliders>();
        services.TryAddScoped<NavigationService>();
        services.TryAddScoped(sp => new PlayArea(sp.GetRequiredService<VoxelWorld>().Grid));
        services.TryAddScoped<IBenchmarkHost, EngineBenchmarkHost>();
        services.AddScoped<ILoadingTask, WorldLoadingTask>();
        services.AddScoped<ILoadingTask, NavigationLoadingTask>();
        services.AddScoped<ILoadingTask, EditWarmUpLoadingTask>();
        return services;
    }
}
