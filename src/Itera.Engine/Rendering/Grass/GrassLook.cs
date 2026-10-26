namespace Itera.Engine.Rendering.Grass;

/// <summary>
/// The grass a game grows (<see cref="IGrassSource"/>): the material blades stand on, how far the field reaches and
/// thins, its blades' density, size, lean, and colour round the ground's, the wind, and the hue a texel must keep to hold
/// blades. Lengths in metres, angles in degrees. The defaults are a short, sparse field: a game sets what it wants with
/// <c>with</c> or an initialiser.
/// </summary>
public readonly record struct GrassLook
{
    public GrassLook() { }

    /// <summary>The material blades grow on, by the share of it among each vertex's surface corners.</summary>
    public byte Material { get; init; }

    /// <summary>How far from the eye the field reaches.</summary>
    public float Radius { get; init; } = 30;

    /// <summary>How far from the eye the field keeps its full density; past it, it thins to none at <see cref="Radius"/>.</summary>
    public float FullMetres { get; init; } = 12;

    /// <summary>How far from the eye blades have three segments; past it, one triangle.</summary>
    public float NearMetres { get; init; } = 12;

    /// <summary>Blades a square metre at full density.</summary>
    public float Density { get; init; } = 40;

    /// <summary>A blade's mean height.</summary>
    public float Height { get; init; } = 0.2f;

    /// <summary>How far blades' heights spread round the mean, as a share either way.</summary>
    public float HeightSpread { get; init; } = 0.5f;

    /// <summary>A blade's mean width at its root.</summary>
    public float Width { get; init; } = 0.012f;

    /// <summary>The least width of a blade's root on the screen, in pixels.</summary>
    public float LeastPixels { get; init; } = 1;

    /// <summary>The steepest ground grass grows on, from level.</summary>
    public float SteepestDegrees { get; init; } = 40;

    /// <summary>The blades' mean lean from upright.</summary>
    public float LeanDegrees { get; init; } = 20;

    /// <summary>How far the wind pushes the blades' tips, as a share of their height.</summary>
    public float Wind { get; init; } = 0.35f;

    /// <summary>How fast the wind's gusts drift over the ground, in metres a second.</summary>
    public float WindSpeed { get; init; } = 3;

    /// <summary>The way the wind blows, from north (-z, 0) towards west (-x, 90).</summary>
    public float WindHeadingDegrees { get; init; }

    /// <summary>How light a blade's tip is, as a share of the ground's colour under it.</summary>
    public float TipLight { get; init; } = 1.2f;

    /// <summary>How light a blade's root is, as a share of the ground's colour under it.</summary>
    public float RootShade { get; init; } = 0.6f;

    /// <summary>How far blades' colours spread round the ground's, as a share either way.</summary>
    public float ColourSpread { get; init; } = 0.12f;

    /// <summary>
    /// How far the ground's texture may stray from its set's mean hue and still hold blades: the distance between the
    /// unit directions of the texel's colour over the mean and of grey. None grow past twice it, so a patch of another
    /// hue (bare ash in a grass) stays bare. 0 lets every texel hold them.
    /// </summary>
    public float HueTolerance { get; init; }
}

/// <summary>
/// Where the grass's passes ask what to grow (<see cref="GrassPlacePass"/>): a game that wants grass implements it and
/// registers it with <see cref="GrassServiceCollectionExtensions.AddIteraGrass{TSource}"/>. Run-wide.
/// </summary>
public interface IGrassSource
{
    /// <summary>
    /// The grass to grow this frame, or false for none (the player turned it off). Main thread, once a frame, from the
    /// place pass's <see cref="IRenderPass.Write"/>; allocates nothing.
    /// </summary>
    bool TryGetLook(out GrassLook look);
}
