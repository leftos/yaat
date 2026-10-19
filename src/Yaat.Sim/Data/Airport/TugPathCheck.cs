using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>Which flown-path rule a refusal comes from; a lower value is more severe.</summary>
internal enum TugPathSeverity
{
    Runway,
    HoldingPosition,

    /// <summary>A push onto a taxiway reaches too far past its centreline; only a taxiway goal is judged by it.</summary>
    TaxiwayOvershoot,

    /// <summary>A move to any other goal crosses movement-area pavement it was not sent to.</summary>
    MovementArea,
    ParkedNeighbour,
}

/// <summary>A flown-path refusal and the rule it comes from.</summary>
/// <param name="Severity">The rule the refusal comes from.</param>
/// <param name="Message">What the controller sees.</param>
/// <param name="Taxiway">The taxiway a taxiway-overshoot or movement-area refusal names; null for every other rule.</param>
internal readonly record struct TugPathRefusal(TugPathSeverity Severity, string Message, string? Taxiway);

/// <summary>
/// Checks a candidate's flown path, sample by sample, against the layout: the footprint (fuselage length ×
/// wingspan about the reference point, oriented by the nose) must stay more than a runway's half-width from its
/// centreline and must not cross an edge touching a runway holding position. A push onto a taxiway must then not take
/// the aircraft's centre (the reference point) more than <see cref="MaxTaxiwayOvershootFt"/> past that taxiway's
/// centreline, on the side away from where the tow started. Any other move's fuselage centreline must not cross
/// movement-area pavement the goal does not name, except the pavement the goal's own first sample already lies across,
/// for the unbroken run of samples still across it, and the pavement its last sample lies across, over the unbroken
/// run back to it — leaving pavement and reaching it. That pavement is the edges the fuselage lies across plus their
/// chain of same-named neighbours within a fuselage length, because the graph cuts a taxiway's centreline into a stub
/// at every junction. Open apron is not checked: the layout carries no pavement polygons, and real aprons have ungraphed
/// stretches wider than any distance-from-the-graph test could allow. Edges are taken as their chords. Works in
/// flat feet east and north of the plan's start, the frame <c>GeoMath.ProjectPoint</c> steps in. Holds one plan's
/// precomputed segments and memoized pavement verdicts, so one instance serves every candidate of a plan.
/// </summary>
public sealed class TugPathCheck
{
    private const double Epsilon = 1e-9;

    /// <summary>The spacing of a trace's samples, feet, for naming where along a move a sample lies.</summary>
    private const double SampleSpacingFt = 5.0;

    /// <summary>How far past an edge's end a point may project and still count as on that edge, feet.</summary>
    private const double ExtentSlackFt = 1.0;

    /// <summary>
    /// How close to the taxiway's centreline a push onto it may start and count as starting on it, feet: such a push has
    /// no far side, so the centre reaching past the centreline on either side counts toward the overshoot.
    /// </summary>
    private const double OnCentrelineFt = 1.0;

    private static readonly ILogger Log = SimLog.CreateLogger("TugPathCheck");
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>No pavement exempt: the end footprint of a marked point clears all of it.</summary>
    private static readonly HashSet<string> NoExemptNames = [];

    private static readonly ConditionalWeakTable<AirportGroundLayout, List<GeoHoldBand>> HoldBands = [];

    private readonly PavementClassifier _pavement;
    private readonly LatLon _origin;
    private readonly double _eastFtPerDeg;
    private readonly double _halfLengthFt;
    private readonly double _halfSpanFt;
    private readonly double _maxOvershootFt;
    private readonly List<RunwaySegment> _runways;

    /// <summary>Every edge of the layout as its chord in the local frame, by <see cref="TugLayoutIndex"/> edge index.</summary>
    private readonly EdgeSegment[] _edges;

    /// <summary>Each edge's movement-area name, or null for ramp, by edge index.</summary>
    private readonly string?[] _movementAreaNames;

    /// <summary>Each edge's names, by edge index.</summary>
    private readonly string[][] _edgeNames;

    /// <summary>The indices of the edges touching a runway holding position, in edge order.</summary>
    private readonly int[] _holdShortEdges;

    /// <summary>Every runway holding position in the layout, in the local frame.</summary>
    private readonly List<Pt> _holdShorts;

    /// <summary>Each runway's centreline and nearest hold-short distance, in the local frame (<see cref="HoldDistanceRefusal"/>).</summary>
    private readonly List<RunwayHoldBand> _holdBands;

    /// <summary>Each goal taxiway's straight centreline edges (<see cref="TaxiwayOvershootRefusal"/>), found on first use.</summary>
    private readonly Dictionary<string, List<EdgeSegment>> _centrelineByTaxiway = new(StringComparer.Ordinal);

    // Working lists one check fills and the next clears: an instance serves one plan's candidates, one at a time.
    private readonly List<LocalPose> _poses = [];
    private readonly List<EdgeSegment> _holdEdges = [];
    private readonly List<(Pt A, Pt B)> _nearPieces = [];
    private readonly List<MovementEdge> _movementEdges = [];
    private readonly HashSet<string> _named = new(StringComparer.OrdinalIgnoreCase);

    internal TugPathCheck(AirportGroundLayout layout, MovementAreaClassification movementArea, AircraftFootprint footprint, LatLon planStart)
    {
        var index = TugLayoutIndex.For(layout);
        _pavement = new PavementClassifier(layout, movementArea);
        _origin = planStart;
        _eastFtPerDeg = 60.0 * GeoMath.FeetPerNm * Math.Cos(planStart.Lat * DegToRad);
        _halfLengthFt = footprint.LengthFt / 2.0;
        _halfSpanFt = footprint.WingspanFt / 2.0;
        _maxOvershootFt = MaxTaxiwayOvershootFt(footprint);
        _runways = RunwaySegmentsOf(layout);
        _edges = new EdgeSegment[index.Edges.Length];
        for (int i = 0; i < _edges.Length; i++)
        {
            IGroundEdge edge = index.Edges[i];
            _edges[i] = new EdgeSegment(edge, Local(edge.Nodes[0].Position), Local(edge.Nodes[1].Position));
        }

        _movementAreaNames = index.MovementAreaNames(movementArea);
        _edgeNames = index.EdgeNames;
        _holdShortEdges = index.HoldShortEdges;
        _holdShorts = new List<Pt>(index.HoldShorts.Length);
        foreach (LatLon hold in index.HoldShorts)
        {
            _holdShorts.Add(Local(hold));
        }

        _holdBands = LocalHoldBands(HoldBandsOf(layout));
    }

    /// <summary>
    /// How far past the centreline of the taxiway a push is sent onto, on the side away from where the tow started, the
    /// aircraft's centre (the reference point) may reach, feet: half the wingspan
    /// (<see cref="AircraftFootprint.WingspanFt"/>, which falls back by category when the type has none). A push onto a
    /// taxiway sweeps over the taxiways that meet it — at SFO D7, A and F1 meet at right angles, so lining up on A there
    /// lies across F1 — so it is judged by not going too far past its own taxiway, not by the pavement it crosses.
    /// Known limit: half a span at the centre can put the main gear past the edge of a narrow taxiway; the layout
    /// carries no pavement polygons to check the gear against.
    /// </summary>
    /// <param name="footprint">The aircraft's dimensions.</param>
    /// <returns>The bound, feet.</returns>
    public static double MaxTaxiwayOvershootFt(AircraftFootprint footprint) => footprint.WingspanFt / 2.0;

