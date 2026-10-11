using System.Text.Json;
using Itera.Engine.Audio;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.UI.Toolkit;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Settings;

/// <summary>
/// The player's settings: the volume, mouse sensitivity, field of view, dynamic render
/// scaling and its threshold (in place of the render scale), shadows, anti-aliasing (SMAA's switch and
/// the world's samples a pixel, one choice in the menu, <see cref="AntiAliasMode"/>), VSync, and full
/// screen, changed in the menu and kept in
/// <c>settings.json</c> in the data folder. At start each is the options' value (<c>Engine:Audio:Volume</c> and so on), then the file's, except where the
/// command line gave the option (<see cref="CommandLineKeys"/>): that one wins for the run and the file keeps what it
/// had. Changes apply at once: the volume, render scaling, shadows, and anti-aliasing are handed to their services, the
/// sensitivity and field of view are read where they are used, a VSync change has the render thread make the swapchain
/// again (<see cref="PresentModeChanged"/>), as a change of the samples does with the world's pipelines and images
/// (<see cref="SamplesChanged"/>), and full screen goes to the window. The file's <c>"Keys"</c> holds the
/// keybindings that differ from their defaults (<see cref="Keys"/>), and its <c>"Game"</c> holds a game's own on/off
/// switches (<see cref="RegisterSwitch"/>). <see cref="Initialize"/> reads the file, in a step
/// before the window opens, and <see cref="Save"/> writes it. Main thread.
/// </summary>
public sealed partial class PlayerSettings
{
    /// <summary>The settings' file in the data folder.</summary>
    public const string FileName = "settings.json";

    /// <summary>The least and most mouse sensitivity the menu offers, degrees per pixel.</summary>
    public const float LeastSensitivity = 0.02f;
    public const float MostSensitivity = 0.5f;

    /// <summary>The least and most field of view the menu offers, degrees across a 16:9 screen.</summary>
    public const float LeastFieldOfView = 60;
    public const float MostFieldOfView = 120;

    private const string VolumeKey = "Volume";
    private const string SensitivityKey = "MouseSensitivity";
    private const string FieldOfViewKey = "FieldOfViewDegrees";
    private const string DynamicScalingKey = "DynamicRenderScaling";
    private const string ScalingThresholdKey = "DynamicScalingThreshold";
    private const string ShadowsKey = "Shadows";
    private const string AntiAliasingKey = "AntiAliasing";
    private const string MsaaSamplesKey = "MsaaSamples";
    private const string KeysKey = "Keys";
    private const string GameKey = "Game";
    private const string VSyncKey = "VSync";
    private const string FullscreenKey = "Fullscreen";
    private const string UiScaleKey = "UiScale";

    /// <summary>
    /// Frame blending's switch, a setting until 2026-10-05; frame blending left with the raymarcher on 2026-10-06.
    /// Known, so the next save drops it.
    /// </summary>
    private const string RetiredFrameBlendKey = "FrameBlend";

    /// <summary>
    /// The render scale, a setting before dynamic render scaling took its place.
    /// Known, so the next save drops it.
    /// </summary>
    private const string RetiredRenderScaleKey = "RenderScale";

    /// <summary>
    /// Smooth normals and texture filtering, settings of the raymarcher, which has left. Known, so the next save drops them.
    /// </summary>
    private const string RetiredSmoothNormalsKey = "SmoothNormals";

    private const string RetiredTextureFilteringKey = "TextureFiltering";

    /// <summary>The keys this build writes or has retired; the file's others are kept as they were.</summary>
    private static readonly HashSet<string> s_known = new(StringComparer.Ordinal)
    {
        VolumeKey,
        SensitivityKey,
        FieldOfViewKey,
        DynamicScalingKey,
        ScalingThresholdKey,
        RetiredRenderScaleKey,
        ShadowsKey,
        RetiredSmoothNormalsKey,
        AntiAliasingKey,
        MsaaSamplesKey,
        RetiredTextureFilteringKey,
        RetiredFrameBlendKey,
        VSyncKey,
        FullscreenKey,
        UiScaleKey,
        KeysKey,
        GameKey,
    };

    private static readonly byte[] s_byteOrderMark = [0xEF, 0xBB, 0xBF];

