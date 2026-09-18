using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Data.Airport;

/// <summary>What a <see cref="TugGoal"/> asks the tug to do.</summary>
public enum TugGoalKind
{
    /// <summary>End on a painted ramp spot, nose-out, with the nosewheel on the mark.</summary>
    Spot,

    /// <summary>End on a stand (parking or helipad node), on the node's own heading.</summary>
    Stand,

    /// <summary>End on a ground node, on a given facing or on whatever facing the move leaves.</summary>
    Node,

    /// <summary>Push onto a taxiway's centreline through an exit node and stop once lined up on a facing.</summary>
    TaxiwayLine,

    /// <summary>
    /// Push back onto the taxiway behind the aircraft: straight back when the push crosses it, else onto a stretch
    /// running alongside the push.
    /// </summary>
    StraightBackTo,

    /// <summary>Push back while turning the nose onto a facing.</summary>
    Facing,

    /// <summary>Push straight back the type's simple pushback distance.</summary>
    Clear,
}

/// <summary>One thing a tug move is asked to reach, in request order. Built only through the factories.</summary>
public sealed record TugGoal
{
    private TugGoal() { }

    /// <summary>What the goal asks for.</summary>
    public required TugGoalKind Kind { get; init; }

    /// <summary>The goal's node: the spot, stand or node to end on, or the taxiway exit node. Null for facing and clear goals.</summary>
    public GroundNode? Node { get; init; }

    /// <summary>The goal's point (the node's position), or null for facing and clear goals.</summary>
    public LatLon? Point { get; init; }

    /// <summary>The nose heading to end on, degrees true, when the goal carries one.</summary>
    public double? FacingTrueDeg { get; init; }

    /// <summary>The taxiway a <see cref="TugGoalKind.TaxiwayLine"/> or <see cref="TugGoalKind.StraightBackTo"/> goal names.</summary>
    public string? TaxiwayName { get; init; }

    /// <summary>How a refusal names the goal: <c>spot 6B</c>, <c>D16</c>, <c>taxiway Y</c>, <c>the pushback</c>.</summary>
    public required string Label { get; init; }

    /// <summary>End on a painted ramp spot, nose-out.</summary>
    /// <param name="spot">The spot node.</param>
    /// <returns>The goal.</returns>
    public static TugGoal Spot(GroundNode spot) =>
        new()
        {
            Kind = TugGoalKind.Spot,
            Node = spot,
            Point = spot.Position,
            Label = $"spot {NodeName(spot)}",
        };

    /// <summary>End on a stand, on the stand's own heading.</summary>
    /// <param name="stand">The parking or helipad node.</param>
    /// <returns>The goal.</returns>
    public static TugGoal Stand(GroundNode stand) =>
        new()
        {
            Kind = TugGoalKind.Stand,
            Node = stand,
            Point = stand.Position,
            Label = NodeName(stand),
        };

    /// <summary>
    /// End on a ground node (a <see cref="TugGoalKind.Node"/> goal; named <c>AtNode</c> because <see cref="Node"/> is
    /// the goal's node).
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="facingTrueDeg">The nose heading to end on, degrees true, or null for whatever facing the move leaves.</param>
    /// <returns>The goal.</returns>
    public static TugGoal AtNode(GroundNode node, double? facingTrueDeg) =>
        new()
        {
            Kind = TugGoalKind.Node,
            Node = node,
            Point = node.Position,
            FacingTrueDeg = facingTrueDeg is { } facing ? new TrueHeading(facing).Degrees : null,
            Label = NodeName(node),
        };

    /// <summary>Push onto a taxiway centreline and stop once lined up (<c>PUSH &lt;taxiway&gt; &lt;facing&gt;</c>).</summary>
    /// <param name="exitNode">The node on the taxiway the line runs through.</param>
    /// <param name="taxiway">The taxiway's name.</param>
    /// <param name="facingTrueDeg">The nose heading along the taxiway to end on, degrees true.</param>
    /// <returns>The goal.</returns>
    public static TugGoal TaxiwayLine(GroundNode exitNode, string taxiway, double facingTrueDeg) =>
        new()
        {
            Kind = TugGoalKind.TaxiwayLine,
            Node = exitNode,
            Point = exitNode.Position,
            FacingTrueDeg = new TrueHeading(facingTrueDeg).Degrees,
            TaxiwayName = taxiway,
            Label = $"taxiway {taxiway}",
        };

    /// <summary>
    /// Push back onto the taxiway behind the aircraft (a bare <c>PUSH &lt;taxiway&gt;</c>): straight back until the
    /// reference point reaches the first edge of the taxiway the push crosses steeply enough to be across the push,
    /// or, for a taxiway running alongside the push, onto its centreline with the nose along it.
    /// </summary>
    /// <param name="exitNode">The taxiway's exit node nearest the aircraft; it names the goal and bounds its distance.</param>
    /// <param name="taxiway">The taxiway's name.</param>
    /// <returns>The goal.</returns>
    public static TugGoal StraightBackTo(GroundNode exitNode, string taxiway) =>
        new()
        {
            Kind = TugGoalKind.StraightBackTo,
            Node = exitNode,
            Point = exitNode.Position,
            TaxiwayName = taxiway,
            Label = $"taxiway {taxiway}",
        };

    /// <summary>Push back while turning onto a facing (<c>PUSH FACE</c> / <c>PUSH TAIL</c>).</summary>
    /// <param name="facingTrueDeg">The nose heading to end on, degrees true.</param>
    /// <returns>The goal.</returns>
    public static TugGoal Facing(double facingTrueDeg) =>
        new()
        {
            Kind = TugGoalKind.Facing,
            FacingTrueDeg = new TrueHeading(facingTrueDeg).Degrees,
            Label = "the pushback turn",
        };

    /// <summary>Push straight back the simple pushback distance (a bare <c>PUSH</c>).</summary>
    /// <returns>The goal.</returns>
    public static TugGoal Clear() => new() { Kind = TugGoalKind.Clear, Label = "the pushback" };

    private static string NodeName(GroundNode node) => node.Name ?? $"node {node.Id}";
}

/// <summary>
/// What a stand push-off remembers about the single goal of the <c>PUSH</c> that started it, so a mid-push
/// <c>PUSH FACE</c> / <c>PUSH TAIL</c> (issue #167) can re-plan the same goal on a new facing from the same stand.
/// Only a one-goal <c>PUSH</c> whose goal is a <see cref="TugGoalKind.Facing"/>, <see cref="TugGoalKind.Spot"/> or
/// <see cref="TugGoalKind.TaxiwayLine"/> carries one; a push to a stand keeps the stand's own heading. Plain scalars,
/// so a snapshot carries it field for field.
/// </summary>
/// <param name="GoalKind">The goal's kind.</param>
/// <param name="NodeId">The spot or taxiway exit node's id; null for a facing goal.</param>
/// <param name="TaxiwayName">The taxiway a taxiway-line goal names; null otherwise.</param>
/// <param name="StandStart">The pose the aircraft was in on the stand when the push began.</param>
public sealed record TugAmendment(TugGoalKind GoalKind, int? NodeId, string? TaxiwayName, TugPose StandStart)
{
    /// <summary>The amendment for a goal, or null for a goal kind a mid-push facing change cannot re-plan.</summary>
    /// <param name="goal">The single goal of the push.</param>
    /// <param name="standStart">The pose on the stand when the push began.</param>
    /// <returns>The amendment, or null.</returns>
    public static TugAmendment? For(TugGoal goal, TugPose standStart) =>
        goal.Kind switch
        {
            TugGoalKind.Facing => new TugAmendment(goal.Kind, null, null, standStart),
            TugGoalKind.Spot => new TugAmendment(goal.Kind, goal.Node!.Id, null, standStart),
            TugGoalKind.TaxiwayLine => new TugAmendment(goal.Kind, goal.Node!.Id, goal.TaxiwayName, standStart),
            _ => null,
        };
}

/// <summary>What to plan: where the aircraft is, what it is, and where the tug is to take it.</summary>
public sealed record TugRequest
{
    /// <summary>The aircraft's pose now.</summary>
    public required TugPose Start { get; init; }

    /// <summary>The aircraft is parked on a stand, so the plan starts with a straight push-off.</summary>
    public required bool StartsAtStand { get; init; }

    /// <summary>ICAO type designator; sets the turn radius and the footprint.</summary>
    public required string AircraftType { get; init; }

