using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Rendering;

/// <summary>
/// Where a frame is seen from, in metres. The camera looks along -Z of <paramref name="Orientation"/> with +Y up and +X
/// right (right-handed, as <see cref="Matrix4x4.CreateLookAt"/>).
/// </summary>
/// <param name="Position">Eye position in world metres.</param>
/// <param name="Orientation">Rotation from camera space to world space.</param>
/// <param name="VerticalFovRadians">Vertical field of view.</param>
/// <param name="NearMetres">Distance to the near plane.</param>
public readonly record struct CameraView(
    Vector3 Position,
    Quaternion Orientation,
    float VerticalFovRadians,
    float NearMetres
)
{
    /// <summary>At the origin, looking along -Z, 90 degrees, near plane at 5 cm. Used until a camera system sets one.</summary>
    public static CameraView Default => new(Vector3.Zero, Quaternion.Identity, MathF.PI / 2, 0.05f);

    /// <summary>The direction the camera looks.</summary>
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Orientation);

    /// <summary>The camera's right.</summary>
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Orientation);

    /// <summary>The camera's up.</summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Orientation);
}

/// <summary>
/// The camera and atmosphere of the frame being built, and how it is drawn. Frame systems set the camera and the
/// atmosphere; <see cref="GameLoop"/> hands them to the render thread in <see cref="RenderFrame"/>. Main thread only.
/// </summary>
public sealed class RenderView
{
    private float _renderScale = 1;
    private float _scalingThreshold = 120;
    private float _fixedRenderScale = 1;
    private float _screenDim;
    private readonly Atmosphere _clearSky;

    public RenderView(IOptions<EngineOptions> options)
    {
        var renderer = options.Value.Renderer;
        Shadows = renderer.Shadows;
        RenderScale = renderer.RenderScale;
        FixedRenderScale = renderer.RenderScale;
        DynamicScaling = renderer.DynamicRenderScaling;
        ScalingThreshold = renderer.DynamicScalingThreshold;
        AntiAliasing = renderer.AntiAliasing;
        _clearSky = Atmosphere.Clear(new Vector3(renderer.SkyRed, renderer.SkyGreen, renderer.SkyBlue));
        Atmosphere = _clearSky;
    }

    /// <summary>
    /// A match ended: the camera, the sky, the look, and the dim are as a run starts, so the menu and the next match do
    /// not begin with the last match's view. The console's look tuning stays for the run.
    /// </summary>
    public void ResetAfterMatch()
    {
        Camera = CameraView.Default;
        Atmosphere = _clearSky;
        Look = RenderLook.Default;
        ScreenDim = 0;
    }

    /// <summary>The camera the next frame is drawn from.</summary>
    public CameraView Camera { get; private set; } = CameraView.Default;

    /// <summary>
    /// The debug views drawn for the rest of the run (the console's <c>view</c> and <c>materials</c>; not saved):
    /// <see cref="ViewSwitches.Wireframe"/>, <see cref="ViewSwitches.Levels"/>, <see cref="ViewSwitches.Plain"/>, and
    /// the texture prototype's <c>Materials</c> switches.
    /// </summary>
    public ViewSwitches DebugViews { get; set; }