    /// <summary>
    /// The first rule the path breaks — runway, then holding position, then, for a push onto <paramref name="goalTaxiway"/>,
    /// the overshoot past it, else the movement area — or null when clear.
    /// </summary>
    /// <param name="traces">The candidate's moves, flown.</param>
    /// <param name="exemptNames">Pavement the movement-area rule lets the fuselage cross.</param>
    /// <param name="goalTaxiway">The taxiway a push onto a taxiway is sent onto; null for every other goal.</param>
    /// <param name="naming">
    /// How a refusal names the move (<c>Subject</c>), and the marked point the move ends on (<c>MarkedPoint</c>, <c>the
    /// marked point</c>) or null for every other goal. A marked point has no pavement of its own to arrive on: its end
    /// footprint must clear every movement-area edge.
    /// </param>
    /// <param name="forced">
    /// A forced tow (<c>PUSHF</c>): only the runway and holding-position rules apply; the taxiway overshoot and the
    /// movement-area rule are skipped.
    /// </param>
    /// <returns>The refusal, or null.</returns>
    internal TugPathRefusal? Check(
        IReadOnlyList<TugMoveTrace> traces,
        IReadOnlySet<string> exemptNames,
        string? goalTaxiway,
        (string Subject, string? MarkedPoint) naming,
        bool forced
    )
    {
        (string subject, string? markedPoint) = naming;
        List<LocalPose> poses = Poses(traces);
        if (poses.Count == 0)
        {
            return null;
        }

        Box box = FootprintBox(poses);
        TugPathRefusal? runwayOrHold =
            RunwayRefusal(poses, box, subject) ?? HoldingPositionRefusal(poses, box, subject) ?? HoldDistanceRefusal(poses, box, subject);
        if (forced)
        {
            return runwayOrHold;
        }

        return runwayOrHold
            ?? (
                goalTaxiway is { } taxiway
                    ? TaxiwayOvershootRefusal(poses, box, exemptNames, taxiway, subject)
                    : MovementAreaCrossings(poses, box, exemptNames, (subject, markedPoint), all: null)
            );
    }

    /// <summary>
    /// Every taxiway rule a forced tow skips that the path breaks, one per taxiway, in the order met: for a push onto
    /// <paramref name="goalTaxiway"/>, the overshoot past it; for every other goal, each movement-area taxiway the fuselage
    /// crosses (the rule <see cref="Check"/> refuses a plain tow for at its first crossing). The runway and holding-position
    /// rules are not judged: a forced tow keeps them. Empty when the path breaks none.
    /// </summary>
    /// <param name="traces">The candidate's moves, flown.</param>
    /// <param name="exemptNames">Pavement the movement-area rule lets the fuselage cross.</param>
    /// <param name="goalTaxiway">The taxiway a push onto a taxiway is sent onto; null for every other goal.</param>
    /// <param name="naming">How the move and its marked point are named, as for <see cref="Check"/>.</param>
    /// <returns>The rules broken, each naming its taxiway.</returns>
    internal List<TugPathRefusal> SkippedTaxiwayHits(
        IReadOnlyList<TugMoveTrace> traces,
        IReadOnlySet<string> exemptNames,
        string? goalTaxiway,
        (string Subject, string? MarkedPoint) naming
    )
    {
        List<LocalPose> poses = Poses(traces);
        if (poses.Count == 0)
        {
            return [];
        }

        Box box = FootprintBox(poses);
        if (goalTaxiway is not { } taxiway)
        {
            var hits = new List<TugPathRefusal>();
            MovementAreaCrossings(poses, box, exemptNames, naming, hits);
            return hits;
        }

        return TaxiwayOvershootRefusal(poses, box, exemptNames, taxiway, naming.Subject) is { } overshoot ? [overshoot] : [];
    }

    /// <summary>Every flown sample in the local frame, move by move, into the instance's working list.</summary>
    private List<LocalPose> Poses(IReadOnlyList<TugMoveTrace> traces)
    {
        _poses.Clear();
        for (int move = 0; move < traces.Count; move++)
        {
            TugMoveTrace trace = traces[move];
            bool pulled = trace.Move.Kind == PushbackLegKind.Pull;
            IReadOnlyList<PushbackPose> samples = trace.Samples;
            for (int sample = 0; sample < samples.Count; sample++)
            {
                PushbackPose p = samples[sample];
                _poses.Add(new LocalPose(Local(p.Position), p.NoseTrueDeg * DegToRad, move, sample, pulled));
            }
        }

        return _poses;
    }

    /// <summary>The prefilter box around the poses, reaching as far as the footprint does.</summary>
    private Box FootprintBox(List<LocalPose> poses) => Box.Around(poses).Padded((2.0 * _halfLengthFt) + (2.0 * _halfSpanFt));

    /// <summary>
    /// The nearest edge that the straight ray from <paramref name="from"/> along <paramref name="travelTrueDeg"/>
    /// crosses inside <paramref name="window"/> (from its minimum distance out to its range, feet), among the edges
    /// <paramref name="match"/> accepts, and how far along the ray it is crossed. An edge running parallel to the ray
    /// is never crossed. Null when none is.
    /// </summary>
    internal (IGroundEdge Edge, double DistanceFt)? FirstCrossing(
        LatLon from,
        double travelTrueDeg,
        (double MinDistanceFt, double RangeFt) window,
        Func<IGroundEdge, bool> match
    )
    {
        (double minDistanceFt, double rangeFt) = window;
        Pt start = Local(from);
        double travelRad = travelTrueDeg * DegToRad;
        var ray = new Pt(Math.Sin(travelRad) * rangeFt, Math.Cos(travelRad) * rangeFt);
        (IGroundEdge Edge, double DistanceFt)? nearest = null;
        foreach (EdgeSegment edge in _edges)
        {
            if ((RayFraction(start, ray, edge.A, edge.B) is not { } fraction) || !match(edge.Edge))
            {
                continue;
            }

            double distanceFt = fraction * rangeFt;
            if (distanceFt < minDistanceFt)
            {
                continue;
            }

            if ((nearest is not { } held) || (distanceFt < held.DistanceFt))
            {
                nearest = (edge.Edge, distanceFt);
            }
        }

        return nearest;
    }

    /// <summary>
    /// Where the straight ray from <paramref name="from"/> along <paramref name="travelTrueDeg"/> crosses the line of a
    /// straight edge running from <paramref name="inward"/> to its end <paramref name="end"/>, extended past that end:
    /// how far along the ray, feet, and how far past <paramref name="end"/> along the extension, feet (negative when the
    /// crossing is back along the edge's own line). Null when the ray runs parallel to the line or meets it behind
    /// <paramref name="from"/>.
    /// </summary>
    internal (double AlongRayFt, double PastEndFt)? ExtensionCrossing(LatLon from, double travelTrueDeg, LatLon end, LatLon inward)
    {
        Pt start = Local(from);
        double travelRad = travelTrueDeg * DegToRad;
        var travel = new Pt(Math.Sin(travelRad), Math.Cos(travelRad));
        Pt endPoint = Local(end);
        Pt outward = endPoint - Local(inward);
        double lengthFt = Distance(outward, default);
        if (lengthFt <= Epsilon)
        {
            return null;
        }

        var unit = new Pt(outward.X / lengthFt, outward.Y / lengthFt);
        double denominator = Cross(travel, unit);
        if (Math.Abs(denominator) <= Epsilon)
        {
            return null;
        }

        Pt offset = endPoint - start;
        double alongRayFt = Cross(offset, unit) / denominator;
        double pastEndFt = Cross(offset, travel) / denominator;
        return (alongRayFt >= 0.0) ? (alongRayFt, pastEndFt) : null;
    }

    /// <summary>
    /// How far the point <paramref name="alongFt"/> along the straight ray from <paramref name="from"/> on
    /// <paramref name="travelTrueDeg"/> lies off the line through <paramref name="lineA"/> and <paramref name="lineB"/>,
    /// extended both ways, feet; the distance to <paramref name="lineA"/> when the two coincide.
    /// </summary>
    internal double RayPointOffLineFt(LatLon from, double travelTrueDeg, double alongFt, LatLon lineA, LatLon lineB)
    {
        double travelRad = travelTrueDeg * DegToRad;
        Pt point = Local(from) + new Pt(Math.Sin(travelRad) * alongFt, Math.Cos(travelRad) * alongFt);
        Pt a = Local(lineA);
        Pt direction = Local(lineB) - a;
        double lengthFt = Distance(direction, default);
        return (lengthFt <= Epsilon) ? Distance(point, a) : Math.Abs(Cross(direction, point - a)) / lengthFt;
    }

