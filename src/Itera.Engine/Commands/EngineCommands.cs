using System.Globalization;
using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Settings;
using Itera.Engine.UI;
using Itera.Engine.Windowing;

namespace Itera.Engine.Commands;

/// <summary>
/// The engine's console commands, all <see cref="CommandScope.Local"/>: <c>help</c>, <c>clear</c>, <c>lines</c>,
/// <c>shadows</c>, <c>scale</c> (the render scale), <c>aa</c>, <c>lights</c> (test lights), <c>view</c> (the debug views), <c>stats</c> (its key, F1 by default),
/// <c>screenshot</c>, <c>wait</c> (holds a script: <see cref="DevConsole.Hold"/>), <c>shaders</c>, and <c>quit</c>. A setting given no value prints what it is. <c>shadows</c>
/// changes the player's settings (<see cref="PlayerSettings"/>), as the menu does, so the next change of another
/// setting keeps it (otherwise it would put the file's back).
/// </summary>
public sealed class EngineCommands
{
    /// <summary>Widest help's first column (a command's name and usage) gets; a longer usage wraps within it.</summary>
    public const int MostUsageWidth = 36;

    /// <summary>Most ticks, frames, or seconds one <c>wait</c> may ask for.</summary>
    public const float MostWaited = 1_000_000;

    /// <summary>Seconds a script waits for a screenshot's PNG before it fails (no frames are drawn, for example).</summary>
    public const double ScreenshotHoldSeconds = 60;

    private readonly ConsoleCommands _commands;
    private readonly DevConsole _console;
    private readonly DebugLines _lines;
    private readonly RenderView _view;
    private readonly WorldMeshes? _meshes;
    private readonly MeshPass? _meshPass;
    private readonly StatsLayer _stats;
    private readonly WindowService _window;
    private readonly Screenshots _screenshots;
    private readonly ShaderReloads _shaderReloads;
    private readonly PlayerSettings _settings;
    private readonly DynamicRenderScale _dynamicScale;
    private readonly VulkanContext _vulkan;
    private readonly PointLights? _pointLights;
    private uint _testLightSeed = 0x9E37_79B9;

    public EngineCommands(
        ConsoleCommands commands,
        DevConsole console,
        DebugLines lines,
        RenderView view,
        StatsLayer stats,
        WindowService window,
        Screenshots screenshots,
        ShaderReloads shaderReloads,
        PlayerSettings settings,
        DynamicRenderScale dynamicScale,
        VulkanContext vulkan,
        PointLights? pointLights = null,
        WorldMeshes? meshes = null,
        MeshPass? meshPass = null
    )
    {
        _meshPass = meshPass;
        _vulkan = vulkan;
        _meshes = meshes;
        _pointLights = pointLights;
        _settings = settings;
        _dynamicScale = dynamicScale;
        _screenshots = screenshots;
        _shaderReloads = shaderReloads;
        _commands = commands;
        _console = console;
        _lines = lines;
        _view = view;
        _stats = stats;
        _window = window;
    }