    /// <summary>The goals, in order; at least one.</summary>
    public required IReadOnlyList<TugGoal> Goals { get; init; }

    /// <summary>A facing that overrides the last goal's, degrees true, or null.</summary>
    public required double? FinalFacingTrueDeg { get; init; }

    /// <summary>
    /// The kind of the tug move this plan replaces while that move is under way, or null when the aircraft is not
    /// under tow. A first planned move of the other kind is a reversal, so it dwells before it starts.
    /// </summary>
    public required PushbackLegKind? PreviousKind { get; init; }
}

/// <summary>A planned tug move: every move with its simulated path, and where the aircraft ends up.</summary>
/// <param name="Moves">The moves in order; each trace carries its <see cref="TugMove"/>.</param>
/// <param name="End">The simulated end pose.</param>
public sealed record TugPlan(IReadOnlyList<TugMoveTrace> Moves, TugPose End);

/// <summary>
/// Plans a tug move — every <c>PUSH</c> form and <c>PUSHM</c> — as a chain of <see cref="TugMove"/>s flown by
/// <see cref="TugKinematics"/>. Goals are planned greedily in order: each goal's candidate move lists are
/// simulated from where the previous goal left the aircraft, the unflyable or unsafe ones are dropped, and the
/// best survivor is kept. Pure geometry: it moves nothing and reads no aircraft state.
///
/// <para><b>Stand start.</b> Off a stand the plan opens with a straight push of half the fuselage length
/// (<see cref="TugGoalKind.Clear"/> and <see cref="TugGoalKind.StraightBackTo"/> goals are straight pushes
/// already and carry no separate push-off), and the next move must be a push too.</para>
///
/// <para><b>A goal with a facing</b> (a spot, a stand, a node or the last goal with an explicit facing) is
/// reached along its approach line — through the stop point along the facing, from either side: pulled onto it or
/// pushed onto it. The side test (a pull when the stop is within 90° of the facing as seen from the aircraft, else a
/// push; the push first within 5° of that boundary) only orders the two sides. Three templates are tried per side: the side move straight onto the line (a spot push stops at the
/// staging point and creeps forward onto the mark); the other kind onto the line first; and, when the nose has to
/// rotate more than 30°, a three-point turn first — the other kind turns the nose to 90°, 60° or 30° either side of
/// the facing, six variants. The final pull onto a spot is always a creep. The fewest reversals win, then the
/// shortest path, then the template order, then the side order.</para>
///
/// <para><b>Back onto a taxiway.</b> A bare <c>PUSH &lt;taxiway&gt;</c> splits on one angle, 45°
/// (<see cref="TugPlanBuilder.AcrossAngleDeg"/>), between a taxiway across the push and one alongside it. Across:
/// the push ray crosses an edge of that taxiway at 45° or more, and the aircraft pushes straight back until the
/// reference point reaches the first such crossing. Alongside: no edge crosses it that steeply, but one runs closer
/// than 45° to the push direction behind the aircraft, and the aircraft pushes off the stand and then onto that edge's
/// centreline in an S-curve. The push ends where the S-curve captures the centreline when that already leaves the
/// aircraft within the corridor of the taxiway's extent (<see cref="TugPlanBuilder.OnTaxiwayCorridorFt"/> of a
/// straight centreline edge, between that edge's ends); when the capture ends outside that corridor, the push carries
/// on along the line until it reaches the edge's point nearest the aircraft. Either way it ends on the taxiway.
/// Otherwise the taxiway is not behind the aircraft and the push is refused.</para>
///
/// <para><b>Room before a reversal.</b> A faced-goal candidate dropped only because its final pull onto the stop runs
/// past it by up to <see cref="TugPlanBuilder.MaxRoomRetryOvershootFt"/> is retried once with a straight move of the
/// kind before the reversal, 1.5 × the overshoot + 10 ft long, inserted just before that reversal; the retry is
/// judged like any other candidate.</para>
///
/// <para><b>Refusals.</b> A goal further than <see cref="MaxGoalDistanceFt"/> away, a goal on a runway holding
/// position, and a plan whose flown path puts the aircraft's footprint near a runway, across an edge touching a
/// runway holding position, or its fuselage across movement-area pavement it was not sent to. A move that starts on
/// a stand may also cross the taxiway straight behind it: the first movement-area edge within
/// <see cref="StandBehindExemptionFt"/> of the stand along the push-off direction is exempt for the whole plan, since
/// the push clearance covers pushing onto that taxiway and pulling back off it. Open apron is not checked: the layout
/// carries no pavement polygons.</para>
/// </summary>
public static class TugMovePlanner
{
    private static readonly ILogger Log = SimLog.CreateLogger("TugMovePlanner");

    /// <summary>
    /// The farthest a goal may be from where the aircraft is when the planner reaches it, feet. A UI sanity bound
    /// against a mis-click on the ground view, not an aviation rule: nothing in the AIM or AC 00-65A caps how far a
    /// tug may move an aircraft.
    /// </summary>
    public const double MaxGoalDistanceFt = 2000.0;

    /// <summary>
    /// How far behind a stand, along the push-off direction, the taxiway a push may cross is looked for, feet; a
    /// judgement call. It covers SFO's taxiway Y, 186 ft behind the B gates, for a B738 and an E75L alike.
    /// </summary>
    public const double StandBehindExemptionFt = 300.0;

    /// <summary>The simulation step every candidate is flown with, feet.</summary>
    internal const double StepFt = 1.0;

    // PUSH $spot pull-forward: clamp(factor × fuselage length, min, max) ft behind the stop point. Aviation-reviewed.
    private const double SpotPullForwardFactor = 0.75;
    private const double SpotPullForwardMinFt = 40.0;
    private const double SpotPullForwardMaxFt = 100.0;

    /// <summary>
    /// Plans a tug move.
    /// </summary>
    /// <param name="layout">
    /// The airport ground layout the move happens on, or null when the airport has none. Without a layout only
    /// <see cref="TugGoalKind.Clear"/> and <see cref="TugGoalKind.Facing"/> goals can be planned, and their flown
    /// path goes unchecked.
    /// </param>
    /// <param name="request">Where the aircraft is, what it is, and its goals.</param>
    /// <param name="refusal">Why the move cannot be planned, or empty on success.</param>
    /// <returns>The plan, or null when <paramref name="refusal"/> says why not.</returns>
    /// <exception cref="ArgumentException">
    /// The request has no goals; carries a final facing while its last goal is a <see cref="TugGoalKind.Clear"/> or
    /// <see cref="TugGoalKind.StraightBackTo"/> goal, which has no facing to override; or has no layout for a goal
    /// that needs one.
    /// </exception>
    public static TugPlan? Plan(AirportGroundLayout? layout, TugRequest request, out string refusal)
    {
        ValidateRequest(request);
        if ((layout is null) && request.Goals.Any(g => g.Kind is not (TugGoalKind.Clear or TugGoalKind.Facing)))
        {
            throw new ArgumentException(
                "Only Clear and Facing tug goals can be planned without an airport ground layout; refuse the command before planning",
                nameof(layout)
            );
        }

        var builder = new TugPlanBuilder(layout, request);
        for (int i = 0; i < request.Goals.Count; i++)
        {
            if (!builder.TryPlanGoal(i, out refusal))
            {
                Log.LogDebug("Tug move refused: {Refusal}", refusal);
                return null;
            }
        }

        refusal = string.Empty;
        return builder.ToPlan();
    }

    /// <summary>
    /// The two points a spot stop is built from, measured back along <paramref name="facingTrueDeg"/> from the
    /// marking: the <c>Stop</c>, where the reference point stops so the nosewheel — a half-fuselage ahead of it —
    /// sits on the mark (the taxi-to-spot setback, issue #234), and the <c>Staging</c> point a further
    /// clamp(0.75 × length, 40, 100) ft behind it that a push reaches before the pull forward onto the stop.
    /// </summary>
    /// <param name="spot">The spot node.</param>
    /// <param name="facingTrueDeg">The spot's nose-out heading, degrees true.</param>
    /// <param name="aircraftType">ICAO type designator; sets the fuselage length.</param>
    /// <returns>The stop and staging points.</returns>
    public static (LatLon Stop, LatLon Staging) SpotStopGeometry(GroundNode spot, double facingTrueDeg, string aircraftType)
    {
        double halfLenNm = (FuselageLengthFt(aircraftType) / 2.0) / GeoMath.FeetPerNm;
        double pullFwdNm = SpotPullForwardFt(aircraftType) / GeoMath.FeetPerNm;

        TrueHeading intoRamp = new TrueHeading(facingTrueDeg).ToReciprocal();
        return (GeoMath.ProjectPoint(spot.Position, intoRamp, halfLenNm), GeoMath.ProjectPoint(spot.Position, intoRamp, halfLenNm + pullFwdNm));
    }

