using System.Numerics;
using Arch.Core;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Networking;
using Itera.Engine.Rendering;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Rendering.Meshes;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="MeshDrawSystem"/>: each placed mesh is drawn at its pose between the last two fixed steps, scaled, with its
/// tint and shadow; the console's <c>meshes</c> lists the assets, places one or many in front of the camera facing it,
/// and clears only those it placed; and drawing allocates nothing.
/// </summary>
public sealed class MeshDrawSystemTests
{
    private readonly MeshInstances _instances = new();
    private readonly MeshAssets _assets = new(NullLogger<MeshAssets>.Instance);

    public MeshDrawSystemTests()
    {
        _assets.Add("box", MeshAssetPassTests.Asset("box", MeshMaterialData.Default, vertices: 3));
        _assets.Add(
            "pair",
            MeshAssetPassTests.Asset("pair", MeshMaterialData.Default, vertices: 4, second: MeshMaterialData.Default)
        );
    }

    [Fact]
    public void A_mesh_is_drawn_at_its_blended_pose_scaled_with_its_tint_and_shadow()
    {
        using var world = World.Create();
        using var system = Create(world);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        world.Create(
            new MeshPlacement
            {
                PreviousPosition = new Vector3(2, 0, 0),
                Position = new Vector3(6, 0, 0),
                PreviousOrientation = Quaternion.Identity,
                Orientation = turn,
                Scale = 2,
            },
            DrawnMesh.Of(1, 0xFF00FF00u, ModelShadow.None)
        );

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        var instance = Assert.Single(_instances.Instances.ToArray());
        Assert.Equal((1, 0xFF00FF00u, ModelShadow.None), (instance.Asset, instance.Tint, instance.Shadow));
        // Half way from x = 2 to x = 6 and from no turn to a quarter turn, twice the size: +x goes to 2 along
        // (1, 0, -1)/√2.
        var at = Vector3.Transform(Vector3.Zero, instance.Transform);
        var x = Vector3.TransformNormal(Vector3.UnitX, instance.Transform);
        Assert.Equal(4, at.X, 1e-5f);
        Assert.Equal(0, at.Z, 1e-5f);
        Assert.Equal(MathF.Sqrt(2), x.X, 1e-4f);
        Assert.Equal(-MathF.Sqrt(2), x.Z, 1e-4f);
    }

    [Fact]
    public void Meshes_lists_the_assets()
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, commands);
        var output = new RecordingOutput();

        commands.Execute("meshes", output);

        Assert.Equal("0: box, 1 triangles, 1 material(s), 0 texture(s), 2.00 x 1.00 x 0.00 m", output.Lines[0]);
        Assert.Equal("1: pair, 2 triangles, 2 material(s), 0 texture(s), 3.00 x 1.00 x 0.00 m", output.Lines[1]);
        Assert.Equal("2 mesh asset(s)", output.Lines[2]);
    }

    [Fact]
    public void Meshes_place_puts_one_in_front_of_the_camera_facing_it()
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, commands);
        // Looking along -x: -z turned a quarter turn about +y.
        world.Create(new CameraPose { Position = new Vector3(10, 5, 0), Yaw = MathF.PI / 2 }, new ActiveCamera());
        var output = new RecordingOutput();

        commands.Execute("meshes place pair 3", output);

        Assert.Empty(output.Errors);
        system.Update(new FrameTime(1f / 60, 1, 1, 1));
        var instance = Assert.Single(_instances.Instances.ToArray());
        Assert.Equal(1, instance.Asset);
        // 4 m ahead, and with no voxel world 1.6 m below the eye.
        var forward = ThirdPersonCamera.Forward(MathF.PI / 2, 0);
        var at = Vector3.Transform(Vector3.Zero, instance.Transform);
        var expected = new Vector3(10, 5 - 1.6f, 0) + forward * MeshDrawSystem.PlaceMetres;
        Assert.Equal(expected.X, at.X, 1e-4f);
        Assert.Equal(expected.Y, at.Y, 1e-4f);
        Assert.Equal(expected.Z, at.Z, 1e-4f);
        // The asset's front (+z) turned towards the camera, at 3 times its size.
        var front = Vector3.TransformNormal(Vector3.UnitZ, instance.Transform);
        Assert.Equal(-3 * forward.X, front.X, 1e-4f);
        Assert.Equal(-3 * forward.Z, front.Z, 1e-4f);
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(40f)]
    public void Meshes_place_puts_it_on_the_voxel_ground(float height)
    {
        using var harness = new PhysicsTestHarness(loadedMetres: 8);
        using var world = World.Create();
        var commands = NewCommands();
        using var system = new MeshDrawSystem(world, _instances, _assets, harness.Voxels, commands);
        system.Initialize();
        world.Create(new CameraPose { Position = new Vector3(0, height, 0) }, new ActiveCamera());
        var output = new RecordingOutput();

        commands.Execute("meshes place box", output);
        system.Update(new FrameTime(1f / 60, 1, 1, 1));

        // The flat ground's top is y = 0, 4 m along -z.
        Assert.Empty(output.Errors);
        var at = Vector3.Transform(Vector3.Zero, Assert.Single(_instances.Instances.ToArray()).Transform);
        Assert.Equal(0, at.Y, 1e-3f);
        Assert.Equal(-MeshDrawSystem.PlaceMetres, at.Z, 1e-4f);
    }

    [Fact]
    public void Meshes_test_places_many_and_clear_removes_only_the_consoles()
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, commands);
        world.Create(new CameraPose(), new ActiveCamera());
        world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(0));
        var output = new RecordingOutput();

        commands.Execute("meshes test 10 pair", output);
        system.Update(new FrameTime(1f / 60, 1, 1, 1));
        var drawn = _instances.Count;
        commands.Execute("meshes clear", output);
        _instances.Clear();
        system.Update(new FrameTime(1f / 60, 2, 2, 1));

        Assert.Empty(output.Errors);
        Assert.Equal(11, drawn);
        Assert.Equal("10 mesh(es) removed", output.Lines[^1]);
        Assert.Equal(0, Assert.Single(_instances.Instances.ToArray()).Asset);
    }

    [Theory]
    [InlineData("meshes place nothing")]
    [InlineData("meshes place box -1")]
    [InlineData("meshes test 0")]
    [InlineData("meshes spin")]
    public void Meshes_refuses_what_it_cannot_do(string line)
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, commands);
        world.Create(new CameraPose(), new ActiveCamera());
        var output = new RecordingOutput();

        commands.Execute(line, output);

        Assert.NotEmpty(output.Errors);
        Assert.Equal(0, world.CountEntities(new QueryDescription().WithAll<DrawnMesh>()));
    }

    [Fact]
    public void Drawing_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(0));
        world.Create(MeshPlacement.At(Vector3.One, Quaternion.Identity, 2), DrawnMesh.Of(1));

        // The list is cleared between frames by the loop; here every update adds two, far below the limit.
        Assert.Equal(
            0,
            SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 1, 0.5f), warmup: 10, measured: 100)
        );
        Assert.Equal(220, _instances.Count);
    }

    private static ConsoleCommands NewCommands() =>
        new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);

    private MeshDrawSystem Create(World world, ConsoleCommands? commands = null)
    {
        var system = new MeshDrawSystem(world, _instances, _assets, commands: commands);
        system.Initialize();
        return system;
    }

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Errors.Add(text);
    }
}
