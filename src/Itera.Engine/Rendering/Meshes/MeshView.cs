using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The frame's view for what <see cref="MeshPass"/> draws, shared by everything that lists draws in it (the world's
/// blocks and models, <see cref="WorldMeshes"/>): the frustum (<see cref="InView"/>), the sun shadow cascades fitted to
/// it (<see cref="ViewVolume"/>, <see cref="NearCascade"/>, <see cref="FarCascade"/>, and with
/// <c>Engine:Renderer:DistantShadowMetres</c> under a low sun <see cref="DistantCascade"/>; <see cref="Predict"/> gives
/// the same before the frame's <see cref="Begin"/>) with the slot's lighting
/// record for <c>mesh.frag</c> and <c>mesh-shadow.vert</c>, and the frame's point lights (docs/rendering-redesign.md,
/// stage 1.5 and phase 3). The drawers offer their lights (<see cref="Offer"/>: the world's glowing blocks), and
/// <see cref="End"/> the events' (<see cref="PointLights"/>: beams, muzzle flashes, blasts); those within
/// <see cref="LightGatherMetres"/> whose reach meets the frustum are kept, and the light budget's brightest at the camera
/// (<c>Engine:Renderer:MostLights</c>, at most <see cref="MeshPass.MostLights"/>) go into the slot's list, brightest
/// first, which the mesh pass sorts into the view's clusters. Each frame: <see cref="Begin"/>, the drawers' lists and
/// offers, then <see cref="End"/>. Main thread, between taking the frame request and publishing the frame; allocates
/// nothing.
/// </summary>
public sealed class MeshView
{
    /// <summary>The near shadow cascade covers view depths from 0 to this; the far one from here on.</summary>
    internal const float NearShadowMetres = 30;

    /// <summary>The far cascade ends at this view depth, or at the view's distance when that is less.</summary>
    internal const float FarShadowMetres = 200;

    /// <summary>
    /// The band before <see cref="NearShadowMetres"/> where the near cascade blends into the far one, which covers it too.
    /// </summary>
    internal const float NearBlendMetres = 5;

    /// <summary>
    /// The share of the far cascade's end over which its shadows fade out, or blend into the distant cascade's, which
    /// starts there; and the share of the distant cascade's end over which its own fade out.
    /// </summary>
    internal const float FarFadeShare = 0.1f;

    /// <summary>
    /// The distant cascade is drawn only while the sun is lower than this many degrees over the horizon: a higher sun's
    /// shadows are short, a 4 m boulder's under 7 m, a few texels of its map (about 2.4 m at 2 km).
    /// </summary>
    internal const float DistantSunDegrees = 30;

    /// <summary>The sun's height, in degrees, from which down the distant cascade's shadows are drawn whole.</summary>
    internal const float DistantFullDegrees = 25;

    /// <summary>
    /// The distant cascade's casters reach this many times as far towards the sun as the others' (<see cref="CasterReach"/>):
    /// under a low sun, ground tens of metres high casts hundreds of metres.
    /// </summary>
    internal const float DistantCasterShare = 3;

    /// <summary>
    /// How far towards the sun beyond a cascade's slice a caster still casts into it with the sun at
    /// <see cref="CasterElevationDegrees"/> or higher; a lower sun's longer shadows reach farther, up to
    /// <see cref="MostCasterMetres"/> (<see cref="CasterReach"/>).
    /// </summary>
    internal const float CasterMetres = 100;

    /// <summary>The sun's height, in degrees, down to which <see cref="CasterMetres"/> reaches far enough.</summary>
    internal const float CasterElevationDegrees = 30;

    /// <summary>The casters' reach towards a sun on the horizon.</summary>
    internal const float MostCasterMetres = 300;

    private static readonly float s_casterTangent = MathF.Tan(CasterElevationDegrees * MathF.PI / 180);

    /// <summary>Lights farther than this from the camera are not gathered, in metres.</summary>
    internal const float LightGatherMetres = 48;

