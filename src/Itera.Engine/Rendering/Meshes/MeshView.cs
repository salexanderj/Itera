using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The frame's view for what <see cref="MeshPass"/> draws, shared by everything that lists draws in it (the world's
/// blocks and models, <see cref="WorldMeshes"/>): the frustum (<see cref="InView"/>), the sun shadow cascades fitted to
/// it (<see cref="ShadowCascades.Fit"/>, <see cref="NearCascade"/>, <see cref="FarCascade"/>, and with
/// <c>Engine:Renderer:DistantShadowMetres</c> under a low sun <see cref="DistantCascade"/>) with the slot's lighting
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

    // The side planes of the view's frustum, facing in: left, right, bottom, top; each the inward normal (xyz) and the dot
    // product a point on it has with that normal, relative to the camera (w: 0 through the camera, as a perspective view's
    // are; an orthographic view's lie its half width or height out).
    private readonly Vector4[] _planes = new Vector4[4];
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
    public Vector3 Position { get; private set; }

    /// <summary>The unit view direction this frame.</summary>
    public Vector3 Forward { get; private set; }

    /// <summary>How far the view reaches this frame, in metres.</summary>
    public float Distance { get; private set; }

    /// <summary>True when the view's shadows are on this frame.</summary>
    public bool Shadows { get; private set; }

    /// <summary>True when the view is orthographic this frame (<see cref="ViewSwitches.Orthographic"/>).</summary>
    public bool Orthographic { get; private set; }

    /// <summary>Where the drawn depth starts along the view this frame (<see cref="ViewConstants.Near"/>).</summary>
    public float Near { get; private set; }

    /// <summary>Where an orthographic view's box ends along the view this frame (<see cref="ViewConstants.Far"/>).</summary>
    public float Far { get; private set; }

    /// <summary>
    /// How many pixels of the drawn image's height one metre across covers this frame: at one metre from the eye for a
    /// perspective view (divide by the distance along it), anywhere for an orthographic one. For choosing models' levels.
    /// </summary>
    public float PixelsPerMetre { get; private set; }

    /// <summary>The width of one texel of the near cascade's map this frame, in metres (0 without shadows).</summary>
    public float NearTexelMetres { get; private set; }

    /// <summary>The width of one texel of the far cascade's map this frame, in metres (0 without shadows).</summary>
    public float FarTexelMetres { get; private set; }

    /// <summary>The near cascade's casters this frame.</summary>
    public CascadeBounds NearCascade { get; } = new();

    /// <summary>The far cascade's casters this frame.</summary>
    public CascadeBounds FarCascade { get; } = new();

    /// <summary>The distant cascade's casters this frame, when <see cref="DistantShadows"/>.</summary>
    public CascadeBounds DistantCascade { get; } = new();

    /// <summary>
    /// True when the distant cascade is drawn this frame: the view's shadows on, <c>Engine:Renderer:DistantShadowMetres</c>
    /// set and reaching past the far cascade within the view's distance, and the sun under
    /// <see cref="DistantSunDegrees"/>.
    /// </summary>
    public bool DistantShadows { get; private set; }

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
        Position = view.Position;
        Forward = view.Forward;
        Distance = view.MaxDistance;
        Shadows = view.Has(ViewSwitches.Shadows);
        Orthographic = view.Orthographic;
        Near = view.Near;
        Far = view.Far;
        // The image's half height covers Up's length at a metre along the view (an orthographic box: in metres).
        var half = view.Up.Length();
        PixelsPerMetre = half > 0 ? view.Height / (2 * half) : 0;
        _offeredCount = 0;
        SetPlanes(view.Forward, view.Right, view.Up);
        WriteCascades(slot, in view);
    }

    /// <summary>
    /// True when the cube of half-size <paramref name="extent"/> at <paramref name="centre"/> may be seen: within the view
    /// distance ahead (an orthographic view: between its near and far ends) and inside each side plane.
    /// </summary>
    public bool InView(Vector3 centre, float extent)
    {
        var offset = centre - Position;
        var forward = Forward;
        var ahead = Vector3.Dot(offset, forward);
        var reach = extent * (MathF.Abs(forward.X) + MathF.Abs(forward.Y) + MathF.Abs(forward.Z));
        var (first, last) = Orthographic ? (Near, Far) : (0f, Distance);
        if (ahead + reach < first || ahead - reach > last)
        {
            return false;
        }

        foreach (var plane in _planes)
        {
            var spread = extent * (MathF.Abs(plane.X) + MathF.Abs(plane.Y) + MathF.Abs(plane.Z));
            if (Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), offset) - plane.W + spread < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A key that orders points nearest the viewer first, 0 or more: the squared distance from the eye, or how far past an
    /// orthographic view's near end the point lies along it (no farther than 0). For sorting this frame's draws.
    /// </summary>
    public float NearFirstKey(Vector3 point) =>
        Orthographic
            ? Math.Max(Vector3.Dot(point - Position, Forward) - Near, 0)
            : Vector3.DistanceSquared(point, Position);

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
    /// Fits the cascades to the view (<see cref="ShadowCascades.Fit"/>) and writes their record for <c>mesh.frag</c> and
    /// <c>mesh-shadow.vert</c>: the near and far ones, and the distant one from where the far one starts to fade to
    /// <c>Engine:Renderer:DistantShadowMetres</c> or the view's distance (<see cref="DistantShadows"/>).
    /// </summary>
    private void WriteCascades(int slot, in ViewConstants view)
    {
        // A sun that moves turns the maps' image axis as little as it turns (no jump as it passes overhead); a sun that
        // has never moved keeps the fit's own.
        var sun = view.SunDirection;
        if (_lastSun != Vector3.Zero && sun != _lastSun)
        {
            var previous = _imageAxis != Vector3.Zero ? _imageAxis : ShadowCascades.ImageAxis(_lastSun, Vector3.Zero);
            _imageAxis = ShadowCascades.ImageAxis(sun, previous);
        }

        _lastSun = sun;
        var reach = CasterReach(sun);
        var farEnd = Math.Max(Math.Min(FarShadowMetres, view.MaxDistance), 2 * NearShadowMetres);
        var farStart = NearShadowMetres - NearBlendMetres;
        // Each cascade's slice of the view's depth. An orthographic view's cascades lie round its middle, chosen by how
        // far before or behind it a point lies (view.glsl's viewCascadeDepth), so each reaches both ways, within the box.
        var (nearFirst, nearLast) = Slice(0, NearShadowMetres);
        var (farFirst, farLast) = Slice(farStart, farEnd);
        var near = ShadowCascades.Fit(
            view.Position,
            view.Forward,
            view.Right,
            view.Up,
            nearFirst,
            nearLast,
            view.SunDirection,
            _shadowMapTexels,
            reach,
            _imageAxis,
            Orthographic,
            view.Origin
        );
        var far = ShadowCascades.Fit(
            view.Position,
            view.Forward,
            view.Right,
            view.Up,
            farFirst,
            farLast,
            view.SunDirection,
            _shadowMapTexels,
            reach,
            _imageAxis,
            Orthographic,
            view.Origin
        );
        var farFadeStart = farEnd * (1 - FarFadeShare);
        var distantEnd = Math.Min(_distantShadowMetres, view.MaxDistance);
        var strength = DistantStrength(sun);
        // An orthographic view's cascades are chosen by how far before or behind its middle a point lies: a box that ends
        // before the far cascade fades would never read the distant one.
        var reaches = !Orthographic || Math.Max(-view.Near, view.Far) > farFadeStart;
        DistantShadows = Shadows && strength > 0 && distantEnd > farEnd && reaches;
        var distant = default(CascadeFit);
        if (DistantShadows)
        {
            var (distantFirst, distantLast) = Slice(farFadeStart, distantEnd);
            distant = ShadowCascades.Fit(
                view.Position,
                view.Forward,
                view.Right,
                view.Up,
                distantFirst,
                distantLast,
                view.SunDirection,
                _shadowMapTexels,
                reach * DistantCasterShare,
                _imageAxis,
                Orthographic,
                view.Origin
            );
            DistantCascade.Set(
                in distant,
                _shadowMapTexels,
                view.Position,
                view.Forward,
                _planes,
                distantFirst,
                distantLast,
                view.SunDirection
            );
        }

        NearCascade.Set(
            in near,
            _shadowMapTexels,
            view.Position,
            view.Forward,
            _planes,
            nearFirst,
            nearLast,
            view.SunDirection
        );
        FarCascade.Set(
            in far,
            _shadowMapTexels,
            view.Position,
            view.Forward,
            _planes,
            farFirst,
            farLast,
            view.SunDirection
        );
        NearTexelMetres = Shadows ? near.TexelMetres : 0;
        FarTexelMetres = Shadows ? far.TexelMetres : 0;
        _pass.LightingOf(slot) = new MeshLighting
        {
            Near = near.ViewProjection,
            Far = far.ViewProjection,
            NearEnd = NearShadowMetres,
            FarEnd = farEnd,
            NearTexelMetres = near.TexelMetres,
            FarTexelMetres = far.TexelMetres,
            Shadows = Shadows ? 1u : 0u,
            NearBlendStart = farStart,
            FarFadeStart = farFadeStart,
            Distant = distant.ViewProjection,
            DistantEnd = DistantShadows ? distantEnd : 0,
            DistantTexelMetres = distant.TexelMetres,
            DistantFadeStart = DistantShadows ? distantEnd * (1 - FarFadeShare) : 0,
            DistantStrength = DistantShadows ? strength : 0,
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
    /// The slice of view depth a cascade covers from <paramref name="first"/> to <paramref name="last"/>: those depths, or
    /// in an orthographic view the depths no farther than <paramref name="last"/> before or behind its middle within its
    /// box (a metre deep where the box misses them, so the fit has a slice; no point is then shaded from it).
    /// </summary>
    private (float First, float Last) Slice(float first, float last)
    {
        if (!Orthographic)
        {
            return (first, last);
        }

        var low = Math.Max(-last, Near);
        var high = Math.Min(last, Far);
        return (low, Math.Max(high, low + 1));
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

    /// <summary>
    /// The frustum's side planes from the view's axes (right and up reach the image's edges at unit forward, or
    /// orthographically at their length in metres from the middle).
    /// </summary>
    private void SetPlanes(Vector3 forward, Vector3 right, Vector3 up)
    {
        if (Orthographic)
        {
            var across = right.Length();
            var high = up.Length();
            _planes[0] = new Vector4(right / across, -across);
            _planes[1] = new Vector4(-right / across, -across);
            _planes[2] = new Vector4(up / high, -high);
            _planes[3] = new Vector4(-up / high, -high);
            return;
        }

        _planes[0] = new Vector4(InwardNormal(Vector3.Cross(forward - right, up), forward), 0);
        _planes[1] = new Vector4(InwardNormal(Vector3.Cross(forward + right, up), forward), 0);
        _planes[2] = new Vector4(InwardNormal(Vector3.Cross(forward - up, right), forward), 0);
        _planes[3] = new Vector4(InwardNormal(Vector3.Cross(forward + up, right), forward), 0);
    }

    private static Vector3 InwardNormal(Vector3 normal, Vector3 forward)
    {
        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, forward) < 0 ? -normal : normal;
    }
}
