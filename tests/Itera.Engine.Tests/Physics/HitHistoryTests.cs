using System.Numerics;
using Arch.Core;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// <see cref="HitShapes.RaycastCapsule"/> against hand-worked cases and a reference march, and
/// <see cref="HitHistory"/>: a character recorded over steps is met where it was at a fractional tick and not where it
/// is now (and the reverse); a swept sphere meets what its ray grazes by less than its radius; the shooter is ignored,
/// the nearer entity wins, the part number comes back; an entity not
/// recorded for a while is met only at ticks near its records; a move of the origin between records changes nothing in
/// absolute terms; <c>Forget</c>, <c>Clear</c>, the limits, and no allocation.
/// </summary>
public sealed class HitHistoryTests : IDisposable
{
    private const float Radius = 0.3f;
    private const float Height = 1.8f;

    private readonly World _world = World.Create();
    private readonly WorldOrigin _origin = new();
    private readonly HitHistory _history;

    public HitHistoryTests() => _history = new HitHistory(_origin);

    public void Dispose() => World.Destroy(_world);

    // --- The ray against a capsule ---

    private static readonly Vector3 s_a = Vector3.Zero;
    private static readonly Vector3 s_b = new(0, 2, 0);

    /// <summary>A posed rig's capsules: between their joints, reaching past the far one, a sphere, placed and scaled.</summary>
    [Fact]
    public void A_posed_bodys_capsules_lie_between_their_joints_placed_and_scaled()
    {
        Matrix4x4[] globals =
        [
            Matrix4x4.CreateTranslation(0, 1, 0),
            Matrix4x4.CreateTranslation(0, 2, 0),
            Matrix4x4.CreateTranslation(0, 2.5f, 0),
        ];
        HitBone[] bones = [new(0, 1, 0.1f, 5), new(2, -1, 0.2f, 7), new(1, 2, 0.05f, 9, Reach: 2), new(0, 3, 1, 1)];
        var placed = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(10, 0, 0);
        var into = new HitCapsule[4];

        var count = HitShapes.FromPose(globals, in placed, bones, into);

        Assert.Equal(3, count);
        Assert.Equal(new HitCapsule(new Vector3(10, 2, 0), new Vector3(10, 4, 0), 0.2f, 5), into[0]);
        Assert.Equal(new HitCapsule(new Vector3(10, 5, 0), new Vector3(10, 5, 0), 0.4f, 7), into[1]);
        Assert.Equal(new HitCapsule(new Vector3(10, 4, 0), new Vector3(10, 6, 0), 0.1f, 9), into[2]);
    }

    [Fact]
    public void A_ray_from_the_side_meets_the_cylinder()
    {
        Assert.True(
            HitShapes.RaycastCapsule(new(-5, 1, 0), Vector3.UnitX, s_a, s_b, 0.5f, out var distance, out var n)
        );
        Assert.Equal(4.5f, distance, 1e-5f);
        AssertNear(-Vector3.UnitX, n);
    }

    [Fact]
    public void A_ray_along_the_axis_meets_an_end_sphere_from_either_side()
    {
        Assert.True(
            HitShapes.RaycastCapsule(new(0, -5, 0), Vector3.UnitY, s_a, s_b, 0.5f, out var below, out var nBelow)
        );
        Assert.Equal(4.5f, below, 1e-5f);
        AssertNear(-Vector3.UnitY, nBelow);

        Assert.True(
            HitShapes.RaycastCapsule(new(0, 5, 0), -Vector3.UnitY, s_a, s_b, 0.5f, out var above, out var nAbove)
        );
        Assert.Equal(2.5f, above, 1e-5f);
        AssertNear(Vector3.UnitY, nAbove);
    }

    [Fact]
    public void A_ray_off_the_axis_meets_the_end_sphere_off_its_pole()
    {
        Assert.True(
            HitShapes.RaycastCapsule(new(0.3f, -5, 0), Vector3.UnitY, s_a, s_b, 0.5f, out var distance, out var n)
        );
        Assert.Equal(4.6f, distance, 1e-5f);
        AssertNear(new Vector3(0.6f, -0.8f, 0), n);

        Assert.True(
            HitShapes.RaycastCapsule(new(-5, 2.3f, 0), Vector3.UnitX, s_a, s_b, 0.5f, out var side, out var nSide)
        );
        Assert.Equal(4.6f, side, 1e-5f);
        AssertNear(new Vector3(-0.8f, 0.3f / 0.5f, 0), nSide);
    }