    /// <summary>A light reaches where its power over the distance squared falls to this, at most <see cref="MostReachMetres"/>.</summary>
    private const float LightCutoff = 0.02f;

    private const float MostReachMetres = 16;

    /// <summary>Lights offered in one frame at most; more are not gathered.</summary>
    private const int MostOffered = 4096;

    private readonly IMeshDrawTarget _pass;
    private readonly int _shadowMapTexels;
    private readonly float _distantShadowMetres;
    private readonly int _lightBudget;

    // The frame's frustum and cascades.
    private readonly ViewVolume _volume = new();
    private readonly MeshLight[] _offered = new MeshLight[MostOffered];
    private readonly float[] _offeredRanks = new float[MostOffered];
    private int _offeredCount;

    // The last frame's sun, and the shadow maps' image axis since the sun first moved (zero until then: the fit's own).
    private Vector3 _lastSun;
    private Vector3 _imageAxis;

    public MeshView(MeshPass pass, IOptions<EngineOptions> options)
        : this((IMeshDrawTarget)pass, options) { }

    /// <summary>The view over a draw target of a test's own.</summary>
    internal MeshView(IMeshDrawTarget pass, IOptions<EngineOptions> options)
    {
        _pass = pass;
        var renderer = options.Value.Renderer;
        _shadowMapTexels = renderer.ShadowMapTexels;
        _distantShadowMetres = renderer.DistantShadowMetres;
        _lightBudget = Math.Clamp(renderer.MostLights, 1, MeshPass.MostLights);
    }

    /// <summary>The eye's position this frame, in world metres; an orthographic view's middle.</summary>
    public Vector3 Position => _volume.Position;

    /// <summary>The unit view direction this frame.</summary>
    public Vector3 Forward => _volume.Forward;

    /// <summary>How far the view reaches this frame, in metres.</summary>
    public float Distance => _volume.Distance;

    /// <summary>True when the view's shadows are on this frame.</summary>
    public bool Shadows => _volume.Shadows;

    /// <summary>True when the view is orthographic this frame (<see cref="ViewSwitches.Orthographic"/>).</summary>
    public bool Orthographic => _volume.Orthographic;

    /// <summary>Where the drawn depth starts along the view this frame (<see cref="ViewConstants.Near"/>).</summary>
    public float Near => _volume.Near;

    /// <summary>Where an orthographic view's box ends along the view this frame (<see cref="ViewConstants.Far"/>).</summary>
    public float Far => _volume.Far;

    /// <summary>
    /// How many pixels of the drawn image's height one metre across covers this frame: at one metre from the eye for a
    /// perspective view (divide by the distance along it), anywhere for an orthographic one. For choosing models' levels.
    /// </summary>
    public float PixelsPerMetre => _volume.PixelsPerMetre;

    /// <summary>The width of one texel of the near cascade's map this frame, in metres (0 without shadows).</summary>
    public float NearTexelMetres => _volume.NearTexelMetres;

    /// <summary>The width of one texel of the far cascade's map this frame, in metres (0 without shadows).</summary>
    public float FarTexelMetres => _volume.FarTexelMetres;

    /// <summary>The near cascade's casters this frame.</summary>
    public CascadeBounds NearCascade => _volume.NearCascade;

    /// <summary>The far cascade's casters this frame.</summary>
    public CascadeBounds FarCascade => _volume.FarCascade;

    /// <summary>The distant cascade's casters this frame, when <see cref="DistantShadows"/>.</summary>
    public CascadeBounds DistantCascade => _volume.DistantCascade;

    /// <summary>
    /// True when the distant cascade is drawn this frame: the view's shadows on, <c>Engine:Renderer:DistantShadowMetres</c>
    /// set and reaching past the far cascade within the view's distance, and the sun under
    /// <see cref="DistantSunDegrees"/>.
    /// </summary>
    public bool DistantShadows => _volume.DistantShadows;

    /// <summary>Lights the last <see cref="End"/> wrote into its slot's list.</summary>
    public int LastLights { get; private set; }