    /// <summary>
    /// The nearest edge, among those <paramref name="match"/> accepts, that runs within the window's
    /// <c>MaxAngleDeg</c> of <paramref name="travelTrueDeg"/> (either way along the edge) and whose point nearest
    /// <paramref name="from"/> lies ahead along that travel, past zero, with both that distance ahead and the
    /// straight-line distance from <paramref name="from"/> no further than the window's range, feet. Bounding the
    /// straight-line distance too keeps an edge far off to the side from qualifying on its along-track distance alone.
    /// Reports the edge's direction nearest the travel, degrees true, that nearest point, how far ahead along the
    /// travel it lies, and how far it is from <paramref name="from"/>. Null when no edge qualifies.
    /// </summary>
    internal (IGroundEdge Edge, double LineTravelTrueDeg, LatLon NearestPoint, double AlongFt, double DistanceFt)? NearestAlongside(
        LatLon from,
        double travelTrueDeg,
        (double MaxAngleDeg, double RangeFt) window,
        Func<IGroundEdge, bool> match
    )
    {
        Pt start = Local(from);
        double travelRad = travelTrueDeg * DegToRad;
        var travel = new Pt(Math.Sin(travelRad), Math.Cos(travelRad));
        (IGroundEdge Edge, double LineTravelTrueDeg, LatLon NearestPoint, double AlongFt, double DistanceFt)? nearest = null;
        foreach (EdgeSegment edge in _edges)
        {
            if (!match(edge.Edge))
            {
                continue;
            }

            Pt nearestPoint = NearestPointOnSegment(start, edge.A, edge.B);
            Pt offset = nearestPoint - start;
            double alongFt = (offset.X * travel.X) + (offset.Y * travel.Y);
            double distanceFt = Distance(offset, default);
            double? lineTravel = AlongsideTravelDeg(edge, travelTrueDeg, window.MaxAngleDeg);
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "Alongside search on {TravelDeg:F1}°: edge {NodeA}-{NodeB} nearest point {DistanceFt:F1} ft away, "
                        + "{AlongFt:F1} ft along; {Verdict}",
                    travelTrueDeg,
                    edge.Edge.Nodes[0].Id,
                    edge.Edge.Nodes[1].Id,
                    distanceFt,
                    alongFt,
                    lineTravel is { } deg ? $"runs on {deg:F1}°" : "runs across"
                );
            }

            if (lineTravel is not { } lineTravelDeg)
            {
                continue;
            }

