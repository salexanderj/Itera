using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Meshes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// Draws the image views a game declared (<see cref="RenderViews"/>): at the run's "image views" step it makes each one's
/// lists, records, images, and sets (<see cref="RenderViewTarget"/>) and seals the declarations; each frame the main thread
/// starts the views due (<see cref="BeginViews"/>: their constants and frustums, before the world's and the mesh assets'
/// lists, which list theirs too) and ends them (<see cref="EndViews"/>: their lights, and the HUD's panels), and the render
/// thread draws each due view after the world (<see cref="Record"/>, timed as <see cref="Diagnostics.GpuPass.ImageViews"/>)
/// with the mesh pass's pipelines. Views are drawn without shadows and particles: the world, the models, the mesh assets,
/// the sky, and the point lights.
/// </summary>
public sealed partial class RenderViewPass : IDisposable
{
    private readonly RenderViews _views;
    private readonly VulkanContext _context;
    private readonly GpuMemoryAllocator _allocator;
    private readonly MeshPass _meshPass;
    private readonly MeshAssetPass _assetPass;
    private readonly UiPass _uiPass;
    private readonly IOptions<EngineOptions> _options;
    private readonly ILogger<RenderViewPass> _logger;
    private readonly IRenderViewLists[] _due = new IRenderViewLists[RenderViews.MostViews];
    private readonly ViewPanel[][] _panels = new ViewPanel[FrameSync.FramesInFlight][];
    private readonly int[] _panelCounts = new int[FrameSync.FramesInFlight];
    private RenderViewTarget[] _targets = [];
    private bool _disposed;

    public RenderViewPass(
        RenderViews views,
        VulkanContext context,
        GpuMemoryAllocator allocator,
        MeshPass meshPass,
        MeshAssetPass assetPass,
        UiPass uiPass,
        IOptions<EngineOptions> options,
        ILogger<RenderViewPass> logger
    )
    {
        _views = views;
        _context = context;
        _allocator = allocator;
        _meshPass = meshPass;
        _assetPass = assetPass;
        _uiPass = uiPass;
        _options = options;
        _logger = logger;
        for (var i = 0; i < _panels.Length; i++)
        {
            _panels[i] = new ViewPanel[RenderViews.MostViews];
        }
    }

    /// <summary>
    /// Makes every declared view's lists, records, images, and sets, hands their shown images to the HUD, and seals the
    /// declarations. At the run's "image views" step, after the mesh assets reach the GPU, before the render thread.
    /// </summary>
    public void Initialize()
    {
        var targets = new RenderViewTarget[_views.Count];
        if (targets.Length > 0)
        {
            // Views are drawn with one sample a pixel, whatever the world's.
            _meshPass.WantViewPipelines();
            _assetPass.WantViewPipelines();
        }

        var made = 0;
        try
        {
            for (; made < targets.Length; made++)
            {
                var info = _views.Info(made);
                targets[made] = new RenderViewTarget(made, info, _context, _allocator, _options);
                targets[made].Initialize(_meshPass, _assetPass);
                LogCreated(info.Name, info.Width, info.Height, info.EveryFrames);
            }
        }
        catch
        {
            // What was made goes with the step's failure; the pass keeps no half-made view.
            for (var i = 0; i <= made && i < targets.Length; i++)
            {
                targets[i]?.Dispose();
            }

            throw;
        }

        _targets = targets;

        _views.Seal();
        GiveShownImages();
    }

    /// <summary>
    /// Starts the views due this frame (asked for, and their frames since the last update past their cadence): their
    /// constants (the asked camera at the view's size, the frame's sun and sky, no shadows) and frustums, before the
    /// world's and the mesh assets' lists are written. Returns them. Main thread, after the frame systems.
    /// </summary>
    internal ReadOnlySpan<IRenderViewLists> BeginViews(int slot, RenderView renderView, float maxDistance)
    {
        var count = 0;
        var atmosphere = renderView.Atmosphere;
        var sun = renderView.DrawnSun;
        foreach (var target in _targets)
        {
            ref var frame = ref target.FrameOf(slot);
            frame = default;
            if (!_views.IsDue(target.Index, out var camera))
            {
                continue;
            }

            var info = target.Info;
            frame.Due = true;
            frame.View = ViewConstants.Create(
                in camera,
                (uint)info.Width,
                (uint)info.Height,
                slot,
                sun,
                in atmosphere,
                maxDistance,
                shadows: false
            );
            target.View.Begin(slot, in frame.View);
            _due[count++] = target;
        }

        return _due.AsSpan(0, count);
    }