    [Fact]
    public void A_ray_from_inside_meets_the_capsule_at_zero_with_the_normal_against_the_ray()
    {
        var direction = Vector3.Normalize(new Vector3(1, 1, 0));
        Assert.True(HitShapes.RaycastCapsule(new(0.1f, 1, 0), direction, s_a, s_b, 0.5f, out var distance, out var n));
        Assert.Equal(0, distance);
        AssertNear(-direction, n);
    }

    [Fact]
    public void A_ray_that_passes_beside_or_points_away_misses()
    {
        Assert.False(HitShapes.RaycastCapsule(new(-5, 1, 1), Vector3.UnitX, s_a, s_b, 0.5f, out _, out _));
        Assert.False(HitShapes.RaycastCapsule(new(5, 1, 0), Vector3.UnitX, s_a, s_b, 0.5f, out _, out _));
        Assert.False(HitShapes.RaycastCapsule(new(-5, 2.6f, 0), Vector3.UnitX, s_a, s_b, 0.5f, out _, out _));
        Assert.False(HitShapes.RaycastCapsule(new(0, 5, 0), Vector3.UnitY, s_a, s_b, 0.5f, out _, out _));
    }

    [Fact]
    public void A_capsule_of_no_length_is_a_sphere()
    {
        Assert.True(HitShapes.RaycastCapsule(new(0, 0, -5), Vector3.UnitZ, s_a, s_a, 1, out var distance, out var n));
        Assert.Equal(4, distance, 1e-5f);
        AssertNear(-Vector3.UnitZ, n);

        Assert.False(HitShapes.RaycastCapsule(new(1.01f, 0, -5), Vector3.UnitZ, s_a, s_a, 1, out _, out _));
    }

    [Fact]
    public void A_ray_parallel_to_the_axis_meets_it_only_within_the_radius()
    {
        Assert.False(HitShapes.RaycastCapsule(new(2, -5, 0), Vector3.UnitY, s_a, s_b, 0.5f, out _, out _));
        Assert.False(HitShapes.RaycastCapsule(new(0, -5, 0.6f), Vector3.UnitY, s_a, s_b, 0.5f, out _, out _));
        // Beside the cylinder, on its side and parallel to it: from inside the radius it starts inside.
        Assert.True(HitShapes.RaycastCapsule(new(0.4f, 1, 0), Vector3.UnitY, s_a, s_b, 0.5f, out var distance, out _));
        Assert.Equal(0, distance);
    }

    [Fact]
    public void A_ray_agrees_with_a_march_along_it_for_random_capsules()
    {
        var random = new Random(11);
        var checkedHits = 0;
        for (var i = 0; i < 1500; i++)
        {
            var a = RandomPoint(random, 2);
            var b = i % 7 == 0 ? a : RandomPoint(random, 2);
            var radius = 0.1f + (float)random.NextDouble() * 0.6f;
            var from = RandomPoint(random, 5);
            var aim = Vector3.Lerp(a, b, (float)random.NextDouble()) + RandomPoint(random, radius * 1.5f);
            var direction = Vector3.Normalize(aim - from);

            var hit = HitShapes.RaycastCapsule(from, direction, a, b, radius, out var distance, out var normal);
            var marched = MarchTo(from, direction, a, b, radius);
            if (marched >= 0)
            {
                Assert.True(hit, $"case {i}: the march met it at {marched}");
                Assert.InRange(distance, marched - 0.006f, marched + 0.006f);
                checkedHits++;
            }

            if (hit)
            {
                var point = from + direction * distance;
                if (distance > 0)
                {
                    Assert.Equal(radius, Math.Sqrt(DistanceSquaredToSegment(point, a, b)), 1e-3);
                    Assert.True(Vector3.Dot(normal, direction) <= 1e-4f);
                }

                Assert.Equal(1, normal.Length(), 1e-3f);
            }
        }

        Assert.True(checkedHits > 100);
    }

    // --- The history ---