    /// <summary>How far behind a spot's stop point its staging point lies, feet: clamp(0.75 × fuselage length, 40, 100).</summary>
    internal static double SpotPullForwardFt(string aircraftType) =>
        Math.Clamp(SpotPullForwardFactor * FuselageLengthFt(aircraftType), SpotPullForwardMinFt, SpotPullForwardMaxFt);

    /// <summary>The fuselage length, feet: the FAA record's, else the CWT-based fallback.</summary>
    internal static double FuselageLengthFt(string aircraftType) =>
        FaaAircraftDatabase.Get(aircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(aircraftType);

    /// <summary>
    /// The wingspan, feet: the FAA record's, else by category — Jet 118 ft, Turboprop 90 ft, Piston 36 ft,
    /// Helicopter 40 ft. The fallbacks are judgement calls.
    /// </summary>
    internal static double WingspanFt(string aircraftType) =>
        (FaaAircraftDatabase.Get(aircraftType)?.WingspanFt is { } span && (span > 0.0))
            ? span
            : FallbackWingspanFt(AircraftCategorization.Categorize(aircraftType));

    private static double FallbackWingspanFt(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 118.0,
            AircraftCategory.Turboprop => 90.0,
            AircraftCategory.Piston => 36.0,
            AircraftCategory.Helicopter => 40.0,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown aircraft category"),
        };

    /// <summary>The type's simple pushback distance, feet.</summary>
    internal static double SimplePushbackFt(string aircraftType) => CategoryPerformance.SimplePushbackDistanceNm(aircraftType) * GeoMath.FeetPerNm;

    internal static double AbsDiffDeg(double a, double b) => new TrueHeading(a).AbsAngleTo(new TrueHeading(b));

    /// <summary>How far <paramref name="point"/> lies ahead of <paramref name="reference"/> along <paramref name="headingDeg"/>, feet.</summary>
    internal static double AlongFt(LatLon point, LatLon reference, double headingDeg) =>
        GeoMath.AlongTrackDistanceNm(point, reference, new TrueHeading(headingDeg)) * GeoMath.FeetPerNm;

    private static void ValidateRequest(TugRequest request)
    {
        if (request.Goals.Count == 0)
        {
            throw new ArgumentException("A tug request needs at least one goal", nameof(request));
        }

        TugGoalKind last = request.Goals[^1].Kind;
        if ((request.FinalFacingTrueDeg is not null) && (last is TugGoalKind.Clear or TugGoalKind.StraightBackTo))
        {
            throw new ArgumentException(
                $"A {last} goal has no facing for a final facing to override; plan a Facing or TaxiwayLine goal instead",
                nameof(request)
            );
        }
    }
}

/// <summary>How the planner reaches a resolved goal.</summary>
internal enum TugGoalShape
{
    /// <summary>Along the approach line through <see cref="ResolvedTugGoal.Stop"/> on <see cref="ResolvedTugGoal.FacingTrueDeg"/>.</summary>
    Faced,

    /// <summary>Straight for the node, on whatever facing that leaves.</summary>
    ToNode,

    /// <summary>A push turning onto the facing, then the rest of the simple pushback distance.</summary>
    Facing,

    /// <summary>A straight push of the simple pushback distance.</summary>
    Clear,

    /// <summary>A straight push onto the taxiway the push crosses, or a push onto its centreline where it runs alongside.</summary>
    StraightBack,

    /// <summary>A push onto the line through the node on the facing, stopping once lined up.</summary>
    TaxiwayLine,
}

/// <summary>A goal with its facing, stop point and refusal wording worked out.</summary>
internal sealed record ResolvedTugGoal
{
    public required TugGoal Goal { get; init; }

    public required TugGoalShape Shape { get; init; }

    /// <summary>How a refusal names the goal on its own: <c>spot 6A</c>, or <c>leg 2 to spot 6B</c> in a multi-goal request.</summary>
    public required string Name { get; init; }

    /// <summary>How a refusal names the move to the goal: <c>the move to spot 6A</c>, or <c>leg 2 to spot 6B</c>.</summary>
    public required string Subject { get; init; }

    /// <summary>The nose heading to end on, degrees true (faced, facing and taxiway-line goals).</summary>
    public double FacingTrueDeg { get; init; }

    /// <summary>Where the reference point stops; on the approach line for a faced goal. The goal's node otherwise.</summary>
    public LatLon Stop { get; init; }

    /// <summary>A faced spot's staging point, where a push onto its line stops before the creep forward.</summary>
    public LatLon? Staging { get; init; }

    /// <summary>Taxiway names the movement-area check lets the fuselage cross.</summary>
    public required IReadOnlySet<string> ExemptNames { get; init; }
}

/// <summary>Turns a request's goal into a <see cref="ResolvedTugGoal"/>.</summary>
internal static class TugGoalResolver
{
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static ResolvedTugGoal Resolve(AirportGroundLayout? layout, TugRequest request, int index)
    {
        TugGoal goal = request.Goals[index];
        bool isLast = index == (request.Goals.Count - 1);
        double? facing = FacingOf(layout, goal, isLast ? request.FinalFacingTrueDeg : null);
        ResolvedTugGoal basis = Basis(goal, index, request.Goals.Count);
        return goal.Kind switch
        {
            TugGoalKind.Spot or TugGoalKind.Stand or TugGoalKind.Node => ResolveNodeGoal(request.AircraftType, basis, facing),
            TugGoalKind.TaxiwayLine or TugGoalKind.StraightBackTo => ResolveTaxiwayGoal(basis, facing),
            TugGoalKind.Facing => basis with { Shape = TugGoalShape.Facing, FacingTrueDeg = facing!.Value },
            TugGoalKind.Clear => basis,
            _ => throw new ArgumentOutOfRangeException(nameof(request), goal.Kind, "Unknown tug goal kind"),
        };
    }

    /// <summary>
    /// The facing a goal ends on: the override, else a spot's nose-out heading, a stand's own heading, or the
    /// facing the goal carries. Null when none applies.
    /// </summary>
    private static double? FacingOf(AirportGroundLayout? layout, TugGoal goal, double? overrideDeg) =>
        overrideDeg
        ?? goal.Kind switch
        {
            TugGoalKind.Spot => layout!.TryGetSpotOutboundHeading(goal.Node!, out double outbound) ? outbound : null,
            TugGoalKind.Stand => goal.Node!.TrueHeading?.Degrees,
            _ => goal.FacingTrueDeg,
        };

    private static ResolvedTugGoal Basis(TugGoal goal, int index, int count)
    {
        bool multi = count > 1;
        string name = multi ? $"leg {index + 1} to {goal.Label}" : goal.Label;
        bool unplaced = goal.Kind is TugGoalKind.Clear or TugGoalKind.Facing;
        string subject = (multi || unplaced) ? name : $"the move to {goal.Label}";
        return new ResolvedTugGoal
        {
            Goal = goal,
            Shape = TugGoalShape.Clear,
            Name = name,
            Subject = subject,
            ExemptNames = NoNames,
        };
    }

    /// <summary>
    /// A spot, stand or node goal. With no facing it is reached like a bare node; a faced spot stops a half-fuselage
    /// behind its mark with a staging point behind that; a faced stand or node stops on the node.
    /// </summary>
    private static ResolvedTugGoal ResolveNodeGoal(string aircraftType, ResolvedTugGoal basis, double? facing)
    {
        GroundNode node = basis.Goal.Node!;
        if (facing is not { } facingDeg)
        {
            return ToNode(basis, node);
        }

        ResolvedTugGoal faced = basis with { Shape = TugGoalShape.Faced, FacingTrueDeg = facingDeg, Stop = node.Position };
        switch (basis.Goal.Kind)
        {
            case TugGoalKind.Spot:
                (LatLon stop, LatLon staging) = TugMovePlanner.SpotStopGeometry(node, facingDeg, aircraftType);
                return faced with { Stop = stop, Staging = staging };
            case TugGoalKind.Node:
                return faced with { ExemptNames = NodeEdgeNames(node) };
            default:
                return faced;
        }
    }

