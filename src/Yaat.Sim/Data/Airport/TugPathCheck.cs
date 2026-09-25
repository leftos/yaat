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

    private readonly TugPavementClassifier _pavement;
    private readonly LatLon _origin;
    private readonly double _eastFtPerDeg;
    private readonly double _halfLengthFt;
    private readonly double _halfSpanFt;
    private readonly double _maxOvershootFt;
    private readonly List<RunwaySegment> _runways;
    private readonly List<EdgeSegment> _edges;

    /// <summary>Every runway holding position in the layout, in the local frame.</summary>
    private readonly List<Pt> _holdShorts;

    /// <summary>No pavement exempt: the end footprint of a marked point clears all of it.</summary>
    private static readonly HashSet<string> NoExemptNames = [];

    internal TugPathCheck(AirportGroundLayout layout, string aircraftType, LatLon planStart)
    {
        _pavement = new TugPavementClassifier(layout);
        _origin = planStart;
        _eastFtPerDeg = 60.0 * GeoMath.FeetPerNm * Math.Cos(planStart.Lat * DegToRad);
        _halfLengthFt = TugMovePlanner.FuselageLengthFt(aircraftType) / 2.0;
        _halfSpanFt = TugMovePlanner.WingspanFt(aircraftType) / 2.0;
        _maxOvershootFt = MaxTaxiwayOvershootFt(aircraftType);
        _runways = [.. layout.Runways.SelectMany(RunwaySegments)];
        _edges = [.. layout.AllEdges.Select(EdgeSegmentOf)];
        _holdShorts = [.. layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => Local(n.Position))];
    }

    /// <summary>
    /// How far past the centreline of the taxiway a push is sent onto, on the side away from where the tow started, the
    /// aircraft's centre (the reference point) may reach, feet: half the wingspan
    /// (<see cref="TugMovePlanner.WingspanFt"/>, which falls back by category when the type has none). A push onto a
    /// taxiway sweeps over the taxiways that meet it — at SFO D7, A and F1 meet at right angles, so lining up on A there
    /// lies across F1 — so it is judged by not going too far past its own taxiway, not by the pavement it crosses.
    /// Known limit: half a span at the centre can put the main gear past the edge of a narrow taxiway; the layout
    /// carries no pavement polygons to check the gear against.
    /// </summary>
    /// <param name="aircraftType">ICAO type designator.</param>
    /// <returns>The bound, feet.</returns>
    public static double MaxTaxiwayOvershootFt(string aircraftType) => TugMovePlanner.WingspanFt(aircraftType) / 2.0;

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
        TugPathRefusal? runwayOrHold = RunwayRefusal(poses, box, subject) ?? HoldingPositionRefusal(poses, box, subject);
        if (forced)
        {
            return runwayOrHold;
        }

        return runwayOrHold
            ?? (
                goalTaxiway is { } taxiway
                    ? TaxiwayOvershootRefusal(poses, box, exemptNames, taxiway, subject)
                    : MovementAreaCrossings(poses, box, exemptNames, subject, markedPoint).Select(r => (TugPathRefusal?)r).FirstOrDefault()
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
            return [.. MovementAreaCrossings(poses, box, exemptNames, naming.Subject, naming.MarkedPoint)];
        }

        return TaxiwayOvershootRefusal(poses, box, exemptNames, taxiway, naming.Subject) is { } overshoot ? [overshoot] : [];
    }

    private List<LocalPose> Poses(IReadOnlyList<TugMoveTrace> traces) =>
        [
            .. traces.SelectMany(
                (t, move) =>
                    t.Samples.Select(
                        (p, sample) => new LocalPose(Local(p.Position), p.NoseTrueDeg * DegToRad, move, sample, t.Move.Kind == PushbackLegKind.Pull)
                    )
            ),
        ];

    /// <summary>The prefilter box around the poses, reaching as far as the footprint does.</summary>
    private Box FootprintBox(List<LocalPose> poses) => Box.Around(poses).Padded((2.0 * _halfLengthFt) + (2.0 * _halfSpanFt));

    private static bool TouchesHoldShort(IGroundEdge edge) =>
        (edge.Nodes[0].Type == GroundNodeType.RunwayHoldShort) || (edge.Nodes[1].Type == GroundNodeType.RunwayHoldShort);

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
            Log.LogDebug(
                "Alongside search on {TravelDeg:F1}°: edge {NodeA}-{NodeB} nearest point {DistanceFt:F1} ft away, {AlongFt:F1} ft along; {Verdict}",
                travelTrueDeg,
                edge.Edge.Nodes[0].Id,
                edge.Edge.Nodes[1].Id,
                distanceFt,
                alongFt,
                lineTravel is { } deg ? $"runs on {deg:F1}°" : "runs across"
            );
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
        foreach (RunwaySegment? runway in _runways.Where(r => box.Padded(r.HalfWidthFt).Overlaps(r.A, r.B)))
        {
            foreach (LocalPose pose in poses)
            {
                if (FootprintSides(pose).Any(side => SegmentDistanceFt(side.A, side.B, runway.A, runway.B) <= runway.HalfWidthFt))
                {
                    return new TugPathRefusal(TugPathSeverity.Runway, $"Unable, {subject} would put the aircraft on runway {runway.Name}", null);
                }
            }
        }

        return null;
    }

    private TugPathRefusal? HoldingPositionRefusal(List<LocalPose> poses, Box box, string subject)
    {
        var holdEdges = _edges.Where(e => e.TouchesHoldShort && box.Overlaps(e.A, e.B)).ToList();
        foreach (LocalPose pose in poses)
        {
            foreach ((Pt A, Pt B) side in FootprintSides(pose))
            {
                if (holdEdges.Any(e => Crosses(side.A, side.B, e.A, e.B)))
                {
                    return new TugPathRefusal(TugPathSeverity.HoldingPosition, $"Unable, {subject} reaches a runway holding position", null);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Each movement-area taxiway the fuselage crosses where it may not, as a refusal, once per taxiway, lazily and in
    /// sample order: the first is the refusal of a plain tow. Pavement the goal names is exempt throughout, and the
    /// pavement at either end over its end run — except at a marked point, which arrives on no pavement: its end
    /// footprint, wings included, must lie across no movement-area edge at all, and the taxiway it lies across comes first.
    /// </summary>
    private IEnumerable<TugPathRefusal> MovementAreaCrossings(
        List<LocalPose> poses,
        Box box,
        IReadOnlySet<string> exemptNames,
        string subject,
        string? markedPoint
    )
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if ((markedPoint is not null) && (FootprintOnMovementArea(poses[^1], MovementEdgesIn(box, NoExemptNames)) is { } under))
        {
            LogCrossing(subject, poses[^1], under);
            named.Add(under.Name);
            yield return new TugPathRefusal(TugPathSeverity.MovementArea, $"Unable, {markedPoint} is on taxiway {under.Name}", under.Name);
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
            (Pt nose, Pt tail) = Fuselage(poses[sample]);
            for (int e = 0; e < movementEdges.Count; e++)
            {
                MovementEdge edge = movementEdges[e];
                if (named.Contains(edge.Name) || runs.Exempts(sample, e) || !Crosses(nose, tail, edge.Segment.A, edge.Segment.B))
                {
                    continue;
                }

                LogCrossing(subject, poses[sample], edge);
                named.Add(edge.Name);
                yield return new TugPathRefusal(
                    TugPathSeverity.MovementArea,
                    $"Unable, {subject} would put the aircraft on taxiway {edge.Name}",
                    edge.Name
                );
            }
        }
    }

    /// <summary>The movement-area edges inside <paramref name="box"/> that carry none of <paramref name="exemptNames"/>.</summary>
    private List<MovementEdge> MovementEdgesIn(Box box, IReadOnlySet<string> exemptNames) =>
        [
            .. _edges
                .Where(e => box.Overlaps(e.A, e.B) && !RampLaneReposition.EdgeNames(e.Edge).Any(exemptNames.Contains))
                .Select(e => (Segment: e, Name: _pavement.MovementAreaName(e.Edge)))
                .Where(e => e.Name is not null)
                .Select(e => new MovementEdge(e.Segment, e.Name!)),
        ];

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
        List<EdgeSegment> centreline =
        [
            .. _edges.Where(e => (e.Edge is GroundEdge) && e.Edge.MatchesTaxiway(taxiway) && (Distance(e.A, e.B) > Epsilon)),
        ];
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
        Log.LogDebug(
            "{Subject}: the centre reaches {PastFt:F1} ft past {Taxiway}'s centreline (move {Move} sample {Sample}); the bound is {BoundFt:F1} ft",
            subject,
            farthest.PastFt,
            taxiway,
            farthest.Pose.MoveIndex + 1,
            farthest.Pose.SampleIndex,
            boundFt
        );
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
        EdgeSegment nearest = centreline.MinBy(e => PointToSegmentFt(point, e.A, e.B))!;
        Pt direction = nearest.B - nearest.A;
        double lengthFt = Distance(direction, default);
        double pointSideFt = Cross(direction, point - nearest.A) / lengthFt;
        double startSideFt = Cross(direction, start - nearest.A) / lengthFt;
        bool farSide = (Math.Abs(startSideFt) <= OnCentrelineFt) || (Math.Sign(pointSideFt) != Math.Sign(startSideFt));
        return farSide ? Math.Abs(pointSideFt) : 0.0;
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
        return among.Any(e => Crosses(nose, tail, edges[e].Segment.A, edges[e].Segment.B));
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
        (Pt A, Pt B)[] sides = FootprintSides(pose);
        foreach (MovementEdge edge in edges)
        {
            if (InsideFootprint(pose, edge.Segment.A) || sides.Any(side => Crosses(side.A, side.B, edge.Segment.A, edge.Segment.B)))
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
        if (_runways.FirstOrDefault(r => PointToSegmentFt(p, r.A, r.B) <= r.HalfWidthFt) is { } runway)
        {
            return $"Unable, {reachName} is on runway {runway.Name}";
        }

        if (_holdShorts.Any(hold => Distance(p, hold) <= TugPlanBuilder.OnTaxiwayCorridorFt))
        {
            return $"Unable, {reachName} is a runway holding position";
        }

        foreach (EdgeSegment edge in _edges)
        {
            if ((_pavement.MovementAreaName(edge.Edge) is { } name) && (PointToSegmentFt(p, edge.A, edge.B) <= TugPlanBuilder.OnTaxiwayCorridorFt))
            {
                return $"Unable, {reachName} is on taxiway {name}";
            }
        }

        return null;
    }

    private (Pt A, Pt B)[] FootprintSides(LocalPose pose)
    {
        Pt ahead = Ahead(pose);
        var right = new Pt(Math.Cos(pose.NoseRad) * _halfSpanFt, -Math.Sin(pose.NoseRad) * _halfSpanFt);
        Pt noseRight = pose.Position + ahead + right;
        Pt noseLeft = pose.Position + ahead - right;
        Pt tailLeft = pose.Position - ahead - right;
        Pt tailRight = pose.Position - ahead + right;
        return [(noseRight, noseLeft), (noseLeft, tailLeft), (tailLeft, tailRight), (tailRight, noseRight)];
    }

    private Pt Ahead(LocalPose pose) => new(Math.Sin(pose.NoseRad) * _halfLengthFt, Math.Cos(pose.NoseRad) * _halfLengthFt);

    private Pt Local(LatLon point) => new((point.Lon - _origin.Lon) * _eastFtPerDeg, (point.Lat - _origin.Lat) * 60.0 * GeoMath.FeetPerNm);

    /// <summary>The inverse of <see cref="Local"/>.</summary>
    private LatLon Geo(Pt point) => new(_origin.Lat + (point.Y / (60.0 * GeoMath.FeetPerNm)), _origin.Lon + (point.X / _eastFtPerDeg));

    private IEnumerable<RunwaySegment> RunwaySegments(GroundRunway runway)
    {
        for (int i = 1; i < runway.Coordinates.Count; i++)
        {
            (double Lat, double Lon) a = runway.Coordinates[i - 1];
            (double Lat, double Lon) b = runway.Coordinates[i];
            yield return new RunwaySegment(runway.Name, runway.WidthFt / 2.0, Local(new LatLon(a.Lat, a.Lon)), Local(new LatLon(b.Lat, b.Lon)));
        }
    }

    private EdgeSegment EdgeSegmentOf(IGroundEdge edge) =>
        new(edge, Local(edge.Nodes[0].Position), Local(edge.Nodes[1].Position), TouchesHoldShort(edge));

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

    /// <summary>An edge as its chord in the local frame.</summary>
    private sealed record EdgeSegment(IGroundEdge Edge, Pt A, Pt B, bool TouchesHoldShort);

    /// <summary>An axis-aligned box in the local frame, used to skip pavement nowhere near the path.</summary>
    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        public static Box Around(List<LocalPose> poses) =>
            new(poses.Min(p => p.Position.X), poses.Min(p => p.Position.Y), poses.Max(p => p.Position.X), poses.Max(p => p.Position.Y));

        public Box Padded(double byFt) => new(MinX - byFt, MinY - byFt, MaxX + byFt, MaxY + byFt);

        public bool Overlaps(Pt a, Pt b) =>
            (Math.Max(a.X, b.X) >= MinX) && (Math.Min(a.X, b.X) <= MaxX) && (Math.Max(a.Y, b.Y) >= MinY) && (Math.Min(a.Y, b.Y) <= MaxY);
    }
}
