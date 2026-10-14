using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Tests.Voxels.Generation;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A world of 1 m voxels (<see cref="OpeningGenerator"/>): flat ground at 0, and a stone wall 8 m long (x -4 to 4), 4 m
/// tall, and 1 m thick (z 0 to 1), with an opening 3 m wide (x -1 to 2) and 3 m tall cut through it under a lintel 1 m
/// thick. Loaded once for the tests.
/// </summary>
public sealed class CoarseCeilingFixture : IDisposable
{
    public CoarseCeilingFixture()
    {
        World = TestWorlds.Load(new OpeningGenerator(), new Vector3(0, 2, 1), radiusMetres: 12);
        Mover = new VoxelCharacterMover(World, new NothingPassable());
    }

    public VoxelWorld World { get; }

    public VoxelCharacterMover Mover { get; }

    public void Dispose() => World.Dispose();
}

/// <summary>
/// The mover in 1 m cells under a ceiling over open space: the lintel over an opening is not ground for a body under
/// it, however high the body is in the air (the blended ground reads a cell or two over the feet, which on cells this
/// coarse reaches over the head); a body on the lintel's top stands on it; a wall beside the opening still stops a
/// body in the air.
/// </summary>
public sealed class CoarseCeilingMoverTests(CoarseCeilingFixture fixture) : IClassFixture<CoarseCeilingFixture>
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;

    // A person 1.75 m tall whose feet step onto a top in the air, as a game's jump asks.
    private static readonly CharacterShape s_shape = new(1.75f, 0.2625f, 0.42f, StepsInAir: true);

    private readonly VoxelCharacterMover _mover = fixture.Mover;

    /// <summary>
    /// A body in the corner of the opening's mouth (the east jamb and the wall's face), rising fast with a push into the
    /// corner, as a run up the wall beside it leaves it: it rises until its head meets the lintel, falls, and stands on
    /// the floor, never grounded on the way.
    /// </summary>
    [Fact]
    public void A_body_rising_under_a_lintel_falls_back_to_the_floor_and_is_never_grounded_in_the_air()
    {
        var feet = new Vector3(1.8f, 0, 1.24f);
        var velocity = new Vector3(2.25f, 7.7f, -2.68f);
        var highest = 0f;
        for (var tick = 0; tick < 120; tick++)
        {
            velocity = new Vector3(2.25f, velocity.Y - Gravity * Step, -2.68f);
            var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            highest = MathF.Max(highest, feet.Y);
            Assert.False(result.Grounded && feet.Y > 0.05f, $"Tick {tick}: grounded in the air at {feet}.");
        }

        Assert.True(highest + s_shape.HeightMetres > 2.9f, $"It rose only to {highest} m.");
        Assert.True(feet.Y < 0.01f, $"It ended at {feet}.");
    }

    /// <summary>Under the middle of the lintel, a body placed with its feet 1.25 m up falls to the floor.</summary>
    [Fact]
    public void A_body_under_the_middle_of_the_lintel_falls_to_the_floor()
    {
        var feet = new Vector3(0.5f, 1.25f, 0.5f);
        var velocity = Vector3.Zero;
        for (var tick = 0; tick < 60; tick++)
        {
            velocity.Y -= Gravity * Step;
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.True(feet.Y < 0.01f, $"It ended at {feet}.");
    }

    /// <summary>A body standing on the wall's top over the opening stays there.</summary>
    [Fact]
    public void A_body_on_the_lintels_top_stands_on_it()
    {
        var feet = new Vector3(0.5f, 4, 0.5f);
        var velocity = Vector3.Zero;
        CharacterMoveResult result = default;
        for (var tick = 0; tick < 60; tick++)
        {
            velocity.Y -= Gravity * Step;
            result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.True(result.Grounded, $"Not grounded at {feet}.");
        Assert.Equal(4, feet.Y, 0.01f);
    }

    /// <summary>
    /// A body in the air beside the opening, moving into the wall, is stopped at the wall's face and falls to the floor.
    /// </summary>
    [Fact]
    public void A_body_in_the_air_beside_the_opening_is_stopped_by_the_wall()
    {
        var feet = new Vector3(3, 1.2f, 1.6f);
        var velocity = Vector3.Zero;
        for (var tick = 0; tick < 90; tick++)
        {
            velocity = new Vector3(0, velocity.Y - Gravity * Step, -3);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.True(feet.Z >= 1 + s_shape.RadiusMetres - 0.01f, $"Tick {tick}: into the wall at {feet}.");
        }

        Assert.True(feet.Y < 0.01f, $"It ended at {feet}.");
    }

    /// <summary>A body walking through the opening on the floor passes under the lintel.</summary>
    [Fact]
    public void A_body_walks_through_the_opening()
    {
        var feet = new Vector3(0.5f, 0, 3);
        var velocity = Vector3.Zero;
        for (var tick = 0; tick < 120; tick++)
        {
            velocity = new Vector3(0, velocity.Y - Gravity * Step, -4.5f);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.True(feet.Z < -2 && feet.Y < 0.01f, $"It ended at {feet}.");
    }
}

/// <summary>
/// Flat ground at 0 on 1 m voxels, and a stone wall 8 m long, 4 m tall, and 1 m thick with an opening 3 m wide and
/// 3 m tall cut through it (<see cref="CoarseCeilingFixture"/>).
/// </summary>
internal sealed class OpeningGenerator()
    : HeightfieldGenerator(new VoxelGrid(100), new GroundLayers(100, [], HillsGenerator.Stone))
{
    private static readonly Box[] s_boxes =
    [
        new(-400, 0, 0, 400, 400, 100, HillsGenerator.Stone),
        new(-100, 0, 0, 200, 300, 100, VoxelConstants.Air),
    ];

    public override string Id => "test.opening";

    public override int Version => 1;

    protected override double HeightAt(int x, int z, ulong seed) => 0;

    protected override byte SurfaceAt(int x, int z, ulong seed, double height) => HillsGenerator.Grass;

    protected override GroundFeatures? CreateFeatures() => new Boxes();

    /// <summary>A box of one material, in centimetres, from its low corner up to, not on, its high one.</summary>
    private readonly record struct Box(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ, byte Material)
        : IGroundFeature
    {
        public bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
            MaxX >= minX && MinX <= maxX && MaxY >= minY && MinY <= maxY && MaxZ >= minZ && MinZ <= maxZ;

        public void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels)
        {
            const int size = VoxelConstants.ChunkSize;
            var (zStart, zEnd) = VoxelRows.Rows(MinZ, MaxZ - 1, first.Z, level);
            var (yStart, yEnd) = VoxelRows.Rows(MinY, MaxY - 1, first.Y, level);
            for (var row = zStart; row < zEnd; row++)
            {
                for (var column = yStart; column < yEnd; column++)
                {
                    VoxelRows.FillRange(
                        voxels.Slice((row * size + column) * size, size),
                        MinX,
                        (long)MaxX - 1,
                        first.X,
                        level,
                        Material
                    );
                }
            }
        }
    }

    /// <summary>Both boxes, the wall then the opening, wherever they reach.</summary>
    private sealed class Boxes : GroundFeatures
    {
        private readonly GroundFeatureList<Box> _found = new(2);

        public override void Find(ulong seed, GridLevel level, int minX, int maxX, int minZ, int maxZ)
        {
            _found.Clear();
            foreach (var box in s_boxes)
            {
                if (box.MaxX >= minX && box.MinX <= maxX && box.MaxZ >= minZ && box.MinZ <= maxZ)
                {
                    _found.Add(box);
                }
            }
        }

        public override bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
            _found.Touches(minX, maxX, minY, maxY, minZ, maxZ);

        public override void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels) =>
            _found.Fill(first, level, voxels);
    }
}