    /// <summary>
    /// Ends the views started this frame: each keeps its brightest lights with the events' (<paramref name="lights"/>),
    /// counts as drawn, and the frame's panels go into the slot for the HUD. Main thread, before the frame is published.
    /// </summary>
    internal void EndViews(int slot, ReadOnlySpan<IRenderViewLists> due, ReadOnlySpan<PointLight> lights)
    {
        foreach (var target in due)
        {
            target.View.End(slot, lights);
            _views.Drawn(target.Index);
        }

        var panels = _views.Panels;
        panels.CopyTo(_panels[slot]);
        _panelCounts[slot] = panels.Length;
    }

    /// <summary>
    /// A frame without a match's world: no view is drawn or shown in <paramref name="slot"/>. Main thread, before the frame
    /// is published.
    /// </summary>
    internal void NoViews(int slot)
    {
        foreach (var target in _targets)
        {
            target.FrameOf(slot).Due = false;
        }

        _panelCounts[slot] = 0;
    }

    /// <summary>The views the HUD shows in the frame of <paramref name="slot"/>. Render thread.</summary>
    internal ReadOnlySpan<ViewPanel> PanelsOf(int slot) => _panels[slot].AsSpan(0, _panelCounts[slot]);

    /// <summary>
    /// Clears the shown images not cleared yet (the first frame's), so nothing reads one undefined. Every frame, outside
    /// rendering, before anything reads them (under the loading screen too). Render thread.
    /// </summary>
    internal void RecordFirstFrame(CommandBuffer commandBuffer)
    {
        foreach (var target in _targets)
        {
            if (!target.ShownReady)
            {
                _meshPass.RecordViewCleared(commandBuffer, target);
            }
        }
    }

    /// <summary>True when a view is drawn in the frame of <paramref name="slot"/>.</summary>
    internal bool AnyDue(int slot)
    {
        foreach (var target in _targets)
        {
            if (target.FrameOf(slot).Due)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Draws each view due in the frame of <paramref name="slot"/> into its images and its shown image. After the world's
    /// pass (the frame's skinned vertices are written), outside rendering. Render thread.
    /// </summary>
    internal void Record(CommandBuffer commandBuffer, int slot)
    {
        foreach (var target in _targets)
        {
            ref readonly var frame = ref target.FrameOf(slot);
            if (frame.Due)
            {
                _meshPass.RecordView(commandBuffer, target, slot, in frame, _assetPass);
            }
        }
    }

    /// <summary>
    /// Makes the views' images again when the mesh pass's format changed (after the mesh pass's own), and gives the HUD
    /// the new shown images. Render thread, device idle.
    /// </summary>
    public void OnSwapchainRecreated()
    {
        var shownChanged = false;
        foreach (var target in _targets)
        {
            shownChanged |= target.OnTargetsChanged(_meshPass);
        }

        if (shownChanged)
        {
            GiveShownImages();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var target in _targets)
        {
            target.Dispose();
        }

        _targets = [];
    }

    /// <summary>The views' shown images to the HUD and to the mesh assets' texture array (the pass's set and each view's).</summary>
    private void GiveShownImages()
    {
        Span<ImageView> shown = stackalloc ImageView[RenderViews.MostViews];
        Span<DescriptorSet> sets = stackalloc DescriptorSet[RenderViews.MostViews];
        for (var i = 0; i < _targets.Length; i++)
        {
            shown[i] = _targets[i].Shown.View;
            sets[i] = _targets[i].AssetSet;
        }

        _uiPass.SetViewImages(shown[.._targets.Length]);
        _assetPass.SetViewImages(shown[.._targets.Length], sets[.._targets.Length]);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Image view {Name}: {Width}x{Height} texels, one sample a pixel, drawn at most every {EveryFrames} frame(s)"
    )]
    private partial void LogCreated(string name, int width, int height, int everyFrames);
}
