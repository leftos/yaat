using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>Which flown-path rule a refusal comes from; a lower value is more severe.</summary>
internal enum TugPathSeverity
{
    Runway,
    HoldingPosition,
    MovementArea,
    OffGraph,
}

/// <summary>A flown-path refusal and the rule it comes from.</summary>
internal readonly record struct TugPathRefusal(TugPathSeverity Severity, string Message);

/// <summary>
/// Checks a candidate's flown path, sample by sample, against the layout: the footprint (fuselage length ×
/// wingspan about the reference point, oriented by the nose) must stay more than a runway's half-width from its
/// centreline and must not cross an edge touching a runway holding position; the fuselage centreline must not
/// cross movement-area pavement the goal does not name, except within 25 ft of the plan's start or the goal's
/// end; and the reference point must stay within <see cref="MaxOffGraphFt"/> of some ground-graph edge. Edges are
/// taken as their chords. Works in flat feet east and north of the plan's start, the frame
/// <c>GeoMath.ProjectPoint</c> steps in. Holds one plan's precomputed segments and memoized pavement verdicts, so
/// one instance serves every candidate of a plan.
/// </summary>
internal sealed class TugPathCheck
{
    /// <summary>
    /// How far a sample's reference point may lie from the nearest ground-graph edge of any type (ramp lane,
    /// parking stub or taxiway), feet; a judgement call. The layout carries no pavement polygons, so distance from
    /// the graph stands in for "on pavement".
    /// </summary>
    internal const double MaxOffGraphFt = 100.0;

