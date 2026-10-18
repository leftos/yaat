using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A resolved taxi route: an ordered sequence of segments with hold-short points.
/// </summary>
public sealed class TaxiRoute
{
    public required List<TaxiRouteSegment> Segments { get; init; }
    public required List<HoldShortPoint> HoldShortPoints { get; init; }
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// The one-way lanes the resolver implied into the route, uncleared, as the way to a gate or spot (SFO <c>TAXI T A @B2</c>
    /// enters the Terminal 1 ramp on M1). The readback names them, and they are not warned as taxiways outside the route
    /// issued: the route carries its own <c>M1 not in clearance</c> note (<see cref="Pathfinding.RouteMaterialiser.NotInClearanceWarning"/>).
    /// Read when the TAXI is issued; not snapshotted, like <see cref="Warnings"/>.
    /// </summary>
    public List<string> ImpliedLanes { get; init; } = [];

    /// <summary>
    /// Number of mandatory connector insertions the resolver had to bridge between cleared taxiways
    /// that shared no direct junction (the "X and Y do not connect directly — taxi via Z" case). A
    /// route that honors the clearance without any blind detour has 0; used by
    /// <see cref="Pathfinding.SegmentExpander.Run"/> to prefer a clearance-honoring variant (e.g. one
    /// threaded through a curated connector) over a shorter route that had to blind-detour.
    /// </summary>
    public int MandatoryConnectorCount { get; init; }

    /// <summary>Parking destination name (@ prefix), if any.</summary>
    public string? DestinationParking { get; init; }

    /// <summary>Spot destination name ($ prefix), if any.</summary>
    public string? DestinationSpot { get; init; }

    /// <summary>
    /// For a spot line-up (<see cref="RampLaneReposition.TryPlanSpotLineUp"/>), the index of the first segment on the
    /// spot's lane: from there the taxi holds the slow pull speed up the lane onto the spot
    /// (<see cref="Phases.Ground.TaxiingPhase.SpotLineUpPullSpeedKts"/>). Null on every other route.
    /// </summary>
    public int? SpotLineUpPullFromSegment { get; init; }

    /// <summary>
    /// How the clearance turns the aircraft about on the taxiway it stood mid-way along, toward that edge's node behind it
    /// (<see cref="TurnAboutTargetNodeId"/>). Set by the TAXI handler on the route it assigns, and cleared to
    /// <see cref="TaxiTurnAboutShape.None"/> by <see cref="Phases.Ground.TaxiingPhase"/> once a
    /// <see cref="TaxiTurnAboutShape.FromFarEnd"/> turn about with no leg back to its target (segment 0 starts on the
    /// target) reaches that target; <see cref="TaxiTurnAboutShape.None"/> on every other route.
    /// </summary>
    public TaxiTurnAboutShape TurnAboutShape { get; set; }

    /// <summary>
    /// The far node of the edge the aircraft stood mid-way along, which the turn about (<see cref="TurnAboutShape"/>) turns
    /// it toward, for either shape; null on a route without a turn about. Set by the TAXI handler with the shape, and
    /// cleared to null with it by <see cref="Phases.Ground.TaxiingPhase"/> when a no-leg far-end turn about reaches it.
    /// </summary>
    public int? TurnAboutTargetNodeId { get; set; }

    /// <summary>
    /// The shape of the turn about this route starts with (<see cref="TurnAboutShape"/>) while the aircraft has yet to finish
    /// it: it is still on segment 0, and the shape has not been cleared at its target (a far-end turn about with no leg back
    /// ends when the aircraft reaches the target, still on segment 0). <see cref="TaxiTurnAboutShape.None"/> once either
    /// happens, and on every route without one.
    /// </summary>
    public TaxiTurnAboutShape PendingTurnAboutShape =>
        ((CurrentSegmentIndex == 0) && (Segments.Count > 0)) ? TurnAboutShape : TaxiTurnAboutShape.None;

    public double TotalDistanceNm => Segments.Sum(s => s.Edge.DistanceNm);

    /// <summary>The whole route's length in feet — the unit every ground-distance rule in the taxi stack is written in.</summary>
    public double TotalDistanceFt => TotalDistanceNm * GeoMath.FeetPerNm;

    /// <summary>
    /// Length in feet of the first <paramref name="segmentCount"/> segments — how far along the route the node
    /// that segment count ends at lies. <c>0</c> is the route's start node, <see cref="Segments"/>.Count its end.
    /// </summary>
    public double PrefixDistanceFt(int segmentCount) => Segments.Take(segmentCount).Sum(s => s.Edge.DistanceNm) * GeoMath.FeetPerNm;

