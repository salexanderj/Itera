using Itera.Engine.Audio;
using Itera.Engine.UI.Menu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// What every toolkit layer (<see cref="UiLayer"/>) shares, run-wide: the typefaces with their fallbacks
/// (<see cref="Typefaces"/>), the measurer the layout uses, the painter the styles draw with (its text blobs kept across
/// layers), the style new layers start with (<see cref="Style"/>: the engine's plain one, its body text
/// <c>Engine:Ui:FontPixels</c> units, in the game's <see cref="MenuStyle"/> colours), the UI scale
/// (<see cref="Scale"/>, <c>Engine:Ui:Scale</c>), and the clicks played as the pointer comes onto an element and as one
/// is clicked (the engine's <see cref="EngineSounds.MenuHover"/> and <see cref="EngineSounds.MenuPress"/>, at the
/// listener; a game sets others, or none). Main thread.
/// </summary>
public sealed partial class UiToolkit : IDisposable
{
    private readonly AudioService? _audio;
    private readonly UiFonts _fonts;
    private readonly ILogger<UiToolkit> _logger;
    private int _hoverRecipe = -1;
    private int _pressRecipe = -1;
    private string? _hoverSound = EngineSounds.MenuHover;
    private string? _pressSound = EngineSounds.MenuPress;
    private bool _disposed;

    public UiToolkit(
        UiFonts fonts,
        MenuStyle colours,
        IOptions<EngineOptions> options,
        ILogger<UiToolkit> logger,
        AudioService? audio = null
    )
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _audio = audio;
        _fonts = fonts;
        Typefaces = new UiTypefaces(() => fonts.Mono);
        Measurer = new UiTextMeasurer(Typefaces);
        Painter = new UiPainter(Typefaces);
        Style = new UiStyle(options.Value.Ui.FontPixels, colours);
    }

    /// <summary>The UI's typefaces, with fallbacks for characters they lack.</summary>
    public UiTypefaces Typefaces { get; }

    /// <summary>How the layout measures text.</summary>
    public IUiTextMeasurer Measurer { get; }

    /// <summary>What the styles draw with.</summary>
    public UiPainter Painter { get; }

    /// <summary>The style a layer starts with; a layer may set its own (<see cref="Ui.Style"/>).</summary>
    public UiStyle Style { get; set; }

    /// <summary>
    /// The UI scale, 0.75 to 1.5: units grow with it (<see cref="UnitPixels"/>), here and in the layers that size their
    /// text by <see cref="UiFonts"/> (the console, the stats line, the loading list), which keeps it.
    /// </summary>
    public float Scale
    {
        get => _fonts.UiScale;
        set => _fonts.UiScale = value;
    }

    /// <summary>The recipe played as the pointer comes onto an element; null for none.</summary>
    public string? HoverSound
    {
        get => _hoverSound;
        set
        {
            _hoverSound = value;
            _hoverRecipe = -1;
        }
    }

    /// <summary>The recipe played as an element is clicked or pressed by a key; null for none.</summary>
    public string? PressSound
    {
        get => _pressSound;
        set
        {
            _pressSound = value;
            _pressRecipe = -1;
        }
    }

    /// <summary>Screen pixels a unit on a screen <paramref name="screenHeight"/> pixels high (<see cref="Ui.UnitPixelsFor"/>).</summary>
    public float UnitPixels(float screenHeight) => _fonts.UnitPixels(screenHeight);

    /// <summary>A UI for a layer: the toolkit's measurer, and its style.</summary>
    public Ui CreateUi() => new(Measurer, Style);

    /// <summary>Plays the hover click at the listener.</summary>
    public void PlayHover() => Play(_hoverSound, ref _hoverRecipe);

    /// <summary>Plays the press click at the listener.</summary>
    public void PlayPress() => Play(_pressSound, ref _pressRecipe);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Painter.Dispose();
        Typefaces.Dispose();
    }

    /// <summary>A layer's declaration threw: logged once until it declares again without.</summary>
    internal void BuildFailed(string layer, Exception exception) => LogBuildFailed(layer, exception);

    /// <summary>A layer's drawing (its style's, or its areas') threw: logged once until it draws again without.</summary>
    internal void DrawFailed(string layer, Exception exception) => LogDrawFailed(layer, exception);

    private void Play(string? name, ref int recipe)
    {
        if (_audio is null || name is null)
        {
            return;
        }

        if (recipe < 0 && !_audio.Recipes.TryFind(name, out recipe))
        {
            recipe = -1;
            return;
        }

        _audio.Play(new SoundEvent { Recipe = recipe, AtListener = true });
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "UI: the layer {Layer} threw while declaring its UI; it shows nothing until it declares one without (reported once until then)"
    )]
    private partial void LogBuildFailed(string layer, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "UI: the layer {Layer} threw while drawing; what was drawn before stays (reported once until it draws without)"
    )]
    private partial void LogDrawFailed(string layer, Exception exception);
}
