using System.Numerics;
using System.Text.Json.Nodes;
using Arch.Core;
using Itera.Engine;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Tests.Assets;
using Itera.Modding;
using Itera.Tools.ModelView;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.ModelView.Tests;

/// <summary>
/// The model stage played without a window (<see cref="HeadlessRun"/>), from its shipped settings: a match with no voxel
/// world has the stage's ground at the world's middle and an active camera; an extra model named on the command line is
/// read and placed where a script asks; the ground faces up with its grid; and the stage's system allocates nothing.
/// </summary>
public sealed class StageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("itera-stage-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_match_has_the_ground_and_a_camera_and_places_an_extra_model_where_asked()
    {
        var model = WriteTriangle();
        using var provider = Build(
            new Dictionary<string, string?>
            {
                ["Engine:Assets:Extra:0:Name"] = "probe",
                ["Engine:Assets:Extra:0:Path"] = model,
            }
        );
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new RecordingOutput();

        commands.Execute("meshes place probe at 1 0 -2 0", output);

        Assert.Empty(output.Errors);
        Assert.Equal(1, match.World.CountEntities(new QueryDescription().WithAll<ActiveCamera>()));
        var origin = match.Services.GetRequiredService<WorldOrigin>();
        var placed = Placements(match.World);
        Assert.Equal(2, placed.Count);
        Assert.Contains(origin.ToLocal(WorldPoint.Zero), placed);
        Assert.Contains(origin.ToLocal(new WorldPoint(1, 0, -2)), placed);
    }

    [Fact]
    public void The_ground_is_two_triangles_facing_up_with_its_grid()
    {
        var ground = StageGround.Mesh();

        var a = ground.Vertices[ground.Indices[0]].Position;
        var b = ground.Vertices[ground.Indices[1]].Position;
        var c = ground.Vertices[ground.Indices[2]].Position;
        Assert.Equal(2, ground.TriangleCount);
        Assert.True(Vector3.Cross(b - a, c - a).Y > 0);
        Assert.Equal(new Vector3(StageGround.HalfMetres, 0, StageGround.HalfMetres), ground.BoundsMax);
        var grid = Assert.Single(ground.Textures);
        Assert.Equal(StageGround.TexelsPerMetre, grid.Width);
    }

    [Fact]
    public void The_stage_system_allocates_nothing()
    {
        using var provider = Build();
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var system = match.Services.GetServices<IFixedUpdateSystem>().OfType<StageSystem>().Single();

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FixedStepTime(0, 1)));
    }

    /// <summary>The stage's real services, from its shipped settings and <paramref name="options"/> over them, with no log file.</summary>
    private static ServiceProvider Build(IReadOnlyDictionary<string, string?>? options = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Engine:Logging:FileEnabled"] = "false",
                    ["Engine:Physics:Threads"] = "1",
                }
            )
            .AddInMemoryCollection(options ?? new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddIteraEngine(configuration);
        services.AddModelStage();
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private static List<Vector3> Placements(World world)
    {
        var found = new List<Vector3>();
        world.Query(
            new QueryDescription().WithAll<MeshPlacement, DrawnMesh>(),
            (ref MeshPlacement placement) => found.Add(placement.Position)
        );
        return found;
    }

    /// <summary>A triangle with a named material, written as a .glb in the test's folder.</summary>
    private string WriteTriangle()
    {
        var gltf = new GltfBuilder();
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(positions),
                material: gltf.Material(new JsonObject { ["name"] = "paint" })
            )
        );
        gltf.Node(new JsonObject { ["mesh"] = mesh });
        return gltf.WriteGlb(_folder, "probe");
    }

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Errors.Add(text);
    }
}
