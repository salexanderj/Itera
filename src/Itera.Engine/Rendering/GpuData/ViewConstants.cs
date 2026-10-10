using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// Per-frame camera data in push constants (<c>view.glsl</c>, <c>layout(push_constant, scalar)</c>). A pixel's ray
/// direction is <c>Forward + x * Right + y * Up</c> for x and y in [-1, 1], y up: <see cref="Right"/> and
/// <see cref="Up"/> are scaled by the field of view and the aspect ratio. Built on the main thread for each frame
/// (<see cref="Create"/>) and pushed by the render thread. Field order and types match the GLSL block;
/// <c>GpuLayoutTests</c> checks them. It is 108 bytes of the 128 the spec guarantees for push constants; the switches
/// share one word's bits (<see cref="Switches"/>), and a new switch takes a bit there.
/// </summary>
public struct ViewConstants
{
    /// <summary>Eye position in world metres.</summary>
    public Vector3 Position;

    /// <summary>Unit view direction.</summary>
    public Vector3 Forward;

    /// <summary>Camera right, scaled so x = 1 is the right edge of the image.</summary>
    public Vector3 Right;

    /// <summary>Camera up, scaled so y = 1 is the top edge of the image.</summary>
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

    /// <summary>True when every switch in <paramref name="switches"/> is on.</summary>
    public readonly bool Has(ViewSwitches switches) => (Switches & switches) == switches;

    /// <summary>
    /// The engine's sun, high (58 degrees) and from +x and +z, which an atmosphere without a sun direction gives and the
    /// test clock's noon is (<see cref="SunPath.AtHour"/>).
    /// </summary>
    public static Vector3 DefaultSunDirection { get; } = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f));

    /// <summary>
    /// The near distance of the reverse depth, in metres (<c>NearMetres</c> in view.glsl, which a test checks): a point
    /// stores it over its distance along the view direction, so the sky is 0 and nearer is greater.
    /// </summary>
    public const float NearMetres = 0.01f;

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
    /// keeps the screen's aspect ratio, and <see cref="Width"/> and <see cref="Height"/> are its scaled size.
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
        var tanHalf = MathF.Tan(camera.VerticalFovRadians * 0.5f);
        var aspect = height == 0 ? 1f : (float)width / height;
        return new ViewConstants
        {
            Position = camera.Position,
            Forward = camera.Forward,
            Right = camera.Right * (tanHalf * aspect),
            Up = camera.Up * tanHalf,
            SunDirection = sunDirection,
            SkyHorizon = atmosphere.SkyHorizon,
            Slot = (uint)slot,
            MaxDistance = maxDistance,
            // At night (the sun under the horizon) no shadow maps are drawn: the sun lights nothing (LookConstants).
            Switches =
                (shadows && SunPath.CastsShadows(sunDirection) ? ViewSwitches.Shadows : ViewSwitches.None)
                | (debugViews & DebugViewSwitches),
            Width = RenderScale.Apply(width, renderScale),
            Height = RenderScale.Apply(height, renderScale),
            SunColour = atmosphere.SunColour,
            Air = atmosphere.PackAir(),
        };
    }
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
