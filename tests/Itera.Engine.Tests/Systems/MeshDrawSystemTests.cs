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
/// tint and shadow, a posed one with its clip's skinning matrices at the blend of its last two times, and while it fades
/// with each joint blended between its two clips, and with a <see cref="ShapedPose"/> shaped by the game's shaper; a mesh
/// attached to a joint of another's pose drawn there, and nothing when its holder is gone; the console's
/// <c>meshes</c> lists the assets and their clips, places one or many in front of the camera facing it, playing a clip or
/// held at a time of it, and clears only those it placed; and drawing allocates nothing.
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
        _assets.Add("bending", MeshAssetPassTests.SkinnedAsset("bending", bend: true));
    }

    [Fact]
    public void A_posed_mesh_is_drawn_with_its_clips_skinning_matrices_at_the_blended_time()
    {
        using var world = World.Create();
        using var system = Create(world);
        var animator = MeshAnimator.Playing(0);
        animator.PreviousSeconds = 0.2f;
        animator.Seconds = 0.6f;
        world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(2), animator);

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        var instance = Assert.Single(_instances.Instances.ToArray());
        Assert.True(instance.Posed);
        Assert.Equal(2, instance.Joints);
        var rig = _assets[2].Rig!;
        var locals = new JointTransform[2];
        var expected = new Matrix4x4[2];
        rig.Pose(0, 0.4f, locals);
        rig.WritePalette(locals, expected);
        var palette = _instances.Palettes.Slice(instance.FirstJoint, instance.Joints).ToArray();
        for (var j = 0; j < 2; j++)
        {
            Assert.True(
                Vector3.Distance(
                    Vector3.Transform(Vector3.One, expected[j]),
                    Vector3.Transform(Vector3.One, palette[j])
                ) < 1e-5f
            );
        }
    }

    [Fact]
    public void A_fading_mesh_blends_each_joint_between_its_two_clips()
    {
        using var world = World.Create();
        using var system = Create(world);
        // Half way from the bend at 0.4 s to the rest pose.
        var animator = MeshAnimator.Playing(0, seconds: 0.4f, speed: 0);
        animator.FadeTo(-1, 1);
        animator.Faded = 0.5f;
        animator.PreviousFaded = 0.5f;
        world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(2), animator);

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        var instance = Assert.Single(_instances.Instances.ToArray());
        var rig = _assets[2].Rig!;
        var bent = new JointTransform[2];
        var rest = new JointTransform[2];
        rig.Pose(0, 0.4f, bent);
        rig.Pose(-1, 0, rest);
        var blended = new JointTransform[2];
        for (var j = 0; j < 2; j++)
        {
            blended[j] = new JointTransform(
                Vector3.Lerp(bent[j].Translation, rest[j].Translation, 0.5f),
                Quaternion.Slerp(bent[j].Rotation, rest[j].Rotation, 0.5f),
                Vector3.Lerp(bent[j].Scale, rest[j].Scale, 0.5f)
            );
        }

        var expected = new Matrix4x4[2];
        rig.WritePalette(blended, expected);
        var tip = Vector3.Transform(new Vector3(2, 0, 0), _instances.Palettes[instance.FirstJoint + 1]);
        Assert.True(Vector3.Distance(Vector3.Transform(new Vector3(2, 0, 0), expected[1]), tip) < 1e-4f);
        // And not the bend's alone: the fade moved the tip.
        var bentPalette = new Matrix4x4[2];
        rig.WritePalette(bent, bentPalette);
        Assert.True(Vector3.Distance(Vector3.Transform(new Vector3(2, 0, 0), bentPalette[1]), tip) > 1e-3f);
    }

    [Fact]
    public void A_looping_clip_blends_across_its_wrap()
    {
        using var world = World.Create();
        using var system = Create(world);
        // After a wrap of the one-second clip: from -0.1 s (0.9 s before the wrap) to 0.15 s; a fifth of the way is 0.95 s.
        var animator = MeshAnimator.Playing(0);
        animator.PreviousSeconds = -0.1f;
        animator.Seconds = 0.15f;
        world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(2), animator);

        system.Update(new FrameTime(1f / 240, 1, 1, 0.2f));

        var instance = Assert.Single(_instances.Instances.ToArray());
        var rig = _assets[2].Rig!;
        var locals = new JointTransform[2];
        var expected = new Matrix4x4[2];
        rig.Pose(0, 0.95f, locals);
        rig.WritePalette(locals, expected);
        var tip = Vector3.Transform(new Vector3(2, 0, 0), _instances.Palettes[instance.FirstJoint + 1]);
        Assert.True(Vector3.Distance(Vector3.Transform(new Vector3(2, 0, 0), expected[1]), tip) < 1e-4f);
    }

    [Fact]
    public void Meshes_place_with_a_clip_and_seconds_holds_that_time_and_without_seconds_plays_it()
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, commands);
        world.Create(new CameraPose(), new ActiveCamera());
        var output = new RecordingOutput();

        commands.Execute("meshes place bending bend 0.5", output);
        commands.Execute("meshes place bending 2 bend", output);

        Assert.Empty(output.Errors);
        var animators = new List<(MeshAnimator Animator, float Scale)>();
        world.Query(
            new QueryDescription().WithAll<MeshAnimator, MeshPlacement>(),
            (ref MeshAnimator animator, ref MeshPlacement placement) => animators.Add((animator, placement.Scale))
        );
        Assert.Contains((MeshAnimator.Playing(0, 0.5f, speed: 0), 1f), animators);
        Assert.Contains((MeshAnimator.Playing(0), 2f), animators);
    }

    [Fact]
    public void Meshes_place_with_a_clip_the_asset_does_not_have_names_its_clips()
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, commands);
        world.Create(new CameraPose(), new ActiveCamera());
        var output = new RecordingOutput();

        commands.Execute("meshes place bending wave", output);
        commands.Execute("meshes place box wave", output);

        Assert.Equal(
            ["meshes: bending has no clip named wave; its clips: bend", "meshes: box has no rig, so no clips"],
            output.Errors
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
        Assert.Equal(
            "2: bending, 1 triangles, 1 material(s), 0 texture(s), 2.00 x 0.00 x 0.00 m, 2 joints, clips: bend 1.00 s",
            output.Lines[2]
        );
        Assert.Equal("3 mesh asset(s)", output.Lines[3]);
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
    [InlineData("meshes place bending bend -1")]
    [InlineData("meshes place bending bend 1 more")]
    [InlineData("meshes test 2 bending wave")]
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
    public void A_shaped_pose_is_shaped_by_the_games_shaper_before_its_palette_and_others_are_not()
    {
        using var world = World.Create();
        var shaper = new TurningShaper();
        using var system = Create(world, shaper: shaper);
        var shaped = world.Create(
            MeshPlacement.At(Vector3.Zero, Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f),
            new ShapedPose()
        );
        world.Create(
            MeshPlacement.At(Vector3.UnitX, Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f)
        );

        system.Update(new FrameTime(1f / 240, 1, 1, 1));

        Assert.Equal([shaped], shaper.Shaped);
        var rig = _assets[2].Rig!;
        var locals = new JointTransform[2];
        var globals = new Matrix4x4[2];
        var expected = new Matrix4x4[2];
        rig.Pose(0, 0.4f, locals);
        rig.Turn(locals, 1, TurningShaper.Turn, globals);
        rig.WritePalette(locals, expected);
        var instance = _instances.Instances.ToArray().Single(i => i.Transform.Translation == Vector3.Zero);
        var palette = _instances.Palettes.Slice(instance.FirstJoint, instance.Joints).ToArray();
        for (var j = 0; j < 2; j++)
        {
            var point = new Vector3(0.3f, 1.2f, -0.4f);
            Assert.True(
                Vector3.Distance(Vector3.Transform(point, expected[j]), Vector3.Transform(point, palette[j])) < 1e-5f
            );
        }
    }

    [Fact]
    public void An_attached_mesh_is_drawn_at_its_holders_joint_and_not_once_the_holder_is_gone()
    {
        using var world = World.Create();
        using var system = Create(world);
        var holder = world.Create(
            MeshPlacement.At(new Vector3(3, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f)
        );
        var offset = new Vector3(0, 0.5f, 0.1f);
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(holder, 1, offset, Quaternion.Identity));

        system.Update(new FrameTime(1f / 240, 1, 1, 1));

        var rig = _assets[2].Rig!;
        var locals = new JointTransform[2];
        var globals = new Matrix4x4[2];
        rig.Pose(0, 0.4f, locals);
        rig.Globals(locals, globals);
        var placed = MeshPoses.Placed(in world.Get<MeshPlacement>(holder), 1);
        var expected = Matrix4x4.CreateTranslation(offset) * globals[1] * placed;
        var attached = _instances.Instances.ToArray().Single(i => i.Asset == 0);
        Assert.True(
            Vector3.Distance(expected.Translation, attached.Transform.Translation) < 1e-5f,
            $"Drawn at {attached.Transform.Translation}, its joint's grip at {expected.Translation}."
        );

        _instances.Clear();
        world.Destroy(holder);
        system.Update(new FrameTime(1f / 240, 1, 1, 1));
        Assert.Equal(0, _instances.Count);
    }

    [Fact]
    public void Drawing_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world, shaper: new TurningShaper { Keep = false });
        var holder = world.Create(
            MeshPlacement.At(Vector3.UnitZ, Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.2f),
            new ShapedPose()
        );
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(holder, 1, Vector3.UnitY, Quaternion.Identity));
        world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(0));
        world.Create(MeshPlacement.At(Vector3.One, Quaternion.Identity, 2), DrawnMesh.Of(1));
        world.Create(
            MeshPlacement.At(Vector3.UnitX, Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.3f)
        );

        // The list is cleared between frames by the loop; here every update adds three, far below the limit.
        Assert.Equal(
            0,
            SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 1, 0.5f), warmup: 10, measured: 100)
        );
        Assert.Equal(550, _instances.Count);
    }

    private static ConsoleCommands NewCommands() =>
        new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);

    /// <summary>A shaper that turns joint 1 a quarter about +z, and keeps the entities it shaped.</summary>
    private sealed class TurningShaper : IMeshPoseShaper
    {
        public static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

        public List<Entity> Shaped { get; } = [];

        public bool Keep { get; init; } = true;

        public void Shape(
            World world,
            Entity entity,
            Rig rig,
            Span<JointTransform> locals,
            Span<Matrix4x4> globals,
            float alpha
        )
        {
            if (Keep)
            {
                Shaped.Add(entity);
            }

            rig.Turn(locals, 1, Turn, globals);
        }
    }

    private MeshDrawSystem Create(World world, ConsoleCommands? commands = null, IMeshPoseShaper? shaper = null)
    {
        var system = new MeshDrawSystem(world, _instances, _assets, commands: commands, shaper: shaper);
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