    /// <summary>Adds the commands to the registry. Once, at startup.</summary>
    public void Register()
    {
        _commands.Register("help", "[command]", "Lists the commands, or says what one does.", CommandScope.Local, Help);
        _commands.Register("clear", "", "Empties the console.", CommandScope.Local, _ => _console.Clear());
        _commands.Register(
            "lines",
            "[on|off]",
            "Debug lines: bodies' outlines, stand-ins, and collision cells.",
            CommandScope.Local,
            Lines
        );
        _commands.Register("shadows", "[on|off]", "The sun's shadows.", CommandScope.Local, Shadows);
        _commands.Register(
            "scale",
            "[0.5-1|auto]",
            "The render scale for this run (not saved): the world is drawn at this share of the screen's size and upscaled with FSR 1; auto gives it back to the settings.",
            CommandScope.Local,
            Scale
        );
        _commands.Register(
            "aa",
            "[on|off|edges|weights|auto]",
            "Anti-aliasing (SMAA, when the world is drawn with one sample a pixel) for this run (not saved); edges and weights show what it finds; auto gives it back to the settings.",
            CommandScope.Local,
            AntiAlias
        );
        _commands.Register(
            "lights",
            "test N|clear",
            "Point lights for this run: test scatters N warm lights round the camera for ten minutes (the light budget keeps the brightest); clear takes every light's flash away.",
            CommandScope.Local,
            Lights
        );
        _commands.Register(
            "view",
            "[off|wire|levels|all|plain]",
            "Debug views for this run (not saved): wire draws the meshes' edges over the world, levels tints each surface by its voxels' size (1 cm magenta, 2 violet, 4 blue, 8 cyan, 16 green, 32 yellow, 64 orange), all both, plain draws every block from its plain mesh with the seams between levels open.",
            CommandScope.Local,
            View
        );
        _commands.Register(
            "materials",
            "[textures|flat] [all] | sets | set ID SET|none [MATCH] | tune ID DETAIL RELIEF BLUR | bake ID top|side [NAME]",
            "How the world's materials are drawn for this run (not saved): from their texture sets where they name one, or in flat colours; all draws every world material without a set from the first small set, for timings. sets lists the texture sets loaded; set draws material ID from a set (its colour matched to the material's by MATCH, 0 to 1) or plain; tune draws material ID's set keeping DETAIL of its colour's variation and RELIEF of its relief (0 to 1), BLUR mip levels softer (0 to 4); bake writes material ID's recipe, on the ground's plane or a wall's, as images for tools/Textures into the data folder's Bakes/.",
            CommandScope.Local,
            Materials
        );
        _commands.Register(
            "stats",
            "[on|off]",
            "The stats line in the top-right corner (its key: see Keybindings).",
            CommandScope.Local,
            Stats
        );
        _commands.Register(
            "screenshot",
            "[scale] [noui] [PATH.png]",
            "Writes the picture to a PNG, at PATH.png (from the game's working folder) or in the data folder's Screenshots/, at a share of its size (0.1 to 1), with or without the UI. In a script, the commands after it wait until the PNG is written.",
            CommandScope.Local,
            Screenshot
        );
        _commands.Register(
            "wait",
            "loaded|ticks N|frames N|seconds N [TIMEOUT]",
            "Holds the commands after it in the line (or the startup script) until a match is played with its loading screen gone, N fixed steps have run, N frames are built, or N seconds of the game clock pass; past TIMEOUT seconds (300) it fails and they are dropped.",
            CommandScope.Local,
            Wait
        );
        _commands.Register(
            "shaders",
            "[reload]",
            "The folder shaders are compiled from; reload compiles them again and remakes the pipelines (the old ones stay on an error).",
            CommandScope.Local,
            Shaders
        );
        _commands.Register(
            "quit",
            "[CODE]",
            "Ends the game, as the menu's Quit does; CODE (0 to 255) is the process's exit code.",
            CommandScope.Local,
            Quit
        );
    }

    private void Quit(CommandContext context)
    {
        var code = 0;
        if (context.Count > 1 || (context.Count == 1 && !(context.TryGetInt(0, out code) && code is >= 0 and <= 255)))
        {
            context.PrintUsage();
            return;
        }

        _window.RequestExit("console", code);
    }

