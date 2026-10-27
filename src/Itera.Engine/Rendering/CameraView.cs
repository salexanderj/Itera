using System.Numerics;
using Itera.Engine.Components;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Rendering;

/// <summary>How a camera projects what it sees onto the image.</summary>
public enum CameraProjection
{
    /// <summary>Rays from the eye through the image, spread by the field of view: nearer is larger.</summary>
    Perspective,

    /// <summary>
    /// Parallel rays along the view, the image a fixed size in metres: the same size at any depth (a map, an
    /// overhead view). The camera's position is the middle of what it shows.
    /// </summary>
    Orthographic,
}

/// <summary>
/// Where a frame is seen from, in metres. The camera looks along -Z of <paramref name="Orientation"/> with +Y up and +X
/// right (right-handed, as <see cref="Matrix4x4.CreateLookAt"/>).
/// </summary>
/// <param name="Position">
/// Eye position in world metres; for an orthographic view, the middle of what it shows, from which its fog, the
/// terrain's levels, and its lights are measured as a perspective view's are from the eye.
/// </param>
/// <param name="Orientation">Rotation from camera space to world space.</param>
/// <param name="VerticalFovRadians">Vertical field of view (perspective).</param>
/// <param name="NearMetres">
/// Distance to the near plane along the view; nothing nearer is drawn. Perspective: above 0. Orthographic: where its box
/// starts, behind the position when negative.
/// </param>
/// <param name="Projection">Perspective, or orthographic.</param>
/// <param name="HeightMetres">The image's height in metres (orthographic); its width follows the aspect ratio.</param>
/// <param name="FarMetres">
/// Where an orthographic view's box ends along the view; nothing farther is drawn. A perspective view has no far plane:
/// it reaches the view distance.
/// </param>
public readonly record struct CameraView(
    Vector3 Position,
    Quaternion Orientation,
    float VerticalFovRadians,
    float NearMetres,
    CameraProjection Projection = CameraProjection.Perspective,
    float HeightMetres = 0,
    float FarMetres = 0
)
{
    /// <summary>At the origin, looking along -Z, 90 degrees, near plane at 5 cm. Used until a camera system sets one.</summary>
    public static CameraView Default =>
        new(Vector3.Zero, Quaternion.Identity, MathF.PI / 2, ViewConstants.DefaultNearMetres);

    /// <summary>True for an orthographic view.</summary>
    public bool Orthographic => Projection == CameraProjection.Orthographic;

    /// <summary>This view seen through <paramref name="lens"/> instead of its own lens: its place and turn kept.</summary>
    public CameraView Through(in CameraLens lens) =>
        new(
            Position,
            Orientation,
            lens.VerticalFovRadians,
            lens.NearMetres,
            lens.Projection,
            lens.HeightMetres,
            lens.FarMetres
        );

    /// <summary>The direction the camera looks.</summary>
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Orientation);

    /// <summary>The camera's right.</summary>
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Orientation);

    /// <summary>The camera's up.</summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Orientation);
}

