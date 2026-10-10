using System.Globalization;
using System.Numerics;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Microsoft.Extensions.Options;

namespace Game;

/// <summary>The minimap's size, bound from the <c>Game:Minimap</c> configuration section.</summary>
public sealed class MinimapOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "Game:Minimap";

    /// <summary>The least and most <see cref="Metres"/> can be.</summary>
    public const double MinMetres = 16;
    public const double MaxMetres = 256;

    /// <summary>How much of the world the minimap shows across, in metres, round the player.</summary>
    public double Metres { get; set; } = 64;
}

/// <summary>Rejects minimap options the game cannot draw, naming the configuration key.</summary>
public sealed class MinimapOptionsValidator : IValidateOptions<MinimapOptions>
{
    public ValidateOptionsResult Validate(string? name, MinimapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Metres is >= MinimapOptions.MinMetres and <= MinimapOptions.MaxMetres
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{MinimapOptions.SectionName}:Metres must be between {MinimapOptions.MinMetres} and {MinimapOptions.MaxMetres}; it is {options.Metres}."
                )
            );
    }
}

/// <summary>
/// The starter's minimap, run-wide: its image view (<see cref="RenderViews"/>, <see cref="Texels"/> square, drawn at most
/// every <see cref="EveryFrames"/> frames), its key (M, rebindable), and its switch in <c>settings.json</c> (on until the
/// player turns it off), made at the run's start (<see cref="Declare"/>); and each frame whether it shows and where the
/// player looks, which <see cref="Systems.MinimapSystem"/> sets and <see cref="MinimapLayer"/> draws. Main thread.
/// </summary>
public sealed class Minimap
{
    /// <summary>The image view's name.</summary>
    public const string ViewName = "minimap";

    /// <summary>The view's image, texels a side.</summary>
    public const int Texels = 512;

    /// <summary>The view is drawn at most once in this many frames.</summary>
    public const int EveryFrames = 4;

    /// <summary>The camera that draws it looks straight down, with north (-z) up the image and east (+x) to its right.</summary>
    public static Quaternion LookingDown { get; } = Quaternion.CreateFromYawPitchRoll(0, -MathF.PI / 2, 0);

    /// <summary>The image view's index, or -1 before <see cref="Declare"/>.</summary>
    public int View { get; private set; } = -1;

    /// <summary>The key that shows and hides it.</summary>
    public KeyAction Key { get; private set; }

    /// <summary>Whether it is shown, kept in <c>settings.json</c>.</summary>
    public GameSwitch Switch { get; private set; }

    /// <summary>True when the map is drawn this frame.</summary>
    public bool Shown { get; set; }

    /// <summary>Where the player looks this frame, in radians (0 north, positive turning left), for its arrow.</summary>
    public float Yaw { get; set; }

    /// <summary>Declares the image view, the key, and the switch. At the run's <c>game</c> step, once.</summary>
    public void Declare(RenderViews views, KeyBindings keys, PlayerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(settings);
        View = views.Declare(ViewName, Texels, Texels, EveryFrames);
        Key = keys.Register(
            "minimap",
            "Minimap",
            "Shows or hides the map in the top-right corner.",
            InputBinding.Of(Silk.NET.Input.Key.M)
        );
        Switch = settings.RegisterSwitch("Minimap", defaultValue: true);
    }
}