    /// <summary>
    /// How far (ft) an aircraft at <paramref name="position"/> has to go along the route to the node the first
    /// <paramref name="segmentCount"/> segments end at: the segment in progress (<see cref="CurrentSegmentIndex"/>, or segment 0
    /// before the route starts) measured straight from the position to its end node, then each later segment up to that count by
    /// its length. The in-progress leg is measured straight rather than along the navigator's primitive, which publishes no
    /// remaining distance, so on a fillet it reads the chord, a little short of the arc. The route must have a segment in progress.
    /// </summary>
    public double RemainingDistanceFt(LatLon position, int segmentCount)
    {
        int current = Math.Max(0, CurrentSegmentIndex);
        double inProgressFt = GeoMath.DistanceNm(position, Segments[current].Edge.ToNode.Position) * GeoMath.FeetPerNm;
        return inProgressFt + PrefixDistanceFt(segmentCount) - PrefixDistanceFt(current + 1);
    }

    /// <summary>
    /// The cleared taxiways in order for operator-facing display. Junction/membership arcs
    /// (<c>"D - RAMP"</c>) are transitions between taxiways, not a leg of one, so they never appear as
    /// a named part of the route, and ramp pavement is dropped: a route out of a stand through the
    /// RAMP↔D corner and on down D, C, B reads <c>"D C B"</c>. Drives the Aircraft List Info column
    /// and the DTO TaxiRoute field.
    /// </summary>
    public string FormatTaxiwaySequence() => string.Join(" ", TaxiwaySequence([]).Select(t => t.Display));

    /// <summary>
    /// The cleared taxiways in order, from the shared <see cref="TaxiRouteFormatter.TaxiwayLegs"/>
    /// walk — composite junction labels (<c>"C - E"</c>) decomposed to the taxiway actually being
    /// followed, ramp edges dropped. A runway taxied along is rewritten to its operator-facing end.
    /// </summary>
    private List<(string Display, bool IsRunway)> TaxiwaySequence(IReadOnlyCollection<string> clearedRunways)
    {
        var taxiways = new List<(string, bool)>();
        foreach (TaxiRouteFormatter.TaxiwayLeg leg in TaxiRouteFormatter.TaxiwayLegs(this))
        {
            taxiways.Add(leg.IsRunway ? (RunwayDisplay(leg.Segment, clearedRunways), true) : (leg.Name, false));
        }

        return taxiways;
    }

    /// <summary>
    /// Operator-facing token for a runway taxied ALONG. The segment carries the internal combined
    /// centerline name (<c>"RWY28R/10L"</c>); show the single FAA end the controller cleared (matched
    /// from <paramref name="clearedRunways"/> — the command path) de-padded to <c>"28R"</c>. With no
    /// command context — a snapshot, the Aircraft List, or a drawn route whose path is all node
    /// references — name the end the aircraft is travelling toward instead.
    /// </summary>
    private static string RunwayDisplay(TaxiRouteSegment seg, IReadOnlyCollection<string> clearedRunways)
    {
        foreach (string designator in clearedRunways)
        {
            if (seg.Edge.Edge.MatchesRunway(designator))
            {
                return RunwayIdentifier.ToDisplayDesignator(designator);
            }
        }

        string name = seg.TaxiwayName;
        if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var id = RunwayIdentifier.Parse(name[3..]);
        return RunwayIdentifier.ToDisplayDesignator(EndTravelledToward(seg, id));
    }

    /// <summary>
    /// Which end of a runway the aircraft is taxiing toward. Designators are magnetic and the two ends
    /// are reciprocal, so comparing them against the segment's true bearing is safe — magnetic
    /// variation never approaches the 90° needed to flip the choice. Falls back to
    /// <see cref="RunwayIdentifier.End1"/> when a designator carries no leading number.
    /// </summary>
    private static string EndTravelledToward(TaxiRouteSegment seg, RunwayIdentifier id)
    {
        double travelBearing = GeoMath.BearingTo(seg.Edge.FromNode.Position, seg.Edge.ToNode.Position);
        double? end1 = DesignatorHeading(id.End1);
        double? end2 = DesignatorHeading(id.End2);
        if ((end1 is null) || (end2 is null))
        {
            return id.End1;
        }

        return
            Math.Abs(GeoMath.SignedBearingDifference(end1.Value, travelBearing))
            <= Math.Abs(GeoMath.SignedBearingDifference(end2.Value, travelBearing))
            ? id.End1
            : id.End2;
    }

    /// <summary>Approximate magnetic heading a runway designator encodes ("28R" → 280°); null when it has no number.</summary>
    private static double? DesignatorHeading(string designator)
    {
        int digits = 0;
        foreach (char c in designator)
        {
            if (!char.IsAsciiDigit(c))
            {
                break;
            }

            digits = (digits * 10) + (c - '0');
        }

        return digits is > 0 and <= 36 ? digits * 10.0 : null;
    }

