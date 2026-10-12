using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <c>ToolAim.Raycast</c> over a flat stone floor (its top at y = 0, 10 cm voxels) with a bush of leaves 1.3 to
/// 1.7 m up: from an eye inside the bush a tool's ray goes past the leaves round it to the floor, and from outside it
/// meets the leaves as before; within reach only; and a ray with no leaves meets what the voxel world's own ray meets.
/// <see cref="ToolAim.FindPlayer"/> and <c>ToolAim.Look</c> read the local player.
/// </summary>
public sealed class ToolAimTests : IDisposable
{
    private const byte Leaves = 5;
    private static readonly Vector3 s_down = -Vector3.UnitY;

    private readonly VoxelWorld _voxels;
    private readonly LeavesPassable _passability = new();

    public ToolAimTests()
    {
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings
            {
                StreamingRadiusMetres = 8,
                Levels = 1,
                WorkerThreads = 2,
            },
        };
        _voxels = new VoxelWorld(
            Options.Create(options),
            new FlatGenerator(new VoxelGrid(10)),
            NullLogger<VoxelWorld>.Instance
        );
        _voxels.Initialize();
        _voxels.SetCenters([Vector3.Zero]);
        VoxelLoading.WaitUntilLoaded(_voxels);
        _voxels.Apply(
            VoxelEdit.Box(new VoxelCoord(-20, 130, -20), new VoxelCoord(40, 40, 40), VoxelEditOperation.Set, Leaves)
        );
        _voxels.ApplyPendingEdits(int.MaxValue);
    }

    public void Dispose() => _voxels.Dispose();

    [Fact]
    public void From_inside_a_bush_a_tools_ray_goes_past_the_leaves_round_the_eye()
    {
        var eye = new Vector3(0.005f, 1.5f, 0.005f);

        Assert.True(ToolAim.Raycast(_voxels, _passability, eye, s_down, 4, out var hit));

        Assert.Equal(FlatGenerator.Stone, hit.Material);
        // The floor's top voxel spans y = -10 to 0 cm; the hit names its low corner.
        Assert.Equal(-10, hit.Voxel.Y);
        Assert.Equal(1.5f, hit.DistanceMetres, 0.002f);
        // Looking up, out of the bush, there is nothing.
        Assert.False(ToolAim.Raycast(_voxels, _passability, eye, Vector3.UnitY, 4, out _));
        // Within reach only.
        Assert.False(ToolAim.Raycast(_voxels, _passability, eye, s_down, 1, out _));
    }

    [Fact]
    public void From_outside_the_bush_a_tools_ray_meets_its_leaves()
    {
        var eye = new Vector3(0.005f, 3, 0.005f);

        Assert.True(ToolAim.Raycast(_voxels, _passability, eye, s_down, 4, out var hit));
        Assert.True(_voxels.Raycast(eye, s_down, 4, out var plain));

        Assert.Equal(Leaves, hit.Material);
        Assert.Equal(plain, hit);
    }

    [Fact]
    public void The_local_player_is_found_with_its_eye_look_and_buttons()
    {
        using var world = World.Create();
        Assert.False(ToolAim.FindPlayer(world).Found);
        world.Create(
            new LocalPlayer(),
            new PlayerCommand
            {
                Yaw = 0.5f,
                Pitch = -0.25f,
                Buttons = PlayerButtons.Sprint,
                Actions = 6,
            },
            new PlayerMode { Flying = true },
            new CharacterPosition { Feet = new Vector3(1, 2, 3) },
            CharacterSize.ForHeight(1.7f)
        );

        var player = ToolAim.FindPlayer(world);

        Assert.True(player.Found);
        Assert.True(player.Flying);
        Assert.Equal(PlayerButtons.Sprint, player.Buttons);
        Assert.Equal(6u, player.Actions);
        Assert.Equal(0.5f, player.Yaw);
        Assert.Equal(-0.25f, player.Pitch);
        Assert.Equal(new Vector3(1, 2 + 1.7f * CharacterSize.EyeShare, 3), player.Eye);
    }

    [Fact]
    public void Tools_aim_along_the_look_in_first_person_and_from_the_camera_while_flying()
    {
        using var world = World.Create();
        var eye = new Vector3(0, 1.6f, 0);
        world.Create(new CameraPose { Position = new Vector3(5, 6, 7), Yaw = 0.3f }, new ActiveCamera());

        var (from, forward) = ToolAim.Look(world, _voxels, eye, 0, 0, flying: false);
        Assert.Equal(eye, from);
        Assert.Equal(-Vector3.UnitZ, forward);

        (from, forward) = ToolAim.Look(world, _voxels, eye, 0, 0, flying: true);
        Assert.Equal(new Vector3(5, 6, 7), from);
        Assert.Equal(ThirdPersonCamera.Forward(0.3f, 0), forward);
    }

    [Fact]
    public void In_third_person_the_tools_aim_towards_what_the_screens_middle_points_at()
    {
        using var world = World.Create();
        var eye = new Vector3(0, 3f, 0);
        world.Create(new LocalPlayer(), new ThirdPersonView { On = true, Metres = 2.5f });
        _voxels.Apply(
            VoxelEdit.Box(new VoxelCoord(-200, 0, -520), new VoxelCoord(400, 400, 20), VoxelEditOperation.Set, 3)
        );
        _voxels.ApplyPendingEdits(int.MaxValue);

        var (from, forward) = ToolAim.Look(world, _voxels, eye, 0, 0, flying: false);

        Assert.Equal(eye, from);
        Assert.Equal(ThirdPersonCamera.Aim(_voxels, eye, 0, 0, 2.5f), forward);
        // Over the right shoulder: right of and above the eye's own look.
        Assert.True(forward.X > 0.05f && forward.Y > 0.03f, $"{forward}");
    }

    private sealed class LeavesPassable : IVoxelPassability
    {
        public bool IsPassable(byte material) => material == VoxelConstants.Air || material == Leaves;
    }
}
