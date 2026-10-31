namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>How a straight-over leg gets across the runway.</summary>
internal enum StraightOverKind
{
    /// <summary>The first taxiway's line runs on to the second taxiway's line, one corner where they meet.</summary>
    Continue,

    /// <summary>A straight diagonal between a point set back on each line, a corner at each end.</summary>
    Chord,
}

/// <summary>
/// One taxiway's side of an offset crossing, its line drawn outward from the runway: from <see cref="Via"/>, the taxiway's own
/// node at its join, along <see cref="OutwardBearingDeg"/>, with the walk from there to its bar of the crossed runway
/// (<see cref="WalkToBar"/>, never empty). Positions on the line are feet outward from <see cref="Via"/>; a negative one lies
/// on the line's extension over the runway.
/// </summary>
internal sealed record TaxiwayLine(string Taxiway, GroundNode Via, double OutwardBearingDeg, IReadOnlyList<DirectionalEdge> WalkToBar)
{
    /// <summary>
    /// How far, feet, a node may lie off the line and the taxiway still run straight along it: a sim convention wide enough for
    /// survey noise and far narrower than a taxiway's half-width.
    /// </summary>
    private const double StraightRunToleranceFt = 5.0;

    /// <summary>The bearing an aircraft flies along the line toward the runway.</summary>
    public double InboundBearingDeg => (OutwardBearingDeg + 180.0) % 360.0;

    /// <summary>The bar's position on the line.</summary>
    public double BarFt => OutwardFt(WalkToBar[^1].ToNode.Position);

    /// <summary>
    /// How far outward the taxiway runs straight along the line: the last node of the walk reached by straight edges with no
    /// node off the line.
    /// </summary>
    public double RunFt
    {
        get
        {
            double runFt = 0.0;
            foreach (DirectionalEdge step in WalkToBar)
            {
                double offLineFt = GeoMath.SignedCrossTrackDistanceNmRaw(step.ToNode.Position, Via.Position, OutwardBearingDeg) * GeoMath.FeetPerNm;
                if ((step.Edge is not GroundEdge { IntermediatePoints.Count: 0 }) || (Math.Abs(offLineFt) > StraightRunToleranceFt))
                {
                    break;
                }

                runFt = OutwardFt(step.ToNode.Position);
            }

            return runFt;
        }
    }

    /// <summary>The position on the line of the point abreast of <paramref name="position"/>.</summary>
    public double OutwardFt(LatLon position) => GeoMath.AlongTrackDistanceNmRaw(position, Via.Position, OutwardBearingDeg) * GeoMath.FeetPerNm;

    /// <summary>The point at <paramref name="outwardFt"/> on the line.</summary>
    public LatLon PointAt(double outwardFt) => SegmentExpander.ProjectAlong(Via.Position, OutwardBearingDeg, outwardFt);

    /// <summary>
    /// The nearest real node at or outward of <paramref name="outwardFt"/>, which is <see cref="Via"/> for a point on the
    /// line's extension over the runway.
    /// </summary>
    public GroundNode NodeAtOrOutwardOf(double outwardFt) =>
        outwardFt <= 0.0 ? Via : WalkToBar.Select(step => step.ToNode).First(node => OutwardFt(node.Position) >= outwardFt);

    /// <summary>The ids of the taxiway's nodes between <see cref="Via"/>, included, and <paramref name="node"/>, excluded.</summary>
    public IReadOnlyList<int> NodeIdsInsideOf(GroundNode node) =>
        [.. WalkToBar.Select(step => step.FromNode).TakeWhile(from => from.Id != node.Id).Select(from => from.Id)];
}

/// <summary>
/// A built straight-over leg: <see cref="Edges"/>, virtual and named for the taxiways only, run from the real node
/// <see cref="Entry"/> on the first taxiway to the real node <see cref="Landing"/> on the second; the head then arrives on
/// <see cref="LandingBearingDeg"/>. <see cref="CornersDeg"/> are the turns flown, in order. <see cref="PastEntryNodeIds"/> are the
/// first taxiway's nodes between <see cref="Entry"/> and its join, from which the leg cannot start, and
/// <see cref="SkippedNodeIds"/> the second taxiway's nodes the leg passes without touching. <see cref="SetBackFt"/> is a chord's
/// set-back on each line, 0 for <see cref="StraightOverKind.Continue"/>.
/// </summary>
internal sealed record StraightOverLeg(
    StraightOverKind Kind,
    IReadOnlyList<DirectionalEdge> Edges,
    IReadOnlyList<double> CornersDeg,
    double SetBackFt,
    GroundNode Entry,
    GroundNode Landing,
    double LandingBearingDeg,
    IReadOnlyList<int> PastEntryNodeIds,
    IReadOnlyList<int> SkippedNodeIds
)
{
    /// <summary>The larger corner, the turn judged against a category's limit.</summary>
    public double MaxCornerDeg => CornersDeg.Max();
}