    /// <summary>
    /// Returns a shallow copy of this route truncated to end at the segment whose
    /// ToNodeId matches <paramref name="nodeId"/>. If the node is not found, returns this route. A spot line-up's
    /// pull (<see cref="SpotLineUpPullFromSegment"/>) survives when its first segment is kept.
    /// </summary>
    public TaxiRoute TruncateAt(int nodeId)
    {
        for (int i = 0; i < Segments.Count; i++)
        {
            if (Segments[i].ToNodeId == nodeId)
            {
                return new TaxiRoute
                {
                    Segments = [.. Segments.Take(i + 1)],
                    HoldShortPoints = [.. HoldShortPoints.Where(hs => Segments.Take(i + 1).Any(s => s.ToNodeId == hs.NodeId))],
                    Warnings = Warnings,
                    ImpliedLanes = ImpliedLanes,
                    SpotLineUpPullFromSegment = SpotLineUpPullFromSegment <= i ? SpotLineUpPullFromSegment : null,
                    TurnAboutShape = TurnAboutShape,
                    TurnAboutTargetNodeId = TurnAboutTargetNodeId,
                };
            }
        }

        return this;
    }

    /// <summary>Current segment index being traversed.</summary>
    public int CurrentSegmentIndex { get; set; }

    public TaxiRouteSegment? CurrentSegment =>
        CurrentSegmentIndex >= 0 && CurrentSegmentIndex < Segments.Count ? Segments[CurrentSegmentIndex] : null;

    public bool IsComplete => CurrentSegmentIndex >= Segments.Count;

    /// <summary>
    /// Check if the given node is a hold-short point in this route.
    /// </summary>
    public HoldShortPoint? GetHoldShortAt(int nodeId)
    {
        foreach (HoldShortPoint hs in HoldShortPoints)
        {
            if (hs.NodeId == nodeId)
            {
                return hs;
            }
        }

        return null;
    }

    /// <summary>
    /// The first hold short still ahead of the aircraft and not cleared, in the order the route reaches them rather than the
    /// order <see cref="HoldShortPoints"/> lists them: a hold short added while taxiing is appended to the list. A bar is ahead
    /// when a segment from <see cref="CurrentSegmentIndex"/> on ends at its node. "Next" is route order from the current segment
    /// by design: a bar behind the aircraft is never next, whatever its cleared state. Null when there is none.
    /// </summary>
    public HoldShortPoint? NextUnclearedHoldShortAhead()
    {
        HoldShortPoint? next = null;
        int nextIndex = int.MaxValue;
        foreach (HoldShortPoint hs in HoldShortPoints)
        {
            if (hs.IsCleared)
            {
                continue;
            }

            int index = SegmentIndexAhead(hs.NodeId);
            if (index < nextIndex)
            {
                next = hs;
                nextIndex = index;
            }
        }

        return next;
    }

    /// <summary>
    /// True while the route is still being taxied and its next uncleared hold short ahead
    /// (<see cref="NextUnclearedHoldShortAhead"/>) is taxiway-class (<see cref="IsTaxiwayClassHoldShort"/>): the bar a bare
    /// <c>RES</c> pre-clears.
    /// </summary>
    public bool HasTaxiwayHoldShortAhead() => !IsComplete && (NextUnclearedHoldShortAhead() is { } next) && IsTaxiwayClassHoldShort(next);

    /// <summary>
    /// Whether <paramref name="holdShort"/> is a controller's explicit hold short that protects no runway: a taxiway or a spot
    /// (<c>HS B</c>, <c>HS $17</c>). A runway crossing, the destination runway, an explicit runway hold short (<c>HS 28R</c>)
    /// and the bar ending an incomplete route are not.
    /// </summary>
    public static bool IsTaxiwayClassHoldShort(HoldShortPoint holdShort) =>
        (holdShort.Reason == HoldShortReason.ExplicitHoldShort) && !HoldingShortPhase.ProtectsRunway(holdShort);

