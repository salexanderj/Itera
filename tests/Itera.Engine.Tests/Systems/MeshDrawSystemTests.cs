using System.Numerics;
using Arch.Core;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Networking;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Rendering.Meshes;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="MeshDrawSystem"/>: each placed mesh is drawn at its pose between the last two fixed steps, scaled, with its
/// tint and shadow, a posed one with its clip's skinning matrices at the blend of its last two times, and while it fades
/// with each joint blended between its two clips, a blended clip posed its shift further through its length, and with a <see cref="ShapedPose"/> shaped by the game's shaper; with a
/// <see cref="SmoothedPose"/> a change of clip starting from the pose drawn before and settling into the new one, an
/// unsmoothed one changing at once, a marked switch of the game's shaping smoothed too, a new entity in a gone one's slot starting afresh, and what it holds following it; a
/// mesh attached to a joint of another's pose drawn there, and nothing when its holder is gone; a hidden mesh, posed or
/// not, and what is attached to it, not drawn; the console's
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
    public void A_blended_clip_moves_each_joint_towards_it_by_its_weight_between_the_steps()
    {
        using var world = World.Create();
        using var system = Create(world);
        // The bend at 0.4 s, the rest pose blended in: from none at the last step to all at this one, half at alpha 0.5.
        var animator = MeshAnimator.Playing(0, seconds: 0.4f, speed: 0);
        animator.SetBlend(-1, 0);
        animator.SetBlend(-1, 1);
        world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(2), animator);
        // The rest pose with the bend blended in at its own share of its length: 0.4 s, as the clip has no length.
        var resting = MeshAnimator.Playing(-1, seconds: 0.4f, speed: 0);
        resting.SetBlend(0, 0.5f);
        resting.SetBlend(0, 0.5f);
        world.Create(MeshPlacement.At(new Vector3(10, 0, 0), Quaternion.Identity), DrawnMesh.Of(2), resting);

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        var instances = _instances.Instances.ToArray();
        var rig = _assets[2].Rig!;
        var bent = new JointTransform[2];
        var rest = new JointTransform[2];
        rig.Pose(0, 0.4f, bent);
        rig.Pose(-1, 0, rest);
        var halfway = new JointTransform[2];
        for (var j = 0; j < 2; j++)
        {
            halfway[j] = new JointTransform(
                Vector3.Lerp(bent[j].Translation, rest[j].Translation, 0.5f),
                Quaternion.Slerp(bent[j].Rotation, rest[j].Rotation, 0.5f),
                Vector3.Lerp(bent[j].Scale, rest[j].Scale, 0.5f)
            );
        }

        var expected = new Matrix4x4[2];
        rig.WritePalette(halfway, expected);
        var want = Vector3.Transform(new Vector3(2, 0, 0), expected[1]);
        Assert.Equal(2, instances.Length);
        foreach (var instance in instances)
        {
            var tip = Vector3.Transform(new Vector3(2, 0, 0), _instances.Palettes[instance.FirstJoint + 1]);
            Assert.True(Vector3.Distance(want, tip) < 1e-4f, $"{tip} against {want}");
        }

        Assert.Equal((0f, 1f), (animator.PreviousBlendWeight, animator.BlendWeight));
    }

    [Fact]
    public void A_shifted_blend_poses_its_clip_that_share_further_through_its_length()
    {
        using var world = World.Create();
        using var system = Create(world);
        var duration = _assets[2].Rig!.Clips[0].Duration;
        // The clip at a fifth of its length, itself blended in whole a quarter further on: the clip at 0.45 of it.
        var animator = MeshAnimator.Playing(0, seconds: 0.2f * duration, speed: 0);
        animator.SetBlend(0, 1, 0.25f);
        animator.SetBlend(0, 1, 0.25f);
        world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(2), animator);

        Frame(system, 1);

        var drawn = DrawnTip(0);
        Assert.True(Vector3.Distance(drawn, Tip(0, 0.45f * duration)) < 1e-4f, $"Drawn at {drawn}.");
        Assert.True(Vector3.Distance(drawn, Tip(0, 0.2f * duration)) > 1e-3f, "The shift moved nothing.");
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
    [InlineData("meshes place box at 1 2 3 90")]
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
        Assert.Equal([MeshPosePurpose.Drawing], shaper.Purposes);
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
    public void A_hidden_mesh_posed_or_not_and_what_is_attached_to_it_are_not_drawn_and_drawn_again_once_shown()
    {
        using var world = World.Create();
        using var system = Create(world);
        var still = world.Create(MeshPlacement.At(Vector3.Zero, Quaternion.Identity), DrawnMesh.Of(1));
        var posed = world.Create(
            MeshPlacement.At(new Vector3(3, 0, 0), Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f)
        );
        var shaped = world.Create(
            MeshPlacement.At(new Vector3(6, 0, 0), Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f),
            new ShapedPose()
        );
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(posed, 1, Vector3.Zero, Quaternion.Identity));
        foreach (var entity in (Entity[])[still, posed, shaped])
        {
            world.Get<DrawnMesh>(entity).Shadow = ModelShadow.Hidden;
        }

        system.Update(new FrameTime(1f / 240, 1, 1, 1));
        var hidden = _instances.Count;
        _instances.Clear();
        foreach (var entity in (Entity[])[still, posed, shaped])
        {
            world.Get<DrawnMesh>(entity).Shadow = ModelShadow.Casts;
        }

        system.Update(new FrameTime(1f / 240, 2, 2, 1));

        Assert.Equal(0, hidden);
        Assert.Equal(4, _instances.Count);
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
    public void A_smoothed_change_of_clip_starts_where_the_last_pose_was_and_settles_into_the_new_one()
    {
        using var world = World.Create();
        using var system = Create(world);
        // Two meshes held at the bend's 0.4 s, one smoothed; then each changes at once to the rest pose.
        var smoothed = world.Create(
            MeshPlacement.At(Vector3.Zero, Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f, speed: 0),
            SmoothedPose.Of(0.2f)
        );
        var plain = world.Create(
            MeshPlacement.At(new Vector3(10, 0, 0), Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f, speed: 0)
        );
        var (bent, rest) = (Tip(0, 0.4f), Tip(-1, 0));
        Frame(system, 1);
        world.Get<MeshAnimator>(smoothed).FadeTo(-1, 0);
        world.Get<MeshAnimator>(plain).FadeTo(-1, 0);

        Frame(system, 2);
        var (smoothedAfter, plainAfter) = (DrawnTip(0), DrawnTip(10));
        var settled = Vector3.Zero;
        for (ulong frame = 3; frame < 63; frame++)
        {
            Frame(system, frame);
            settled = DrawnTip(0);
        }

        // A frame after the change the smoothed tip is nearly where it was; the plain one is at the rest pose at once.
        Assert.True(Share(smoothedAfter, bent, rest) > 0.9f, $"At {Share(smoothedAfter, bent, rest)} of the way back.");
        Assert.True(Vector3.Distance(plainAfter, rest) < 1e-4f, $"Plain at {plainAfter}.");
        // A second later it is the rest pose.
        Assert.True(Vector3.Distance(settled, rest) < 1e-3f, $"Settled at {settled}, the rest pose's tip at {rest}.");
        Assert.Equal(1, system.Smoothing.Count);
    }

    [Fact]
    public void A_marked_switch_of_the_shaping_is_smoothed_as_a_change_of_clip_is()
    {
        using var world = World.Create();
        var shaper = new TurningShaper { Keep = false };
        using var system = Create(world, shaper: shaper);
        var animator = MeshAnimator.Playing(-1, 0, speed: 0);
        var mesh = world.Create(
            MeshPlacement.At(Vector3.Zero, Quaternion.Identity),
            DrawnMesh.Of(2),
            animator,
            new ShapedPose(),
            SmoothedPose.Of(0.2f)
        );
        Frame(system, 1);
        var turned = DrawnTip(0);
        // The shaper's turn switched off at once, and marked.
        shaper.Turning = false;
        world.Get<MeshAnimator>(mesh).MarkChange();
        Frame(system, 2);
        var after = DrawnTip(0);
        for (ulong frame = 3; frame < 63; frame++)
        {
            Frame(system, frame);
        }

        var rest = Tip(-1, 0);
        Assert.True(Share(after, turned, rest) > 0.9f, $"At {Share(after, turned, rest)} of the way back.");
        Assert.True(Vector3.Distance(DrawnTip(0), rest) < 1e-3f, $"Settled at {DrawnTip(0)}.");
    }

    [Fact]
    public void A_new_entity_in_a_gone_ones_slot_starts_afresh_and_what_a_smoothed_mesh_holds_follows_it()
    {
        using var world = World.Create();
        using var system = Create(world);
        var gone = world.Create(
            MeshPlacement.At(Vector3.Zero, Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f, speed: 0),
            SmoothedPose.Of()
        );
        Frame(system, 1);
        var slot = world.Get<SmoothedPose>(gone).Slot;
        world.Destroy(gone);
        Frame(system, 2);
        // A new mesh at the rest pose, given the gone one's slot, and holding a box at its tip.
        var holder = world.Create(
            MeshPlacement.At(Vector3.Zero, Quaternion.Identity),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(-1, 0, speed: 0),
            new SmoothedPose { Seconds = 0.2f, Slot = slot }
        );
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(holder, 1, Vector3.Zero, Quaternion.Identity));
        Frame(system, 3);
        var fresh = DrawnTip(0);
        // Then a change to the bend: a frame later the box is still at the smoothed joint, not the bend's.
        world.Get<MeshAnimator>(holder).FadeTo(0, 0, 0.4f, 0);
        Frame(system, 4);

        var rig = _assets[2].Rig!;
        var joint = _instances.Palettes[_instances.Instances.ToArray().Single(i => i.Asset == 2).FirstJoint + 1];
        var box = _instances.Instances.ToArray().Single(i => i.Asset == 0).Transform.Translation;
        var bind = Matrix4x4.Invert(rig.InverseBinds[1], out var bound) ? bound : Matrix4x4.Identity;
        var drawnJoint = (bind * joint).Translation;
        Assert.True(Vector3.Distance(fresh, Tip(-1, 0)) < 1e-4f, $"The new mesh started at {fresh}.");
        Assert.True(Vector3.Distance(box, drawnJoint) < 1e-4f, $"The box at {box}, the drawn joint at {drawnJoint}.");
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
        var smoothed = MeshAnimator.Playing(0, 0.3f);
        smoothed.FadeTo(-1, 0);
        world.Create(
            MeshPlacement.At(Vector3.UnitY, Quaternion.Identity),
            DrawnMesh.Of(2),
            smoothed,
            SmoothedPose.Of()
        );

        // The list is cleared between frames by the loop; here every update adds six, far below the limit.
        Assert.Equal(
            0,
            SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 1, 0.5f), warmup: 10, measured: 100)
        );
        Assert.Equal(660, _instances.Count);
    }

    [Fact]
    public void With_culling_a_mesh_in_view_is_posed_and_one_behind_the_camera_outside_every_cascade_is_not()
    {
        using var world = World.Create();
        var harness = new PoseCullingHarness();
        using var system = Create(world, culling: harness.Culling);
        world.Create(Placed(0, 0, -10), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.4f));
        world.Create(Placed(0, 0, 60), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.4f));

        CullingFrame(system, harness, 1, PoseCullingHarness.Ahead);

        var instance = Assert.Single(_instances.Instances.ToArray());
        Assert.Equal(-10, instance.Transform.Translation.Z, 1e-4f);
        Assert.Equal(instance.Joints, _instances.PaletteJoints);
        Assert.Equal((1, 1), (system.LastPosed, system.LastCulled));
    }

    [Fact]
    public void With_culling_a_mesh_out_of_view_is_posed_while_it_casts_into_a_cascade()
    {
        using var world = World.Create();
        var harness = new PoseCullingHarness();
        using var system = Create(world, culling: harness.Culling);
        // 40 m up, above the view, between the sun and the ground ahead; one that casts and one that does not.
        world.Create(Placed(0, 40, -20), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.4f));
        world.Create(
            Placed(0, 40, -20),
            DrawnMesh.Of(2, MeshInstances.NoTint, ModelShadow.None),
            MeshAnimator.Playing(0, 0.4f)
        );

        CullingFrame(system, harness, 1, PoseCullingHarness.Ahead);
        var casting = _instances.Instances.ToArray();
        harness.View.Shadows = false;
        CullingFrame(system, harness, 2, PoseCullingHarness.Ahead);

        Assert.Equal(ModelShadow.Casts, Assert.Single(casting).Shadow);
        Assert.True(Assert.Single(casting).Posed);
        Assert.Equal(0, _instances.Count);
    }

    [Fact]
    public void Without_a_camera_set_this_frame_every_mesh_is_posed()
    {
        using var world = World.Create();
        var harness = new PoseCullingHarness();
        using var system = Create(world, culling: harness.Culling);
        world.Create(Placed(0, 0, 60), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.4f));
        harness.View.BeginFrame(
            PoseCullingHarness.Width,
            PoseCullingHarness.Height,
            PoseCullingHarness.Distance,
            default
        );

        system.Update(new FrameTime(1f / 60, 0, 1, 1));

        Assert.True(Assert.Single(_instances.Instances.ToArray()).Posed);
        Assert.Equal((1, 0), (system.LastPosed, system.LastCulled));
    }

    [Fact]
    public void With_culling_what_a_culled_holder_holds_is_left_out_and_shaped_holders_and_holders_of_drawn_meshes_are_posed()
    {
        using var world = World.Create();
        var harness = new PoseCullingHarness(shadows: false);
        var shaper = new TurningShaper();
        using var system = Create(world, shaper: shaper, culling: harness.Culling);
        var behind = world.Create(Placed(0, 0, 60), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.4f));
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(behind, 1, Vector3.Zero, Quaternion.Identity));
        // Shaped, so posed wherever it is: a game's shaper may move its joints anywhere.
        var shaped = world.Create(Placed(5, 0, 60), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.4f), new ShapedPose());
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(shaped, 1, Vector3.Zero, Quaternion.Identity));
        // Beside the view, holding a box 30 m past its tip, which is in view.
        var beside = world.Create(Placed(-40, 0, -10), DrawnMesh.Of(2), MeshAnimator.Playing(-1, 0, speed: 0));
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(beside, 1, new Vector3(30, 0, 0), Quaternion.Identity));

        CullingFrame(system, harness, 1, PoseCullingHarness.Ahead);

        Assert.NotEmpty(shaper.Shaped);
        Assert.All(shaper.Shaped, entity => Assert.Equal(shaped, entity));
        Assert.Equal((2, 1), (system.LastPosed, system.LastCulled));
        var instances = _instances.Instances.ToArray();
        Assert.Equal(4, instances.Length);
        Assert.Equal([-40f, 5f], instances.Where(i => i.Asset == 2).Select(i => i.Transform.Translation.X).Order());
        Assert.Equal(
            [-9f, 6f],
            instances.Where(i => i.Asset == 0).Select(i => MathF.Round(i.Transform.Translation.X, 3)).Order()
        );
    }

    /// <summary>
    /// A smoothed mesh changes clip in view, is culled for eight frames with the camera turned away, and comes back: its
    /// slot is kept, and it is drawn as its twin, posed every frame without culling, is.
    /// </summary>
    [Fact]
    public void With_culling_a_culled_smoothed_mesh_keeps_its_change_going_and_returns_as_if_drawn_throughout()
    {
        using var world = World.Create();
        using var twinWorld = World.Create();
        var harness = new PoseCullingHarness(shadows: false);
        using var system = Create(world, culling: harness.Culling);
        var twinInstances = new MeshInstances();
        using var twinSystem = new MeshDrawSystem(twinWorld, twinInstances, _assets);
        twinSystem.Initialize();
        var mesh = world.Create(
            Placed(0, 0, -10),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f, speed: 0),
            SmoothedPose.Of(0.5f)
        );
        var twin = twinWorld.Create(
            Placed(0, 0, -10),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f, speed: 0),
            SmoothedPose.Of(0.5f)
        );
        void Both(ulong frame, CameraView camera)
        {
            CullingFrame(system, harness, frame, camera);
            twinInstances.Clear();
            twinSystem.Update(new FrameTime(1f / 60, frame / 60.0, frame, 1));
        }

        Both(1, PoseCullingHarness.Ahead);
        world.Get<MeshAnimator>(mesh).FadeTo(-1, 0);
        twinWorld.Get<MeshAnimator>(twin).FadeTo(-1, 0);
        Both(2, PoseCullingHarness.Ahead);
        var slot = world.Get<SmoothedPose>(mesh).Slot;
        var culled = 0;
        for (ulong frame = 3; frame < 11; frame++)
        {
            Both(frame, PoseCullingHarness.Behind);
            culled += system.LastCulled;
        }

        var kept = system.Smoothing.Count;
        Both(11, PoseCullingHarness.Ahead);

        var drawn = Vector3.Transform(new Vector3(2, 0, 0), _instances.Palettes[1]);
        var expected = Vector3.Transform(new Vector3(2, 0, 0), twinInstances.Palettes[1]);
        Assert.Equal((8, 1, slot), (culled, kept, world.Get<SmoothedPose>(mesh).Slot));
        Assert.True(Vector3.Distance(drawn, expected) < 1e-5f, $"Drawn at {drawn}, its twin at {expected}.");
        // Still part way through the change, so the test sees the change going on.
        var share = Share(expected, Tip(0, 0.4f), Tip(-1, 0));
        Assert.InRange(share, 0.05f, 0.95f);
    }

    /// <summary>
    /// A smoothed mesh is culled, changes clip while culled, and comes back: the change started from the pose last drawn
    /// and is blending in, as its twin, posed every frame without culling, shows it.
    /// </summary>
    [Fact]
    public void With_culling_a_change_marked_while_culled_blends_in_when_the_mesh_returns()
    {
        using var world = World.Create();
        using var twinWorld = World.Create();
        var harness = new PoseCullingHarness(shadows: false);
        using var system = Create(world, culling: harness.Culling);
        var twinInstances = new MeshInstances();
        using var twinSystem = new MeshDrawSystem(twinWorld, twinInstances, _assets);
        twinSystem.Initialize();
        var mesh = world.Create(
            Placed(0, 0, -10),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f, speed: 0),
            SmoothedPose.Of(0.5f)
        );
        var twin = twinWorld.Create(
            Placed(0, 0, -10),
            DrawnMesh.Of(2),
            MeshAnimator.Playing(0, 0.4f, speed: 0),
            SmoothedPose.Of(0.5f)
        );
        void Both(ulong frame, CameraView camera)
        {
            CullingFrame(system, harness, frame, camera);
            twinInstances.Clear();
            twinSystem.Update(new FrameTime(1f / 60, frame / 60.0, frame, 1));
        }

        Both(1, PoseCullingHarness.Ahead);
        Both(2, PoseCullingHarness.Behind);
        world.Get<MeshAnimator>(mesh).FadeTo(-1, 0);
        twinWorld.Get<MeshAnimator>(twin).FadeTo(-1, 0);
        for (ulong frame = 3; frame < 6; frame++)
        {
            Both(frame, PoseCullingHarness.Behind);
        }

        Both(6, PoseCullingHarness.Ahead);

        var drawn = Vector3.Transform(new Vector3(2, 0, 0), _instances.Palettes[1]);
        var expected = Vector3.Transform(new Vector3(2, 0, 0), twinInstances.Palettes[1]);
        Assert.True(Vector3.Distance(drawn, expected) < 1e-5f, $"Drawn at {drawn}, its twin at {expected}.");
        // Still near the pose last drawn, not the new clip's at once.
        Assert.True(Share(drawn, Tip(0, 0.4f), Tip(-1, 0)) > 0.5f, $"Drawn at {drawn}.");
    }

    [Fact]
    public void Drawing_with_culling_allocates_nothing()
    {
        using var world = World.Create();
        var harness = new PoseCullingHarness();
        using var system = Create(world, shaper: new TurningShaper { Keep = false }, culling: harness.Culling);
        var holder = world.Create(Placed(0, 0, -10), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.2f), new ShapedPose());
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(holder, 1, Vector3.UnitY, Quaternion.Identity));
        var behind = world.Create(Placed(0, 0, 60), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.2f));
        world.Create(DrawnMesh.Of(0), MeshAttachment.To(behind, 1, Vector3.UnitY, Quaternion.Identity));
        var smoothed = MeshAnimator.Playing(0, 0.3f);
        smoothed.FadeTo(-1, 0);
        world.Create(Placed(0, 0, 70), DrawnMesh.Of(2), smoothed, SmoothedPose.Of());
        world.Create(Placed(0, 2, -12), DrawnMesh.Of(2), MeshAnimator.Playing(0, 0.3f), SmoothedPose.Of());
        harness.BeginFrame(PoseCullingHarness.Ahead);

        // The list is cleared between frames by the loop; here every update adds three, far below the limit.
        Assert.Equal(
            0,
            SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 1, 0.5f), warmup: 10, measured: 100)
        );
        Assert.Equal((2, 2), (system.LastPosed, system.LastCulled));
        Assert.Equal(330, _instances.Count);
    }

    /// <summary>A placement at a point, unturned.</summary>
    private static MeshPlacement Placed(float x, float y, float z) =>
        MeshPlacement.At(new Vector3(x, y, z), Quaternion.Identity);

    /// <summary>A frame as the loop runs it with a view: the instances cleared, the frame started, the camera set.</summary>
    private void CullingFrame(MeshDrawSystem system, PoseCullingHarness harness, ulong frame, CameraView camera)
    {
        _instances.Clear();
        harness.BeginFrame(in camera);
        system.Update(new FrameTime(1f / 60, frame / 60.0, frame, 1));
    }

    private static ConsoleCommands NewCommands() =>
        new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);

    /// <summary>A shaper that turns joint 1 a quarter about +z, and keeps the entities it shaped.</summary>
    private sealed class TurningShaper : IMeshPoseShaper
    {
        public static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

        public List<Entity> Shaped { get; } = [];

        public List<MeshPosePurpose> Purposes { get; } = [];

        public bool Keep { get; init; } = true;

        /// <summary>False: the pose is left as sampled.</summary>
        public bool Turning { get; set; } = true;

        public void Shape(
            World world,
            Entity entity,
            Rig rig,
            Span<JointTransform> locals,
            Span<Matrix4x4> globals,
            float alpha,
            MeshPosePurpose purpose
        )
        {
            if (Keep)
            {
                Shaped.Add(entity);
                Purposes.Add(purpose);
            }

            if (Turning)
            {
                rig.Turn(locals, 1, Turn, globals);
            }
        }
    }

    /// <summary>A frame of the system at 60 a second, numbered <paramref name="frame"/>, the instances cleared first.</summary>
    private void Frame(MeshDrawSystem system, ulong frame)
    {
        _instances.Clear();
        system.Update(new FrameTime(1f / 60, frame / 60.0, frame, 1));
    }

    /// <summary>Where the bending mesh's tip is in clip <paramref name="clip"/> at <paramref name="seconds"/>, placed at the origin.</summary>
    private Vector3 Tip(int clip, float seconds)
    {
        var rig = _assets[2].Rig!;
        var locals = new JointTransform[2];
        var palette = new Matrix4x4[2];
        rig.Pose(clip, seconds, locals);
        rig.WritePalette(locals, palette);
        return Vector3.Transform(new Vector3(2, 0, 0), palette[1]);
    }

    /// <summary>The drawn tip, in the model's space, of the bending mesh placed at x = <paramref name="x"/>.</summary>
    private Vector3 DrawnTip(float x)
    {
        var instance = _instances
            .Instances.ToArray()
            .Single(i => i.Asset == 2 && MathF.Abs(i.Transform.Translation.X - x) < 1e-3f);
        return Vector3.Transform(new Vector3(2, 0, 0), _instances.Palettes[instance.FirstJoint + 1]);
    }

    /// <summary>How far <paramref name="at"/> lies from <paramref name="to"/> back towards <paramref name="from"/>, 0 to 1.</summary>
    private static float Share(Vector3 at, Vector3 from, Vector3 to) =>
        Vector3.Distance(at, to) / MathF.Max(Vector3.Distance(from, to), 1e-6f);

    private MeshDrawSystem Create(
        World world,
        ConsoleCommands? commands = null,
        IMeshPoseShaper? shaper = null,
        MeshPoseCulling? culling = null
    )
    {
        var system = new MeshDrawSystem(
            world,
            _instances,
            _assets,
            commands: commands,
            shaper: shaper,
            culling: culling
        );
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