    /// <summary>
    /// How dark the next frame's picture is drawn under the UI, 0 (as it is) to 1 (black): a game fades to black with it
    /// (a dead player's view). Frame systems set it; it stays until they change it.
    /// </summary>
    public float ScreenDim
    {
        get => _screenDim;
        set => _screenDim = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    /// <summary>
    /// The sky, sunlight, and air the next frame is drawn with. Until a frame system sets it, a clear sky of the
    /// <c>Engine:Renderer</c> sky colour.
    /// </summary>
    public Atmosphere Atmosphere { get; private set; }

    /// <summary>
    /// The look the next frame is drawn with, as a frame system set it (<see cref="Systems.AtmosphereSystem"/>); the engine's
    /// until one does. The frame is drawn with <see cref="DrawnLook"/>.
    /// </summary>
    public RenderLook Look { get; private set; } = RenderLook.Default;

    /// <summary>
    /// The console's <c>sun</c> and <c>time</c>: a direction towards the sun for the run in place of the atmosphere's, or
    /// null.
    /// </summary>
    public Vector3? SunOverride { get; set; }

    /// <summary>The direction towards the sun the next frame is drawn with: <see cref="SunOverride"/>, else the atmosphere's.</summary>
    public Vector3 DrawnSun => SunOverride ?? Atmosphere.TowardsSun;

    /// <summary>The console's <c>look</c>: values set for the run over every frame's look.</summary>
    public LookTuning LookTuning { get; } = new();

    /// <summary>The look the next frame is drawn with: <see cref="Look"/> with <see cref="LookTuning"/>'s values over it. Allocates nothing.</summary>
    public RenderLook DrawnLook => LookTuning.Apply(Look);

    /// <summary>Whether the sun casts shadows through the shadow maps (<c>Engine:Renderer:Shadows</c>).</summary>
    public bool Shadows { get; set; }

    /// <summary>
    /// The share of the screen's size, on each axis, the frame being built draws the world at before FSR 1 upscales it,
    /// <see cref="Rendering.RenderScale.Min"/> to 1 (set values are clamped to that range).
    /// <see cref="DynamicRenderScale"/> sets it every frame.
    /// </summary>
    public float RenderScale
    {
        get => _renderScale;
        set => _renderScale = Math.Clamp(value, Rendering.RenderScale.Min, Rendering.RenderScale.Max);
    }

    /// <summary>
    /// Whether the render scale follows the GPU's time to hold <see cref="ScalingThreshold"/>
    /// (<c>Engine:Renderer:DynamicRenderScaling</c>, the settings' Dynamic render scaling).
    /// </summary>
    public bool DynamicScaling { get; set; }

    /// <summary>
    /// The frame rate dynamic render scaling holds, when the screen's refresh rate is not lower
    /// (<c>Engine:Renderer:DynamicScalingThreshold</c>; set values are clamped to
    /// <see cref="DynamicRenderScale.LeastThreshold"/> to <see cref="DynamicRenderScale.MostThreshold"/>).
    /// </summary>
    public float ScalingThreshold
    {
        get => _scalingThreshold;
        set =>
            _scalingThreshold = Math.Clamp(value, DynamicRenderScale.LeastThreshold, DynamicRenderScale.MostThreshold);
    }

    /// <summary>The render scale while dynamic render scaling is off (<c>Engine:Renderer:RenderScale</c>).</summary>
    public float FixedRenderScale
    {
        get => _fixedRenderScale;
        set => _fixedRenderScale = Math.Clamp(value, Rendering.RenderScale.Min, Rendering.RenderScale.Max);
    }

    /// <summary>The console's <c>scale</c>: a render scale for the run in place of the others, or null.</summary>
    public float? ScaleOverride { get; set; }

    /// <summary>
    /// Whether the picture is anti-aliased with SMAA 1x (<c>Engine:Renderer:AntiAliasing</c>, the settings'
    /// Anti-aliasing), unless <see cref="AntiAliasOverride"/> says otherwise; only while the world is drawn with one
    /// sample a pixel.
    /// </summary>
    public bool AntiAliasing { get; set; }

    /// <summary>The console's <c>aa</c>: anti-aliasing on or off for the run in place of the setting, or null.</summary>
    public bool? AntiAliasOverride { get; set; }

    /// <summary>Whether the frame being built is anti-aliased: <see cref="AntiAliasOverride"/>, else the setting.</summary>
    public bool AntiAliasDrawn => AntiAliasOverride ?? AntiAliasing;

    /// <summary>What the anti-aliasing shows: the picture, or its edges or weights (the console's <c>aa</c>).</summary>
    public SmaaPicture AntiAliasPicture { get; set; }

    /// <summary>Sets the camera for the frame being built.</summary>
    public void SetCamera(in CameraView camera) => Camera = camera;

    /// <summary>Sets the sky, sunlight, and air for the frame being built; they stay until set again.</summary>
    public void SetAtmosphere(in Atmosphere atmosphere) => Atmosphere = atmosphere;

    /// <summary>Sets the look for the frame being built; it stays until set again.</summary>
    public void SetLook(in RenderLook look) => Look = look;
}