    private const double Epsilon = 1e-9;

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
        _runways = layout.Runways.SelectMany(RunwaySegments).ToList();
        _edges = layout.AllEdges.Select(EdgeSegmentOf).ToList();
    }

    /// <summary>
    /// The first rule the path breaks — runway, then holding position, then movement area, then off the ground
    /// graph — or null when clear.
    /// </summary>
    internal TugPathRefusal? Check(IReadOnlyList<TugMoveTrace> traces, TugPose goalEnd, IReadOnlySet<string> exemptNames, string subject)
    {
        var poses = traces.SelectMany(t => t.Samples).Select(p => new LocalPose(Local(p.Position), p.NoseTrueDeg * DegToRad)).ToList();
        if (poses.Count == 0)
        {
            return null;
        }

        // The prefilter reaches as far as the footprint does and as far as the off-graph test looks.
        var box = Box.Around(poses).Padded(Math.Max((2.0 * _halfLengthFt) + (2.0 * _halfSpanFt), MaxOffGraphFt));
        return RunwayRefusal(poses, box, subject)
            ?? HoldingPositionRefusal(poses, box, subject)
            ?? MovementAreaRefusal(poses, box, Local(goalEnd.Position), exemptNames, subject)
            ?? OffGraphRefusal(poses, box, subject);
    }

    private TugPathRefusal? RunwayRefusal(List<LocalPose> poses, Box box, string subject)
    {
        foreach (var runway in _runways.Where(r => box.Padded(r.HalfWidthFt).Overlaps(r.A, r.B)))
        {
            foreach (var pose in poses)
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
        foreach (var pose in poses)
        {
            foreach (var side in FootprintSides(pose))
            {
                if (holdEdges.Any(e => Crosses(side.A, side.B, e.A, e.B)))
                {
                    return new TugPathRefusal(TugPathSeverity.HoldingPosition, $"Unable, {subject} reaches a runway holding position");
                }
            }
        }

        return null;
    }

    private TugPathRefusal? MovementAreaRefusal(List<LocalPose> poses, Box box, Pt goalEnd, IReadOnlySet<string> exemptNames, string subject)
    {
        var movementEdges = _edges
            .Where(e => box.Overlaps(e.A, e.B) && !RampLaneReposition.EdgeNames(e.Edge).Any(exemptNames.Contains))
            .Select(e => (Segment: e, Name: _pavement.MovementAreaName(e.Edge)))
            .Where(e => e.Name is not null)
            .ToList();
        foreach (var pose in poses.Where(p => !IsNearStartOrEnd(p.Position, goalEnd)))
        {
            var (nose, tail) = Fuselage(pose);
            foreach (var (segment, name) in movementEdges)
            {
                if (Crosses(nose, tail, segment.A, segment.B))
                {
                    return new TugPathRefusal(TugPathSeverity.MovementArea, $"Unable, {subject} would put the aircraft on taxiway {name}");
                }
            }
        }

        return null;
    }

    private TugPathRefusal? OffGraphRefusal(List<LocalPose> poses, Box box, string subject)
    {
        var nearbyEdges = _edges.Where(e => box.Overlaps(e.A, e.B)).ToList();
        foreach (var pose in poses)
        {
            if (!nearbyEdges.Any(e => PointToSegmentFt(pose.Position, e.A, e.B) <= MaxOffGraphFt))
            {
                Log.LogDebug(
                    "{Subject}: the sample {EastFt:F0} ft east and {NorthFt:F0} ft north of the plan's start is {DistanceFt:F0} ft from the nearest ground-graph edge",
                    subject,
                    pose.Position.X,
                    pose.Position.Y,
                    nearbyEdges.Count == 0 ? double.PositiveInfinity : nearbyEdges.Min(e => PointToSegmentFt(pose.Position, e.A, e.B))
                );
                return new TugPathRefusal(TugPathSeverity.OffGraph, $"Unable, {subject} would leave the ramp");
            }
        }

        return null;
    }

    /// <summary>Pavement within the window of the plan's start is being left; within the window of the goal's end, reached.</summary>
    private static bool IsNearStartOrEnd(Pt position, Pt goalEnd) =>
        (Distance(position, default) <= TugPavementClassifier.MovementAreaEndWindowFt)
        || (Distance(position, goalEnd) <= TugPavementClassifier.MovementAreaEndWindowFt);

    private (Pt Nose, Pt Tail) Fuselage(LocalPose pose)
    {
        var ahead = Ahead(pose);
        return (pose.Position + ahead, pose.Position - ahead);
    }

    private (Pt A, Pt B)[] FootprintSides(LocalPose pose)
    {
        var ahead = Ahead(pose);
        var right = new Pt(Math.Cos(pose.NoseRad) * _halfSpanFt, -Math.Sin(pose.NoseRad) * _halfSpanFt);
        var noseRight = pose.Position + ahead + right;
        var noseLeft = pose.Position + ahead - right;
        var tailLeft = pose.Position - ahead - right;
        var tailRight = pose.Position - ahead + right;
        return [(noseRight, noseLeft), (noseLeft, tailLeft), (tailLeft, tailRight), (tailRight, noseRight)];
    }

    private Pt Ahead(LocalPose pose) => new(Math.Sin(pose.NoseRad) * _halfLengthFt, Math.Cos(pose.NoseRad) * _halfLengthFt);

    private Pt Local(LatLon point) => new((point.Lon - _origin.Lon) * _eastFtPerDeg, (point.Lat - _origin.Lat) * 60.0 * GeoMath.FeetPerNm);

    private IEnumerable<RunwaySegment> RunwaySegments(GroundRunway runway)
    {
        for (int i = 1; i < runway.Coordinates.Count; i++)
        {
            var a = runway.Coordinates[i - 1];
            var b = runway.Coordinates[i];
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

    private static double PointToSegmentFt(Pt p, Pt a, Pt b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSq = (dx * dx) + (dy * dy);
        double t = lengthSq <= Epsilon ? 0.0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSq, 0.0, 1.0);
        return Distance(p, new Pt(a.X + (t * dx), a.Y + (t * dy)));
    }

    /// <summary>A point in flat feet east (X) and north (Y) of the plan's start.</summary>
    private readonly record struct Pt(double X, double Y)
    {
        public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

        public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    }

    private readonly record struct LocalPose(Pt Position, double NoseRad);

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