    [Fact]
    public void A_character_is_met_where_it_was_at_a_fractional_tick_and_not_where_it_is_now()
    {
        var entity = _world.Create();
        for (var tick = 0; tick <= 10; tick++)
        {
            Record(entity, tick, tick);
        }

        // It runs 1 m a step along x; at tick 4.5 it stands at x = 4.5, at tick 10 at x = 10.
        Assert.True(Shoot(4.5f, 4.5, out var hit));
        Assert.Equal(entity, hit.Entity);
        Assert.Equal(4.7f, hit.Distance, 1e-4f);
        AssertNear(new Vector3(4.5f, 1, -0.3f), hit.Point);
        AssertNear(-Vector3.UnitZ, hit.Normal);

        Assert.False(Shoot(4.5f, 10));
        Assert.False(Shoot(4.5f + 0.4f, 4.5));
        Assert.True(Shoot(4.5f + 0.25f, 4.5));

        // The reverse: a ray at where it is now meets it only now.
        Assert.True(Shoot(10, 10));
        Assert.False(Shoot(10, 4.5));
        Assert.True(Shoot(4.25f, 4.25));
        Assert.False(Shoot(4.25f, 5));
    }

    [Fact]
    public void The_shooter_is_ignored_and_the_nearer_of_two_in_line_wins()
    {
        var near = _world.Create();
        var far = _world.Create();
        Record(far, 1, 0, z: 3);
        Record(near, 1, 0, z: 0);

        Assert.True(Shoot(0, 1, out var first));
        Assert.True(Raycast(0, 1, near, out var second));
        Assert.Equal(near, first.Entity);
        Assert.Equal(far, second.Entity);
        Assert.Equal(4.7f, first.Distance, 1e-4f);
        Assert.Equal(7.7f, second.Distance, 1e-4f);

        // With the only entity in line ignored, nothing is met.
        _history.Forget(far);
        Assert.False(Raycast(0, 1, near, out _));
        Record(far, 1, 0, z: 3);

        // A longer range than the nearer one's distance does not change the nearer winning; a shorter one drops the far.
        Assert.True(_history.Raycast(new(0, 1, -5), Vector3.UnitZ, 5, 1, Entity.Null, out var inRange));
        Assert.Equal(near, inRange.Entity);
        Assert.False(_history.Raycast(new(0, 1, -5), Vector3.UnitZ, 4, 1, Entity.Null, out _));
        Assert.True(_history.Raycast(new(0, 1, -5), Vector3.UnitZ, 4.7f, 1, Entity.Null, out _));
    }

    [Fact]
    public void A_sphere_swept_beside_a_capsule_meets_what_a_ray_there_misses()
    {
        var entity = _world.Create();
        Record(entity, 1, 0);
        var from = new Vector3(Radius + 0.05f, 1, -5);

        Assert.False(_history.Raycast(from, Vector3.UnitZ, 20, 1, Entity.Null, out _));
        Assert.False(_history.SphereCast(from, Vector3.UnitZ, 0.04f, 20, 1, new IgnoreEntity(Entity.Null), out _));
        Assert.True(_history.SphereCast(from, Vector3.UnitZ, 0.06f, 20, 1, new IgnoreEntity(Entity.Null), out var hit));

        // Its centre stops 0.36 m from the axis; it touches the capsule's surface on the line to the centre.
        var z = -MathF.Sqrt(0.36f * 0.36f - 0.35f * 0.35f);
        Assert.Equal(entity, hit.Entity);
        Assert.Equal(5 + z, hit.Distance, 1e-4f);
        AssertNear(Vector3.Normalize(new Vector3(0.35f, 0, z)), hit.Normal);
        AssertNear(new Vector3(0.35f, 0, z) * (Radius / 0.36f) + new Vector3(0, 1, 0), hit.Point);
    }

    [Fact]
    public void A_swept_sphere_meets_a_sphere_its_ray_passes_outside_the_bounds_of()
    {
        // A capsule of no length, whose bounds are its own sphere: the ray passes outside them, the swept sphere does not.
        var entity = _world.Create();
        _history.Record(entity, 1, [new HitCapsule(new(0, 1, 0), new(0, 1, 0), 0.2f, 3)]);
        var from = new Vector3(0.25f, 1, -5);

        Assert.False(_history.Raycast(from, Vector3.UnitZ, 20, 1, Entity.Null, out _));
        Assert.True(_history.SphereCast(from, Vector3.UnitZ, 0.1f, 20, 1, new IgnoreEntity(Entity.Null), out var hit));
        Assert.Equal(3, hit.Part);
    }

