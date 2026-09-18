using Yaat.Sim.Data.Airport;

namespace Yaat.Sim;

/// <summary>A point in flat feet east (X) and north (Y) of a <see cref="GroundOutlineFrame"/>'s origin.</summary>
internal readonly record struct OutlinePoint(double EastFt, double NorthFt)
{
    public static OutlinePoint operator +(OutlinePoint a, OutlinePoint b) => new(a.EastFt + b.EastFt, a.NorthFt + b.NorthFt);

    public static OutlinePoint operator -(OutlinePoint a, OutlinePoint b) => new(a.EastFt - b.EastFt, a.NorthFt - b.NorthFt);

    public static OutlinePoint operator *(double scale, OutlinePoint a) => new(scale * a.EastFt, scale * a.NorthFt);

    /// <summary>The distance between two points, feet.</summary>
    public static double Distance(OutlinePoint a, OutlinePoint b) =>
        Math.Sqrt(((a.EastFt - b.EastFt) * (a.EastFt - b.EastFt)) + ((a.NorthFt - b.NorthFt) * (a.NorthFt - b.NorthFt)));
}

/// <summary>One straight piece of an outline, between two points in a <see cref="GroundOutlineFrame"/>.</summary>
internal readonly record struct OutlineSegment(OutlinePoint A, OutlinePoint B);

/// <summary>
/// Flat feet east and north of an origin, for comparing outlines a few hundred feet apart: longitude is scaled by the
/// cosine of the origin's latitude, the same flat frame <c>GeoMath.ProjectPoint</c> steps in over that range.
/// </summary>
internal readonly record struct GroundOutlineFrame
{
    private const double FeetPerDegLat = 60.0 * GeoMath.FeetPerNm;

    private readonly LatLon _origin;
    private readonly double _feetPerDegLon;

    /// <summary>Creates a frame centred on <paramref name="origin"/>.</summary>
    /// <param name="origin">The point that maps to (0, 0).</param>
    public GroundOutlineFrame(LatLon origin)
    {
        _origin = origin;
        _feetPerDegLon = FeetPerDegLat * Math.Cos(origin.Lat * Math.PI / 180.0);
    }

    /// <summary>Where <paramref name="point"/> lies in this frame.</summary>
    /// <param name="point">A position near the origin.</param>
    /// <returns>Feet east and north of the origin.</returns>
    public OutlinePoint ToLocal(LatLon point) => new((point.Lon - _origin.Lon) * _feetPerDegLon, (point.Lat - _origin.Lat) * FeetPerDegLat);
}

/// <summary>
/// The size of an aircraft's outline, feet: fuselage length, wingspan, and how far a tug and towbar reach ahead of the
/// nose (zero unless the aircraft is being pulled).
/// </summary>
internal readonly record struct GroundOutlineSize(double LengthFt, double WingspanFt, double NoseLeadFt)
{
    /// <summary>
    /// The size of <paramref name="aircraftType"/> from the FAA record, with the fallbacks <see cref="TugMovePlanner"/>
    /// uses when the record has no length or wingspan.
    /// </summary>
    /// <param name="aircraftType">ICAO type designator.</param>
    /// <param name="towedNoseFirst">The aircraft is on a pull, so the tug and towbar lead its nose.</param>
    /// <returns>The outline size.</returns>
    public static GroundOutlineSize Of(string aircraftType, bool towedNoseFirst) =>
        new(TugMovePlanner.FuselageLengthFt(aircraftType), TugMovePlanner.WingspanFt(aircraftType), towedNoseFirst ? GroundOutline.TugLeadFt : 0.0);

    /// <summary>
    /// The farthest any part of the outline lies from the reference point, feet: the nose (with the tug), a wingtip, or a
    /// tailplane tip.
    /// </summary>
    public double ReachFt
    {
        get
        {
            double halfLengthFt = LengthFt / 2.0;
            double tailplaneTipFt = Math.Sqrt((halfLengthFt * halfLengthFt) + Math.Pow(GroundOutline.TailplaneSpanFraction * WingspanFt, 2.0));
            return Math.Max(Math.Max(halfLengthFt + NoseLeadFt, WingspanFt / 2.0), tailplaneTipFt);
        }
    }
}

