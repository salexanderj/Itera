using Itera.Engine;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Settings;
using Itera.Engine.Systems;
using Itera.MeshSample.Rendering;
using Itera.MeshSample.Systems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.MeshSample;

/// <summary>
/// The sample on top of the engine (<c>AddIteraEngine</c>), with no voxel terrain: no generator and no
/// <c>AddIteraVoxels</c>, so matches have no voxel world (no streaming, no navigation, no terrain to settle into). Its
/// world is the yard's meshes and their colliders; its player walks and climbs them (the engine's
/// <see cref="ClimbingMode"/>) inside a play area the size of the yard, balls from the console land on them and stay
/// where they rest (no game decides otherwise), and the sky is the engine's default (no <c>IAtmosphereSource</c>).
/// </summary>
public static class MeshSampleServiceCollectionExtensions
{
    public static IServiceCollection AddMeshSample(this IServiceCollection services)
    {
        services.AddSingleton<StockEffects>();
        services.AddSingleton<IRunStartup, SampleRunStartup>();
        // The sample's own render pass: the glow round the block the player aims at, which AimHighlightSystem finds.
        services.AddSingleton<AimHighlight>();
        services.AddSingleton<IRenderPass, AimHighlightPass>();
        // The yard camera's image view, shown on the yard's screen.
        services.AddSingleton<YardCamera>();
        services.AddScoped<YardColliders>();
        // The play area round the yard: the mover's map edge is a wall at its side, and with the floor 1 m under the
        // ground, a body 11 m under the ground has fallen out.
        services.Replace(
            ServiceDescriptor.Scoped(_ => new PlayArea(Yard.GroundHalfMetres, worldFloorMetres: -1, worldTopMetres: 30))
        );
        services.AddScoped<IMovementMode>(sp => new ClimbingMode(
            sp.GetRequiredService<MovementProbes>(),
            Yard.Climbable
        ));
        AddFixedSystems(services);
        AddFrameSystems(services);
        return services;
    }

    /// <summary>
    /// The fixed systems, in their run order: the yard and the player at the start, the fox (its pose before the clips
    /// move on); the console's cleanup and reset, and its balls; the player's input; what the last physics step did;
    /// the player's move and climb; the stand-ins and the loose bodies; and the clips moved on.
    /// </summary>
    private static void AddFixedSystems(IServiceCollection services)
    {
        services.AddScoped<IFixedUpdateSystem>(sp => new YardSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<MeshAssets>(),
            sp.GetRequiredService<MeshColliders>(),
            sp.GetRequiredService<YardColliders>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new SampleSpawnSystem(sp.GetRequiredService<EcsWorld>().World));
        services.AddScoped<IFixedUpdateSystem>(sp => new SampleFoxSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<MeshAssets>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new MapResetSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<LooseBodies>(),
            sp.GetRequiredService<ParticleEmitter>(),
            null,
            sp.GetRequiredService<WorldResets>(),
            sp.GetRequiredService<ConsoleCommands>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new DebugBallSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<LooseBodies>(),
            null,
            sp.GetRequiredService<ConsoleCommands>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new PlayerInputSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<ITickInput>(),
            sp.GetRequiredService<IOptions<EngineOptions>>(),
            false,
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
            sp.GetRequiredService<ConsoleCommands>(),
            mode: sp.GetService<IMovementMode>()
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
            sp.GetRequiredService<ConsoleCommands>(),
            sp.GetService<ILooseBodyRest>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new MeshAnimatorSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<MeshAssets>()
        ));
    }

    /// <summary>
    /// The frame systems, in their run order: the camera at the player's eye, or the fly camera; the debug outlines;
    /// the camera to the renderer; the block the player aims at, from the camera's pose; the loose bodies' models; and
    /// the mesh assets placed (the yard, the fox).
    /// </summary>
    private static void AddFrameSystems(IServiceCollection services)
    {
        services.AddScoped<IFrameUpdateSystem>(sp => new PlayerCameraSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<IFrameInput>(),
            sp.GetRequiredService<IOptions<EngineOptions>>(),
            null,
            sp.GetRequiredService<PlayerInputBlock>(),
            sp.GetRequiredService<PlayerSettings>(),
            SampleSpawnSystem.StartPose
        ));
        services.AddScoped<IFrameUpdateSystem>(sp =>
        {
            var start = SampleSpawnSystem.StartPose;
            return new DebugFlyCameraSystem(
                sp.GetRequiredService<EcsWorld>().World,
                sp.GetRequiredService<IFrameInput>(),
                sp.GetRequiredService<IOptions<EngineOptions>>(),
                sp.GetRequiredService<PlayArea>(),
                start.Position,
                start.Yaw,
                sp.GetRequiredService<ILogger<DebugFlyCameraSystem>>(),
                start.Pitch,
                sp.GetRequiredService<ConsoleCommands>(),
                sp.GetRequiredService<PlayerSettings>(),
                sp.GetRequiredService<MovementKeys>(),
                sp.GetRequiredService<PlayerInputBlock>()
            );
        });
        services.AddScoped<IFrameUpdateSystem>(sp => new DebugOutlineSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<DebugLines>()
        ));
        services.AddScoped<IFrameUpdateSystem>(sp => new CameraExtractSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<RenderView>(),
            sp.GetRequiredService<PlayerSettings>()
        ));
        services.AddScoped<IFrameUpdateSystem>(sp => new AimHighlightSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<CollisionSources>(),
            sp.GetRequiredService<YardColliders>(),
            sp.GetRequiredService<AimHighlight>()
        ));
        // The yard camera asked for each frame (any place in the frame group: it reads nothing the others write).
        services.AddScoped<IFrameUpdateSystem>(sp => new YardCameraSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<YardCamera>(),
            sp.GetRequiredService<RenderViews>()
        ));
        services.AddScoped<IFrameUpdateSystem>(sp => new LooseBodyModelSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<ModelInstances>(),
            sp.GetRequiredService<LooseBodies>(),
            sp.GetRequiredService<VoxelTintSampler>(),
            sp.GetRequiredService<RenderView>()
        ));
        services.AddScoped<IFrameUpdateSystem>(sp => new MeshDrawSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<MeshInstances>(),
            sp.GetRequiredService<MeshAssets>(),
            null,
            sp.GetRequiredService<ConsoleCommands>()
        ));
    }
}
