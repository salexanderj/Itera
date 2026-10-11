using Itera.Engine.Saves;

namespace Itera.Engine.Tests.Saves;

/// <summary>
/// <see cref="SaveFiles"/>: the saves a menu can offer, newest first, only <c>NAME.save</c> files whose name a match can
/// start from, and none when the folder does not exist.
/// </summary>
public sealed class SaveFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "itera-save-files-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Saves_are_listed_newest_first_with_their_size()
    {
        Directory.CreateDirectory(_folder);
        Write("castle", bytes: 10, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        Write("my farm", bytes: 25, new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc));
        Write("b", bytes: 1, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));

        var saves = new SaveFiles(_folder).List();

        Assert.Equal(["my farm", "b", "castle"], saves.Select(save => save.Name));
        Assert.Equal(25, saves[0].Bytes);
        Assert.Equal(new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc), saves[0].LastWrittenUtc);
    }

    [Fact]
    public void Files_no_match_could_start_from_are_left_out()
    {
        Directory.CreateDirectory(_folder);
        var when = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Write("kept", 1, when);
        Write(" leading space", 1, when);
        Write("bad!name", 1, when);
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_folder, "kept.save.tmp"), "x");
        Directory.CreateDirectory(Path.Combine(_folder, "folder.save"));

        Assert.Equal(["kept"], new SaveFiles(_folder).List().Select(save => save.Name));
    }

    [Fact]
    public void A_link_to_nothing_is_left_out_and_the_rest_still_listed()
    {
        Directory.CreateDirectory(_folder);
        Write("kept", 1, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        try
        {
            File.CreateSymbolicLink(Path.Combine(_folder, "dangling.save"), Path.Combine(_folder, "missing"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A platform or account that cannot make links: nothing to check.
            return;
        }

        Assert.Equal(["kept"], new SaveFiles(_folder).List().Select(save => save.Name));
    }

    [Fact]
    public void No_folder_lists_no_saves() => Assert.Empty(new SaveFiles(_folder).List());

    private void Write(string name, int bytes, DateTime written)
    {
        var path = Path.Combine(_folder, name + ".save");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, written);
    }
}
