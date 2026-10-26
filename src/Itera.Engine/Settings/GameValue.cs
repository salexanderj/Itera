namespace Itera.Engine.Settings;

/// <summary>
/// One of a game's own number settings, as <see cref="PlayerSettings.RegisterValue"/> returns it: read with
/// <see cref="PlayerSettings.ValueOf"/>, changed with <see cref="PlayerSettings.SetValue"/>, and kept in the
/// <c>"Game"</c> object of <c>settings.json</c>.
/// </summary>
/// <param name="Index">The value's place in the order they were registered.</param>
public readonly record struct GameValue(int Index);
