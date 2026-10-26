using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Grass;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine;

/// <summary>Registers the engine's grass, for a game that grows it.</summary>
public static class GrassServiceCollectionExtensions
{
    /// <summary>
    /// Adds blades of grass on the voxel terrain the camera draws: the game's <typeparamref name="TSource"/>, which says
    /// what to grow each frame, then <see cref="GrassPlacePass"/> (the <see cref="RenderStage.Compute"/> stage) and
    /// <see cref="GrassDrawPass"/> (the <see cref="RenderStage.Opaque"/> stage), in that order, since the draw pass takes
    /// the place pass's buffers. The two count among a game's render passes (<c>GpuTimings.MostGamePasses</c>). A game
    /// without terrain, or that does not call this, has no grass. A second call adds nothing.
    /// </summary>
    public static IServiceCollection AddIteraGrass<TSource>(this IServiceCollection services)
        where TSource : class, IGrassSource
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(GrassPlacePass)))
        {
            return services;
        }

        services.AddSingleton<IGrassSource, TSource>();
        services.AddSingleton<GrassPlacePass>();
        services.AddSingleton<IRenderPass>(sp => sp.GetRequiredService<GrassPlacePass>());
        services.AddSingleton<IRenderPass, GrassDrawPass>();
        return services;
    }
}
