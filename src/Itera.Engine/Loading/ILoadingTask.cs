namespace Itera.Engine.Loading;

/// <summary>
/// Work the loading screen waits for (<see cref="LoadingScreen"/>): the engine's sounds, world, navigation, and edit
/// warm-up, and whatever the game registers (a DI service of this type). <see cref="Progress"/> is read once per
/// main-loop iteration on the main thread while loading, after the iteration's fixed steps; it must not allocate. A
/// task that cannot finish in this game (no device, a session client) reports done.
/// </summary>
public interface ILoadingTask
{
    /// <summary>What the loading screen calls it: one or two short words.</summary>
    string Name { get; }

    /// <summary>How far it is now.</summary>
    LoadingProgress Progress { get; }
}

/// <summary>
/// A loading task's state: done or not, and how much is done, 0 to 1, or below 0 when it cannot tell.
/// </summary>
public readonly record struct LoadingProgress(bool Done, float Fraction)
{
    /// <summary>Done.</summary>
    public static LoadingProgress Finished => new(true, 1);

    /// <summary>Not done, with no measure of how far.</summary>
    public static LoadingProgress Waiting => new(false, -1);

    /// <summary>Done when <paramref name="done"/>, with no measure of how far otherwise.</summary>
    public static LoadingProgress When(bool done) => done ? Finished : Waiting;

    /// <summary>Done when <paramref name="done"/>, otherwise <paramref name="fraction"/> of the way.</summary>
    public static LoadingProgress Part(bool done, float fraction) =>
        done ? Finished : new(false, Math.Clamp(fraction, 0, 0.99f));
}
