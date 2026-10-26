namespace Itera.Engine.Rendering;

/// <summary><paramref name="Rows"/> whole rows of the UI from row <paramref name="RowStart"/>.</summary>
public readonly record struct UiBand(int RowStart, int Rows)
{
    /// <summary>The row after the band.</summary>
    public int RowEnd => RowStart + Rows;
}

/// <summary>
/// Up to <see cref="MostBands"/> bands of rows of the UI to copy to the GPU, top to bottom, none overlapping or touching
/// another, so their copies never write the same bytes. A value: nothing allocates.
/// </summary>
public readonly record struct UiBands
{
    /// <summary>The most bands a frame copies.</summary>
    public const int MostBands = 4;

    private readonly UiBand _band0;
    private readonly UiBand _band1;
    private readonly UiBand _band2;
    private readonly UiBand _band3;

    /// <summary>The bands held, 0 when no row changed.</summary>
    public int Count { get; }

    /// <summary>The rows of every band.</summary>
    public int Rows => _band0.Rows + _band1.Rows + _band2.Rows + _band3.Rows;

    /// <summary>The band at <paramref name="index"/>, from the top.</summary>
    public UiBand this[int index] =>
        (uint)index >= (uint)Count ? throw new ArgumentOutOfRangeException(nameof(index), index, "No such band.")
        : index == 0 ? _band0
        : index == 1 ? _band1
        : index == 2 ? _band2
        : _band3;

    /// <summary>One band of <paramref name="rows"/> rows from <paramref name="rowStart"/>; none when it has no rows.</summary>
    public static UiBands One(int rowStart, int rows) => default(UiBands).With(rowStart, rows);

    /// <summary>
    /// These bands with <paramref name="rows"/> rows from <paramref name="rowStart"/> added, merged with every band it
    /// overlaps or touches. Throws when the bands would then number more than <see cref="MostBands"/>.
    /// </summary>
    public UiBands With(int rowStart, int rows)
    {
        if (rows <= 0)
        {
            return this;
        }

        var added = new UiBand(rowStart, rows);
        Span<UiBand> bands = stackalloc UiBand[MostBands + 1];
        var count = 0;
        var placed = false;
        for (var i = 0; i < Count; i++)
        {
            var band = this[i];
            if (!placed && added.RowStart <= band.RowStart)
            {
                count = Append(bands, count, added);
                placed = true;
            }

            count = Append(bands, count, band);
        }

        if (!placed)
        {
            count = Append(bands, count, added);
        }

        if (count > MostBands)
        {
            throw new InvalidOperationException($"The UI copies at most {MostBands} bands of rows a frame.");
        }

        return new UiBands(bands[..count]);
    }

    private UiBands(ReadOnlySpan<UiBand> bands)
    {
        Count = bands.Length;
        _band0 = bands.Length > 0 ? bands[0] : default;
        _band1 = bands.Length > 1 ? bands[1] : default;
        _band2 = bands.Length > 2 ? bands[2] : default;
        _band3 = bands.Length > 3 ? bands[3] : default;
    }

    /// <summary>
    /// Appends <paramref name="band"/>, which starts at or below the last band's start, to the first
    /// <paramref name="count"/> of <paramref name="bands"/>, merged with the last band when they overlap or touch.
    /// </summary>
    private static int Append(Span<UiBand> bands, int count, UiBand band)
    {
        if (count > 0 && band.RowStart <= bands[count - 1].RowEnd)
        {
            var last = bands[count - 1];
            var end = Math.Max(last.RowEnd, band.RowEnd);
            bands[count - 1] = new UiBand(last.RowStart, end - last.RowStart);
            return count;
        }

        bands[count] = band;
        return count + 1;
    }
}