    [Fact]
    public void A_sphere_of_no_radius_is_the_ray_and_a_bad_radius_meets_nothing()
    {
        var entity = _world.Create();
        Record(entity, 1, 0);
        var from = new Vector3(0.1f, 1, -5);

        Assert.True(_history.Raycast(from, Vector3.UnitZ, 20, 1, Entity.Null, out var ray));
        Assert.True(_history.SphereCast(from, Vector3.UnitZ, 0, 20, 1, new IgnoreEntity(Entity.Null), out var none));
        Assert.Equal(ray, none);
        Assert.False(_history.SphereCast(from, Vector3.UnitZ, -0.01f, 20, 1, new IgnoreEntity(Entity.Null), out _));
        Assert.False(_history.SphereCast(from, Vector3.UnitZ, float.NaN, 20, 1, new IgnoreEntity(Entity.Null), out _));
    }

    [Fact]
    public void The_part_of_the_capsule_hit_comes_back()
    {
        var entity = _world.Create();
        HitCapsule[] parts =
        [
            new(new(0, 0.3f, 0), new(0, 1.2f, 0), 0.3f, 5),
            new(new(0, 1.55f, 0), new(0, 1.55f, 0), 0.25f, 9),
        ];
        _history.Record(entity, 3, parts);

        Assert.True(_history.Raycast(new(0, 0.8f, -4), Vector3.UnitZ, 10, 3, Entity.Null, out var body));
        Assert.True(_history.Raycast(new(0, 1.6f, -4), Vector3.UnitZ, 10, 3, Entity.Null, out var head));
        Assert.Equal(5, body.Part);
        Assert.Equal(3.7f, body.Distance, 1e-4f);
        Assert.Equal(9, head.Part);
        Assert.Equal(4 - MathF.Sqrt(0.06f), head.Distance, 1e-4f);
        AssertNear(new Vector3(0, 1.6f, -MathF.Sqrt(0.06f)), head.Point);
    }

    [Fact]
    public void Capsules_recorded_in_one_step_and_not_the_other_are_used_alone()
    {
        var entity = _world.Create();
        _history.Record(
            entity,
            1,
            [new(new(0, 0, 0), new(0, 1, 0), 0.3f, 1), new(new(5, 0, 0), new(5, 1, 0), 0.3f, 2)]
        );
        _history.Record(entity, 2, [new(new(1, 0, 0), new(1, 1, 0), 0.3f, 1)]);

        // Part 1 is half way, part 2 stays where it was last recorded.
        Assert.True(_history.Raycast(new(0.5f, 0.5f, -4), Vector3.UnitZ, 10, 1.5, Entity.Null, out var moved));
        Assert.Equal(1, moved.Part);
        Assert.True(_history.Raycast(new(5, 0.5f, -4), Vector3.UnitZ, 10, 1.5, Entity.Null, out var stayed));
        Assert.Equal(2, stayed.Part);
        Assert.False(_history.Raycast(new(0, 0.5f, -4), Vector3.UnitZ, 10, 1.5, Entity.Null, out _));
        // At step 2 itself the part is not there.
        Assert.False(_history.Raycast(new(5, 0.5f, -4), Vector3.UnitZ, 10, 2, Entity.Null, out _));
        Assert.True(_history.Raycast(new(5, 0.5f, -4), Vector3.UnitZ, 10, 1, Entity.Null, out _));
    }

