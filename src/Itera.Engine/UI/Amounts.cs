using System.Globalization;

namespace Itera.Engine.UI;

/// <summary>
/// Large amounts as a panel shows them (500, 12.5K, 30M): exact below 1,000; above, at most three significant digits
/// with K, M, B, T, Qa, or Qi, trailing zeros dropped, and rounded down, so a panel never shows more than there is
/// (999,999 is 999K, 1,000,000 is 1M). A console reads them back.
/// </summary>
public static class Amounts
{
    /// <summary>The most characters <see cref="Format"/> writes ("9.22Qi", "-" never).</summary>
    public const int MostCharacters = 8;

    private static readonly string[] s_suffixes = ["", "K", "M", "B", "T", "Qa", "Qi"];

    /// <summary>
    /// Writes <paramref name="amount"/> (0 or more) into <paramref name="destination"/> (at least
    /// <see cref="MostCharacters"/> long) and returns how many characters it wrote. Allocates nothing.
    /// </summary>
    public static int Format(long amount, Span<char> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, MostCharacters);
        if (amount < 1000)
        {
            amount.TryFormat(destination, out var count, provider: CultureInfo.InvariantCulture);
            return count;
        }

        // The unit: the largest power of 1,000 not above the amount.
        var unit = 1;
        long divisor = 1000;
        while (unit < s_suffixes.Length - 1 && amount / divisor >= 1000)
        {
            divisor *= 1000;
            unit++;
        }

        // Three significant digits, rounded down: 1,234 is 123 hundredths of a thousand.
        var whole = amount / divisor;
        var decimals =
            whole >= 100 ? 0
            : whole >= 10 ? 1
            : 2;
        var step =
            divisor
            / (
                decimals == 2 ? 100
                : decimals == 1 ? 10
                : 1
            );
        var digits = amount / step;
        var fraction = (int)(
            digits
            % (
                decimals == 2 ? 100
                : decimals == 1 ? 10
                : 1
            )
        );
        whole.TryFormat(destination, out var written, provider: CultureInfo.InvariantCulture);
        if (decimals > 0 && fraction > 0)
        {
            // Trailing zeros dropped: 1.50K is 1.5K.
            if (decimals == 2 && fraction % 10 == 0)
            {
                fraction /= 10;
                decimals = 1;
            }

            destination[written++] = '.';
            if (decimals == 2 && fraction < 10)
            {
                destination[written++] = '0';
            }

            fraction.TryFormat(destination[written..], out var fractionWritten, provider: CultureInfo.InvariantCulture);
            written += fractionWritten;
        }

        var suffix = s_suffixes[unit];
        suffix.AsSpan().CopyTo(destination[written..]);
        return written + suffix.Length;
    }

    /// <summary><paramref name="amount"/> as <see cref="Format"/> writes it, as a string (the console's answers).</summary>
    public static string Text(long amount)
    {
        Span<char> text = stackalloc char[MostCharacters];
        return new string(text[..Format(amount, text)]);
    }

    /// <summary>
    /// Reads an amount as the console takes it: a whole number (12500), or a number with a suffix (12.5K, 30M, 2B,
    /// ignoring case), rounded down to a whole number. False for anything else, below 0, or past <see cref="long.MaxValue"/>.
    /// </summary>
    public static bool TryParse(string text, out long amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var unit = 0;
        for (var i = s_suffixes.Length - 1; i > 0; i--)
        {
            if (text.EndsWith(s_suffixes[i], StringComparison.OrdinalIgnoreCase))
            {
                unit = i;
                text = text[..^s_suffixes[i].Length];
                break;
            }
        }

        if (
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            || number < 0
        )
        {
            return false;
        }

        try
        {
            for (var i = 0; i < unit; i++)
            {
                number *= 1000;
            }

            var whole = decimal.Floor(number);
            if (whole > long.MaxValue)
            {
                return false;
            }

            amount = (long)whole;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