    private void Wait(CommandContext context)
    {
        if (context.Count == 0)
        {
            context.PrintUsage();
            return;
        }

        var now = _console.Now;
        Func<bool> done;
        var next = 1;
        if (context.Is(0, "loaded"))
        {
            done = () => _console.Now.Loaded;
        }
        else if (context.Count >= 2 && context.TryGetFloat(1, out var n) && n >= 0 && n <= MostWaited)
        {
            next = 2;
            if (context.Is(0, "ticks"))
            {
                var steps = (ulong)Math.Ceiling(n);
                done = () => _console.Now.Steps - now.Steps >= steps;
            }
            else if (context.Is(0, "frames"))
            {
                var frames = (ulong)Math.Ceiling(n);
                done = () => _console.Now.Frames - now.Frames >= frames;
            }
            else if (context.Is(0, "seconds"))
            {
                done = () => _console.Now.Seconds - now.Seconds >= n;
            }
            else
            {
                context.PrintUsage();
                return;
            }
        }
        else
        {
            context.PrintUsage();
            return;
        }

        var timeout = DevConsole.DefaultHoldSeconds;
        if (context.Count > next + 1)
        {
            context.PrintUsage();
            return;
        }

        if (context.Count == next + 1)
        {
            if (!context.TryGetFloat(next, out var given) || !(given > 0 && given <= DevConsole.MostHoldSeconds))
            {
                context.PrintUsage();
                return;
            }

            timeout = given;
        }

        var what = "wait " + string.Join(' ', Enumerable.Range(0, next).Select(i => context[i]));
        if (!_console.Hold(what, done, timeout))
        {
            context.PrintError(
                "wait: holds the commands after it in a console line or the startup script; the control port has a wait of its own (game.py wait)."
            );
        }
    }

    private void Help(CommandContext context)
    {
        if (context.Count > 0)
        {
            if (!_commands.TryGet(context[0], out var command))
            {
                context.PrintError($"{context[0]}: no such command.");
                return;
            }

            context.Print($"{command.Name} {command.Usage}".TrimEnd() + ScopeNote(command.Scope));
            context.PrintColumns("", command.Help, 2, 0);
            return;
        }

        // Two columns, each wrapped within itself: the name and usage, at most MostUsageWidth wide, a long usage's
        // later rows under its first argument so they do not read as other commands, and what it does.
        var width = Math.Min(MostUsageWidth, _commands.All.Max(c => (c.Name + " " + c.Usage).Length));
        foreach (var command in _commands.All)
        {
            context.PrintColumns(
                (command.Name + " " + command.Usage).TrimEnd(),
                command.Help,
                width + 2,
                command.Name.Length + 1
            );
        }
    }

    private static string ScopeNote(CommandScope scope) =>
        scope switch
        {
            CommandScope.Match => "  (offline or host)",
            CommandScope.Offline => "  (offline only)",
            _ => "",
        };

    private void Lines(CommandContext context)
    {
        if (context.Count > 0)
        {
            if (!context.TryGetOnOff(0, out var on))
            {
                context.PrintUsage();
                return;
            }

            _lines.Enabled = on;
        }

        context.Print("lines " + CommandContext.OnOff(_lines.Enabled));
    }

    private void Lights(CommandContext context)
    {
        if (_pointLights is null)
        {
            context.PrintError("lights: no point lights in this run.");
            return;
        }

        if (context.Count == 1 && context.Is(0, "clear"))
        {
            _pointLights.Clear();
            context.Print("lights cleared");
            return;
        }

        if (context.Count != 2 || !context.Is(0, "test") || !context.TryGetFloat(1, out var asked))
        {
            context.PrintUsage();
            return;
        }

        var count = Math.Clamp((int)asked, 0, PointLights.MostLights);
        var camera = _view.Camera.Position;
        for (var i = 0; i < count; i++)
        {
            var place = new Vector3(Random() * 50 - 25, Random() * 3 - 2.5f, Random() * 50 - 25);
            var colour = new Vector3(1, 0.4f + 0.5f * Random(), 0.2f + 0.3f * Random());
            _pointLights.Flash(camera + place, colour, 4 + 16 * Random(), 600);
        }

        context.Print(string.Create(CultureInfo.InvariantCulture, $"lights: {count} test lights for ten minutes"));
    }

    /// <summary>The test lights' scatter, 0 to 1 (a xorshift, so each run's lights repeat).</summary>
    private float Random()
    {
        _testLightSeed ^= _testLightSeed << 13;
        _testLightSeed ^= _testLightSeed >> 17;
        _testLightSeed ^= _testLightSeed << 5;
        return (_testLightSeed >> 8) / (float)(1 << 24);
    }