    private readonly string? _path;
    private readonly AudioService? _audio;
    private readonly RenderView? _view;
    private readonly WindowService? _window;
    private readonly UiToolkit? _toolkit;
    private readonly ILogger<PlayerSettings> _logger;
    private readonly HashSet<string> _fromCommandLine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _file = new(StringComparer.Ordinal);
    private readonly List<string> _switchNames = [];
    private readonly List<bool> _switchValues = [];
    private readonly Dictionary<string, int> _switchIndex = new(StringComparer.Ordinal);
    private bool _changed;
    private int _keysSaved;

    public PlayerSettings(
        IOptions<EngineOptions> options,
        CommandLineKeys commandLine,
        AudioService audio,
        RenderView view,
        WindowService window,
        KeyBindings keys,
        UiToolkit toolkit,
        ILogger<PlayerSettings> logger
    )
        : this(
            options.Value,
            commandLine,
            Path.Combine(options.Value.Application.DataDirectory(), FileName),
            logger,
            audio,
            view,
            window,
            keys,
            toolkit
        ) { }

    /// <summary>
    /// The player's settings at <paramref name="options"/>' values (the engine's defaults when null), read from and
    /// written to no file, applied to nothing but <paramref name="keys"/> and <paramref name="toolkit"/>: for a game's
    /// tests of its menu or settings.
    /// </summary>
    public static PlayerSettings WithoutFile(
        EngineOptions? options = null,
        KeyBindings? keys = null,
        UiToolkit? toolkit = null
    ) =>
        new(
            options ?? new EngineOptions(),
            CommandLineKeys.None,
            null,
            NullLogger<PlayerSettings>.Instance,
            keys: keys,
            toolkit: toolkit
        );

    /// <param name="options">The options, whose values come first.</param>
    /// <param name="commandLine">The keys the command line gave, which win over the file.</param>
    /// <param name="path">The settings' file, or null to read and write none.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="audio">Takes the volume; null for none (tests).</param>
    /// <param name="view">Takes the render scaling, shadows, and anti-aliasing; null for none.</param>
    /// <param name="window">Goes full screen; null for none.</param>
    /// <param name="keys">The keybindings, read from and written to the file's <c>"Keys"</c>; null for the defaults.</param>
    /// <param name="toolkit">Takes the UI scale; null for none.</param>
    internal PlayerSettings(
        EngineOptions options,
        CommandLineKeys commandLine,
        string? path,
        ILogger<PlayerSettings> logger,
        AudioService? audio = null,
        RenderView? view = null,
        WindowService? window = null,
        KeyBindings? keys = null,
        UiToolkit? toolkit = null
    )
    {
        _path = path;
        _toolkit = toolkit;
        UiScale = Math.Clamp(options.Ui.Scale, LeastUiScale, MostUiScale);
        Keys = keys ?? new KeyBindings();
        _audio = audio;
        _view = view;
        _window = window;
        _logger = logger;
        Volume = Math.Clamp(options.Audio.Volume, 0, 1);
        MouseSensitivity = (float)options.Camera.MouseSensitivity;
        FieldOfViewDegrees = (float)options.Camera.FieldOfViewDegrees;
        DynamicScaling = options.Renderer.DynamicRenderScaling;
        ScalingThreshold = Math.Clamp(
            options.Renderer.DynamicScalingThreshold,
            DynamicRenderScale.LeastThreshold,
            DynamicRenderScale.MostThreshold
        );
        Shadows = options.Renderer.Shadows;
        AntiAliasing = options.Renderer.AntiAliasing;
        MsaaSamples = AntiAliasModes.SnapSamples(options.Renderer.MsaaSamples);
        VSync = options.Window.VSync;
        Fullscreen = options.Window.Fullscreen;
        Remember(commandLine, VolumeKey, "Engine:Audio:Volume");
        Remember(commandLine, SensitivityKey, "Engine:Camera:MouseSensitivity");
        Remember(commandLine, FieldOfViewKey, "Engine:Camera:FieldOfViewDegrees");
        Remember(commandLine, DynamicScalingKey, "Engine:Renderer:DynamicRenderScaling");
        Remember(commandLine, ScalingThresholdKey, "Engine:Renderer:DynamicScalingThreshold");
        Remember(commandLine, ShadowsKey, "Engine:Renderer:Shadows");
        Remember(commandLine, AntiAliasingKey, "Engine:Renderer:AntiAliasing");
        Remember(commandLine, MsaaSamplesKey, "Engine:Renderer:MsaaSamples");
        Remember(commandLine, VSyncKey, "Engine:Window:VSync");
        Remember(commandLine, FullscreenKey, "Engine:Window:Fullscreen");
        Remember(commandLine, UiScaleKey, "Engine:Ui:Scale");
    }

