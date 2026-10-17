using Itera.Engine.Diagnostics;
using Itera.Engine.Networking;

namespace Itera.Engine.Tests.Diagnostics;

/// <summary>
/// Text another game sent, made safe to log: a line break, an escape, or a mark that turns the text's direction could
/// forge a line of the log or hide what it says, so each becomes '?'; other text, a name in any script with its emoji
/// among it, stays as it was, the same string.
/// </summary>
public sealed class LogTextTests
{
    [Theory]
    [InlineData("left\n[ERR] forged", "left?[ERR] forged")]
    [InlineData("a\r\nb", "a??b")]
    [InlineData("\u001b[31mred", "?[31mred")]
    [InlineData("tab\there", "tab?here")]
    [InlineData("del\u007f c1\u0085", "del? c1?")]
    [InlineData("line\u2028para\u2029", "line?para?")]
    [InlineData("evil\u202Etxt.exe", "evil?txt.exe")]
    [InlineData("\u2066iso\u2069 \u200Emark\u200F \u061C", "?iso? ?mark? ?")]
    public void Characters_that_could_forge_or_hide_a_line_become_question_marks(string text, string printable) =>
        Assert.Equal(printable, LogText.Printable(text));

    [Theory]
    [InlineData("")]
    [InlineData("Player")]
    [InlineData("Ünïcødé 名前 🙂")]
    public void Plain_text_stays_the_same_string(string text) => Assert.Same(text, LogText.Printable(text));

    [Fact]
    public void A_disconnects_text_and_a_name_are_read_printable()
    {
        var writer = new MessageWriter();
        writer.WriteByte((byte)DisconnectKind.Bye);
        writer.WriteByte(0);
        writer.WriteString("bye\n[CRT] the host crashed");

        Assert.True(SessionProtocol.TryReadDisconnect(writer.Written, out _, out _, out var text));
        Assert.Equal("bye?[CRT] the host crashed", text);
        Assert.Equal("Eve?[INF] admin joined", SessionProtocol.CutName("Eve\n[INF] admin joined"));
        Assert.Equal("why?", SessionProtocol.Cut("why\r", 64));
    }
}