    /// <summary>The switches <c>materials</c> sets and <c>view</c> keeps.</summary>
    private const ViewSwitches MaterialViews = ViewSwitches.MaterialsFlat | ViewSwitches.MaterialsAll;

    /// <summary>
    /// How the world's materials are drawn for the run (docs/rendering-redesign.md, the materials plan): from their
    /// texture sets where they have one (the default) or in their flat colours; with <c>all</c>, every world material
    /// that way (for timings: <c>materials textures all</c> draws every one without a set from the first small set).
    /// </summary>
    private void Materials(CommandContext context)
    {
        if (context.Count >= 1 && (context.Is(0, "set") || context.Is(0, "sets")))
        {
            MaterialSets(context);
            return;
        }

        if (context.Count >= 1 && context.Is(0, "bake"))
        {
            BakeMaterial(context);
            return;
        }

        if (context.Count >= 1 && context.Is(0, "tune"))
        {
            TuneMaterial(context);
            return;
        }

        if (context.Count > 2 || (context.Count == 2 && !context.Is(1, "all")))
        {
            context.PrintUsage();
            return;
        }

        if (context.Count >= 1)
        {
            ViewSwitches method;
            if (context.Is(0, "textures"))
            {
                method = ViewSwitches.None;
            }
            else if (context.Is(0, "flat"))
            {
                method = ViewSwitches.MaterialsFlat;
            }
            else
            {
                context.PrintUsage();
                return;
            }

            var all = context.Count == 2 ? ViewSwitches.MaterialsAll : ViewSwitches.None;
            _view.DebugViews = (_view.DebugViews & ~MaterialViews) | method | all;
        }

        var views = _view.DebugViews;
        var shown = (views & ViewSwitches.MaterialsFlat) != 0 ? "flat" : "textures";
        context.Print(
            "materials "
                + shown
                + ((views & ViewSwitches.MaterialsAll) != 0 ? " on every material" : " where a set is named")
        );
    }

    /// <summary>
    /// <c>materials sets</c> lists the texture sets loaded; <c>materials set ID SET|none [MATCH]</c> draws material ID from
    /// a set (its colour matched by MATCH, 0 to 1, 0 by default) or from its recipe for the rest of the run.
    /// </summary>
    private void MaterialSets(CommandContext context)
    {
        if (_meshes is null)
        {
            context.PrintError("materials: the world's meshes are not running.");
            return;
        }

        if (context.Is(0, "sets") && context.Count == 1)
        {
            context.Print("texture sets: " + string.Join(", ", _meshes.TextureSetNames));
            return;
        }

        var match = 0f;
        if (
            !context.Is(0, "set")
            || context.Count is < 3 or > 4
            || !context.TryGetFloat(1, out var id)
            || id is < 1 or > 255
            || id != MathF.Floor(id)
            || (context.Count == 4 && (!context.TryGetFloat(3, out match) || match is < 0 or > 1))
        )
        {
            context.PrintUsage();
            return;
        }

        var name = context.Is(2, "none") ? null : context[2];
        if (!_meshes.SetTexture((byte)id, name, match))
        {
            context.PrintError($"materials: no texture set {name} (materials sets lists them).");
            return;
        }

        context.Print(
            name is null
                ? $"material {id} drawn from its recipe"
                : $"material {id} drawn from {name}, colour matched {match.ToString("0.##", CultureInfo.InvariantCulture)}"
        );
    }

