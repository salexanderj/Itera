using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Rendering;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Systems;

public sealed class DebugOutlineSystemTests
{
    private readonly DebugLines _lines = new(NullLogger<DebugLines>.Instance);

    [Fact]
    public void Each_body_is_outlined_between_its_last_two_poses_while_the_lines_are_on()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(
            new BodyPose
            {
                PreviousPosition = Vector3.Zero,
                Position = new Vector3(4, 0, 0),
                PreviousOrientation = Quaternion.Identity,
                Orientation = Quaternion.Identity,
            },
            new BodyBox { Min = new Vector3(-0.1f), Max = new Vector3(0.1f) }
        );

        system.Update(new FrameTime(1f / 240, 1, 1, 0.25f));
        var off = _lines.Vertices.Length;
        _lines.Enabled = true;
        system.Update(new FrameTime(1f / 240, 1, 1, 0.25f));

        Assert.Equal(0, off);
        Assert.Equal(24, _lines.Vertices.Length);
        // A quarter of the way from x = 0 to x = 4: the box spans x 0.9 to 1.1.
        Assert.All(_lines.Vertices.ToArray(), v => Assert.InRange(v.Position.X, 0.9f - 1e-5f, 1.1f + 1e-5f));
        Assert.Equal(DebugOutlineSystem.BodyColour, _lines.Vertices[0].Colour);
    }

    [Fact]
    public void A_character_with_a_stand_in_is_outlined_and_one_without_is_not()
    {
        using var world = World.Create();
        using var system = Create(world);
        var size = new CharacterSize { HeightMetres = 1.7f, RadiusMetres = 0.26f };
        world.Create(
            new CharacterStandIn { Present = true },
            new CharacterPosition { PreviousFeet = Vector3.Zero, Feet = new Vector3(0, 0, 2) },
            size
        );
        world.Create(new CharacterStandIn(), new CharacterPosition(), size);
        _lines.Enabled = true;

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        // Two circles of 16 segments and four sides, around feet halfway to z = 2.
        Assert.Equal(2 * 36, _lines.Vertices.Length);
        Assert.All(_lines.Vertices.ToArray(), v => Assert.InRange(v.Position.Z, 1 - 0.27f, 1 + 0.27f));
        Assert.Equal(DebugOutlineSystem.StandInColour, _lines.Vertices[0].Colour);
    }

    /// <summary>A frame with a body and a stand-in to outline allocates nothing.</summary>
    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(
            new BodyPose { Orientation = Quaternion.Identity, PreviousOrientation = Quaternion.Identity },
            new BodyBox()
        );
        world.Create(
            new CharacterStandIn { Present = true },
            new CharacterPosition(),
            new CharacterSize { HeightMetres = 1.7f, RadiusMetres = 0.26f }
        );
        _lines.Enabled = true;
        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));
        Assert.Equal(2 * (12 + 36), _lines.Vertices.Length);

        // The list is cleared between frames by the loop; here every update adds 48 lines, far below the limit.
        Assert.Equal(
            0,
            SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 1, 0.5f), warmup: 10, measured: 100)
        );
    }

    private DebugOutlineSystem Create(World world)
    {
        var system = new DebugOutlineSystem(world, _lines);
        system.Initialize();
        return system;
    }
}
