using Game.Systems;
using Game.WorldGeneration;
using Itera.Engine;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Settings;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Game;

/// <summary>
/// The game on top of the engine (<c>AddIteraEngine</c>): its options, its world, its run-wide registries, and its
/// systems in the order they run. A game grows from here: its own systems go in the run order below, where the comments
/// say why each sits where it does (<c>SystemOrderTests</c> checks the order).
/// </summary>
public static class GameServiceCollectionExtensions
{
    public static IServiceCollection AddGame(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Bound here because registrations depend on it; a value that does not bind fails later, when the engine's
        // options are read, once the log file exists.
        var benchmark = BenchmarkAtRegistration(configuration);
        AddOptions(services, configuration);
        AddWorld(services);
        AddFixedSystems(services, benchmark);
        AddFrameSystems(services, benchmark);
        return services;
    }

    /// <summary>
    /// Reads the game's options, so a bad value stops the game before the window opens (exit code 1, naming the key),
    /// as a bad engine option does when <c>GameLoop</c> is resolved; and names any key under <c>Game</c> that no
    /// section of the game's holds.
    /// </summary>
    public static void ValidateOptions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Read<PlayerOptions>(services, PlayerOptions.SectionName);
        Read<WorldOptions>(services, WorldOptions.SectionName);
        string[] sections = [PlayerOptions.SectionName, WorldOptions.SectionName];
        var unknown = services
            .GetRequiredService<IConfiguration>()
            .GetSection("Game")
            .GetChildren()
            .Where(child => !sections.Contains(child.Path, StringComparer.OrdinalIgnoreCase))
            .Select(child => child.Path + " is not an option; check its spelling.")
            .ToList();
        if (unknown.Count > 0)
        {
            throw new OptionsValidationException("Game", typeof(object), unknown);
        }
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PlayerOptions>(configuration.GetSection(PlayerOptions.SectionName));
        services.AddSingleton<IValidateOptions<PlayerOptions>, PlayerOptionsValidator>();
        services.AddSingleton<IValidateOptions<PlayerOptions>>(
            new UnknownOptionKeys<PlayerOptions>(configuration.GetSection(PlayerOptions.SectionName))
        );
        services.Configure<WorldOptions>(configuration.GetSection(WorldOptions.SectionName));
        services.AddSingleton<IValidateOptions<WorldOptions>, WorldOptionsValidator>();
        services.AddSingleton<IValidateOptions<WorldOptions>>(
            new UnknownOptionKeys<WorldOptions>(configuration.GetSection(WorldOptions.SectionName))
        );
    }

    /// <summary>The world of a match: its generator, its looks and sky, and where the player starts.</summary>
    private static void AddWorld(IServiceCollection services)
    {
        // What a run keeps whatever match it plays is a singleton; what a match keeps is Scoped (each match has a scope of
        // its own) and is resolved only from the match's steps on, once its session has settled the seed.
        services.AddSingleton<IChunkGenerator, GameGenerator>();
        services.AddSingleton<IVoxelPalette, GamePalette>();
        services.AddSingleton<IAtmosphereSource, GameAtmosphere>();
        services.AddScoped(sp =>
            StartPose.For(
                sp.GetRequiredService<IOptions<WorldOptions>>().Value,
                sp.GetRequiredService<SessionService>().Match.Seed,
                CharacterSize
                    .ForHeight((float)sp.GetRequiredService<IOptions<PlayerOptions>>().Value.HeightMetres)
                    .EyeMetres
            )
        );
        // The run-wide registries, made once at the engine's "game" step: the movement keys first, then the engine's stock
        // particle effects.
        services.AddSingleton<StockEffects>();
        services.AddSingleton<IRunStartup, GameRunStartup>();
    }

    /// <summary>
    /// The fixed systems, in their run order: the player made at the start; the console's cleanup and reset (the
    /// systems after them put things back in the same step) and its balls; the player's input; what the last physics step did; the
    /// player's move (it reads the physics' pushes, and stand-ins are placed after it); the stand-ins and the loose
    /// bodies; and last the streaming centres, from where everything ended up.
    /// </summary>
    private static void AddFixedSystems(IServiceCollection services, BenchmarkSettings? benchmark)
    {
        services.AddScoped<IFixedUpdateSystem>(sp => new PlayerSpawnSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<IOptions<PlayerOptions>>().Value,
            sp.GetRequiredService<StartPose>(),
            sp.GetRequiredService<ILogger<PlayerSpawnSystem>>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new MapResetSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<LooseBodies>(),
            sp.GetRequiredService<ParticleEmitter>(),
            sp.GetRequiredService<VoxelWorld>(),
            sp.GetRequiredService<WorldResets>(),
            sp.GetRequiredService<ConsoleCommands>()
        ));
        // The console's balls, dropped before the physics read sees them.
        services.AddScoped<IFixedUpdateSystem>(sp => new DebugBallSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<LooseBodies>(),
            sp.GetRequiredService<VoxelWorld>(),
            sp.GetRequiredService<ConsoleCommands>(),
            GameMaterials.Stone
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new PlayerInputSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<ITickInput>(),
            sp.GetRequiredService<IOptions<EngineOptions>>(),
            ForceFlying(sp, benchmark),
            sp.GetRequiredService<ILogger<PlayerInputSystem>>(),
            sp.GetRequiredService<ConsoleCommands>(),
            sp.GetRequiredService<PlayerInputBlock>(),
            sp.GetRequiredService<PlayerSettings>(),
            sp.GetRequiredService<MovementKeys>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new PhysicsReadSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<LooseBodies>(),
            sp.GetRequiredService<PhysicsWorld>(),
            sp.GetRequiredService<ILogger<PhysicsReadSystem>>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new PlayerMovementSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<VoxelCharacterMover>(),
            sp.GetRequiredService<PlayArea>(),
            sp.GetRequiredService<ILogger<PlayerMovementSystem>>(),
            sp.GetRequiredService<ConsoleCommands>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new StandInSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<PhysicsWorld>(),
            sp.GetRequiredService<LooseBodies>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new LooseBodySystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<LooseBodies>(),
            sp.GetRequiredService<IOptions<EngineOptions>>(),
            sp.GetRequiredService<VoxelTintSampler>(),
            sp.GetRequiredService<ConsoleCommands>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new StreamingCentreSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<VoxelWorld>(),
            sp.GetRequiredService<SessionService>().Role,
            sp.GetRequiredService<LooseBodies>()
        ));
    }

    /// <summary>
    /// The frame systems, in their run order: the camera at the player's eye, or the fly camera (and the benchmark over
    /// it); the debug outlines; the camera to the renderer; the loose bodies' models; and the sky from where the camera
    /// is.
    /// </summary>
    private static void AddFrameSystems(IServiceCollection services, BenchmarkSettings? benchmark)
    {
        services.AddScoped<IFrameUpdateSystem>(sp => new PlayerCameraSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<IFrameInput>(),
            sp.GetRequiredService<IOptions<EngineOptions>>(),
            sp.GetRequiredService<VoxelWorld>(),
            sp.GetRequiredService<PlayerInputBlock>(),
            sp.GetRequiredService<PlayerSettings>(),
            sp.GetRequiredService<StartPose>().Pose
        ));
        services.AddScoped<IFrameUpdateSystem>(sp =>
        {
            var start = sp.GetRequiredService<StartPose>();
            return new DebugFlyCameraSystem(
                sp.GetRequiredService<EcsWorld>().World,
                sp.GetRequiredService<IFrameInput>(),
                sp.GetRequiredService<IOptions<EngineOptions>>(),
                sp.GetRequiredService<PlayArea>(),
                start.Eye,
                start.Yaw,
                sp.GetRequiredService<ILogger<DebugFlyCameraSystem>>(),
                start.Pitch,
                sp.GetRequiredService<ConsoleCommands>(),
                sp.GetRequiredService<PlayerSettings>(),
                sp.GetRequiredService<MovementKeys>(),
                sp.GetRequiredService<PlayerInputBlock>()
            );
        });
        // The benchmark, when enabled, overrides the fly camera's pose, so it runs after it.
        if (benchmark is { Enabled: true })
        {
            services.AddScoped<IFrameUpdateSystem>(sp => new BenchmarkSystem(
                sp.GetRequiredService<EcsWorld>().World,
                sp.GetRequiredService<IBenchmarkHost>(),
                benchmark,
                BenchmarkViews.For(
                    sp.GetRequiredService<StartPose>(),
                    sp.GetRequiredService<SessionService>().Match.Seed
                ),
                sp.GetRequiredService<ILogger<BenchmarkSystem>>(),
                (float)sp.GetRequiredService<IOptions<EngineOptions>>().Value.Camera.CircleHeightMetres
            ));
        }

        services.AddScoped<IFrameUpdateSystem>(sp => new DebugOutlineSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<DebugLines>()
        ));
        services.AddScoped<IFrameUpdateSystem>(sp => new CameraExtractSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<RenderView>(),
            sp.GetRequiredService<PlayerSettings>()
        ));
        services.AddScoped<IFrameUpdateSystem>(sp => new LooseBodyModelSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<ModelInstances>(),
            sp.GetRequiredService<LooseBodies>(),
            sp.GetRequiredService<VoxelTintSampler>(),
            sp.GetRequiredService<RenderView>()
        ));
        services.AddScoped<IFrameUpdateSystem>(sp => new AtmosphereSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<RenderView>(),
            sp.GetRequiredService<IAtmosphereSource>()
        ));
    }

    /// <summary>
    /// True when the player starts flying whatever its options say: the benchmark and the automatic path fly the debug
    /// camera, and the console's <c>fly</c> cannot switch it off then.
    /// </summary>
    private static bool ForceFlying(IServiceProvider sp, BenchmarkSettings? benchmark) =>
        benchmark is { Enabled: true }
        || sp.GetRequiredService<IOptions<EngineOptions>>().Value.Voxels.DebugWalkSpeed > 0;

    /// <summary>
    /// The benchmark's options as registration reads them, or null when they do not bind: the engine's options then fail
    /// when they are read, after the host and its log file exist.
    /// </summary>
    private static BenchmarkSettings? BenchmarkAtRegistration(IConfiguration configuration)
    {
        try
        {
            return configuration.GetSection("Engine:Benchmark").Get<BenchmarkSettings>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one section's options, so its validators run; a value that does not bind (text where a number goes) is
    /// reported as a failed option naming the section, as a value out of range is.
    /// </summary>
    private static void Read<TOptions>(IServiceProvider services, string section)
        where TOptions : class
    {
        try
        {
            _ = services.GetRequiredService<IOptions<TOptions>>().Value;
        }
        catch (InvalidOperationException ex)
        {
            throw new OptionsValidationException(section, typeof(TOptions), [$"{section}: {ex.Message}"]);
        }
    }
}
