using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// Per-frame camera data in push constants (<c>view.glsl</c>, <c>layout(push_constant, scalar)</c>). A perspective pixel's
/// ray direction is <c>Forward + x * Right + y * Up</c> for x and y in [-1, 1], y up: <see cref="Right"/> and
/// <see cref="Up"/> are scaled by the field of view and the aspect ratio. An orthographic view
/// (<see cref="ViewSwitches.Orthographic"/>) has parallel rays along <see cref="Forward"/> from
/// <c>Position + x * Right + y * Up</c>: <see cref="Right"/> and <see cref="Up"/> are the image's half width and half
/// height in metres. Built on the main thread for each frame (<see cref="Create"/>) and pushed by the render thread.
/// Field order and types match the GLSL block; <c>GpuLayoutTests</c> checks them. It is 116 bytes of the 128 the spec
/// guarantees for push constants; the switches share one word's bits (<see cref="Switches"/>), and a new switch takes a
/// bit there.
/// </summary>
public struct ViewConstants
{
    /// <summary>Eye position in world metres; an orthographic view's middle.</summary>
    public Vector3 Position;

    /// <summary>Unit view direction.</summary>
    public Vector3 Forward;

    /// <summary>
    /// Camera right, scaled so x = 1 is the right edge of the image: at unit depth (perspective), or in metres from the
    /// position (orthographic).
    /// </summary>
    public Vector3 Right;

    /// <summary>Camera up, scaled so y = 1 is the top edge of the image, as <see cref="Right"/>.</summary>
    public Vector3 Up;

    /// <summary>Unit direction towards the sun.</summary>
    public Vector3 SunDirection;

    /// <summary>Linear colour of the sky at the horizon (<see cref="Atmosphere.SkyHorizon"/>).</summary>
    public Vector3 SkyHorizon;

    /// <summary>Frame slot the frame is drawn in, for per-slot buffers.</summary>
    public uint Slot;

    /// <summary>How far the world is drawn, in metres; beyond it is sky, and fog grows over the last 40 %.</summary>
    public float MaxDistance;

    /// <summary>The view's switches, a bit each (<c>switches</c> and <c>viewSwitch</c> in view.glsl).</summary>
    public ViewSwitches Switches;

    /// <summary>Width of the image drawn, in pixels: the screen's, scaled by the render scale.</summary>
    public uint Width;

    /// <summary>Height of the image drawn, in pixels: the screen's, scaled by the render scale.</summary>
    public uint Height;

    /// <summary>Linear colour and strength of direct sunlight (<see cref="Atmosphere.SunColour"/>).</summary>
    public Vector3 SunColour;

    /// <summary>Haze, overcast, and grade, a byte each (<see cref="Atmosphere.PackAir"/>; GLSL <c>unpackUnorm4x8</c>).</summary>
    public uint Air;

    /// <summary>
    /// Where the drawn depth starts along the view, in metres from the position: a perspective view's near plane (the
    /// depth stored is it over the distance along the view, so the sky is 0 and nearer is greater), or where an
    /// orthographic view's box starts (negative: behind the position).
    /// </summary>
    public float Near;

    /// <summary>
    /// Where an orthographic view's box ends along the view (the depth stored falls evenly from 1 at <see cref="Near"/>
    /// to 0 here); 0 for a perspective view, which has no far plane.
    /// </summary>
    public float Far;

    /// <summary>True when every switch in <paramref name="switches"/> is on.</summary>
    public readonly bool Has(ViewSwitches switches) => (Switches & switches) == switches;

    /// <summary>True for an orthographic view.</summary>
    public readonly bool Orthographic => Has(ViewSwitches.Orthographic);

    /// <summary>
    /// The clip position of a point at <paramref name="offset"/> from <see cref="Position"/>, as view.glsl's
    /// <c>viewClip</c> puts it (x and y over w in [-1, 1] across the image, y up; z over w the depth stored).
    /// </summary>
    public readonly Vector4 Clip(Vector3 offset)
    {
        var depth = Vector3.Dot(offset, Forward);
        var x = Vector3.Dot(offset, Right) / Right.LengthSquared();
        var y = Vector3.Dot(offset, Up) / Up.LengthSquared();
        return Orthographic ? new Vector4(x, y, (Far - depth) / (Far - Near), 1) : new Vector4(x, y, Near, depth);
    }

    /// <summary>
    /// The depth along the view, in metres from <see cref="Position"/>, of a stored depth above 0 (0 is the sky), as
    /// view.glsl's <c>viewDepthOf</c> gives it.
    /// </summary>
    public readonly float DepthOf(float stored) => Orthographic ? Far - stored * (Far - Near) : Near / stored;

