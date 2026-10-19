namespace Yaat.Sim.Data.Airport;

/// <summary>
/// <see cref="GroundOutline.Clearance"/> without its per-call segment arrays, for the tug planner's per-pose checks: the
/// same segments in the same order and the same arithmetic, so it returns the same figure to the bit.
/// </summary>
internal static class TugOutlineClearance
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// How close two outlines come, feet: the least distance between any segment of one and any segment of the other;
    /// zero when they touch or cross.
    /// </summary>
    /// <param name="a">One outline.</param>
    /// <param name="b">The other, in the same frame.</param>
    /// <returns>The clearance, feet.</returns>
    internal static double Clearance(GroundOutline a, GroundOutline b)
    {
        double closestFt = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            OutlineSegment segmentA = SegmentOf(a, i);
            for (int j = 0; j < 3; j++)
            {
                closestFt = Math.Min(closestFt, SegmentDistanceFt(segmentA, SegmentOf(b, j)));
                if (closestFt <= 0.0)
                {
                    return 0.0;
                }
            }
        }

        return closestFt;
    }

    /// <summary>The outline's segments in <see cref="GroundOutline"/>'s order: fuselage, wing, tailplane.</summary>
    private static OutlineSegment SegmentOf(GroundOutline outline, int index) =>
        index switch
        {
            0 => outline.Fuselage,
            1 => outline.Wing,
            _ => outline.Tailplane,
        };

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
