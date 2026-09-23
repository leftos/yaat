using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Yaat.Sim.Data.Airport;

/// <summary>The part of a towed aircraft's outline that reaches furthest into a taxiway's object-free area.</summary>
public enum TugFootprintPart
{
    Nose,
    Tail,
    LeftWing,
    RightWing,
}

/// <summary>
/// How far a candidate's flown path reaches into the object-free area of the movement-area taxiways it is kept out of:
/// the deepest penetration anywhere along it, which taxiway and which part of the outline that was, and the
/// penetration summed over the path (feet of penetration × feet flown, standing in for time: the tug runs at one speed).
/// </summary>
/// <param name="Taxiway">The taxiway of the deepest penetration; null when the path stays clear.</param>
/// <param name="Part">The part of the outline that penetrates deepest.</param>
/// <param name="PeakFt">The deepest penetration, feet; zero when the path stays clear.</param>
/// <param name="ExposureFtFt">The penetration integrated along the path, feet × feet.</param>
internal readonly record struct TugTaxiwayFouling(string? Taxiway, TugFootprintPart Part, double PeakFt, double ExposureFtFt)
{
    internal static readonly TugTaxiwayFouling Clear = new(null, TugFootprintPart.Nose, 0.0, 0.0);

    internal bool Fouls => PeakFt > 0.0;
}

/// <summary>
/// Measures a tug move's outline — fuselage, wing and tailplane, <see cref="GroundOutline"/>, without the tug's lead —
/// against the object-free area of every movement-area taxiway near it: at each sampled pose, the least distance from
/// each outline segment to the taxiway's own centreline pieces (its straight edges and the arcs joining two pieces of
/// the same taxiway; a fillet turning onto it from another taxiway or lane is not its centreline), against the
/// taxiway's <see cref="AirplaneDesignGroups.TaxiwayObjectFreeHalfWidthFt"/> for its
/// <see cref="AirplaneDesignGroups.ForTaxiway"/> group. A push kept out of the movement area (a spot, stand or node
/// goal) is held to stay outside it. The protected pieces and their clipping are built once per layout (in the
/// layout's own flat frame, <see cref="AirplaneDesignGroups.LayoutFrame"/>) and shared by every plan; one instance
/// serves every candidate of a plan.
/// </summary>
internal sealed class TugTaxiwayClearance
{
    /// <summary>How many chords a same-taxiway arc is measured as.</summary>
    private const int ArcChords = 8;

    /// <summary>
    /// How far outside a taxiway's object-free half-width the planned outline must stay, feet: the worst the flown
    /// closest approach fell short of the planned one over the push review cases (SFO F8 → 7A, planned 131.6 ft, flown
    /// 126.6 ft at one-second sampling), rounded up to 5 ft.
    /// </summary>
    internal const double ClearanceMarginFt = 5.0;

    /// <summary>The length of the stretches a piece is clipped in, feet.</summary>
    private const double ClipStepFt = 5.0;

    private static readonly ConditionalWeakTable<AirportGroundLayout, ConditionalWeakTable<MovementAreaClassification, LayoutZones>> Cache = [];

    private readonly LayoutZones _zones;
    private readonly GroundOutlineSize _size;

    internal TugTaxiwayClearance(AirportGroundLayout layout, string aircraftType)
    {
        var classification = MovementAreaClassification.For(layout);
        _zones = Cache.GetOrCreateValue(layout).GetValue(classification, c => new LayoutZones(layout, c));
        _size = GroundOutlineSize.Of(aircraftType, towedNoseFirst: false);
    }

