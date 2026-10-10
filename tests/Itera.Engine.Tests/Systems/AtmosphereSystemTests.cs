using System.Numerics;
using Arch.Core;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

public sealed class AtmosphereSystemTests
{
    private static readonly FrameTime Time = new(1f / 60, 1, 1, 0);
    private readonly RenderView _view = new(Options.Create(new EngineOptions()));

    [Fact]
    public void The_frame_gets_the_sources_atmosphere_at_the_camera()
    {
        using var world = World.Create();
        var source = new ByHeight();
        using var system = new AtmosphereSystem(world, _view, source);

        _view.SetCamera(CameraView.Default with { Position = new Vector3(3, 0, -4) });
        system.Update(in Time);
        var low = _view.Atmosphere;
        _view.SetCamera(CameraView.Default with { Position = new Vector3(3, 100, -4) });
        system.Update(in Time);
        var high = _view.Atmosphere;

        Assert.Equal(ByHeight.Low, low);
        Assert.Equal(ByHeight.High, high);
        Assert.Equal(new WorldPoint(3, 100, -4), source.LastAsked);
    }

    [Fact]
    public void The_source_is_asked_at_the_cameras_absolute_place_whatever_the_origin()
    {
        using var world = World.Create();
        var source = new ByHeight();
        var origin = new WorldOrigin();
        origin.MoveTo(new VoxelCoord(150_000_00, -200, -3_000_00));
        using var system = new AtmosphereSystem(world, _view, source, origin);
        _view.SetCamera(CameraView.Default with { Position = new Vector3(3, 100, -4) });

        system.Update(in Time);

        Assert.Equal(new WorldPoint(150_003, 98, -3_004), source.LastAsked);
        Assert.Equal(ByHeight.High, _view.Atmosphere);
    }

    [Fact]
    public void The_frame_gets_the_sources_look_at_the_camera_or_the_engines()
    {
        using var world = World.Create();
        using var withLook = new AtmosphereSystem(world, _view, new ByHeight());
        _view.SetCamera(CameraView.Default with { Position = new Vector3(0, 100, 0) });

        withLook.Update(in Time);
        Assert.Equal(ByHeight.HighLook, _view.Look);

        using var withoutLook = new AtmosphereSystem(world, _view, new Plain());
        withoutLook.Update(in Time);
        Assert.Equal(RenderLook.Default, _view.Look);
    }

    [Fact]
    public void A_camera_far_outside_the_map_is_asked_as_it_is()
    {
        using var world = World.Create();
        var source = new ByHeight();
        using var system = new AtmosphereSystem(world, _view, source);
        _view.SetCamera(CameraView.Default with { Position = new Vector3(1e9f, 0, -1e9f) });

        system.Update(in Time);

        Assert.Equal(new WorldPoint(1e9f, 0, -1e9f), source.LastAsked);
        Assert.Equal(ByHeight.Low, _view.Atmosphere);
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = new AtmosphereSystem(world, _view, new ByHeight());

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Time));
        using var plain = new AtmosphereSystem(world, _view, new Plain());
        Assert.Equal(0, SystemAllocation.BytesAllocated(plain, Time));
    }

    /// <summary>A source with one look below 50 m and another above.</summary>
    private sealed class ByHeight : IAtmosphereSource
    {
        public static readonly Atmosphere Low = new(
            new Vector3(0.5f, 0.6f, 0.7f),
            new Vector3(1, 0.9f, 0.8f),
            0.1f,
            0.2f,
            0.3f
        );
        public static readonly Atmosphere High = new(
            new Vector3(0.1f, 0.2f, 0.3f),
            new Vector3(0.7f, 0.6f, 0.5f),
            0.4f,
            0.5f,
            0.6f
        );

        /// <summary>A brighter look above 50 m.</summary>
        public static readonly RenderLook HighLook = RenderLook.Default with { Exposure = 1.1f };

        public WorldPoint LastAsked { get; private set; }

        public RenderLook LookAt(in WorldPoint position) => position.Y < 50 ? RenderLook.Default : HighLook;

        public Atmosphere At(in WorldPoint position)
        {
            LastAsked = position;
            return position.Y < 50 ? Low : High;
        }
    }

    /// <summary>A source that gives no look of its own.</summary>
    private sealed class Plain : IAtmosphereSource
    {
        public Atmosphere At(in WorldPoint position) => ByHeight.Low;
    }
}
