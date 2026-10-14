using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// Shaping a rig's pose before it is drawn (<see cref="Rig"/>), on a rig made here: a chain of three joints a metre apart
/// up from a root, and a branch off the root along +x. A joint found by name; the joints' places in the asset's space,
/// which the palette is made of; a mask over a joint and every joint below it; two poses blended joint by joint; a joint
/// turned in the asset's space under a turned parent, its children swinging round it; a joint moved in the asset's
/// space under a turned and scaled parent, its children with it; two-bone IK reaching a point, and
/// stretching towards one out of reach, bent towards its pole; and no allocation.
/// </summary>
public sealed class RigShapingTests
{
    private static readonly Rig s_rig = Chain();

    [Fact]
    public void A_joint_is_found_by_name_and_the_globals_are_the_palettes_places()
    {
        var locals = Rest();
        var globals = new Matrix4x4[5];
        var palette = new Matrix4x4[5];

        s_rig.Globals(locals, globals);
        s_rig.WritePalette(locals, palette);

        Assert.Equal(2, s_rig.FindJoint("lower"));
        Assert.Equal(-1, s_rig.FindJoint("tail"));
        Assert.Equal(new Vector3(0, 3, 0), globals[3].Translation);
        Assert.Equal(new Vector3(1, 0, 0), globals[4].Translation);
        // The inverse binds are the identity here, so the palette is the globals.
        Assert.Equal(globals, palette);
    }

