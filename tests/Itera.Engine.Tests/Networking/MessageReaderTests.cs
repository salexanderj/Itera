using Itera.Engine.Networking;

namespace Itera.Engine.Tests.Networking;

/// <summary>Variable-length ints as messages carry them: written and read back, and refused when malformed.</summary>
public sealed class MessageReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(300_000)]
    [InlineData(int.MaxValue)]
    public void A_written_number_reads_back(int value)
    {
        var writer = new MessageWriter();
        writer.WriteVarInt(value);

        var reader = new MessageReader(writer.Written);

        Assert.Equal(value, reader.ReadVarInt());
        reader.EnsureEnd();
    }

    /// <summary>
    /// A fifth byte above 0x07 holds bits past an int's 31: they were shifted out and lost, so 0x10 in the fifth byte
    /// read as 0. Such a number, a negative one, and one that never ends are refused.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x08 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]
    public void A_number_past_an_int_is_refused(byte[] bytes) =>
        Assert.Throws<InvalidDataException>(() => new MessageReader(bytes).ReadVarInt());
}
