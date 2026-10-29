using System.Text.Json;
using Itera.Engine.Audio;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.UI.Toolkit;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Settings;

/// <summary>
/// The player's settings: the volume, mouse sensitivity, field of view, dynamic render
/// scaling and its threshold, the render scale while it is off, shadows, anti-aliasing (SMAA's switch and
/// the world's samples a pixel, one choice in the menu, <see cref="AntiAliasMode"/>), object detail, effects detail, the
/// graphics preset those follow (<see cref="GraphicsPreset"/>), VSync, full
/// screen, and the screen it covers, changed in the menu and kept in
/// <c>settings.json</c> in the data folder. At start each is the options' value (<c>Engine:Audio:Volume</c> and so on), then the file's, except where the
/// command line gave the option (<see cref="CommandLineKeys"/>): that one wins for the run and the file keeps what it
/// had. Changes apply at once: the volume, render scaling, shadows, and anti-aliasing are handed to their services, the
/// sensitivity and field of view are read where they are used, a VSync change has the render thread make the swapchain
/// again (<see cref="PresentModeChanged"/>), as a change of the samples does with the world's pipelines and images
/// (<see cref="SamplesChanged"/>), and full screen and its screen go to the window. The file's <c>"Keys"</c> holds the
/// keybindings that differ from their defaults (<see cref="Keys"/>), and its <c>"Game"</c> holds a game's own on/off
/// switches and numbers (<see cref="RegisterSwitch"/>, <see cref="RegisterValue"/>). <see cref="Initialize"/> reads the
/// file, in a step before the window opens, and <see cref="Save"/> writes it. Main thread.
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
    private const string MusicVolumeKey = "MusicVolume";
    private const string EffectsVolumeKey = "EffectsVolume";
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
    private const string ScreenKey = "Screen";
    private const string UiScaleKey = "UiScale";
    private const string GraphicsPresetKey = "GraphicsPreset";
    private const string ObjectDetailKey = "ObjectDetail";
    private const string EffectsDetailKey = "EffectsDetail";

    /// <summary>
    /// The render scale while dynamic scaling is off. Not <c>"RenderScale"</c>, the retired key, so an old file's value
    /// is not taken.
    /// </summary>
    private const string RenderScaleKey = "FixedRenderScale";

    /// <summary>
    /// The file's version, which <see cref="Save"/> writes as <c>"Version"</c>. A file with none is version 1. Version 2
    /// came with <see cref="UnversionedDefaultThreshold"/>'s upgrade.
    /// </summary>
    private const int FileVersion = 2;

    private const string FileVersionKey = "Version";

    /// <summary>
    /// The dynamic scaling threshold's default before the presets came: a file written before them (no
    /// <c>"GraphicsPreset"</c>) that holds it takes the options' threshold.
    /// </summary>
    private const float OldDefaultThreshold = 120;

    /// <summary>
    /// The engine's dynamic scaling threshold default from the presets on: a file of version 1 that holds it takes the
    /// options' threshold, since the file did not tell the default from a choice.
    /// </summary>
    private const float UnversionedDefaultThreshold = 60;

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
        FileVersionKey,
        VolumeKey,
        MusicVolumeKey,
        EffectsVolumeKey,
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
        ScreenKey,
        UiScaleKey,
        GraphicsPresetKey,
        ObjectDetailKey,
        EffectsDetailKey,
        RenderScaleKey,
        KeysKey,
        GameKey,
    };

    /// <summary>
    /// The engine's part of each preset's table, by <see cref="GraphicsPreset"/> (Low, Medium, High). A preset's object
    /// detail is its table's or the options' (<c>Engine:Renderer:ObjectVoxelPixels</c>, the game's), whichever is coarser
    /// (<see cref="PresetObjectDetail"/>).
    /// </summary>
    private static readonly PresetTable[] s_presets =
    [
        new(AntiAliasMode.Smaa, true, 2, 0.5f),
        new(AntiAliasMode.Msaa2, true, 2, 0.75f),
        new(AntiAliasMode.Msaa4, true, 1, 1),
    ];

    private static readonly byte[] s_byteOrderMark = [0xEF, 0xBB, 0xBF];

    private readonly string? _path;
    private readonly AudioService? _audio;
    private readonly RenderView? _view;
    private readonly WindowService? _window;
    private readonly UiToolkit? _toolkit;
    private readonly WorldMeshes? _meshes;
    private readonly EffectsBudget? _effects;
    private readonly float _optionsThreshold;

    // The options' object detail, the coarsest a preset's object detail may be finer than.
    private readonly float _optionsObjectDetail;
    private readonly List<PresetValue> _presetValues = [];
    private readonly ILogger<PlayerSettings> _logger;
    private readonly HashSet<string> _fromCommandLine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _file = new(StringComparer.Ordinal);
    private readonly List<string> _switchNames = [];
    private readonly List<bool> _switchValues = [];
    private readonly Dictionary<string, int> _switchIndex = new(StringComparer.Ordinal);
    private readonly List<string> _valueNames = [];
    private readonly List<float> _valueNumbers = [];
    private readonly List<float> _valueLeast = [];
    private readonly List<float> _valueMost = [];
    private readonly Dictionary<string, int> _valueIndex = new(StringComparer.Ordinal);
    private bool _changed;
    private int _keysSaved;

    // The preset the file names (Custom for none): its values as its table is now replace the file's.
    private GraphicsPreset _filePreset = GraphicsPreset.Custom;

    public PlayerSettings(
        IOptions<EngineOptions> options,
        CommandLineKeys commandLine,
        AudioService audio,
        RenderView view,
        WindowService window,
        KeyBindings keys,
        UiToolkit toolkit,
        WorldMeshes meshes,
        EffectsBudget effects,
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
            toolkit,
            meshes,
            effects
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
    /// <param name="meshes">Takes the object detail; null for none.</param>
    /// <param name="effects">Takes the effects detail; null for none.</param>
    internal PlayerSettings(
        EngineOptions options,
        CommandLineKeys commandLine,
        string? path,
        ILogger<PlayerSettings> logger,
        AudioService? audio = null,
        RenderView? view = null,
        WindowService? window = null,
        KeyBindings? keys = null,
        UiToolkit? toolkit = null,
        WorldMeshes? meshes = null,
        EffectsBudget? effects = null
    )
    {
        _path = path;
        _toolkit = toolkit;
        _meshes = meshes;
        _effects = effects;
        UiScale = Math.Clamp(options.Ui.Scale, LeastUiScale, MostUiScale);
        Keys = keys ?? new KeyBindings();
        _audio = audio;
        _view = view;
        _window = window;
        _logger = logger;
        Volume = Math.Clamp(options.Audio.Volume, 0, 1);
        MusicVolume = Math.Clamp(options.Audio.MusicVolume, 0, 1);
        EffectsVolume = Math.Clamp(options.Audio.EffectsVolume, 0, 1);
        MouseSensitivity = (float)options.Camera.MouseSensitivity;
        FieldOfViewDegrees = (float)options.Camera.FieldOfViewDegrees;
        DynamicScaling = options.Renderer.DynamicRenderScaling;
        ScalingThreshold = Math.Clamp(
            options.Renderer.DynamicScalingThreshold,
            DynamicRenderScale.LeastThreshold,
            DynamicRenderScale.MostThreshold
        );
        _optionsThreshold = ScalingThreshold;
        Shadows = options.Renderer.Shadows;
        AntiAliasing = options.Renderer.AntiAliasing;
        MsaaSamples = AntiAliasModes.SnapSamples(options.Renderer.MsaaSamples);
        ObjectDetail = Math.Clamp(options.Renderer.ObjectVoxelPixels, LeastObjectDetail, MostObjectDetail);
        _optionsObjectDetail = ObjectDetail;
        EffectsDetail = EffectsBudget.MostShare;
        RenderScale = Math.Clamp(options.Renderer.RenderScale, Rendering.RenderScale.Min, Rendering.RenderScale.Max);
        VSync = options.Window.VSync;
        Fullscreen = options.Window.Fullscreen;
        Screen = options.Window.Screen ?? "";
        Remember(commandLine, VolumeKey, "Engine:Audio:Volume");
        Remember(commandLine, MusicVolumeKey, "Engine:Audio:MusicVolume");
        Remember(commandLine, EffectsVolumeKey, "Engine:Audio:EffectsVolume");
        Remember(commandLine, SensitivityKey, "Engine:Camera:MouseSensitivity");
        Remember(commandLine, FieldOfViewKey, "Engine:Camera:FieldOfViewDegrees");
        Remember(commandLine, DynamicScalingKey, "Engine:Renderer:DynamicRenderScaling");
        Remember(commandLine, ScalingThresholdKey, "Engine:Renderer:DynamicScalingThreshold");
        Remember(commandLine, ShadowsKey, "Engine:Renderer:Shadows");
        Remember(commandLine, AntiAliasingKey, "Engine:Renderer:AntiAliasing");
        Remember(commandLine, MsaaSamplesKey, "Engine:Renderer:MsaaSamples");
        Remember(commandLine, ObjectDetailKey, "Engine:Renderer:ObjectVoxelPixels");
        Remember(commandLine, RenderScaleKey, "Engine:Renderer:RenderScale");
        Remember(commandLine, VSyncKey, "Engine:Window:VSync");
        Remember(commandLine, FullscreenKey, "Engine:Window:Fullscreen");
        Remember(commandLine, ScreenKey, "Engine:Window:Screen");
        Remember(commandLine, UiScaleKey, "Engine:Ui:Scale");
    }

    /// <summary>The file's values over the options', for every setting the command line did not give. Once, before the window.</summary>
    public void Initialize()
    {
        Load();
        // The render settings a run measures with, whichever of the options, the command line, and settings.json gave
        // them (so a log shows them).
        var antiAliasing = AntiAliasModes.Label(AntiAliasMode);
        LogApplied(DynamicScaling, ScalingThreshold, Shadows, antiAliasing, VSync, Fullscreen, ScreenLabel);
        var preset = GraphicsPresets.Label(GraphicsPreset);
        LogGraphics(preset, ObjectDetail, EffectsDetail, RenderScale);
    }

    /// <summary>The master volume, 0 to 1.</summary>
    public float Volume { get; private set; }

    /// <summary>Music's volume under the master, 0 to 1.</summary>
    public float MusicVolume { get; private set; }

    /// <summary>The effects' volume under the master (recipes, file sounds, the menu's clicks), 0 to 1.</summary>
    public float EffectsVolume { get; private set; }

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

    /// <summary>
    /// The least object detail kept: a voxel object's voxel covers this many pixels before its level is drawn
    /// (<c>Engine:Renderer:ObjectVoxelPixels</c>'s range, as <see cref="WorldMeshes.ObjectVoxelPixels"/> takes it).
    /// </summary>
    public const float LeastObjectDetail = 0.5f;

    /// <summary>The most object detail number kept: coarser levels nearer.</summary>
    public const float MostObjectDetail = 16;

    /// <summary>The finest object detail the Settings page's slider offers (High).</summary>
    public const float SliderFinestObjectDetail = 1;

    /// <summary>The coarsest object detail the Settings page's slider offers (Low).</summary>
    public const float SliderCoarsestObjectDetail = 4;

    /// <summary>
    /// The settings' Object detail: how wide one voxel of a voxel object's model level must be on the drawn image, in
    /// pixels, for that level to be drawn (<c>Engine:Renderer:ObjectVoxelPixels</c>, kept within
    /// <see cref="LeastObjectDetail"/> to <see cref="MostObjectDetail"/>; <see cref="WorldMeshes.ObjectVoxelPixels"/>).
    /// 1 is the finest; larger numbers draw coarser levels nearer.
    /// </summary>
    public float ObjectDetail { get; private set; }

    /// <summary>
    /// The settings' Effects detail: the most share of cosmetic effects drawn, <see cref="EffectsBudget.MinimumShare"/>
    /// to 1 (<see cref="EffectsBudget.Ceiling"/>).
    /// </summary>
    public float EffectsDetail { get; private set; }

    /// <summary>
    /// The settings' Render scale: the share of the screen's size the world is drawn at while dynamic render scaling is
    /// off, <see cref="Rendering.RenderScale.Min"/> to 1 (<c>Engine:Renderer:RenderScale</c>,
    /// <see cref="RenderView.FixedRenderScale"/>). The console's <c>scale</c> sets one for the run over it.
    /// </summary>
    public float RenderScale { get; private set; }

    /// <summary>
    /// The settings' Quality: the preset whose table every value matches (the engine's anti-aliasing, shadows, object
    /// detail, and effects detail, and the game's values added with <see cref="AddPresetValue"/>), else
    /// <see cref="GraphicsPreset.Custom"/>. Allocates nothing.
    /// </summary>
    public GraphicsPreset GraphicsPreset
    {
        get
        {
            for (var preset = GraphicsPreset.Low; preset <= GraphicsPreset.High; preset++)
            {
                if (Matches(preset))
                {
                    return preset;
                }
            }

            return GraphicsPreset.Custom;
        }
    }

    /// <summary>True for FIFO presentation, false for mailbox (or immediate where there is none).</summary>
    public bool VSync { get; private set; }

    /// <summary>True when the window covers its screen.</summary>
    public bool Fullscreen { get; private set; }

    /// <summary>
    /// The screen full screen covers, by the name the system gives it (such as <c>DP-1</c>, one of
    /// <see cref="Screens"/>); empty for the main screen, which full screen also takes while no screen of that name is
    /// connected (<c>Engine:Window:Screen</c>).
    /// </summary>
    public string Screen { get; private set; }

    /// <summary><see cref="Screen"/> as the menu and the console show it: its name, or "main".</summary>
    public string ScreenLabel => Screen.Length > 0 ? Screen : MainScreenLabel;

    /// <summary>What the menu and the console call the main screen (an empty <see cref="Screen"/>).</summary>
    public const string MainScreenLabel = "main";

    /// <summary>
    /// The names of the connected screens, the main one first (<see cref="WindowService.Screens"/>); empty without a
    /// window. <see cref="ReadScreens"/> reads them again.
    /// </summary>
    public IReadOnlyList<string> Screens => _window?.Screens ?? [];

    /// <summary>The keybindings, kept in the file's <c>"Keys"</c> (<see cref="KeyBindings"/>).</summary>
    public KeyBindings Keys { get; }

    /// <summary>
    /// Makes one of a game's own on/off settings, kept as <paramref name="name"/> in the file's <c>"Game"</c> object. Its
    /// value is the file's when that is a JSON boolean, else <paramref name="defaultValue"/>. A game calls it once for
    /// each switch at its run's <c>game</c> step (<c>IRunStartup.Start</c>), which runs after <see cref="Initialize"/>
    /// has read the file.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is empty or white space, or was registered before as a switch or a value.
    /// </exception>
    public GameSwitch RegisterSwitch(string name, bool defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_switchIndex.ContainsKey(name) || _valueIndex.ContainsKey(name))
        {
            throw new ArgumentException($"The game setting '{name}' is registered twice.", nameof(name));
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

    /// <summary>
    /// Makes one of a game's own number settings, kept as <paramref name="name"/> in the file's <c>"Game"</c> object. Its
    /// value is the file's when that is a JSON number, else <paramref name="defaultValue"/>, kept within
    /// <paramref name="least"/> and <paramref name="most"/> either way. A game calls it once for each value at its run's
    /// <c>game</c> step (<c>IRunStartup.Start</c>), which runs after <see cref="Initialize"/> has read the file. Switches
    /// and values share one set of names.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is empty or white space, or was registered before as a switch or a value; a bound or the default is not a
    /// finite number; or <paramref name="least"/> is above <paramref name="most"/>.
    /// </exception>
    public GameValue RegisterValue(string name, float defaultValue, float least, float most)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_switchIndex.ContainsKey(name) || _valueIndex.ContainsKey(name))
        {
            throw new ArgumentException($"The game setting '{name}' is registered twice.", nameof(name));
        }

        if (!float.IsFinite(least) || !float.IsFinite(most) || !float.IsFinite(defaultValue))
        {
            throw new ArgumentException(
                $"The game value '{name}' needs a finite default and finite bounds.",
                nameof(name)
            );
        }

        if (least > most)
        {
            throw new ArgumentException(
                $"The game value '{name}' has its least ({least}) above its most ({most}).",
                nameof(least)
            );
        }

        var number = Math.Clamp(defaultValue, least, most);
        if (
            _file.TryGetValue(GameKey, out var section)
            && section.ValueKind == JsonValueKind.Object
            && section.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetSingle(out var read)
            && float.IsFinite(read)
        )
        {
            number = Math.Clamp(read, least, most);
        }

        var which = new GameValue(_valueNames.Count);
        _valueIndex.Add(name, which.Index);
        _valueNames.Add(name);
        _valueNumbers.Add(number);
        _valueLeast.Add(least);
        _valueMost.Add(most);
        return which;
    }

    /// <summary>A game's number setting. Allocates nothing.</summary>
    public float ValueOf(GameValue which) => _valueNumbers[which.Index];

    /// <summary>The least a game's number setting can be.</summary>
    public float LeastOf(GameValue which) => _valueLeast[which.Index];

    /// <summary>The most a game's number setting can be.</summary>
    public float MostOf(GameValue which) => _valueMost[which.Index];

    /// <summary>The name a game's value was registered with, as it is kept in the file.</summary>
    public string ValueName(GameValue which) => _valueNames[which.Index];

    /// <summary>
    /// Sets a game's number setting, kept within its bounds. A change is written by the next <see cref="Save"/>; the same
    /// value (after keeping it within the bounds), or one that is not a finite number, changes nothing. Allocates nothing.
    /// </summary>
    public void SetValue(GameValue which, float value)
    {
        if (!float.IsFinite(value))
        {
            return;
        }

        value = Math.Clamp(value, _valueLeast[which.Index], _valueMost[which.Index]);
        if (_valueNumbers[which.Index] == value)
        {
            return;
        }

        _valueNumbers[which.Index] = value;
        _changed = true;
        Version++;
    }

    /// <summary>
    /// Adds a game's value to the graphics presets' table (such as its grass's density): choosing Low, Medium, or High
    /// sets it to <paramref name="low"/>, <paramref name="medium"/>, or <paramref name="high"/>, each kept within the
    /// value's bounds, and <see cref="GraphicsPreset"/> matches it too. When the file names a preset, the value takes that
    /// preset's, as the engine's values did at the file's reading. A game calls it once for each such value at its run's
    /// <c>game</c> step, after <see cref="RegisterValue"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The value was added before, or a number is not finite.</exception>
    public void AddPresetValue(GameValue which, float low, float medium, float high)
    {
        if (!float.IsFinite(low) || !float.IsFinite(medium) || !float.IsFinite(high))
        {
            throw new ArgumentException(
                $"The game value '{ValueName(which)}' needs finite preset values.",
                nameof(which)
            );
        }

        foreach (var added in _presetValues)
        {
            if (added.Value == which)
            {
                throw new ArgumentException(
                    $"The game value '{ValueName(which)}' is in the presets twice.",
                    nameof(which)
                );
            }
        }

        var least = _valueLeast[which.Index];
        var most = _valueMost[which.Index];
        var value = new PresetValue(
            which,
            Math.Clamp(low, least, most),
            Math.Clamp(medium, least, most),
            Math.Clamp(high, least, most)
        );
        _presetValues.Add(value);

        // A file that names a preset takes the preset's value, as it took the engine's at its reading.
        if (_filePreset != GraphicsPreset.Custom && !Near(ValueOf(which), value.Of(_filePreset)))
        {
            _valueNumbers[which.Index] = value.Of(_filePreset);
            _changed = true;
        }
    }

    /// <summary>
    /// Sets every value in <paramref name="preset"/>'s table at once, each applied at once as its own setter does:
    /// anti-aliasing, shadows, object detail, effects detail, and the game's values (<see cref="AddPresetValue"/>).
    /// <see cref="GraphicsPreset.Custom"/> changes nothing. Dynamic render scaling, its threshold, and the render scale
    /// are in no preset.
    /// </summary>
    public void SetGraphicsPreset(GraphicsPreset preset)
    {
        if (preset is not (GraphicsPreset.Low or GraphicsPreset.Medium or GraphicsPreset.High))
        {
            return;
        }

        var table = s_presets[(int)preset];
        SetAntiAliasMode(table.AntiAlias);
        SetShadows(table.Shadows);
        SetObjectDetail(PresetObjectDetail(preset));
        SetEffectsDetail(table.EffectsDetail);
        foreach (var value in _presetValues)
        {
            SetValue(value.Value, value.Of(preset));
        }
    }

    /// <summary>True when every value in <paramref name="preset"/>'s table (Low, Medium, or High) is set as it says.</summary>
    private bool Matches(GraphicsPreset preset)
    {
        var table = s_presets[(int)preset];
        if (
            AntiAliasMode != table.AntiAlias
            || Shadows != table.Shadows
            || !Near(ObjectDetail, PresetObjectDetail(preset))
            || !Near(EffectsDetail, table.EffectsDetail)
        )
        {
            return false;
        }

        foreach (var value in _presetValues)
        {
            if (!Near(ValueOf(value.Value), value.Of(preset)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The object detail of <paramref name="preset"/> (Low, Medium, or High): its table's or the options', whichever is
    /// coarser, so a game whose default is coarser has its presets no finer.
    /// </summary>
    private float PresetObjectDetail(GraphicsPreset preset) =>
        Math.Max(s_presets[(int)preset].ObjectDetail, _optionsObjectDetail);

    /// <summary>Equal to the four places the file keeps.</summary>
    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

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
            _audio.MusicVolume = MusicVolume;
            _audio.EffectsVolume = EffectsVolume;
        }

        if (_toolkit is not null)
        {
            _toolkit.Scale = UiScale;
        }

        if (_view is not null)
        {
            _view.DynamicScaling = DynamicScaling;
            _view.ScalingThreshold = ScalingThreshold;
            _view.FixedRenderScale = RenderScale;
            _view.Shadows = Shadows;
            _view.AntiAliasing = AntiAliasing;
        }

        if (_meshes is not null)
        {
            _meshes.ObjectVoxelPixels = ObjectDetail;
        }

        if (_effects is not null)
        {
            _effects.Ceiling = EffectsDetail;
        }
    }

    /// <summary>
    /// Sets the object detail, kept within <see cref="LeastObjectDetail"/> and <see cref="MostObjectDetail"/>; at once.
    /// </summary>
    public void SetObjectDetail(float pixels)
    {
        if (!float.IsFinite(pixels))
        {
            return;
        }

        var clamped = Math.Clamp(pixels, LeastObjectDetail, MostObjectDetail);
        if (Change(ObjectDetailKey, ObjectDetail, clamped, out var value))
        {
            ObjectDetail = value;
            Apply();
        }
    }

    /// <summary>Sets the effects detail, kept within <see cref="EffectsBudget.MinimumShare"/> and 1; at once.</summary>
    public void SetEffectsDetail(float share)
    {
        if (!float.IsFinite(share))
        {
            return;
        }

        var clamped = Math.Clamp(share, EffectsBudget.MinimumShare, EffectsBudget.MostShare);
        if (Change(EffectsDetailKey, EffectsDetail, clamped, out var value))
        {
            EffectsDetail = value;
            Apply();
        }
    }

    /// <summary>
    /// Sets the render scale used while dynamic render scaling is off, kept within
    /// <see cref="Rendering.RenderScale.Min"/> and 1; at once.
    /// </summary>
    public void SetRenderScale(float scale)
    {
        if (!float.IsFinite(scale))
        {
            return;
        }

        var clamped = Math.Clamp(scale, Rendering.RenderScale.Min, Rendering.RenderScale.Max);
        if (Change(RenderScaleKey, RenderScale, clamped, out var value))
        {
            RenderScale = value;
            Apply();
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

    /// <summary>Sets music's volume under the master, 0 to 1; at once.</summary>
    public void SetMusicVolume(float volume)
    {
        if (Change(MusicVolumeKey, MusicVolume, Math.Clamp(volume, 0, 1), out var value))
        {
            MusicVolume = value;
            Apply();
        }
    }

    /// <summary>Sets the effects' volume under the master, 0 to 1; at once.</summary>
    public void SetEffectsVolume(float volume)
    {
        if (Change(EffectsVolumeKey, EffectsVolume, Math.Clamp(volume, 0, 1), out var value))
        {
            EffectsVolume = value;
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
        _window?.SetFullscreen(fullscreen, Screen);
    }

    /// <summary>
    /// Sets the screen full screen covers, by name (empty for the main one); a window in full screen moves to it at
    /// once. A name longer than <see cref="WindowSettings.MostScreenNameLength"/> characters changes nothing.
    /// </summary>
    public void SetScreen(string screen)
    {
        screen ??= "";
        if (
            screen.Length > WindowSettings.MostScreenNameLength
            || string.Equals(screen, Screen, StringComparison.Ordinal)
        )
        {
            return;
        }

        Screen = screen;
        Changed(ScreenKey);
        _window?.SetScreen(screen);
    }

    /// <summary>
    /// Reads the connected screens again (<see cref="Screens"/>), such as when the Settings page opens.
    /// </summary>
    public void ReadScreens() => _window?.ReadScreens();

    /// <summary>
    /// Writes <c>settings.json</c> when a setting or a keybinding changed since: its version, every setting, but those the
    /// command line gave keep what the file had (and the file keeps its preset while one of them is in the preset's
    /// table), then the keybindings that differ from their defaults, then the game's switches and
    /// values (<c>"Game"</c>: every registered switch, every registered value, then what the file's object held that
    /// this run did not register), then what the file held that this build does not know. It writes a file beside it and
    /// moves that over the old one, so a failed write leaves the old file whole; a failure is a Warning, the game goes on,
    /// and the next save tries again.
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
                writer.WriteNumber(FileVersionKey, FileVersion);
                WriteNumber(writer, VolumeKey, Volume);
                WriteNumber(writer, MusicVolumeKey, MusicVolume);
                WriteNumber(writer, EffectsVolumeKey, EffectsVolume);
                WriteNumber(writer, SensitivityKey, MouseSensitivity);
                WriteNumber(writer, FieldOfViewKey, FieldOfViewDegrees);
                WriteBoolean(writer, DynamicScalingKey, DynamicScaling);
                WriteNumber(writer, ScalingThresholdKey, ScalingThreshold);
                WriteBoolean(writer, ShadowsKey, Shadows);
                WriteBoolean(writer, AntiAliasingKey, AntiAliasing);
                WriteNumber(writer, MsaaSamplesKey, MsaaSamples);
                WritePreset(writer);
                WriteNumber(writer, ObjectDetailKey, ObjectDetail);
                WriteNumber(writer, EffectsDetailKey, EffectsDetail);
                WriteNumber(writer, RenderScaleKey, RenderScale);
                WriteBoolean(writer, VSyncKey, VSync);
                WriteBoolean(writer, FullscreenKey, Fullscreen);
                WriteString(writer, ScreenKey, Screen);
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

    /// <summary>
    /// The file's values, for every setting the command line did not give; anything else in it is left alone. A preset
    /// the file names sets its table's values as they are now (<see cref="TakeFilesPreset"/>), and an old file's default
    /// threshold takes the options'.
    /// </summary>
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
        MusicVolume = Read(MusicVolumeKey, MusicVolume, 0, 1);
        EffectsVolume = Read(EffectsVolumeKey, EffectsVolume, 0, 1);
        MouseSensitivity = Read(SensitivityKey, MouseSensitivity, LeastSensitivity, MostSensitivity);
        FieldOfViewDegrees = Read(FieldOfViewKey, FieldOfViewDegrees, LeastFieldOfView, MostFieldOfView);
        DynamicScaling = Read(DynamicScalingKey, DynamicScaling);
        ScalingThreshold = Read(
            ScalingThresholdKey,
            ScalingThreshold,
            DynamicRenderScale.LeastThreshold,
            DynamicRenderScale.MostThreshold
        );
        if (
            !_fromCommandLine.Contains(ScalingThresholdKey)
            && _file.TryGetValue(ScalingThresholdKey, out var threshold)
            && threshold.ValueKind == JsonValueKind.Number
            && threshold.TryGetSingle(out var stored)
            && (
                (stored == OldDefaultThreshold && !_file.ContainsKey(GraphicsPresetKey))
                || (stored == UnversionedDefaultThreshold && ReadFileVersion() == 1)
            )
        )
        {
            // A file from before the presets, or from before the file's version, holds the threshold's default of its
            // time: it takes the options' threshold, and the next save writes it with the version.
            ScalingThreshold = _optionsThreshold;
            _changed = true;
        }

        Shadows = Read(ShadowsKey, Shadows);
        AntiAliasing = Read(AntiAliasingKey, AntiAliasing);
        MsaaSamples = AntiAliasModes.SnapSamples((int)Read(MsaaSamplesKey, MsaaSamples, 1, 8));
        ObjectDetail = Read(ObjectDetailKey, ObjectDetail, LeastObjectDetail, MostObjectDetail);
        EffectsDetail = Read(EffectsDetailKey, EffectsDetail, EffectsBudget.MinimumShare, EffectsBudget.MostShare);
        _filePreset = ReadPreset();
        TakeFilesPreset();
        RenderScale = Read(RenderScaleKey, RenderScale, Rendering.RenderScale.Min, Rendering.RenderScale.Max);
        VSync = Read(VSyncKey, VSync);
        Fullscreen = Read(FullscreenKey, Fullscreen);
        Screen = Read(ScreenKey, Screen);
        UiScale = Read(UiScaleKey, UiScale, LeastUiScale, MostUiScale);
        if (_file.TryGetValue(KeysKey, out var keys))
        {
            Keys.Load(keys);
        }

        LogLoaded(_path);
    }

    /// <summary>
    /// Sets the engine's table values to those of the preset the file names, as its table is now, but for the values the
    /// command line gave. <see cref="Save"/> writes the preset the values match, so values that no longer match the
    /// preset named are an earlier table's, such as a game's object detail made coarser since.
    /// </summary>
    private void TakeFilesPreset()
    {
        if (_filePreset == GraphicsPreset.Custom || Matches(_filePreset))
        {
            return;
        }

        var table = s_presets[(int)_filePreset];
        if (!_fromCommandLine.Contains(AntiAliasingKey) && !_fromCommandLine.Contains(MsaaSamplesKey))
        {
            // As SetAntiAliasMode does: the MSAA modes leave SMAA's switch as it was.
            if (table.AntiAlias is AntiAliasMode.Off or AntiAliasMode.Smaa)
            {
                AntiAliasing = table.AntiAlias == AntiAliasMode.Smaa;
            }

            MsaaSamples = AntiAliasModes.Samples(table.AntiAlias);
        }

        if (!_fromCommandLine.Contains(ShadowsKey))
        {
            Shadows = table.Shadows;
        }

        if (!_fromCommandLine.Contains(ObjectDetailKey))
        {
            ObjectDetail = PresetObjectDetail(_filePreset);
        }

        EffectsDetail = table.EffectsDetail;
        _changed = true;
    }

    /// <summary>The preset the file's <c>"GraphicsPreset"</c> names, else Custom.</summary>
    private GraphicsPreset ReadPreset()
    {
        if (_file.TryGetValue(GraphicsPresetKey, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var label = element.GetString();
            for (var preset = GraphicsPreset.Low; preset <= GraphicsPreset.High; preset++)
            {
                if (string.Equals(label, GraphicsPresets.Label(preset), StringComparison.Ordinal))
                {
                    return preset;
                }
            }
        }

        return GraphicsPreset.Custom;
    }

    /// <summary>The file's <c>"Version"</c>, or 1 when it has none.</summary>
    private int ReadFileVersion() =>
        _file.TryGetValue(FileVersionKey, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out var version)
            ? version
            : 1;

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

    private string Read(string key, string value) =>
        !_fromCommandLine.Contains(key)
        && _file.TryGetValue(key, out var element)
        && element.ValueKind == JsonValueKind.String
        && element.GetString() is { Length: <= WindowSettings.MostScreenNameLength } read
            ? read
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
        if (_switchNames.Count == 0 && _valueNames.Count == 0)
        {
            // No switch and no value registered: the file's value stays as it was, whatever it is.
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

        for (var i = 0; i < _valueNames.Count; i++)
        {
            writer.WriteNumber(_valueNames[i], MathF.Round(_valueNumbers[i], 4));
        }

        if (section.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in section.EnumerateObject())
            {
                if (!_switchIndex.ContainsKey(property.Name) && !_valueIndex.ContainsKey(property.Name))
                {
                    property.WriteTo(writer);
                }
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The preset the values match; while the command line gives one of the table's values, the file's own preset, since
    /// the file then keeps its own value of that row.
    /// </summary>
    private void WritePreset(Utf8JsonWriter writer)
    {
        if (
            !_fromCommandLine.Contains(AntiAliasingKey)
            && !_fromCommandLine.Contains(MsaaSamplesKey)
            && !_fromCommandLine.Contains(ShadowsKey)
            && !_fromCommandLine.Contains(ObjectDetailKey)
        )
        {
            writer.WriteString(GraphicsPresetKey, GraphicsPresets.Label(GraphicsPreset));
        }
        else if (_file.TryGetValue(GraphicsPresetKey, out var kept))
        {
            writer.WritePropertyName(GraphicsPresetKey);
            kept.WriteTo(writer);
        }
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

    private void WriteString(Utf8JsonWriter writer, string key, string value)
    {
        if (!_fromCommandLine.Contains(key))
        {
            writer.WriteString(key, value);
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
        Message = "Settings: dynamic render scaling {DynamicScaling} at {Threshold} fps, shadows {Shadows}, anti-aliasing {AntiAliasing}, VSync {VSync}, full screen {Fullscreen} on screen {Screen}"
    )]
    private partial void LogApplied(
        bool dynamicScaling,
        float threshold,
        bool shadows,
        string antiAliasing,
        bool vSync,
        bool fullscreen,
        string screen
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Settings: graphics {Preset}, object detail {ObjectDetail}, effects detail {EffectsDetail}, render scale {RenderScale} with dynamic scaling off"
    )]
    private partial void LogGraphics(string preset, float objectDetail, float effectsDetail, float renderScale);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Settings: wrote {Path}")]
    private partial void LogSaved(string path);

    /// <summary>The engine's values in one preset's table.</summary>
    private readonly record struct PresetTable(
        AntiAliasMode AntiAlias,
        bool Shadows,
        float ObjectDetail,
        float EffectsDetail
    );

    /// <summary>A game's value in the presets' table (<see cref="AddPresetValue"/>).</summary>
    private readonly record struct PresetValue(GameValue Value, float Low, float Medium, float High)
    {
        public float Of(GraphicsPreset preset) =>
            preset switch
            {
                GraphicsPreset.Low => Low,
                GraphicsPreset.Medium => Medium,
                _ => High,
            };
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Settings: {Path} could not be read ({Reason}); the options' values hold"
    )]
    private partial void LogUnreadable(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings: {Path} could not be written ({Reason})")]
    private partial void LogSaveFailed(string path, string reason);
}