    /// <summary>
    /// An entity's capsules read at a tick are those <see cref="HitHistory.Raycast"/> tests: moved between the two steps
    /// round the tick, an unmatched one where its step has it, none for an entity not there or not kept, and no more than
    /// the room given; reading allocates nothing.
    /// </summary>
    [Fact]
    public void An_entitys_capsules_at_a_tick_are_those_the_ray_tests()
    {
        var entity = _world.Create();
        var other = _world.Create();
        _history.Record(
            entity,
            1,
            [new(new(0, 0, 0), new(0, 1, 0), 0.3f, 1), new(new(5, 0, 0), new(5, 1, 0), 0.2f, 2)]
        );
        _history.Record(entity, 2, [new(new(1, 0, 0), new(1, 2, 0), 0.5f, 1)]);
        Span<HitCapsule> into = stackalloc HitCapsule[4];

        var halfway = _history.CapsulesAt(entity, 1.5, into);
        var moved = into[0];
        var stayed = into[1];
        var atTwo = _history.CapsulesAt(entity, 2, into);
        var short1 = _history.CapsulesAt(entity, 1.5, into[..1]);
        var late = _history.CapsulesAt(entity, 40, into);
        var unknown = _history.CapsulesAt(other, 1.5, into);

        Assert.Equal(2, halfway);
        Assert.Equal(new HitCapsule(new(0.5f, 0, 0), new(0.5f, 1.5f, 0), 0.4f, 1), moved);
        Assert.Equal(new HitCapsule(new(5, 0, 0), new(5, 1, 0), 0.2f, 2), stayed);
        Assert.Equal(1, atTwo);
        Assert.Equal(1, short1);
        Assert.Equal((0, 0), (late, unknown));
        // What the ray meets at the tick lies on a capsule read there.
        Assert.True(_history.Raycast(new(0.5f, 0.5f, -4), Vector3.UnitZ, 10, 1.5, Entity.Null, out var hit));
        Assert.True(
            HitShapes.RaycastCapsule(
                new(0.5f, 0.5f, -4),
                Vector3.UnitZ,
                moved.A,
                moved.B,
                moved.Radius,
                out var distance,
                out _
            )
        );
        Assert.Equal(hit.Distance, distance, 1e-5f);

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            _ = _history.CapsulesAt(entity, 1.25, into);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Fact]
    public void A_ring_keeps_the_last_steps_and_no_more()
    {
        var entity = _world.Create();
        for (var tick = 0; tick < 40; tick++)
        {
            Record(entity, tick, 0);
        }

        Assert.True(Shoot(0, 39));
        Assert.True(Shoot(0, 8));
        Assert.False(Shoot(0, 3));

        // A record older than the ring holds is dropped, and does not replace a newer one.
        Record(entity, 2, 7);
        Assert.False(Shoot(7, 34));
        Assert.True(Shoot(0, 34));

        // A tick still held is replaced.
        Record(entity, 10, 7);
        Assert.True(Shoot(7, 10));
        Assert.False(Shoot(0, 10));
    }

    [Fact]
    public void An_entity_no_longer_recorded_is_met_only_at_ticks_it_was_there()
    {
        var gone = _world.Create();
        var later = _world.Create();
        for (var tick = 0; tick <= 5; tick++)
        {
            Record(gone, tick, 0);
        }

        for (var tick = 10; tick <= 20; tick++)
        {
            Record(later, tick, 0, z: 4);
        }

        // The one that stopped being recorded after step 5: met to a step past it, then gone.
        Assert.True(Raycast(0, 4, Entity.Null, out var at4));
        Assert.Equal(gone, at4.Entity);
        Assert.True(Raycast(0, 5.5, Entity.Null, out var at55));
        Assert.Equal(gone, at55.Entity);
        Assert.True(Raycast(0, 6, Entity.Null, out var at6));
        Assert.Equal(gone, at6.Entity);
        Assert.False(Raycast(0, 7, Entity.Null, out var at7) && at7.Entity == gone);
        Assert.False(Raycast(0, 15, Entity.Null, out var at15) && at15.Entity == gone);

        // The one that began at step 10: not there before, there from a step before.
        Assert.False(Raycast(0, 5, Entity.Null, out var early) && early.Entity == later);
        Assert.False(Raycast(0, 8, Entity.Null, out var at8) && at8.Entity == later);
        Assert.True(Raycast(0, 9, Entity.Null, out var at9));
        Assert.Equal(later, at9.Entity);
        Assert.True(Raycast(0, 12, Entity.Null, out var at12));
        Assert.Equal(later, at12.Entity);
    }

    [Fact]
    public void A_move_of_the_origin_between_records_changes_nothing_in_absolute_terms()
    {
        var entity = _world.Create();
        _origin.MoveTo(new VoxelCoord(100_000, 0, 0));
        Record(entity, 1, 1);

        _origin.MoveTo(new VoxelCoord(105_000, 0, 0));
        // 50 m on, step 2 puts the entity 1 m further along x: in the new frame at local x = 2 - 50.
        Record(entity, 2, 2 - 50);

        // Half way between: absolute x = 1.5 + 1000 = 1001.5, local -48.5 now.
        Assert.True(Shoot(-48.5f, 1.5, out var hit));
        var absolute = _origin.ToWorld(hit.Point);
        Assert.Equal(1001.5, absolute.X, 1e-4);
        Assert.Equal(1, absolute.Y, 1e-4);
        Assert.Equal(-0.3, absolute.Z, 1e-4);
        Assert.False(Shoot(1.5f, 1.5));
        Assert.True(Shoot(-49, 1));
        Assert.False(Shoot(-48, 1));
        Assert.True(Shoot(-48, 2));

        // And once more after moving back.
        _origin.MoveTo(new VoxelCoord(100_000, 0, 0));
        Assert.True(Shoot(1.5f, 1.5));
    }

