using Microsoft.Extensions.Options;

namespace Itera.Engine.Saves;

/// <summary>One save in the data folder's <c>Saves</c>: its name (what a match starts from), when it was last written, and its size.</summary>
/// <param name="Name">The save's name, without <c>.save</c>.</param>
/// <param name="LastWrittenUtc">When the file was last written, UTC.</param>
/// <param name="Bytes">The file's size.</param>
public sealed record SaveFile(string Name, DateTime LastWrittenUtc, long Bytes);

/// <summary>
/// The saves a menu can offer: each <c>NAME.save</c> in the data folder's <c>Saves</c> (<see cref="SaveSettings.Folder"/>)
/// whose name a match can start from (<see cref="SaveSettings.Name"/>'s rule), newest first. A match starts from one with
/// <c>MatchCommands.OfflineStart(saveName: ...)</c> or <c>HostStart</c>, and writes it at its end. Whether this game and
/// version can read a save shows only when a match starts from it (the reason is the match's end). Run-wide; main thread.
/// </summary>
public sealed class SaveFiles
{
    private const string Extension = ".save";

    public SaveFiles(IOptions<EngineOptions> options)
        : this(SaveSettings.Folder(options.Value.Application)) { }

    /// <param name="folder">The folder that holds the saves.</param>
    internal SaveFiles(string folder) => Folder = folder;

    /// <summary>The folder that holds the saves.</summary>
    public string Folder { get; }

    /// <summary>
    /// The saves in <see cref="Folder"/>, newest first, by name where two were written at once; none when the folder does
    /// not exist. Reads the folder each call and allocates: call it when a page opens, not every frame.
    /// </summary>
    public IReadOnlyList<SaveFile> List()
    {
        var folder = new DirectoryInfo(Folder);
        if (!folder.Exists)
        {
            return [];
        }

        var saves = new List<SaveFile>();
        // The platform's case rule matches the extension, as it opens the file: "a.SAVE" is a's save on Windows and macOS.
        foreach (var file in folder.EnumerateFiles("*" + Extension))
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);
            if (!EngineOptionsValidator.IsSaveName(name))
            {
                continue;
            }

            try
            {
                // A link counts by what it leads to; one to nothing, or round in a loop, is no save to offer.
                var target = file.LinkTarget is null ? file : file.ResolveLinkTarget(returnFinalTarget: true);
                if (target is FileInfo { Exists: true } read)
                {
                    saves.Add(new SaveFile(name, read.LastWriteTimeUtc, read.Length));
                }
            }
            catch (IOException)
            {
                // Gone since the folder was read: not a save to offer.
            }
        }

        saves.Sort(
            static (a, b) =>
                b.LastWrittenUtc != a.LastWrittenUtc
                    ? b.LastWrittenUtc.CompareTo(a.LastWrittenUtc)
                    : string.CompareOrdinal(a.Name, b.Name)
        );
        return saves;
    }
}
