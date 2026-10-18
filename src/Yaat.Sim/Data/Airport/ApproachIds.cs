using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Approach ids that name no published procedure. A visual approach is <c>VIS{runway}</c> (<c>VIS28R</c>): the id an
/// active visual clearance carries, the one an expected visual approach (<c>EAPP VIS28R</c>) is stored as, and the one the
/// pilot speaks as "visual approach runway". Every reader of an approach id that otherwise resolves it against CIFP asks
/// <see cref="TryParseVisual"/> first.
/// </summary>
public static partial class ApproachIds
{
    private const string VisualPrefix = "VIS";

    /// <summary>The visual approach id for <paramref name="runwayDesignator"/> (<c>28R</c> → <c>VIS28R</c>).</summary>
    public static string Visual(string runwayDesignator) => VisualPrefix + runwayDesignator;

    /// <summary>
    /// True when <paramref name="approachId"/> is a visual approach id, with <paramref name="runwayId"/> the runway it names
    /// as written, upper-cased (<c>VIS28R</c> → <c>28R</c>, <c>VIS1R</c> → <c>1R</c>).
    /// </summary>
    public static bool TryParseVisual(string? approachId, [NotNullWhen(true)] out string? runwayId)
    {
        Match match = approachId is null ? Match.Empty : VisualRegex().Match(approachId);
        runwayId = match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        return runwayId is not null;
    }

    /// <summary>
    /// The system line for an expected approach: "Expecting visual approach runway 28R" for a visual, else "Expecting I28R
    /// approach".
    /// </summary>
    public static string ExpectingText(string approachId) =>
        TryParseVisual(approachId, out string? runwayId)
            ? $"Expecting visual approach runway {RunwayIdentifier.ToDisplayDesignator(runwayId)}"
            : $"Expecting {approachId} approach";

    [GeneratedRegex(@"^VIS(\d{1,2}[LRC]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex VisualRegex();
}