    [Fact]
    public void A_far_origin_keeps_the_centimetres_of_a_record()
    {
        var entity = _world.Create();
        _origin.MoveTo(new VoxelCoord(15_000_000, 0, -15_000_000));
        Record(entity, 1, 0.37f);
        Assert.True(Shoot(0.37f + 0.29f, 1));
        Assert.False(Shoot(0.37f + 0.31f, 1));
    }

    [Fact]
    public void Forget_drops_one_entity_and_Clear_all_and_the_storage_is_reused()
    {
        var one = _world.Create();
        var two = _world.Create();
        Record(one, 1, 0, z: 0);
        Record(two, 1, 0, z: 3);
        Assert.Equal(2, _history.Count);

        _history.Forget(one);
        _history.Forget(one);
        _history.Forget(Entity.Null);
        Assert.Equal(1, _history.Count);
        Assert.True(Shoot(0, 1, out var hit));
        Assert.Equal(two, hit.Entity);

        // Its old records are not met by a new entity that takes its storage.
        var three = _world.Create();
        Record(three, 9, 5);
        Assert.False(Shoot(0, 1, out var after) && after.Entity == three);
        Assert.False(Shoot(5, 1));

        _history.Clear();
        Assert.Equal(0, _history.Count);
        Assert.False(Shoot(0, 1));
        Assert.False(Shoot(5, 9));
        Record(one, 1, 0);
        Assert.Equal(1, _history.Count);
        Assert.True(Shoot(0, 1));
    }

