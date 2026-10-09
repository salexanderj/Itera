using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// The UI's typeface: JetBrains Mono Regular (SIL Open Font License 1.1, <c>Fonts/OFL.txt</c>), shipped in
/// <c>Fonts/</c> next to the executable so text looks the same on every platform, and the text size from
/// <c>Engine:Ui:FontPixels</c>, times the screen's framebuffer pixels per window unit (<see cref="Scale"/>). Without the
/// file the default typeface is used and a Warning logged.
/// </summary>
public sealed partial class UiFonts : IDisposable
{
    /// <summary>The font file, in <c>Fonts/</c> next to the executable.</summary>
    public const string MonoFileName = "JetBrainsMono-Regular.ttf";

    private readonly ILogger<UiFonts> _logger;
    private readonly float _pixels;
    private readonly Func<float?> _contentScale;
    private float _scale;
    private SKTypeface? _mono;
    private bool _ownsMono;

    public UiFonts(IOptions<EngineOptions> options, ILogger<UiFonts> logger, WindowService? window = null)
        : this(options, logger, () => window?.ContentScale) { }

    /// <param name="options">The options, whose <c>Engine:Ui:FontPixels</c> is the text's height.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="contentScale">The screen's framebuffer pixels per window unit; null until the window exists.</param>
    internal UiFonts(IOptions<EngineOptions> options, ILogger<UiFonts> logger, Func<float?> contentScale)
    {
        _logger = logger;
        _pixels = options.Value.Ui.FontPixels;
        _contentScale = contentScale;
    }

    /// <summary>Height of the UI's text in pixels: <c>Engine:Ui:FontPixels</c> times <see cref="Scale"/>.</summary>
    public float Pixels => _pixels * Scale;

    /// <summary>
    /// The screen's framebuffer pixels per window unit, so the console, the stats line, and the loading list keep their
    /// size on a Retina Mac, where they were drawn at half. Read once the window
    /// exists, and kept; 1 until then. The layers make their fonts at their first draw, after the window.
    /// </summary>
    public float Scale
    {
        get
        {
            if (_scale > 0)
            {
                return _scale;
            }

            if (_contentScale() is not { } scale)
            {
                return 1;
            }

            _scale = scale;
            return _scale;
        }
    }

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
        LogLoaded(typeface.FamilyName, Pixels);
        return typeface;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "UI: font {Family}, {Pixels} px")]
    private partial void LogLoaded(string family, float pixels);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UI: no font at {Path}; the default typeface is used")]
    private partial void LogMissing(string path);
}