/// <summary>
/// Where a camera stands and turns, without its lens: a place in local metres, and the yaw and pitch in radians that
/// <see cref="Systems.CameraExtractSystem.ToView"/> turns a camera's pose by (the console's <c>viewpoint</c>).
/// </summary>
public readonly record struct CameraPlace(Vector3 Position, float Yaw, float Pitch)
{
    /// <summary>The turn from camera space to world space.</summary>
    public Quaternion Orientation => Quaternion.CreateFromYawPitchRoll(Yaw, Pitch, 0);
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
    private readonly HazeGlow[] _glows = new HazeGlow[HazeGlow.Most];

    // The frame being built: the screen's size (0 for none: no picture is drawn), the view's distance, and the origin.
    private uint _frameWidth;
    private uint _frameHeight;
    private float _frameDistance;
    private VoxelCoord _frameOrigin;

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
        CameraOverride = null;
        Atmosphere = _clearSky;
        Look = RenderLook.Default;
        GlowCount = 0;
        ScreenDim = 0;
    }

    /// <summary>The camera the next frame is drawn from, as a frame system set it; it is drawn as <see cref="DrawnCamera"/>.</summary>
    public CameraView Camera { get; private set; } = CameraView.Default;

    /// <summary>The console's <c>lens</c>: a lens for the run in place of the camera's own, or null.</summary>
    public CameraLens? LensOverride { get; set; }

    /// <summary>
    /// The console's <c>viewpoint</c>: where the frames are drawn from for the match in place of the camera's own place and
    /// turn, or null. The camera's lens stays, under <see cref="LensOverride"/>. What reads <see cref="Camera"/> (the
    /// listener, the streaming) still follows the camera's own. It moves with the origin, and a match's end clears it.
    /// </summary>
    public CameraPlace? CameraOverride { get; set; }

    /// <summary>
    /// The camera the next frame is drawn from: <see cref="Camera"/>, at <see cref="CameraOverride"/>'s place and turn,
    /// through <see cref="LensOverride"/>, each when set.
    /// </summary>
    public CameraView DrawnCamera
    {
        get
        {
            var camera = CameraOverride is { } place
                ? Camera with
                {
                    Position = place.Position,
                    Orientation = place.Orientation,
                }
                : Camera;
            return LensOverride is { } lens ? camera.Through(in lens) : camera;
        }
    }

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

    /// <summary>How many of <see cref="Glows"/> the next frame draws, up to <see cref="HazeGlow.Most"/>.</summary>
    public int GlowCount { get; private set; }

    /// <summary>The places where the air glows in the next frame, as a frame system set them; none until one does.</summary>
    public ReadOnlySpan<HazeGlow> Glows => _glows.AsSpan(0, GlowCount);

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
    public void SetCamera(in CameraView camera)
    {
        Camera = camera;
        CameraSet = true;
    }

    /// <summary>
    /// True once a frame system has set the camera (<see cref="SetCamera"/>) since the frame being built started
    /// (<see cref="BeginFrame"/>).
    /// </summary>
    public bool CameraSet { get; private set; }

    /// <summary>The view's distance of the frame being built, in metres (<see cref="BeginFrame"/>).</summary>
    internal float FrameDistance => _frameDistance;

    /// <summary>The match's origin of the frame being built, in absolute centimetres (<see cref="BeginFrame"/>).</summary>
    internal VoxelCoord FrameOrigin => _frameOrigin;

    /// <summary>
    /// Starts a frame, before the frame systems: drawn on a <paramref name="width"/> x <paramref name="height"/> screen (0
    /// x 0 for a frame without a picture), as far as <paramref name="maxDistance"/>, local to <paramref name="origin"/>; no
    /// camera set yet (<see cref="CameraSet"/>).
    /// </summary>
    internal void BeginFrame(uint width, uint height, float maxDistance, VoxelCoord origin)
    {
        _frameWidth = width;
        _frameHeight = height;
        _frameDistance = maxDistance;
        _frameOrigin = origin;
        CameraSet = false;
    }

    /// <summary>
    /// The constants the frame being built will be drawn with (<see cref="CreateView"/>), from the camera, sun, and
    /// atmosphere set so far: false for a frame without a picture, or when no frame system has set the camera since the
    /// frame started.
    /// </summary>
    public bool TryFrameView(out ViewConstants view)
    {
        if (_frameWidth == 0 || _frameHeight == 0 || !CameraSet)
        {
            view = default;
            return false;
        }

        view = CreateView(_frameWidth, _frameHeight, 0, _frameDistance, _frameOrigin);
        return true;
    }

    /// <summary>
    /// The constants for drawing <see cref="DrawnCamera"/> on a <paramref name="width"/> x <paramref name="height"/> screen
    /// in frame slot <paramref name="slot"/>, with the sun, atmosphere, shadows, render scale, and debug views set now, as
    /// far as <paramref name="maxDistance"/>, local to <paramref name="origin"/> (<see cref="ViewConstants.Create"/>).
    /// </summary>
    internal ViewConstants CreateView(uint width, uint height, int slot, float maxDistance, VoxelCoord origin)
    {
        var camera = DrawnCamera;
        var atmosphere = Atmosphere;
        return ViewConstants.Create(
            in camera,
            width,
            height,
            slot,
            DrawnSun,
            in atmosphere,
            maxDistance,
            Shadows,
            origin,
            RenderScale,
            DebugViews
        );
    }

    /// <summary>
    /// The constants for drawing image view <paramref name="info"/> from <paramref name="camera"/> in frame slot
    /// <paramref name="slot"/>: at its size, with the sun and atmosphere set now, without shadows, as far as
    /// <paramref name="maxDistance"/>, local to <paramref name="origin"/>.
    /// </summary>
    internal ViewConstants CreateImageView(
        in CameraView camera,
        in RenderViewInfo info,
        int slot,
        float maxDistance,
        VoxelCoord origin
    )
    {
        var atmosphere = Atmosphere;
        return ViewConstants.Create(
            in camera,
            (uint)info.Width,
            (uint)info.Height,
            slot,
            DrawnSun,
            in atmosphere,
            maxDistance,
            shadows: false,
            origin
        );
    }

    /// <summary>
    /// The match's origin moved by <paramref name="metres"/>: the camera last set moves the other way, so what reads it
    /// before the next frame sets it (the listener, the console's commands) has it in the new origin (<c>OriginShifts</c>).
    /// <see cref="CameraOverride"/> moves with it, as it stays where it is in the world.
    /// </summary>
    public void ShiftCamera(Vector3 metres)
    {
        Camera = Camera with { Position = Camera.Position - metres };
        if (CameraOverride is { } place)
        {
            CameraOverride = place with { Position = place.Position - metres };
        }
    }

    /// <summary>Sets the sky, sunlight, and air for the frame being built; they stay until set again.</summary>
    public void SetAtmosphere(in Atmosphere atmosphere) => Atmosphere = atmosphere;

    /// <summary>Sets the look for the frame being built; it stays until set again.</summary>
    public void SetLook(in RenderLook look) => Look = look;

    /// <summary>
    /// Sets the haze glows for the frame being built, the first <see cref="HazeGlow.Most"/> of
    /// <paramref name="glows"/>; they stay until set again. Allocates nothing.
    /// </summary>
    public void SetGlows(ReadOnlySpan<HazeGlow> glows)
    {
        var count = Math.Min(glows.Length, HazeGlow.Most);
        glows[..count].CopyTo(_glows);
        GlowCount = count;
    }
}
