using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// <see cref="MeshPoseCulling"/>: nothing culled before a camera is set in a frame or for a frame without a picture; a
/// sphere may be drawn exactly where the frame's view, begun later with the same camera and sun, lists it (the camera's
/// view, the cascades as its shadow allows) or a due image view sees it; and a view changed after the culling warns
/// once a run.
/// </summary>
public sealed class MeshPoseCullingTests
{
    [Fact]
    public void Nothing_is_culled_before_a_camera_is_set_in_the_frame_or_without_a_picture()
    {
        var harness = new PoseCullingHarness();
        harness.View.SetCamera(PoseCullingHarness.Ahead);
        harness.View.BeginFrame(
            PoseCullingHarness.Width,
            PoseCullingHarness.Height,
            PoseCullingHarness.Distance,
            default
        );
        var unset = harness.Culling.Begin();
        harness.View.BeginFrame(0, 0, PoseCullingHarness.Distance, default);
        harness.View.SetCamera(PoseCullingHarness.Ahead);
        var pictureless = harness.Culling.Begin();
        harness.BeginFrame(PoseCullingHarness.Ahead);

        Assert.False(unset);
        Assert.False(pictureless);
        Assert.True(harness.Culling.Begin());
    }

    /// <summary>
    /// Spheres all round the camera, of each shadow, may be drawn exactly where the frame's view, begun after the culling
    /// with the same camera and sun, lists them: the culling fits the same frustum and cascades.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_sphere_may_be_drawn_exactly_where_the_frames_view_lists_it(bool shadows)
    {
        var harness = new PoseCullingHarness(shadows);
        var camera = PoseCullingHarness.Ahead with
        {
            Position = new Vector3(3, 2, -1),
            Orientation = Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 0),
        };
        harness.BeginFrame(camera);
        Assert.True(harness.Culling.Begin());
        var frame = harness.FrameView();
        harness.MeshView.Begin(0, in frame);

        var random = new Random(7);
        var mismatches = 0;
        for (var i = 0; i < 2000; i++)
        {
            var centre = camera.Position + new Vector3(Next(random, 260), Next(random, 120), Next(random, 260));
            var radius = 0.5f + 4 * random.NextSingle();
            foreach (var shadow in (ModelShadow[])[ModelShadow.Casts, ModelShadow.Only, ModelShadow.None])
            {
                var seen = shadow != ModelShadow.Only && harness.MeshView.InView(centre, radius);
                var casts =
                    shadow != ModelShadow.None
                    && harness.MeshView.Shadows
                    && (
                        harness.MeshView.NearCascade.Holds(centre, radius)
                        || harness.MeshView.FarCascade.Holds(centre, radius)
                    );
                mismatches += harness.Culling.MayDraw(centre, radius, shadow) == (seen || casts) ? 0 : 1;
            }
        }

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void A_sphere_only_a_due_image_view_sees_may_be_drawn_unless_it_casts_no_shadow()
    {
        var harness = new PoseCullingHarness(shadows: false);
        var map = harness.Views.Declare("map", 64, 64);
        harness.Views.BeginFrame();
        // Looking down from 50 m over a point 100 m behind the camera.
        var above = new CameraView(
            new Vector3(0, 50, 100),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2),
            MathF.PI / 3,
            0.1f
        );
        harness.Views.Draw(map, in above);
        harness.BeginFrame(PoseCullingHarness.Ahead);

        Assert.True(harness.Culling.Begin());
        Assert.True(harness.Culling.MayDraw(new Vector3(0, 0, 100), 1, ModelShadow.Casts));
        Assert.True(harness.Culling.MayDraw(new Vector3(0, 0, 100), 1, ModelShadow.Only));
        Assert.False(harness.Culling.MayDraw(new Vector3(0, 0, 100), 1, ModelShadow.None));
        Assert.False(harness.Culling.MayDraw(new Vector3(0, 0, 300), 1, ModelShadow.Casts));
    }

    [Fact]
    public void A_camera_moved_after_the_culling_warns_once_a_run()
    {
        var lines = new List<string>();
        var harness = new PoseCullingHarness(logger: new ListLogger(lines));
        harness.BeginFrame(PoseCullingHarness.Ahead);
        harness.Culling.Begin();
        var same = harness.FrameView();
        harness.Culling.Check(0, in same, []);
        for (var frame = 0; frame < 2; frame++)
        {
            harness.BeginFrame(PoseCullingHarness.Ahead);
            harness.Culling.Begin();
            harness.View.SetCamera(PoseCullingHarness.Behind);
            var moved = harness.FrameView();
            harness.Culling.Check(0, in moved, []);
        }

        Assert.Single(lines);
    }

    private static float Next(Random random, float half) => (2 * random.NextSingle() - 1) * half;

    private sealed class ListLogger(List<string> lines) : ILogger<MeshPoseCulling>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => lines.Add(formatter(state, exception));
    }
}

/// <summary>
/// A frame's view for <see cref="MeshPoseCulling"/> over memory: a 1600 x 900 screen, 200 m deep, at the match's origin,
/// with the engine's sun, and the view's shadows on or off.
/// </summary>
internal sealed class PoseCullingHarness
{
    public const uint Width = 1600;
    public const uint Height = 900;
    public const float Distance = 200;

    /// <summary>At the origin, looking along -z, 90 degrees.</summary>
    public static readonly CameraView Ahead = CameraView.Default;

    /// <summary>At the origin, looking along +z.</summary>
    public static readonly CameraView Behind = CameraView.Default with
    {
        Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
    };

    public PoseCullingHarness(bool shadows = true, ILogger<MeshPoseCulling>? logger = null)
    {
        var options = Options.Create(new EngineOptions());
        View = new RenderView(options) { Shadows = shadows, SunOverride = ViewConstants.DefaultSunDirection };
        MeshView = new MeshView(new LightingTarget(), options);
        Culling = new MeshPoseCulling(View, Views, MeshView, logger ?? NullLogger<MeshPoseCulling>.Instance);
    }

    public RenderView View { get; }

    public RenderViews Views { get; } = new();

    public MeshView MeshView { get; }

    public MeshPoseCulling Culling { get; }

    /// <summary>Starts a frame, as the loop does, and sets its camera, as a camera system does.</summary>
    public void BeginFrame(in CameraView camera)
    {
        View.BeginFrame(Width, Height, Distance, default);
        View.SetCamera(in camera);
    }

    /// <summary>The constants the loop draws the frame with.</summary>
    public ViewConstants FrameView() => View.CreateView(Width, Height, 0, Distance, default);

    /// <summary>The lighting records and lights of the frame slots; the view writes nothing else.</summary>
    private sealed class LightingTarget : IMeshDrawTarget
    {
        private readonly MeshLighting[] _lighting = new MeshLighting[FrameSync.FramesInFlight];
        private readonly MeshLight[] _lights = new MeshLight[FrameSync.FramesInFlight * MeshPass.MostLights];

        public ulong HeapBytes => 0;

        public ref MeshLighting LightingOf(int slot) => ref _lighting[slot];

        public Span<MeshLight> LightsOf(int slot) => _lights.AsSpan(slot * MeshPass.MostLights, MeshPass.MostLights);

        public Span<MeshDraw> DrawsOf(int slot) => throw new NotSupportedException();

        public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) => throw new NotSupportedException();

        public Span<MeshModelDraw> ModelDrawsOf(int slot, int list) => throw new NotSupportedException();

        public Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list) =>
            throw new NotSupportedException();
    }
}
