using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// The engine's real service collection with the least a game must add: a world generator (<see cref="FloorGenerator"/>)
/// and one fixed and one frame system (<see cref="StepCounter"/>, <see cref="FrameCounter"/>), for tests of the
/// composition and of matches, headless. Every registration is checked as a game's <c>Program.cs</c> checks it.
/// </summary>
internal static class EngineComposition
{
    /// <summary>A small world round the centre, no Steam, no sound, and no log file.</summary>
    public static readonly IReadOnlyDictionary<string, string?> Quiet = new Dictionary<string, string?>
    {
        ["Engine:Voxels:WorkerThreads"] = "2",
        ["Engine:Voxels:StreamingRadiusMetres"] = "3",
        ["Engine:Voxels:Levels"] = "1",
        ["Engine:Steam:Enabled"] = "false",
        ["Engine:Audio:Enabled"] = "false",
        // Composing the engine with a log file prunes the developer's real log folder.
        ["Engine:Logging:FileEnabled"] = "false",
    };

    /// <summary>
    /// The engine's services and the test game's, from <see cref="Quiet"/> and <paramref name="options"/> over it, with
    /// its floor of voxel terrain unless <paramref name="terrain"/> is false. <paramref name="configure"/> may replace
    /// registrations after the game's.
    /// </summary>
    public static ServiceProvider Build(
        IReadOnlyDictionary<string, string?>? options = null,
        Action<IServiceCollection>? configure = null,
        bool terrain = true
    )
    {
        var services = Services(options, terrain);
        configure?.Invoke(services);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    /// <summary>The service collection <see cref="Build"/> builds, before it is built.</summary>
    public static ServiceCollection Services(IReadOnlyDictionary<string, string?>? options = null, bool terrain = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Quiet)
            .AddInMemoryCollection(options ?? new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddIteraEngine(configuration);
        if (terrain)
        {
            services.AddSingleton<IChunkGenerator, FloorGenerator>();
            services.AddIteraVoxels();
        }

        services.AddScoped<IFixedUpdateSystem>(sp => new StepCounter(sp.GetRequiredService<EcsWorld>().World));
        services.AddScoped<IFrameUpdateSystem>(sp => new FrameCounter(sp.GetRequiredService<EcsWorld>().World));
        // The host adds ILogger<T>; a null factory keeps Serilog from writing a log file.
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return services;
    }

    /// <summary>Stone below y = 0 and air above, on 10 cm voxels.</summary>
    internal sealed class FloorGenerator : IChunkGenerator
    {
        public const byte Stone = 3;

        public VoxelGrid Grid { get; } = new(10);

        public string Id => "test.floor";

        public int Version => 1;

        public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
        {
            for (var layer = 0; layer < layers.Length; layer++)
            {
                layers[layer] = column.Chunk(layer, Grid).Y < 0 ? ChunkFill.Uniform(Stone) : ChunkFill.Air;
            }
        }

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) =>
            voxels[..VoxelConstants.ChunkVoxels].Fill(chunk.Y < 0 ? Stone : VoxelConstants.Air);
    }

    /// <summary>A fixed system that makes one entity at its start and counts the steps.</summary>
    internal sealed class StepCounter(World world) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
    {
        public int Steps { get; private set; }

        public override void Initialize() => World.Create(new Counted());

        public override void Update(in FixedStepTime step) => Steps++;
    }

    /// <summary>A frame system that counts the frames.</summary>
    internal sealed class FrameCounter(World world) : BaseSystem<World, FrameTime>(world), IFrameUpdateSystem
    {
        public int Frames { get; private set; }

        public override void Update(in FrameTime frame) => Frames++;
    }

    /// <summary>The entity <see cref="StepCounter"/> makes.</summary>
    internal struct Counted;
}
