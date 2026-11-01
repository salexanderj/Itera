using System.Numerics;
using Arch.Core;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// <see cref="HitHistory"/>'s grid of cells, which finds the entities near a ray: its answers are those of testing every
/// entity, bit for bit, over crowds that move, grow wide, are forgotten, and are met from far origins; of two capsules
/// at the same distance the entity recorded first wins either way; and an entity is met through every cell its kept
/// steps reach.
/// </summary>
public sealed class HitHistoryGridTests : IDisposable
{
    private readonly World _world = World.Create();
    private readonly WorldOrigin _origin = new();

    public void Dispose() => World.Destroy(_world);

    /// <summary>
    /// 240 entities in clusters and spread over 160 m, walking, running, flung fast enough to span more cells than the
    /// grid holds them in, and a few giants; some skip steps, some are forgotten and others made; the origin moves now and
    /// then. After each step, 60 rays and swept spheres of every kind (along an axis, straight down, from inside a crowd,
    /// long enough to test every entity, at fractional and old ticks, past a filter) answer as testing every entity does.
    /// </summary>
    [Fact]
    public void Its_answers_are_those_of_testing_every_entity()
    {
        var random = new Random(41);
        var history = new HitHistory(_origin, mostEntities: 300);
        var bodies = new List<Body>();
        for (var i = 0; i < 240; i++)
        {
            bodies.Add(NewBody(random));
        }

        Span<HitCapsule> capsules = stackalloc HitCapsule[3];
        int hits = 0,
            casts = 0;
        for (var tick = 0; tick < 90; tick++)
        {
            if (tick % 30 == 29)
            {
                _origin.MoveTo(new VoxelCoord(random.Next(-3, 4) * 1000, 0, random.Next(-3, 4) * 1000));
            }

            for (var i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                if (random.Next(40) == 0)
                {
                    history.Forget(body.Entity);
                    bodies[i] = NewBody(random);
                    continue;
                }

                body.Place = body.Place.Plus(body.Velocity.X / 60, 0, body.Velocity.Z / 60);
                if (random.Next(6) == 0)
                {
                    continue;
                }

                var local = _origin.ToLocal(body.Place);
                for (var c = 0; c < body.Capsules; c++)
                {
                    var foot = local + new Vector3(c * 0.4f * body.Size, 0, 0);
                    capsules[c] = new HitCapsule(
                        foot + new Vector3(0, body.Size * 0.3f, 0),
                        foot + new Vector3(0, body.Size * 1.5f, 0),
                        body.Size * 0.3f,
                        c
                    );
                }

                history.Record(body.Entity, tick, capsules[..body.Capsules]);
            }

            for (var cast = 0; cast < 60; cast++)
            {
                var from = Ray(random, bodies, out var direction, out var radius, out var length);
                var at = tick - random.NextDouble() * (random.Next(4) == 0 ? 40 : 3);
                var filter = new EveryOther(random.Next(3) == 0);
                var grid = history.SphereCast(from, direction, radius, length, at, in filter, out var found);
                var every = history.SphereCastTestingAll(from, direction, radius, length, at, in filter, out var all);
                if (grid != every || !found.Equals(all))
                {
                    Assert.Fail(
                        $"Tick {tick}, cast {cast} from {from} along {direction} (radius {radius}, {length} m) at {at}: the grid gave {grid} {found}, every entity {every} {all}."
                    );
                }

                hits += grid ? 1 : 0;
                casts++;
            }
        }

        Assert.True(hits > casts / 8, $"{hits} hits of {casts} casts.");
    }

    [Fact]
    public void Of_two_capsules_at_the_same_distance_the_entity_recorded_first_wins()
    {
        var history = new HitHistory(_origin);
        var first = _world.Create();
        var second = _world.Create();
        var capsule = new HitCapsule(new Vector3(0, 0.3f, 0), new Vector3(0, 1.5f, 0), 0.3f, 0);
        history.Record(first, 1, [capsule]);
        history.Record(second, 1, [capsule]);

        var filter = new IgnoreEntity(Entity.Null);
        Assert.True(history.SphereCast(new(0, 1, -5), Vector3.UnitZ, 0, 20, 1, in filter, out var grid));
        Assert.True(history.SphereCastTestingAll(new(0, 1, -5), Vector3.UnitZ, 0, 20, 1, in filter, out var all));
        Assert.Equal(first, grid.Entity);
        Assert.Equal(all, grid);
    }

    /// <summary>
    /// An entity that crossed a cell's corner over its kept steps is met by a short ray in each of the four cells, at the
    /// tick it was there.
    /// </summary>
    [Fact]
    public void An_entity_is_met_through_every_cell_its_kept_steps_reach()
    {
        var history = new HitHistory(_origin);
        Bystanders(history, 1);
        var entity = _world.Create();
        var corner = HitHistory.CellMetres;
        for (var tick = 0; tick < 4; tick++)
        {
            var x = corner - 1.5f + tick;
            var z = corner - 1.5f + tick;
            history.Record(entity, tick, [new HitCapsule(new(x, 0.3f, z), new(x, 1.5f, z), 0.3f, 0)]);
        }

        for (var tick = 0; tick < 4; tick++)
        {
            var x = corner - 1.5f + tick;
            var z = corner - 1.5f + tick;
            Assert.True(history.Raycast(new(x, 1, z - 1), Vector3.UnitZ, 2, tick, Entity.Null, out var hit));
            Assert.Equal(entity, hit.Entity);
        }
    }

