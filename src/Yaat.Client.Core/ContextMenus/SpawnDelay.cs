using System.Text.RegularExpressions;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The spawn delays a delayed aircraft is offered: the presets the Change spawn delay submenu lists, and the parser
/// behind its free-text box, which reads a bare number of seconds or hours, minutes and seconds with unit suffixes
/// (<c>90</c>, <c>2m15s</c>, <c>1h</c>).
/// </summary>
public static class SpawnDelay
{
    /// <summary>The delay presets, each with the seconds it sends.</summary>
    public static readonly IReadOnlyList<(string Label, int Seconds)> Presets =
    [
        ("15 seconds", 15),
        ("30 seconds", 30),
        ("1 minute", 60),
        ("2 minutes", 120),
        ("5 minutes", 300),
        ("10 minutes", 600),
    ];

    private static readonly Regex DelayUnitsPattern = new(
        @"^\s*(?:(?<h>\d+)\s*h)?\s*(?:(?<m>\d+)\s*m)?\s*(?:(?<s>\d+)\s*s)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>
    /// The seconds <paramref name="input"/> names, or null when it names no delay: blank text, a bare number of
    /// seconds, or hours, minutes and seconds with unit suffixes, which accumulate.
    /// </summary>
    public static int? ParseDelayInput(string? input)
    {
        string? trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int bareSeconds))
        {
            return bareSeconds;
        }

        Match match = DelayUnitsPattern.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        Group hours = match.Groups["h"];
        Group minutes = match.Groups["m"];
        Group secs = match.Groups["s"];
        if (!hours.Success && !minutes.Success && !secs.Success)
        {
            return null;
        }

        long total = 0;
        if (hours.Success)
        {
            total += long.Parse(hours.Value, System.Globalization.CultureInfo.InvariantCulture) * 3600L;
        }
        if (minutes.Success)
        {
            total += long.Parse(minutes.Value, System.Globalization.CultureInfo.InvariantCulture) * 60L;
        }
        if (secs.Success)
        {
            total += long.Parse(secs.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (total < 0 || total > int.MaxValue)
        {
            return null;
        }
        return (int)total;
    }
}