            bool ahead = (alongFt > 0.0) && (alongFt <= window.RangeFt) && (distanceFt <= window.RangeFt);
            if (ahead && ((nearest is not { } held) || (distanceFt < held.DistanceFt)))
            {
                nearest = (edge.Edge, lineTravelDeg, Geo(nearestPoint), alongFt, distanceFt);
            }
        }

        return nearest;
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies on the extent of one of the edges <paramref name="match"/> accepts: it
    /// projects between that edge's own ends, with <see cref="ExtentSlackFt"/> of slack at each, and lies no further
    /// than <paramref name="toleranceFt"/> off its line. Being near an edge's line is not the same as being on the
    /// edge — a point off the end of every edge of a taxiway is not on the taxiway.
    /// </summary>
    internal bool IsOnEdgeExtent(LatLon point, double toleranceFt, Func<IGroundEdge, bool> match)
    {
        Pt p = Local(point);
        foreach (EdgeSegment edge in _edges)
        {
            if (!match(edge.Edge))
            {
                continue;
            }

            Pt direction = edge.B - edge.A;
            double lengthFt = Distance(direction, default);
            if (lengthFt <= Epsilon)
            {
                continue;
            }

            Pt offset = p - edge.A;
            double alongFt = ((offset.X * direction.X) + (offset.Y * direction.Y)) / lengthFt;
            double crossFt = Math.Abs(Cross(direction, offset)) / lengthFt;
            bool onEdge = (alongFt >= -ExtentSlackFt) && (alongFt <= (lengthFt + ExtentSlackFt)) && (crossFt <= toleranceFt);
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "Extent check: edge {NodeA}-{NodeB} is {LengthFt:F1} ft long; "
                        + "the point lies {AlongFt:F1} ft along it and {CrossFt:F1} ft off its line — {Verdict}",
                    edge.Edge.Nodes[0].Id,
                    edge.Edge.Nodes[1].Id,
                    lengthFt,
                    alongFt,
                    crossFt,
                    onEdge ? "on the edge" : "off it"
                );
            }

            if (onEdge)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The movement-area name an edge carries, or null when it is ramp.</summary>
    internal string? MovementAreaName(IGroundEdge edge) => _pavement.MovementAreaName(edge);

    /// <summary>
    /// The edge's direction, one way or the other, nearest <paramref name="travelTrueDeg"/>, degrees true, when it is
    /// within <paramref name="maxAngleDeg"/> of it; null otherwise or for a zero-length edge.
    /// </summary>
    private static double? AlongsideTravelDeg(EdgeSegment edge, double travelTrueDeg, double maxAngleDeg)
    {
        Pt direction = edge.B - edge.A;
        if (Distance(direction, default) <= Epsilon)
        {
            return null;
        }

        var forward = new TrueHeading(Math.Atan2(direction.X, direction.Y) / DegToRad);
        var travel = new TrueHeading(travelTrueDeg);
        TrueHeading nearer = forward.AbsAngleTo(travel) <= forward.ToReciprocal().AbsAngleTo(travel) ? forward : forward.ToReciprocal();
        return nearer.AbsAngleTo(travel) <= maxAngleDeg ? nearer.Degrees : null;
    }

    private TugPathRefusal? RunwayRefusal(List<LocalPose> poses, Box box, string subject)
    {
        foreach (RunwaySegment runway in _runways)
        {
            if (!box.Padded(runway.HalfWidthFt).Overlaps(runway.A, runway.B))
            {
                continue;
            }

            foreach (LocalPose pose in poses)
            {
                if (SideWithinRunway(FootprintSides(pose), runway))
                {
                    return new TugPathRefusal(TugPathSeverity.Runway, $"Unable, {subject} would put the aircraft on runway {runway.Name}", null);
                }
            }
        }

        return null;
    }

    /// <summary>Whether a side of the footprint comes within the runway's half-width of its centreline.</summary>
    private static bool SideWithinRunway(Sides sides, RunwaySegment runway)
    {
        for (int s = 0; s < Sides.Count; s++)
        {
            (Pt A, Pt B) side = sides[s];
            if (SegmentDistanceFt(side.A, side.B, runway.A, runway.B) <= runway.HalfWidthFt)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a side of the footprint crosses one of <paramref name="edges"/>.</summary>
    private static bool SideCrossesAny(Sides sides, List<EdgeSegment> edges)
    {
        for (int s = 0; s < Sides.Count; s++)
        {
            (Pt A, Pt B) side = sides[s];
            foreach (EdgeSegment edge in edges)
            {
                if (Crosses(side.A, side.B, edge.A, edge.B))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a side of the footprint crosses the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static bool SideCrosses(Sides sides, Pt a, Pt b)
    {
        for (int s = 0; s < Sides.Count; s++)
        {
            if (Crosses(sides[s].A, sides[s].B, a, b))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// No flown sample's footprint lies across an edge touching a runway holding position, unless the first sample's
    /// footprint already lies over that same edge (a side crosses it, or an end of it lies inside): an aircraft stopped at
    /// or past a hold bar may be towed back off it, and <see cref="HoldDistanceRefusal"/> keeps it from being towed further in.
    /// </summary>
    private TugPathRefusal? HoldingPositionRefusal(List<LocalPose> poses, Box box, string subject)
    {
        LocalPose start = poses[0];
        Sides startSides = FootprintSides(start);
        _holdEdges.Clear();
        foreach (int e in _holdShortEdges)
        {
            EdgeSegment edge = _edges[e];
            if (box.Overlaps(edge.A, edge.B) && !LiesOver(start, startSides, edge))
            {
                _holdEdges.Add(edge);
            }
        }

        if (_holdEdges.Count == 0)
        {
            return null;
        }

        foreach (LocalPose pose in poses)
        {
            if (SideCrossesAny(FootprintSides(pose), _holdEdges))
            {
                return new TugPathRefusal(TugPathSeverity.HoldingPosition, $"Unable, {subject} reaches a runway holding position", null);
            }
        }

        return null;
    }

    /// <summary>
    /// The footprint never moves deeper into a runway's safety area (AIM 2-3-5.a.1: the hold line marks the edge of the
    /// runway safety area, and no part of the aircraft may extend past it). A flown sample is refused when the footprint
    /// corner nearest a runway's centreline lies nearer it than both that runway's nearest hold-short node does
    /// (<see cref="HoldBandsOf"/>) and the first sample's own nearest corner does. An aircraft already inside the band,
    /// stopped at or past a hold bar, may be towed back out of it; it may never be towed further in.
    /// </summary>
    private TugPathRefusal? HoldDistanceRefusal(List<LocalPose> poses, Box box, string subject)
    {
        foreach (RunwayHoldBand band in _holdBands)
        {
            Box padded = box.Padded(band.HoldFt);
            _nearPieces.Clear();
            foreach ((Pt A, Pt B) piece in band.Pieces)
            {
                if (padded.Overlaps(piece.A, piece.B))
                {
                    _nearPieces.Add(piece);
                }
            }

            if (_nearPieces.Count == 0)
            {
                continue;
            }

            double limitFt = Math.Min(band.HoldFt, NearestCornerFt(poses[0], _nearPieces));
            foreach (LocalPose pose in poses)
            {
                if (NearestCornerFt(pose, _nearPieces) < limitFt)
                {
                    return new TugPathRefusal(TugPathSeverity.HoldingPosition, $"Unable, {subject} moves deeper toward runway {band.Name}", null);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the pose's footprint lies over the edge: one of its <paramref name="sides"/> crosses it, or an end of it
    /// lies inside.
    /// </summary>
    private bool LiesOver(LocalPose pose, Sides sides, EdgeSegment edge) =>
        SideCrosses(sides, edge.A, edge.B) || Inside(pose, edge.A) || Inside(pose, edge.B);

    /// <summary>Whether <paramref name="point"/> lies inside the pose's footprint, its outline included.</summary>
    private bool Inside(LocalPose pose, Pt point)
    {
        Pt offset = point - pose.Position;
        double aheadFt = (offset.X * Math.Sin(pose.NoseRad)) + (offset.Y * Math.Cos(pose.NoseRad));
        double rightFt = (offset.X * Math.Cos(pose.NoseRad)) - (offset.Y * Math.Sin(pose.NoseRad));
        return (Math.Abs(aheadFt) <= _halfLengthFt) && (Math.Abs(rightFt) <= _halfSpanFt);
    }

    /// <summary>How far the footprint corner nearest the centreline <paramref name="pieces"/> lies from them, feet.</summary>
    private double NearestCornerFt(LocalPose pose, List<(Pt A, Pt B)> pieces)
    {
        Sides sides = FootprintSides(pose);
        double nearestFt = NearestPieceFt(sides[0].A, pieces);
        for (int s = 1; s < Sides.Count; s++)
        {
            double cornerFt = NearestPieceFt(sides[s].A, pieces);
            if (cornerFt < nearestFt)
            {
                nearestFt = cornerFt;
            }
        }

        return nearestFt;
    }

    /// <summary>How far <paramref name="point"/> lies from the nearest of the (non-empty) <paramref name="pieces"/>, feet.</summary>
    private static double NearestPieceFt(Pt point, List<(Pt A, Pt B)> pieces)
    {
        double nearestFt = PointToSegmentFt(point, pieces[0].A, pieces[0].B);
        for (int p = 1; p < pieces.Count; p++)
        {
            double pieceFt = PointToSegmentFt(point, pieces[p].A, pieces[p].B);
            if (pieceFt < nearestFt)
            {
                nearestFt = pieceFt;
            }
        }

        return nearestFt;
    }

    /// <summary>
    /// Each runway's centreline pieces (its runway-centreline edges, grouped by name) and the distance of its nearest
    /// hold-short node from them, feet. A <see cref="GroundNodeType.RunwayHoldShort"/> node counts for the runway its
    /// <see cref="GroundNode.RunwayId"/> names, or, when it names none, for the runway whose centreline it lies nearest; a
    /// node nearer its runway's centreline than half the runway's width lies on the runway and is ignored. A runway with no
    /// hold-short node is left out. Built once per layout.
    /// </summary>
    private static List<GeoHoldBand> HoldBandsOf(AirportGroundLayout layout) => HoldBands.GetValue(layout, BuildHoldBands);

    private static List<GeoHoldBand> BuildHoldBands(AirportGroundLayout layout)
    {
        List<HoldBandRunway> runways =
        [
            .. layout
                .Edges.Where(e => e.IsRunwayCenterline)
                .GroupBy(e => e.TaxiwayName, StringComparer.OrdinalIgnoreCase)
                .Select(g => HoldBandRunwayOf(layout, g.Key, [.. g.SelectMany(e => Chords(EdgeGeometry.PointsFrom(e, e.Nodes[0])))])),
        ];
        if (runways.Count == 0)
        {
            return [];
        }

        var nearestByRunway = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode hold in layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort))
        {
            if (ProtectedRunway(hold, runways) is not { } protectedRunway)
            {
                Log.LogWarning("Hold-short node {Id} names runway {Runway}, which has no centreline edges; no hold band", hold.Id, hold.RunwayId);
                continue;
            }

            (HoldBandRunway runway, double holdFt) = protectedRunway;
            if (holdFt < runway.HalfWidthFt)
            {
                Log.LogDebug(
                    "Hold-short node {Id} lies {HoldFt:F1} ft from the {Runway} centreline, inside its {HalfWidthFt:F1} ft half-width; ignored",
                    hold.Id,
                    holdFt,
                    runway.Name,
                    runway.HalfWidthFt
                );
                continue;
            }

            nearestByRunway[runway.Name] = nearestByRunway.TryGetValue(runway.Name, out double held) ? Math.Min(held, holdFt) : holdFt;
        }

        return
        [
            .. runways
                .Where(r => nearestByRunway.ContainsKey(r.Name))
                .Select(r => new GeoHoldBand(r.Id.ToString(), nearestByRunway[r.Name], r.Pieces)),
        ];
    }

    /// <summary>
    /// A runway's centreline group named <paramref name="name"/> (<c>RWY</c> and the runway's identifier), with its
    /// identifier and half its width, from the layout runway of that identifier; a group with no such runway gets no
    /// half-width, so none of its holds is ignored.
    /// </summary>
    private static HoldBandRunway HoldBandRunwayOf(AirportGroundLayout layout, string name, List<(LatLon A, LatLon B)> pieces)
    {
        var id = RunwayIdentifier.Parse(name["RWY".Length..]);
        if (layout.Runways.FirstOrDefault(r => r.Id.Equals(id)) is not { } runway)
        {
            Log.LogWarning(
                "{Airport}: runway centreline {Name} has no runway of that identifier; none of its holds is ignored",
                layout.AirportId,
                name
            );
            return new HoldBandRunway(name, id, 0.0, pieces);
        }

        return new HoldBandRunway(name, id, runway.WidthFt / 2.0, pieces);
    }

    /// <summary>
    /// The runway <paramref name="hold"/> protects and its distance from that runway's centreline, feet: the runway its
    /// <see cref="GroundNode.RunwayId"/> names, else the runway whose centreline it lies nearest. Null when it names a
    /// runway with no centreline edges.
    /// </summary>
    private static (HoldBandRunway Runway, double HoldFt)? ProtectedRunway(GroundNode hold, List<HoldBandRunway> runways)
    {
        (HoldBandRunway Runway, double HoldFt)? nearest = null;
        foreach (HoldBandRunway runway in runways)
        {
            if ((hold.RunwayId is { } named) && !runway.Id.Equals(named))
            {
                continue;
            }

            double holdFt = runway.Pieces.Min(p => FlatDistanceFt(hold.Position, p.A, p.B));
            if ((nearest is not { } held) || (holdFt < held.HoldFt))
            {
                nearest = (runway, holdFt);
            }
        }

        return nearest;
    }

    private static IEnumerable<(LatLon A, LatLon B)> Chords(List<LatLon> points) => points.Zip(points.Skip(1));

    /// <summary>The distance from <paramref name="p"/> to the segment, feet, in a flat frame around <paramref name="a"/>.</summary>
    private static double FlatDistanceFt(LatLon p, LatLon a, LatLon b)
    {
        double northFtPerDeg = 60.0 * GeoMath.FeetPerNm;
        double eastFtPerDeg = northFtPerDeg * Math.Cos(a.Lat * DegToRad);
        var local = new Pt((p.Lon - a.Lon) * eastFtPerDeg, (p.Lat - a.Lat) * northFtPerDeg);
        var end = new Pt((b.Lon - a.Lon) * eastFtPerDeg, (b.Lat - a.Lat) * northFtPerDeg);
        return PointToSegmentFt(local, default, end);
    }

    /// <summary>
    /// Each movement-area taxiway the fuselage crosses where it may not, as a refusal, once per taxiway, in sample
    /// order: the first is the refusal of a plain tow. Pavement the goal names is exempt throughout, and the
    /// pavement at either end over its end run — except at a marked point, which arrives on no pavement: its end
    /// footprint, wings included, must lie across no movement-area edge at all, and the taxiway it lies across comes first.
    /// </summary>
    /// <param name="poses">The flown samples.</param>
    /// <param name="box">The prefilter box around them.</param>
    /// <param name="exemptNames">Pavement the goal names.</param>
    /// <param name="naming">How the move and its marked point are named, as for <see cref="Check"/>.</param>
    /// <param name="all">Null to stop at the first crossing; otherwise every crossing is added to it.</param>
    /// <returns>The first crossing, or null when there is none or <paramref name="all"/> collects them.</returns>
    private TugPathRefusal? MovementAreaCrossings(
        List<LocalPose> poses,
        Box box,
        IReadOnlySet<string> exemptNames,
        (string Subject, string? MarkedPoint) naming,
        List<TugPathRefusal>? all
    )
    {
        (string subject, string? markedPoint) = naming;
        _named.Clear();
        if ((MarkedPointCrossing(poses, box, naming) is { } onTaxiway) && StopsAt(onTaxiway, all))
        {
            return onTaxiway;
        }

        List<MovementEdge> movementEdges = MovementEdgesIn(box, exemptNames);
        Dictionary<int, List<int>> adjacency = AdjacencyOf(movementEdges);
        HashSet<int> leaving = PavementAt(poses[0], movementEdges, adjacency);

        // The pavement the move arrives on is the aircraft's own: a tug the last pull leaves on a taxiway the aircraft is
        // not across is on pavement the move was not sent to.
        HashSet<int> arriving = markedPoint is null ? PavementAt(poses[^1] with { Pulled = false }, movementEdges, adjacency) : [];
        var runs = new EndRuns(leaving, LeavingRunLength(poses, movementEdges, leaving), arriving, ArrivingRunStart(poses, movementEdges, arriving));
        for (int sample = 0; sample < poses.Count; sample++)
        {
            if (CrossingAt(poses, (sample, subject), (movementEdges, runs), all) is { } crossing)
            {
                return crossing;
            }
        }

        return null;
    }

    /// <summary>
    /// The taxiway a marked point's end footprint lies across, as a refusal, its name added to the names already met; null
    /// for every other goal or when the footprint lies across no movement-area edge.
    /// </summary>
    private TugPathRefusal? MarkedPointCrossing(List<LocalPose> poses, Box box, (string Subject, string? MarkedPoint) naming)
    {
        (string subject, string? markedPoint) = naming;
        if ((markedPoint is null) || (FootprintOnMovementArea(poses[^1], MovementEdgesIn(box, NoExemptNames)) is not { } under))
        {
            return null;
        }

        LogCrossing(subject, poses[^1], under);
        _named.Add(under.Name);
        return new TugPathRefusal(TugPathSeverity.MovementArea, $"Unable, {markedPoint} is on taxiway {under.Name}", under.Name);
    }

    /// <summary>
    /// The crossings of one sample's fuselage over taxiways not yet met, each name added to the names met: the first
    /// returned when <paramref name="all"/> is null, otherwise each added to it and null returned.
    /// </summary>
    private TugPathRefusal? CrossingAt(
        List<LocalPose> poses,
        (int Index, string Subject) sample,
        (List<MovementEdge> Edges, EndRuns Runs) pavement,
        List<TugPathRefusal>? all
    )
    {
        (Pt nose, Pt tail) = Fuselage(poses[sample.Index]);
        for (int e = 0; e < pavement.Edges.Count; e++)
        {
            MovementEdge edge = pavement.Edges[e];
            if (_named.Contains(edge.Name) || pavement.Runs.Exempts(sample.Index, e) || !Crosses(nose, tail, edge.Segment.A, edge.Segment.B))
            {
                continue;
            }

            LogCrossing(sample.Subject, poses[sample.Index], edge);
            _named.Add(edge.Name);
            var crossing = new TugPathRefusal(
                TugPathSeverity.MovementArea,
                $"Unable, {sample.Subject} would put the aircraft on taxiway {edge.Name}",
                edge.Name
            );
            if (StopsAt(crossing, all))
            {
                return crossing;
            }
        }

        return null;
    }

    /// <summary>Whether the scan stops at <paramref name="refusal"/>: it does when nothing collects them; otherwise it is collected.</summary>
    private static bool StopsAt(TugPathRefusal refusal, List<TugPathRefusal>? all)
    {
        if (all is null)
        {
            return true;
        }

        all.Add(refusal);
        return false;
    }

    /// <summary>
    /// The movement-area edges inside <paramref name="box"/> that carry none of <paramref name="exemptNames"/>, into the
    /// instance's working list: the caller is done with the last call's edges before it asks again.
    /// </summary>
    private List<MovementEdge> MovementEdgesIn(Box box, IReadOnlySet<string> exemptNames)
    {
        _movementEdges.Clear();
        for (int e = 0; e < _edges.Length; e++)
        {
            EdgeSegment edge = _edges[e];
            if (!box.Overlaps(edge.A, edge.B) || CarriesAny(_edgeNames[e], exemptNames) || (_movementAreaNames[e] is not { } name))
            {
                continue;
            }

            _movementEdges.Add(new MovementEdge(edge, name));
        }

        return _movementEdges;
    }

    private static bool CarriesAny(string[] names, IReadOnlySet<string> exemptNames)
    {
        foreach (string name in names)
        {
            if (exemptNames.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A push onto <paramref name="taxiway"/> whose centre — the reference point, the fuselage midpoint, at any sample
    /// including the end pose — reaches more than <see cref="MaxTaxiwayOvershootFt"/> past the taxiway's centreline on the
    /// side away from where the tow started, as a refusal naming the farthest reach. The centre, not the nose or tail: a
    /// straight push back across a taxiway ends with the centre on it and the tail half a fuselage past it by design.
    /// Each point is measured perpendicular to the line of the taxiway's straight edge nearest it; a tow starting on the
    /// centreline counts either side.
    /// </summary>
    private TugPathRefusal? TaxiwayOvershootRefusal(List<LocalPose> poses, Box box, IReadOnlySet<string> exemptNames, string taxiway, string subject)
    {
        LogOtherTaxiwaysSwept(poses, box, exemptNames, subject);
        List<EdgeSegment> centreline = CentrelineOf(taxiway);
        if (centreline.Count == 0)
        {
            Log.LogWarning("{Subject}: taxiway {Taxiway} has no straight edge to measure an overshoot from", subject, taxiway);
            return null;
        }

        Pt start = poses[0].Position;
        (double PastFt, LocalPose Pose) farthest = (0.0, poses[0]);
        foreach (LocalPose pose in poses)
        {
            double pastFt = PastCentrelineFt(pose.Position, start, centreline);
            if (pastFt > farthest.PastFt)
            {
                farthest = (pastFt, pose);
            }
        }

        double boundFt = _maxOvershootFt;
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "{Subject}: the centre reaches {PastFt:F1} ft past {Taxiway}'s centreline (move {Move} sample {Sample}); "
                    + "the bound is {BoundFt:F1} ft",
                subject,
                farthest.PastFt,
                taxiway,
                farthest.Pose.MoveIndex + 1,
                farthest.Pose.SampleIndex,
                boundFt
            );
        }

        return farthest.PastFt > boundFt
            ? new TugPathRefusal(
                TugPathSeverity.TaxiwayOvershoot,
                $"Unable, {subject} would take the aircraft {farthest.PastFt:F0} ft past taxiway {taxiway}",
                taxiway
            )
            : null;
    }

    /// <summary>
    /// How far <paramref name="point"/> lies past the centreline, feet, measured perpendicular to the line of the
    /// centreline edge nearest it: zero when it is on the same side of that line as <paramref name="start"/>, unless the
    /// start lies within <see cref="OnCentrelineFt"/> of the line.
    /// </summary>
    private static double PastCentrelineFt(Pt point, Pt start, List<EdgeSegment> centreline)
    {
        EdgeSegment nearest = centreline[0];
        double nearestFt = PointToSegmentFt(point, nearest.A, nearest.B);
        for (int e = 1; e < centreline.Count; e++)
        {
            double edgeFt = PointToSegmentFt(point, centreline[e].A, centreline[e].B);
            if (edgeFt < nearestFt)
            {
                (nearest, nearestFt) = (centreline[e], edgeFt);
            }
        }

        Pt direction = nearest.B - nearest.A;
        double lengthFt = Distance(direction, default);
        double pointSideFt = Cross(direction, point - nearest.A) / lengthFt;
        double startSideFt = Cross(direction, start - nearest.A) / lengthFt;
        bool farSide = (Math.Abs(startSideFt) <= OnCentrelineFt) || (Math.Sign(pointSideFt) != Math.Sign(startSideFt));
        return farSide ? Math.Abs(pointSideFt) : 0.0;
    }

    /// <summary>The straight edges of <paramref name="taxiway"/> that have a length, in edge order; found once per name and kept.</summary>
    private List<EdgeSegment> CentrelineOf(string taxiway)
    {
        if (_centrelineByTaxiway.TryGetValue(taxiway, out List<EdgeSegment>? held))
        {
            return held;
        }

        var centreline = new List<EdgeSegment>();
        foreach (EdgeSegment edge in _edges)
        {
            if ((edge.Edge is GroundEdge) && edge.Edge.MatchesTaxiway(taxiway) && (Distance(edge.A, edge.B) > Epsilon))
            {
                centreline.Add(edge);
            }
        }

        _centrelineByTaxiway[taxiway] = centreline;
        return centreline;
    }

    /// <summary>Logs, for diagnostics only, the movement-area taxiways other than the goal's that a push onto a taxiway sweeps over.</summary>
    private void LogOtherTaxiwaysSwept(List<LocalPose> poses, Box box, IReadOnlySet<string> exemptNames, string subject)
    {
        if (!Log.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        List<MovementEdge> movementEdges = MovementEdgesIn(box, exemptNames);
        var swept = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LocalPose pose in poses)
        {
            (Pt nose, Pt tail) = Fuselage(pose);
            swept.UnionWith(movementEdges.Where(e => Crosses(nose, tail, e.Segment.A, e.Segment.B)).Select(e => e.Name));
        }

        Log.LogDebug(
            "{Subject}: the fuselage sweeps over movement-area taxiways {Names} (not refused: a push onto a taxiway is judged by its overshoot)",
            subject,
            swept.Count == 0 ? "none" : string.Join(", ", swept)
        );
    }

    /// <summary>
    /// The pavement the pose stands on: the edges the fuselage lies across, and the edges of the same movement area
    /// reached from their end nodes within one fuselage length of chain. The graph splits a taxiway's centreline at
    /// every junction, into stubs as short as a few feet — SFO's taxiway A runs through 5 ft and 7 ft pieces between
    /// the terminal alleys — so the one edge a fuselage happens to lie across is not the pavement it is standing on,
    /// and its neighbours in the chain are the same taxiway.
    /// </summary>
    private HashSet<int> PavementAt(LocalPose pose, List<MovementEdge> edges, Dictionary<int, List<int>> adjacency)
    {
        HashSet<int> pavement = CrossedAt(pose, edges);
        foreach (IGrouping<string, int> chain in pavement.ToList().GroupBy(e => edges[e].Name, StringComparer.OrdinalIgnoreCase))
        {
            WalkChain(edges, adjacency, chain, pavement);
        }

        return pavement;
    }

    /// <summary>
    /// Adds to <paramref name="pavement"/> the edges of one movement area reached from the end nodes of the seed
    /// edges within a fuselage length, walked as cumulative edge length each way. The edge that would take the walk
    /// past the bound is taken and the walk stops there: a 5 ft stub has to pull in the 69 ft neighbour it is part of.
    /// The bound is a fuselage length because that is about what it takes to clear a centreline — a B738 pulling off
    /// at 90° on its 51 ft radius is clear after roughly 65 ft — so a fuselage still across a taxiway further along
    /// the chain than its own length is transiting it, not leaving it.
    /// </summary>
    private void WalkChain(List<MovementEdge> edges, Dictionary<int, List<int>> adjacency, IEnumerable<int> seeds, HashSet<int> pavement)
    {
        double boundFt = 2.0 * _halfLengthFt;
        var reached = new Dictionary<int, double>();
        var queue = new Queue<int>();
        string? name = null;
        foreach (int seed in seeds)
        {
            name ??= edges[seed].Name;
            foreach (GroundNode node in edges[seed].Segment.Edge.Nodes)
            {
                if (reached.TryAdd(node.Id, 0.0))
                {
                    queue.Enqueue(node.Id);
                }
            }
        }

        while (queue.Count > 0)
        {
            int nodeId = queue.Dequeue();
            double fromSeedFt = reached[nodeId];
            if (fromSeedFt >= boundFt)
            {
                continue;
            }

            foreach (int e in adjacency.TryGetValue(nodeId, out List<int>? touching) ? touching : [])
            {
                if (name!.Equals(edges[e].Name, StringComparison.OrdinalIgnoreCase))
                {
                    pavement.Add(e);
                    Step(edges[e], nodeId, fromSeedFt, reached, queue);
                }
            }
        }
    }

    /// <summary>
    /// Walks an edge from a node reached at <paramref name="fromSeedFt"/>, queueing its far node when that is nearer than
    /// any walk before.
    /// </summary>
    private static void Step(MovementEdge edge, int nodeId, double fromSeedFt, Dictionary<int, double> reached, Queue<int> queue)
    {
        GroundNode far = edge.Segment.Edge.Nodes[0].Id == nodeId ? edge.Segment.Edge.Nodes[1] : edge.Segment.Edge.Nodes[0];
        double farFt = fromSeedFt + Distance(edge.Segment.A, edge.Segment.B);
        if (reached.TryGetValue(far.Id, out double held) && (held <= farFt))
        {
            return;
        }

        reached[far.Id] = farFt;
        queue.Enqueue(far.Id);
    }

    /// <summary>The movement edges touching each node, by node id.</summary>
    private static Dictionary<int, List<int>> AdjacencyOf(List<MovementEdge> edges)
    {
        var adjacency = new Dictionary<int, List<int>>();
        for (int e = 0; e < edges.Count; e++)
        {
            foreach (GroundNode node in edges[e].Segment.Edge.Nodes)
            {
                if (!adjacency.TryGetValue(node.Id, out List<int>? touching))
                {
                    touching = [];
                    adjacency[node.Id] = touching;
                }

                touching.Add(e);
            }
        }

        return adjacency;
    }

    /// <summary>The indices of the edges the pose's fuselage lies across.</summary>
    private HashSet<int> CrossedAt(LocalPose pose, List<MovementEdge> edges)
    {
        (Pt nose, Pt tail) = Fuselage(pose);
        var crossed = new HashSet<int>();
        for (int e = 0; e < edges.Count; e++)
        {
            if (Crosses(nose, tail, edges[e].Segment.A, edges[e].Segment.B))
            {
                crossed.Add(e);
            }
        }

        return crossed;
    }

    /// <summary>Whether the pose's fuselage lies across any of the edges <paramref name="among"/> indexes.</summary>
    private bool CrossesAny(LocalPose pose, List<MovementEdge> edges, HashSet<int> among)
    {
        (Pt nose, Pt tail) = Fuselage(pose);
        foreach (int e in among)
        {
            if (Crosses(nose, tail, edges[e].Segment.A, edges[e].Segment.B))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How many samples, from the first, the fuselage is still across pavement it started across: the run over which
    /// the leg is leaving that pavement. It ends at the first sample across none of it, whatever the distance flown.
    /// </summary>
    private int LeavingRunLength(List<LocalPose> poses, List<MovementEdge> edges, HashSet<int> among)
    {
        int count = 0;
        while ((count < poses.Count) && CrossesAny(poses[count], edges, among))
        {
            count++;
        }

        return count;
    }

    /// <summary>The first sample of the unbroken run back from the last over which the fuselage is across the pavement it ends across.</summary>
    private int ArrivingRunStart(List<LocalPose> poses, List<MovementEdge> edges, HashSet<int> among)
    {
        int start = poses.Count;
        while ((start > 0) && CrossesAny(poses[start - 1], edges, among))
        {
            start--;
        }

        return start;
    }

    private static void LogCrossing(string subject, LocalPose pose, MovementEdge edge) =>
        Log.LogDebug(
            "{Subject}: move {Move} sample {Sample} (about {AlongFt:F0} ft along it), {EastFt:F0} ft east and {NorthFt:F0} ft "
                + "north of the plan's start, nose {NoseDeg:F1}°: the fuselage crosses {Name} edge {NodeA}-{NodeB} ({EdgeNames})",
            subject,
            pose.MoveIndex + 1,
            pose.SampleIndex,
            pose.SampleIndex * SampleSpacingFt,
            pose.Position.X,
            pose.Position.Y,
            new TrueHeading(pose.NoseRad / DegToRad).Degrees,
            edge.Name,
            edge.Segment.Edge.Nodes[0].Id,
            edge.Segment.Edge.Nodes[1].Id,
            string.Join("/", RampLaneReposition.EdgeNames(edge.Segment.Edge))
        );

    /// <summary>
    /// The fuselage segment, nose to tail; on a pull carried <see cref="GroundOutline.TugLeadFt"/> further ahead of the
    /// nose for the tug and towbar leading it, so a pull cannot put the tug on pavement the aircraft itself stays off.
    /// </summary>
    private (Pt Nose, Pt Tail) Fuselage(LocalPose pose)
    {
        Pt ahead = Ahead(pose);
        double noseFt = _halfLengthFt + (pose.Pulled ? GroundOutline.TugLeadFt : 0.0);
        var nose = new Pt(Math.Sin(pose.NoseRad) * noseFt, Math.Cos(pose.NoseRad) * noseFt);
        return (pose.Position + nose, pose.Position - ahead);
    }

    /// <summary>
    /// The first of <paramref name="edges"/> the pose's footprint (fuselage length × wingspan) lies across — an edge
    /// crossing its outline or starting inside it — or null.
    /// </summary>
    private MovementEdge? FootprintOnMovementArea(LocalPose pose, List<MovementEdge> edges)
    {
        Sides sides = FootprintSides(pose);
        foreach (MovementEdge edge in edges)
        {
            if (InsideFootprint(pose, edge.Segment.A) || SideCrosses(sides, edge.Segment.A, edge.Segment.B))
            {
                return edge;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="point"/> lies inside the pose's footprint, measured along and across the nose.</summary>
    private bool InsideFootprint(LocalPose pose, Pt point)
    {
        Pt offset = point - pose.Position;
        double alongFt = (offset.X * Math.Sin(pose.NoseRad)) + (offset.Y * Math.Cos(pose.NoseRad));
        double acrossFt = (offset.X * Math.Cos(pose.NoseRad)) - (offset.Y * Math.Sin(pose.NoseRad));
        return (Math.Abs(alongFt) <= _halfLengthFt) && (Math.Abs(acrossFt) <= _halfSpanFt);
    }

    /// <summary>
    /// Why a marked point may not be a tug move's goal, or null: it lies within a runway's half-width of its centreline,
    /// within <see cref="TugPlanBuilder.OnTaxiwayCorridorFt"/> of a runway holding position, or within that corridor of a
    /// movement-area taxiway's centreline — checked in that order, so a holding position on a taxiway is named as one.
    /// </summary>
    /// <param name="point">The marked point.</param>
    /// <param name="reachName">How the refusal names it: <c>the marked point</c>, <c>leg 2: marked point 1</c>.</param>
    /// <returns>The refusal, or null.</returns>
    internal string? MarkedPointRefusal(LatLon point, string reachName)
    {
        Pt p = Local(point);
        foreach (RunwaySegment runway in _runways)
        {
            if (PointToSegmentFt(p, runway.A, runway.B) <= runway.HalfWidthFt)
            {
                return $"Unable, {reachName} is on runway {runway.Name}";
            }
        }

        foreach (Pt hold in _holdShorts)
        {
            if (Distance(p, hold) <= TugPlanBuilder.OnTaxiwayCorridorFt)
            {
                return $"Unable, {reachName} is a runway holding position";
            }
        }

        for (int e = 0; e < _edges.Length; e++)
        {
            if ((_movementAreaNames[e] is { } name) && (PointToSegmentFt(p, _edges[e].A, _edges[e].B) <= TugPlanBuilder.OnTaxiwayCorridorFt))
            {
                return $"Unable, {reachName} is on taxiway {name}";
            }
        }

        return null;
    }

    private Sides FootprintSides(LocalPose pose)
    {
        Pt ahead = Ahead(pose);
        var right = new Pt(Math.Cos(pose.NoseRad) * _halfSpanFt, -Math.Sin(pose.NoseRad) * _halfSpanFt);
        Pt noseRight = pose.Position + ahead + right;
        Pt noseLeft = pose.Position + ahead - right;
        Pt tailLeft = pose.Position - ahead - right;
        Pt tailRight = pose.Position - ahead + right;
        return new Sides((noseRight, noseLeft), (noseLeft, tailLeft), (tailLeft, tailRight), (tailRight, noseRight));
    }

    private Pt Ahead(LocalPose pose) => new(Math.Sin(pose.NoseRad) * _halfLengthFt, Math.Cos(pose.NoseRad) * _halfLengthFt);

    private Pt Local(LatLon point) => new((point.Lon - _origin.Lon) * _eastFtPerDeg, (point.Lat - _origin.Lat) * 60.0 * GeoMath.FeetPerNm);

    /// <summary>The inverse of <see cref="Local"/>.</summary>
    private LatLon Geo(Pt point) => new(_origin.Lat + (point.Y / (60.0 * GeoMath.FeetPerNm)), _origin.Lon + (point.X / _eastFtPerDeg));

    /// <summary>Every runway's centreline, piece by piece, in the local frame.</summary>
    private List<RunwaySegment> RunwaySegmentsOf(AirportGroundLayout layout)
    {
        var segments = new List<RunwaySegment>();
        foreach (GroundRunway runway in layout.Runways)
        {
            for (int i = 1; i < runway.Coordinates.Count; i++)
            {
                (double Lat, double Lon) a = runway.Coordinates[i - 1];
                (double Lat, double Lon) b = runway.Coordinates[i];
                segments.Add(new RunwaySegment(runway.Name, runway.WidthFt / 2.0, Local(new LatLon(a.Lat, a.Lon)), Local(new LatLon(b.Lat, b.Lon))));
            }
        }

        return segments;
    }

    /// <summary>The layout's hold bands with their centreline pieces in the local frame.</summary>
    private List<RunwayHoldBand> LocalHoldBands(List<GeoHoldBand> bands)
    {
        var local = new List<RunwayHoldBand>(bands.Count);
        foreach (GeoHoldBand band in bands)
        {
            var pieces = new List<(Pt A, Pt B)>(band.Pieces.Count);
            foreach ((LatLon a, LatLon b) in band.Pieces)
            {
                pieces.Add((Local(a), Local(b)));
            }

            local.Add(new RunwayHoldBand(band.Name, band.HoldFt, pieces));
        }

        return local;
    }

    private static double Distance(Pt a, Pt b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double Orient(Pt a, Pt b, Pt p) => ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    /// <summary>Whether two segments share a point, touching included.</summary>
    private static bool Crosses(Pt p1, Pt p2, Pt q1, Pt q2)
    {
        double d1 = Orient(q1, q2, p1);
        double d2 = Orient(q1, q2, p2);
        double d3 = Orient(p1, p2, q1);
        double d4 = Orient(p1, p2, q2);
        if (((d1 * d2) < 0.0) && ((d3 * d4) < 0.0))
        {
            return true;
        }

        return Touches(d1, q1, q2, p1) || Touches(d2, q1, q2, p2) || Touches(d3, p1, p2, q1) || Touches(d4, p1, p2, q2);
    }

    /// <summary>Whether <paramref name="p"/>, collinear with the segment by <paramref name="orientation"/>, lies on it.</summary>
    private static bool Touches(double orientation, Pt a, Pt b, Pt p) =>
        (Math.Abs(orientation) <= Epsilon) && Within(p.X, a.X, b.X) && Within(p.Y, a.Y, b.Y);

    private static bool Within(double value, double end1, double end2) =>
        (value >= (Math.Min(end1, end2) - Epsilon)) && (value <= (Math.Max(end1, end2) + Epsilon));

    /// <summary>
    /// Where along <paramref name="ray"/> (as a fraction of its length, from <paramref name="start"/>) the segment
    /// crosses it, or null when it does not or runs parallel to it.
    /// </summary>
    private static double? RayFraction(Pt start, Pt ray, Pt q1, Pt q2)
    {
        Pt segment = q2 - q1;
        double denominator = Cross(ray, segment);
        if (Math.Abs(denominator) <= Epsilon)
        {
            return null;
        }

        Pt offset = q1 - start;
        double alongRay = Cross(offset, segment) / denominator;
        double alongSegment = Cross(offset, ray) / denominator;
        bool crosses = (alongRay >= 0.0) && (alongRay <= 1.0) && (alongSegment >= 0.0) && (alongSegment <= 1.0);
        return crosses ? alongRay : null;
    }

    private static double Cross(Pt a, Pt b) => (a.X * b.Y) - (a.Y * b.X);

    private static double SegmentDistanceFt(Pt p1, Pt p2, Pt q1, Pt q2)
    {
        if (Crosses(p1, p2, q1, q2))
        {
            return 0.0;
        }

        double fromP = Math.Min(PointToSegmentFt(p1, q1, q2), PointToSegmentFt(p2, q1, q2));
        double fromQ = Math.Min(PointToSegmentFt(q1, p1, p2), PointToSegmentFt(q2, p1, p2));
        return Math.Min(fromP, fromQ);
    }

    private static double PointToSegmentFt(Pt p, Pt a, Pt b) => Distance(p, NearestPointOnSegment(p, a, b));

    private static Pt NearestPointOnSegment(Pt p, Pt a, Pt b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSq = (dx * dx) + (dy * dy);
        double t = lengthSq <= Epsilon ? 0.0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSq, 0.0, 1.0);
        return new Pt(a.X + (t * dx), a.Y + (t * dy));
    }

    /// <summary>A point in flat feet east (X) and north (Y) of the plan's start.</summary>
    private readonly record struct Pt(double X, double Y)
    {
        public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

        public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    }

    /// <summary>One flown sample in the local frame; <c>Pulled</c> when its move is a pull, so the tug leads the nose.</summary>
    private readonly record struct LocalPose(Pt Position, double NoseRad, int MoveIndex, int SampleIndex, bool Pulled);

    /// <summary>A movement-area edge the goal does not name, and the pavement it carries.</summary>
    private readonly record struct MovementEdge(EdgeSegment Segment, string Name);

    /// <summary>
    /// The pavement a goal's candidate is allowed to be across at either end of its own flown path: the pavement its
    /// first sample stands on, over the run of samples that are still across one of its edges, and the pavement its
    /// last sample stands on, over the run back to the first sample that is across one of its edges. Indices into
    /// the candidate's movement edges.
    /// </summary>
    private readonly record struct EndRuns(HashSet<int> Leaving, int LeavingEnd, HashSet<int> Arriving, int ArrivingStart)
    {
        internal bool Exempts(int sample, int edge) =>
            ((sample < LeavingEnd) && Leaving.Contains(edge)) || ((sample >= ArrivingStart) && Arriving.Contains(edge));
    }

    private sealed record RunwaySegment(string Name, double HalfWidthFt, Pt A, Pt B);

    /// <summary>A runway's display identifier, its centreline chords and its nearest hold-short distance, feet, in geographic coordinates.</summary>
    private sealed record GeoHoldBand(string Name, double HoldFt, List<(LatLon A, LatLon B)> Pieces);

    /// <summary>A runway's centreline group: its edge name, identifier, half its width, feet, and its chords.</summary>
    private sealed record HoldBandRunway(string Name, RunwayIdentifier Id, double HalfWidthFt, List<(LatLon A, LatLon B)> Pieces);

    /// <summary>A runway's name, its centreline chords in the local frame and its nearest hold-short distance, feet.</summary>
    private sealed record RunwayHoldBand(string Name, double HoldFt, List<(Pt A, Pt B)> Pieces);

    /// <summary>An edge as its chord in the local frame.</summary>
    private readonly record struct EdgeSegment(IGroundEdge Edge, Pt A, Pt B);

    /// <summary>A footprint's four sides, nose, left, tail and right, each corner to corner.</summary>
    private readonly record struct Sides((Pt A, Pt B) Nose, (Pt A, Pt B) Left, (Pt A, Pt B) Tail, (Pt A, Pt B) Right)
    {
        internal const int Count = 4;

        internal (Pt A, Pt B) this[int index] =>
            index switch
            {
                0 => Nose,
                1 => Left,
                2 => Tail,
                _ => Right,
            };
    }

    /// <summary>An axis-aligned box in the local frame, used to skip pavement nowhere near the path.</summary>
    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        /// <summary>The box around the (non-empty) poses' positions.</summary>
        public static Box Around(List<LocalPose> poses)
        {
            Pt first = poses[0].Position;
            (double minX, double minY, double maxX, double maxY) = (first.X, first.Y, first.X, first.Y);
            for (int i = 1; i < poses.Count; i++)
            {
                Pt p = poses[i].Position;
                minX = p.X < minX ? p.X : minX;
                minY = p.Y < minY ? p.Y : minY;
                maxX = p.X > maxX ? p.X : maxX;
                maxY = p.Y > maxY ? p.Y : maxY;
            }

            return new Box(minX, minY, maxX, maxY);
        }

        public Box Padded(double byFt) => new(MinX - byFt, MinY - byFt, MaxX + byFt, MaxY + byFt);

        public bool Overlaps(Pt a, Pt b) =>
            (Math.Max(a.X, b.X) >= MinX) && (Math.Min(a.X, b.X) <= MaxX) && (Math.Max(a.Y, b.Y) >= MinY) && (Math.Min(a.Y, b.Y) <= MaxY);
    }
}
