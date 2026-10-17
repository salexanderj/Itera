using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// Shaping a rig's pose before it is drawn (<see cref="Rig"/>), on a rig made here: a chain of three joints a metre apart
/// up from a root, and a branch off the root along +x. A joint found by name; the joints' places in the asset's space,
/// which the palette is made of; a mask over a joint and every joint below it; two poses blended joint by joint; a joint
/// turned in the asset's space under a turned parent, its children swinging round it; a joint moved in the asset's
/// space under a turned and scaled parent, its children with it; reaching where the end already is, or a hair off it,
/// leaving the pose finite; joints placed at given transforms in one pass, the
/// others following their parents; two-bone IK reaching a point, and
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
    public void Reaching_where_the_end_already_is_or_nearly_leaves_the_pose_finite_and_in_place()
    {
        // Bent at random, each chain reaches where its end already is, or a hair off it: a turn from two ways that round
        // to one, which once came out not finite.
        var random = new Random(11);
        var globals = new Matrix4x4[5];
        for (var trial = 0; trial < 5_000; trial++)
        {
            var pose = Rest();
            for (var joint = 1; joint <= 3; joint++)
            {
                var axis = Vector3.Normalize(
                    new Vector3(
                        (float)random.NextDouble() - 0.5f,
                        (float)random.NextDouble() - 0.5f,
                        (float)random.NextDouble() - 0.5f
                    )
                );
                pose[joint] = pose[joint] with
                {
                    Rotation = Quaternion.CreateFromAxisAngle(axis, (float)random.NextDouble() * 2),
                };
            }

            s_rig.Globals(pose, globals);
            var end = globals[3].Translation;
            var off = (float)(random.NextDouble() - 0.5) * 2e-6f * (trial % 2);
            var pole = globals[2].Translation + new Vector3(0, 0, 0.5f);

            s_rig.ReachTwoBone(pose, 1, 2, 3, end + new Vector3(off, -off, off), pole, globals);

            s_rig.Globals(pose, globals);
            Assert.True(
                Vector3.Distance(end, globals[3].Translation) < 1e-3f,
                $"Trial {trial}: reaching its own place put the end at {globals[3].Translation}, from {end}."
            );
        }
    }

    [Fact]
    public void Placed_joints_take_their_transforms_and_the_others_follow_their_parents()
    {
        var locals = Rest();
        locals[0] = locals[0] with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) };
        var globals = new Matrix4x4[5];
        s_rig.Globals(locals, globals);
        var upper = globals[1];
        var middle = globals[2];
        var placed = new Matrix4x4[5];
        bool[] placing = [false, true, false, true, false];
        // The upper joint lifted and turned; the end set apart where it is put.
        placed[1] =
            Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f))
            * Matrix4x4.CreateTranslation(new Vector3(0.2f, 1.5f, -0.3f));
        placed[3] = Matrix4x4.CreateTranslation(new Vector3(2, 0.5f, 1));

        s_rig.Place(locals, placed, placing, globals);

        var placedGlobals = (Matrix4x4[])globals.Clone();
        s_rig.Globals(locals, globals);
        Assert.Equal(placedGlobals, globals);
        Assert.True(Vector3.Distance(placed[1].Translation, globals[1].Translation) < 1e-5f);
        Assert.True(Vector3.Distance(placed[3].Translation, globals[3].Translation) < 1e-5f);
        // The middle joint keeps its place under the upper joint as it was.
        var before = middle * Inverse(upper);
        var after = globals[2] * Inverse(globals[1]);
        Assert.True(Vector3.Distance(before.Translation, after.Translation) < 1e-5f);
    }

    private static Matrix4x4 Inverse(Matrix4x4 matrix) =>
        Matrix4x4.Invert(matrix, out var inverse) ? inverse : throw new InvalidOperationException("Not invertible.");

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
    public void Turning_moving_placing_and_reaching_allocate_nothing()
    {
        var locals = Rest();
        var globals = new Matrix4x4[5];
        var weights = new float[5];
        var placed = new Matrix4x4[5];
        placed[3] = Matrix4x4.CreateTranslation(1, 1, 0);
        bool[] placing = [false, false, false, true, false];
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f);
        s_rig.Turn(locals, 2, turn, globals);
        s_rig.ReachTwoBone(locals, 1, 2, 3, new Vector3(0.5f, 2, 0), Vector3.UnitZ, globals);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            s_rig.Turn(locals, 2, turn, globals);
            s_rig.ReachTwoBone(locals, 1, 2, 3, new Vector3(0.5f, 2, i * 0.001f), Vector3.UnitZ, globals);
            s_rig.Move(locals, 1, new Vector3(0, -0.001f, 0), globals);
            s_rig.Place(locals, placed, placing, globals);
            Rig.Blend(locals, locals, weights, locals);
            s_rig.Mask(1, weights, 1);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>
    /// The tools work out only the globals they need (the joints up to the one they shape), and shape any pose exactly as
    /// when they worked out every joint's (<see cref="EveryJoint"/>, their code before): the same bits.
    /// </summary>
    [Fact]
    public void Turning_moving_and_reaching_shape_exactly_as_with_every_joints_globals()
    {
        var random = new Random(7);
        var globals = new Matrix4x4[5];
        for (var trial = 0; trial < 300; trial++)
        {
            var pose = Rest();
            for (var j = 0; j < pose.Length; j++)
            {
                pose[j] = pose[j] with { Rotation = Rotation(random) };
            }

            var joint = random.Next(5);
            var (shaped, before) = (pose.ToArray(), pose.ToArray());
            switch (trial % 3)
            {
                case 0:
                    var turn = Rotation(random);
                    s_rig.Turn(shaped, joint, turn, globals);
                    EveryJoint.Turn(s_rig, before, joint, turn, globals);
                    break;
                case 1:
                    var offset = Point(random);
                    s_rig.Move(shaped, joint, offset, globals);
                    EveryJoint.Move(s_rig, before, joint, offset, globals);
                    break;
                default:
                    var (target, pole) = (Point(random), Point(random));
                    Assert.Equal(
                        EveryJoint.ReachTwoBone(s_rig, before, 1, 2, 3, target, pole, globals),
                        s_rig.ReachTwoBone(shaped, 1, 2, 3, target, pole, globals)
                    );
                    break;
            }

            Assert.Equal(before, shaped);
        }
    }

    private static Quaternion Rotation(Random random) =>
        Quaternion.Normalize(
            new Quaternion(
                random.NextSingle() * 2 - 1,
                random.NextSingle() * 2 - 1,
                random.NextSingle() * 2 - 1,
                random.NextSingle() * 2 - 1
            )
        );

    private static Vector3 Point(Random random) =>
        new(random.NextSingle() * 4 - 2, random.NextSingle() * 4 - 2, random.NextSingle() * 4 - 2);

    /// <summary>The rig's turn, move, and two-bone reach as they were before, each working out every joint's globals.</summary>
    private static class EveryJoint
    {
        public static void Globals(Rig rig, ReadOnlySpan<JointTransform> locals, Span<Matrix4x4> globals)
        {
            for (var j = 0; j < rig.Names.Length; j++)
            {
                var global = locals[j].ToMatrix() * rig.Offsets[j];
                globals[j] = rig.Parents[j] < 0 ? global : global * globals[rig.Parents[j]];
            }
        }

        public static void Turn(
            Rig rig,
            Span<JointTransform> locals,
            int joint,
            Quaternion turn,
            Span<Matrix4x4> globals
        )
        {
            if ((uint)joint >= (uint)rig.Names.Length)
            {
                return;
            }

            Globals(rig, locals, globals);
            // The joint's global is local * frame, where frame is its offset and its parent's global. Turned in the asset's
            // space after it, the global becomes local * frame * turn, so the new local is local * frame * turn * frame⁻¹.
            var frame = rig.Parents[joint] < 0 ? rig.Offsets[joint] : rig.Offsets[joint] * globals[rig.Parents[joint]];
            if (!Matrix4x4.Decompose(frame, out _, out var frameRotation, out _))
            {
                return;
            }

            var local = locals[joint];
            // Quaternions here read right to left (the right one first): the local, then the frame, then the turn, then back.
            var rotation = Quaternion.Inverse(frameRotation) * turn * frameRotation * local.Rotation;
            locals[joint] = local with { Rotation = Quaternion.Normalize(rotation) };
        }

        public static void Move(
            Rig rig,
            Span<JointTransform> locals,
            int joint,
            Vector3 offset,
            Span<Matrix4x4> globals
        )
        {
            if ((uint)joint >= (uint)rig.Names.Length)
            {
                return;
            }

            Globals(rig, locals, globals);
            // The joint's place is its local translation through its frame (its offset and its parent's global), so a move in
            // the asset's space is the same move turned back through the frame.
            var frame = rig.Parents[joint] < 0 ? rig.Offsets[joint] : rig.Offsets[joint] * globals[rig.Parents[joint]];
            if (!Matrix4x4.Invert(frame, out var inverse))
            {
                return;
            }

            var local = locals[joint];
            locals[joint] = local with { Translation = local.Translation + Vector3.TransformNormal(offset, inverse) };
        }

        public static bool ReachTwoBone(
            Rig rig,
            Span<JointTransform> locals,
            int upper,
            int lower,
            int end,
            Vector3 target,
            Vector3 pole,
            Span<Matrix4x4> globals
        )
        {
            if (
                (uint)end >= (uint)rig.Names.Length
                || (uint)lower >= (uint)rig.Names.Length
                || (uint)upper >= (uint)rig.Names.Length
                || rig.Parents[end] != lower
                || rig.Parents[lower] != upper
            )
            {
                return false;
            }

            Globals(rig, locals, globals);
            var a = globals[upper].Translation;
            var b = globals[lower].Translation;
            var c = globals[end].Translation;
            var upperLength = Vector3.Distance(a, b);
            var lowerLength = Vector3.Distance(b, c);
            var toTarget = target - a;
            var reach = toTarget.Length();
            if (upperLength < 1e-6f || lowerLength < 1e-6f || reach < 1e-6f)
            {
                return false;
            }

            var direction = toTarget / reach;
            reach = Math.Clamp(reach, MathF.Abs(upperLength - lowerLength) + 1e-4f, upperLength + lowerLength - 1e-4f);
            // The middle joint's new place: along the target's direction by the law of cosines, out towards the pole.
            var along = (upperLength * upperLength - lowerLength * lowerLength + reach * reach) / (2 * reach);
            var across = MathF.Sqrt(MathF.Max(0, upperLength * upperLength - along * along));
            var bend = pole - a;
            bend -= direction * Vector3.Dot(bend, direction);
            if (bend.LengthSquared() < 1e-10f)
            {
                // A pole on the line: bend the way the middle joint already does.
                bend = b - a;
                bend -= direction * Vector3.Dot(bend, direction);
            }

            bend = bend.LengthSquared() > 1e-10f ? Vector3.Normalize(bend) : Perpendicular(direction);
            var middle = a + direction * along + bend * across;
            Turn(rig, locals, upper, Arc(b - a, middle - a), globals);
            Globals(rig, locals, globals);
            var bent = globals[lower].Translation;
            Turn(rig, locals, lower, Arc(globals[end].Translation - bent, a + direction * reach - bent), globals);
            return true;
        }

        private static Quaternion Arc(Vector3 from, Vector3 to)
        {
            var lengths = from.Length() * to.Length();
            if (!(lengths > 1e-12f) || !float.IsFinite(lengths))
            {
                return Quaternion.Identity;
            }

            // The axis's length is the angle's sine: two ways that are nearly one turn by nothing, or by half a turn about any
            // axis square to them when nearly opposite (normalising a cross product rounded to nothing would not be finite).
            var dot = Vector3.Dot(from, to) / lengths;
            var axis = Vector3.Cross(from, to) / lengths;
            var sin = axis.Length();
            if (!(sin > 1e-6f))
            {
                return dot >= 0
                    ? Quaternion.Identity
                    : Quaternion.CreateFromAxisAngle(Perpendicular(Vector3.Normalize(from)), MathF.PI);
            }

            return Quaternion.CreateFromAxisAngle(axis / sin, MathF.Atan2(sin, dot));
        }

        private static Vector3 Perpendicular(Vector3 direction) =>
            Vector3.Normalize(
                MathF.Abs(direction.X) < 0.9f
                    ? Vector3.Cross(direction, Vector3.UnitX)
                    : Vector3.Cross(direction, Vector3.UnitY)
            );
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
