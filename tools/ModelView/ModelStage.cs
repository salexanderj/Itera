using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Settings;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Itera.Tools.ModelView;

/// <summary>
/// The model stage's composition over the engine's (<c>AddIteraEngine</c>): no voxel world, a plain ground with a metre
/// grid at the world's middle (<see cref="StageGround"/>), a camera, and the engine's systems that draw and pose mesh
/// assets, so a script of console commands shows the models of <c>Engine:Assets:Extra</c> from set places
/// (<c>meshes place NAME at</c>, <c>viewpoint</c>, <c>screenshot</c>). The sky and the look are the engine's own: no game
/// sets them.
/// </summary>
public static class ModelStage
{
    public static IServiceCollection AddModelStage(this IServiceCollection services)
    {
        services.AddSingleton<IRunStartup, StageStartup>();

        // Fixed: the stage's ground and camera at the match's start, then the clips' times.
        services.AddScoped<IFixedUpdateSystem>(sp => new StageSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<MeshAssets>(),
            sp.GetRequiredService<WorldOrigin>(),
            sp.GetRequiredService<IOptions<EngineOptions>>()
        ));
        services.AddScoped<IFixedUpdateSystem>(sp => new MeshAnimatorSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<MeshAssets>()
        ));

        // Frame: the camera into the view, then every placed mesh drawn.
        services.AddScoped<IFrameUpdateSystem>(sp => new CameraExtractSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<RenderView>(),
            sp.GetRequiredService<PlayerSettings>()
        ));
        services.AddScoped<IFrameUpdateSystem>(sp => new MeshDrawSystem(
            sp.GetRequiredService<EcsWorld>().World,
            sp.GetRequiredService<MeshInstances>(),
            sp.GetRequiredService<MeshAssets>(),
            null,
            sp.GetRequiredService<ConsoleCommands>(),
            shifts: sp.GetRequiredService<OriginShifts>()
        ));
        return services;
    }
}

/// <summary>Adds the stage's ground among the run's mesh assets, at the engine's <c>game</c> step.</summary>
public sealed class StageStartup(MeshAssets assets) : IRunStartup
{
    public void Start() => assets.Add(StageGround.Name, StageGround.Mesh());
}

/// <summary>
/// The stage's ground: a square <see cref="HalfMetres"/> each way from the world's middle at y = 0, mid grey, with a
/// line every 10 cm and a darker one every metre, so a model's size reads against it.
/// </summary>
public static class StageGround
{
    /// <summary>The ground's name among the run's mesh assets.</summary>
    public const string Name = "stage-ground";

    /// <summary>How far the ground reaches from the world's middle each way, in metres.</summary>
    public const float HalfMetres = 30;

    /// <summary>The texels of the ground's texture across a metre, which it repeats.</summary>
    public const int TexelsPerMetre = 200;

    public static MeshAsset Mesh()
    {
        var h = HalfMetres;
        Vector3[] corners = [new(-h, 0, -h), new(-h, 0, h), new(h, 0, h), new(h, 0, -h)];
        var vertices = new MeshAssetVertex[4];
        for (var i = 0; i < 4; i++)
        {
            // The texture repeats every metre; x and z in metres give its coordinates.
            vertices[i] = MeshAssetVertex.Pack(
                corners[i],
                Vector3.UnitY,
                new Vector4(1, 0, 0, 1),
                new Vector2(corners[i].X, corners[i].Z),
                Vector2.Zero,
                Vector4.One
            );
        }

        var material = MeshMaterialData.Default with
        {
            Name = "ground",
            BaseColourTexture = new MeshTextureRef(0, MeshSampler.LinearRepeat, 0),
        };
        return new MeshAsset(Name, vertices, [0, 1, 2, 0, 2, 3], [new(0, 6, 0)], [material], [Grid()]);
    }

    /// <summary>A metre of the ground's texture: mid grey, lighter lines every 10 cm, a dark line at its edge.</summary>
    private static MeshTextureData Grid()
    {
        const int size = TexelsPerMetre;
        const int tenth = size / 10;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                byte shade =
                    x == 0 || y == 0 ? (byte)70
                    : x % tenth == 0 || y % tenth == 0 ? (byte)135
                    : (byte)150;
                var i = ((y * size) + x) * 4;
                rgba[i] = shade;
                rgba[i + 1] = shade;
                rgba[i + 2] = shade;
                rgba[i + 3] = 255;
            }
        }

        return MeshTextureData.FromRgba("stage grid", size, size, MeshTextureRole.Colour, rgba);
    }
}

/// <summary>
/// Makes the stage at a match's start (fixed group, first): the ground at the world's middle and the active camera 6 m
/// in front of it, 1.7 m up, looking at it. The camera only stands there: <c>viewpoint</c> draws from other places.
/// </summary>
public sealed class StageSystem(World world, MeshAssets assets, WorldOrigin origin, IOptions<EngineOptions> options)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    public override void Initialize()
    {
        var middle = origin.ToLocal(WorldPoint.Zero);
        World.Create(MeshPlacement.At(middle, Quaternion.Identity), DrawnMesh.Of(assets.Find(StageGround.Name)));
        var pose = new CameraPose
        {
            Position = middle + new Vector3(0, 1.7f, 6),
            Yaw = 0,
            Pitch = -0.2f,
        };
        CameraRules.CreateCamera(World, in pose, options.Value);
    }

    public override void Update(in FixedStepTime time) { }
}