    /// <summary>The file's values over the options', for every setting the command line did not give. Once, before the window.</summary>
    public void Initialize()
    {
        Load();
        // The render settings a run measures with, whichever of the options, the command line, and settings.json gave
        // them (so a log shows them).
        var antiAliasing = AntiAliasModes.Label(AntiAliasMode);
        LogApplied(DynamicScaling, ScalingThreshold, Shadows, antiAliasing, VSync, Fullscreen);
    }

    /// <summary>The master volume, 0 to 1.</summary>
    public float Volume { get; private set; }

    /// <summary>The least UI scale.</summary>
    public const float LeastUiScale = 0.75f;

    /// <summary>The most UI scale.</summary>
    public const float MostUiScale = 1.5f;

    /// <summary>The UI's size, <see cref="LeastUiScale"/> to <see cref="MostUiScale"/>: the toolkit's units grow with it.</summary>
    public float UiScale { get; private set; }

    /// <summary>Mouse look: degrees the camera turns per pixel of mouse movement.</summary>
    public float MouseSensitivity { get; private set; }

    /// <summary>Mouse look in radians per pixel.</summary>
    public float RadiansPerPixel => MouseSensitivity * MathF.PI / 180;

    /// <summary>The field of view in degrees across a 16:9 screen.</summary>
    public float FieldOfViewDegrees { get; private set; }

    /// <summary>The vertical field of view in radians that <see cref="FieldOfViewDegrees"/> gives.</summary>
    public float VerticalFieldOfViewRadians =>
        (float)(2 * Math.Atan(Math.Tan(FieldOfViewDegrees * Math.PI / 360) * 9 / 16));

    /// <summary>
    /// True when the render scale follows the GPU's time to hold <see cref="ScalingThreshold"/>
    /// (<see cref="DynamicRenderScale"/>); false draws the world at <c>Engine:Renderer:RenderScale</c>, the screen's size
    /// unless the command line gives another.
    /// </summary>
    public bool DynamicScaling { get; private set; }

    /// <summary>
    /// The frame rate dynamic render scaling holds, or the screen's refresh rate when that is lower, frames a second
    /// (<see cref="DynamicRenderScale.LeastThreshold"/> to <see cref="DynamicRenderScale.MostThreshold"/>).
    /// </summary>
    public float ScalingThreshold { get; private set; }

    /// <summary>True when the sun casts shadows.</summary>
    public bool Shadows { get; private set; }

    /// <summary>
    /// True when the picture is anti-aliased with SMAA 1x before FSR 1 while the world is drawn with one sample a pixel
    /// (<c>Engine:Renderer:AntiAliasing</c>).
    /// </summary>
    public bool AntiAliasing { get; private set; }

    /// <summary>
    /// The world's samples a pixel, 1, 2, 4, or 8 (<c>Engine:Renderer:MsaaSamples</c>), lowered to what the device offers;
    /// above 1, SMAA does not run.
    /// </summary>
    public int MsaaSamples { get; private set; }

    /// <summary>The settings' Anti-aliasing, of <see cref="MsaaSamples"/> and <see cref="AntiAliasing"/>.</summary>
    public AntiAliasMode AntiAliasMode =>
        MsaaSamples switch
        {
            2 => AntiAliasMode.Msaa2,
            4 => AntiAliasMode.Msaa4,
            8 => AntiAliasMode.Msaa8,
            _ => AntiAliasing ? AntiAliasMode.Smaa : AntiAliasMode.Off,
        };

    /// <summary>True for FIFO presentation, false for mailbox.</summary>
    public bool VSync { get; private set; }

    /// <summary>True when the window covers its screen.</summary>
    public bool Fullscreen { get; private set; }

    /// <summary>The keybindings, kept in the file's <c>"Keys"</c> (<see cref="KeyBindings"/>).</summary>
    public KeyBindings Keys { get; }

