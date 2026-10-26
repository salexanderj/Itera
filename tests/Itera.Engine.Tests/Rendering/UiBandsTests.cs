using Itera.Engine.Rendering;

namespace Itera.Engine.Tests.Rendering;

public sealed class UiBandsTests
{
    [Fact]
    public void Bands_are_kept_top_to_bottom()
    {
        var bands = UiBands.One(40, 8).With(0, 4).With(20, 2);

        Assert.Equal(3, bands.Count);
        Assert.Equal(new UiBand(0, 4), bands[0]);
        Assert.Equal(new UiBand(20, 2), bands[1]);
        Assert.Equal(new UiBand(40, 8), bands[2]);
        Assert.Equal(14, bands.Rows);
    }

    /// <summary>Bands that overlap or touch become one, so no two copy regions write the same bytes.</summary>
    [Theory]
    [InlineData(10, 10, 15, 10, 10, 15)]
    [InlineData(10, 10, 20, 5, 10, 15)]
    [InlineData(10, 10, 5, 5, 5, 15)]
    [InlineData(10, 10, 12, 2, 10, 10)]
    public void Bands_that_overlap_or_touch_merge(int start, int rows, int addedStart, int added, int merged, int all)
    {
        var bands = UiBands.One(start, rows).With(addedStart, added);

        Assert.Equal(UiBands.One(merged, all), bands);
    }

    [Fact]
    public void A_band_bridging_two_merges_all_three()
    {
        var bands = UiBands.One(0, 10).With(20, 10).With(5, 20);

        Assert.Equal(UiBands.One(0, 30), bands);
    }

    [Fact]
    public void A_band_without_rows_adds_nothing()
    {
        Assert.Equal(default, UiBands.One(10, 0));
        Assert.Equal(UiBands.One(0, 4), UiBands.One(0, 4).With(20, 0));
    }

    [Fact]
    public void A_fifth_band_apart_is_refused()
    {
        var bands = UiBands.One(0, 1).With(10, 1).With(20, 1).With(30, 1);

        Assert.Throws<InvalidOperationException>(() => bands.With(40, 1));
    }
}