/// <summary>What a crossing's straight-over geometry came to: its <see cref="Leg"/>, or why none was built (<see cref="NotBuilt"/>).</summary>
internal sealed record StraightOver(StraightOverLeg? Leg, string? NotBuilt)
{
    /// <summary>
    /// The lines meet past the second taxiway's bar, so no leg lands short of it and today's route is no answer either: the clearance
    /// is refused for every aircraft (<see cref="FailureKind.NoRoomToCrossStraight"/>).
    /// </summary>
    public bool NoRoom { get; init; }

    public static StraightOver Refused(string reason) => new(null, reason);
}

/// <summary>
/// The straight-over leg between two taxiways that meet only across a runway, measured on the lines the taxiways are flown on. A
/// pure function of the layout's doubles, the same at the top level, in every probe and on the client, whatever the aircraft: the
/// category's turn limit is judged by the caller.
/// </summary>
internal static class StraightOverGeometry
{
    /// <summary>
    /// Lines meeting at less than this take a chord, however far away they meet: their meeting point is no place to turn. A sim
    /// convention; no FAA figure exists.
    /// </summary>
    internal const double NearParallelDeg = 45.0;

    /// <summary>The larger corner a chord is set back to reach. A sim convention; no FAA figure exists.</summary>
    internal const double ChordCornerDeg = 45.0;

    /// <summary>How close, feet, two points of a leg may lie and still be two points: closer than this, the leg has no edge between them.</summary>
    private const double MinLegEdgeFt = 1.0;

    /// <summary>Steps of the set-back bisection: fixed so the result is the same in every process.</summary>
    private const int ChordBisectionSteps = 40;

    /// <summary>The leg from <paramref name="from"/> onto <paramref name="to"/> across the runway of <paramref name="rect"/>.</summary>
    public static StraightOver Build(TaxiwayLine from, TaxiwayLine to, in RunwayRectangle rect)
    {
        double lineAngleDeg = RouteCostFunction.HeadingDelta(from.InboundBearingDeg, to.OutwardBearingDeg);
        if (lineAngleDeg >= NearParallelDeg)
        {
            (double onFromFt, double onToFt) = Meet(from, to);
            if (onFromFt < from.BarFt)
            {
                return Continue(from, to, onFromFt, onToFt);
            }
        }

        return Chord(from, to, rect);
    }

    /// <summary>
    /// Where the two lines meet, as a position on each. Closed-form on the flat plane of the bearing and distance between the
    /// joins, which at this scale is true to a small fraction of a foot.
    /// </summary>
    private static (double OnFromFt, double OnToFt) Meet(TaxiwayLine from, TaxiwayLine to)
    {
        double acrossFt = GeoMath.DistanceNm(from.Via.Position, to.Via.Position) * GeoMath.FeetPerNm;
        double acrossDeg = GeoMath.BearingTo(from.Via.Position, to.Via.Position);
        double divisor = Sin(from.OutwardBearingDeg - to.OutwardBearingDeg);
        return (acrossFt * Sin(acrossDeg - to.OutwardBearingDeg) / divisor, acrossFt * Sin(acrossDeg - from.OutwardBearingDeg) / divisor);
    }

    private static double Sin(double degrees) => Math.Sin(degrees * Math.PI / 180.0);

    private static double Cos(double degrees) => Math.Cos(degrees * Math.PI / 180.0);

    private static StraightOver Continue(TaxiwayLine from, TaxiwayLine to, double onFromFt, double onToFt)
    {
        if (onToFt > to.BarFt)
        {
            return StraightOver.Refused(FormattableString.Invariant($"the lines meet {onToFt:F0}ft along {to.Taxiway}, past its bar")) with
            {
                NoRoom = true,
            };
        }

        if ((onFromFt > from.RunFt) || (onToFt > to.RunFt))
        {
            return StraightOver.Refused(FormattableString.Invariant($"the lines meet off a straight run ({onFromFt:F0}ft, {onToFt:F0}ft)"));
        }

        return Leg(StraightOverKind.Continue, (from, onFromFt), (to, onToFt), [from.PointAt(onFromFt)], 0.0);
    }

    private static StraightOver Chord(TaxiwayLine from, TaxiwayLine to, in RunwayRectangle rect)
    {
        double capFt = Math.Min(Math.Min(from.RunFt, to.RunFt), Math.Min(PavementReachFt(from, rect), PavementReachFt(to, rect)));
        double setBackFt = ChordSetBackFt(from, to, capFt);
        if (setBackFt <= 0.0)
        {
            return StraightOver.Refused(FormattableString.Invariant($"no chord: set-back {setBackFt:F1}ft, room {capFt:F1}ft"));
        }

        return Leg(StraightOverKind.Chord, (from, setBackFt), (to, setBackFt), [from.PointAt(setBackFt), to.PointAt(setBackFt)], setBackFt);
    }

