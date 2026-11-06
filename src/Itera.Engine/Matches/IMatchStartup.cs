using Itera.Engine.Networking;

namespace Itera.Engine.Matches;

/// <summary>
/// Something the game makes once per match from the match's settings, at the match's "game" step: once the session has
/// settled the match and the origin is set, before physics, the world, the HUD layers, the systems, and the save
/// sections. A game registers it as a scoped service; every one is started in their registration order and finished in
/// reverse when the match ends. Match state a run-wide service holds for the match's length (what a game generates from
/// the match's seed, say) is made here, so every game of a session makes it alike before anything reads it. Main
/// thread.
/// </summary>
public interface IMatchStartup
{
    /// <summary>Makes what the game needs for this match, from its settings (its seed among them).</summary>
    void Start(MatchSettings match);

    /// <summary>Lets go of what <see cref="Start"/> made, as the match ends.</summary>
    void Finish();
}
