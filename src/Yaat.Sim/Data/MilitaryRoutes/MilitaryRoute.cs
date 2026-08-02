namespace Yaat.Sim.Data.MilitaryRoutes;

/// <summary>The protected half-widths over one span of a route, per FAA Order JO 7110.65 §9-2-6.d.</summary>
/// <param name="FromPoint">Span start label, or null when the clause covers the whole route.</param>
/// <param name="ToPoint">Span end label, or null when the clause covers the whole route.</param>
/// <param name="LeftNm">Protected distance left of centerline, in nautical miles.</param>
/// <param name="RightNm">Protected distance right of centerline, in nautical miles.</param>
public sealed record MilitaryRouteWidthSpan(string? FromPoint, string? ToPoint, double LeftNm, double RightNm);

/// <summary>
/// One published direction of an aerial refueling track or anchor.
///
/// The two directions of a track are <b>not</b> the same line flown backwards: opposing refueling
/// tracks are laterally offset so the traffic is separated, and only 33 of the 82 two-direction
/// entries in AP/1B 2607 are exact reversals. AR4A's southbound ARIP sits 50 NM from its northbound
/// exit. Each direction therefore carries its own geometry, and the clearance picks the one the
/// aircraft is actually positioned to fly.
/// </summary>
public sealed record MilitaryRouteVariant
{
    /// <summary>"North", "South", "East", "West", or empty when only one direction is published.</summary>
    public required string Direction { get; init; }

    /// <summary>Ordered points in the direction of flight: ARIP, ARCP, check points, exit.</summary>
    public required IReadOnlyList<MilitaryRoutePoint> Points { get; init; }

    /// <summary>An anchor's orbit corners, in the order printed. Empty for a track.</summary>
    public IReadOnlyList<MilitaryRoutePoint> Pattern { get; init; } = [];

    public IReadOnlyList<string> EntryPoints { get; init; } = [];

    public IReadOnlyList<string> ExitPoints { get; init; } = [];
}

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

    /// <summary>Which chapter 5 table this came from; <see cref="MilitaryRouteArKind.None"/> for IR/VR/SR.</summary>
    public MilitaryRouteArKind ArKind { get; init; } = MilitaryRouteArKind.None;

    /// <summary>
    /// Every published direction. Empty for IR/VR/SR, whose single direction is <see cref="Points"/>.
    /// For an AR entry <see cref="Points"/> is the first variant's points, so route expansion and the
    /// airway shadow registration keep working without either learning about directions.
    /// </summary>
    public IReadOnlyList<MilitaryRouteVariant> Variants { get; init; } = [];

    /// <summary>
    /// Chapter 5 publishes one altitude block for the whole refueling entry rather than chapters
    /// 2-4's per-segment blocks, so the block lives on the route instead of on each point.
    /// </summary>
    public MilitaryRouteAltitude RouteAltitude { get; init; } = MilitaryRouteAltitude.None;

    /// <summary>An anchor's ATC Assigned Airspace polygon, or empty when it publishes none.</summary>
    public IReadOnlyList<LatLon> AtcAssignedAirspace { get; init; } = [];

    /// <summary>True for an AP/1B chapter 5 aerial refueling track or anchor.</summary>
    public bool IsAerialRefueling => ArKind != MilitaryRouteArKind.None;

    /// <summary>Every point of every published direction, including anchor orbit corners.</summary>
    public IEnumerable<MilitaryRoutePoint> AllPoints => Variants.Count == 0 ? Points : Variants.SelectMany(v => v.Points.Concat(v.Pattern));

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
