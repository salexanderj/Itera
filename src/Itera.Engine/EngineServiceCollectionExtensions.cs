using Itera.Engine.Assets;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Control;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Loading;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Modding;
using Itera.Engine.Networking;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Saves;
using Itera.Engine.Settings;
using Itera.Engine.Systems;
using Itera.Engine.Time;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.Voxels;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine;

/// <summary>
/// Registers the engine's services; a game with voxel terrain adds it with
/// <see cref="VoxelServiceCollectionExtensions.AddIteraVoxels"/>. The run's services are singletons: there is one
/// window and one device. A match's services (its session, physics, save, and ECS world, and with terrain its world and
/// navigation) are Scoped: each match has a DI scope of its own (<see cref="MatchServices"/>), made when it starts and
/// disposed when it ends. A singleton never takes a scoped service in its constructor; it is given the match's when one
/// starts (<see cref="MatchControl"/>).
/// </summary>
public static class EngineServiceCollectionExtensions
{
    /// <summary>
    /// Adds logging and the engine to <paramref name="services"/>. <paramref name="configuration"/> is the root
    /// configuration: <see cref="EngineOptions"/> binds from its "Engine" section and log levels from "Serilog".
    /// </summary>
    public static IServiceCollection AddIteraEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EngineOptions>(configuration.GetSection(EngineOptions.SectionName));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<EngineOptions>, EngineOptionsValidator>()
        );
        services.AddSingleton<IValidateOptions<EngineOptions>>(
            new UnknownOptionKeys<EngineOptions>(configuration.GetSection(EngineOptions.SectionName))
        );
        services.AddIteraLogging(configuration);

        services.TryAddSingleton<GameClock>();
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
            return new FixedStepAccumulator(1f / options.FixedStepHz, options.MaxFixedStepsPerFrame);
        });

        services.TryAddSingleton<WindowService>();
        // The player's settings (settings.json); the host adds the command line's keys, which win over the file.
        services.TryAddSingleton(CommandLineKeys.None);
        // The keybindings: the engine's and the game's actions, read from settings.json by the player's settings.
        services.TryAddSingleton<KeyBindings>();
        services.TryAddSingleton<PlayerSettings>();
        services.TryAddSingleton<InputService>();
        services.TryAddSingleton<IFrameInput>(sp => sp.GetRequiredService<InputService>());
        services.TryAddSingleton<ITickInput>(sp => sp.GetRequiredService<InputService>());
        services.TryAddSingleton<VulkanContext>();
        services.TryAddSingleton<VulkanSwapchain>();
        services.TryAddSingleton<FrameSync>();
        services.TryAddSingleton<GpuMemoryAllocator>();
        services.TryAddSingleton<StagingBuffer>();
        services.TryAddSingleton<IVoxelPalette, GreyVoxelPalette>();
        services.TryAddScoped<IVoxelTintMap, NoVoxelTintMap>();
        services.TryAddSingleton<WorldMeshes>();
        services.TryAddSingleton<MeshPass>();
        services.TryAddSingleton<MeshView>();
        services.TryAddSingleton<MeshAssets>();
        services.TryAddSingleton<NoGpuWorld>();
        // Without graphics nothing is uploaded: the loading screen waits on the stand-in.
        services.TryAddSingleton<IWorldOnGpu>(sp =>
            sp.GetRequiredService<IOptions<EngineOptions>>().Value.Window.Mode == WindowMode.None
                ? sp.GetRequiredService<NoGpuWorld>()
                : sp.GetRequiredService<WorldMeshes>()
        );
        services.TryAddSingleton<DebugLinePass>();
        services.TryAddSingleton<UiPass>();
        services.TryAddSingleton<UpscalePass>();
        services.TryAddSingleton<SmaaPass>();
        services.TryAddSingleton<LoadingPass>();
        services.TryAddSingleton<DepthTarget>();
        services.TryAddSingleton<ParticleEmitter>();
        services.TryAddSingleton<ParticleStats>();
        services.TryAddSingleton(sp => new ParticleColours(sp.GetRequiredService<IVoxelPalette>()));
        services.TryAddSingleton<ParticlePass>();
        services.TryAddSingleton<Screenshots>();
        services.TryAddSingleton<ScreenshotPass>();
        services.TryAddSingleton<ModelInstances>();
        services.TryAddSingleton<MeshInstances>();
        services.TryAddSingleton<RenderViews>();
        services.TryAddSingleton<MeshAssetPass>();
        services.TryAddSingleton<RenderViewPass>();
        // The game's own passes (IRenderPass, registered by the game), run at their stages.
        services.TryAddSingleton<RenderPasses>();
        services.TryAddSingleton<MeshAssetDraws>();
        services.TryAddSingleton<PointLights>();
        services.TryAddScoped<VoxelTintSampler>();
        services.TryAddSingleton(sp => new DebugLines(
            sp.GetRequiredService<ILogger<DebugLines>>(),
            sp.GetRequiredService<IOptions<EngineOptions>>().Value.Renderer.DebugLines
        ));
        services.TryAddSingleton<GpuTimings>();
        services.TryAddSingleton<SystemTimes>();
        services.TryAddSingleton<GpuTimer>();
        services.TryAddSingleton<FrameRenderer>();
        services.TryAddSingleton(sp => new FrameExchange(sp.GetRequiredService<WindowService>().Wake));
        services.TryAddSingleton<RenderThread>();
        services.TryAddSingleton<RenderView>();
        services.TryAddSingleton<DynamicRenderScale>();
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
            return new ShaderCompiler(
                sp.GetRequiredService<ILogger<ShaderCompiler>>(),
                Path.Combine(options.Application.DataDirectory(), "ShaderCache"),
                options.Renderer.ShaderSourceDirectory,
                options.Renderer.GameShaderSourceDirectory
            );
        });
        services.TryAddSingleton<ShaderReloads>();
        services.TryAddSingleton(sp => AudioDeviceFactory.Create(sp.GetRequiredService<ILogger<OpenAlAudioDevice>>()));
        services.TryAddSingleton<AudioService>();
        // A match's services: its world, physics, session, navigation, save, and ECS world.
        services.TryAddScoped<MatchContext>();
        services.TryAddScoped<MatchServices>();
        services.TryAddSingleton<MatchControl>();
        services.TryAddSingleton<WarmUps>();
        // What a match is, as a joining game must share it: the voxel world's generator when the game has one.
        services.TryAddSingleton<IMatchIdentity>(sp =>
            sp.GetService<IChunkGenerator>() is { } generator
                ? new GeneratorMatchIdentity(generator)
                : new ApplicationMatchIdentity(sp.GetRequiredService<IOptions<EngineOptions>>())
        );
        services.TryAddScoped<EcsWorld>();
        // The match's origin, which every local position of the match (the ECS's, the physics', the renderer's) is measured
        // from: one for the world, the colliders, and the mover.
        // It starts where the game's match starts (its IOriginStart, read at the match's "origin" step, once the session has
        // settled the seed), else at the map's middle.
        services.TryAddScoped<WorldOrigin>();
        // What moves the origin and everything kept in local positions with it (the game's IOriginShiftHandlers last).
        services.TryAddScoped<OriginShifts>();
        services.TryAddScoped<PhysicsWorld>();
        // The match's colliders a game registers, and the sources rays and the character mover meet: those and the terrain.
        services.TryAddScoped<MeshColliders>();
        services.TryAddScoped<CollisionSources>();
        services.TryAddSingleton<IVoxelPassability, NothingPassable>();
        services.TryAddSingleton<IVoxelSurfaceTags, NoVoxelSurfaceTags>();
        // The voxel models (in the voxel world's memory with terrain, their own without), the loose bodies (which meet the
        // terrain through its colliders, or the mesh colliders alone), and the bodies as the character mover's cells.
        services.TryAddScoped(sp => new VoxelModels(sp.GetService<VoxelWorld>()));
        services.TryAddScoped(CreateLooseBodies);
        services.TryAddScoped(CreateLooseBodyVoxels);
        // The character mover over the terrain when there is one and the colliders, or the colliders alone in cells of
        // Engine:Physics:CollisionCentimetres, the map's edge the play area's world edge; the loose bodies either way.
        services.TryAddScoped(CreateCharacterMover);
        // What a game's movement mode asks of the world round a body: a wall ahead, a ledge's top, room to stand.
        services.TryAddScoped<MovementProbes>();
        // What the engine's player, camera, and benchmark systems share (Systems/): the play area (round the terrain's
        // grid, or PlayArea.WithoutTerrain; a game without terrain may replace it), the input block a game sets, the
        // console's resets, and the movement keys, made when a game first asks for them (an IRunStartup, before its own
        // keys). The benchmark's view of the engine comes with the terrain (AddIteraVoxels).
        services.TryAddScoped(sp =>
            sp.GetService<VoxelWorld>() is { } world ? new PlayArea(world.Grid) : PlayArea.WithoutTerrain
        );
        services.TryAddScoped<PlayerInputBlock>();
        services.TryAddScoped<WorldResets>();
        services.TryAddSingleton<MovementKeys>();
        // The transports live for the run; each match's session opens and closes the one it uses.
        services.TryAddSingleton<NetworkService>();
        services.TryAddSingleton<SteamService>();
        services.TryAddSingleton<SteamTransport>();
        services.TryAddSingleton<ISessionTransports, SessionTransports>();
        services.TryAddSingleton<SteamFriendList>();
        services.TryAddScoped<SaveService>();
        services.TryAddScoped<SessionService>();
        services.TryAddSingleton<UiFonts>();
        services.TryAddSingleton<ConsoleCommands>();
        services.TryAddSingleton<DevConsole>();
        services.TryAddSingleton<EngineCommands>();
        services.TryAddSingleton<TimeCommands>();
        services.TryAddSingleton<MatchCommands>();
        services.TryAddSingleton<SettingsCommands>();
        services.TryAddSingleton<ParticleCommands>();
        services.TryAddSingleton<SoundCommands>();
        // The loading screen and the engine's loading tasks of each match, shown in this order: Sounds, then the terrain's
        // (AddIteraVoxels), then the game's own.
        services.TryAddSingleton<LoadingScreen>();
        services.AddScoped<ILoadingTask, SoundsLoadingTask>();
        // The run's UI layers, drawn in this order (later on top), the console over the match's (IMatchUiLayer).
        services.TryAddSingleton<LoadingLayer>();
        services.AddSingleton<IUiLayer>(sp => sp.GetRequiredService<LoadingLayer>());
        services.TryAddSingleton<StatsLayer>();
        services.AddSingleton<IUiLayer>(sp => sp.GetRequiredService<StatsLayer>());
        // The menu over the match's layers, and the console over the menu. A game gives the menu its own look.
        services.TryAddSingleton(MenuStyle.Plain);
        services.TryAddSingleton<MenuLayer>();
        services.AddSingleton<IUiLayer>(sp => sp.GetRequiredService<MenuLayer>());
        services.TryAddSingleton<ConsoleLayer>();
        services.AddSingleton<IUiLayer>(sp => sp.GetRequiredService<ConsoleLayer>());
        // The aim dot, the first of a match's layers (the HUD's lowest; the game's are registered after the engine's).
        services.TryAddScoped<AimDotLayer>();
        services.AddScoped<IMatchUiLayer>(sp => sp.GetRequiredService<AimDotLayer>());
        services.TryAddSingleton<UISurface>();
        services.TryAddSingleton<ModLoader>();
        services.TryAddSingleton<ControlService>();
        services.TryAddSingleton<GameLoop>();

        return services;
    }

    private static VoxelCharacterMover CreateCharacterMover(IServiceProvider services)
    {
        var passability = services.GetRequiredService<IVoxelPassability>();
        var colliders = services.GetRequiredService<MeshColliders>();
        if (services.GetService<VoxelWorld>() is { } world)
        {
            return new VoxelCharacterMover(
                world,
                passability,
                services.GetRequiredService<LooseBodyVoxels>(),
                colliders
            );
        }

        var cell = services.GetRequiredService<IOptions<EngineOptions>>().Value.Physics.CollisionCentimetres;
        var edge = services.GetRequiredService<PlayArea>().WorldEdgeMetres;
        return new VoxelCharacterMover(
            passability,
            colliders,
            cell,
            edge,
            services.GetRequiredService<LooseBodyVoxels>()
        );
    }

    private static LooseBodies CreateLooseBodies(IServiceProvider services)
    {
        var physics = services.GetRequiredService<PhysicsWorld>();
        var models = services.GetRequiredService<VoxelModels>();
        var passability = services.GetRequiredService<IVoxelPassability>();
        var options = services.GetRequiredService<IOptions<EngineOptions>>();
        var logger = services.GetRequiredService<ILogger<LooseBodies>>();
        if (services.GetService<VoxelWorld>() is { } world)
        {
            var colliders = services.GetRequiredService<VoxelColliders>();
            return new LooseBodies(physics, world, colliders, models, passability, options, logger);
        }

        // Read at the first need: a game's play area may read the match's seed, which the session settles later.
        return new LooseBodies(
            physics,
            models,
            passability,
            options,
            logger,
            () => services.GetRequiredService<PlayArea>().FallLimitMetres,
            services.GetRequiredService<WorldOrigin>()
        );
    }

    private static LooseBodyVoxels CreateLooseBodyVoxels(IServiceProvider services)
    {
        var bodies = services.GetRequiredService<LooseBodies>();
        var passability = services.GetRequiredService<IVoxelPassability>();
        if (services.GetService<VoxelWorld>() is { } world)
        {
            return new LooseBodyVoxels(bodies, world, passability);
        }

        var cell = services.GetRequiredService<IOptions<EngineOptions>>().Value.Physics.CollisionCentimetres;
        return new LooseBodyVoxels(bodies, passability, cell);
    }
}
