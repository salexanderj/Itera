using Itera.Engine.UI;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="Amounts"/>: exact below 1,000; above, three significant digits with a suffix, trailing zeros dropped,
/// rounded down; the console reads whole numbers and suffixed ones back, refusing the rest; writing allocates nothing.
/// </summary>
public sealed class AmountsTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    [InlineData(500, "500")]
    [InlineData(999, "999")]
    [InlineData(1000, "1K")]
    [InlineData(1050, "1.05K")]
    [InlineData(1234, "1.23K")]
    [InlineData(1500, "1.5K")]
    [InlineData(1999, "1.99K")]
    [InlineData(12500, "12.5K")]
    [InlineData(125000, "125K")]
    [InlineData(999999, "999K")]
    [InlineData(1000000, "1M")]
    [InlineData(30000000, "30M")]
    [InlineData(30412118, "30.4M")]
    [InlineData(2000000000, "2B")]
    [InlineData(1000000000000, "1T")]
    [InlineData(1000000000000000, "1Qa")]
    [InlineData(long.MaxValue, "9.22Qi")]
    public void Amounts_read_as_the_panel_shows_them(long amount, string shown)
    {
        Assert.Equal(shown, Amounts.Text(amount));
    }

    [Theory]
    [InlineData("12500", 12500)]
    [InlineData("12.5K", 12500)]
    [InlineData("12.5k", 12500)]
    [InlineData("30M", 30000000)]
    [InlineData("2b", 2000000000)]
    [InlineData("1.0000001K", 1000)]
    [InlineData("0", 0)]
    public void The_console_reads_amounts(string text, long amount)
    {
        Assert.True(Amounts.TryParse(text, out var read));
        Assert.Equal(amount, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("K")]
    [InlineData("12.5X")]
    [InlineData("1e3")]
    [InlineData("10Qi")]
    public void The_console_refuses_what_is_no_amount(string text)
    {
        Assert.False(Amounts.TryParse(text, out _));
    }

    [Fact]
    public void Writing_an_amount_allocates_nothing()
    {
        Span<char> text = stackalloc char[Amounts.MostCharacters];
        Amounts.Format(123456789, text);
        var before = SystemAllocation.StartMeasurement();
        var written = 0;
        for (long amount = 1; amount < long.MaxValue / 7; amount *= 7)
        {
            written += Amounts.Format(amount, text);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(written > 0);
    }
}
