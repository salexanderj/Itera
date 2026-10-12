using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Matches;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Matches;
using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// Voxel objects in a match the engine's real composition makes (<see cref="HeadlessMatch"/> over
/// <see cref="EngineComposition"/>, its generator placing a rock, the rock's kind registered at the run's start): a
/// body dropped on the rock rests on it, and falls to the ground once the rock under it is dug away.
/// </summary>
public sealed class ObjectDamageMatchTests
{
    private const byte Rock = 40;

    [Fact]
    public void A_body_on_a_rock_falls_when_the_rock_is_dug_away()
    {
        var placements = new List<ObjectPlacement>();
        using var provider = EngineComposition.Build(
            new Dictionary<string, string?> { ["Engine:Physics:Threads"] = "1" },
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IChunkGenerator>(new PlacingGenerator(placements)));
                services.AddSingleton<IRunStartup>(sp => new RockStartup(
                    sp.GetRequiredService<VoxelObjectKinds>(),
                    placements
                ));
            }
        );
        var run = new HeadlessRun(provider);
        using var match = run.Play(seed: 5);
        match.Match.Voxels.SetCenters([Vector3.Zero]);
        Assert.True(match.WaitUntilLoaded());
        var terrain = match.Match.Terrain!;
        match.Step(2);
        Assert.Equal(1, terrain.Placed.Count);

        var physics = match.Services.GetRequiredService<PhysicsWorld>();
        var sphere = new Sphere(0.1f);
        var ball = physics.AddBody(
            BodyDescription.CreateDynamic(
                new RigidPose(match.Match.Voxels.Origin.ToLocal(new VoxelCoord(0, 150, 0))),
                sphere.ComputeInertia(1),
                new CollidableDescription(physics.Simulation.Shapes.Add(sphere), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
        match.Step(90);
        var resting = physics.Simulation.Bodies[ball].Pose.Position;
        Assert.Equal(match.Match.Voxels.Origin.ToLocal(new VoxelCoord(0, 60, 0)).Y, resting.Y, 0.01f);

        match.Match.Voxels.Apply(
            VoxelEdit.Box(new VoxelCoord(-80, 0, -80), new VoxelCoord(160, 80, 160), VoxelEditOperation.Clear)
        );
        match.Step(90);

        Assert.Equal(1, terrain.Damage.Count);
        var fallen = physics.Simulation.Bodies[ball].Pose.Position;
        Assert.Equal(match.Match.Voxels.Origin.ToLocal(new VoxelCoord(0, 10, 0)).Y, fallen.Y, 0.01f);
    }

    /// <summary>Registers a rock 1.2 m square and 50 cm high and places one at the map's middle.</summary>
    private sealed class RockStartup(VoxelObjectKinds kinds, List<ObjectPlacement> placements) : IRunStartup
    {
        public void Start()
        {
            var shape = new VoxelObjectShape(5, new VoxelCoord(24, 12, 24), new VoxelCoord(60, 0, 60));
            shape.FillBox(0, -10, 0, 120, 50, 120, Rock);
            var rock = kinds.Register("rock", shape);
            placements.Add(new ObjectPlacement(new ObjectId(1), rock, default, 300));
        }
    }
}
