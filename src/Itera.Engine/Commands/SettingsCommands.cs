using System.Globalization;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>settings</c> (<see cref="CommandScope.Local"/>): the player's settings as the menu's settings page
/// shows them, read or changed by name and written to <c>settings.json</c> (<see cref="PlayerSettings"/>).
/// </summary>
public sealed class SettingsCommands(ConsoleCommands commands, PlayerSettings settings)
{
    /// <summary>Adds the command to the registry. Once, at startup.</summary>
    public void Register()
    {
        commands.Register(
            "settings",
            "[volume V|musicvolume V|effectsvolume V|sensitivity DEG|fov DEG|uiscale X|scaling on|off|threshold FPS|renderscale X|shadows on|off|aa off|smaa|2x|4x|8x|preset low|medium|high|objectdetail PX|effectsdetail X|vsync on|off|fullscreen on|off|screen NAME|main]",
            "The player's settings (the menu's Settings), kept in settings.json; with nothing, each one.",
            CommandScope.Local,
            Run
        );
    }

    private void Run(CommandContext context)
    {
        if (context.Count == 0)
        {
            Print(context);
            return;
        }

        if (context.Count != 2)
        {
            context.PrintUsage();
            return;
        }

        var name = context[0].ToUpperInvariant();
        if (name == "SCREEN")
        {
            if (!TrySetScreen(context))
            {
                return;
            }
        }
        else if (name == "PRESET")
        {
            if (!TryPreset(context, out var preset))
            {
                context.PrintUsage();
                return;
            }

            settings.SetGraphicsPreset(preset);
        }
        else if (name == "AA")
        {
            if (!TryAntiAliasMode(context, out var mode))
            {
                context.PrintUsage();
                return;
            }

            settings.SetAntiAliasMode(mode);
        }
        else if (name is "SCALING" or "SHADOWS" or "VSYNC" or "FULLSCREEN")
        {
            if (!context.TryGetOnOff(1, out var on))
            {
                context.PrintUsage();
                return;
            }

            switch (name)
            {
                case "SCALING":
                    settings.SetDynamicScaling(on);
                    break;
                case "SHADOWS":
                    settings.SetShadows(on);
                    break;
                case "VSYNC":
                    settings.SetVSync(on);
                    break;
                default:
                    settings.SetFullscreen(on);
                    break;
            }
        }
        else if (context.TryGetFloat(1, out var value))
        {
            switch (name)
            {
                case "VOLUME":
                    settings.SetVolume(value);
                    break;
                case "MUSICVOLUME":
                    settings.SetMusicVolume(value);
                    break;
                case "EFFECTSVOLUME":
                    settings.SetEffectsVolume(value);
                    break;
                case "SENSITIVITY":
                    settings.SetMouseSensitivity(value);
                    break;
                case "FOV":
                    settings.SetFieldOfView(value);
                    break;
                case "UISCALE":
                    settings.SetUiScale(value);
                    break;
                case "THRESHOLD":
                    // The menu's thresholds, as its slider offers them.
                    settings.SetScalingThreshold(DynamicRenderScale.NearestThreshold(value));
                    break;
                case "RENDERSCALE":
                    settings.SetRenderScale(value);
                    break;
                case "OBJECTDETAIL":
                    settings.SetObjectDetail(value);
                    break;
                case "EFFECTSDETAIL":
                    settings.SetEffectsDetail(value);
                    break;
                default:
                    context.PrintUsage();
                    return;
            }
        }
        else
        {
            context.PrintUsage();
            return;
        }

        settings.Save();
        Print(context);
    }

    /// <summary>
    /// Sets the screen full screen covers: <c>main</c>, or a connected screen's name in any case (kept as the system
    /// spells it). False, with the connected screens printed, for a name not connected.
    /// </summary>
    private bool TrySetScreen(CommandContext context)
    {
        if (context.Is(1, PlayerSettings.MainScreenLabel))
        {
            settings.SetScreen("");
            return true;
        }

        var screens = settings.Screens;
        for (var i = 0; i < screens.Count; i++)
        {
            if (string.Equals(screens[i], context[1], StringComparison.OrdinalIgnoreCase))
            {
                settings.SetScreen(screens[i]);
                return true;
            }
        }

        context.PrintError(
            screens.Count == 0
                ? $"No screen named {context[1]}: no screen is connected."
                : $"No screen named {context[1]}; the screens: {string.Join(", ", screens)}, or main."
        );
        return false;
    }

    /// <summary>The settings' Anti-aliasing by its console name (off, smaa, 2x, 4x, 8x).</summary>
    private static bool TryAntiAliasMode(CommandContext context, out AntiAliasMode mode)
    {
        for (var i = AntiAliasMode.Off; i <= AntiAliasMode.Msaa8; i++)
        {
            if (context.Is(1, AntiAliasModes.ConsoleName(i)))
            {
                mode = i;
                return true;
            }
        }

        mode = default;
        return false;
    }

    /// <summary>A graphics preset by its console name (low, medium, high); Custom is no choice.</summary>
    private static bool TryPreset(CommandContext context, out GraphicsPreset preset)
    {
        for (var i = GraphicsPreset.Low; i <= GraphicsPreset.High; i++)
        {
            if (context.Is(1, GraphicsPresets.ConsoleName(i)))
            {
                preset = i;
                return true;
            }
        }

        preset = default;
        return false;
    }

    private void Print(CommandContext context)
    {
        var culture = CultureInfo.InvariantCulture;
        context.Print(
            string.Create(
                culture,
                $"volume {settings.Volume:F2}, musicvolume {settings.MusicVolume:F2}, effectsvolume {settings.EffectsVolume:F2}, sensitivity {settings.MouseSensitivity:F2}, fov {settings.FieldOfViewDegrees:F0}, uiscale {settings.UiScale:F2}, scaling {CommandContext.OnOff(settings.DynamicScaling)}, threshold {settings.ScalingThreshold:F0}, renderscale {settings.RenderScale:F2}, shadows {CommandContext.OnOff(settings.Shadows)}, aa {AntiAliasModes.ConsoleName(settings.AntiAliasMode)}, preset {GraphicsPresets.ConsoleName(settings.GraphicsPreset)}, objectdetail {settings.ObjectDetail:F2}, effectsdetail {settings.EffectsDetail:F2}, vsync {CommandContext.OnOff(settings.VSync)}, fullscreen {CommandContext.OnOff(settings.Fullscreen)}, screen {settings.ScreenLabel}"
            )
        );
    }
}
