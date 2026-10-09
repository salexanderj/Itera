namespace Itera.Engine.Commands;

/// <summary>Where a console command may run, by the game's session role.</summary>
public enum CommandScope
{
    /// <summary>Anywhere: the command changes only this game (the debug lines, the fly camera).</summary>
    Local = 0,

    /// <summary>Offline or on a host: the command changes what the session shares (figures, loose bodies).</summary>
    Match = 1,

    /// <summary>
    /// Offline only: the command changes rules a host checks its clients against, which they received once, when they
    /// joined (the player rules).
    /// </summary>
    Offline = 2,
}