    /// <summary>
    /// A sphere swept beside a cell's edge meets a ball wholly in the next cell that its centre's line misses by less than
    /// its radius.
    /// </summary>
    [Fact]
    public void A_swept_sphere_meets_an_entity_across_a_cells_edge_within_its_radius()
    {
        var history = new HitHistory(_origin);
        Bystanders(history, 1);
        var entity = _world.Create();
        // A ball from 0.05 to 0.65 m past the edge at x = 8 m (its bounds too); the sphere's centre runs 0.3 m before it.
        var ball = new Vector3(HitHistory.CellMetres + 0.35f, 1, 0);
        history.Record(entity, 1, [new HitCapsule(ball, ball, 0.3f, 0)]);

        var filter = new IgnoreEntity(Entity.Null);
        var from = new Vector3(HitHistory.CellMetres - 0.3f, 1, -5);
        Assert.False(history.SphereCast(from, Vector3.UnitZ, 0.3f, 20, 1, in filter, out _));
        Assert.True(history.SphereCast(from, Vector3.UnitZ, 0.4f, 20, 1, in filter, out var hit));
        Assert.Equal(entity, hit.Entity);
    }

    /// <summary>
    /// Records 20 entities 100 m off at <paramref name="tick"/>: with so few others, a ray crossing a few cells tests every
    /// entity rather than walk the grid.
    /// </summary>
    private void Bystanders(HitHistory history, long tick)
    {
        for (var i = 0; i < 20; i++)
        {
            var at = new Vector3(i * 2 - 20, 0, -100);
            history.Record(_world.Create(), tick, [new HitCapsule(at, at + Vector3.UnitY, 0.3f, 0)]);
        }
    }

    private Body NewBody(Random random)
    {
        // Half in four clusters 6 m across, half spread over 160 m.
        var clustered = random.Next(2) == 0;
        var centre = clustered
            ? new Vector3(random.Next(4) * 30 - 45, 0, random.Next(4) * 25 - 40)
            : new Vector3(random.NextSingle() * 160 - 80, 0, random.NextSingle() * 160 - 80);
        var spread = clustered ? 3 : 0;
        var kind = random.Next(20);
        return new Body
        {
            Entity = _world.Create(),
            Place = new WorldPoint(
                centre.X + (random.NextSingle() * 2 - 1) * spread,
                random.NextSingle() * 2,
                centre.Z + (random.NextSingle() * 2 - 1) * spread
            ),
            // Most walk or run; one in ten is flung at 40 m/s, one in twenty a giant.
            Velocity = RandomFlat(random) * (kind < 2 ? 40 : random.NextSingle() * 7),
            Size = kind == 19 ? 12 : 1,
            Capsules = random.Next(1, 4),
        };
    }

    /// <summary>A cast of a random kind, its start local to the origin now.</summary>
    private Vector3 Ray(Random random, List<Body> bodies, out Vector3 direction, out float radius, out float length)
    {
        radius = random.Next(3) == 0 ? 0 : random.NextSingle() * 0.5f;
        length = random.Next(10) == 0 ? 2000 : random.NextSingle() * 60;
        direction = random.Next(6) switch
        {
            0 => Vector3.UnitX * (random.Next(2) * 2 - 1),
            1 => Vector3.UnitZ * (random.Next(2) * 2 - 1),
            2 => -Vector3.UnitY,
            _ => Vector3.Normalize(RandomFlat(random) + new Vector3(0, random.NextSingle() * 0.4f - 0.2f, 0)),
        };
        if (random.Next(3) == 0)
        {
            // From inside the crowd, near one of them.
            var near = _origin.ToLocal(bodies[random.Next(bodies.Count)].Place);
            return near
                + new Vector3(random.NextSingle() * 4 - 2, random.NextSingle() * 3, random.NextSingle() * 4 - 2);
        }

        var at = new WorldPoint(
            random.NextSingle() * 200 - 100,
            random.NextSingle() * 3,
            random.NextSingle() * 200 - 100
        );
        return direction.Y < 0 ? _origin.ToLocal(at) + new Vector3(0, 20, 0) : _origin.ToLocal(at);
    }

    private static Vector3 RandomFlat(Random random)
    {
        var angle = random.NextSingle() * MathF.Tau;
        return new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
    }

    /// <summary>Leaves out entities of odd id, when on.</summary>
    private readonly record struct EveryOther(bool On) : IHitFilter
    {
        public bool Includes(Entity entity) => !On || entity.Id % 2 == 0;
    }

    private sealed class Body
    {
        public Entity Entity;
        public WorldPoint Place;
        public Vector3 Velocity;
        public float Size;
        public int Capsules;
    }
}
