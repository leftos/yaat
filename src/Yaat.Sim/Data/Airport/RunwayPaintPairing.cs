using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A painted hold bar a runway crossing can pair with (<see cref="RunwayPaintPairing.Candidates"/>): the bar's index among
/// the videomap's segments (file order), the snap point where the bar (extended at most
/// <see cref="RunwayPaintPairing.BarExtensionFt"/> at either end) meets the crossing's own taxiway centerline, D_paint (that
/// point's distance from the runway centerline, feet), the bar's near end (the smaller of its two drawn ends' distances
/// from the runway centerline, feet) and how far along the taxiway walk from the runway the snap point lies, feet.
/// </summary>
internal readonly record struct PaintPair(int SegmentIndex, LatLon SnapPoint, double PaintFt, double NearEndFt, double WalkFt);

/// <summary>What crossings pair against: the videomap's two-point segments and every runway's centerline as drawn in the layout.</summary>
internal readonly record struct PaintSource(IReadOnlyList<VideoMapSegment> Segments, IReadOnlyList<IReadOnlyList<LatLon>> RunwayCenterlines);

/// <summary>
/// Pairs a runway crossing's generated hold-short point with the painted hold bar the tower-cab videomap draws on its
/// taxiway, without changing the layout. A candidate bar crosses no runway centerline, does not lie along a taxiway or
/// runway centerline, crosses the crossing's own taxiway centerline (walked outward from the boundary edge while the
/// taxiway keeps its name) within 30° of perpendicular to it or within 15° of parallel to the runway, lies on the
/// crossing's side of the runway, and puts its snap point within the band D_rule − 60 ft … D_rule + 100 ft and at least
/// runway half-width + 50 ft from the centerline. Candidates rank innermost first: the first bar an aircraft leaving the
/// runway crosses along the taxiway (AIM 2-3-5.a.1), then the smaller D_paint, then the lower file index.
/// </summary>
internal static class RunwayPaintPairing
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayPaintPairing");

    /// <summary>How far (ft) a painted segment is extended at either end when it is tested against a centerline.</summary>
    internal const double BarExtensionFt = 10.0;

    /// <summary>How far (ft) inside the rule's centerline standoff D_rule a snap point may sit.</summary>
    internal const double MaxInsideRuleFt = 60.0;

    /// <summary>How far (ft) beyond the rule's centerline standoff D_rule a snap point may sit.</summary>
    internal const double MaxOutsideRuleFt = 100.0;

    /// <summary>How far (ft) beyond the runway's edge a snap point must sit at least.</summary>
    internal const double MinBeyondRunwayEdgeFt = 50.0;

    /// <summary>The smallest angle (degrees) between a bar and the taxiway segment it crosses: within 30° of perpendicular.</summary>
    private const double MinTaxiwayAngleDeg = 60.0;

    /// <summary>The largest angle (degrees) between a bar and the runway for the bar to count as parallel to the runway.</summary>
    private const double MaxRunwayAngleDeg = 15.0;

    /// <summary>
    /// A segment whose both ends lie this close (ft) to a centerline segment, and within <see cref="AlongCenterlineAngleDeg"/>
    /// of it, is that centerline.
    /// </summary>
    private const double AlongCenterlineFt = 15.0;

    /// <summary>See <see cref="AlongCenterlineFt"/>.</summary>
    private const double AlongCenterlineAngleDeg = 20.0;

    private const double FeetPerDegreeLat = GeoMath.FeetPerNm * 60.0;

    /// <summary>
    /// The first-ranked painted bar <paramref name="crossing"/>'s hold-short point pairs with (the innermost by walk order), or
    /// null when none qualifies. Production may pass over this candidate for a later one when the placement cannot seat the bar
    /// on it.
    /// </summary>
    /// <param name="layout">The layout being built, before this crossing's bar is seated.</param>
    /// <param name="crossing">The boundary edge, its node on the runway and its node off it.</param>
    /// <param name="rect">The runway's parse-time rectangle.</param>
    /// <param name="ruleFt">The crossing's rule standoff D_rule, feet.</param>
    /// <param name="paint">The videomap segments and the runway centerlines.</param>
    /// <returns>The first of <see cref="Candidates"/>, or null.</returns>
    internal static PaintPair? Pair(AirportGroundLayout layout, Crossing crossing, in RunwayRectangle rect, double ruleFt, PaintSource paint) =>
        Candidates(layout, crossing, rect, ruleFt, paint) is [var innermost, ..] ? innermost : null;

    /// <summary>
    /// Every painted bar <paramref name="crossing"/>'s hold-short point can pair with, innermost first
    /// (<see cref="InnermostFirst"/>).
    /// </summary>
    /// <param name="layout">The layout being built, before this crossing's bar is seated.</param>
    /// <param name="crossing">The boundary edge, its node on the runway and its node off it.</param>
    /// <param name="rect">The runway's parse-time rectangle.</param>
    /// <param name="ruleFt">The crossing's rule standoff D_rule, feet.</param>
    /// <param name="paint">The videomap segments and the runway centerlines.</param>
    /// <returns>The candidates, innermost first; empty when none qualifies.</returns>
    internal static List<PaintPair> Candidates(
        AirportGroundLayout layout,
        Crossing crossing,
        in RunwayRectangle rect,
        double ruleFt,
        PaintSource paint
    )
    {
        var context = new PairingContext(
            layout,
            rect,
            new LocalFrame(rect.RefLat, rect.RefLon),
            WalkChain(layout, crossing, rect),
            Math.Sign(SignedCrossFt(rect, crossing.Off.Position)),
            ruleFt,
            paint
        );

        var candidates = new List<PaintPair>();
        for (int index = 0; index < paint.Segments.Count; index++)
        {
            if (Candidate(context, index) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        candidates.Sort(InnermostFirst);
        if (candidates.Count == 0)
        {
            GroundNode end = context.Chain[^1].Off;
            Log.LogDebug(
                "No paint pairs with {Taxiway} at {Runway} (D_rule={RuleFt:F1} ft): the {Links}-link walk ends at node {Node}, {EndFt:F1} ft out",
                crossing.Edge.TaxiwayName,
                rect.CombinedId,
                ruleFt,
                context.Chain.Count,
                end.Id,
                Math.Abs(SignedCrossFt(rect, end.Position))
            );
        }

        return candidates;
    }

    /// <summary>
    /// Innermost first: the snap point nearer the runway along the taxiway walk, then the smaller D_paint, then the lower
    /// file index. An aircraft leaving the runway is clear at the first marking it crosses (AIM 2-3-5.a.1).
    /// </summary>
    internal static int InnermostFirst(PaintPair x, PaintPair y)
    {
        int byWalk = x.WalkFt.CompareTo(y.WalkFt);
        if (byWalk != 0)
        {
            return byWalk;
        }

        int byPaint = x.PaintFt.CompareTo(y.PaintFt);
        return (byPaint != 0) ? byPaint : x.SegmentIndex.CompareTo(y.SegmentIndex);
    }

    /// <summary>
    /// The crossing's own taxiway centerline as straight links, nearest the runway first: the boundary edge, then the
    /// hold-short walker's continuations (<see cref="RunwayCrossingDetector.PickWalkContinuation"/>), through junctions while
    /// the next edge carries the crossing's own taxiway name, until the name changes, a dead end or the walker's hop budget.
    /// Paint on another taxiway's branch beyond a junction never lies on the chain, so it never pairs.
    /// </summary>
    private static List<Crossing> WalkChain(AirportGroundLayout layout, Crossing crossing, in RunwayRectangle rect)
    {
        var chain = new List<Crossing> { crossing };
        GroundNode prev = crossing.On;
        GroundNode current = crossing.Off;
        var visited = new HashSet<int> { prev.Id, current.Id };
        for (int hop = 0; hop < RunwayCrossingDetector.HoldShortWalkMaxHops; hop++)
        {
            if (
                RunwayCrossingDetector.PickWalkContinuation(layout, new WalkStep(crossing.Edge.TaxiwayName, prev, current), visited, rect)
                is not { } next
            )
            {
                break;
            }

            chain.Add(new Crossing(next.Edge, current, next.Far));
            prev = current;
            current = next.Far;
            visited.Add(current.Id);
        }

        return chain;
    }

    /// <summary>Segment <paramref name="index"/> as a candidate pair, or null when it fails any condition.</summary>
    private static PaintPair? Candidate(PairingContext context, int index)
    {
        VideoMapSegment segment = context.Paint.Segments[index];
        if ((Extend(context, segment) is not { } bar) || (FirstChainHit(context, bar) is not (int linkIndex, double fraction)))
        {
            return null;
        }

        Crossing link = context.Chain[linkIndex];
        LatLon snap = Lerp(link.On.Position, link.Off.Position, fraction);
        double signedFt = SignedCrossFt(context.Rect, snap);
        double paintFt = Math.Abs(signedFt);

        // The side check is a backstop: a bar on the far side of the centerline meets the chain only on the boundary edge,
        // inside the half-width + 50 ft floor, so the band refuses it first.
        if ((Math.Sign(signedFt) != context.NodeSide) || !InBand(paintFt, context.RuleFt, context.Rect))
        {
            Log.LogDebug(
                "Paint segment {Segment} crosses {Taxiway} at {Runway} outside the band or on the far side: "
                    + "D_paint={PaintFt:F1} ft, D_rule={RuleFt:F1} ft",
                index,
                link.Edge.TaxiwayName,
                context.Rect.CombinedId,
                paintFt,
                context.RuleFt
            );
            return null;
        }

        if (NotABar(context, bar, link) is { } reason)
        {
            Log.LogDebug(
                "Paint segment {Segment} crosses {Taxiway} at {Runway} (D_paint={PaintFt:F1} ft) but is not a bar: {Reason}",
                index,
                link.Edge.TaxiwayName,
                context.Rect.CombinedId,
                paintFt,
                reason
            );
            return null;
        }

        double nearEndFt = Math.Min(Math.Abs(SignedCrossFt(context.Rect, segment.A)), Math.Abs(SignedCrossFt(context.Rect, segment.B)));
        return new PaintPair(index, snap, paintFt, nearEndFt, WalkFt(context, linkIndex, fraction));
    }

    /// <summary>
    /// The segment as drawn and extended <see cref="BarExtensionFt"/> at either end, in the local frame; null when it has
    /// no length.
    /// </summary>
    private static Bar? Extend(PairingContext context, VideoMapSegment segment)
    {
        Vec drawnA = context.Frame.ToFt(segment.A);
        Vec drawnB = context.Frame.ToFt(segment.B);
        Vec direction = drawnB - drawnA;
        double length = direction.Length;
        if (length < 1e-9)
        {
            return null;
        }

        Vec extension = direction * (BarExtensionFt / length);
        return new Bar(drawnA, drawnB, drawnA - extension, drawnB + extension);
    }

    /// <summary>
    /// Why a segment that crosses the taxiway in the band is not a hold bar, or null when it is one: neither within 30° of
    /// perpendicular to the taxiway segment nor within 15° of parallel to the runway, crossing a runway centerline, or
    /// lying along a drawn centerline.
    /// </summary>
    private static string? NotABar(PairingContext context, Bar bar, Crossing link)
    {
        Vec linkDirection = context.Frame.ToFt(link.Off.Position) - context.Frame.ToFt(link.On.Position);
        double headingRad = context.Rect.TrueHeading.Degrees * Math.PI / 180.0;
        var runwayDirection = new Vec(Math.Sin(headingRad), Math.Cos(headingRad));
        bool squareToTaxiway = AxisAngleDeg(bar.Direction, linkDirection) >= MinTaxiwayAngleDeg;
        bool parallelToRunway = AxisAngleDeg(bar.Direction, runwayDirection) <= MaxRunwayAngleDeg;
        if (!squareToTaxiway && !parallelToRunway)
        {
            return "neither square to the taxiway nor parallel to the runway";
        }

        if (CrossesRunwayCenterline(context, bar.A, bar.B))
        {
            return "crosses a runway centerline";
        }

        return LiesAlongCenterline(context, bar.DrawnA, bar.DrawnB) ? "lies along a drawn centerline" : null;
    }

    /// <summary>Inclusive: D_rule − 60 ft ≤ D_paint ≤ D_rule + 100 ft, and D_paint ≥ runway half-width + 50 ft.</summary>
    private static bool InBand(double paintFt, double ruleFt, in RunwayRectangle rect) =>
        (paintFt - ruleFt <= MaxOutsideRuleFt)
        && (ruleFt - paintFt <= MaxInsideRuleFt)
        && (paintFt >= (rect.HalfWidthNm * GeoMath.FeetPerNm) + MinBeyondRunwayEdgeFt);

    /// <summary>
    /// The first chain link (nearest the runway) the extended bar crosses, by its index in the chain, and where along it.
    /// </summary>
    private static (int LinkIndex, double Fraction)? FirstChainHit(PairingContext context, Bar bar)
    {
        for (int i = 0; i < context.Chain.Count; i++)
        {
            Crossing link = context.Chain[i];
            if (Intersect(bar.A, bar.B, context.Frame.ToFt(link.On.Position), context.Frame.ToFt(link.Off.Position)) is { } fraction)
            {
                return (i, fraction);
            }
        }

        return null;
    }

    /// <summary>
    /// How far along the chain from the runway node the point <paramref name="fraction"/> along link
    /// <paramref name="linkIndex"/> lies, feet.
    /// </summary>
    private static double WalkFt(PairingContext context, int linkIndex, double fraction)
    {
        double walkFt = 0.0;
        for (int i = 0; i <= linkIndex; i++)
        {
            Crossing link = context.Chain[i];
            double linkFt = (context.Frame.ToFt(link.Off.Position) - context.Frame.ToFt(link.On.Position)).Length;
            walkFt += (i < linkIndex) ? linkFt : fraction * linkFt;
        }

        return walkFt;
    }

    private static bool CrossesRunwayCenterline(PairingContext context, Vec a, Vec b)
    {
        foreach (IReadOnlyList<LatLon> centerline in context.Paint.RunwayCenterlines)
        {
            for (int i = 0; i + 1 < centerline.Count; i++)
            {
                if (Intersect(a, b, context.Frame.ToFt(centerline[i]), context.Frame.ToFt(centerline[i + 1])) is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether the drawn segment is a drawn centerline: a taxiway edge of the layout, or a runway centerline.</summary>
    private static bool LiesAlongCenterline(PairingContext context, Vec a, Vec b)
    {
        foreach (GroundEdge edge in context.Layout.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            if (Along(a, b, context.Frame.ToFt(edge.Nodes[0].Position), context.Frame.ToFt(edge.Nodes[1].Position)))
            {
                return true;
            }
        }

        foreach (IReadOnlyList<LatLon> centerline in context.Paint.RunwayCenterlines)
        {
            for (int i = 0; i + 1 < centerline.Count; i++)
            {
                if (Along(a, b, context.Frame.ToFt(centerline[i]), context.Frame.ToFt(centerline[i + 1])))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Along(Vec a, Vec b, Vec p, Vec q) =>
        (DistanceToSegmentFt(a, p, q) < AlongCenterlineFt)
        && (DistanceToSegmentFt(b, p, q) < AlongCenterlineFt)
        && (AxisAngleDeg(b - a, q - p) < AlongCenterlineAngleDeg);

    /// <summary>
    /// Where segment <paramref name="p"/>–<paramref name="q"/> crosses segment <paramref name="a"/>–<paramref name="b"/>, as a
    /// fraction along p–q; null when they do not cross.
    /// </summary>
    private static double? Intersect(Vec a, Vec b, Vec p, Vec q)
    {
        Vec r = b - a;
        Vec s = q - p;
        double denominator = Cross(r, s);
        if (Math.Abs(denominator) < 1e-9)
        {
            return null;
        }

        Vec offset = p - a;
        double alongBar = Cross(offset, s) / denominator;
        double alongLink = Cross(offset, r) / denominator;
        return ((alongBar is >= 0.0 and <= 1.0) && (alongLink is >= 0.0 and <= 1.0)) ? alongLink : null;
    }

    private static double DistanceToSegmentFt(Vec point, Vec p, Vec q)
    {
        Vec s = q - p;
        double lengthSquared = Dot(s, s);
        double fraction = (lengthSquared < 1e-12) ? 0.0 : Math.Clamp(Dot(point - p, s) / lengthSquared, 0.0, 1.0);
        return (point - (p + (s * fraction))).Length;
    }

    /// <summary>The angle between two lines, folded to 0-90 degrees.</summary>
    private static double AxisAngleDeg(Vec u, Vec v)
    {
        double degrees = Math.Abs(Math.Atan2(Cross(u, v), Dot(u, v))) * 180.0 / Math.PI;
        return Math.Min(degrees, 180.0 - degrees);
    }

    private static double Cross(Vec u, Vec v) => (u.X * v.Y) - (u.Y * v.X);

    private static double Dot(Vec u, Vec v) => (u.X * v.X) + (u.Y * v.Y);

    private static LatLon Lerp(LatLon from, LatLon to, double fraction) =>
        new(from.Lat + (fraction * (to.Lat - from.Lat)), from.Lon + (fraction * (to.Lon - from.Lon)));

    private static double SignedCrossFt(in RunwayRectangle rect, LatLon position) =>
        GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading) * GeoMath.FeetPerNm;

    private readonly record struct PairingContext(
        AirportGroundLayout Layout,
        RunwayRectangle Rect,
        LocalFrame Frame,
        List<Crossing> Chain,
        int NodeSide,
        double RuleFt,
        PaintSource Paint
    );

    /// <summary>A painted segment in the local frame: as drawn, and extended <see cref="BarExtensionFt"/> at either end.</summary>
    private readonly record struct Bar(Vec DrawnA, Vec DrawnB, Vec A, Vec B)
    {
        public Vec Direction => DrawnB - DrawnA;
    }

    /// <summary>A flat frame in feet around the runway's reference point (x east, y north), for the line geometry.</summary>
    private readonly record struct LocalFrame(double Lat0, double Lon0)
    {
        public Vec ToFt(LatLon position) =>
            new((position.Lon - Lon0) * FeetPerDegreeLat * Math.Cos(Lat0 * Math.PI / 180.0), (position.Lat - Lat0) * FeetPerDegreeLat);
    }

    private readonly record struct Vec(double X, double Y)
    {
        public double Length => Math.Sqrt((X * X) + (Y * Y));

        public static Vec operator -(Vec u, Vec v) => new(u.X - v.X, u.Y - v.Y);

        public static Vec operator +(Vec u, Vec v) => new(u.X + v.X, u.Y + v.Y);

        public static Vec operator *(Vec u, double k) => new(u.X * k, u.Y * k);
    }
}
