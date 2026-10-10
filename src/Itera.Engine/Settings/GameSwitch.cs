namespace Itera.Engine.Settings;

/// <summary>
/// One of a game's own on/off settings, as <see cref="PlayerSettings.RegisterSwitch"/> returns it: read with
/// <see cref="PlayerSettings.IsOn"/>, changed with <see cref="PlayerSettings.SetSwitch"/>, and kept in the
/// <c>"Game"</c> object of <c>settings.json</c>.
/// </summary>
/// <param name="Index">The switch's place in the order they were registered.</param>
public readonly record struct GameSwitch(int Index);
