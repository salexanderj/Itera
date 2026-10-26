using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The lists a frame's mesh assets may be drawn in, known while the frame systems run, so a system poses only what some
/// list could draw (<c>MeshDrawSystem</c>). <see cref="Begin"/> takes the view the frame will be drawn with from the
/// camera, sun, and atmosphere set so far (<see cref="RenderView.TryFrameView"/>) and the image views due
/// (<see cref="RenderViews"/>), and sets their volumes as the frame's view will (<see cref="MeshView.Predict"/>);
/// <see cref="MayDraw"/> then tests a sphere as <see cref="MeshAssetDraws"/> will test an instance's: the camera's view,
/// the near and far cascades as its shadow allows, and each due image view. <c>GameLoop</c> compares the view the frame
/// is drawn with against it (<see cref="Check"/>) and warns once a run when a frame system changed the camera, the sun,
/// or the image views after the culling. Run-wide; main thread; allocates nothing.
/// </summary>
public sealed partial class MeshPoseCulling
{
    private readonly RenderView _view;
    private readonly RenderViews _views;
    private readonly MeshView _meshView;
    private readonly ILogger<MeshPoseCulling> _logger;
    private readonly ViewVolume _camera = new();
    private readonly ViewVolume[] _viewVolumes = NewVolumes();
    private readonly ViewConstants[] _viewConstants = new ViewConstants[RenderViews.MostViews];
    private readonly int[] _viewIndices = new int[RenderViews.MostViews];
    private ViewConstants _predicted;
    private int _viewCount;
    private bool _active;
    private bool _changeReported;

    public MeshPoseCulling(RenderView view, RenderViews views, MeshView meshView, ILogger<MeshPoseCulling> logger)
    {
        _view = view;
        _views = views;
        _meshView = meshView;
        _logger = logger;
    }

    /// <summary>
    /// Starts the frame's culling from the view as far as the frame systems have set it: false, and nothing culled, for a
    /// frame without a picture or before a frame system has set the camera this frame (pose everything then).
    /// </summary>
    public bool Begin()
    {
        _active = false;
        _viewCount = 0;
        if (!_view.TryFrameView(out var frame))
        {
            return false;
        }

        _predicted = frame;
        _meshView.Predict(in frame, _camera);
        for (var i = 0; i < _views.Count; i++)
        {
            if (!_views.IsDue(i, out var camera))
            {
                continue;
            }

            var info = _views.Info(i);
            _viewConstants[_viewCount] = _view.CreateImageView(
                in camera,
                in info,
                0,
                _view.FrameDistance,
                _view.FrameOrigin
            );
            _viewIndices[_viewCount] = i;
            // An image view draws no shadows: its frustum alone.
            _viewVolumes[_viewCount].SetFrustum(in _viewConstants[_viewCount]);
            _viewCount++;
        }

        _active = true;
        return true;
    }

    /// <summary>
    /// True when a sphere of <paramref name="radius"/> at <paramref name="centre"/>, in world metres, may be drawn in a list
    /// this frame, as <see cref="MeshAssetDraws"/> lists an instance with <paramref name="shadow"/>: seen by the camera
    /// (not <see cref="ModelShadow.Only"/>), cast into the near or far cascade while the view's shadows are on, or seen by
    /// a due image view (neither of those for <see cref="ModelShadow.None"/>). After <see cref="Begin"/> gave true.
    /// </summary>
    public bool MayDraw(Vector3 centre, float radius, ModelShadow shadow)
    {
        if (shadow == ModelShadow.Hidden)
        {
            return false;
        }

        if (shadow != ModelShadow.Only && _camera.InView(centre, radius))
        {
            return true;
        }

        if (shadow == ModelShadow.None)
        {
            return false;
        }

        if (_camera.Shadows && (_camera.NearCascade.Holds(centre, radius) || _camera.FarCascade.Holds(centre, radius)))
        {
            return true;
        }

        for (var v = 0; v < _viewCount; v++)
        {
            if (_viewVolumes[v].InView(centre, radius))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares the frame's <paramref name="view"/> and the image views due (<paramref name="views"/>, their records in
    /// <paramref name="slot"/>) with those the culling took this frame, if it ran: a Warning once a run when they differ in
    /// what culls (the camera, the lens, the sun, the view's distance, the origin, or the views due). A change of the
    /// shadows' switch alone is left out: the settings turn it in the frame's UI, after the frame systems. Main thread,
    /// after the views began.
    /// </summary>
    internal void Check(int slot, in ViewConstants view, ReadOnlySpan<IRenderViewLists> views)
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        if (_changeReported)
        {
            return;
        }

        var same = Same(in _predicted, in view) && views.Length == _viewCount;
        for (var v = 0; same && v < views.Length; v++)
        {
            same = views[v].Index == _viewIndices[v] && Same(in _viewConstants[v], in views[v].FrameOf(slot).View);
        }

        if (!same)
        {
            _changeReported = true;
            LogViewChanged();
        }
    }

    /// <summary>True when two views cull alike: the same place, axes, sun, distance, depths, origin, and projection.</summary>
    private static bool Same(in ViewConstants a, in ViewConstants b) =>
        a.Position == b.Position
        && a.Forward == b.Forward
        && a.Right == b.Right
        && a.Up == b.Up
        && a.SunDirection == b.SunDirection
        && a.MaxDistance == b.MaxDistance
        && a.Near == b.Near
        && a.Far == b.Far
        && a.Origin == b.Origin
        && a.Orthographic == b.Orthographic;

    private static ViewVolume[] NewVolumes()
    {
        var volumes = new ViewVolume[RenderViews.MostViews];
        for (var v = 0; v < volumes.Length; v++)
        {
            volumes[v] = new ViewVolume();
        }

        return volumes;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: the frame's camera, sun, or image views changed after the posed meshes were culled by them; a mesh may be missing from a frame. Register MeshDrawSystem after the frame systems that set the camera, the atmosphere, and the image views"
    )]
    private partial void LogViewChanged();
}