/// <summary>
/// An aircraft's plan-view outline for ground clearance: a cross of three segments about its reference point, in a
/// <see cref="GroundOutlineFrame"/>. <see cref="GroundConflictDetector"/> sweeps a tug-moved aircraft's outline along the
/// rest of its move and measures it against a parked or held neighbour's with <see cref="Clearance"/>.
///
/// <para>Every proportion is a judgement call:</para>
/// <list type="bullet">
/// <item>the fuselage runs from the tail, half its length behind the reference point along the nose heading, to the
/// nose, half its length ahead;</item>
/// <item>the wing spans half the wingspan either side of the reference point, square to the fuselage;</item>
/// <item>the tailplane spans <see cref="TailplaneSpanFraction"/> of the wingspan either side of the tail (a horizontal
/// stabiliser is roughly 40 % of the span on transport jets);</item>
/// <item>an aircraft being pulled has its fuselage segment carried <see cref="TugLeadFt"/> further forward, ahead of the
/// nose tip, for the tug and towbar. The 2026-09-16 aviation review's 30–40 ft is measured from the nose gear, which
/// sits well behind the tip (about 17 ft on a B738), so the outline overstates a towbar rig by roughly that much — a
/// pull only ever stops earlier for it.</item>
/// </list>
/// </summary>
/// <param name="Fuselage">Tail to nose, including the tug's lead on a pull.</param>
/// <param name="Wing">Left wingtip to right wingtip, through the reference point.</param>
/// <param name="Tailplane">Left tip to right tip, through the tail.</param>
internal readonly record struct GroundOutline(OutlineSegment Fuselage, OutlineSegment Wing, OutlineSegment Tailplane)
{
    /// <summary>Half the tailplane span as a fraction of the wingspan; a judgement call.</summary>
    public const double TailplaneSpanFraction = 0.2;

    /// <summary>How far the tug and towbar reach ahead of the nose on a pull, feet; a judgement call.</summary>
    public const double TugLeadFt = 30.0;

    private const double DegToRad = Math.PI / 180.0;
    private const double Epsilon = 1e-9;

    /// <summary>The outline of an aircraft of <paramref name="size"/> with its reference point at <paramref name="reference"/>.</summary>
    /// <param name="reference">The reference point, in the frame.</param>
    /// <param name="noseTrueDeg">The nose heading, degrees true.</param>
    /// <param name="size">The aircraft's outline size.</param>
    /// <returns>The outline.</returns>
    public static GroundOutline At(OutlinePoint reference, double noseTrueDeg, GroundOutlineSize size)
    {
        double noseRad = noseTrueDeg * DegToRad;
        var forward = new OutlinePoint(Math.Sin(noseRad), Math.Cos(noseRad));
        var right = new OutlinePoint(Math.Cos(noseRad), -Math.Sin(noseRad));
        double halfLengthFt = size.LengthFt / 2.0;
        OutlinePoint tail = reference - (halfLengthFt * forward);
        OutlinePoint nose = reference + ((halfLengthFt + size.NoseLeadFt) * forward);
        double halfSpanFt = size.WingspanFt / 2.0;
        double halfTailplaneFt = TailplaneSpanFraction * size.WingspanFt;
        return new GroundOutline(
            new OutlineSegment(tail, nose),
            new OutlineSegment(reference - (halfSpanFt * right), reference + (halfSpanFt * right)),
            new OutlineSegment(tail - (halfTailplaneFt * right), tail + (halfTailplaneFt * right))
        );
    }

    /// <summary>
    /// How close two outlines come, feet: the least distance between any segment of one and any segment of the other;
    /// zero when they touch or cross.
    /// </summary>
    /// <param name="a">One outline.</param>
    /// <param name="b">The other, in the same frame.</param>
    /// <returns>The clearance, feet.</returns>
    public static double Clearance(GroundOutline a, GroundOutline b)
    {
        double closestFt = double.MaxValue;
        foreach (OutlineSegment segmentA in a.Segments())
        {
            foreach (OutlineSegment segmentB in b.Segments())
            {
                closestFt = Math.Min(closestFt, SegmentDistanceFt(segmentA, segmentB));
                if (closestFt <= 0.0)
                {
                    return 0.0;
                }
            }
        }

        return closestFt;
    }

    /// <summary>
    /// How close two aircraft's outlines come where they stand, feet; zero when they touch or overlap. The frame is
    /// centred on <paramref name="a"/>, so the pair may sit anywhere on the field.
    /// </summary>
    /// <param name="a">One aircraft; its position is the frame's origin.</param>
    /// <param name="aTowedNoseFirst">This aircraft is on a pull, so a tug and towbar lead its nose.</param>
    /// <param name="b">The other aircraft.</param>
    /// <returns>The clearance between the two outlines, feet.</returns>
    public static double ClearanceBetween(AircraftState a, bool aTowedNoseFirst, AircraftState b)
    {
        var frame = new GroundOutlineFrame(a.Position);
        return Clearance(
            At(frame.ToLocal(a.Position), a.TrueHeading.Degrees, GroundOutlineSize.Of(a.AircraftType, aTowedNoseFirst)),
            At(frame.ToLocal(b.Position), b.TrueHeading.Degrees, GroundOutlineSize.Of(b.AircraftType, towedNoseFirst: false))
        );
    }

    private OutlineSegment[] Segments() => [Fuselage, Wing, Tailplane];

    /// <summary>The least distance between two segments, feet: zero when they cross, else the nearest endpoint-to-segment distance.</summary>
    private static double SegmentDistanceFt(OutlineSegment p, OutlineSegment q)
    {
        if (CrossProperly(p, q))
        {
            return 0.0;
        }

        double fromP = Math.Min(PointToSegmentFt(p.A, q), PointToSegmentFt(p.B, q));
        double fromQ = Math.Min(PointToSegmentFt(q.A, p), PointToSegmentFt(q.B, p));
        return Math.Min(fromP, fromQ);
    }

    /// <summary>
    /// Whether the segments cross at a point inside both. Touching and collinear overlap are left to the
    /// endpoint distances, which are zero in those cases.
    /// </summary>
    private static bool CrossProperly(OutlineSegment p, OutlineSegment q)
    {
        double d1 = Orient(q.A, q.B, p.A);
        double d2 = Orient(q.A, q.B, p.B);
        double d3 = Orient(p.A, p.B, q.A);
        double d4 = Orient(p.A, p.B, q.B);
        return ((d1 * d2) < 0.0) && ((d3 * d4) < 0.0);
    }

    private static double Orient(OutlinePoint a, OutlinePoint b, OutlinePoint p) =>
        ((b.EastFt - a.EastFt) * (p.NorthFt - a.NorthFt)) - ((b.NorthFt - a.NorthFt) * (p.EastFt - a.EastFt));

    private static double PointToSegmentFt(OutlinePoint p, OutlineSegment segment)
    {
        OutlinePoint along = segment.B - segment.A;
        double lengthSq = (along.EastFt * along.EastFt) + (along.NorthFt * along.NorthFt);
        double t =
            lengthSq <= Epsilon
                ? 0.0
                : Math.Clamp(
                    (((p.EastFt - segment.A.EastFt) * along.EastFt) + ((p.NorthFt - segment.A.NorthFt) * along.NorthFt)) / lengthSq,
                    0.0,
                    1.0
                );
        return OutlinePoint.Distance(p, segment.A + (t * along));
    }
}
