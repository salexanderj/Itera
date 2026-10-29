using System.Numerics;
using BepuPhysics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Loose bodies as voxels (<see cref="LooseBodyVoxels"/>) and the character mover among them: the body voxels are the
/// ones a piece edit would place, and a character is blocked by bodies, stands and steps on them, is not lifted by one
/// that hits it from the side, is carried up by one rising under its feet, and never ends inside the world's voxels.
/// </summary>
public sealed class MoverWithBodiesTests : IDisposable
{
    private const int Floor = 200;
    private const float Step = 1f / 60;

    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.4f);

    /// <summary>A wide body's cylinder: its windows span three strips of 128 cells, and bodies fall in the later ones.</summary>
    private static readonly CharacterShape s_wide = new(6, 1.5f, 1);

    private readonly VoxelWorld _world;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;
    private readonly LooseBodies _bodies;
    private readonly LooseBodyVoxels _bodyVoxels;
    private readonly VoxelCharacterMover _mover;
    private readonly ITestOutputHelper _output;

    public MoverWithBodiesTests(ITestOutputHelper output)
    {
        _output = output;
        _world = TestWorlds.Load(new BoxesGenerator(Floor), new Vector3(0, 2, 0), radiusMetres: 5);
        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        var passability = new MoverWorldFixture.LeavesPass();
        _colliders = new VoxelColliders(_physics, _world, passability, NullLogger<VoxelColliders>.Instance);
        _colliders.Initialize();
        _bodies = new LooseBodies(
            _physics,
            _world,
            _colliders,
            new VoxelModels(_world),
            passability,
            Options.Create(new EngineOptions()),
            NullLogger<LooseBodies>.Instance
        );
        _bodyVoxels = new LooseBodyVoxels(_bodies, _world, passability);
        _mover = new VoxelCharacterMover(_world, passability, _bodyVoxels);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void Body_voxels_are_the_voxels_a_piece_edit_places_there(int degrees)
    {
        // A lopsided piece: a box of the floor's dirt 30 by 12 by 18 voxels with a ball on it.
        Assert.True(
            _world.TryLift(
                VoxelEdit.Box(new VoxelCoord(-300, 188, -300), new VoxelCoord(30, 12, 18), VoxelEditOperation.Lift),
                out var piece
            )
        );
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180);
        var pivot = new VoxelCoord(40, 260, -20);
        var body = Place(piece, pivot, turn);
        _bodyVoxels.Refresh();

        // The same piece put into the world there, 1.5 m above the floor, where nothing else is.
        Assert.True(_world.TryGetPiece(piece, out var voxels));
        _world.Apply(VoxelEdit.PieceAt(piece, voxels.Radius, pivot, VoxelEdit.PackTurn(turn)));

        Span<UInt128> fromBodies = stackalloc UInt128[64];
        Span<UInt128> fromWorld = stackalloc UInt128[64];
        for (var y = 240; y < 280; y += 3)
        {
            _bodyVoxels.BlockedCells(pivot.X - 32, pivot.Z - 32, 64, y, y, fromBodies);
            _world.BlockedCells(pivot.X - 32, pivot.Z - 32, 64, y, y, MaterialSet.None, fromWorld);
            for (var row = 0; row < 64; row++)
            {
                Assert.True(fromWorld[row] == fromBodies[row], $"Row {row} at y {y} differs.");
            }
        }

        var footprint = new CircleFootprint(pivot.X + 3.5, pivot.Z - 2.5, 9);
        Assert.Equal(
            _world.HighestTop(in footprint, 200, 300, MaterialSet.None),
            _bodyVoxels.HighestTop(in footprint, 200, 300)
        );
        Assert.Equal(
            _world.LowestBottom(in footprint, 220, 300, MaterialSet.None),
            _bodyVoxels.LowestBottom(in footprint, 220, 300)
        );
        Assert.True(_physics.Simulation.Bodies.BodyExists(body));
    }

    [Fact]
    public void A_character_walking_into_a_body_taller_than_its_step_is_stopped_by_it()
    {
        // A post of the floor's dirt 30 cm square and 60 cm tall, standing on the floor from x = 0.6 m.
        Assert.True(
            _world.TryLift(
                VoxelEdit.Box(new VoxelCoord(-300, 130, -300), new VoxelCoord(30, 60, 30), VoxelEditOperation.Lift),
                out var piece
            )
        );
        Place(piece, new VoxelCoord(75, 230, 0), Quaternion.Identity);
        _bodyVoxels.Refresh();
        var feet = new Vector3(0, 2, 0);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 30; i++)
        {
            velocity = new Vector3(3, velocity.Y - 0.3f, 0);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        // The post's near side is at x = 0.6 m.
        Assert.InRange(feet.X, 0.6f - s_shape.RadiusMetres - 0.01f, 0.6f - s_shape.RadiusMetres);
        Assert.Equal(2, feet.Y, 0.005f);
    }

    [Fact]
    public void A_character_does_not_step_up_onto_a_ball_lower_than_its_step()
    {
        // Loose bodies are pushed (by the character's stand-in), not climbed: here, with no stand-in, the character
        // meets the ball and slides around it on the floor.
        var ball = RestingBall(new Vector3(0.6f, 2.2f, 0));
        var feet = new Vector3(0, 2, 0);
        var velocity = Vector3.Zero;
        var highest = 0f;
        var hit = false;

        for (var i = 0; i < 40; i++)
        {
            velocity = new Vector3(3, velocity.Y - 0.3f, 0);
            hit |= _mover.Move(ref feet, ref velocity, in s_shape, Step).HitWall;
            highest = MathF.Max(highest, feet.Y);
        }

        Assert.True(hit);
        Assert.Equal(2, highest, 0.005f);
        Assert.True(MathF.Abs(feet.Z - _bodies.PivotOf(ball).Z) > 0.3f, $"The character ended at {feet}.");
    }

    [Fact]
    public void A_standing_body_stays_unchanged_until_a_loose_body_comes_round_it()
    {
        var feet = new Vector3(0, 2, 0);
        var stamp = _mover.Stamp();
        Assert.True(_mover.StandsUnchanged(ref stamp, feet, in s_shape));

        // A ball resting 3 m off, lifted 3 m away the other way: neither reaches round it.
        var far = RestingBall(new Vector3(3, 2.2f, 0));
        Assert.True(_mover.StandsUnchanged(ref stamp, feet, in s_shape));

        _bodies.Remove(far);
        Assert.True(_world.TryLift(Ball(new VoxelCoord(-300, 188, -100)), out var piece));
        Assert.True(_bodies.TryCreate(piece, new Vector3(0.5f, 2.2f, 0), Vector3.Zero, out _));
        _bodyVoxels.Refresh();
        Assert.False(_mover.StandsUnchanged(ref stamp, feet, in s_shape));
    }

    [Fact]
    public void A_character_lands_on_a_body_and_stands_on_it()
    {
        var ball = RestingBall(new Vector3(0.5f, 2.2f, 0.5f));
        var top = _bodyVoxels.HighestTop(new CircleFootprint(50, 50, 26), 200, 260);
        var feet = new Vector3(0.5f, 3, 0.5f);
        var velocity = Vector3.Zero;

        CharacterMoveResult result = default;
        for (var i = 0; i < 60; i++)
        {
            velocity.Y -= 20 * Step;
            result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.True(result.Grounded);
        Assert.Equal(top / 100f, feet.Y, 0.005f);
        Assert.True(_physics.Simulation.Bodies.BodyExists(ball));
    }

    [Fact]
    public void A_character_walks_along_a_body_it_stands_on_and_steps_down_off_it()
    {
        // A slab 10 cm high, 80 cm long, and 40 cm wide, lying on the floor from x = 0.5 m, with the character on it.
        Assert.True(
            _world.TryLift(
                VoxelEdit.Box(new VoxelCoord(-300, 190, 100), new VoxelCoord(80, 10, 40), VoxelEditOperation.Lift),
                out var piece
            )
        );
        Place(piece, new VoxelCoord(90, 205, 0), Quaternion.Identity);
        _bodyVoxels.Refresh();
        var feet = new Vector3(0.7f, 2.1f, 0);
        var velocity = Vector3.Zero;
        var onSlab = 0f;

        for (var i = 0; i < 40; i++)
        {
            velocity = new Vector3(2, velocity.Y - 0.3f, 0);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
            if (i == 10)
            {
                onSlab = feet.Y;
            }
        }

        Assert.Equal(2.1f, onSlab, 0.005f);
        Assert.True(feet.X > 1.5f, $"The character stopped at {feet}.");
        Assert.Equal(2, feet.Y, 0.005f);
    }

    [Fact]
    public void A_body_that_hits_a_character_from_the_side_does_not_lift_it()
    {
        // A ball at chest height, 2 cm into the character's side.
        Assert.True(_world.TryLift(Ball(new VoxelCoord(-300, 188, -100)), out var piece));
        Place(piece, new VoxelCoord(26 + 12 - 2, 330, 0), Quaternion.Identity);
        _bodyVoxels.Refresh();
        var feet = new Vector3(0, 2, 0);
        var velocity = new Vector3(0, -0.3f, 0);

        var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.NotEqual(CharacterMoveStatus.Lifted, result.Status);
        Assert.Equal(2, feet.Y, 0.005f);
    }

    [Fact]
    public void A_body_rising_under_the_feet_carries_the_character_up()
    {
        // A ball whose top is 3 cm above the character's feet, right under it.
        Assert.True(_world.TryLift(Ball(new VoxelCoord(-300, 188, -100)), out var piece));
        Place(piece, new VoxelCoord(0, 190, 0), Quaternion.Identity);
        _bodyVoxels.Refresh();
        var top = _bodyVoxels.HighestTop(new CircleFootprint(0, 0, 26), 150, 260);
        var feet = new Vector3(0, 2, 0);
        var velocity = new Vector3(0, -0.3f, 0);

        var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.InRange(top, 201, 240);
        Assert.Equal(CharacterMoveStatus.Lifted, result.Status);
        Assert.Equal(top / 100f, feet.Y, 0.005f);
    }

    [Fact]
    public void Random_moves_among_moving_bodies_never_end_inside_the_voxels()
    {
        // Four balls rolling about while a character walks among them.
        var random = new Random(8);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(_world.TryLift(Ball(new VoxelCoord(-300 + 40 * i, 188, 200)), out var piece));
            Assert.True(
                _bodies.TryCreate(
                    piece,
                    new Vector3(random.NextSingle() * 2 - 1, 2.5f, random.NextSingle() * 2 - 1),
                    new Vector3(random.NextSingle() * 4 - 2, 0, random.NextSingle() * 4 - 2),
                    out _
                )
            );
        }

        var feet = new Vector3(0, 2, 0);
        var velocity = Vector3.Zero;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        const int Moves = 2000;
        for (var i = 0; i < Moves; i++)
        {
            if (i % 30 == 0)
            {
                var angle = random.NextSingle() * 2 * MathF.PI;
                velocity = new Vector3(MathF.Cos(angle) * 3, velocity.Y, MathF.Sin(angle) * 3);
            }

            velocity.Y -= 20 * Step;
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.False(_mover.Overlaps(feet, in s_shape), $"Inside the voxels after move {i}, at {feet}.");
            _colliders.Update(Step);
            _physics.Step(Step);
            _bodyVoxels.Refresh();
            // Keep the character near the bodies.
            if (feet.LengthSquared() > 9)
            {
                velocity = new Vector3(-feet.X, velocity.Y, -feet.Z);
            }
        }

        _output.WriteLine(
            $"{Moves} moves among 4 balls: {timer.Elapsed.TotalMicroseconds / Moves:F1} us per move and step."
        );
    }

    [Fact]
    public void A_wide_character_walking_into_a_body_lower_than_its_step_is_stopped_by_it()
    {
        // The 60 cm post of the test above, in the third strip of the slide's window; a body is pushed, not climbed.
        Assert.True(
            _world.TryLift(
                VoxelEdit.Box(new VoxelCoord(-300, 130, -300), new VoxelCoord(30, 60, 30), VoxelEditOperation.Lift),
                out var piece
            )
        );
        Place(piece, new VoxelCoord(75, 230, 0), Quaternion.Identity);
        _bodyVoxels.Refresh();
        var feet = new Vector3(-1.5f, 2, 0);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 40; i++)
        {
            velocity = new Vector3(3, velocity.Y - 0.3f, 0);
            _mover.Move(ref feet, ref velocity, in s_wide, Step);
        }

        Assert.InRange(feet.X, 0.6f - s_wide.RadiusMetres - 0.01f, 0.6f - s_wide.RadiusMetres);
        Assert.Equal(2, feet.Y, 0.005f);
    }

    [Fact]
    public void A_wide_character_a_body_hit_from_the_side_walks_on_without_being_lifted()
    {
        // A ball at chest height, 2 cm into the character's side, in the third strip of the window: the cells under
        // where the character starts each move leave it out, so it walks on along its side.
        Assert.True(_world.TryLift(Ball(new VoxelCoord(-300, 188, -100)), out var piece));
        Place(piece, new VoxelCoord(150 + 12 - 2, 330, 0), Quaternion.Identity);
        _bodyVoxels.Refresh();
        var feet = new Vector3(0, 2, 0);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 10; i++)
        {
            velocity = new Vector3(0, velocity.Y - 0.3f, 1);
            var result = _mover.Move(ref feet, ref velocity, in s_wide, Step);
            Assert.NotEqual(CharacterMoveStatus.Lifted, result.Status);
        }

        Assert.Equal(2, feet.Y, 0.005f);
        Assert.Equal(0, feet.X, 0.001f);
        Assert.InRange(feet.Z, 0.16f, 0.17f);
    }

    [Fact]
    public void A_body_rising_under_a_wide_characters_feet_carries_it_up()
    {
        // A ball whose top is 3 cm above the feet, under the character's edge.
        Assert.True(_world.TryLift(Ball(new VoxelCoord(-300, 188, -100)), out var piece));
        Place(piece, new VoxelCoord(120, 190, 0), Quaternion.Identity);
        _bodyVoxels.Refresh();
        var top = _bodyVoxels.HighestTop(new CircleFootprint(0, 0, 150), 150, 260);
        var feet = new Vector3(0, 2, 0);
        var velocity = new Vector3(0, -0.3f, 0);

        var result = _mover.Move(ref feet, ref velocity, in s_wide, Step);

        Assert.InRange(top, 201, 240);
        Assert.Equal(CharacterMoveStatus.Lifted, result.Status);
        Assert.Equal(top / 100f, feet.Y, 0.005f);
    }

    [Fact]
    public void Moving_next_to_a_body_allocates_nothing()
    {
        RestingBall(new Vector3(0.6f, 2.2f, 0));
        var feet = new Vector3(0, 2, 0);
        var velocity = new Vector3(3, 0, 0);
        _mover.Move(ref feet, ref velocity, in s_shape, Step);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            feet = new Vector3(0, 2, 0);
            velocity = new Vector3(3, -0.3f, 0);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
            _bodyVoxels.Refresh();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static VoxelEdit Ball(VoxelCoord centre) => VoxelEdit.Sphere(centre, 12, VoxelEditOperation.Lift);

    /// <summary>A ball 25 cm across of the floor's dirt, dropped at <paramref name="pivot"/> and left to rest.</summary>
    private BodyHandle RestingBall(Vector3 pivot)
    {
        Assert.True(_world.TryLift(Ball(new VoxelCoord(-300, 188, -300)), out var piece));
        Assert.True(_bodies.TryCreate(piece, pivot, Vector3.Zero, out var body));
        for (var i = 0; i < 120; i++)
        {
            _colliders.Update(Step);
            _physics.Step(Step);
        }

        _bodyVoxels.Refresh();
        return body;
    }

    /// <summary>Makes a body of a piece and puts its pivot exactly at voxel corner <paramref name="pivot"/>, turned.</summary>
    private BodyHandle Place(long piece, VoxelCoord pivot, Quaternion turn)
    {
        Assert.True(_bodies.TryCreate(piece, Vector3.Zero, Vector3.Zero, out var body));
        var reference = _physics.Simulation.Bodies[body];
        reference.Pose.Orientation = turn;
        var target = new Vector3(pivot.X, pivot.Y, pivot.Z) / 100f;
        reference.Pose.Position += target - _bodies.PivotOf(body);
        return body;
    }
}
