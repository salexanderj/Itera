namespace Itera.Engine.Rendering;

/// <summary>
/// The share of cosmetic effects drawn: what gives way first when frames take too long, before the render scale. The
/// player's Effects detail sets <see cref="Ceiling"/>; <see cref="DynamicRenderScale"/> moves <see cref="Share"/> between
/// <see cref="MinimumShare"/> and it, and holds it at the ceiling while dynamic scaling is off. Particle recipes marked
/// <see cref="Particles.ParticleRecipe.Detail"/> make this share of their particles. A game's grass source, or any other
/// effect of the game's, takes this service and reads <see cref="Share"/> to scale what it chooses. Shares are whole
/// hundredths. Run-wide; main thread; allocates nothing.
/// </summary>
public sealed class EffectsBudget
{
    /// <summary>The least share, and the least ceiling.</summary>
    public const float MinimumShare = 0.25f;

    /// <summary>The most share, and the most ceiling: every effect.</summary>
    public const float MostShare = 1;

    /// <summary>How far one step of dynamic scaling lowers the share.</summary>
    public const float LowerStep = 0.25f;

    /// <summary>How far one step of dynamic scaling raises the share.</summary>
    public const float RaiseStep = 0.1f;

    private float _ceiling = MostShare;
    private float _share = MostShare;

    /// <summary>
    /// The most share drawn, <see cref="MinimumShare"/> to <see cref="MostShare"/> (the settings' Effects detail; set
    /// values are kept within that range and rounded to hundredths). A lower ceiling lowers <see cref="Share"/> with it.
    /// </summary>
    public float Ceiling
    {
        get => _ceiling;
        set
        {
            _ceiling = Hundredths(float.IsFinite(value) ? Math.Clamp(value, MinimumShare, MostShare) : MostShare);
            _share = Math.Min(_share, _ceiling);
        }
    }

    /// <summary>
    /// The share in use, <see cref="MinimumShare"/> to <see cref="Ceiling"/> (set values are kept within that range and
    /// rounded to hundredths).
    /// </summary>
    public float Share
    {
        get => _share;
        set => _share = Hundredths(float.IsFinite(value) ? Math.Clamp(value, MinimumShare, _ceiling) : _ceiling);
    }

    /// <summary>
    /// <paramref name="count"/> thinned by <see cref="Share"/>: rounded, and at least 1 when <paramref name="count"/> is
    /// above 0.
    /// </summary>
    public int Thin(int count) =>
        count <= 0 ? count : Math.Max(1, (int)MathF.Round(count * _share, MidpointRounding.AwayFromZero));

    private static float Hundredths(float value) => MathF.Round(value * 100) / 100;
}
