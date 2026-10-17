namespace Itera.Engine.Saves;

/// <summary>
/// A part of a game's own state kept in its match's named save (save format 10; sections since format 8): a scoped
/// service the game registers in the match's scope, one for each name. On a host or offline game with a save (<c>Engine:Save:Name</c>), at the match's
/// "save sections" step (after the systems' <c>Initialize</c>, before the first step), each section the save holds is
/// handed to the one of its name (<see cref="Restore"/>); and at each save (an autosave at a step's end, the console's
/// <c>save</c> between steps, and the last at a clean end, before the ECS world goes) every section writes its state as
/// it is (<see cref="Capture"/>). <see cref="SaveService.StartedFromSave"/> tells a system that the match resumes a save,
/// so it may leave out what it would make at a new match's start. Main thread, outside any query. A client neither saves
/// nor restores.
/// </summary>
public interface ISaveSection
{
    /// <summary>
    /// Its name in the save: 1 to 64 letters, digits, '.', '-', or '_', and no other section's (a game's own start
    /// with a word of the game's, as the starter's <c>game.</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The version of the layout <see cref="Capture"/> writes, from 1. A save holding a higher one does not start (a newer
    /// build wrote it); one holding this or a lower one is restored.
    /// </summary>
    int Version { get; }

    /// <summary>Writes the state as it is now. It runs within a step's time, so it copies and does little else.</summary>
    void Capture(BinaryWriter writer);

    /// <summary>
    /// Reads the state the save held, written by <see cref="Capture"/> at <paramref name="version"/> (this
    /// <see cref="Version"/> or an older one), and puts it back into the match. Throws <see cref="InvalidDataException"/>
    /// (or lets <see cref="EndOfStreamException"/> through) when the bytes are wrong: the match does not start, and the
    /// save is left as it is.
    /// </summary>
    void Restore(BinaryReader reader, int version);
}
