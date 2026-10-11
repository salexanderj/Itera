using Itera.Engine.UI.Toolkit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// The UI's typeface: JetBrains Mono Regular (SIL Open Font License 1.1, <c>Fonts/OFL.txt</c>), shipped in
/// <c>Fonts/</c> next to the executable so text looks the same on every platform; and the UI's one sizing rule: a unit
/// is a pixel of a screen <see cref="Ui.ReferenceHeight"/> pixels high, scaled with the screen's height and the UI scale
/// (<see cref="UnitPixels"/>), and the text is <c>Engine:Ui:FontPixels</c> units high, never under
/// <see cref="Ui.LeastTextPixels"/> (<see cref="Pixels"/>). The screen's height is its framebuffer's, so a Retina Mac's
/// doubled pixels are counted. Without the font file the default typeface is used and a Warning logged.
/// </summary>
public sealed partial class UiFonts : IDisposable
{
    /// <summary>The font file, in <c>Fonts/</c> next to the executable.</summary>
    public const string MonoFileName = "JetBrainsMono-Regular.ttf";

    private readonly ILogger<UiFonts> _logger;
    private readonly float _units;
    private SKTypeface? _mono;
    private bool _ownsMono;

    /// <param name="options">The options: <c>Engine:Ui:FontPixels</c>, the text's height in units, and
    /// <c>Engine:Ui:Scale</c>, the UI scale until the player's is set.</param>
    /// <param name="logger">Logger.</param>
    public UiFonts(IOptions<EngineOptions> options, ILogger<UiFonts> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _units = options.Value.Ui.FontPixels;
        UiScale = options.Value.Ui.Scale;
    }

    /// <summary>
    /// The UI scale, 0.75 to 1.5: every size grows with it. The player's (<c>PlayerSettings.UiScale</c>, through
    /// <see cref="Toolkit.UiToolkit.Scale"/>); <c>Engine:Ui:Scale</c> until set.
    /// </summary>
    public float UiScale { get; set; }

    /// <summary>The UI's text height in units (<c>Engine:Ui:FontPixels</c>).</summary>
    public float TextUnits => _units;

    /// <summary>Screen pixels a unit on a screen <paramref name="screenHeight"/> pixels high, at the UI scale.</summary>
    public float UnitPixels(float screenHeight) => Ui.UnitPixelsFor(screenHeight, UiScale);

    /// <summary>
    /// The UI's text height in pixels on a screen <paramref name="screenHeight"/> pixels high, <paramref name="share"/>
    /// times the base size, never under <see cref="Ui.LeastTextPixels"/>.
    /// </summary>
    public float Pixels(float screenHeight, float share = 1) =>
        MathF.Max(Ui.LeastTextPixels, _units * share * UnitPixels(screenHeight));

    /// <summary>The monospaced typeface every layer draws text with.</summary>
    public SKTypeface Mono => _mono ??= Load();

    public void Dispose()
    {
        if (_ownsMono)
        {
            _mono?.Dispose();
        }

        _mono = null;
        _ownsMono = false;
    }

    private SKTypeface Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fonts", MonoFileName);
        var typeface = File.Exists(path) ? SKTypeface.FromFile(path) : null;
        if (typeface is null)
        {
            LogMissing(path);
            return SKTypeface.Default;
        }

        _ownsMono = true;
        LogLoaded(typeface.FamilyName, _units);
        return typeface;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "UI: font {Family}, {Units} units high")]
    private partial void LogLoaded(string family, float units);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UI: no font at {Path}; the default typeface is used")]
    private partial void LogMissing(string path);
}
