using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Networking;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// Variable-length ints as messages carry them: written and read back, and refused when malformed; and positions:
/// absolute to 1/64 cm at any distance, the same point for peers whose origins differ, refused when a message cannot
/// carry them, and any twelve bytes read as one.
/// </summary>
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

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(150_000.123456, -51.2, -149_999.987654)]
    [InlineData(-335_000, 335_000, 1e-4)]
    public void A_position_reads_back_within_a_64th_of_a_centimetre(double x, double y, double z)
    {
        var writer = new MessageWriter();
        writer.WritePosition(new WorldPoint(x, y, z));

        var reader = new MessageReader(writer.Written);
        var read = reader.ReadPosition();
        reader.EnsureEnd();

        Assert.Equal(12, writer.Length);
        var step = 1.0 / SessionProtocol.PositionStepsPerMetre;
        Assert.Equal(x, read.X, step / 2);
        Assert.Equal(y, read.Y, step / 2);
        Assert.Equal(z, read.Z, step / 2);
    }

    /// <summary>A peer writes a local position from its origin; one whose origin is 150 km away reads the same place.</summary>
    [Fact]
    public void Peers_with_other_origins_read_the_same_place()
    {
        var near = new WorldOrigin();
        near.StartAt(new WorldPoint(149_600, 30, -2_000));
        var far = new WorldOrigin();
        far.StartAt(new WorldPoint(-200, 0, 300));
        var writer = new MessageWriter();
        writer.WriteLocalPosition(near, new Vector3(12.5f, -3.25f, 0.75f));

        var reader = new MessageReader(writer.Written);
        var local = reader.ReadLocalPosition(far);

        Assert.Equal(new WorldPoint(149_612.5, 26.75, -1_999.25), far.ToWorld(local));
        Assert.Equal(new Vector3(149_812.5f, 26.75f, -2_299.25f), local);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(335_545)]
    [InlineData(-335_545)]
    public void A_position_a_message_cannot_carry_is_refused(double value)
    {
        var writer = new MessageWriter();

        Assert.Throws<ArgumentException>(() => writer.WritePosition(new WorldPoint(1, value, 1)));
        Assert.Throws<ArgumentException>(() => writer.WritePosition(new WorldPoint(value, value, value)));
    }

    /// <summary>
    /// A peer's twelve bytes read as a finite point within reach, or are refused where an axis is int.MinValue, 1/64 cm
    /// past it; fewer throw.
    /// </summary>
    [Fact]
    public void Any_twelve_bytes_read_as_a_finite_position_within_reach_or_are_refused()
    {
        var sample = new MessageWriter();
        sample.WritePosition(new WorldPoint(1, 2, 3));

        foreach (var (label, body) in HostileValues.Damaged("position", sample.Written.ToArray()))
        {
            var axes =
                body.Length < 12 ? [] : Enumerable.Range(0, 3).Select(axis => BitConverter.ToInt32(body, 4 * axis));
            if (body.Length < 12 || axes.Contains(int.MinValue))
            {
                Assert.Throws<InvalidDataException>(() => new MessageReader(body).ReadPosition());
                continue;
            }

            var read = new MessageReader(body).ReadPosition();
            Assert.True(read.IsFinite, label);
            Assert.True(
                Math.Max(Math.Abs(read.X), Math.Max(Math.Abs(read.Y), Math.Abs(read.Z)))
                    <= SessionProtocol.MaxPositionMetres + 1e-9,
                label
            );
        }
    }
}
