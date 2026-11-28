using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Strips;

/// <summary>
/// One annotation box of a departure strip, in the three spellings it has: the parser's canonical box
/// (<c>"1"</c>..<c>"9"</c>, <c>"8a"</c>, <c>"8b"</c>, what <c>AN</c> parses to), the spelling the editor writes
/// (<c>10</c>..<c>18</c>, <c>8a</c>, <c>8b</c>) and the label it shows. The set is closed: every box is one of
/// <see cref="All"/>.
/// </summary>
public sealed class StripBox
{
    private static readonly ILogger Log = SimLog.CreateLogger("StripBox");

    private StripBox(string canonical, string written)
    {
        Canonical = canonical;
        Written = written;
        Label = $"Box {written}";
    }

    /// <summary>The box as <c>AN</c> parses it: <c>"1"</c>..<c>"9"</c>, <c>"8a"</c> or <c>"8b"</c>.</summary>
    public string Canonical { get; }

    /// <summary>The box as the editor writes it: <c>10</c>..<c>18</c>, <c>8a</c> or <c>8b</c>.</summary>
    public string Written { get; }

    /// <summary>The box's label.</summary>
    public string Label { get; }

    /// <summary>Every box, 8a and 8b first, then 10 to 18.</summary>
    public static IReadOnlyList<StripBox> All { get; } =
    [
        new("8a", "8a"),
        new("8b", "8b"),
        .. Enumerable
            .Range(1, 9)
            .Select(n => new StripBox(
                n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (n + 9).ToString(System.Globalization.CultureInfo.InvariantCulture)
            )),
    ];

    /// <summary>The box a parsed <c>AN</c> record names, or null for a box outside the set.</summary>
    public static StripBox? FromCanonical(string canonical)
    {
        StripBox? box = All.FirstOrDefault(b => b.Canonical.Equals(canonical, StringComparison.OrdinalIgnoreCase));
        if (box is null)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("No strip box for canonical {Box}", canonical);
            }
        }

        return box;
    }
}