    /// <summary>
    /// The smallest set-back at which a chord's larger corner comes down to <see cref="ChordCornerDeg"/>, or
    /// <paramref name="capFt"/> when the larger corner is still over it there; 0 when the joins need none.
    /// </summary>
    private static double ChordSetBackFt(TaxiwayLine from, TaxiwayLine to, double capFt)
    {
        if ((capFt <= 0.0) || (MaxChordCornerDeg(from, to, 0.0) <= ChordCornerDeg))
        {
            return 0.0;
        }

        if (MaxChordCornerDeg(from, to, capFt) > ChordCornerDeg)
        {
            return capFt;
        }

        double tooShortFt = 0.0;
        double longEnoughFt = capFt;
        for (int step = 0; step < ChordBisectionSteps; step++)
        {
            double middleFt = (tooShortFt + longEnoughFt) / 2.0;
            if (MaxChordCornerDeg(from, to, middleFt) <= ChordCornerDeg)
            {
                longEnoughFt = middleFt;
            }
            else
            {
                tooShortFt = middleFt;
            }
        }

        return longEnoughFt;
    }

    private static double MaxChordCornerDeg(TaxiwayLine from, TaxiwayLine to, double setBackFt)
    {
        double chordDeg = GeoMath.BearingTo(from.PointAt(setBackFt), to.PointAt(setBackFt));
        return Math.Max(
            RouteCostFunction.HeadingDelta(from.InboundBearingDeg, chordDeg),
            RouteCostFunction.HeadingDelta(chordDeg, to.OutwardBearingDeg)
        );
    }

    /// <summary>
    /// How far outward from its join a point on <paramref name="line"/> stays on the pavement of the runway in
    /// <paramref name="rect"/>; 0 for a join outside it.
    /// </summary>
    private static double PavementReachFt(TaxiwayLine line, in RunwayRectangle rect)
    {
        var threshold = new LatLon(rect.RefLat, rect.RefLon);
        double headingDeg = rect.TrueHeading.Degrees;
        double alongFt = GeoMath.AlongTrackDistanceNmRaw(line.Via.Position, threshold, headingDeg) * GeoMath.FeetPerNm;
        double offCentreFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNmRaw(line.Via.Position, threshold, headingDeg)) * GeoMath.FeetPerNm;
        double lengthFt = rect.LengthNm * GeoMath.FeetPerNm;
        double halfWidthFt = rect.HalfWidthNm * GeoMath.FeetPerNm;
        if ((alongFt < 0.0) || (alongFt > lengthFt) || (offCentreFt > halfWidthFt))
        {
            return 0.0;
        }

        double acrossRate = Math.Abs(Sin(line.OutwardBearingDeg - headingDeg));
        double alongRate = Cos(line.OutwardBearingDeg - headingDeg);
        double acrossReachFt = (halfWidthFt - offCentreFt) / acrossRate;
        double alongReachFt = (alongRate >= 0.0 ? lengthFt - alongFt : alongFt) / Math.Abs(alongRate);
        return Math.Min(acrossReachFt, alongReachFt);
    }

    /// <summary>
    /// The leg through <paramref name="points"/>, from the real node at or outward of the first point's position on its line to
    /// the one at or outward of the last point's: every edge but the last named for the first taxiway, the last for the second.
    /// </summary>
    private static StraightOver Leg(
        StraightOverKind kind,
        (TaxiwayLine Line, double OutwardFt) first,
        (TaxiwayLine Line, double OutwardFt) last,
        IReadOnlyList<LatLon> points,
        double setBackFt
    )
    {
        GroundNode entry = first.Line.NodeAtOrOutwardOf(first.OutwardFt);
        GroundNode landing = last.Line.NodeAtOrOutwardOf(last.OutwardFt);
        List<GroundNode> path = [entry, .. points.Select(p => VirtualNode.Create(p.Lat, p.Lon)), landing];
        var edges = new List<DirectionalEdge>();
        var bearings = new List<double> { first.Line.InboundBearingDeg };
        for (int i = 1; i < path.Count; i++)
        {
            (GroundNode a, GroundNode b) = (path[i - 1], path[i]);
            if ((GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm) < MinLegEdgeFt)
            {
                return StraightOver.Refused($"a point of the leg falls on #{(a.Id > 0 ? a.Id : b.Id)}");
            }

            string taxiway = i == (path.Count - 1) ? last.Line.Taxiway : first.Line.Taxiway;
            edges.Add(VirtualNode.CreateEdge(a, b, taxiway).Directed(a, b));
        }

        bearings.AddRange(path.Skip(1).Take(points.Count - 1).Zip(path.Skip(2), (a, b) => GeoMath.BearingTo(a.Position, b.Position)));
        bearings.Add(last.Line.OutwardBearingDeg);
        List<double> corners = [.. bearings.Zip(bearings.Skip(1), RouteCostFunction.HeadingDelta)];
        var leg = new StraightOverLeg(
            kind,
            edges,
            corners,
            setBackFt,
            entry,
            landing,
            last.Line.OutwardBearingDeg,
            first.Line.NodeIdsInsideOf(entry),
            last.Line.NodeIdsInsideOf(landing)
        );
        return new StraightOver(leg, null);
    }
}
