using System.Globalization;

namespace Yaat.Sim.Authoring;

/// <summary>
/// The scenario editor's one H:M:S parser and formatter for the second counts a scenario holds (spawn delays, preset
/// and trigger offsets, generator windows).
/// </summary>
public static class ScenarioTime
{
    private const int MaxDigitsOnlyLength = 6;

    private const int MaxColonGroupLength = 9;

    /// <summary>
    /// Parses a time an author typed into total seconds.
    /// </summary>
    /// <param name="text">
    /// Blank or null for zero; <c>m:ss</c> or <c>h:m:s</c> (groups read right to left as seconds, minutes, hours); or up
    /// to six bare digits filled right to left in pairs, so <c>130</c> is one minute thirty. Overflowing minutes or
    /// seconds are accepted and normalised (<c>1:90</c> is 150 seconds).
    /// </param>
    /// <returns>The total seconds, or null when the text is not a time (a sign, a decimal, a letter, an empty colon group).</returns>
    public static int? Parse(string? text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return 0;
        }

        return trimmed.Contains(':', StringComparison.Ordinal) ? ParseColonForm(trimmed) : ParseDigitsOnly(trimmed);
    }

    /// <summary>
    /// Formats a second count the way <see cref="Parse"/> reads it back.
    /// </summary>
    /// <param name="seconds">The total seconds; zero or more.</param>
    /// <returns><c>m:ss</c> under an hour, <c>h:mm:ss</c> from an hour on.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> is negative.</exception>
    public static string Format(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        int hours = seconds / 3600;
        int minutes = (seconds % 3600) / 60;
        int secs = seconds % 60;
        return hours == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}");
    }

    private static int? ParseColonForm(string text)
    {
        string[] groups = text.Split(':');
        if ((groups.Length < 2) || (groups.Length > 3))
        {
            return null;
        }

        long total = 0;
        foreach (string group in groups)
        {
            if ((group.Length > MaxColonGroupLength) || !IsAsciiDigits(group))
            {
                return null;
            }

            total = (total * 60) + long.Parse(group, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return total > int.MaxValue ? null : (int)total;
    }

    private static int? ParseDigitsOnly(string text)
    {
        if ((text.Length > MaxDigitsOnlyLength) || !IsAsciiDigits(text))
        {
            return null;
        }

        string padded = text.PadLeft(MaxDigitsOnlyLength, '0');
        int hours = int.Parse(padded.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int minutes = int.Parse(padded.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int seconds = int.Parse(padded.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        return (hours * 3600) + (minutes * 60) + seconds;
    }

    private static bool IsAsciiDigits(string text) => (text.Length > 0) && text.All(char.IsAsciiDigit);
}
