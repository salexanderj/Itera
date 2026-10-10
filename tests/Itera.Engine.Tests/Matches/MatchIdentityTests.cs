using Itera.Engine.Loading;
using Itera.Engine.Matches;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// What a match is to a joining game (<see cref="IMatchIdentity"/>): the voxel world's generator's id and version when the
/// game registers one, the application's name at version 1 when it does not, and a game's own when it registers one;
/// and the voxel terrain's registration (<c>AddIteraVoxels</c>), which a second call does not repeat.
/// </summary>
public sealed class MatchIdentityTests
{
    [Fact]
    public void With_a_generator_the_match_is_the_generators()
    {
        using var provider = EngineComposition.Build();

        var identity = provider.GetRequiredService<IMatchIdentity>();

        var generator = provider.GetRequiredService<IChunkGenerator>();
        Assert.IsType<GeneratorMatchIdentity>(identity);
        Assert.Equal((generator.Id, generator.Version), (identity.Id, identity.Version));
    }

    [Fact]
    public void Without_one_the_match_is_the_applications_name_at_version_one()
    {
        using var provider = Core(new Dictionary<string, string?> { ["Engine:Application:Name"] = "Mesh Game" })
            .BuildServiceProvider();

        var identity = provider.GetRequiredService<IMatchIdentity>();

        Assert.Equal(("Mesh Game", 1), (identity.Id, identity.Version));
    }

    [Fact]
    public void A_games_own_identity_wins()
    {
        var services = Core([]);
        services.AddSingleton<IMatchIdentity>(new OwnIdentity());
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            ("own", 7),
            (provider.GetRequiredService<IMatchIdentity>().Id, provider.GetRequiredService<IMatchIdentity>().Version)
        );
    }

    [Fact]
    public void A_second_call_adds_the_terrain_once()
    {
        var services = EngineComposition.Services();
        var before = services.Count;

        services.AddIteraVoxels();

        Assert.Equal(before, services.Count);
        Assert.Single(
            services,
            d => d.ServiceType == typeof(ILoadingTask) && d.ImplementationType == typeof(WorldLoadingTask)
        );
    }

    /// <summary>The engine alone, with no generator and no terrain.</summary>
    private static ServiceCollection Core(Dictionary<string, string?> options)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(options).Build();
        var services = new ServiceCollection();
        services.AddIteraEngine(configuration);
        return services;
    }

    private sealed class OwnIdentity : IMatchIdentity
    {
        public string Id => "own";

        public int Version => 7;
    }
}