    /// <summary>
    /// The index of the first segment from <see cref="CurrentSegmentIndex"/> on that ends at <paramref name="nodeId"/>, or
    /// <see cref="int.MaxValue"/> when none does — a bar already passed, or one on no segment of the route.
    /// </summary>
    public int SegmentIndexAhead(int nodeId)
    {
        for (int i = Math.Max(0, CurrentSegmentIndex); i < Segments.Count; i++)
        {
            if (Segments[i].ToNodeId == nodeId)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>How close (ft) a bar's stop must lie to a segment's chord to count as on that segment.</summary>
    private const double StopOnChordToleranceFt = 3.0;

    /// <summary>Slack (nm, ~1 ft) when deciding whether a bar's stop lies on the segment that ends at its node.</summary>
    private const double StopOnSegmentToleranceNm = 1.0 / GeoMath.FeetPerNm;

    /// <summary>
    /// How far (nm, along the route) <paramref name="holdShort"/>'s stop sits back from the node it protects, the far end of
    /// segment <paramref name="barSegmentIndex"/>. A painted stop is placed on the route's node-to-node chords, possibly
    /// several segments back, so the walk goes backward from the bar's segment to the segment the stop lies on; whole
    /// segments count at their own length (an arc's arc length), the one the stop lies on pro rata. A stop the taxi phase
    /// moved forward lies on the segment's own geometry instead, which on a fillet is the arc, not its chord: a segment is
    /// read on whichever of its chord and its arc the stop lies closer to, and on the arc the stop's distance to the
    /// segment's end is measured along the arc (<see cref="GroundArc.Locate"/>). A stop on no segment counts its
    /// straight-line distance to the node. Zero when the bar has no stop position.
    /// </summary>
    /// <param name="barSegmentIndex">Index of the segment whose far node is the bar's node.</param>
    /// <param name="holdShort">The bar.</param>
    /// <returns>The setback in nautical miles.</returns>
    internal double HoldShortSetbackNm(int barSegmentIndex, HoldShortPoint holdShort)
    {
        if (
            (holdShort.Latitude is not { } lat)
            || (holdShort.Longitude is not { } lon)
            || (barSegmentIndex < 0)
            || (barSegmentIndex >= Segments.Count)
        )
        {
            return 0.0;
        }

        // The chord the stop is closest to, not the first within tolerance: a stop a foot or two before a bend is within
        // tolerance of the next chord's start too, and taking that one reads the setback short by the gap.
        var stop = new LatLon(lat, lon);
        double walkedNm = 0.0;
        double bestOffFt = StopOnChordToleranceFt;
        double? setbackNm = null;
        for (int j = barSegmentIndex; j >= 0; j--)
        {
            DirectionalEdge edge = Segments[j].Edge;
            (double offFt, double toEndNm) = StopOnSegment(edge, stop);
            if (offFt < bestOffFt)
            {
                bestOffFt = offFt;
                setbackNm = walkedNm + toEndNm;
            }

            walkedNm += edge.DistanceNm;
        }

        return setbackNm ?? GeoMath.DistanceNm(stop, Segments[barSegmentIndex].Edge.ToNode.Position);
    }

    /// <summary>
    /// True when a stop <paramref name="chordOffFt"/> off <paramref name="arc"/>'s chord cannot lie within
    /// <see cref="StopOnChordToleranceFt"/> of the arc itself, so the arc walk (<see cref="GroundArc.Locate"/>) can be
    /// skipped: the curve stays inside the convex hull of its control points, so no point of it lies further from the chord
    /// than the further of P1 and P2.
    /// </summary>
    private static bool TooFarFromArc(GroundArc arc, LatLon from, LatLon to, double chordOffFt)
    {
        double arcBoundFt = Math.Max(
            GeoMath.DistanceToSegmentFt(new LatLon(arc.P1Lat, arc.P1Lon), from, to),
            GeoMath.DistanceToSegmentFt(new LatLon(arc.P2Lat, arc.P2Lon), from, to)
        );
        return (chordOffFt - arcBoundFt) > StopOnChordToleranceFt;
    }

    /// <summary>
    /// How far (ft) <paramref name="stop"/> lies off <paramref name="edge"/>, and how far (nm) along it the stop sits from
    /// its end node: on the chord between its nodes, counted pro rata of the edge's length, or — on a fillet arc the stop
    /// lies closer to than to that chord — on the arc itself, measured along it. The arc is walked only when the stop can
    /// lie within tolerance of it (<see cref="TooFarFromArc"/>).
    /// </summary>
    private static (double OffFt, double ToEndNm) StopOnSegment(DirectionalEdge edge, LatLon stop)
    {
        LatLon from = edge.FromNode.Position;
        LatLon to = edge.ToNode.Position;
        double chordOffFt = GeoMath.DistanceToSegmentFt(stop, from, to);
        if ((edge.Edge is GroundArc arc) && !TooFarFromArc(arc, from, to, chordOffFt))
        {
            (double alongFt, double arcOffFt) = arc.Locate(edge.ToNode, stop);
            if (arcOffFt < chordOffFt)
            {
                return (arcOffFt, alongFt / GeoMath.FeetPerNm);
            }
        }

        double chordNm = GeoMath.DistanceNm(from, to);
        double fraction = chordNm < 1e-9 ? 0.0 : Math.Min(1.0, GeoMath.DistanceNm(stop, to) / chordNm);
        return (chordOffFt, fraction * edge.DistanceNm);
    }

    /// <summary>
    /// True when segment <paramref name="segmentIndex"/> ends at <paramref name="bar"/>'s node and the bar's stop lies on
    /// that segment, so a navigator aimed at the stop reaches it before the node. False for a stop set back past the
    /// segment's start (a setback longer than the segment), which the taxi phase takes on an earlier segment.
    /// </summary>
    /// <param name="segmentIndex">Index of the segment to test.</param>
    /// <param name="bar">The bar.</param>
    /// <returns>True when the stop is on the segment that ends at the bar's node.</returns>
    internal bool StopLiesOnSegment(int segmentIndex, HoldShortPoint bar) =>
        (segmentIndex >= 0)
        && (segmentIndex < Segments.Count)
        && (Segments[segmentIndex].ToNodeId == bar.NodeId)
        && (HoldShortSetbackNm(segmentIndex, bar) <= Segments[segmentIndex].Edge.DistanceNm + StopOnSegmentToleranceNm);

    /// <summary>A taxiway as the summary names it: "left on C" / "right on A" when the controller gave it a turn glyph.</summary>
    private static string WithTurnHint(IReadOnlyDictionary<string, TurnDirection>? turnHints, string twy) =>
        ((turnHints is not null) && turnHints.TryGetValue(twy, out TurnDirection dir))
            ? $"{(dir == TurnDirection.Left ? "left" : "right")} on {twy}"
            : twy;

    public string ToSummary() => ToSummary(null, []);

    public string ToSummary(IReadOnlyDictionary<string, TurnDirection>? turnHints) => ToSummary(turnHints, []);

    /// <summary>
    /// Build a human-readable taxi route summary (e.g., "S T U W W1 HS 28L, RWY 30"). When
    /// <paramref name="turnHints"/> is supplied (keyed by taxiway name), a cleared taxiway the
    /// controller prefixed with a turn glyph (<c>&gt;A</c> / <c>&lt;C</c>) renders as "right on A" /
    /// "left on C" — matching the pilot readback — so the controller's echo confirms the requested turn.
    /// A runway taxied ALONG renders as "on 28R" (7110.65 §3-7-2.a "ON (runway)"), with the single cleared
    /// end resolved from <paramref name="clearedRunways"/> (pass the command's taxi path; non-runway tokens
    /// are ignored).
    /// </summary>
    public string ToSummary(IReadOnlyDictionary<string, TurnDirection>? turnHints, IReadOnlyCollection<string> clearedRunways) =>
        ToSummary(turnHints, clearedRunways, static _ => true, []);

    /// <summary>
    /// The summary above naming only the taxiways <paramref name="includeTaxiway"/> keeps: the TAXI readback
    /// renders the clearance as issued, not every lane the driven path adds. Runways taxied along, hold-shorts
    /// and the destination are always shown. A taxiway left adjacent to itself by a dropped leg is named once.
    /// <paramref name="issuedBeyondRoute"/> are issued taxiways the route stops short of (it ends at the junction
    /// where they begin); they follow the driven ones, before the hold-shorts, so the readback is the route as issued.
    /// </summary>
    public string ToSummary(
        IReadOnlyDictionary<string, TurnDirection>? turnHints,
        IReadOnlyCollection<string> clearedRunways,
        Func<string, bool> includeTaxiway,
        IReadOnlyList<string> issuedBeyondRoute
    )
    {
        var parts = new List<string>();
        string? lastShown = null;
        bool droppedSinceLast = false;
        foreach ((string? twy, bool isRunway) in TaxiwaySequence(clearedRunways))
        {
            if (!isRunway && !includeTaxiway(twy))
            {
                droppedSinceLast = true;
                continue;
            }

            if (droppedSinceLast && string.Equals(twy, lastShown, StringComparison.OrdinalIgnoreCase))
            {
                droppedSinceLast = false;
                continue;
            }

            lastShown = twy;
            droppedSinceLast = false;
            if (isRunway)
            {
                parts.Add($"on {twy}");
            }
            else
            {
                parts.Add(WithTurnHint(turnHints, twy));
            }
        }

        foreach (string twy in issuedBeyondRoute)
        {
            if (!string.Equals(twy, lastShown, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(WithTurnHint(turnHints, twy));
                lastShown = twy;
            }
        }

        // Emit each explicit hold-short. A runway bar is located with "at (taxiway)" per 7110.65
        // §3-7-2 phraseology, and shows the single commanded end when the command context names one
        // ("HS 33 at F, HS 33 at C" — two distinct stops, never collapsed into one entry; hiding an
        // armed bar from the echo is how a hold-short gets missed). Only true duplicates of the same
        // bar text collapse — a taxiway hold-short annotated at several adjacent nodes still reads
        // "HS B" once.
        string? lastHoldShort = null;
        for (int i = 0; i < HoldShortPoints.Count; i++)
        {
            HoldShortPoint hs = HoldShortPoints[i];
            if (hs.Reason != HoldShortReason.ExplicitHoldShort || hs.TargetName is null)
            {
                continue;
            }

            string entry = DisplayHoldShortTarget(hs, clearedRunways);
            if (string.Equals(entry, lastHoldShort, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lastHoldShort is not null)
            {
                parts[^1] += ",";
            }

            parts.Add("HS");
            parts.Add(entry);
            lastHoldShort = entry;
        }

        // Append destination runway assignment
        foreach (HoldShortPoint hs in HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.DestinationRunway && hs.TargetName is not null)
            {
                parts.Add("RWY");
                parts.Add(hs.TargetName);
                break;
            }
        }

        // Append parking or spot destination
        if (DestinationParking is not null)
        {
            parts.Add($"@{DestinationParking}");
        }
        else if (DestinationSpot is not null)
        {
            parts.Add($"${DestinationSpot}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Operator-facing text for one explicit hold-short. A runway bar shows the single commanded
    /// end when the command's path names one (else the combined pair) and is located with
    /// "at (taxiway)" so two bars for the same runway on one route read as the two distinct stops
    /// they are. A taxiway hold-short stays the bare name.
    /// </summary>
    private string DisplayHoldShortTarget(HoldShortPoint hs, IReadOnlyCollection<string> clearedRunways)
    {
        string target = hs.TargetName!;
        GroundNode? node = FindRouteNode(hs.NodeId);
        if (node is null || node.Type != GroundNodeType.RunwayHoldShort)
        {
            return target;
        }

        string display = target;
        var id = RunwayIdentifier.Parse(target);
        foreach (string token in clearedRunways)
        {
            if (id.Contains(token))
            {
                display = RunwayIdentifier.ToDisplayDesignator(token);
                break;
            }
        }

        foreach (IGroundEdge edge in node.Edges)
        {
            string name = edge.TaxiwayName;
            if (edge is GroundArc arc)
            {
                string? arcName = Array.Find(arc.TaxiwayNames, n => !IsRunwayOrRampName(n));
                if (arcName is null)
                {
                    continue;
                }

                name = arcName;
            }

            if (!IsRunwayOrRampName(name))
            {
                return $"{display} at {name}";
            }
        }

        return display;
    }

    private static bool IsRunwayOrRampName(string name) =>
        name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase);

    /// <summary>The route's node instance for <paramref name="nodeId"/>, or null when no segment touches it.</summary>
    private GroundNode? FindRouteNode(int nodeId)
    {
        foreach (TaxiRouteSegment seg in Segments)
        {
            if (seg.FromNodeId == nodeId)
            {
                return seg.Edge.FromNode;
            }

            if (seg.ToNodeId == nodeId)
            {
                return seg.Edge.ToNode;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="segment"/> as a snapshot segment: its end node ids and taxiway, and — for a free-space leg — the
    /// positions of its virtual ends. <see cref="ResolveSegment"/> restores it.
    /// </summary>
    public static TaxiSegmentDto ToSegmentSnapshot(TaxiRouteSegment segment) =>
        new()
        {
            FromNodeId = segment.FromNodeId,
            ToNodeId = segment.ToNodeId,
            TaxiwayName = segment.TaxiwayName,
            IsFreeSpace = VirtualNode.IsVirtualEdge(segment.Edge.Edge),
            FromLatitude = segment.FromNodeId < 0 ? segment.Edge.FromNode.Position.Lat : null,
            FromLongitude = segment.FromNodeId < 0 ? segment.Edge.FromNode.Position.Lon : null,
            ToLatitude = segment.ToNodeId < 0 ? segment.Edge.ToNode.Position.Lat : null,
            ToLongitude = segment.ToNodeId < 0 ? segment.Edge.ToNode.Position.Lon : null,
        };

    public TaxiRouteDto ToSnapshot() =>
        new()
        {
            Segments = [.. Segments.Select(ToSegmentSnapshot)],
            CurrentSegmentIndex = CurrentSegmentIndex,
            HoldShortPoints =
            [
                .. HoldShortPoints.Select(hs => new HoldShortPointDto
                {
                    NodeId = hs.NodeId,
                    RunwayId = hs.TargetName ?? "",
                    IsSatisfied = hs.IsCleared,
                    Latitude = hs.Latitude,
                    Longitude = hs.Longitude,
                    Reason = hs.Reason,
                    ClearedByAutoCross = hs.ClearedByAutoCross,
                    RecrossesLandingRunway = hs.RecrossesLandingRunway ? true : null,
                    TailOverRunwayNodeId = hs.TailOverRunwayNodeId,
                    Unable = hs.Unable,
                    StopMoved = hs.StopMoved ? true : null,
                    MergeHoldOnTaxiway = hs.MergeHold?.OnTaxiway,
                    MergeHoldBeforeTaxiway = hs.MergeHold?.BeforeTaxiway,
                    MergeHoldDeclined = hs.MergeHoldDeclined ? true : null,
                }),
            ],
            Description = ToSummary(),
            DestinationParking = DestinationParking,
            DestinationSpot = DestinationSpot,
            SpotLineUpPullFromSegment = SpotLineUpPullFromSegment,
            TurnAboutShape = TurnAboutShape,
            TurnAboutTargetNodeId = TurnAboutTargetNodeId,
        };

    /// <summary>
    /// A snapshot segment endpoint: the layout node by id, or — for a virtual node the layout never held —
    /// a fresh <see cref="VirtualNode"/> at the recorded position. Null when neither is available.
    /// </summary>
    private static GroundNode? ResolveSnapshotNode(AirportGroundLayout layout, int nodeId, double? latitude, double? longitude)
    {
        if (layout.Nodes.TryGetValue(nodeId, out GroundNode? node))
        {
            return node;
        }

        return latitude is { } lat && longitude is { } lon ? VirtualNode.Create(lat, lon) : null;
    }

    /// <summary>
    /// The snapshot segment <paramref name="seg"/> over <paramref name="layout"/>: the first of its from node's edges to its to
    /// node, pointed that way — every snapshot segment resolves through this, so two nodes joined by more than one edge restore
    /// to the same one everywhere — or, for a free-space leg (a ramp-lane cut), the virtual edge rebuilt from its ends. Null when
    /// an end node or the edge is not in the layout.
    /// </summary>
    public static TaxiRouteSegment? ResolveSegment(AirportGroundLayout layout, TaxiSegmentDto seg)
    {
        GroundNode? fromNode = ResolveSnapshotNode(layout, seg.FromNodeId, seg.FromLatitude, seg.FromLongitude);
        GroundNode? toNode = ResolveSnapshotNode(layout, seg.ToNodeId, seg.ToLatitude, seg.ToLongitude);
        if ((fromNode is null) || (toNode is null))
        {
            return null;
        }

        if ((fromNode.Id < 0) || (toNode.Id < 0) || seg.IsFreeSpace)
        {
            return VirtualNode.CreateSegment(fromNode, toNode, seg.TaxiwayName ?? "");
        }

        IGroundEdge? edge = fromNode.Edges.FirstOrDefault(e => e.HasNode(seg.ToNodeId));
        return edge is null
            ? null
            : new TaxiRouteSegment { TaxiwayName = seg.TaxiwayName ?? edge.TaxiwayName, Edge = edge.Directed(fromNode, toNode) };
    }

    public static TaxiRoute? FromSnapshot(TaxiRouteDto dto, AirportGroundLayout? layout)
    {
        if (layout is null)
        {
            return null;
        }

        List<TaxiRouteSegment> segments = [];
        foreach (TaxiSegmentDto seg in dto.Segments)
        {
            if (ResolveSegment(layout, seg) is not { } segment)
            {
                return null;
            }

            segments.Add(segment);
        }

        List<HoldShortPoint> holdShorts = [];
        if (dto.HoldShortPoints is not null)
        {
            foreach (HoldShortPointDto hs in dto.HoldShortPoints)
            {
                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = hs.NodeId,
                        Reason = hs.Reason ?? HoldShortReason.ExplicitHoldShort,
                        TargetName = hs.RunwayId,
                        IsCleared = hs.IsSatisfied,
                        ClearedByAutoCross = hs.ClearedByAutoCross,
                        RecrossesLandingRunway = hs.RecrossesLandingRunway ?? false,
                        Latitude = hs.Latitude,
                        Longitude = hs.Longitude,
                        TailOverRunwayNodeId = hs.TailOverRunwayNodeId,
                        Unable = hs.Unable,
                        StopMoved = hs.StopMoved == true,
                        MergeHold = (hs.MergeHoldOnTaxiway is { } on) && (hs.MergeHoldBeforeTaxiway is { } before) ? new MergeHold(on, before) : null,
                        MergeHoldDeclined = hs.MergeHoldDeclined == true,
                    }
                );
            }
        }

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
            CurrentSegmentIndex = dto.CurrentSegmentIndex,
            DestinationParking = dto.DestinationParking,
            DestinationSpot = dto.DestinationSpot,
            SpotLineUpPullFromSegment = dto.SpotLineUpPullFromSegment,
            TurnAboutShape = dto.TurnAboutShape,
            TurnAboutTargetNodeId = dto.TurnAboutTargetNodeId,
        };
    }
}

public sealed class TaxiRouteSegment
{
    public required DirectionalEdge Edge { get; init; }
    public required string TaxiwayName { get; init; }

    public int FromNodeId => Edge.FromNodeId;
    public int ToNodeId => Edge.ToNodeId;
}

public enum HoldShortReason
{
    RunwayCrossing,
    ExplicitHoldShort,
    DestinationRunway,

    /// <summary>
    /// The end of a route that could not reach its destination as cleared: the aircraft holds short of the taxiway
    /// the destination needs and the clearance did not name. It stops there like an explicit hold-short, but it is
    /// the resolver's, not the controller's: a later <c>HS</c> of that taxiway or a new TAXI replaces it.
    /// </summary>
    RouteIncomplete,
}

public sealed class HoldShortPoint
{
    public required int NodeId { get; init; }
    public required HoldShortReason Reason { get; set; }

    /// <summary>Runway ID or taxiway name this hold-short protects.</summary>
    public string? TargetName { get; init; }

    /// <summary>Whether this hold-short has been cleared (e.g., CROSS command issued).</summary>
    public bool IsCleared { get; set; }

    /// <summary>
    /// True when <see cref="IsCleared"/> was set by the AutoCrossRunway scenario toggle
    /// (either at TAXI-resolution time or via a mid-session toggle that re-evaluated
    /// already-active routes). Distinguishes AutoCross-driven clearance from other
    /// sources (first-crossing-resume, explicit CROSS keyword, future user CTO commands)
    /// so toggling AutoCross OFF only reverts the clearances it owns.
    /// </summary>
    public bool ClearedByAutoCross { get; set; }

    /// <summary>
    /// True at a re-crossing of the runway just landed on, on a route a TAXI stored during the landing rollout: the AutoCrossRunway
    /// scenario toggle never clears it, at TAXI time or mid-session, so only an explicit <c>CROSS</c> does (7110.65 3-10-9.b.2).
    /// </summary>
    public bool RecrossesLandingRunway { get; set; }

    /// <summary>
    /// Computed hold-short position. For taxiway hold-shorts, this is offset from the
    /// intersection node by the aircraft's fuselage length + buffer. For runway hold-shorts,
    /// this is the node position itself. Null when not yet computed (legacy snapshots).
    /// </summary>
    public double? Latitude { get; set; }

    /// <summary>
    /// Computed hold-short position longitude. See <see cref="Latitude"/>.
    /// </summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// True when the aircraft was already inside its own braking distance of this bar when the bar was
    /// armed, so the painted stop cannot be made. <see cref="Latitude"/>/<see cref="Longitude"/> are then
    /// moved forward to the point the aircraft can actually stop at (never past the node the bar protects),
    /// the taxi brakes at the firm rate onto it, and the pilot answers "unable … stopping" instead of
    /// reading back a hold it will not fly. Set once, at the moment the bar is armed; a bar the aircraft has
    /// room for is never flagged. Setting it drops any <see cref="MergeHold"/>: the moved stop is no longer before the
    /// capping intersection, so the hold report must not say it is.
    /// </summary>
    public bool Unable
    {
        get;
        set
        {
            field = value;
            if (value)
            {
                MergeHold = null;
            }
        }
    }

    /// <summary>
    /// True once the taxi phase has moved this <see cref="Unable"/> bar's stop forward to the point the aircraft can
    /// reach. Kept on the bar rather than the phase so the stop is moved once per bar: a later taxi phase over the same
    /// route — a follow resuming its assigned route, a restore — would otherwise project it again from wherever the
    /// aircraft then stands.
    /// </summary>
    public bool StopMoved { get; set; }

    /// <summary>
    /// When this taxiway hold-short sits within a fuselage length past a runway the route crosses, the
    /// aircraft holds at the taxiway line with its tail over the runway's hold-short bars and cannot
    /// fully clear the runway (issue #172). This is the runway hold-short node the tail hangs over; the
    /// runway is "not clear" while the aircraft holds here. Null in the normal case.
    /// </summary>
    public int? TailOverRunwayNodeId { get; set; }

    /// <summary>
    /// Set when this taxiway hold-short binds a shallow merge (the arriving taxiway runs into the target at a few degrees)
    /// and an intersection before the merge caps the wingtip-floor walk-back: the aircraft then holds short of that whole
    /// intersection, nose at its near edge, and the readback and the hold report name where ("will hold on D before G").
    /// Null for every other placement. Set by <see cref="HoldShortAnnotator.ComputeHoldShortPositions"/>.
    /// </summary>
    public MergeHold? MergeHold { get; set; }

    /// <summary>
    /// True once the aircraft was found already past the <see cref="MergeHold"/> stop, or unable to brake to it, when the
    /// hold-short was armed: placement then keeps the stop capped at the first junction, with the wingtip warning and the
    /// plain readback, on every recompute.
    /// </summary>
    public bool MergeHoldDeclined { get; set; }
}

/// <summary>
/// Where a hold-short at a shallow merge holds instead of at the merge: on <paramref name="OnTaxiway"/> (the taxiway the
/// aircraft arrives on) before <paramref name="BeforeTaxiway"/> (the taxiway of the intersection that capped the walk-back).
/// </summary>
public sealed record MergeHold(string OnTaxiway, string BeforeTaxiway);