    /// <summary>The movement-area taxiways whose object-free area the outline already reaches into at <paramref name="pose"/>.</summary>
    /// <param name="pose">The pose.</param>
    /// <returns>The taxiway names.</returns>
    internal IReadOnlySet<string> TaxiwaysFouledAt(TugPose pose)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        GroundOutline outline = OutlineAt(pose);
        foreach (Piece piece in Near([pose]).SelectMany(_zones.Clipped))
        {
            if (Deepest(outline, piece).PenetrationFt > 0.0)
            {
                names.Add(piece.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// How far the flown path reaches into the object-free area of the protected taxiways — every movement-area
    /// taxiway near it but <paramref name="excluded"/>.
    /// </summary>
    /// <param name="traces">The candidate's moves, flown.</param>
    /// <param name="excluded">Taxiways the path is not held clear of.</param>
    /// <returns>The deepest penetration and the exposure; <see cref="TugTaxiwayFouling.Clear"/> when the path stays clear.</returns>
    internal TugTaxiwayFouling Measure(IReadOnlyList<TugMoveTrace> traces, IReadOnlySet<string> excluded) =>
        Scan(traces, excluded, firstFoulingOnly: false);

    /// <summary>
    /// Whether the flown path reaches into the object-free area of any protected taxiway: <see cref="Measure"/>'s
    /// verdict, the scan stopping at the first penetration it finds.
    /// </summary>
    /// <param name="traces">The candidate's moves, flown.</param>
    /// <param name="excluded">Taxiways the path is not held clear of.</param>
    /// <returns>True when the path fouls one.</returns>
    internal bool Fouls(IReadOnlyList<TugMoveTrace> traces, IReadOnlySet<string> excluded) => Scan(traces, excluded, firstFoulingOnly: true).Fouls;

    /// <summary>The path's fouling, or — with <paramref name="firstFoulingOnly"/> — the first penetration found, its exposure unsummed.</summary>
    private TugTaxiwayFouling Scan(IReadOnlyList<TugMoveTrace> traces, IReadOnlySet<string> excluded, bool firstFoulingOnly)
    {
        List<TugPose> poses = [.. traces.SelectMany(t => t.Samples)];
        List<(Piece Piece, double ReachableFt)> pieces =
        [
            .. Near(poses)
                .Where(p => !excluded.Contains(p.Name))
                .SelectMany(_zones.Clipped)
                .Select(p => (p, _zones.HalfWidthFt(p.Name) + ClearanceMarginFt + _size.ReachFt)),
        ];
        if (pieces.Count == 0)
        {
            return TugTaxiwayFouling.Clear;
        }

        TugTaxiwayFouling worst = TugTaxiwayFouling.Clear;
        double exposure = 0.0;
        TugPose? previous = null;
        foreach (TugPose pose in poses)
        {
            GroundOutline outline = OutlineAt(pose);
            OutlinePoint centre = _zones.Frame.ToLocal(pose.Position);
            double deepestHereFt = 0.0;

            // A piece farther from the reference point than the outline's reach plus its zone cannot be reached into.
            foreach ((Piece piece, _) in pieces.Where(p => PointToSegmentFt(centre, p.Piece.A, p.Piece.B) < p.ReachableFt))
            {
                (double penetrationFt, TugFootprintPart part) = Deepest(outline, piece);
                deepestHereFt = Math.Max(deepestHereFt, penetrationFt);
                if (penetrationFt > worst.PeakFt)
                {
                    worst = worst with { Taxiway = piece.Name, Part = part, PeakFt = penetrationFt };
                }

                if (firstFoulingOnly && worst.Fouls)
                {
                    return worst;
                }
            }

            double stepFt = previous is { } last ? GeoMath.DistanceNm(last.Position, pose.Position) * GeoMath.FeetPerNm : 0.0;
            exposure += deepestHereFt * stepFt;
            previous = pose;
        }

        return worst with
        {
            ExposureFtFt = exposure,
        };
    }

    private GroundOutline OutlineAt(TugPose pose) => GroundOutline.At(_zones.Frame.ToLocal(pose.Position), pose.NoseTrueDeg, _size);

    /// <summary>The pieces that could come within their taxiway's half-width of the outline at any of the poses; none for no poses.</summary>
    private IEnumerable<Piece> Near(IReadOnlyCollection<TugPose> poses)
    {
        if (poses.Count == 0)
        {
            return [];
        }

        double padFt = _size.ReachFt + AirplaneDesignGroups.TaxiwayObjectFreeHalfWidthFt(AirplaneDesignGroup.VI) + ClearanceMarginFt;
        var box = Box.Around(poses.Select(p => _zones.Frame.ToLocal(p.Position)), padFt);
        return _zones.Pieces.Where(p => box.Touches(p.A, p.B));
    }

    /// <summary>How far the outline reaches into the piece's object-free area, feet (zero or less when clear), and with which part.</summary>
    private (double PenetrationFt, TugFootprintPart Part) Deepest(GroundOutline outline, Piece piece)
    {
        (double fuselageFt, double fuselageT) = Closest(outline.Fuselage, piece);
        (double wingFt, double wingT) = Closest(outline.Wing, piece);
        (double tailplaneFt, _) = Closest(outline.Tailplane, piece);
        (double closestFt, TugFootprintPart part) = (fuselageFt, fuselageT >= 0.5 ? TugFootprintPart.Nose : TugFootprintPart.Tail);
        if (wingFt < closestFt)
        {
            (closestFt, part) = (wingFt, wingT < 0.5 ? TugFootprintPart.LeftWing : TugFootprintPart.RightWing);
        }

        if (tailplaneFt < closestFt)
        {
            (closestFt, part) = (tailplaneFt, TugFootprintPart.Tail);
        }

        return (_zones.HalfWidthFt(piece.Name) + ClearanceMarginFt - closestFt, part);
    }

    /// <summary>
    /// The least distance between an outline segment and a piece, feet, and where along the outline segment (0 at its
    /// first point, 1 at its second) the nearest point lies.
    /// </summary>
    private static (double DistanceFt, double T) Closest(OutlineSegment segment, Piece piece)
    {
        OutlinePoint p = segment.A;
        OutlinePoint r = segment.B - segment.A;
        OutlinePoint q = piece.A;
        OutlinePoint s = piece.B - piece.A;
        double denominator = Cross(r, s);
        if (Math.Abs(denominator) > 1e-9)
        {
            double t = Cross(q - p, s) / denominator;
            double u = Cross(q - p, r) / denominator;
            if ((t >= 0.0) && (t <= 1.0) && (u >= 0.0) && (u <= 1.0))
            {
                return (0.0, t);
            }
        }

        (double DistanceFt, double T) best = (PointToSegmentFt(segment.A, piece.A, piece.B), 0.0);
        best = Nearer(best, (PointToSegmentFt(segment.B, piece.A, piece.B), 1.0));
        best = Nearer(best, (PointToSegmentFt(piece.A, segment.A, segment.B), Projection(piece.A, segment.A, segment.B)));
        return Nearer(best, (PointToSegmentFt(piece.B, segment.A, segment.B), Projection(piece.B, segment.A, segment.B)));
    }

    /// <summary>The nearer of two candidates, the first on a tie.</summary>
    private static (double DistanceFt, double T) Nearer((double DistanceFt, double T) held, (double DistanceFt, double T) other) =>
        other.DistanceFt < held.DistanceFt ? other : held;

    private static double Cross(OutlinePoint a, OutlinePoint b) => (a.EastFt * b.NorthFt) - (a.NorthFt * b.EastFt);

    private static double Projection(OutlinePoint point, OutlinePoint a, OutlinePoint b)
    {
        OutlinePoint along = b - a;
        double lengthSq = (along.EastFt * along.EastFt) + (along.NorthFt * along.NorthFt);
        return lengthSq <= 1e-9
            ? 0.0
            : Math.Clamp((((point.EastFt - a.EastFt) * along.EastFt) + ((point.NorthFt - a.NorthFt) * along.NorthFt)) / lengthSq, 0.0, 1.0);
    }

    private static double PointToSegmentFt(OutlinePoint point, OutlinePoint a, OutlinePoint b) =>
        OutlinePoint.Distance(point, a + (Projection(point, a, b) * (b - a)));

    /// <summary>One straight piece of a protected taxiway's centreline, in the layout's frame.</summary>
    private readonly record struct Piece(string Name, OutlinePoint A, OutlinePoint B);

    /// <summary>An axis-aligned box in the flat frame.</summary>
    private readonly record struct Box(double MinX, double MaxX, double MinY, double MaxY)
    {
        /// <summary>The box around the points, grown by <paramref name="padFt"/> on every side.</summary>
        internal static Box Around(IEnumerable<OutlinePoint> points, double padFt)
        {
            List<OutlinePoint> all = [.. points];
            return new Box(
                all.Min(p => p.EastFt) - padFt,
                all.Max(p => p.EastFt) + padFt,
                all.Min(p => p.NorthFt) - padFt,
                all.Max(p => p.NorthFt) + padFt
            );
        }

        /// <summary>Whether the segment's own bounding box overlaps this box.</summary>
        internal bool Touches(OutlinePoint a, OutlinePoint b) =>
            (Math.Max(a.EastFt, b.EastFt) >= MinX)
            && (Math.Min(a.EastFt, b.EastFt) <= MaxX)
            && (Math.Max(a.NorthFt, b.NorthFt) >= MinY)
            && (Math.Min(a.NorthFt, b.NorthFt) <= MaxY);
    }

    /// <summary>
    /// One layout's protected centreline pieces under one classification, laid out once in the layout's frame, with
    /// each taxiway's object-free half-width and each piece's clipping computed on first use and kept. Shared by every
    /// plan at the airport, so every cache is safe to fill from several threads.
    /// </summary>
    private sealed class LayoutZones
    {
        private readonly AirportGroundLayout _layout;
        private readonly ConcurrentDictionary<string, double> _halfWidthByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Piece, List<Piece>> _clippedByPiece = new();

        internal LayoutZones(AirportGroundLayout layout, MovementAreaClassification classification)
        {
            _layout = layout;
            Frame = AirplaneDesignGroups.LayoutFrame(layout);
            GroundOutlineFrame frame = Frame;
            Pieces =
            [
                .. layout
                    .Edges.Where(e => AirplaneDesignGroups.IsProtectable(e, classification))
                    .SelectMany(e => AirplaneDesignGroups.Pieces(e, frame).Select(p => new Piece(e.TaxiwayName, p.A, p.B))),
                .. layout
                    .Arcs.Where(a =>
                        (a.TaxiwayNames.Length == 1) && !a.IsRamp && !a.IsRunwayCenterline && classification.IsMovementArea(a.TaxiwayNames[0])
                    )
                    .SelectMany(a => ArcPieces(a, frame)),
            ];
        }

        internal GroundOutlineFrame Frame { get; }

        internal List<Piece> Pieces { get; }

        /// <summary>The object-free half-width of a taxiway, feet, by its design group.</summary>
        internal double HalfWidthFt(string taxiway) =>
            _halfWidthByName.GetOrAdd(
                taxiway,
                name => AirplaneDesignGroups.TaxiwayObjectFreeHalfWidthFt(AirplaneDesignGroups.ForTaxiway(_layout, name))
            );

        /// <summary>
        /// A piece with the stretches cut out that lie inside a narrower movement-area taxiway's object-free half-width
        /// (the midpoint of each <see cref="ClipStepFt"/> stretch judged), so a wider-zoned taxiway meeting or crossing
        /// another — SFO's D, ADG VI for want of a parallel, running up to and across A — protects nothing past that
        /// taxiway's own zone: the part inside A's zone is A's. A taxiway meeting one as narrow or wider is not cut: its
        /// zone already stops at the other's edge. Computed once per piece; independent of the path measured.
        /// </summary>
        internal List<Piece> Clipped(Piece piece) => _clippedByPiece.GetOrAdd(piece, p => KeptStretches(p, NarrowerNear(p)));

        /// <summary>The pieces of other, narrower-zoned taxiways close enough to <paramref name="piece"/> to cut it.</summary>
        private List<Piece> NarrowerNear(Piece piece)
        {
            double halfWidthFt = HalfWidthFt(piece.Name);
            var reach = Box.Around([piece.A, piece.B], AirplaneDesignGroups.TaxiwayObjectFreeHalfWidthFt(AirplaneDesignGroup.VI));
            return
            [
                .. Pieces.Where(z =>
                    !z.Name.Equals(piece.Name, StringComparison.OrdinalIgnoreCase) && reach.Touches(z.A, z.B) && (HalfWidthFt(z.Name) < halfWidthFt)
                ),
            ];
        }

        /// <summary>The runs of <paramref name="piece"/> whose stretches lie outside every narrower piece's zone.</summary>
        private List<Piece> KeptStretches(Piece piece, List<Piece> narrower)
        {
            var kept = new List<Piece>();
            int stretches = Math.Max(1, (int)Math.Ceiling(OutlinePoint.Distance(piece.A, piece.B) / ClipStepFt));
            OutlinePoint? runStart = null;
            for (int i = 0; i < stretches; i++)
            {
                OutlinePoint from = piece.A + ((double)i / stretches * (piece.B - piece.A));
                OutlinePoint to = piece.A + ((double)(i + 1) / stretches * (piece.B - piece.A));
                bool inside = IsInsideAny(0.5 * (from + to), narrower);
                if (!inside)
                {
                    runStart ??= from;
                }
                else if (runStart is { } start)
                {
                    kept.Add(piece with { A = start, B = from });
                    runStart = null;
                }
            }

            if (runStart is { } open)
            {
                kept.Add(piece with { A = open });
            }

            return kept;
        }

        private bool IsInsideAny(OutlinePoint point, List<Piece> zones) => zones.Any(z => PointToSegmentFt(point, z.A, z.B) < HalfWidthFt(z.Name));

        /// <summary>A same-taxiway arc as <see cref="ArcChords"/> chords of its Bezier.</summary>
        private static IEnumerable<Piece> ArcPieces(GroundArc arc, GroundOutlineFrame frame)
        {
            OutlinePoint p0 = frame.ToLocal(arc.Nodes[0].Position);
            OutlinePoint p1 = frame.ToLocal(new LatLon(arc.P1Lat, arc.P1Lon));
            OutlinePoint p2 = frame.ToLocal(new LatLon(arc.P2Lat, arc.P2Lon));
            OutlinePoint p3 = frame.ToLocal(arc.Nodes[1].Position);
            OutlinePoint previous = p0;
            for (int i = 1; i <= ArcChords; i++)
            {
                double t = (double)i / ArcChords;
                double m = 1.0 - t;
                OutlinePoint point = ((m * m * m) * p0) + ((3.0 * m * m * t) * p1) + ((3.0 * m * t * t) * p2) + ((t * t * t) * p3);
                yield return new Piece(arc.TaxiwayNames[0], previous, point);
                previous = point;
            }
        }
    }
}
