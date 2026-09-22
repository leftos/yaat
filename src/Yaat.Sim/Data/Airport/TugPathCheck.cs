using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>Which flown-path rule a refusal comes from; a lower value is more severe.</summary>
internal enum TugPathSeverity
{
    Runway,
    HoldingPosition,
    MovementArea,
    ParkedNeighbour,
}

/// <summary>A flown-path refusal and the rule it comes from.</summary>
internal readonly record struct TugPathRefusal(TugPathSeverity Severity, string Message);

/// <summary>
/// Checks a candidate's flown path, sample by sample, against the layout: the footprint (fuselage length ×
/// wingspan about the reference point, oriented by the nose) must stay more than a runway's half-width from its
/// centreline and must not cross an edge touching a runway holding position; the fuselage centreline must not
/// cross movement-area pavement the goal does not name, except the pavement the goal's own first sample already
/// lies across, for the unbroken run of samples still across it, and the pavement its last sample lies across,
/// over the unbroken run back to it — leaving pavement and reaching it. That pavement is the edges the fuselage
/// lies across plus their chain of same-named neighbours within a fuselage length, because the graph cuts a
/// taxiway's centreline into a stub at every junction.
/// Open apron is not checked: the layout carries no pavement polygons, and real aprons have ungraphed
/// stretches wider than any distance-from-the-graph test could allow. Edges are taken as their chords. Works in
/// flat feet east and north of the plan's start, the frame <c>GeoMath.ProjectPoint</c> steps in. Holds one plan's
/// precomputed segments and memoized pavement verdicts, so one instance serves every candidate of a plan.
/// </summary>
internal sealed class TugPathCheck
{
    private const double Epsilon = 1e-9;

    /// <summary>The spacing of a trace's samples, feet, for naming where along a move a sample lies.</summary>
    private const double SampleSpacingFt = 5.0;

    /// <summary>How far past an edge's end a point may project and still count as on that edge, feet.</summary>
    private const double ExtentSlackFt = 1.0;

    private static readonly ILogger Log = SimLog.CreateLogger("TugPathCheck");
    private const double DegToRad = Math.PI / 180.0;

    private readonly TugPavementClassifier _pavement;
    private readonly LatLon _origin;
    private readonly double _eastFtPerDeg;
    private readonly double _halfLengthFt;
    private readonly double _halfSpanFt;
    private readonly List<RunwaySegment> _runways;
    private readonly List<EdgeSegment> _edges;

    internal TugPathCheck(AirportGroundLayout layout, string aircraftType, LatLon planStart)
    {
        _pavement = new TugPavementClassifier(layout);
        _origin = planStart;
        _eastFtPerDeg = 60.0 * GeoMath.FeetPerNm * Math.Cos(planStart.Lat * DegToRad);
        _halfLengthFt = TugMovePlanner.FuselageLengthFt(aircraftType) / 2.0;
        _halfSpanFt = TugMovePlanner.WingspanFt(aircraftType) / 2.0;
        _runways = [.. layout.Runways.SelectMany(RunwaySegments)];
        _edges = [.. layout.AllEdges.Select(EdgeSegmentOf)];
    }

    /// <summary>
    /// The first rule the path breaks — runway, then holding position, then movement area — or null when clear.
    /// </summary>
    internal TugPathRefusal? Check(IReadOnlyList<TugMoveTrace> traces, IReadOnlySet<string> exemptNames, string subject)
    {
        var poses = traces
            .SelectMany((t, move) => t.Samples.Select((p, sample) => new LocalPose(Local(p.Position), p.NoseTrueDeg * DegToRad, move, sample)))
            .ToList();
        if (poses.Count == 0)
        {
            return null;
        }

        // The prefilter reaches as far as the footprint does.
        Box box = Box.Around(poses).Padded((2.0 * _halfLengthFt) + (2.0 * _halfSpanFt));
        return RunwayRefusal(poses, box, subject)
            ?? HoldingPositionRefusal(poses, box, subject)
            ?? MovementAreaRefusal(poses, box, exemptNames, subject);
    }

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
                "Extent check: edge {NodeA}-{NodeB} is {LengthFt:F1} ft long; the point lies {AlongFt:F1} ft along it and {CrossFt:F1} ft off its line — {Verdict}",
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
                    return new TugPathRefusal(TugPathSeverity.Runway, $"Unable, {subject} would put the aircraft on runway {runway.Name}");
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
                    return new TugPathRefusal(TugPathSeverity.HoldingPosition, $"Unable, {subject} reaches a runway holding position");
                }
            }
        }

        return null;
    }

    private TugPathRefusal? MovementAreaRefusal(List<LocalPose> poses, Box box, IReadOnlySet<string> exemptNames, string subject)
    {
        var movementEdges = _edges
            .Where(e => box.Overlaps(e.A, e.B) && !RampLaneReposition.EdgeNames(e.Edge).Any(exemptNames.Contains))
            .Select(e => (Segment: e, Name: _pavement.MovementAreaName(e.Edge)))
            .Where(e => e.Name is not null)
            .Select(e => new MovementEdge(e.Segment, e.Name!))
            .ToList();
        Dictionary<int, List<int>> adjacency = AdjacencyOf(movementEdges);
        HashSet<int> leaving = PavementAt(poses[0], movementEdges, adjacency);
        HashSet<int> arriving = PavementAt(poses[^1], movementEdges, adjacency);
        var runs = new EndRuns(leaving, LeavingRunLength(poses, movementEdges, leaving), arriving, ArrivingRunStart(poses, movementEdges, arriving));
        for (int sample = 0; sample < poses.Count; sample++)
        {
            (Pt nose, Pt tail) = Fuselage(poses[sample]);
            for (int e = 0; e < movementEdges.Count; e++)
            {
                MovementEdge edge = movementEdges[e];
                if (runs.Exempts(sample, e) || !Crosses(nose, tail, edge.Segment.A, edge.Segment.B))
                {
                    continue;
                }

                LogCrossing(subject, poses[sample], edge);
                return new TugPathRefusal(TugPathSeverity.MovementArea, $"Unable, {subject} would put the aircraft on taxiway {edge.Name}");
            }
        }

        return null;
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

    /// <summary>Walks an edge from a node reached at <paramref name="fromSeedFt"/>, queueing its far node when that is nearer than any walk before.</summary>
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

    private (Pt Nose, Pt Tail) Fuselage(LocalPose pose)
    {
        Pt ahead = Ahead(pose);
        return (pose.Position + ahead, pose.Position - ahead);
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

    private EdgeSegment EdgeSegmentOf(IGroundEdge edge)
    {
        bool touchesHoldShort = (edge.Nodes[0].Type == GroundNodeType.RunwayHoldShort) || (edge.Nodes[1].Type == GroundNodeType.RunwayHoldShort);
        return new EdgeSegment(edge, Local(edge.Nodes[0].Position), Local(edge.Nodes[1].Position), touchesHoldShort);
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

    private readonly record struct LocalPose(Pt Position, double NoseRad, int MoveIndex, int SampleIndex);

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
