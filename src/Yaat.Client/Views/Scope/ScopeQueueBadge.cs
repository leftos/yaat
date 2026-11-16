using SkiaSharp;

namespace Yaat.Client.Views.Scope;

/// <summary>One track's queued-commands badge as the frame lays it out.</summary>
/// <param name="Count">How many pending lines the aircraft has; the badge draws it.</param>
/// <param name="Lines">The pending lines, verbatim and in order; a hovered badge draws them.</param>
/// <param name="Rect">The count's text box in screen points; the hover hit-test reads it inflated.</param>
/// <param name="Hovered">Whether the pointer is on the badge, so its lines draw.</param>
public sealed record ScopeQueueBadgeView(int Count, IReadOnlyList<string> Lines, SKRect Rect, bool Hovered);

/// <summary>
/// The pilot lens's queued-commands badge: reads an aircraft's <c>PendingCommands</c>, whose lines are <c>[Active] …</c> for
/// the executing block and <c>[n] …</c> for each command still waiting on its trigger or deferral.
/// </summary>
public static class ScopeQueueBadge
{
    /// <summary>How far the badge's text box sits left of the symbol (its right edge) and below it (its top).</summary>
    public const float SymbolGapPx = 6f;

    private const string ActivePrefix = "[Active]";

    /// <summary>
    /// The pending lines of <paramref name="pendingCommands"/>, verbatim and in order with their <c>[n] </c> prefix: every
    /// non-empty line, split on line feeds with a trailing carriage return trimmed, that does not start <c>[Active]</c>.
    /// Empty when there is none.
    /// </summary>
    public static IReadOnlyList<string> PendingLines(string? pendingCommands)
    {
        if (string.IsNullOrEmpty(pendingCommands))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (string raw in pendingCommands.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if ((line.Length > 0) && !line.StartsWith(ActivePrefix, StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