    [Fact]
    public void A_mask_covers_a_joint_and_every_joint_below_it_and_a_blend_follows_its_weights()
    {
        var weights = new float[5];
        weights[4] = 0.25f;

        s_rig.Mask(1, weights, 1);

        Assert.Equal([0, 1, 1, 1, 0.25f], weights);
        var under = Rest();
        var over = Rest();
        over[2] = over[2] with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) };
        over[0] = over[0] with { Translation = new Vector3(5, 0, 0) };
        var into = new JointTransform[5];
        Rig.Blend(under, over, weights, into);
        Assert.Equal(over[2], into[2]);
        Assert.Equal(under[0], into[0]);

        Rig.Blend(under, over, [0, 0, 0.5f, 0, 0], into);
        var angle = 2 * MathF.Acos(into[2].Rotation.W);
        Assert.Equal(MathF.PI / 4, angle, 1e-5f);
    }

    [Fact]
    public void A_turn_in_the_assets_space_swings_the_children_round_the_joint_under_a_turned_parent()
    {
        var locals = Rest();
        // The root turned a quarter about +y, so the joints' own frames are not the asset's.
        locals[0] = locals[0] with
        {
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
        };
        var globals = new Matrix4x4[5];
        s_rig.Globals(locals, globals);
        var pivot = globals[1].Translation;
        var tip = globals[3].Translation;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

        s_rig.Turn(locals, 1, turn, globals);

        s_rig.Globals(locals, globals);
        Assert.True(Vector3.Distance(pivot, globals[1].Translation) < 1e-5f, $"{globals[1].Translation}");
        var expected = pivot + Vector3.Transform(tip - pivot, turn);
        Assert.True(Vector3.Distance(expected, globals[3].Translation) < 1e-5f, $"{globals[3].Translation}");
        // The branch off the root stays.
        Assert.Equal(new Vector3(0, 0, -1), Round(globals[4].Translation));
    }

    [Fact]
    public void A_move_in_the_assets_space_shifts_the_joint_and_its_children_under_a_turned_parent()
    {
        var locals = Rest();
        // The root turned a quarter about +y and doubled in size, so the joints' own frames are not the asset's.
        locals[0] = locals[0] with
        {
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            Scale = new Vector3(2),
        };
        var globals = new Matrix4x4[5];
        s_rig.Globals(locals, globals);
        var (joint, tip, branch) = (globals[1].Translation, globals[3].Translation, globals[4].Translation);
        var offset = new Vector3(0.3f, -0.2f, 0.1f);

        s_rig.Move(locals, 1, offset, globals);

        s_rig.Globals(locals, globals);
        Assert.True(Vector3.Distance(joint + offset, globals[1].Translation) < 1e-5f, $"{globals[1].Translation}");
        Assert.True(Vector3.Distance(tip + offset, globals[3].Translation) < 1e-5f, $"{globals[3].Translation}");
        Assert.True(Vector3.Distance(branch, globals[4].Translation) < 1e-5f, $"{globals[4].Translation}");
    }

    [Fact]
    public void Two_bones_reach_a_point_bent_to_the_pole_and_stretch_towards_one_out_of_reach()
    {
        var locals = Rest();
        var globals = new Matrix4x4[5];
        var target = new Vector3(1, 2, 0);
        var pole = new Vector3(0, 0, 5);

        Assert.True(s_rig.ReachTwoBone(locals, 1, 2, 3, target, pole, globals));

        s_rig.Globals(locals, globals);
        Assert.True(Vector3.Distance(target, globals[3].Translation) < 1e-3f, $"{globals[3].Translation}");
        Assert.True(
            globals[2].Translation.Z > 0.1f,
            $"The middle joint at {globals[2].Translation}, not towards the pole."
        );
        Assert.Equal(1, Vector3.Distance(globals[1].Translation, globals[2].Translation), 1e-4f);
        Assert.Equal(1, Vector3.Distance(globals[2].Translation, globals[3].Translation), 1e-4f);

        var far = new Vector3(0, 5, 3);
        Assert.True(s_rig.ReachTwoBone(locals, 1, 2, 3, far, pole, globals));
        s_rig.Globals(locals, globals);
        var toward = Vector3.Normalize(far - globals[1].Translation);
        var reached = globals[3].Translation - globals[1].Translation;
        Assert.Equal(2, reached.Length(), 1e-3f);
        Assert.True(Vector3.Dot(Vector3.Normalize(reached), toward) > 0.9999f, $"{globals[3].Translation}");

        Assert.False(s_rig.ReachTwoBone(locals, 1, 3, 2, target, pole, globals));
    }

    [Fact]
    public void Turning_moving_and_reaching_allocate_nothing()
    {
        var locals = Rest();
        var globals = new Matrix4x4[5];
        var weights = new float[5];
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f);
        s_rig.Turn(locals, 2, turn, globals);
        s_rig.ReachTwoBone(locals, 1, 2, 3, new Vector3(0.5f, 2, 0), Vector3.UnitZ, globals);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            s_rig.Turn(locals, 2, turn, globals);
            s_rig.ReachTwoBone(locals, 1, 2, 3, new Vector3(0.5f, 2, i * 0.001f), Vector3.UnitZ, globals);
            s_rig.Move(locals, 1, new Vector3(0, -0.001f, 0), globals);
            Rig.Blend(locals, locals, weights, locals);
            s_rig.Mask(1, weights, 1);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>Root at the origin; upper, lower, and end a metre apart up +y; a branch off the root a metre along +x.</summary>
    private static Rig Chain()
    {
        var rest = Rest();
        return new Rig(
            ["root", "upper", "lower", "end", "branch"],
            [-1, 0, 1, 2, 0],
            [.. Enumerable.Repeat(Matrix4x4.Identity, 5)],
            rest,
            [.. Enumerable.Repeat(Matrix4x4.Identity, 5)],
            [0, 0, 0, 0, 0],
            []
        );
    }

    private static JointTransform[] Rest() =>
        [
            JointTransform.Identity,
            JointTransform.Identity with
            {
                Translation = new Vector3(0, 1, 0),
            },
            JointTransform.Identity with
            {
                Translation = new Vector3(0, 1, 0),
            },
            JointTransform.Identity with
            {
                Translation = new Vector3(0, 1, 0),
            },
            JointTransform.Identity with
            {
                Translation = new Vector3(1, 0, 0),
            },
        ];

    private static Vector3 Round(Vector3 value) =>
        new(MathF.Round(value.X, 4), MathF.Round(value.Y, 4), MathF.Round(value.Z, 4));
}
