namespace Yaat.Sim.Data.MilitaryRoutes;

/// <summary>The protected half-widths over one span of a route, per FAA Order JO 7110.65 §9-2-6.d.</summary>
/// <param name="FromPoint">Span start label, or null when the clause covers the whole route.</param>
/// <param name="ToPoint">Span end label, or null when the clause covers the whole route.</param>
/// <param name="LeftNm">Protected distance left of centerline, in nautical miles.</param>
/// <param name="RightNm">Protected distance right of centerline, in nautical miles.</param>
public sealed record MilitaryRouteWidthSpan(string? FromPoint, string? ToPoint, double LeftNm, double RightNm);

/// <summary>
/// One published military training route or aerial refueling track from DoD AP/1B.
///
/// Routes are strictly <b>one-way</b> and course reversals are prohibited (AP/1B chapter 1 §V.B.1),
/// so <see cref="Points"/> order is the direction of flight and must never be walked backwards. This
/// is the key difference from a civil airway, whose fix list
/// <see cref="NavigationDatabase.ExpandAirwaySegment"/> walks in either direction.
/// </summary>
public sealed record MilitaryRoute
{
    /// <summary>Designator without the hyphen, as written in a flight plan: <c>IR149</c>, <c>VR1257</c>.</summary>
    public required string Designator { get; init; }

    /// <summary>Designator as printed in AP/1B, with the hyphen: <c>IR-149</c>.</summary>
    public required string Printed { get; init; }

    public required MilitaryRouteType Type { get; init; }

    /// <summary>Ordered points in the direction of flight.</summary>
    public required IReadOnlyList<MilitaryRoutePoint> Points { get; init; }

    public IReadOnlyList<MilitaryRouteWidthSpan> Widths { get; init; } = [];

    /// <summary>Primary entry point first, then any alternates.</summary>
    public IReadOnlyList<string> EntryPoints { get; init; } = [];

    /// <summary>Primary exit point first, then any alternates.</summary>
    public IReadOnlyList<string> ExitPoints { get; init; } = [];

    public bool TerrainFollowing { get; init; }

    public string OriginatingActivity { get; init; } = string.Empty;

    public string SchedulingActivity { get; init; } = string.Empty;

    /// <summary>Published hours of operation, verbatim (e.g. "Continuous", "0700-2200 LCL").</summary>
    public string Hours { get; init; } = string.Empty;

    /// <summary>
    /// True when the route has a segment above 1500 ft AGL. AP/1B chapter 1 §II assigns three-digit
    /// designators to those routes and four-digit designators to routes flown entirely at or below
    /// 1500 ft AGL. The rule does not apply to SR routes, whose digit count carries no such meaning.
    /// </summary>
    public bool HasSegmentsAboveFifteenHundredAgl => Type != MilitaryRouteType.Sr && Designator.Length - PrefixLength == 3;

    private const int PrefixLength = 2;

    /// <summary>The index of a point by its AP/1B label, or -1.</summary>
    public int IndexOf(string pointId)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            if (string.Equals(Points[i].Id, pointId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The protected half-widths at a point, falling back to the widest published span when no clause
    /// names it. AP/1B width clauses do not always cover every span, and under-reporting protected
    /// airspace would be the less safe error.
    /// </summary>
    public MilitaryRouteWidthSpan? WidthAt(string pointId)
    {
        int index = IndexOf(pointId);
        if (index < 0 || Widths.Count == 0)
        {
            return null;
        }

        foreach (var span in Widths)
        {
            if (span.FromPoint is null || span.ToPoint is null)
            {
                continue;
            }

            int from = IndexOf(span.FromPoint);
            int to = IndexOf(span.ToPoint);
            if (from >= 0 && to >= 0 && index >= Math.Min(from, to) && index <= Math.Max(from, to))
            {
                return span;
            }
        }

        return Widths.MaxBy(w => Math.Max(w.LeftNm, w.RightNm));
    }
}
