using System.Numerics;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// A view's volume as the draw lists cull by it: its frustum (<see cref="InView"/>) and the sun's shadow cascades fitted
/// to it (<see cref="ShadowCascades.Fit"/>; <see cref="NearCascade"/>, <see cref="FarCascade"/>, and with
/// <c>Engine:Renderer:DistantShadowMetres</c> under a low sun <see cref="DistantCascade"/>), with the values the slot's
/// lighting record takes from the fit (<see cref="Cascades"/>). <see cref="MeshView"/> sets one each frame from the frame's
/// view, and <see cref="MeshPoseCulling"/> one from the view the frame will have, so what is posed is culled by the same
/// volume the lists cull by. Pure maths; allocates nothing.
/// </summary>
public sealed class ViewVolume
{
    // The side planes of the view's frustum, facing in: left, right, bottom, top; each the inward normal (xyz) and the dot
    // product a point on it has with that normal, relative to the camera (w: 0 through the camera, as a perspective view's
    // are; an orthographic view's lie its half width or height out).
    private readonly Vector4[] _planes = new Vector4[4];

    /// <summary>The eye's position, in world metres; an orthographic view's middle.</summary>
    public Vector3 Position { get; private set; }

    /// <summary>The unit view direction.</summary>
    public Vector3 Forward { get; private set; }

    /// <summary>How far the view reaches, in metres.</summary>
    public float Distance { get; private set; }

    /// <summary>True when the view's shadows are on.</summary>
    public bool Shadows { get; private set; }

    /// <summary>True when the view is orthographic (<see cref="ViewSwitches.Orthographic"/>).</summary>
    public bool Orthographic { get; private set; }

    /// <summary>Where the drawn depth starts along the view (<see cref="ViewConstants.Near"/>).</summary>
    public float Near { get; private set; }

    /// <summary>Where an orthographic view's box ends along the view (<see cref="ViewConstants.Far"/>).</summary>
    public float Far { get; private set; }

    /// <summary>The near cascade's casters.</summary>
    public CascadeBounds NearCascade { get; } = new();

    /// <summary>The far cascade's casters.</summary>
    public CascadeBounds FarCascade { get; } = new();

    /// <summary>The distant cascade's casters, when <see cref="DistantShadows"/>.</summary>
    public CascadeBounds DistantCascade { get; } = new();

    /// <summary>
    /// True when the distant cascade is drawn: the view's shadows on, <c>Engine:Renderer:DistantShadowMetres</c> set and
    /// reaching past the far cascade within the view's distance, and the sun under <see cref="MeshView.DistantSunDegrees"/>.
    /// </summary>
    public bool DistantShadows { get; private set; }

    /// <summary>
    /// How many pixels of the drawn image's height one metre across covers: at one metre from the eye for a perspective
    /// view (divide by the distance along it), anywhere for an orthographic one. For choosing models' levels.
    /// </summary>
    public float PixelsPerMetre { get; private set; }

    /// <summary>The width of one texel of the near cascade's map, in metres (0 without shadows).</summary>
    public float NearTexelMetres { get; private set; }

    /// <summary>The width of one texel of the far cascade's map, in metres (0 without shadows).</summary>
    public float FarTexelMetres { get; private set; }

    /// <summary>The cascades' fits and the view depths their shading starts, blends, and fades at.</summary>
    internal CascadeFits Cascades { get; private set; }

    /// <summary>
    /// Sets the volume of <paramref name="view"/>: its frustum (<see cref="SetFrustum"/>), and its cascades fitted to it
    /// with the shadow maps' image axis <paramref name="imageAxis"/> (zero for the fit's own), <paramref name="mapTexels"/>
    /// wide, the distant one reaching <paramref name="distantShadowMetres"/> (0 for none).
    /// </summary>
    public void Set(in ViewConstants view, Vector3 imageAxis, int mapTexels, float distantShadowMetres)
    {
        SetFrustum(in view);
        FitCascades(in view, imageAxis, mapTexels, distantShadowMetres);
    }

    /// <summary>
    /// Sets the frustum of <paramref name="view"/> alone, for a view that only <see cref="InView"/> is asked of (an image
    /// view, which draws no shadows); the cascades are left as they were.
    /// </summary>
    public void SetFrustum(in ViewConstants view)
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
        SetPlanes(view.Forward, view.Right, view.Up);
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
    /// orthographic view's near end the point lies along it (no farther than 0).
    /// </summary>
    public float NearFirstKey(Vector3 point) =>
        Orthographic
            ? Math.Max(Vector3.Dot(point - Position, Forward) - Near, 0)
            : Vector3.DistanceSquared(point, Position);

    /// <summary>
    /// Fits the cascades to the view (<see cref="ShadowCascades.Fit"/>): the near and far ones, and the distant one from
    /// where the far one starts to fade to <paramref name="distantShadowMetres"/> or the view's distance.
    /// </summary>
    private void FitCascades(in ViewConstants view, Vector3 imageAxis, int mapTexels, float distantShadowMetres)
    {
        var sun = view.SunDirection;
        var reach = MeshView.CasterReach(sun);
        var farEnd = Math.Max(Math.Min(MeshView.FarShadowMetres, view.MaxDistance), 2 * MeshView.NearShadowMetres);
        var farStart = MeshView.NearShadowMetres - MeshView.NearBlendMetres;
        // Each cascade's slice of the view's depth. An orthographic view's cascades lie round its middle, chosen by how
        // far before or behind it a point lies (view.glsl's viewCascadeDepth), so each reaches both ways, within the box.
        var (nearFirst, nearLast) = Slice(0, MeshView.NearShadowMetres);
        var (farFirst, farLast) = Slice(farStart, farEnd);
        var near = ShadowCascades.Fit(
            view.Position,
            view.Forward,
            view.Right,
            view.Up,
            nearFirst,
            nearLast,
            sun,
            mapTexels,
            reach,
            imageAxis,
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
            sun,
            mapTexels,
            reach,
            imageAxis,
            Orthographic,
            view.Origin
        );
        var farFadeStart = farEnd * (1 - MeshView.FarFadeShare);
        var distantEnd = Math.Min(distantShadowMetres, view.MaxDistance);
        var strength = MeshView.DistantStrength(sun);
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
                sun,
                mapTexels,
                reach * MeshView.DistantCasterShare,
                imageAxis,
                Orthographic,
                view.Origin
            );
            DistantCascade.Set(
                in distant,
                mapTexels,
                view.Position,
                view.Forward,
                _planes,
                distantFirst,
                distantLast,
                sun
            );
        }

        NearCascade.Set(in near, mapTexels, view.Position, view.Forward, _planes, nearFirst, nearLast, sun);
        FarCascade.Set(in far, mapTexels, view.Position, view.Forward, _planes, farFirst, farLast, sun);
        NearTexelMetres = Shadows ? near.TexelMetres : 0;
        FarTexelMetres = Shadows ? far.TexelMetres : 0;
        Cascades = new CascadeFits(near, far, distant, farEnd, farStart, farFadeStart, distantEnd, strength);
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

/// <summary>
/// A view's cascades as <see cref="ViewVolume"/> fitted them: the near, far, and distant fits (the distant one default when
/// not drawn), where the far one ends, where the near one starts to blend into it, where the far one starts to fade,
/// where the distant one ends, and how strong its shadows are under the sun.
/// </summary>
internal readonly record struct CascadeFits(
    CascadeFit Near,
    CascadeFit Far,
    CascadeFit Distant,
    float FarEnd,
    float NearBlendStart,
    float FarFadeStart,
    float DistantEnd,
    float DistantStrength
);
