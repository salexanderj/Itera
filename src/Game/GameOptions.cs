using System.Globalization;
using Game.WorldGeneration;
using Itera.Engine.Systems;
using Microsoft.Extensions.Options;

namespace Game;

/// <summary>
/// How the local player's body is sized, and how it starts, bound from the <c>Game:Player</c> configuration section.
/// The body's radius, eye height, and step height follow from its height (<c>CharacterSize.ForHeight</c>).
/// </summary>
public sealed class PlayerOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "Game:Player";

    /// <summary>The least and most <see cref="HeightMetres"/> can be.</summary>
    public const double MinHeightMetres = 1.2;
    public const double MaxHeightMetres = 2.5;

    /// <summary>Height from the feet to the top of the head, in metres.</summary>
    public double HeightMetres { get; set; } = 1.7;

    /// <summary>
    /// When true, the game starts with the camera behind the player's right shoulder (third person; its key and the
    /// console's <c>camera</c> switch it for the rest of the run).
    /// </summary>
    public bool ThirdPerson { get; set; }
}

/// <summary>The world's debug aids, bound from the <c>Game:World</c> configuration section.</summary>
public sealed class WorldOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "Game:World";

    /// <summary>
    /// Debug aid: when set, the player's eye and the debug camera start here instead of at the world's start:
    /// "x,y,z,yaw,pitch" with the position in metres and the angles in degrees (yaw 0 looks along -z, 90 along -x; pitch
    /// below 0 looks down). The console's <c>pos</c> prints the pose in this form.
    /// </summary>
    public string? CameraPose { get; set; }
}

/// <summary>Rejects player options the game cannot run with, naming the configuration key.</summary>
public sealed class PlayerOptionsValidator : IValidateOptions<PlayerOptions>
{
    public ValidateOptionsResult Validate(string? name, PlayerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.HeightMetres is >= PlayerOptions.MinHeightMetres and <= PlayerOptions.MaxHeightMetres
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{PlayerOptions.SectionName}:HeightMetres must be between {PlayerOptions.MinHeightMetres} and {PlayerOptions.MaxHeightMetres}; it is {options.HeightMetres}."
                )
            );
    }
}

/// <summary>
/// Rejects world options the game cannot run with, naming the configuration key: a camera pose must parse and lie in
/// the play area (<see cref="PlayArea.HoldsEye"/> round the world's grid), since a start far outside the world makes
/// the streaming's column walks and the edits very large.
/// </summary>
public sealed class WorldOptionsValidator : IValidateOptions<WorldOptions>
{
    private static readonly PlayArea s_area = new(GameGenerator.WorldGrid);

    public ValidateOptionsResult Validate(string? name, WorldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.CameraPose))
        {
            return ValidateOptionsResult.Success;
        }

        if (!CameraPoseText.TryParse(options.CameraPose, out var eye, out _, out _))
        {
            return ValidateOptionsResult.Fail(
                $"{WorldOptions.SectionName}:CameraPose must be \"{CameraPoseText.Form}\", five finite numbers; it is '{options.CameraPose}'."
            );
        }

        return s_area.HoldsEye(eye)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{WorldOptions.SectionName}:CameraPose must lie in the play area ({s_area.EyeBounds}); it is '{options.CameraPose}'."
            );
    }
}
