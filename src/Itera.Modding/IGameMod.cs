namespace Itera.Modding;

/// <summary>
/// Entry point of a mod. The mod loader creates one instance per mod through a public parameterless
/// constructor and calls the lifecycle methods on the main thread.
/// </summary>
/// <remarks>
/// Order of calls: <see cref="OnModLoaded"/> once after the run's engine services are ready (before any match); then,
/// for each match the run plays (offline, hosted, or joined; one run may play several),
/// <see cref="OnMatchStarted"/>, <see cref="OnFixedUpdate"/> on every fixed simulation step and <see cref="OnUpdate"/>
/// on every frame of it, and <see cref="OnMatchEnded"/>; then <see cref="OnGameExit"/> when the game shuts down, then
/// <see cref="OnModUnloaded"/>. An exception thrown from any method is logged and stops that mod's further
/// <see cref="OnUpdate"/>, <see cref="OnFixedUpdate"/>, and <see cref="OnMatchStarted"/>; it still hears
/// <see cref="OnMatchEnded"/>, <see cref="OnGameExit"/>, and <see cref="OnModUnloaded"/>, to let go of what it holds.
/// </remarks>
public interface IGameMod
{
    /// <summary>
    /// Called once after the mod assembly is loaded and the run's engine services exist. A match's services (its world,
    /// ECS, session) do not exist yet: <see cref="OnMatchStarted"/> hands them over.
    /// </summary>
    /// <param name="context">The run's services, manifest data, and a logger for this mod.</param>
    void OnModLoaded(IModContext context);

    /// <summary>
    /// Called when a match starts, once its world and systems exist (as its loading screen begins).
    /// <paramref name="match"/> resolves the match's services, which live until <see cref="OnMatchEnded"/>.
    /// </summary>
    /// <param name="match">The match's services.</param>
    void OnMatchStarted(IServiceProvider match) { }

    /// <summary>Called when the match ends, before its services are freed: drop every reference to them here.</summary>
    void OnMatchEnded() { }

    /// <summary>
    /// Called once per rendered frame of a match, after engine frame systems and before the frame is drawn. It does not run while
    /// no frames are drawn, for example while the window is hidden; <see cref="OnFixedUpdate"/> keeps running then.
    /// </summary>
    /// <param name="time">Frame timing.</param>
    void OnUpdate(in FrameTime time) { }

    /// <summary>Called once per fixed simulation step of a match, after engine fixed systems and physics.</summary>
    /// <param name="time">Fixed step timing.</param>
    void OnFixedUpdate(in FixedStepTime time) { }

    /// <summary>Called when the mod is being unloaded, at game exit or on an explicit unload. Release everything here.</summary>
    void OnModUnloaded();

    /// <summary>Called once when the game is shutting down, before <see cref="OnModUnloaded"/>.</summary>
    void OnGameExit() { }
}