    /// <summary>
    /// <c>materials bake ID top|side [NAME]</c>: bakes material ID's recipe on the ground's plane or a wall's into the data
    /// folder's <c>Bakes/NAME</c> (by default <c>material-ID-top</c> or <c>-side</c>), for <c>tools/Textures</c>.
    /// </summary>
    private void BakeMaterial(CommandContext context)
    {
        if (
            context.Count is < 3 or > 4
            || !context.TryGetFloat(1, out var id)
            || id is < 1 or > 255
            || id != MathF.Floor(id)
            || !(context.Is(2, "top") || context.Is(2, "side"))
        )
        {
            context.PrintUsage();
            return;
        }

        if (_meshPass is null)
        {
            context.PrintError("materials: the world's meshes are not running.");
            return;
        }

        var side = context.Is(2, "side");
        var name = context.Count == 4 ? context[3] : $"material-{id}-{(side ? "side" : "top")}";
        var folder = _meshPass.RequestBake((byte)id, side, name);
        if (folder is null)
        {
            context.PrintError("materials: a bake is running; ask again in a moment.");
            return;
        }

        context.Print($"baking material {id} into {folder}");
    }

    /// <summary>
    /// <c>materials tune ID DETAIL RELIEF BLUR</c>: draws material ID's texture set keeping DETAIL of its colour's variation
    /// and RELIEF of its normal map and occlusion (0 to 1), BLUR mip levels softer (0 to 4), for the run.
    /// </summary>
    private void TuneMaterial(CommandContext context)
    {
        if (
            context.Count != 5
            || !context.TryGetFloat(1, out var id)
            || id is < 1 or > 255
            || id != MathF.Floor(id)
            || !context.TryGetFloat(2, out var detail)
            || detail is < 0 or > 1
            || !context.TryGetFloat(3, out var relief)
            || relief is < 0 or > 1
            || !context.TryGetFloat(4, out var blur)
            || blur is < 0 or > 4
        )
        {
            context.PrintUsage();
            return;
        }

        if (_meshes is null || !_meshes.SetTextureStyle((byte)id, detail, relief, blur))
        {
            context.PrintError("materials: the world's meshes are not running.");
            return;
        }

        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"material {id}: detail {detail:0.##}, relief {relief:0.##}, blur {blur:0.##} mip levels"
            )
        );
    }

    private void View(CommandContext context)
    {
        if (context.Count > 1)
        {
            context.PrintUsage();
            return;
        }

        if (context.Count == 1)
        {
            ViewSwitches views;
            if (context.Is(0, "off"))
            {
                views = ViewSwitches.None;
            }
            else if (context.Is(0, "wire"))
            {
                views = ViewSwitches.Wireframe;
            }
            else if (context.Is(0, "levels"))
            {
                views = ViewSwitches.Levels;
            }
            else if (context.Is(0, "all"))
            {
                views = ViewSwitches.Wireframe | ViewSwitches.Levels;
            }
            else if (context.Is(0, "plain"))
            {
                views = ViewSwitches.Plain;
            }
            else
            {
                context.PrintUsage();
                return;
            }

            if ((views & ViewSwitches.Wireframe) != 0 && !_vulkan.SupportsWireframe)
            {
                context.PrintError("view: this device does not draw triangles as lines (fillModeNonSolid); no wire.");
                views &= ~ViewSwitches.Wireframe;
            }

            _view.DebugViews = views | (_view.DebugViews & MaterialViews);
        }

        var shown = (_view.DebugViews & ~MaterialViews) switch
        {
            ViewSwitches.Wireframe => "wire",
            ViewSwitches.Levels => "levels",
            ViewSwitches.Wireframe | ViewSwitches.Levels => "wire and levels",
            ViewSwitches.Plain => "plain",
            _ => "off",
        };
        context.Print("view " + shown);
    }

    private void AntiAlias(CommandContext context)
    {
        if (context.Count > 1)
        {
            context.PrintUsage();
            return;
        }

        if (context.Count == 1)
        {
            if (context.Is(0, "edges") || context.Is(0, "weights"))
            {
                _view.AntiAliasOverride = true;
                _view.AntiAliasPicture = context.Is(0, "edges") ? SmaaPicture.Edges : SmaaPicture.Weights;
            }
            else if (context.Is(0, "auto"))
            {
                _view.AntiAliasOverride = null;
                _view.AntiAliasPicture = SmaaPicture.Blended;
            }
            else if (context.TryGetOnOff(0, out var on))
            {
                _view.AntiAliasOverride = on;
                _view.AntiAliasPicture = SmaaPicture.Blended;
            }
            else
            {
                context.PrintUsage();
                return;
            }
        }

        var shown = _view.AntiAliasPicture switch
        {
            SmaaPicture.Edges => ", showing its edges",
            SmaaPicture.Weights => ", showing its weights",
            _ => "",
        };
        var from = _view.AntiAliasOverride is null ? " (the settings')" : " (set for this run)";
        context.Print("aa " + CommandContext.OnOff(_view.AntiAliasDrawn) + shown + from);
    }

    private void Shadows(CommandContext context)
    {
        if (context.Count > 0)
        {
            if (!context.TryGetOnOff(0, out var on))
            {
                context.PrintUsage();
                return;
            }

            _settings.SetShadows(on);
        }

        context.Print("shadows " + CommandContext.OnOff(_view.Shadows));
    }

    private void Screenshot(CommandContext context)
    {
        var scale = 1f;
        var withUi = true;
        string? path = null;
        for (var i = 0; i < context.Count; i++)
        {
            if (context.Is(i, "noui"))
            {
                withUi = false;
            }
            else if (context.TryGetFloat(i, out var share))
            {
                if (!(share >= 0.1f && share <= 1f))
                {
                    context.PrintUsage();
                    return;
                }

                scale = share;
            }
            else if (path is null && context[i].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.GetFullPath(context[i]);
            }
            else
            {
                context.PrintUsage();
                return;
            }
        }

        var request = new ScreenshotRequest(path ?? _screenshots.NewPath(), scale, withUi: withUi);
        if (_screenshots.Request(request) is { } refusal)
        {
            context.PrintError("screenshot: " + refusal);
            return;
        }

        context.Print("screenshot: writing " + request.Path);
        // In a script, the commands after it (a quit, a move elsewhere) wait for the PNG.
        _console.Hold(
            "screenshot " + request.Path,
            () => request.IsDone,
            ScreenshotHoldSeconds,
            () => request.Error is { } error ? "screenshot: " + error : null,
            () => _screenshots.Withdraw(request)
        );
    }

    private void Shaders(CommandContext context)
    {
        if (context.Count == 0)
        {
            context.Print("shaders from " + _shaderReloads.SourceDirectory);
            return;
        }

        if (context.Count > 1 || !context.Is(0, "reload"))
        {
            context.PrintUsage();
            return;
        }

        if (_shaderReloads.Request(new ShaderReloadRequest()) is { } refusal)
        {
            context.PrintError("shaders: " + refusal);
            return;
        }

        context.Print("shaders: reloading from " + _shaderReloads.SourceDirectory);
    }

    private void Scale(CommandContext context)
    {
        if (context.Count > 0)
        {
            if (string.Equals(context[0], "auto", StringComparison.OrdinalIgnoreCase))
            {
                _view.ScaleOverride = null;
            }
            else if (context.TryGetFloat(0, out var scale) && scale >= RenderScale.Min && scale <= RenderScale.Max)
            {
                _view.ScaleOverride = scale;
            }
            else
            {
                context.PrintUsage();
                return;
            }

            // The frame being built takes it at once; auto hands it back from the next frame.
            if (_view.ScaleOverride is { } set)
            {
                _view.RenderScale = set;
            }
        }

        var how =
            _view.ScaleOverride is not null ? "set for this run"
            : !_view.DynamicScaling ? "dynamic render scaling off"
            : string.Create(CultureInfo.InvariantCulture, $"dynamic, holding {_dynamicScale.TargetFps:F0} fps");
        context.Print("scale " + CommandContext.Number(_view.RenderScale) + " (" + how + ")");
    }

    private void Stats(CommandContext context)
    {
        if (context.Count > 0)
        {
            if (!context.TryGetOnOff(0, out var on))
            {
                context.PrintUsage();
                return;
            }

            _stats.Shown = on;
        }

        context.Print("stats " + CommandContext.OnOff(_stats.Shown));
    }
}