    /// <summary>
    /// The engine's sun, high (58 degrees) and from +x and +z, which an atmosphere without a sun direction gives and the
    /// test clock's noon is (<see cref="SunPath.AtHour"/>).
    /// </summary>
    public static Vector3 DefaultSunDirection { get; } = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f));

    /// <summary>The switches the console's <c>view</c> and <c>materials</c> set for the run (<c>RenderView.DebugViews</c>).</summary>
    public const ViewSwitches DebugViewSwitches =
        ViewSwitches.Wireframe
        | ViewSwitches.Levels
        | ViewSwitches.Plain
        | ViewSwitches.MaterialsFlat
        | ViewSwitches.MaterialsAll;

    /// <summary>
    /// The constants for drawing <paramref name="camera"/> onto a <paramref name="width"/> x <paramref name="height"/>
    /// screen, at <paramref name="renderScale"/> of its size on each axis (<see cref="RenderScale"/>): the image drawn
    /// keeps the screen's aspect ratio, and <see cref="Width"/> and <see cref="Height"/> are its scaled size. A lens's
    /// values that cannot be drawn give way: a perspective field of view not between 0 and 180 degrees is 90 degrees, and a
    /// near plane not above 0 is 5 cm; an orthographic height not above 0 is 1 m, a near not finite (such as NaN, for
    /// none) is <paramref name="maxDistance"/> behind the position, and a far not finite or not past the near is
    /// <paramref name="maxDistance"/> past the position (past the near when that lies ahead). Sizes and depths are kept
    /// within <see cref="MostLensMetres"/>, where a float still tells a metre's depths apart.
    /// </summary>
    public static ViewConstants Create(
        in CameraView camera,
        uint width,
        uint height,
        int slot,
        Vector3 sunDirection,
        in Atmosphere atmosphere,
        float maxDistance,
        bool shadows,
        float renderScale = 1,
        ViewSwitches debugViews = ViewSwitches.None
    )
    {
        var aspect = height == 0 ? 1f : (float)width / height;
        var orthographic = camera.Orthographic;
        float halfHeight;
        float near;
        float far = 0;
        if (orthographic)
        {
            var high = camera.HeightMetres > 0 && float.IsFinite(camera.HeightMetres) ? camera.HeightMetres : 1;
            halfHeight = 0.5f * Math.Min(high, MostLensMetres);
            near = Math.Clamp(
                float.IsFinite(camera.NearMetres) ? camera.NearMetres : -maxDistance,
                -MostLensMetres,
                MostLensMetres - 1
            );
            far = Math.Min(
                camera.FarMetres > near && float.IsFinite(camera.FarMetres)
                    ? camera.FarMetres
                    : Math.Max(near, 0) + Math.Max(maxDistance, 1),
                MostLensMetres
            );
        }
        else
        {
            var field = camera.VerticalFovRadians is > 0 and < MathF.PI ? camera.VerticalFovRadians : MathF.PI / 2;
            halfHeight = MathF.Tan(field * 0.5f);
            near =
                camera.NearMetres > 0 && float.IsFinite(camera.NearMetres)
                    ? Math.Min(camera.NearMetres, MostLensMetres)
                    : DefaultNearMetres;
        }

        return new ViewConstants
        {
            Position = camera.Position,
            Forward = camera.Forward,
            Right = camera.Right * (halfHeight * aspect),
            Up = camera.Up * halfHeight,
            SunDirection = sunDirection,
            SkyHorizon = atmosphere.SkyHorizon,
            Slot = (uint)slot,
            MaxDistance = maxDistance,
            // At night (the sun under the horizon) no shadow maps are drawn: the sun lights nothing (LookConstants).
            Switches =
                (shadows && SunPath.CastsShadows(sunDirection) ? ViewSwitches.Shadows : ViewSwitches.None)
                | (orthographic ? ViewSwitches.Orthographic : ViewSwitches.None)
                | (debugViews & DebugViewSwitches),
            Width = RenderScale.Apply(width, renderScale),
            Height = RenderScale.Apply(height, renderScale),
            SunColour = atmosphere.SunColour,
            Air = atmosphere.PackAir(),
            Near = near,
            Far = far,
        };
    }

    /// <summary>The near plane of a perspective lens that gives none, in metres (<see cref="CameraView.Default"/>'s).</summary>
    public const float DefaultNearMetres = 0.05f;

    /// <summary>
    /// The largest size, near plane, or depth a lens is drawn with, in metres either way (100 km, far past any world the
    /// engine holds): a float there still tells a centimetre's depths apart.
    /// </summary>
    public const float MostLensMetres = 100_000;
}

/// <summary>
/// The view's switches, a bit each of <see cref="ViewConstants.Switches"/>; view.glsl names the same bits
/// (<c>ViewShadows</c> and the rest; <c>ViewConstantsTests</c> checks them).
/// </summary>
[Flags]
public enum ViewSwitches : uint
{
    /// <summary>No switch on.</summary>
    None = 0,

    /// <summary>The sun casts shadows through the shadow maps (<c>Engine:Renderer:Shadows</c>).</summary>
    Shadows = 1,

    /// <summary>
    /// The view is orthographic (<see cref="CameraProjection.Orthographic"/>): parallel rays, and depth falling evenly
    /// from <see cref="ViewConstants.Near"/> to <see cref="ViewConstants.Far"/>.
    /// </summary>
    Orthographic = 2,

    /// <summary>
    /// Debug view (the console's <c>view</c>): the meshes' edges drawn over the world, where the device draws triangles
    /// as lines (<c>VulkanContext.SupportsWireframe</c>).
    /// </summary>
    Wireframe = 4,

    /// <summary>Debug view: each surface tinted by the size of the voxels it was meshed from.</summary>
    Levels = 8,

    /// <summary>
    /// Debug view: every block of the world drawn from its plain mesh, the seams between levels left open (the CPU's
    /// choice, <c>WorldMeshes</c>; no shader reads it).
    /// </summary>
    Plain = 16,

    /// <summary>
    /// The console's <c>materials flat</c>: materials drawn in their flat colours, not their texture sets (for timings;
    /// docs/rendering-redesign.md, the materials plan).
    /// </summary>
    MaterialsFlat = 32,

    /// <summary>
    /// The console's <c>all</c> after <c>materials textures</c>: every world material without a texture set drawn from
    /// the first small set (for timings).
    /// </summary>
    MaterialsAll = 64,
}
