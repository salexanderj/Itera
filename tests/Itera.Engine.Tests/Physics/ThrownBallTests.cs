using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A thrown ball on a world of 10 cm voxels (<see cref="LooseBodies.TryCreateBall"/>): made from nothing, of
/// centimetres whatever the world's voxels (its hull, mass, box, and model; until 2026-10-07 a piece's centimetre was
/// taken for a world voxel, a ball ten times its size), and at rest it goes back as a sphere of its material filled into
/// the air.
/// </summary>
public sealed class ThrownBallTests : IDisposable
{
    private const float Step = 1f / 60;
    private const int Radius = 12;

    private readonly VoxelWorld _world;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;
    private readonly VoxelModels _models;
    private readonly LooseBodies _bodies;

    public ThrownBallTests()
    {
        _world = TestWorlds.Load(new DensityTerrainGenerator(new VoxelGrid(10)), new Vector3(1, 2, 1), radiusMetres: 4);
        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        var passability = new NothingPassable();
        _colliders = new VoxelColliders(_physics, _world, passability, NullLogger<VoxelColliders>.Instance);
        _colliders.Initialize();
        _models = new VoxelModels(_world);
        _bodies = new LooseBodies(
            _physics,
            _world,
            _colliders,
            _models,
            passability,
            Options.Create(new EngineOptions()),
            NullLogger<LooseBodies>.Instance
        );
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_ball_is_made_from_nothing_of_centimetres()
    {
        Assert.True(
            _bodies.TryCreateBall(
                Radius,
                DensityTerrainGenerator.Stone,
                new Vector3(1, 2.5f, 1),
                Vector3.Zero,
                out var body
            )
        );
        Assert.True(
            _bodies.TryCreateBall(
                Radius,
                DensityTerrainGenerator.Stone,
                new Vector3(2, 2.5f, 1),
                Vector3.Zero,
                out var other
            )
        );

        // No edit: nothing was taken from the world.
        Assert.Equal(0, _world.EditCount);
        Assert.Equal(Radius, _bodies.BallRadiusOf(body));
        Assert.Equal(0, _bodies.PieceOf(body));
        // A sphere of 1 cm voxels of radius 12 (7,153 voxels) at 2,000 kg a cubic metre.
        Assert.InRange(_bodies.MassOf(body), 14.2f, 14.4f);
        Assert.Equal(_bodies.MassOf(body), _bodies.MassOf(other));
        _bodies.BoxOf(body, out var min, out var max);
        Assert.Equal(new Vector3(-0.12f), min);
        Assert.Equal(new Vector3(0.13f), max);
        Assert.Equal(VoxelModels.Centimetres, _models.UnitCentimetres(_bodies.ModelOf(body)));
        Assert.Equal(0.01f, _models.VoxelMetres(_bodies.ModelOf(body), 0));
        // Its hull is the ball's: its bounds reach its radius, not ten times it.
        _bodies.BoundsOf(body, out var low, out var high);
        Assert.InRange(high.X - low.X, 0.24f, 0.27f);
    }

    [Fact]
    public void A_ball_rests_on_the_floor_and_goes_back_as_a_sphere_filled_into_the_air()
    {
        Assert.True(
            _bodies.TryCreateBall(
                Radius,
                DensityTerrainGenerator.Stone,
                new Vector3(1, 2.2f, 1),
                Vector3.Zero,
                out var body
            )
        );
        var steps = 0;
        var resting = 0;
        while (resting < 60)
        {
            Assert.True(++steps < 1200, $"The ball did not rest; it is at {_bodies.PivotOf(body)}.");
            _colliders.Update(Step);
            _physics.Step(Step);
            resting = _bodies.IsAtRest(body) ? resting + 1 : 0;
        }

        // The floor's surface is at 152.2 cm, inside its voxel; the ball's pivot is 12 cm above its lowest centimetre.
        var pivot = _bodies.PivotOf(body);
        Assert.InRange(pivot.Y, 1.63f, 1.66f);

        var sequence = _bodies.Settle(body);

        Assert.Equal(1, _world.EditCount);
        Assert.Equal(0, _bodies.Count);
        ref readonly var edit = ref _world.GetEdit(sequence);
        Assert.Equal(VoxelEditShape.Sphere, edit.Shape);
        Assert.Equal(VoxelEditOperation.Fill, edit.Operation);
        Assert.Equal(Radius, edit.Radius);
        Assert.Equal(DensityTerrainGenerator.Stone, edit.Material);
        // Its pivot rounded to the nearest centimetre corner, as Settle rounds it.
        var centimetres = pivot * 100;
        Assert.Equal(
            new VoxelCoord(
                (int)MathF.Round(centimetres.X),
                (int)MathF.Round(centimetres.Y),
                (int)MathF.Round(centimetres.Z)
            ),
            edit.Origin
        );
        // The 10 cm voxel round the ball's middle is stone now.
        _world.TryGetMaterial(_world.Origin.ToCentimetres(pivot), out var middle);
        Assert.Equal(DensityTerrainGenerator.Stone, middle);
    }
}