    /// <summary>
    /// Makes one of a game's own on/off settings, kept as <paramref name="name"/> in the file's <c>"Game"</c> object. Its
    /// value is the file's when that is a JSON boolean, else <paramref name="defaultValue"/>. A game calls it once for
    /// each switch at its run's <c>game</c> step (<c>IRunStartup.Start</c>), which runs after <see cref="Initialize"/>
    /// has read the file.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or white space, or was registered before.</exception>
    public GameSwitch RegisterSwitch(string name, bool defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_switchIndex.ContainsKey(name))
        {
            throw new ArgumentException($"The game switch '{name}' is registered twice.", nameof(name));
        }

        var on = defaultValue;
        if (
            _file.TryGetValue(GameKey, out var section)
            && section.ValueKind == JsonValueKind.Object
            && section.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False
        )
        {
            on = element.GetBoolean();
        }

        var which = new GameSwitch(_switchNames.Count);
        _switchIndex.Add(name, which.Index);
        _switchNames.Add(name);
        _switchValues.Add(on);
        return which;
    }

    /// <summary>True when the game's switch is on. Allocates nothing.</summary>
    public bool IsOn(GameSwitch which) => _switchValues[which.Index];

    /// <summary>The name a game's switch was registered with, as it is kept in the file.</summary>
    public string SwitchName(GameSwitch which) => _switchNames[which.Index];

    /// <summary>
    /// Turns a game's switch on or off. A change is written by the next <see cref="Save"/>; the same value changes
    /// nothing. Allocates nothing.
    /// </summary>
    public void SetSwitch(GameSwitch which, bool on)
    {
        if (_switchValues[which.Index] == on)
        {
            return;
        }

        _switchValues[which.Index] = on;
        _changed = true;
        Version++;
    }

    /// <summary>Rises whenever a setting changes.</summary>
    public int Version { get; private set; }

    /// <summary>VSync changed: the render thread makes the swapchain again with the new present mode.</summary>
    public event Action? PresentModeChanged;

    /// <summary>
    /// <see cref="MsaaSamples"/> changed: the render thread makes the world's pipelines and multisampled images again
    /// (a change takes effect at once).
    /// </summary>
    public event Action? SamplesChanged;

    /// <summary>
    /// Hands the volume, render scaling, shadows, and anti-aliasing to their services. Once
    /// the audio device and the window exist.
    /// </summary>
    public void Apply()
    {
        if (_audio is not null)
        {
            _audio.Volume = Volume;
        }

        if (_toolkit is not null)
        {
            _toolkit.Scale = UiScale;
        }

        if (_view is not null)
        {
            _view.DynamicScaling = DynamicScaling;
            _view.ScalingThreshold = ScalingThreshold;
            _view.Shadows = Shadows;
            _view.AntiAliasing = AntiAliasing;
        }
    }

    /// <summary>Sets the UI's size, kept within <see cref="LeastUiScale"/> and <see cref="MostUiScale"/>; at once.</summary>
    public void SetUiScale(float scale)
    {
        if (Change(UiScaleKey, UiScale, Math.Clamp(scale, LeastUiScale, MostUiScale), out var value))
        {
            UiScale = value;
            Apply();
        }
    }

    public void SetVolume(float volume)
    {
        if (Change(VolumeKey, Volume, Math.Clamp(volume, 0, 1), out var value))
        {
            Volume = value;
            Apply();
        }
    }

    public void SetMouseSensitivity(float degreesPerPixel)
    {
        var clamped = Math.Clamp(degreesPerPixel, LeastSensitivity, MostSensitivity);
        if (Change(SensitivityKey, MouseSensitivity, clamped, out var value))
        {
            MouseSensitivity = value;
        }
    }

    public void SetFieldOfView(float degrees)
    {
        var clamped = Math.Clamp(degrees, LeastFieldOfView, MostFieldOfView);
        if (Change(FieldOfViewKey, FieldOfViewDegrees, clamped, out var value))
        {
            FieldOfViewDegrees = value;
        }
    }

    public void SetDynamicScaling(bool dynamic)
    {
        if (dynamic == DynamicScaling)
        {
            return;
        }

        DynamicScaling = dynamic;
        Changed(DynamicScalingKey);
        Apply();
    }

    public void SetScalingThreshold(float fps)
    {
        var clamped = Math.Clamp(fps, DynamicRenderScale.LeastThreshold, DynamicRenderScale.MostThreshold);
        if (Change(ScalingThresholdKey, ScalingThreshold, clamped, out var value))
        {
            ScalingThreshold = value;
            Apply();
        }
    }

    public void SetShadows(bool shadows)
    {
        if (shadows == Shadows)
        {
            return;
        }

        Shadows = shadows;
        Changed(ShadowsKey);
        Apply();
    }

    public void SetAntiAliasing(bool antiAliasing)
    {
        if (antiAliasing == AntiAliasing)
        {
            return;
        }

        AntiAliasing = antiAliasing;
        Changed(AntiAliasingKey);
        Apply();
    }

    /// <summary>
    /// Sets the settings' Anti-aliasing: Off and SMAA draw the world with one sample a pixel and switch SMAA; the MSAA
    /// modes set the samples and leave SMAA's switch as it was (it does not run with them).
    /// </summary>
    public void SetAntiAliasMode(AntiAliasMode mode)
    {
        if (mode is AntiAliasMode.Off or AntiAliasMode.Smaa)
        {
            SetAntiAliasing(mode == AntiAliasMode.Smaa);
        }

        var samples = AntiAliasModes.Samples(mode);
        if (samples == MsaaSamples)
        {
            return;
        }

        MsaaSamples = samples;
        Changed(MsaaSamplesKey);
        SamplesChanged?.Invoke();
    }

    public void SetVSync(bool vsync)
    {
        if (vsync == VSync)
        {
            return;
        }

        VSync = vsync;
        Changed(VSyncKey);
        PresentModeChanged?.Invoke();
    }

    public void SetFullscreen(bool fullscreen)
    {
        if (fullscreen == Fullscreen)
        {
            return;
        }

        Fullscreen = fullscreen;
        Changed(FullscreenKey);
        _window?.SetFullscreen(fullscreen);
    }

    /// <summary>
    /// Writes <c>settings.json</c> when a setting or a keybinding changed since: every setting, but those the command line
    /// gave keep what the file had, then the keybindings that differ from their defaults, then the game's switches
    /// (<c>"Game"</c>: every registered one, then what the file's object held that this run did not register), then what
    /// the file held that this build does not know. It writes a file beside it and moves that
    /// over the old one, so a failed write leaves the old file whole; a failure is a Warning,
    /// the game goes on, and the next save tries again.
    /// </summary>
    public void Save()
    {
        if ((!_changed && Keys.Version == _keysSaved) || _path is null)
        {
            return;
        }

        var keysVersion = Keys.Version;
        var written = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = File.Create(written))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                WriteNumber(writer, VolumeKey, Volume);
                WriteNumber(writer, SensitivityKey, MouseSensitivity);
                WriteNumber(writer, FieldOfViewKey, FieldOfViewDegrees);
                WriteBoolean(writer, DynamicScalingKey, DynamicScaling);
                WriteNumber(writer, ScalingThresholdKey, ScalingThreshold);
                WriteBoolean(writer, ShadowsKey, Shadows);
                WriteBoolean(writer, AntiAliasingKey, AntiAliasing);
                WriteNumber(writer, MsaaSamplesKey, MsaaSamples);
                WriteBoolean(writer, VSyncKey, VSync);
                WriteBoolean(writer, FullscreenKey, Fullscreen);
                WriteNumber(writer, UiScaleKey, UiScale);
                Keys.Write(writer);
                WriteGame(writer);
                foreach (var (key, value) in _file)
                {
                    if (!s_known.Contains(key))
                    {
                        writer.WritePropertyName(key);
                        value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            File.Move(written, _path, overwrite: true);
            _changed = false;
            _keysSaved = keysVersion;
            LogSaved(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_path, ex.Message);
        }
    }

    private void Remember(CommandLineKeys commandLine, string key, string option)
    {
        if (commandLine.Contains(option))
        {
            _fromCommandLine.Add(key);
        }
    }

    /// <summary>The file's values, for every setting the command line did not give; anything else in it is left alone.</summary>
    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            // A byte order mark, which some editors write, is not JSON.
            ReadOnlyMemory<byte> bytes = File.ReadAllBytes(_path);
            if (bytes.Span.StartsWith(s_byteOrderMark))
            {
                bytes = bytes[s_byteOrderMark.Length..];
            }

            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                LogUnreadable(_path, "not a JSON object");
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                _file[property.Name] = property.Value.Clone();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogUnreadable(_path, ex.Message);
            return;
        }

        Volume = Read(VolumeKey, Volume, 0, 1);
        MouseSensitivity = Read(SensitivityKey, MouseSensitivity, LeastSensitivity, MostSensitivity);
        FieldOfViewDegrees = Read(FieldOfViewKey, FieldOfViewDegrees, LeastFieldOfView, MostFieldOfView);
        DynamicScaling = Read(DynamicScalingKey, DynamicScaling);
        ScalingThreshold = Read(
            ScalingThresholdKey,
            ScalingThreshold,
            DynamicRenderScale.LeastThreshold,
            DynamicRenderScale.MostThreshold
        );
        Shadows = Read(ShadowsKey, Shadows);
        AntiAliasing = Read(AntiAliasingKey, AntiAliasing);
        MsaaSamples = AntiAliasModes.SnapSamples((int)Read(MsaaSamplesKey, MsaaSamples, 1, 8));
        VSync = Read(VSyncKey, VSync);
        Fullscreen = Read(FullscreenKey, Fullscreen);
        UiScale = Read(UiScaleKey, UiScale, LeastUiScale, MostUiScale);
        if (_file.TryGetValue(KeysKey, out var keys))
        {
            Keys.Load(keys);
        }

        LogLoaded(_path);
    }

    private float Read(string key, float value, float least, float most) =>
        !_fromCommandLine.Contains(key)
        && _file.TryGetValue(key, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetSingle(out var read)
        && float.IsFinite(read)
            ? Math.Clamp(read, least, most)
            : value;

    private bool Read(string key, bool value) =>
        !_fromCommandLine.Contains(key)
        && _file.TryGetValue(key, out var element)
        && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : value;

    private bool Change(string key, float before, float after, out float value)
    {
        value = after;
        if (Math.Abs(after - before) < 1e-6f)
        {
            return false;
        }

        Changed(key);
        return true;
    }

    private void Changed(string key)
    {
        // A setting the command line gave is the run's: changing it in the menu makes it the player's again.
        _fromCommandLine.Remove(key);
        _changed = true;
        Version++;
    }

    private void WriteGame(Utf8JsonWriter writer)
    {
        _file.TryGetValue(GameKey, out var section);
        if (_switchNames.Count == 0)
        {
            // No switch registered: the file's value stays as it was, whatever it is.
            if (section.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName(GameKey);
                section.WriteTo(writer);
            }

            return;
        }

        writer.WriteStartObject(GameKey);
        for (var i = 0; i < _switchNames.Count; i++)
        {
            writer.WriteBoolean(_switchNames[i], _switchValues[i]);
        }

        if (section.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in section.EnumerateObject())
            {
                if (!_switchIndex.ContainsKey(property.Name))
                {
                    property.WriteTo(writer);
                }
            }
        }

        writer.WriteEndObject();
    }

    private void WriteNumber(Utf8JsonWriter writer, string key, float value)
    {
        if (!_fromCommandLine.Contains(key))
        {
            writer.WriteNumber(key, MathF.Round(value, 4));
        }
        else if (_file.TryGetValue(key, out var kept))
        {
            writer.WritePropertyName(key);
            kept.WriteTo(writer);
        }
    }

    private void WriteBoolean(Utf8JsonWriter writer, string key, bool value)
    {
        if (!_fromCommandLine.Contains(key))
        {
            writer.WriteBoolean(key, value);
        }
        else if (_file.TryGetValue(key, out var kept))
        {
            writer.WritePropertyName(key);
            kept.WriteTo(writer);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings: read {Path}")]
    private partial void LogLoaded(string path);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Settings: dynamic render scaling {DynamicScaling} at {Threshold} fps, shadows {Shadows}, anti-aliasing {AntiAliasing}, VSync {VSync}, full screen {Fullscreen}"
    )]
    private partial void LogApplied(
        bool dynamicScaling,
        float threshold,
        bool shadows,
        string antiAliasing,
        bool vSync,
        bool fullscreen
    );

    [LoggerMessage(Level = LogLevel.Debug, Message = "Settings: wrote {Path}")]
    private partial void LogSaved(string path);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Settings: {Path} could not be read ({Reason}); the options' values hold"
    )]
    private partial void LogUnreadable(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings: {Path} could not be written ({Reason})")]
    private partial void LogSaveFailed(string path, string reason);
}
