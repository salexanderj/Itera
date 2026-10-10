using Itera.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.MeshSample.Tests;

/// <summary>The sample's real service collection, as its <c>Program.cs</c> composes it.</summary>
internal static class SampleComposition
{
    /// <summary>
    /// The sample's services, from its shipped settings and <paramref name="options"/> over them, with no log file and
    /// every registration checked.
    /// </summary>
    public static ServiceProvider Build(IReadOnlyDictionary<string, string?>? options = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:Logging:FileEnabled"] = "false" })
            .AddInMemoryCollection(options ?? new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddIteraEngine(configuration);
        services.AddMeshSample();
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
