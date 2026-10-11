using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="OriginShiftSystem"/>: the origin stays while the active camera is less than <c>ShiftMetres</c> from it on
/// every axis, and moves to the camera's whole metres once it is that far on any; other cameras do not count; a point a
/// jump asked for goes first; its warm-up leaves the origin where it was; and a step allocates nothing, with a move or
/// without.
/// </summary>
public sealed class OriginShiftSystemTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);

    private readonly PhysicsWorld _physics = new(
        NullLogger<PhysicsWorld>.Instance,
        Options.Create(new EngineOptions { Physics = { Threads = 1 } })
    );
    private readonly EcsWorld _ecs = new(NullLogger<EcsWorld>.Instance, new SystemTimes());
    private readonly VoxelModels _models = new();
    private readonly WorldOrigin _origin = new();
    private OriginShifts? _shifts;

    public OriginShiftSystemTests() => _physics.Initialize();

    public void Dispose()
    {
        _ecs.Dispose();
        _models.Dispose();
        _physics.Dispose();
    }

    [Fact]
    public void The_origin_follows_the_active_camera_once_it_is_shift_metres_out_on_any_axis()
    {
        using var system = Create(shiftMetres: 512);
        var world = _ecs.World;
        var camera = world.Create(new CameraPose { Position = new Vector3(511.9f, -511.9f, 300) }, new ActiveCamera());
        // Another camera, far out but not the active one, counts for nothing.
        world.Create(new CameraPose { Position = new Vector3(5_000, 0, 0) });
        system.Initialize();

        // The warm-up moved it out and back.
        Assert.Equal(default, _origin.Centimetres);
        Assert.Equal(2, _origin.Moves);

        system.Update(in Step);
        Assert.Equal(default, _origin.Centimetres);

        world.Set(camera, new CameraPose { Position = new Vector3(100.25f, 3.5f, -512.75f) });
        system.Update(in Step);

        // To the camera's whole metres (3.5 rounds away from zero), the camera moved the other way.
        Assert.Equal(new VoxelCoord(10_000, 400, -51_300), _origin.Centimetres);
        Assert.Equal(new Vector3(0.25f, -0.5f, 0.25f), world.Get<CameraPose>(camera).Position);
        Assert.Equal(3, _origin.Moves);

        system.Update(in Step);
        Assert.Equal(3, _origin.Moves);
    }

    /// <summary>
    /// A jump the step will make (tp) asks first: the origin goes there, not back to the camera still where it was, and a
    /// request near the origin leaves it.
    /// </summary>
    [Fact]
    public void A_requested_point_goes_before_the_camera()
    {
        using var system = Create(shiftMetres: 512);
        var shifts = _shifts!;
        _ecs.World.Create(new CameraPose { Position = new Vector3(600, 0, 0) }, new ActiveCamera());
        system.Initialize();

        shifts.RequestFollow(new WorldPoint(-1_400, 40, 0));
        system.Update(in Step);

        Assert.Equal(new VoxelCoord(-140_000, 4_000, 0), _origin.Centimetres);
        Assert.False(shifts.TakeRequest(out _));

        shifts.RequestFollow(new WorldPoint(-1_000, 40, 0));
        system.Update(in Step);
        Assert.Equal(new VoxelCoord(-140_000, 4_000, 0), _origin.Centimetres);

        // The camera, now 2 km out, is followed again.
        system.Update(in Step);
        Assert.Equal(new VoxelCoord(60_000, 0, 0), _origin.Centimetres);
    }

    [Fact]
    public void A_smaller_shift_distance_moves_it_sooner()
    {
        using var system = Create(shiftMetres: 8);
        var camera = _ecs.World.Create(new CameraPose { Position = new Vector3(0, 7.9f, 0) }, new ActiveCamera());
        system.Initialize();

        system.Update(in Step);
        Assert.Equal(default, _origin.Centimetres);

        _ecs.World.Set(camera, new CameraPose { Position = new Vector3(0, 0, -8) });
        system.Update(in Step);
        Assert.Equal(new VoxelCoord(0, 0, -800), _origin.Centimetres);
    }

    [Fact]
    public void Registered_anywhere_but_first_it_throws_at_the_first_step()
    {
        var system = Create(shiftMetres: 512);
        _ecs.Initialize([new Idle(_ecs.World), system], []);

        Assert.Throws<InvalidOperationException>(() => _ecs.FixedUpdate(in Step));
    }

    [Fact]
    public void Its_warm_up_at_the_maps_edge_stays_within_the_origins_reach()
    {
        _origin.StartAt(new WorldPoint(WorldOrigin.MostMetres, 0, -WorldOrigin.MostMetres));
        using var system = Create(shiftMetres: 512);
        var before = _origin.Centimetres;

        system.Initialize();

        Assert.Equal(before, _origin.Centimetres);
    }

    [Fact]
    public void Without_an_active_camera_the_origin_stays()
    {
        using var system = Create(shiftMetres: 512);
        _ecs.World.Create(new CameraPose { Position = new Vector3(5_000, 0, 0) });
        system.Initialize();

        system.Update(in Step);

        Assert.Equal(default, _origin.Centimetres);
    }

    [Fact]
    public void A_step_allocates_nothing_with_a_move_or_without()
    {
        using var system = Create(shiftMetres: 512);
        var camera = _ecs.World.Create(new CameraPose(), new ActiveCamera());
        system.Initialize();

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                _ecs.World.Set(camera, new CameraPose { Position = new Vector3(i % 2 == 0 ? 600 : -600, 0, 0) });
                system.Update(in Step);
            }
        });

        Assert.Equal(0, bytes);
        Assert.True(_origin.Moves > 1000);
    }

    /// <summary>A fixed system that does nothing, to come before the one tested.</summary>
    private sealed class Idle(World world) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem;

    private OriginShiftSystem Create(double shiftMetres)
    {
        var options = Options.Create(new EngineOptions { Origin = { ShiftMetres = shiftMetres } });
        var shifts = new OriginShifts(
            _origin,
            _physics,
            _ecs,
            _models,
            new PointLights(),
            new ParticleEmitter(options, NullLogger<ParticleEmitter>.Instance),
            [],
            NullLogger<OriginShifts>.Instance,
            options: options
        );
        _shifts = shifts;
        return new OriginShiftSystem(_ecs.World, shifts);
    }
}
