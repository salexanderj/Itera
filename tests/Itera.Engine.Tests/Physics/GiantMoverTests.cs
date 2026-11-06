using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Tests.Voxels.Generation;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Flat ground of 1 m voxels with a wall and a ledge 2 m high, read by the mover in 10 cm cells, for bodies 30 m and
/// more tall. Loaded once for all the giant tests.
/// </summary>
public sealed class GiantWorldFixture : IDisposable
{
    /// <summary>The wall's face towards -x, in metres, from z = -30 to -10.</summary>
    public const float WallX = 20;

    /// <summary>The ledge's near edge towards -x, in metres, from z = 10 to 40; its top is 2 m over the ground.</summary>
    public const float LedgeX = 10;

    public const float LedgeTop = 2;

    public GiantWorldFixture()
    {
        World = TestWorlds.Load(new GiantGroundGenerator(), new Vector3(0, 2, 0), radiusMetres: 45);
        Mover = new VoxelCharacterMover(World, 10, new NothingPassable());
    }

    public VoxelWorld World { get; }

    /// <summary>The mover in 10 cm cells over the 1 m voxels.</summary>
    public VoxelCharacterMover Mover { get; }

    public void Dispose() => World.Dispose();
}

/// <summary>
/// <see cref="VoxelCharacterMover"/> with a body 30 m tall and 4.5 m in radius, in 10 cm cells over 1 m voxels: it
/// walks and turns over the ground, a wall stops it at its radius, it steps onto a ledge, and once its first move has
/// made the windows its moves allocate nothing. A mover takes a radius of <see cref="VoxelCharacterMover.MaxRadiusCells"/>
/// of its cells, and no more.
/// </summary>
public sealed class GiantMoverTests(GiantWorldFixture fixture) : IClassFixture<GiantWorldFixture>
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;

    // A humanoid 30 m tall: its radius is 0.15 of its height, and it steps up 2.5 m.
    private static readonly CharacterShape s_giant = new(30, 4.5f, 2.5f);

    private readonly VoxelCharacterMover _mover = fixture.Mover;

    [Fact]
    public void A_mover_takes_a_radius_of_its_most_cells_and_no_more()
    {
        Assert.Equal(20f, _mover.MaxRadiusMetres, 4);
        var feet = new Vector3(-10, 0.5f, 0);
        var velocity = Vector3.Zero;
        var wide = new CharacterShape(60, _mover.MaxRadiusMetres + 0.01f, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => _mover.Move(ref feet, ref velocity, in wide, Step));

        var coarse = new VoxelCharacterMover(fixture.World, new NothingPassable());
        Assert.Equal(200f, coarse.MaxRadiusMetres, 3);
    }

    [Fact]
    public void A_giant_walks_and_turns_over_the_ground()
    {
        var feet = new Vector3(-20, 0.5f, 0);
        var velocity = Vector3.Zero;
        CharacterMoveResult result = default;
        var walked = 0f;
        // Four seconds of a circle 8 m across at 3 m/s, turning every step.
        for (var tick = 0; tick < 240; tick++)
        {
            var angle = tick * Step * 3 / 4f;
            var before = feet;
            velocity = new Vector3(
                MathF.Cos(angle) * 3,
                result.Grounded ? 0 : velocity.Y - Gravity * Step,
                MathF.Sin(angle) * 3
            );
            result = _mover.Move(ref feet, ref velocity, in s_giant, Step);
            Assert.Equal(CharacterMoveStatus.Moved, result.Status);
            walked += Vector2.Distance(new Vector2(before.X, before.Z), new Vector2(feet.X, feet.Z));
        }

        Assert.True(result.Grounded);
        Assert.False(result.HitWall);
        Assert.InRange(feet.Y, -0.05f, 0.05f);
        Assert.InRange(walked, 11.5f, 12.1f);
    }

    [Fact]
    public void A_wall_stops_a_giant_at_its_radius()
    {
        var feet = new Vector3(5, 0, -20);
        var velocity = Vector3.Zero;
        var hit = false;
        for (var tick = 0; tick < 300; tick++)
        {
            velocity = new Vector3(4, velocity.Y - Gravity * Step, 0);
            hit |= _mover.Move(ref feet, ref velocity, in s_giant, Step).HitWall;
        }

        Assert.True(hit);
        Assert.InRange(feet.X, GiantWorldFixture.WallX - 4.5f - 0.01f, GiantWorldFixture.WallX - 4.5f);
    }

    [Fact]
    public void A_giant_steps_onto_a_ledge_under_its_step()
    {
        var feet = new Vector3(0, 0, 25);
        var velocity = Vector3.Zero;
        CharacterMoveResult result = default;
        for (var tick = 0; tick < 300; tick++)
        {
            velocity = new Vector3(4, velocity.Y - Gravity * Step, 0);
            result = _mover.Move(ref feet, ref velocity, in s_giant, Step);
        }

        Assert.True(result.Grounded);
        Assert.True(feet.X > GiantWorldFixture.LedgeX + 4.5f, $"The giant stopped at x = {feet.X}.");
        Assert.InRange(feet.Y, GiantWorldFixture.LedgeTop - 0.05f, GiantWorldFixture.LedgeTop + 0.05f);
    }

    [Fact]
    public void A_giants_moves_allocate_nothing_after_its_first()
    {
        var feet = new Vector3(-25, 0, -5);
        var velocity = Vector3.Zero;
        _mover.Move(ref feet, ref velocity, in s_giant, Step);
        var start = Itera.TestSupport.SystemAllocation.StartMeasurement();
        for (var tick = 0; tick < 120; tick++)
        {
            velocity = new Vector3(3, velocity.Y - Gravity * Step, 2);
            _mover.Move(ref feet, ref velocity, in s_giant, Step);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}

/// <summary>Flat ground of 1 m voxels at height 0, a stone wall, and a stone ledge 2 m high (<see cref="GiantWorldFixture"/>).</summary>
internal sealed class GiantGroundGenerator()
    : HeightfieldGenerator(new VoxelGrid(100), new GroundLayers(100, [], HillsGenerator.Stone))
{
    public override string Id => "test.giant-ground";

    public override int Version => 1;

    protected override double HeightAt(int x, int z, ulong seed) => 0;

    protected override byte SurfaceAt(int x, int z, ulong seed, double height) => HillsGenerator.Grass;

    protected override GroundFeatures? CreateFeatures() =>
        new StoneBoxes(
            // The wall: 5 m thick, 40 m high.
            new StoneBox(2000, 0, -3000, 2500, 4000, -1000),
            // The ledge: 2 m high.
            new StoneBox(1000, 0, 1000, 4000, 200, 4000)
        );
}

/// <summary>A box of stone, in centimetres, from its low corner up to, not on, its high one.</summary>
internal readonly record struct StoneBox(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ) : IGroundFeature
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
                    HillsGenerator.Stone
                );
            }
        }
    }
}

/// <summary>Stone boxes, wherever they reach.</summary>
internal sealed class StoneBoxes(params StoneBox[] boxes) : GroundFeatures
{
    private readonly GroundFeatureList<StoneBox> _found = new(boxes.Length);

    public override void Find(ulong seed, GridLevel level, int minX, int maxX, int minZ, int maxZ)
    {
        _found.Clear();
        foreach (var box in boxes)
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