    /// <summary>A taxiway-line or straight-back goal: the line runs through the exit node, and the taxiway is exempt.</summary>
    private static ResolvedTugGoal ResolveTaxiwayGoal(ResolvedTugGoal basis, double? facing)
    {
        TugGoal goal = basis.Goal;
        ResolvedTugGoal onTaxiway = basis with { Stop = goal.Node!.Position, ExemptNames = Names(goal.TaxiwayName!) };
        return goal.Kind == TugGoalKind.TaxiwayLine
            ? onTaxiway with
            {
                Shape = TugGoalShape.TaxiwayLine,
                FacingTrueDeg = facing!.Value,
            }
            : onTaxiway with
            {
                Shape = TugGoalShape.StraightBack,
            };
    }

    private static ResolvedTugGoal ToNode(ResolvedTugGoal basis, GroundNode node) =>
        basis with
        {
            Shape = TugGoalShape.ToNode,
            Stop = node.Position,
            ExemptNames = NodeEdgeNames(node),
        };

    private static IReadOnlySet<string> NodeEdgeNames(GroundNode node) =>
        new HashSet<string>(node.Edges.SelectMany(RampLaneReposition.EdgeNames), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Names(string name) => new HashSet<string>([name], StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A maximal sequence of consecutive same-kind moves, counted across goals: the direction of travel it started
/// with, whether it holds a <see cref="TugMoveShape.TurnTo"/>, and how far its travel has wandered.
/// </summary>
internal readonly record struct TugRun(PushbackLegKind Kind, double StartTravelDeg, bool HasTurn, double MaxDeviationDeg)
{
    /// <summary>A run without a turn that wanders further than this from its start travel is a loop; a judgement call.</summary>
    internal const double MaxWanderDeg = 120.0;

    internal bool Wandered => !HasTurn && (MaxDeviationDeg > MaxWanderDeg);

    /// <summary>Extends the open run through the traces; reports the run left open and whether any run wandered.</summary>
    internal static (TugRun? Open, bool Wandered) Follow(TugRun? open, IReadOnlyList<TugMoveTrace> traces)
    {
        bool wandered = false;
        TugRun? run = open;
        foreach (TugMoveTrace trace in traces)
        {
            PushbackLegKind kind = trace.Move.Kind;
            if ((run is not { } current) || (current.Kind != kind))
            {
                wandered |= run is { Wandered: true };
                current = new TugRun(kind, trace.Samples[0].TravelTrueDeg(kind), false, 0.0);
            }

            run = current.Including(trace);
        }

        return (run, wandered || run is { Wandered: true });
    }

    private TugRun Including(TugMoveTrace trace)
    {
        PushbackLegKind kind = Kind;
        double start = StartTravelDeg;
        double deviation = trace.Samples.Max(s => TugMovePlanner.AbsDiffDeg(s.TravelTrueDeg(kind), start));
        return this with { HasTurn = HasTurn || (trace.Move.Shape == TugMoveShape.TurnTo), MaxDeviationDeg = Math.Max(MaxDeviationDeg, deviation) };
    }
}

/// <summary>One candidate move list for a goal, simulated move by move as it is built.</summary>
internal sealed class TugCandidate
{
    private readonly string _aircraftType;
    private readonly List<TugMoveTrace> _traces = [];

    internal TugCandidate(string template, string aircraftType, TugPose start, PushbackLegKind? previousKind)
    {
        Template = template;
        _aircraftType = aircraftType;
        End = start;
        LastKind = previousKind;
    }

    internal string Template { get; }

    internal TugPose End { get; private set; }

    internal PushbackLegKind? LastKind { get; private set; }

    /// <summary>Every move so far finished inside its travel budget.</summary>
    internal bool Flyable { get; private set; } = true;

    internal IReadOnlyList<TugMoveTrace> Traces => _traces;

    internal int Reversals => _traces.Count(t => t.Move.DwellBefore);

    internal double PathLengthFt => _traces.Sum(t => t.PathLengthFt);

    internal string Describe() => string.Join(", ", _traces.Select(t => $"{t.Move.Kind} {t.Move.Shape} {t.PathLengthFt:F0} ft"));

    /// <summary>
    /// Flies the move from the current end, marking it a reversal when its kind differs from the previous move's.
    /// Does nothing once a move has proved unflyable.
    /// </summary>
    internal void Add(TugMove move)
    {
        if (!Flyable)
        {
            return;
        }

        TugMove flagged = move with { DwellBefore = (LastKind is { } last) && (last != move.Kind) };
        TugMoveTrace trace = TugKinematics.Simulate(End, [flagged], _aircraftType, TugMovePlanner.StepFt).Moves[0];
        _traces.Add(trace);
        End = trace.End;
        LastKind = flagged.Kind;
        Flyable = trace.Completed;
    }
}

/// <summary>
/// How a candidate was judged: the shape rule it breaks, the flown-path rule it breaks, and — when the only shape
/// rule it breaks is its final line pull running past the stop — how far past, feet.
/// </summary>
internal readonly record struct TugVerdict(string? ShapeDrop, TugPathRefusal? Path, double? FinalPullOvershootFt)
{
    internal bool Dropped => (ShapeDrop is not null) || (Path is not null);
}

/// <summary>What a goal's candidates have yielded so far: the best survivor and the most severe path-only refusal.</summary>
internal sealed class TugChoiceTally
{
    internal TugCandidate? Best { get; set; }

    internal TugPathRefusal? PathRefusal { get; set; }
}

/// <summary>Plans a request goal by goal, holding the plan so far.</summary>
internal sealed class TugPlanBuilder
{
    private static readonly ILogger Log = SimLog.CreateLogger("TugMovePlanner");

    /// <summary>A point this far off the reference direction or less is ahead of it; beyond, behind.</summary>
    private const double AheadDeg = 90.0;

    /// <summary>
    /// Within this many degrees of <see cref="AheadDeg"/>, a faced goal's push-side candidates rank ahead of its
    /// pull-side ones on a tie, whatever the side test says; a judgement call. Spots that sit abeam each other put the
    /// side test on its boundary, where a foot of end error flips it.
    /// </summary>
    private const double SideTieBandDeg = 5.0;

    /// <summary>
    /// A controller's <c>PUSH FACE</c> turn that rotates the nose more than this is flown on the tight radius. Turns and
    /// line captures the planner invents always use the routine radius.
    /// </summary>
    private const double TightRotationDeg = 135.0;

    /// <summary>A faced goal whose facing is more than this off the nose also gets the three-point-turn candidates.</summary>
    private const double ThreePointTurnMinRotationDeg = 30.0;

    /// <summary>A three-point turn whose turn is this small or less is no turn, so the variant is not built.</summary>
    private const double MinThreePointTurnDeg = 1.0;

    /// <summary>
    /// How far either side of the facing a three-point turn leaves the nose before the move onto the approach line;
    /// judgement calls, left to simulation and ranking to choose between.
    /// </summary>
    private static readonly double[] ThreePointTurnOffsetsDeg = [90.0, 60.0, 30.0];

    private static readonly double[] ThreePointTurnSides = [1.0, -1.0];

    /// <summary>
    /// The one angle that splits a bare <c>PUSH &lt;taxiway&gt;</c> in two: the acute angle between the push ray and a
    /// taxiway edge, 0° to 90°. At or above it the taxiway runs across the push and is reached by pushing straight back
    /// onto it (<see cref="StraightBackCandidate"/>); below it the taxiway runs alongside the push and is captured in
    /// an S-curve (<see cref="AlongsideCandidate"/>). A judgement call (user, 2026-09-16), measured against the two
    /// cases that set it: OAK gate 25's <c>PUSH TE</c> meets TE 645 ft back at 31.5° down an angled stand row —
    /// alongside, so the S-curve keeps it clear of the aircraft at gate 26 — while SFO B12's <c>PUSH A</c> meets A
    /// 417 ft back at 76°, a square crossing the ZOA technique pushes straight onto.
    /// </summary>
    internal const double AcrossAngleDeg = 45.0;

    /// <summary>How far off its facing and its approach line a faced goal's last move may end.</summary>
    private const double EndFacingToleranceDeg = 2.0;

    private const double EndCrossTrackToleranceFt = 3.0;

    /// <summary>
    /// How far off a taxiway's centreline a point may lie and still be on that taxiway, feet: half the 50 ft width of
    /// an ADG-III taxiway (AC 150/5300-13B), so a reference point inside the corridor is on the pavement. A judgement
    /// call (user, 2026-09-16), and the distance the case that set it needs: TE bends at node 245 beside OAK gate 32,
    /// so the S-curve captures the far piece's line and ends 8.7 ft off the near piece, inside that piece's extent —
    /// on TE, though not on the line it captured.
    /// </summary>
    internal const double OnTaxiwayCorridorFt = 25.0;

    /// <summary>How far a move onto a stop point, push or pull, may run past it.</summary>
    private const double StopOvershootToleranceFt = 3.0;

    /// <summary>How far a push onto a spot's staging point may run past it; a judgement call.</summary>
    private const double StagingOvershootToleranceFt = 100.0;

    /// <summary>
    /// The largest final-pull overshoot, feet, a faced-goal candidate is retried for with room before its reversal;
    /// a judgement call, as are the room's <see cref="RoomRetryOvershootFactor"/> and <see cref="RoomRetryPadFt"/>.
    /// </summary>
    internal const double MaxRoomRetryOvershootFt = 60.0;

    private const double RoomRetryOvershootFactor = 1.5;

    private const double RoomRetryPadFt = 10.0;

    private readonly AirportGroundLayout? _layout;
    private readonly TugRequest _request;

    /// <summary>The flown-path check, or null when there is no layout to check against.</summary>
    private readonly TugPathCheck? _pathCheck;

    /// <summary>The names of the taxiway straight behind the stand the plan starts on, exempt for every goal; empty otherwise.</summary>
    private readonly IReadOnlySet<string> _standBehindNames;
    private readonly List<TugMoveTrace> _moves = [];
    private TugPose _end;
    private PushbackLegKind? _lastKind;
    private TugRun? _run;

    internal TugPlanBuilder(AirportGroundLayout? layout, TugRequest request)
    {
        _layout = layout;
        _request = request;
        _end = request.Start;
        _lastKind = request.PreviousKind;
        _pathCheck = layout is null ? null : new TugPathCheck(layout, request.AircraftType, request.Start.Position);
        _standBehindNames = StandBehindNames(_pathCheck, request);
    }

    internal TugPlan ToPlan() => new([.. _moves], _end);

    internal bool TryPlanGoal(int index, out string refusal)
    {
        ResolvedTugGoal goal = WithStandBehindExempt(TugGoalResolver.Resolve(_layout, _request, index));
        if (IsRefusedOutright(goal, out refusal))
        {
            return false;
        }

        bool offStand = (index == 0) && _request.StartsAtStand;
        if (!TryBuildCandidates(goal, offStand, out List<TugCandidate>? candidates, out refusal))
        {
            return false;
        }

        TugCandidate? best = Choose(goal, candidates, offStand, out refusal);
        if (best is null)
        {
            return false;
        }

        _moves.AddRange(best.Traces);
        _end = best.End;
        _lastKind = best.LastKind;
        _run = TugRun.Follow(_run, best.Traces).Open;
        return true;
    }

    /// <summary>
    /// The names on the first movement-area edge the push-off ray from a stand start crosses within
    /// <see cref="TugMovePlanner.StandBehindExemptionFt"/>, or none when the plan does not start on a stand.
    /// </summary>
    private static IReadOnlySet<string> StandBehindNames(TugPathCheck? pathCheck, TugRequest request)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if ((pathCheck is null) || !request.StartsAtStand)
        {
            return names;
        }

        TugPose start = request.Start;
        (IGroundEdge Edge, double DistanceFt)? behind = pathCheck.FirstCrossing(
            start.Position,
            start.TravelTrueDeg(PushbackLegKind.Push),
            (0.0, TugMovePlanner.StandBehindExemptionFt),
            edge => pathCheck.MovementAreaName(edge) is not null
        );
        if (behind is { } crossing)
        {
            names.UnionWith(RampLaneReposition.EdgeNames(crossing.Edge));
            Log.LogDebug(
                "Tug move off the stand: {Names} crosses the push-off ray {DistanceFt:F0} ft behind the stand and is exempt",
                string.Join("/", names),
                crossing.DistanceFt
            );
        }

        return names;
    }

    private ResolvedTugGoal WithStandBehindExempt(ResolvedTugGoal goal)
    {
        if (_standBehindNames.Count == 0)
        {
            return goal;
        }

        var names = new HashSet<string>(goal.ExemptNames, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(_standBehindNames);
        return goal with { ExemptNames = names };
    }

    private bool IsRefusedOutright(ResolvedTugGoal goal, out string refusal)
    {
        double distanceFt = goal.Goal.Point is { } point ? GeoMath.DistanceNm(_end.Position, point) * GeoMath.FeetPerNm : 0.0;
        if (distanceFt > TugMovePlanner.MaxGoalDistanceFt)
        {
            // A UI sanity guard against a mis-click on the ground view, not an aviation rule.
            refusal = $"Unable, {goal.Name} is {distanceFt:0} ft — past the {TugMovePlanner.MaxGoalDistanceFt:0} ft sanity guard on a tug move";
            return true;
        }

        // A free-space tug move bypasses the hold-short machinery, so it may never end on a runway holding
        // position (AIM 4-3-18.a.5; AC 00-65A §11.13).
        if (goal.Goal.Node?.Type == GroundNodeType.RunwayHoldShort)
        {
            refusal = $"Unable, {goal.Subject} reaches a runway holding position";
            return true;
        }

        refusal = string.Empty;
        return false;
    }

    private bool TryBuildCandidates(ResolvedTugGoal goal, bool offStand, out List<TugCandidate> candidates, out string refusal)
    {
        TugMove? standPushOff = offStand
            ? TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.FuselageLengthFt(_request.AircraftType) / 2.0)
            : null;
        bool straightBack = goal.Shape is TugGoalShape.Clear or TugGoalShape.StraightBack;
        TugMove? pushOff = straightBack ? null : standPushOff;
        refusal = string.Empty;
        switch (goal.Shape)
        {
            case TugGoalShape.Faced:
                candidates = FacedCandidates(goal, pushOff);
                return true;
            case TugGoalShape.ToNode:
                return TryToNodeCandidate(goal, pushOff, out candidates, out refusal);
            case TugGoalShape.StraightBack:
                return TryStraightBackCandidate(goal, standPushOff, out candidates, out refusal);
            default:
                candidates = [SingleCandidate(goal, pushOff)];
                return true;
        }
    }

    /// <summary>
    /// The faced-goal candidates, in rank order: T1 (the side move onto the line), T2 (the other kind first), and —
    /// when the nose has to rotate more than <see cref="ThreePointTurnMinRotationDeg"/> — T3 (a three-point turn
    /// first, one variant per intermediate facing). Each template is built for both sides, in the order
    /// <see cref="SidesFor"/> gives.
    /// </summary>
    private List<TugCandidate> FacedCandidates(ResolvedTugGoal goal, TugMove? pushOff)
    {
        TugPose from = NewCandidate("probe", pushOff).End;
        double facing = goal.FacingTrueDeg;
        double stopOffFacingDeg = TugMovePlanner.AbsDiffDeg(GeoMath.BearingTo(from.Position, goal.Stop), facing);
        PushbackLegKind[] sides = SidesFor(stopOffFacingDeg);
        Log.LogDebug(
            "Tug {Subject}: stop {StopOffFacingDeg:F2}° off the facing; building candidates for the {Sides} side",
            goal.Subject,
            stopOffFacingDeg,
            string.Join(" and ", sides)
        );

        var candidates = sides.Select(side => Direct(goal, pushOff, side)).ToList();
        candidates.AddRange(sides.Select(side => OtherKindFirst(goal, pushOff, side)));
        if (TugMovePlanner.AbsDiffDeg(from.NoseTrueDeg, facing) > ThreePointTurnMinRotationDeg)
        {
            candidates.AddRange(sides.SelectMany(side => ThreePointTurns(goal, pushOff, side, from.NoseTrueDeg)));
        }

        return candidates;
    }

    /// <summary>
    /// Both sides a faced goal can be approached from, in the order their candidates are ranked on a tie: the side
    /// test's first — pull when the stop is at or within 90° of the facing, else push — and push first within
    /// <see cref="SideTieBandDeg"/> of 90°. The side test only orders them: a lane the side test's kind can only
    /// reach through a loop is still reached from the other side.
    /// </summary>
    private static PushbackLegKind[] SidesFor(double stopOffFacingDeg)
    {
        bool pullFirst = (Math.Abs(stopOffFacingDeg - AheadDeg) > SideTieBandDeg) && (stopOffFacingDeg <= AheadDeg);
        return pullFirst ? [PushbackLegKind.Pull, PushbackLegKind.Push] : [PushbackLegKind.Push, PushbackLegKind.Pull];
    }

    /// <summary>T1: the side move straight onto the approach line.</summary>
    private TugCandidate Direct(ResolvedTugGoal goal, TugMove? pushOff, PushbackLegKind side)
    {
        TugCandidate candidate = NewCandidate($"T1 direct, {side} side", pushOff);
        AddApproach(candidate, goal, side);
        return candidate;
    }

    /// <summary>T2: the other kind onto the approach line first, then the side move.</summary>
    private TugCandidate OtherKindFirst(ResolvedTugGoal goal, TugMove? pushOff, PushbackLegKind side)
    {
        TugCandidate candidate = NewCandidate($"T2 other kind first, {side} side", pushOff);
        candidate.Add(LineMove(goal, Opposite(side), stopAt: null));
        AddApproach(candidate, goal, side);
        return candidate;
    }

    /// <summary>
    /// T3: the other kind turns the nose to <c>facing ± offset</c> first, for each of
    /// <see cref="ThreePointTurnOffsetsDeg"/> on each side of the facing, then the side move. A variant whose turn
    /// is no turn at all is left out.
    /// </summary>
    private IEnumerable<TugCandidate> ThreePointTurns(ResolvedTugGoal goal, TugMove? pushOff, PushbackLegKind side, double startNoseDeg)
    {
        foreach (double offsetDeg in ThreePointTurnOffsetsDeg)
        {
            foreach (double sign in ThreePointTurnSides)
            {
                double intermediateDeg = new TrueHeading(goal.FacingTrueDeg + (sign * offsetDeg)).Degrees;
                if (TugMovePlanner.AbsDiffDeg(startNoseDeg, intermediateDeg) <= MinThreePointTurnDeg)
                {
                    continue;
                }

                TugCandidate candidate = NewCandidate($"T3 three-point turn to {intermediateDeg:F0}°, {side} side", pushOff);
                candidate.Add(TugMove.TurnTo(Opposite(side), intermediateDeg));
                AddApproach(candidate, goal, side);
                yield return candidate;
            }
        }
    }

    private static PushbackLegKind Opposite(PushbackLegKind kind) => kind == PushbackLegKind.Push ? PushbackLegKind.Pull : PushbackLegKind.Push;

    /// <summary>
    /// The side move onto the approach line; a spot push stops at the staging point and creeps forward onto the
    /// stop. The final pull onto a spot is always a creep.
    /// </summary>
    private static void AddApproach(TugCandidate candidate, ResolvedTugGoal goal, PushbackLegKind side)
    {
        bool staged = (side == PushbackLegKind.Push) && (goal.Staging is not null);
        bool creepOntoSpot = (side == PushbackLegKind.Pull) && (goal.Goal.Kind == TugGoalKind.Spot);
        TugMove approach = LineMove(goal, side, staged ? goal.Staging : goal.Stop);
        candidate.Add(approach with { Creep = creepOntoSpot });
        if (!staged || !candidate.Flyable)
        {
            return;
        }

        double creepFt = TugMovePlanner.AlongFt(goal.Stop, candidate.End.Position, goal.FacingTrueDeg);
        if (creepFt > 0.0)
        {
            candidate.Add(TugMove.Straight(PushbackLegKind.Pull, creepFt) with { Creep = true });
        }
    }

    private bool TryToNodeCandidate(ResolvedTugGoal goal, TugMove? pushOff, out List<TugCandidate> candidates, out string refusal)
    {
        TugCandidate candidate = NewCandidate("to node", pushOff);
        double offNoseDeg = TugMovePlanner.AbsDiffDeg(GeoMath.BearingTo(candidate.End.Position, goal.Stop), candidate.End.NoseTrueDeg);
        PushbackLegKind kind = offNoseDeg > AheadDeg ? PushbackLegKind.Push : PushbackLegKind.Pull;
        if ((pushOff is not null) && (kind == PushbackLegKind.Pull))
        {
            candidates = [];
            refusal = $"Unable, {goal.Name} is ahead of the nose — the aircraft has to be pushed back off the stand first";
            return false;
        }

        candidate.Add(TugMove.ToPoint(kind, goal.Stop));
        candidates = [candidate];
        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// A bare <c>PUSH &lt;taxiway&gt;</c>, onto a straight centreline edge of the goal's taxiway (fillet and junction
    /// arcs are not the taxiway's centreline), in this order: a straight push until the reference point reaches the
    /// first such edge the push ray crosses at <see cref="AcrossAngleDeg"/> or more
    /// (<see cref="StraightBackCandidate"/>); else, when such an edge runs closer than
    /// <see cref="AcrossAngleDeg"/> to the push direction with its nearest point behind the aircraft, a push onto the
    /// nearest one's line, stopping at the capture when that already puts the aircraft within the corridor of the
    /// taxiway's extent and carrying on along the line to the edge's nearest point when it does not
    /// (<see cref="AlongsideCandidate"/>); else refused. A
    /// crossing too shallow to be across the push falls through to the alongside candidate exactly as no crossing at
    /// all does.
    /// </summary>
    private bool TryStraightBackCandidate(ResolvedTugGoal goal, TugMove? standPushOff, out List<TugCandidate> candidates, out string refusal)
    {
        string taxiway = goal.Goal.TaxiwayName!;
        double pushTravelDeg = _end.TravelTrueDeg(PushbackLegKind.Push);
        bool IsCentreline(IGroundEdge edge) => (edge is GroundEdge) && edge.MatchesTaxiway(taxiway);
        refusal = string.Empty;
        if (StraightBackCandidate(goal, pushTravelDeg, IsCentreline) is { } straight)
        {
            candidates = [straight];
            return true;
        }

        if (AlongsideCandidate(goal, pushTravelDeg, standPushOff, IsCentreline) is { } alongside)
        {
            candidates = [alongside];
            return true;
        }

        candidates = [];
        refusal = $"Unable, {goal.Goal.Label} is not behind the aircraft";
        return false;
    }

    /// <summary>
    /// A straight push until the reference point reaches the first centreline edge the push ray crosses at
    /// <see cref="AcrossAngleDeg"/> or more, or null when it crosses none such. A crossing inside one simulation step
    /// is the aircraft already standing on that edge, not a taxiway behind it. The push is straight already, so it
    /// carries no separate stand push-off.
    /// </summary>
    private TugCandidate? StraightBackCandidate(ResolvedTugGoal goal, double pushTravelDeg, Func<IGroundEdge, bool> isCentreline)
    {
        (IGroundEdge Edge, double DistanceFt)? crossing = _pathCheck!.FirstCrossing(
            _end.Position,
            pushTravelDeg,
            (TugMovePlanner.StepFt, TugMovePlanner.MaxGoalDistanceFt),
            edge => isCentreline(edge) && RunsAcrossThePush(goal, edge, pushTravelDeg)
        );
        if (crossing is not { } behind)
        {
            return null;
        }

        Log.LogDebug(
            "Tug {Subject}: the push ray reaches {Taxiway} edge {NodeA}-{NodeB} {DistanceFt:F1} ft behind the aircraft",
            goal.Subject,
            goal.Goal.TaxiwayName,
            behind.Edge.Nodes[0].Id,
            behind.Edge.Nodes[1].Id,
            behind.DistanceFt
        );
        TugCandidate candidate = NewCandidate("straight back", pushOff: null);
        candidate.Add(TugMove.Straight(PushbackLegKind.Push, behind.DistanceFt));
        return candidate;
    }

    /// <summary>
    /// Whether an edge meets a push travelling <paramref name="pushTravelDeg"/> at <see cref="AcrossAngleDeg"/> or
    /// more, measured as the acute angle between the two — a taxiway across the push rather than along it.
    /// </summary>
    private static bool RunsAcrossThePush(ResolvedTugGoal goal, IGroundEdge edge, double pushTravelDeg)
    {
        var line = new TrueHeading(GeoMath.BearingTo(edge.Nodes[0].Position, edge.Nodes[1].Position));
        var push = new TrueHeading(pushTravelDeg);
        double crossingDeg = Math.Min(line.AbsAngleTo(push), line.ToReciprocal().AbsAngleTo(push));
        if (crossingDeg >= AcrossAngleDeg)
        {
            return true;
        }

        Log.LogDebug(
            "Tug {Subject}: {Taxiway} edge {NodeA}-{NodeB} runs on {LineDeg:F1}°, only {CrossingDeg:F1}° off the push "
                + "{PushDeg:F1}° — alongside the push, not across it",
            goal.Subject,
            goal.Goal.TaxiwayName,
            edge.Nodes[0].Id,
            edge.Nodes[1].Id,
            line.Degrees,
            crossingDeg,
            pushTravelDeg
        );
        return false;
    }

    /// <summary>
    /// For a taxiway that runs alongside the push rather than across it: the nearest centreline edge within
    /// <see cref="AcrossAngleDeg"/> of the push direction, either way along it, whose nearest point lies behind
    /// the aircraft and is itself within <see cref="TugMovePlanner.MaxGoalDistanceFt"/> — both the distance back along
    /// the push and the straight-line distance are bounded, so a taxiway far off to the side does not count as
    /// alongside the push. The stand push-off (off a stand), then a push onto that edge's line travelling along the
    /// edge direction nearest the push: an S-curve onto the centreline. The push stops where the capture leaves the
    /// aircraft when that already puts it within the corridor of the taxiway's extent — abeam a straight centreline
    /// edge of it, between that edge's ends and no further than <see cref="OnTaxiwayCorridorFt"/> off its line, which
    /// at a bend is the piece beside the aircraft rather than the piece whose line was captured. When the capture ends
    /// outside that corridor, the push carries on along the line to the edge's point nearest the aircraft instead, so
    /// it still ends on the taxiway with the nose along it. Null when no edge qualifies.
    /// </summary>
    private TugCandidate? AlongsideCandidate(ResolvedTugGoal goal, double pushTravelDeg, TugMove? standPushOff, Func<IGroundEdge, bool> isCentreline)
    {
        (double AcrossAngleDeg, double MaxGoalDistanceFt) window = (AcrossAngleDeg, TugMovePlanner.MaxGoalDistanceFt);
        if (_pathCheck!.NearestAlongside(_end.Position, pushTravelDeg, window, isCentreline) is not { } alongside)
        {
            return null;
        }

        IGroundEdge edge = alongside.Edge;
        Log.LogDebug(
            "Tug {Subject}: the push ray crosses no {Taxiway} edge; edge {NodeA}-{NodeB} runs alongside on {LineDeg:F1}° "
                + "(push {PushDeg:F1}°), its nearest point {DistanceFt:F1} ft away and {AlongFt:F1} ft behind the aircraft",
            goal.Subject,
            goal.Goal.TaxiwayName,
            edge.Nodes[0].Id,
            edge.Nodes[1].Id,
            alongside.LineTravelTrueDeg,
            pushTravelDeg,
            alongside.DistanceFt,
            alongside.AlongFt
        );
        TugCandidate captured = NewCandidate("onto the taxiway alongside, stopping at the capture", standPushOff);
        captured.Add(TugMove.ViaLine(PushbackLegKind.Push, edge.Nodes[0].Position, alongside.LineTravelTrueDeg, stopAt: null));
        if (captured.Flyable && _pathCheck.IsOnEdgeExtent(captured.End.Position, OnTaxiwayCorridorFt, isCentreline))
        {
            Log.LogDebug(
                "Tug {Subject}: the capture of {Taxiway}'s centreline ends within the corridor of that taxiway's extent after "
                    + "{PathFt:F0} ft — the push stops there",
                goal.Subject,
                goal.Goal.TaxiwayName,
                captured.PathLengthFt
            );
            return captured;
        }

        Log.LogDebug(
            "Tug {Subject}: the capture of {Taxiway}'s centreline ({Moves}) ends outside the corridor of that taxiway's extent "
                + "at ({Lat:F6}, {Lon:F6}) — the push carries on along the line to the edge's nearest point",
            goal.Subject,
            goal.Goal.TaxiwayName,
            captured.Describe(),
            captured.End.Position.Lat,
            captured.End.Position.Lon
        );
        TugCandidate candidate = NewCandidate("onto the taxiway alongside", standPushOff);
        candidate.Add(TugMove.ViaLine(PushbackLegKind.Push, edge.Nodes[0].Position, alongside.LineTravelTrueDeg, alongside.NearestPoint));
        return candidate;
    }

    /// <summary>The one move list a facing, clear or taxiway-line goal has.</summary>
    private TugCandidate SingleCandidate(ResolvedTugGoal goal, TugMove? pushOff)
    {
        TugCandidate candidate = NewCandidate(goal.Shape.ToString(), pushOff);
        switch (goal.Shape)
        {
            case TugGoalShape.Facing:
                candidate.Add(FacingTurnMove(candidate, goal.FacingTrueDeg));
                double remainingFt = TugMovePlanner.SimplePushbackFt(_request.AircraftType) - candidate.PathLengthFt;
                if (remainingFt > 0.0)
                {
                    candidate.Add(TugMove.Straight(PushbackLegKind.Push, remainingFt));
                }

                break;
            case TugGoalShape.Clear:
                candidate.Add(TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.SimplePushbackFt(_request.AircraftType)));
                break;
            case TugGoalShape.TaxiwayLine:
                candidate.Add(LineMove(goal, PushbackLegKind.Push, stopAt: null));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(goal), goal.Shape, "Not a single-candidate goal shape");
        }

        return candidate;
    }

    private TugCandidate NewCandidate(string template, TugMove? pushOff)
    {
        var candidate = new TugCandidate(template, _request.AircraftType, _end, _lastKind);
        if (pushOff is not null)
        {
            candidate.Add(pushOff);
        }

        return candidate;
    }

    /// <summary>
    /// A capture of the goal's approach line that leaves the nose on its facing, flown on the routine radius: the
    /// planner chose this turn, the controller did not ask for a tight one.
    /// </summary>
    private static TugMove LineMove(ResolvedTugGoal goal, PushbackLegKind kind, LatLon? stopAt) =>
        TugMove.ViaLine(kind, goal.Stop, TugKinematics.FlipForKind(goal.FacingTrueDeg, kind), stopAt);

    /// <summary>
    /// A controller's <c>PUSH FACE</c> turn: on the tight radius when it rotates the nose more than
    /// <see cref="TightRotationDeg"/>, the one turn the controller's command sets the radius of.
    /// </summary>
    private static TugMove FacingTurnMove(TugCandidate candidate, double facingTrueDeg)
    {
        bool tight = TugMovePlanner.AbsDiffDeg(candidate.End.NoseTrueDeg, facingTrueDeg) > TightRotationDeg;
        return TugMove.TurnTo(PushbackLegKind.Push, facingTrueDeg) with { Tight = tight };
    }

    /// <summary>
    /// The surviving candidate with the fewest reversals, then the shortest path, then the earliest in rank order
    /// (candidates arrive in rank order, so a later one wins only when strictly better; a candidate's room-before-the-
    /// reversal retry ranks straight after it). With none left, the refusal is the most severe flown-path reason
    /// among the candidates whose shape was sound, else the generic one: a candidate dropped for its shape was never a
    /// way to fly the move, so the pavement it would have crossed is not the reason the move is refused.
    /// </summary>
    private TugCandidate? Choose(ResolvedTugGoal goal, List<TugCandidate> candidates, bool offStand, out string refusal)
    {
        var tally = new TugChoiceTally();
        foreach (TugCandidate candidate in candidates)
        {
            TugVerdict verdict = Judge(goal, candidate, offStand);
            Tally(goal, candidate, verdict, tally);
            if (RoomBeforeReversal(candidate, verdict) is { } retry)
            {
                Tally(goal, retry, Judge(goal, retry, offStand), tally);
            }
        }

        TugCandidate? best = tally.Best;
        refusal = best is null ? (tally.PathRefusal?.Message ?? $"Unable, cannot line up on {goal.Name} from here") : string.Empty;
        if (best is not null)
        {
            Log.LogDebug(
                "Tug {Subject}: chose {Template} ({Moves}), {Reversals} reversal(s), {PathFt:F0} ft",
                goal.Subject,
                best.Template,
                best.Describe(),
                best.Reversals,
                best.PathLengthFt
            );
        }

        return best;
    }

    /// <summary>Logs a judged candidate and counts it: a survivor may become the best, a path-only drop may become the refusal.</summary>
    private static void Tally(ResolvedTugGoal goal, TugCandidate candidate, TugVerdict verdict, TugChoiceTally tally)
    {
        if (verdict.Dropped)
        {
            Log.LogDebug(
                "Tug {Subject}: dropped {Template} ({Moves}): {Reason}",
                goal.Subject,
                candidate.Template,
                candidate.Describe(),
                DropReason(verdict.ShapeDrop, verdict.Path)
            );
            tally.PathRefusal = verdict.ShapeDrop is null ? MoreSevere(tally.PathRefusal, verdict.Path) : tally.PathRefusal;
            return;
        }

        Log.LogDebug(
            "Tug {Subject}: kept {Template} ({Moves}), {Reversals} reversal(s), {PathFt:F0} ft",
            goal.Subject,
            candidate.Template,
            candidate.Describe(),
            candidate.Reversals,
            candidate.PathLengthFt
        );
        tally.Best = IsBetter(candidate, tally.Best) ? candidate : tally.Best;
    }

    /// <summary>
    /// The one retry a candidate gets when it was dropped only because its final pull onto the stop ran past it by
    /// no more than <see cref="MaxRoomRetryOvershootFt"/>: the same moves with a straight of the kind before the
    /// reversal, <see cref="RoomRetryOvershootFactor"/> × the overshoot + <see cref="RoomRetryPadFt"/> long,
    /// inserted just before the reversal into that pull, so the pull has room to capture its line before the stop.
    /// Null when the candidate gets no retry, including when the pull reverses a move of an earlier goal.
    /// </summary>
    private TugCandidate? RoomBeforeReversal(TugCandidate candidate, TugVerdict verdict)
    {
        if ((verdict.Path is not null) || (verdict.FinalPullOvershootFt is not { } overshootFt) || (overshootFt > MaxRoomRetryOvershootFt))
        {
            return null;
        }

        IReadOnlyList<TugMoveTrace> traces = candidate.Traces;
        TugMove pull = traces[^1].Move;
        if ((traces.Count < 2) || !pull.DwellBefore)
        {
            return null;
        }

        double roomFt = (RoomRetryOvershootFactor * overshootFt) + RoomRetryPadFt;
        var retry = new TugCandidate($"{candidate.Template}, {roomFt:F1} ft room before the reversal", _request.AircraftType, _end, _lastKind);
        foreach (TugMoveTrace? trace in traces.Take(traces.Count - 1))
        {
            retry.Add(trace.Move);
        }

        retry.Add(TugMove.Straight(traces[^2].Move.Kind, roomFt));
        retry.Add(pull);
        return retry;
    }

    private static bool IsBetter(TugCandidate candidate, TugCandidate? best) =>
        (best is null)
        || (candidate.Reversals < best.Reversals)
        || ((candidate.Reversals == best.Reversals) && (candidate.PathLengthFt < best.PathLengthFt));

    private static TugPathRefusal? MoreSevere(TugPathRefusal? current, TugPathRefusal? found) =>
        (found is { } next) && ((current is not { } held) || (next.Severity < held.Severity)) ? found : current;

    /// <summary>How a dropped candidate's reason is logged: the shape rule it broke, with any flown-path hit after it.</summary>
    private static string DropReason(string? shapeDrop, TugPathRefusal? path) =>
        (shapeDrop, path) switch
        {
            (null, { } hit) => hit.Message,
            ({ } shape, { } hit) => $"{shape} (its flown path: {hit.Message})",
            _ => shapeDrop!,
        };

    /// <summary>
    /// Why a candidate is dropped: the shape rule it breaks (the travel budget, the stand push-off rule, the run
    /// wander, the end tolerance or the overshoot), and the flown-path rule it breaks. Both null when it survives.
    /// Every flyable candidate's path is checked, even one already dropped for its shape, so the log shows what it
    /// would have crossed; only a shape-sound candidate's path hit can become the refusal.
    /// </summary>
    private TugVerdict Judge(ResolvedTugGoal goal, TugCandidate candidate, bool offStand)
    {
        if (!candidate.Flyable)
        {
            return new TugVerdict("a move ran past its travel budget", null, null);
        }

        (string? shapeReason, double? finalPullOvershootFt) =
            goal.Shape == TugGoalShape.Faced ? FacedDropReason(goal, candidate, offStand) : (null, null);
        TugPathRefusal? path = _pathCheck?.Check(candidate.Traces, goal.ExemptNames, goal.Subject);
        return new TugVerdict(shapeReason, path, finalPullOvershootFt);
    }

    /// <summary>
    /// The first shape rule a faced-goal candidate breaks, or null; and, when the only rule it breaks is its final
    /// line pull running past the stop, how far past.
    /// </summary>
    private (string? Reason, double? FinalPullOvershootFt) FacedDropReason(ResolvedTugGoal goal, TugCandidate candidate, bool offStand)
    {
        IReadOnlyList<TugMoveTrace> traces = candidate.Traces;
        if (offStand && (traces.Count > 1) && (traces[1].Move.Kind == PushbackLegKind.Pull))
        {
            return ("the move after the stand push-off is a pull", null);
        }

        if (TugRun.Follow(_run, traces).Wandered)
        {
            return ($"a same-kind run without a turn wandered more than {TugRun.MaxWanderDeg:0}°", null);
        }

        TugMoveTrace last = traces[^1];
        if ((EndDropReason(goal, last) ?? OvershootDropReason(goal, traces.Take(traces.Count - 1))) is { } reason)
        {
            return (reason, null);
        }

        bool linePull = (last.Move.Kind == PushbackLegKind.Pull) && (last.Move.Shape == TugMoveShape.ViaLine);
        return (StopOvershootFt(last, goal) is { } stopFt) && (stopFt > StopOvershootToleranceFt)
            ? (StopOvershootReason(last.Move.Kind, stopFt), linePull ? stopFt : null)
            : (OvershootDropReason(goal, [last]), null);
    }

    private static string StopOvershootReason(PushbackLegKind kind, double overshootFt) =>
        $"the {(kind == PushbackLegKind.Push ? "push" : "pull")} onto the stop point ran {overshootFt:F1} ft past it";

    private static string? EndDropReason(ResolvedTugGoal goal, TugMoveTrace last)
    {
        double noseErrorDeg = TugMovePlanner.AbsDiffDeg(last.End.NoseTrueDeg, goal.FacingTrueDeg);
        double crossFt = GeoMath.SignedCrossTrackDistanceNm(last.End.Position, goal.Stop, new TrueHeading(goal.FacingTrueDeg)) * GeoMath.FeetPerNm;
        bool offLine = (noseErrorDeg > EndFacingToleranceDeg) || (Math.Abs(crossFt) > EndCrossTrackToleranceFt);
        return offLine ? $"ended {crossFt:F1} ft off the approach line with the nose {noseErrorDeg:F1}° off the facing" : null;
    }

    private static string? OvershootDropReason(ResolvedTugGoal goal, IEnumerable<TugMoveTrace> traces)
    {
        foreach (TugMoveTrace trace in traces)
        {
            if ((StopOvershootFt(trace, goal) is { } stopFt) && (stopFt > StopOvershootToleranceFt))
            {
                return StopOvershootReason(trace.Move.Kind, stopFt);
            }

            if ((StagingOvershootFt(trace, goal) is { } pushFt) && (pushFt > StagingOvershootToleranceFt))
            {
                return $"the push onto the staging point ran {pushFt:F1} ft past it";
            }
        }

        return null;
    }

    /// <summary>
    /// How far a move onto the goal's stop point ran past it, feet: a line move of either kind stopping there, or a
    /// pull's creep onto it. Null for any other move.
    /// </summary>
    private static double? StopOvershootFt(TugMoveTrace trace, ResolvedTugGoal goal)
    {
        TugMove move = trace.Move;
        if ((move.Shape == TugMoveShape.ViaLine) && (move.StopAt == goal.Stop))
        {
            return trace.EndOvershootFt;
        }

        return ((move.Kind == PushbackLegKind.Pull) && move.Creep) ? TugMovePlanner.AlongFt(trace.End.Position, goal.Stop, goal.FacingTrueDeg) : null;
    }

    private static double? StagingOvershootFt(TugMoveTrace trace, ResolvedTugGoal goal) =>
        ((trace.Move.Kind == PushbackLegKind.Push) && (goal.Staging is { } staging) && (trace.Move.StopAt == staging)) ? trace.EndOvershootFt : null;
}
