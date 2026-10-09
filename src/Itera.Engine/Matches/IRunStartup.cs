namespace Itera.Engine.Matches;

/// <summary>
/// Something the game makes once per run, at the run's "game" step: before the mods load and before the audio thread
/// starts, after the renderer and the UI exist. Registries that refuse a name twice (sound and particle recipes
/// and a game's own definitions) are made here, since a match's services are made again for each match.
/// </summary>
public interface IRunStartup
{
    /// <summary>Makes what the game needs for the whole run.</summary>
    void Start();
}