    [Fact]
    public void Bad_records_and_sizes_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HitHistory(_origin, mostEntities: 0));
        var entity = _world.Create();
        var tooMany = new HitCapsule[HitHistory.MostCapsules + 1];
        Assert.Throws<ArgumentException>(() => _history.Record(entity, 0, tooMany));
        Assert.Throws<ArgumentException>(() => _history.Record(entity, 0, [new(default, default, -0.1f, 0)]));
        Assert.Throws<ArgumentException>(() => _history.Record(entity, 0, [new(default, default, float.NaN, 0)]));
        Assert.Throws<ArgumentException>(() => _history.Record(entity, 0, [new(new(float.NaN, 0, 0), default, 1, 0)]));
        Assert.Throws<ArgumentException>(() =>
            _history.Record(entity, 0, [new(default, new(0, float.PositiveInfinity, 0), 1, 0)])
        );
        Assert.Throws<ArgumentException>(() => _history.Record(Entity.Null, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => _history.Record(entity, -1, []));
        Assert.Equal(0, _history.Count);

        _history.Record(entity, 0, new HitCapsule[HitHistory.MostCapsules]);
        Assert.Equal(1, _history.Count);
    }

    [Fact]
    public void A_new_entity_when_full_takes_the_room_of_those_not_recorded_for_its_steps()
    {
        var kept = _world.Create();
        for (var i = 1; i < _history.MostEntities; i++)
        {
            _history.Record(_world.Create(), 0, []);
        }

        _history.Record(kept, 0, []);
        _history.Record(kept, HitHistory.Steps - 1, []);
        Assert.Equal(HitHistory.DefaultMostEntities, _history.Count);

        // Within the steps every one may still be met: one recorded longest ago makes room, and the count holds.
        var late = _world.Create();
        _history.Record(late, HitHistory.Steps - 1, []);
        Assert.Equal(HitHistory.DefaultMostEntities, _history.Count);

        // Past them, those not recorded since go: the new ones and the one still recorded are kept.
        var fresh = _world.Create();
        _history.Record(fresh, HitHistory.Steps, []);
        Assert.Equal(3, _history.Count);
        _history.Record(kept, HitHistory.Steps, [new(Vector3.Zero, Vector3.UnitY, 0.5f, 0)]);
        Assert.Equal(1, _history.CapsulesAt(kept, HitHistory.Steps, new HitCapsule[HitHistory.MostCapsules]));
    }

    [Fact]
    public void A_history_of_a_games_size_keeps_that_many_and_when_full_drops_the_one_recorded_longest_ago()
    {
        var history = new HitHistory(_origin, mostEntities: 3);
        var entities = new[] { _world.Create(), _world.Create(), _world.Create(), _world.Create() };
        HitCapsule[] capsule = [new(Vector3.Zero, Vector3.UnitY, 0.5f, 0)];
        for (var i = 0; i < entities.Length; i++)
        {
            history.Record(entities[i], i, capsule);
        }

        var into = new HitCapsule[HitHistory.MostCapsules];
        var kept = entities.Select((entity, i) => history.CapsulesAt(entity, i, into)).ToArray();

        Assert.Equal(3, history.MostEntities);
        Assert.Equal(3, history.Count);
        Assert.Equal([0, 1, 1, 1], kept);
    }

    [Fact]
    public void A_ray_that_is_not_usable_meets_nothing()
    {
        var entity = _world.Create();
        Record(entity, 1, 0);

        Assert.False(_history.Raycast(new(0, 1, -5), Vector3.Zero, 10, 1, Entity.Null, out _));
        Assert.False(_history.Raycast(new(0, 1, -5), new(float.NaN, 0, 1), 10, 1, Entity.Null, out _));
        Assert.False(_history.Raycast(new(float.NaN, 1, -5), Vector3.UnitZ, 10, 1, Entity.Null, out _));
        Assert.False(_history.Raycast(new(0, 1, -5), Vector3.UnitZ, -1, 1, Entity.Null, out _));
        Assert.False(_history.Raycast(new(0, 1, -5), Vector3.UnitZ, 10, double.NaN, Entity.Null, out _));
        Assert.True(_history.Raycast(new(0, 1, -5), Vector3.UnitZ * 0.001f, 10, 1, Entity.Null, out _));
    }

    [Fact]
    public void A_ray_that_starts_inside_a_capsule_meets_it_at_zero()
    {
        var entity = _world.Create();
        Record(entity, 1, 0);

        Assert.True(_history.Raycast(new(0.05f, 1, 0), new(0, 0, 3), 10, 1, Entity.Null, out var hit));
        Assert.Equal(0, hit.Distance);
        AssertNear(-Vector3.UnitZ, hit.Normal);
        Assert.Equal(new Vector3(0.05f, 1, 0), hit.Point);
    }

    [Fact]
    public void Recording_and_asking_allocate_nothing()
    {
        var entities = new Entity[20];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = _world.Create();
        }

        var capsules = new HitCapsule[3];
        for (var tick = 0; tick < 40; tick++)
        {
            for (var i = 0; i < entities.Length; i++)
            {
                Fill(capsules, i * 2 + tick * 0.1f);
                _history.Record(entities[i], tick, capsules);
            }
        }

        // Warm up every path, then measure.
        var step = 40;
        var hits = 0;
        for (var round = 0; round < 50; round++)
        {
            Round();
        }

        var before = SystemAllocation.StartMeasurement();
        for (var round = 0; round < 1000; round++)
        {
            Round();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(hits > 0);

        void Round()
        {
            var i = step % entities.Length;
            Fill(capsules, i * 2 + step * 0.1f);
            _history.Record(entities[i], step, capsules);
            if (_history.Raycast(new(i * 2 + step * 0.1f, 1, -5), Vector3.UnitZ, 20, step - 3.5, entities[0], out _))
            {
                hits++;
            }

            if (_history.Raycast(new(-3, 1, -5), new(0.6f, 0, 1), 20, step - 10.25, Entity.Null, out _))
            {
                hits++;
            }

            if (_history.SphereCast(new(i * 2, 1, -5), Vector3.UnitZ, 0.01f, 20, step - 2, new IgnoreEntity(), out _))
            {
                hits++;
            }

            step++;
        }
    }

    // --- Helpers ---

    private static void Fill(HitCapsule[] capsules, float x)
    {
        capsules[0] = new HitCapsule(new(x, 0.3f, 0), new(x, 1.2f, 0), Radius, 0);
        capsules[1] = new HitCapsule(new(x, 1.55f, 0), new(x, 1.55f, 0), 0.25f, 1);
        capsules[2] = new HitCapsule(new(x + 0.4f, 0.5f, 0), new(x + 0.4f, 1.2f, 0), 0.1f, 2);
    }

    [Fact]
    public void A_filter_passes_through_the_entities_it_leaves_out_and_meets_the_next_behind_them()
    {
        var near = _world.Create();
        var far = _world.Create();
        var shooter = _world.Create();
        Record(near, 1, 0, z: 0);
        Record(far, 1, 0, z: 5);
        Record(shooter, 1, 0, z: -2);
        var skipNear = new Skipping(near, shooter);

        Assert.True(_history.Raycast(new(0, 1, -5), Vector3.UnitZ, 20, 1, shooter, out var first));
        Assert.True(_history.Raycast(new(0, 1, -5), Vector3.UnitZ, 20, 1, in skipNear, out var past));
        var skipAll = new Skipping(near, far);
        // A ray that starts inside a capsule the filter leaves out meets nothing of it; the shooter's too.
        Assert.False(_history.Raycast(new(0, 1, 0), Vector3.UnitZ, 4, 1, in skipAll, out _));
        Assert.True(_history.Raycast(new(0, 1, 0), Vector3.UnitZ, 20, 1, in skipNear, out var inside));

        Assert.Equal(near, first.Entity);
        Assert.Equal(far, past.Entity);
        Assert.Equal(far, inside.Entity);
        Assert.Equal(first.Distance + 5, past.Distance, 1e-3f);
    }

    [Fact]
    public void The_one_entity_overload_is_a_filter_of_that_entity_and_a_filter_allocates_nothing()
    {
        var near = _world.Create();
        var far = _world.Create();
        Record(near, 1, 0, z: 0);
        Record(far, 1, 0, z: 5);
        var skipNear = new Skipping(near, Entity.Null);
        Assert.True(Raycast(0, 1, near, out var byEntity));
        Assert.True(_history.Raycast(new(0, 1, -5), Vector3.UnitZ, 20, 1, in skipNear, out var byFilter));

        var start = Itera.TestSupport.SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            _history.Raycast(new(0, 1, -5), Vector3.UnitZ, 20, 1, in skipNear, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.Equal(byEntity, byFilter);
    }

    /// <summary>A filter that leaves out two entities.</summary>
    private readonly record struct Skipping(Entity First, Entity Second) : IHitFilter
    {
        public bool Includes(Entity entity) => entity != First && entity != Second;
    }

    /// <summary>Records a character at step <paramref name="tick"/>: a 1.8 m capsule standing at (x, 0, z).</summary>
    private void Record(Entity entity, long tick, float x, float z = 0) =>
        _history.Record(entity, tick, [new HitCapsule(new(x, Radius, z), new(x, Height - Radius, z), Radius, 0)]);

    private bool Shoot(float x, double tick) => Shoot(x, tick, out _);

    /// <summary>A ray along +z at height 1 m from 5 m before z = 0.</summary>
    private bool Shoot(float x, double tick, out HitResult hit) =>
        _history.Raycast(new(x, 1, -5), Vector3.UnitZ, 20, tick, Entity.Null, out hit);

    private bool Raycast(float x, double tick, Entity ignore, out HitResult hit) =>
        _history.Raycast(new(x, 1, -5), Vector3.UnitZ, 20, tick, ignore, out hit);

    private static Vector3 RandomPoint(Random random, float extent) =>
        new(
            ((float)random.NextDouble() * 2 - 1) * extent,
            ((float)random.NextDouble() * 2 - 1) * extent,
            ((float)random.NextDouble() * 2 - 1) * extent
        );

    private static float DistanceSquaredToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var axis = b - a;
        var lengthSquared = axis.LengthSquared();
        var share = lengthSquared > 0 ? Math.Clamp(Vector3.Dot(point - a, axis) / lengthSquared, 0f, 1f) : 0f;
        return (point - a - axis * share).LengthSquared();
    }

    /// <summary>The first distance along the ray (to 5 mm) at which it is within the radius of the segment, or -1.</summary>
    private static float MarchTo(Vector3 from, Vector3 direction, Vector3 a, Vector3 b, float radius)
    {
        const float step = 0.005f;
        for (var t = 0f; t < 20; t += step)
        {
            if (DistanceSquaredToSegment(from + direction * t, a, b) <= radius * radius)
            {
                return t;
            }
        }

        return -1;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) <= 1e-4f, $"expected {expected}, was {actual}");
}