    /// <summary>Lights the events offered at the last <see cref="End"/>.</summary>
    public int LastEventLights { get; private set; }

    /// <summary>
    /// Starts the frame's view: its frustum, the cascades fitted to it, and the slot's lighting record (no lights yet; the
    /// world adds its sky field's part). Main thread, after taking the frame request.
    /// </summary>
    public void Begin(int slot, in ViewConstants view)
    {
        // A sun that moves turns the maps' image axis as little as it turns (no jump as it passes overhead); a sun that
        // has never moved keeps the fit's own.
        _imageAxis = ImageAxisFor(view.SunDirection);
        _lastSun = view.SunDirection;
        _offeredCount = 0;
        _volume.Set(in view, _imageAxis, _shadowMapTexels, _distantShadowMetres);
        WriteLighting(slot);
    }

    /// <summary>
    /// Sets <paramref name="into"/> to the volume <see cref="Begin"/> would give <paramref name="view"/> now: the same
    /// frustum and cascades, with the image axis the sun this view carries would take. Changes nothing of this view.
    /// Main thread, before this frame's <see cref="Begin"/> (<see cref="MeshPoseCulling"/>).
    /// </summary>
    public void Predict(in ViewConstants view, ViewVolume into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Set(in view, ImageAxisFor(view.SunDirection), _shadowMapTexels, _distantShadowMetres);
    }

    /// <summary>
    /// True when the cube of half-size <paramref name="extent"/> at <paramref name="centre"/> may be seen: within the view
    /// distance ahead (an orthographic view: between its near and far ends) and inside each side plane.
    /// </summary>
    public bool InView(Vector3 centre, float extent) => _volume.InView(centre, extent);

    /// <summary>
    /// A key that orders points nearest the viewer first, 0 or more: the squared distance from the eye, or how far past an
    /// orthographic view's near end the point lies along it (no farther than 0). For sorting this frame's draws.
    /// </summary>
    public float NearFirstKey(Vector3 point) => _volume.NearFirstKey(point);

    /// <summary>
    /// Offers a light of <paramref name="power"/> at <paramref name="position"/> for the frame's list: kept when it lies
    /// within <see cref="LightGatherMetres"/> and its reach meets the view's frustum, and the frame has not been offered
    /// <see cref="MostOffered"/> already.
    /// </summary>
    public void Offer(Vector3 position, Vector3 colour, float power)
    {
        if (_offeredCount == MostOffered)
        {
            return;
        }

        var distanceSquared = Vector3.DistanceSquared(position, Position);
        if (distanceSquared > LightGatherMetres * LightGatherMetres)
        {
            return;
        }

        var reach = Math.Min(MathF.Sqrt(power / LightCutoff), MostReachMetres);
        if (!InView(position, reach))
        {
            return;
        }

        _offered[_offeredCount] = new MeshLight
        {
            Position = position,
            Power = power,
            Colour = colour,
            Reach = reach,
        };
        // Brightest at the camera first: the sort is ascending.
        _offeredRanks[_offeredCount++] = -power / (distanceSquared + 1);
    }

    /// <summary>
    /// Ends the frame's view: offers the events' <paramref name="lights"/> after the drawers' own, then writes the light
    /// budget's brightest offered into the slot's list, brightest at the camera first, and their count into its lighting
    /// record; each cluster of the view then keeps those that matter most to it (<c>mesh-clusters.comp</c>).
    /// </summary>
    public void End(int slot, ReadOnlySpan<PointLight> lights)
    {
        LastEventLights = lights.Length;
        foreach (ref readonly var light in lights)
        {
            if (_offeredCount == MostOffered)
            {
                break;
            }

            Offer(light.Position, light.Colour, light.Power);
        }

        var count = Math.Min(_offeredCount, _lightBudget);
        LastLights = count;
        Array.Sort(_offeredRanks, _offered, 0, _offeredCount);
        _offered.AsSpan(0, count).CopyTo(_pass.LightsOf(slot));
        _offeredCount = 0;
        _pass.LightingOf(slot).LightCount = (uint)count;
    }

    /// <summary>
    /// The shadow maps' image axis for a sun along <paramref name="sun"/>: the last axis turned square to it when the sun
    /// moved since the last frame (the fit's own, turned, when it first moves), else the last axis (zero: the fit's own).
    /// </summary>
    private Vector3 ImageAxisFor(Vector3 sun)
    {
        if (_lastSun == Vector3.Zero || sun == _lastSun)
        {
            return _imageAxis;
        }

        var previous = _imageAxis != Vector3.Zero ? _imageAxis : ShadowCascades.ImageAxis(_lastSun, Vector3.Zero);
        return ShadowCascades.ImageAxis(sun, previous);
    }

    /// <summary>
    /// Writes the cascades' record for <c>mesh.frag</c> and <c>mesh-shadow.vert</c>: the near and far ones, and the
    /// distant one from where the far one starts to fade to <c>Engine:Renderer:DistantShadowMetres</c> or the view's
    /// distance (<see cref="DistantShadows"/>).
    /// </summary>
    private void WriteLighting(int slot)
    {
        var fits = _volume.Cascades;
        var distant = DistantShadows;
        _pass.LightingOf(slot) = new MeshLighting
        {
            Near = fits.Near.ViewProjection,
            Far = fits.Far.ViewProjection,
            NearEnd = NearShadowMetres,
            FarEnd = fits.FarEnd,
            NearTexelMetres = fits.Near.TexelMetres,
            FarTexelMetres = fits.Far.TexelMetres,
            Shadows = Shadows ? 1u : 0u,
            NearBlendStart = fits.NearBlendStart,
            FarFadeStart = fits.FarFadeStart,
            Distant = fits.Distant.ViewProjection,
            DistantEnd = distant ? fits.DistantEnd : 0,
            DistantTexelMetres = fits.Distant.TexelMetres,
            DistantFadeStart = distant ? fits.DistantEnd * (1 - FarFadeShare) : 0,
            DistantStrength = distant ? fits.DistantStrength : 0,
        };
    }

    /// <summary>
    /// How much of the distant cascade's shadow a sun along <paramref name="towardsSun"/> (a unit vector) draws: 0 at
    /// <see cref="DistantSunDegrees"/> over the horizon and higher, 1 at <see cref="DistantFullDegrees"/> and lower, and
    /// between them in step with its height, so the shadows fade in as the sun sinks.
    /// </summary>
    internal static float DistantStrength(Vector3 towardsSun)
    {
        var degrees = MathF.Asin(Math.Clamp(towardsSun.Y, -1, 1)) * 180 / MathF.PI;
        return Math.Clamp((DistantSunDegrees - degrees) / (DistantSunDegrees - DistantFullDegrees), 0, 1);
    }

    /// <summary>
    /// Forgets the sun: the next frame's maps take the fit's own image axis, as at the run's start, so a match whose sun
    /// never moves draws the same shadows whatever the last match's sun did. When a match ends.
    /// </summary>
    public void ForgetSun()
    {
        _lastSun = Vector3.Zero;
        _imageAxis = Vector3.Zero;
    }

    /// <summary>
    /// How far towards a sun along <paramref name="towardsSun"/> casters still cast into a cascade: what keeps a caster as
    /// tall as <see cref="CasterMetres"/> reaches at <see cref="CasterElevationDegrees"/> (its shadow's length grows as the
    /// height's tangent falls), from <see cref="CasterMetres"/> to <see cref="MostCasterMetres"/>.
    /// </summary>
    internal static float CasterReach(Vector3 towardsSun)
    {
        var sine = towardsSun.Y;
        if (sine <= 0)
        {
            return MostCasterMetres;
        }

        var tangent = sine / MathF.Sqrt(Math.Max(1 - (sine * sine), 1e-12f));
        return Math.Clamp(CasterMetres * s_casterTangent / tangent, CasterMetres, MostCasterMetres);
    }
}
