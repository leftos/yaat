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

    /// <summary>The facing taxiway a <c>PUSH &lt;taxiway&gt; &lt;facing taxiway&gt;</c> names; null for every other goal.</summary>
    public string? FacingTaxiwayName { get; init; }

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

/// <summary>
/// A parked or held aircraft near a planned tug move: the planner sweeps every candidate's flown path against it with
/// the same outline rule <c>GroundConflictDetector</c> holds the move under way to, so a candidate that would swing
/// into it is dropped at planning time instead of being accepted and then dead-stopped mid-manoeuvre.
/// </summary>
public sealed record TugParkedNeighbour
{
    /// <summary>The neighbour's callsign, for the refusal.</summary>
    public required string Callsign { get; init; }

    /// <summary>Where it stands.</summary>
    public required LatLon Position { get; init; }

    /// <summary>Its nose heading, degrees true.</summary>
    public required double TrueHeadingDeg { get; init; }

    /// <summary>ICAO type designator; sets its outline.</summary>
    public required string AircraftType { get; init; }

    /// <summary>The stand it is parked on, or null when it is not on a named stand.</summary>
    public required string? StandName { get; init; }

    /// <summary>How a refusal names it: the callsign, with the stand when it is on one.</summary>
    /// <returns>For example <c>SKW3398 at D1</c>.</returns>
    public string Describe() => StandName is { } stand ? $"{Callsign} at {stand}" : Callsign;
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

    /// <summary>
    /// The parked or held aircraft near the move; every candidate's flown path is swept against each. Empty when the
    /// caller has no view of the other aircraft, which plans the move blind to them.
    /// </summary>
    public required IReadOnlyList<TugParkedNeighbour> ParkedNeighbours { get; init; }

    /// <summary>A facing that overrides the last goal's, degrees true, or null.</summary>
    public required double? FinalFacingTrueDeg { get; init; }

    /// <summary>
    /// The kind of the tug move this plan replaces while that move is under way, or null when the aircraft is not
    /// under tow. A first planned move of the other kind is a reversal, so it dwells before it starts.
    /// </summary>
    public required PushbackLegKind? PreviousKind { get; init; }
}

/// <summary>What kind of thing a <see cref="TugPlanWarning"/> tells the RPO.</summary>
public enum TugPlanWarningKind
{
    /// <summary>A push kept out of the movement area still reaches into a taxiway's object-free area.</summary>
    FoulsTaxiway,

    /// <summary>A push onto a taxiway tows a long way across the ramp before it lines up on it.</summary>
    LongPushToTaxiway,
}

/// <summary>Something the plan could not avoid, or that the RPO may not have meant, as data; the terminal text is formatted from it.</summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Taxiway">The taxiway it concerns.</param>
public abstract record TugPlanWarning(TugPlanWarningKind Kind, string Taxiway);

/// <summary>
/// A push kept out of the movement area (a spot, stand or node goal) that no candidate could keep outside a taxiway's
/// object-free area: the plan is the one that reaches in least, and this names where it still does.
/// </summary>
/// <param name="Taxiway">The taxiway fouled.</param>
/// <param name="Part">The part of the aircraft that reaches in deepest.</param>
/// <param name="PeakPenetrationFt">How far inside the object-free half-width that part reaches, feet.</param>
public sealed record TugFoulsTaxiwayWarning(string Taxiway, TugFootprintPart Part, double PeakPenetrationFt)
    : TugPlanWarning(TugPlanWarningKind.FoulsTaxiway, Taxiway);

/// <summary>
/// A push onto a taxiway that tows more than <see cref="TugMovePlanner.LongPushToTaxiwayFt"/> before the aircraft is
/// lined up on it — accepted, since the RPO may mean it, but worth a note in case the taxiway was mis-typed.
/// </summary>
/// <param name="Taxiway">The taxiway pushed onto.</param>
/// <param name="DistanceFt">The tow before the aircraft is lined up, feet, rounded to the nearest 50.</param>
public sealed record TugLongPushWarning(string Taxiway, double DistanceFt) : TugPlanWarning(TugPlanWarningKind.LongPushToTaxiway, Taxiway);

/// <summary>A planned tug move: every move with its simulated path, where the aircraft ends up, and what to tell the RPO.</summary>
/// <param name="Moves">The moves in order; each trace carries its <see cref="TugMove"/>.</param>
/// <param name="End">The simulated end pose.</param>
/// <param name="Warnings">What the plan could not avoid, or the RPO may not have meant; empty when there is nothing to say.</param>
/// <param name="FacingJunctionFt">
/// For a <c>PUSH &lt;taxiway&gt; &lt;facing taxiway&gt;</c>, how far the junction with the facing taxiway lies from
/// where the push ends, feet; null for every other push.
/// </param>
/// <param name="FacingTaxiwayName">The facing taxiway <paramref name="FacingJunctionFt"/> measures to; null with it.</param>
public sealed record TugPlan(
    IReadOnlyList<TugMoveTrace> Moves,
    TugPose End,
    IReadOnlyList<TugPlanWarning> Warnings,
    double? FacingJunctionFt,
    string? FacingTaxiwayName
)
{
    /// <summary>
    /// The facing taxiway's junction lies more than <see cref="TugMovePlanner.FarFacingJunctionFt"/> from where the
    /// push ends: the facing only chose the direction along the taxiway, which the RPO is told.
    /// </summary>
    public bool FacingTaxiwayIsFar => FacingJunctionFt > TugMovePlanner.FarFacingJunctionFt;

    /// <summary><see cref="FacingJunctionFt"/> as the RPO's note gives it (<see cref="TugMovePlanner.NoteDistanceFt"/>); null with it.</summary>
    public double? FacingJunctionNoteFt => FacingJunctionFt is { } ft ? TugMovePlanner.NoteDistanceFt(ft) : null;
}

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
/// <para><b>Off a stand onto a spot's lane.</b> Besides the three templates, a spot goal off a stand gets a
/// straight-then-line candidate per side: the push-off, a straight push along the stand's lead-in line to where a
/// pivot on the line-capture roll-out radius lands on the spot's lane, then the push capturing the lane. Those
/// candidates are ranked by path length plus a penalty on how far the aircraft departs the lead-in line before it
/// reaches the lane, so the push goes straight back toward the lane before it pivots.</para>
///
/// <para><b>Room before a reversal.</b> A faced-goal candidate dropped only because its final pull onto the stop runs
/// past it by up to (1 + <see cref="TugKinematics.RolloutMarginRadii"/>) × the routine turn radius — the most one line
/// capture can need — is retried once with a straight move of the kind before the reversal, 1.5 × the overshoot +
/// 10 ft long, inserted just before that reversal; the retry is judged like any other candidate.</para>
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

    /// <summary>
    /// A push onto a taxiway that tows further than this before the aircraft is lined up on it carries a
    /// <see cref="TugLongPushWarning"/>, feet; a judgement call: from SFO F3, A's line lies about 1,285 ft across the
    /// ramp, and <c>PUSH A F1</c> tows about 1,400 ft to line up on it.
    /// </summary>
    public const double LongPushToTaxiwayFt = 500.0;

    /// <summary>What every distance in a push note is rounded to, feet: a note reads "about", not a survey.</summary>
    public const double NoteRoundingFt = 50.0;

    /// <summary>A distance as a push note gives it: rounded to the nearest <see cref="NoteRoundingFt"/>.</summary>
    /// <param name="distanceFt">The measured distance, feet.</param>
    /// <returns>The rounded distance, feet.</returns>
    public static double NoteDistanceFt(double distanceFt) => Math.Round(distanceFt / NoteRoundingFt) * NoteRoundingFt;

    /// <summary>
    /// A facing taxiway whose junction lies further than this from where a <c>PUSH &lt;taxiway&gt; &lt;facing
    /// taxiway&gt;</c> ends is far (<see cref="TugPlan.FacingTaxiwayIsFar"/>), feet; a judgement call: SFO D10's
    /// junction, about 630 ft past the stop, is not far; F3's, about 2,650 ft past it, is.
    /// </summary>
    public const double FarFacingJunctionFt = 1500.0;

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
    public static double FuselageLengthFt(string aircraftType) =>
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

    /// <summary>
    /// An edge's centreline as points from <paramref name="from"/> to its other end: the two nodes with the edge's
    /// intermediate points between them, in that order.
    /// </summary>
    /// <param name="edge">The edge.</param>
    /// <param name="from">The end node the points start at.</param>
    /// <returns>The centreline's points.</returns>
    internal static List<LatLon> EdgePointsFrom(GroundEdge edge, GroundNode from)
    {
        var points = new List<LatLon> { edge.Nodes[0].Position };
        points.AddRange(edge.IntermediatePoints.Select(q => new LatLon(q.Lat, q.Lon)));
        points.Add(edge.Nodes[1].Position);
        if (edge.Nodes[0].Id != from.Id)
        {
            points.Reverse();
        }

        return points;
    }

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
public enum TugGoalShape
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
public sealed record ResolvedTugGoal
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

    /// <summary>
    /// Taxiway names the movement-area check lets the fuselage cross. A taxiway goal is judged by its overshoot instead
    /// (<see cref="OvershootTaxiway"/>); its names only keep the goal's own taxiway out of the swept-pavement debug log.
    /// </summary>
    public required IReadOnlySet<string> ExemptNames { get; init; }

    /// <summary>
    /// The taxiway a taxiway-line or straight-back goal is pushed onto, which the flown-path check judges the push by
    /// (<see cref="TugPathCheck.MaxTaxiwayOvershootFt"/>) in place of the movement-area rule; null for every other goal.
    /// </summary>
    public string? OvershootTaxiway => Shape is TugGoalShape.TaxiwayLine or TugGoalShape.StraightBack ? Goal.TaxiwayName : null;
}

/// <summary>Turns a request's goal into a <see cref="ResolvedTugGoal"/>.</summary>
public static class TugGoalResolver
{
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves the request's goal at <paramref name="index"/>: its shape, facing, stop point and exemptions.</summary>
    /// <param name="layout">The airport's ground layout, or null when it has none.</param>
    /// <param name="request">The tug move request.</param>
    /// <param name="index">Which of the request's goals to resolve.</param>
    /// <returns>The resolved goal.</returns>
    public static ResolvedTugGoal Resolve(AirportGroundLayout? layout, TugRequest request, int index)
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

    /// <summary>
    /// A taxiway-line or straight-back goal: the line runs through the exit node, and the taxiway is exempt.
    /// </summary>
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
    /// <summary>
    /// A run without a turn that wanders further than this from its start travel is a loop; a judgement call, set so
    /// a push off a stand can pivot onto a spot's lane in one capture (SFO F8 → 7A pivots 133°, E12 → 7B 123°).
    /// </summary>
    internal const double MaxWanderDeg = 150.0;

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

    /// <summary>The junction with the facing taxiway a <c>PUSH &lt;taxiway&gt; &lt;facing taxiway&gt;</c> candidate points the nose toward.</summary>
    internal GroundNode? FacingJunction { get; set; }

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
/// One direction a <c>PUSH &lt;taxiway&gt; &lt;facing taxiway&gt;</c> can end in: the line to capture, the stop along it
/// (null stops once lined up), the direction of travel along it (away from the junction, so the nose points at it),
/// whether the exit lies on this side of the junction, and whether the junction lies beyond the stop distance from
/// the exit, so the push stops as soon as it is lined up.
/// </summary>
internal readonly record struct TugTaxiwaySide(LatLon LinePoint, LatLon? Stop, double TravelDeg, bool ExitSide, bool BeyondStopDistance);

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

    /// <summary>Every candidate that survived judging, in the order judged.</summary>
    internal List<TugCandidate> Survivors { get; } = [];
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

    /// <summary>
    /// How far past the lane's heading a lane goal's final pull onto its line may swing the nose before it turns back,
    /// degrees: the pull turns onto the lane heading without passing it.
    /// </summary>
    internal const double MaxPullPastLineDeg = 5.0;

    /// <summary>The step the fouling fallback shortens the straight-then-line straight by, feet; a judgement call.</summary>
    private const double FallbackStraightStepFt = 10.0;

    /// <summary>
    /// How many feet of path one foot of lateral departure from the stand's lead-in line costs when a spot goal off a
    /// stand is ranked; a judgement call.
    /// </summary>
    private const double LeadInDeparturePenalty = 4.0;

    /// <summary>How far a push onto a spot's staging point may run past it; a judgement call.</summary>
    private const double StagingOvershootToleranceFt = 100.0;

    /// <summary>
    /// How long the room a faced-goal candidate is retried with before its reversal is, as a multiple of its final
    /// pull's overshoot, plus <see cref="RoomRetryPadFt"/>; judgement calls.
    /// </summary>
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
    private readonly List<TugPlanWarning> _warnings = [];
    private GroundNode? _facingJunction;
    private string? _facingTaxiwayName;
    private TugTaxiwayClearance? _clearance;

    /// <summary>Each ranked candidate's <see cref="LeadInDepartureFt"/>; a candidate belongs to one goal, and its moves never change once judged.</summary>
    private readonly Dictionary<TugCandidate, double> _leadInDepartureByCandidate = [];

    internal TugPlanBuilder(AirportGroundLayout? layout, TugRequest request)
    {
        _layout = layout;
        _request = request;
        _end = request.Start;
        _lastKind = request.PreviousKind;
        _pathCheck = layout is null ? null : new TugPathCheck(layout, request.AircraftType, request.Start.Position);
        _standBehindNames = StandBehindNames(_pathCheck, request);
    }

    internal TugPlan ToPlan() =>
        new(
            [.. _moves],
            _end,
            [.. _warnings],
            _facingJunction is { } junction ? GeoMath.DistanceNm(_end.Position, junction.Position) * GeoMath.FeetPerNm : null,
            _facingTaxiwayName
        );

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

        TugCandidate? best = Choose(goal, candidates, offStand, out refusal, out List<TugCandidate> survivors);
        if (best is null)
        {
            return false;
        }

        if (IsKeptOffTheMovementArea(goal))
        {
            best = ClearOfTaxiways(goal, best, survivors, offStand);
        }

        if (goal.Shape == TugGoalShape.TaxiwayLine)
        {
            NoteLongPush(goal, best);
        }

        _moves.AddRange(best.Traces);
        if (best.FacingJunction is { } facingJunction)
        {
            _facingJunction = facingJunction;
            _facingTaxiwayName = goal.Goal.FacingTaxiwayName;
        }
        _end = best.End;
        _lastKind = best.LastKind;
        _run = TugRun.Follow(_run, best.Traces).Open;
        return true;
    }

    /// <summary>
    /// A goal in the non-movement area — a spot, a stand, a node — whose push is held outside the object-free area of
    /// every movement-area taxiway it does not name. A push the command sends onto a taxiway, a bare push and a
    /// <c>PUSH FACE</c> are not.
    /// </summary>
    private bool IsKeptOffTheMovementArea(ResolvedTugGoal goal) => (_layout is not null) && (goal.Shape is TugGoalShape.Faced or TugGoalShape.ToNode);

    /// <summary>
    /// A lane goal: a spot, faced along its lane, reached off a stand — the goal the straight-then-line candidates, the
    /// fouling fallback, the lead-in-departure ranking and the pull-past-the-line rule are for.
    /// </summary>
    /// <param name="goal">The goal.</param>
    /// <param name="offStand">The goal is the plan's first and the plan starts on a stand.</param>
    /// <returns>True for a lane goal.</returns>
    private static bool IsLaneGoal(ResolvedTugGoal goal, bool offStand) =>
        offStand && (goal.Shape == TugGoalShape.Faced) && (goal.Goal.Kind == TugGoalKind.Spot);

    /// <summary>
    /// The candidate a non-movement-area goal keeps: the chosen one when its flown outline stays outside the object-free
    /// area of every protected taxiway — the movement-area taxiways but those the goal names, the taxiway straight behind
    /// the stand, and any the outline already reaches into where the goal starts. Otherwise the best clear candidate
    /// among the survivors and the fouling fallback (<see cref="ShorterStraightThenLineCandidates"/>), ranked as
    /// <see cref="Choose"/> ranks them; with none clear, the one reaching in least deep, then for least exposure, and a
    /// <see cref="TugFoulsTaxiwayWarning"/> on the plan. Never a refusal.
    /// </summary>
    private TugCandidate ClearOfTaxiways(ResolvedTugGoal goal, TugCandidate chosen, List<TugCandidate> survivors, bool offStand)
    {
        TugTaxiwayClearance clearance = _clearance ??= new TugTaxiwayClearance(_layout!, _request.AircraftType);
        var excluded = new HashSet<string>(goal.ExemptNames, StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(clearance.TaxiwaysFouledAt(_end));
        TugTaxiwayFouling chosenFouling = clearance.Measure(chosen.Traces, excluded);
        LogChosenClearance(goal, chosen, excluded, chosenFouling);
        if (!chosenFouling.Fouls)
        {
            return chosen;
        }

        List<TugCandidate> pool = [.. survivors.Concat(FallbackSurvivors(goal, offStand))];
        return BestClear(goal, offStand, pool.Where(c => (c != chosen) && !clearance.Fouls(c.Traces, excluded)))
            ?? LeastFouling(goal, [.. pool.Select(c => (c, c == chosen ? chosenFouling : clearance.Measure(c.Traces, excluded)))]);
    }

    private static void LogChosenClearance(ResolvedTugGoal goal, TugCandidate chosen, HashSet<string> excluded, TugTaxiwayFouling fouling) =>
        Log.LogDebug(
            "Tug {Subject}: kept clear of every movement-area taxiway's object-free area but {Excluded}; {Template} {Verdict}",
            goal.Subject,
            excluded.Count == 0 ? "none" : string.Join("/", excluded.Order(StringComparer.OrdinalIgnoreCase)),
            chosen.Template,
            fouling.Fouls ? $"reaches {fouling.PeakFt:F1} ft into {fouling.Taxiway}'s with the {fouling.Part}" : "stays clear"
        );

    /// <summary>The best of the candidates that stay clear, ranked as <see cref="Choose"/> ranks them; null when there are none.</summary>
    private TugCandidate? BestClear(ResolvedTugGoal goal, bool offStand, IEnumerable<TugCandidate> clear)
    {
        TugCandidate? bestClear = null;
        foreach (TugCandidate candidate in clear)
        {
            bestClear = IsBetter(goal, offStand, candidate, bestClear) ? candidate : bestClear;
        }

        if (bestClear is not null)
        {
            Log.LogDebug("Tug {Subject}: {Template} ({Moves}) stays clear instead", goal.Subject, bestClear.Template, bestClear.Describe());
        }

        return bestClear;
    }

    /// <summary>The pool's candidate reaching in least deep, then for least exposure, with a <see cref="TugFoulsTaxiwayWarning"/> on the plan.</summary>
    private TugCandidate LeastFouling(ResolvedTugGoal goal, List<(TugCandidate Candidate, TugTaxiwayFouling Fouling)> pool)
    {
        (TugCandidate least, TugTaxiwayFouling leastFouling) = pool.OrderBy(p => p.Fouling.PeakFt).ThenBy(p => p.Fouling.ExposureFtFt).First();
        Log.LogDebug(
            "Tug {Subject}: no candidate stays clear; {Template} ({Moves}) reaches least, {PeakFt:F1} ft into {Taxiway}'s with the {Part}",
            goal.Subject,
            least.Template,
            least.Describe(),
            leastFouling.PeakFt,
            leastFouling.Taxiway,
            leastFouling.Part
        );
        _warnings.Add(new TugFoulsTaxiwayWarning(leastFouling.Taxiway!, leastFouling.Part, leastFouling.PeakFt));
        return least;
    }

    /// <summary>The fouling fallback's candidates that survive judging, for a spot goal off a stand; none for any other goal.</summary>
    private List<TugCandidate> FallbackSurvivors(ResolvedTugGoal goal, bool offStand)
    {
        if (!IsLaneGoal(goal, offStand))
        {
            return [];
        }

        var pushOff = TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.FuselageLengthFt(_request.AircraftType) / 2.0);
        var survivors = new List<TugCandidate>();
        foreach (TugCandidate candidate in ShorterStraightThenLineCandidates(goal, pushOff))
        {
            TugVerdict verdict = Judge(goal, candidate, offStand);
            if (!verdict.Dropped)
            {
                survivors.Add(candidate);
            }
            else if (RoomBeforeReversal(candidate, verdict) is { } retry && !Judge(goal, retry, offStand).Dropped)
            {
                survivors.Add(retry);
            }
        }

        return survivors;
    }

    /// <summary>
    /// A <c>PUSH &lt;taxiway&gt; &lt;facing taxiway&gt;</c> that tows more than <see cref="TugMovePlanner.LongPushToTaxiwayFt"/>
    /// before the aircraft is lined up on the taxiway: a <see cref="TugLongPushWarning"/>, the distance rounded for the note
    /// (<see cref="TugMovePlanner.NoteDistanceFt"/>).
    /// </summary>
    private void NoteLongPush(ResolvedTugGoal goal, TugCandidate chosen)
    {
        double towFt = PathToLineUpFt(chosen);
        if (towFt <= TugMovePlanner.LongPushToTaxiwayFt)
        {
            return;
        }

        Log.LogDebug("Tug {Subject}: tows {TowFt:F0} ft before it is lined up on {Taxiway}", goal.Subject, towFt, goal.Goal.TaxiwayName);
        _warnings.Add(new TugLongPushWarning(goal.Goal.TaxiwayName!, TugMovePlanner.NoteDistanceFt(towFt)));
    }

    /// <summary>
    /// How far a candidate ending on a line capture tows before it is lined up, feet: every move before the last, and the
    /// last up to its first sample within <see cref="TugKinematics"/>' capture tolerance
    /// (<see cref="TugKinematics.CaptureCrossTrackFt"/> off the line, the travel within
    /// <see cref="TugKinematics.CaptureTravelErrorDeg"/> of it) — the whole of it when it never gets there.
    /// </summary>
    private static double PathToLineUpFt(TugCandidate candidate)
    {
        IReadOnlyList<TugMoveTrace> traces = candidate.Traces;
        double towFt = traces.Take(traces.Count - 1).Sum(t => t.PathLengthFt);
        TugMoveTrace last = traces[^1];
        TugMove move = last.Move;
        var line = new TrueHeading(move.LineTravelTrueDeg);
        LatLon? previous = null;
        foreach (TugPose sample in last.Samples)
        {
            towFt += previous is { } from ? GeoMath.DistanceNm(from, sample.Position) * GeoMath.FeetPerNm : 0.0;
            previous = sample.Position;
            double offFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(sample.Position, move.Point, line)) * GeoMath.FeetPerNm;
            if (
                (offFt <= TugKinematics.CaptureCrossTrackFt)
                && (TugMovePlanner.AbsDiffDeg(sample.TravelTrueDeg(move.Kind), move.LineTravelTrueDeg) <= TugKinematics.CaptureTravelErrorDeg)
            )
            {
                return towFt;
            }
        }

        return towFt;
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
            case TugGoalShape.Faced when IsLaneGoal(goal, offStand):
                candidates = FacedCandidates(goal, pushOff);
                candidates.AddRange(StraightThenLineCandidates(goal, pushOff!));
                return true;
            case TugGoalShape.Faced:
                candidates = FacedCandidates(goal, pushOff);
                return true;
            case TugGoalShape.TaxiwayLine when TaxiwayLineCandidates(goal, pushOff) is { Count: > 0 } lined:
                candidates = lined;
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
    /// The straight-then-line candidates off a stand onto a spot's lane: after the half-fuselage push-off, a straight
    /// push to where the pivot onto the lane starts (<see cref="PivotLeadFt"/> short of the lane), then a push capturing
    /// the lane; the push side carries on to the staging point, the pull side captures the lane floating and pulls
    /// along it onto the stop. Nothing when the lane is closer than that.
    /// </summary>
    private IEnumerable<TugCandidate> StraightThenLineCandidates(ResolvedTugGoal goal, TugMove pushOff) =>
        StraightThenLineStraightFt(goal, pushOff) is { } straightFt ? StraightThenLine(goal, pushOff, straightFt) : [];

    /// <summary>
    /// The fouling fallback for a spot goal off a stand: the straight-then-line candidates with the straight cut short —
    /// every <see cref="FallbackStraightStepFt"/> shorter than <see cref="StraightThenLineCandidates"/>' down to one
    /// step — so the push pivots onto the lane earlier and further from the taxiway behind it. Built only when the chosen
    /// candidate fouls a taxiway's object-free area.
    /// </summary>
    private IEnumerable<TugCandidate> ShorterStraightThenLineCandidates(ResolvedTugGoal goal, TugMove pushOff)
    {
        if (StraightThenLineStraightFt(goal, pushOff) is not { } fullFt)
        {
            yield break;
        }

        for (double straightFt = fullFt - FallbackStraightStepFt; straightFt > TugMovePlanner.StepFt; straightFt -= FallbackStraightStepFt)
        {
            foreach (TugCandidate candidate in StraightThenLine(goal, pushOff, straightFt))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// How long the straight push of a straight-then-line candidate is: from the push-off's end to where the pivot onto
    /// the lane starts (<see cref="PivotLeadFt"/> short of the lane). Null when the lane is closer than that.
    /// </summary>
    private double? StraightThenLineStraightFt(ResolvedTugGoal goal, TugMove pushOff)
    {
        TugPose from = NewCandidate("probe", pushOff).End;
        if ((LaneCrossingFt(goal, from) is not { } crossFt) || (PivotLeadFt(goal, from) is not { } leadFt))
        {
            return null;
        }

        double straightFt = crossFt - leadFt;
        return straightFt > TugMovePlanner.StepFt ? straightFt : null;
    }

    /// <summary>The push-side and pull-side straight-then-line candidates with a straight push of <paramref name="straightFt"/>.</summary>
    private IEnumerable<TugCandidate> StraightThenLine(ResolvedTugGoal goal, TugMove pushOff, double straightFt)
    {
        foreach (PushbackLegKind side in new[] { PushbackLegKind.Push, PushbackLegKind.Pull })
        {
            TugCandidate candidate = NewCandidate($"T0 straight {straightFt:F0} ft then line, {side} side", pushOff);
            candidate.Add(TugMove.Straight(PushbackLegKind.Push, straightFt));
            if (side == PushbackLegKind.Pull)
            {
                candidate.Add(LineMove(goal, PushbackLegKind.Push, stopAt: null));
            }

            AddApproach(candidate, goal, side);
            yield return candidate;
        }
    }

    /// <summary>
    /// How far along the push line from <paramref name="from"/> the reference point reaches the spot's lane, feet: the
    /// first straight, non-ramp edge the push ray crosses that carries a name of the spot's own straight non-ramp edges
    /// (the ramp lane through the spot); failing that, the push ray's intersection with the spot's approach line. Null
    /// when neither lies behind the aircraft.
    /// </summary>
    private double? LaneCrossingFt(ResolvedTugGoal goal, TugPose from)
    {
        double pushDeg = from.TravelTrueDeg(PushbackLegKind.Push);
        var laneNames = new HashSet<string>(
            goal.Goal.Node!.Edges.OfType<GroundEdge>().Where(e => !e.IsRamp && !e.IsRunwayCenterline).Select(e => e.TaxiwayName),
            StringComparer.OrdinalIgnoreCase
        );
        bool IsLane(IGroundEdge edge) => (edge is GroundEdge straight) && !straight.IsRamp && laneNames.Contains(straight.TaxiwayName);
        if (
            (laneNames.Count > 0)
            && (_pathCheck!.FirstCrossing(from.Position, pushDeg, (TugMovePlanner.StepFt, TugMovePlanner.MaxGoalDistanceFt), IsLane) is { } hit)
        )
        {
            Log.LogDebug(
                "Tug {Subject}: the push ray crosses lane {Lane} edge {NodeA}-{NodeB} {DistanceFt:F1} ft behind",
                goal.Subject,
                hit.Edge.TaxiwayName,
                hit.Edge.Nodes[0].Id,
                hit.Edge.Nodes[1].Id,
                hit.DistanceFt
            );
            return hit.DistanceFt;
        }

        double offLineFt = GeoMath.SignedCrossTrackDistanceNm(from.Position, goal.Stop, new TrueHeading(goal.FacingTrueDeg)) * GeoMath.FeetPerNm;
        double closingRate = Math.Sin((pushDeg - goal.FacingTrueDeg) * Math.PI / 180.0);
        double alongFt = Math.Abs(closingRate) < 0.1 ? -1.0 : -offLineFt / closingRate;
        Log.LogDebug(
            "Tug {Subject}: no lane edge ({Names}) on the push ray; the approach line is {AlongFt:F1} ft along it",
            goal.Subject,
            string.Join("/", laneNames),
            alongFt
        );
        return alongFt > 0.0 ? alongFt : null;
    }

    /// <summary>
    /// How far before the lane, along the push line, the pivot onto the lane starts, feet: a turn of Δ from the push
    /// travel onto the lane's push travel (into the ramp) lands tangent on the lane from R·(1 − cos Δ) off it, which
    /// the push line closes at sin Δ per foot — R·tan(Δ/2), on the line-capture roll-out radius R = 1.15 × the routine
    /// radius (<see cref="TugKinematics.RolloutMarginRadii"/>). One roll-out radius for a square pivot. Null past a
    /// pivot of <see cref="TugRun.MaxWanderDeg"/>, which no same-kind run may wander through without a turn, and where
    /// the lead grows without bound.
    /// </summary>
    private double? PivotLeadFt(ResolvedTugGoal goal, TugPose from)
    {
        double pivotDeg = TugMovePlanner.AbsDiffDeg(
            from.TravelTrueDeg(PushbackLegKind.Push),
            TugKinematics.FlipForKind(goal.FacingTrueDeg, PushbackLegKind.Push)
        );
        if (pivotDeg > TugRun.MaxWanderDeg)
        {
            return null;
        }

        double rolloutFt = TugKinematics.RolloutMarginRadii * TugKinematics.TurnRadiusFt(_request.AircraftType, tight: false);
        return rolloutFt * Math.Tan(pivotDeg / 2.0 * Math.PI / 180.0);
    }

    /// <summary><see cref="LeadInDepartureFt"/> of a candidate, measured once: the ranking compares a candidate many times.</summary>
    private double CachedLeadInDepartureFt(ResolvedTugGoal goal, TugCandidate candidate)
    {
        if (!_leadInDepartureByCandidate.TryGetValue(candidate, out double departureFt))
        {
            departureFt = LeadInDepartureFt(goal, candidate);
            _leadInDepartureByCandidate[candidate] = departureFt;
        }

        return departureFt;
    }

    /// <summary>
    /// The largest lateral departure of the reference point, nose or tail from the stand's lead-in line, feet, over the
    /// candidate's poses until the reference point first comes within one routine turn radius of the approach line.
    /// </summary>
    private double LeadInDepartureFt(ResolvedTugGoal goal, TugCandidate candidate)
    {
        TugPose start = _request.Start;
        var lead = new TrueHeading(start.NoseTrueDeg);
        double halfNm = TugMovePlanner.FuselageLengthFt(_request.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        double radiusFt = TugKinematics.TurnRadiusFt(_request.AircraftType, tight: false);
        double maxFt = 0.0;
        foreach (TugPose pose in candidate.Traces.SelectMany(t => t.Samples))
        {
            double laneOffFt =
                Math.Abs(GeoMath.SignedCrossTrackDistanceNm(pose.Position, goal.Stop, new TrueHeading(goal.FacingTrueDeg))) * GeoMath.FeetPerNm;
            if (laneOffFt <= radiusFt)
            {
                break;
            }

            var nose = new TrueHeading(pose.NoseTrueDeg);
            foreach (
                LatLon point in new[]
                {
                    pose.Position,
                    GeoMath.ProjectPoint(pose.Position, nose, halfNm),
                    GeoMath.ProjectPoint(pose.Position, nose.ToReciprocal(), halfNm),
                }
            )
            {
                maxFt = Math.Max(maxFt, Math.Abs(GeoMath.SignedCrossTrackDistanceNm(point, start.Position, lead)) * GeoMath.FeetPerNm);
            }
        }

        return maxFt;
    }

    /// <summary>The candidate's total nose rotation, degrees, summed sample to sample.</summary>
    private static double TotalRotationDeg(TugCandidate candidate)
    {
        double totalDeg = 0.0;
        TugPose? previous = null;
        foreach (TugPose pose in candidate.Traces.SelectMany(t => t.Samples))
        {
            totalDeg += previous is { } last ? TugMovePlanner.AbsDiffDeg(last.NoseTrueDeg, pose.NoseTrueDeg) : 0.0;
            previous = pose;
        }

        return totalDeg;
    }

    /// <summary>
    /// A <c>PUSH &lt;taxiway&gt; &lt;facing taxiway&gt;</c>: a push onto the taxiway's line with the nose toward its
    /// junction with the facing taxiway, in whichever direction along the taxiway turns the nose least (the ranking,
    /// <see cref="IsBetter"/>). When the exit lies within one stop distance (a routine turn radius plus half a fuselage)
    /// of the junction, both directions away from the junction are candidates, each stopping with the nose tip one
    /// routine turn radius short of it. When the junction is farther, the facing taxiway only says which way the nose
    /// points: the one candidate is the push onto the line through the exit, nose toward the junction, stopping as soon
    /// as it is lined up — never a tow along the taxiway toward a far
    /// junction. Null when the goal names no facing taxiway or the junction cannot be found.
    /// </summary>
    private List<TugCandidate>? TaxiwayLineCandidates(ResolvedTugGoal goal, TugMove? pushOff)
    {
        if ((_layout is null) || (goal.Goal.FacingTaxiwayName is not { } facingName))
        {
            return null;
        }

        string taxiway = goal.Goal.TaxiwayName!;
        GroundNode exit = goal.Goal.Node!;
        if (FacingJunction(_layout, taxiway, facingName, exit) is not { } junction)
        {
            Log.LogDebug("Tug {Subject}: no {Taxiway}/{Facing} junction", goal.Subject, taxiway, facingName);
            return null;
        }

        double backFt =
            TugKinematics.TurnRadiusFt(_request.AircraftType, tight: false) + (TugMovePlanner.FuselageLengthFt(_request.AircraftType) / 2.0);
        List<TugTaxiwaySide> sides = ReachableSides(taxiway, junction, exit, backFt);
        Log.LogDebug(
            "Tug {Subject}: {Taxiway}/{Facing} junction node {Node}; {Count} direction(s), {Stop}",
            goal.Subject,
            taxiway,
            facingName,
            junction.Id,
            sides.Count,
            sides.Any(s => s.BeyondStopDistance)
                ? "the junction is far, so the push stops once lined up at the exit"
                : $"stopping {backFt:F0} ft back from the junction"
        );
        return [.. sides.Select(side => SideCandidate(taxiway, junction, side, pushOff))];
    }

    /// <summary>The node joining <paramref name="taxiway"/> and <paramref name="facingName"/> nearest the exit, or null when they never meet.</summary>
    private static GroundNode? FacingJunction(AirportGroundLayout layout, string taxiway, string facingName, GroundNode exit) =>
        layout
            .Nodes.Values.Where(n =>
                n.Edges.OfType<GroundEdge>().Any(e => e.MatchesTaxiway(taxiway))
                && n.Edges.OfType<GroundEdge>().Any(e => e.MatchesTaxiway(facingName))
            )
            .MinBy(n => GeoMath.DistanceNm(n.Position, exit.Position));

    /// <summary>
    /// The directions along the taxiway away from the junction a push can end in (<see cref="SideLine"/>): only the exit's
    /// own when the junction lies beyond the stop distance from it, else the exit's side and any other whose stop lies
    /// within <see cref="TugMovePlanner.MaxGoalDistanceFt"/>.
    /// </summary>
    private List<TugTaxiwaySide> ReachableSides(string taxiway, GroundNode junction, GroundNode exit, double backFt)
    {
        List<TugTaxiwaySide> sides =
        [
            .. junction.Edges.OfType<GroundEdge>().Where(e => e.MatchesTaxiway(taxiway)).Select(e => SideLine(taxiway, junction, e, exit, backFt)),
        ];
        bool beyond = sides.Any(s => s.BeyondStopDistance);
        return [.. sides.Where(s => beyond ? s.BeyondStopDistance : IsWithinReach(s))];
    }

    /// <summary>A side the push can end on when the junction is near: the exit's own, or one whose stop is within <see cref="TugMovePlanner.MaxGoalDistanceFt"/>.</summary>
    private bool IsWithinReach(TugTaxiwaySide side) =>
        side.ExitSide || ((GeoMath.DistanceNm(_end.Position, side.Stop ?? side.LinePoint) * GeoMath.FeetPerNm) <= TugMovePlanner.MaxGoalDistanceFt);

    /// <summary>The push onto one side's line, nose toward the junction.</summary>
    private TugCandidate SideCandidate(string taxiway, GroundNode junction, TugTaxiwaySide side, TugMove? pushOff)
    {
        double noseDeg = new TrueHeading(side.TravelDeg).ToReciprocal().Degrees;
        TugCandidate candidate = NewCandidate($"onto {taxiway} nose {noseDeg:F0} toward node {junction.Id}", pushOff);
        candidate.FacingJunction = junction;
        candidate.Add(TugMove.ViaLine(PushbackLegKind.Push, side.LinePoint, side.TravelDeg, side.Stop));
        return candidate;
    }

    /// <summary>
    /// One side of the junction along the taxiway, reached from its edge <paramref name="first"/>. When the exit node
    /// lies along that side, <c>J</c> ft from the junction, the line is the one through the exit node on the taxiway's
    /// direction there away from the junction: stopping <paramref name="backFt"/> − J ft past the exit when the junction
    /// is that close, else — the junction far — stopping as soon as the push lines up on it. Otherwise (the far side,
    /// or the exit is the junction): the line through the point <paramref name="backFt"/> along the taxiway, stopping
    /// there.
    /// </summary>
    private static TugTaxiwaySide SideLine(string taxiway, GroundNode junction, GroundEdge first, GroundNode exit, double backFt)
    {
        if ((exit.Id != junction.Id) && (AlongToNode(taxiway, junction, first, exit) is { } reach))
        {
            double offsetFt = backFt - reach.AlongFt;
            if (offsetFt < 0.0)
            {
                return new TugTaxiwaySide(exit.Position, null, reach.ArrivalDeg, ExitSide: true, BeyondStopDistance: true);
            }

            LatLon stop = GeoMath.ProjectPoint(exit.Position, new TrueHeading(reach.ArrivalDeg), offsetFt / GeoMath.FeetPerNm);
            return new TugTaxiwaySide(exit.Position, stop, reach.ArrivalDeg, ExitSide: true, BeyondStopDistance: false);
        }

        (LatLon point, double travelDeg) = WalkAlong(taxiway, junction, first, backFt);
        return new TugTaxiwaySide(point, point, travelDeg, ExitSide: exit.Id == junction.Id, BeyondStopDistance: false);
    }

    /// <summary>
    /// How far along the taxiway's straight edges from <paramref name="from"/>, starting on <paramref name="first"/>
    /// and carrying on nearest straight ahead at each node, <paramref name="target"/> is reached, feet, and the
    /// direction of travel on arriving there; null when it is not within three times
    /// <see cref="TugMovePlanner.MaxGoalDistanceFt"/>.
    /// </summary>
    private static (double AlongFt, double ArrivalDeg)? AlongToNode(string taxiway, GroundNode from, GroundEdge first, GroundNode target)
    {
        GroundNode at = from;
        GroundEdge edge = first;
        double alongFt = 0.0;
        while (alongFt <= 3.0 * TugMovePlanner.MaxGoalDistanceFt)
        {
            GroundNode next = edge.OtherNode(at);
            List<LatLon> points = TugMovePlanner.EdgePointsFrom(edge, at);
            double arrivalDeg = GeoMath.BearingTo(points[^2], points[^1]);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                alongFt += GeoMath.DistanceNm(points[i], points[i + 1]) * GeoMath.FeetPerNm;
            }

            if (next.Id == target.Id)
            {
                return (alongFt, arrivalDeg);
            }

            GroundEdge current = edge;
            GroundEdge? onward = next
                .Edges.OfType<GroundEdge>()
                .Where(e => (e != current) && e.MatchesTaxiway(taxiway))
                .MinBy(e => TugMovePlanner.AbsDiffDeg(GeoMath.BearingTo(next.Position, e.OtherNode(next).Position), arrivalDeg));
            if (onward is null)
            {
                return null;
            }

            at = next;
            edge = onward;
        }

        return null;
    }

    /// <summary>
    /// The point <paramref name="distanceFt"/> along the taxiway's straight edges from <paramref name="from"/>, starting
    /// on <paramref name="first"/> and at each node carrying on along the edge of the taxiway nearest straight ahead,
    /// with the direction of travel there. Stops at the last node when the taxiway ends first.
    /// </summary>
    private static (LatLon Point, double TravelDeg) WalkAlong(string taxiway, GroundNode from, GroundEdge first, double distanceFt)
    {
        GroundNode at = from;
        GroundEdge edge = first;
        double leftFt = distanceFt;
        double travelDeg = GeoMath.BearingTo(from.Position, first.OtherNode(from).Position);
        for (int hop = 0; hop < 50; hop++)
        {
            List<LatLon> points = TugMovePlanner.EdgePointsFrom(edge, at);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double segFt = GeoMath.DistanceNm(points[i], points[i + 1]) * GeoMath.FeetPerNm;
                travelDeg = GeoMath.BearingTo(points[i], points[i + 1]);
                if (segFt >= leftFt)
                {
                    return (GeoMath.ProjectPoint(points[i], new TrueHeading(travelDeg), leftFt / GeoMath.FeetPerNm), travelDeg);
                }

                leftFt -= segFt;
            }

            GroundNode next = edge.OtherNode(at);
            GroundEdge current = edge;
            double headingDeg = travelDeg;
            GroundEdge? onward = next
                .Edges.OfType<GroundEdge>()
                .Where(e => (e != current) && e.MatchesTaxiway(taxiway))
                .MinBy(e => TugMovePlanner.AbsDiffDeg(GeoMath.BearingTo(next.Position, e.OtherNode(next).Position), headingDeg));
            if (onward is null)
            {
                return (next.Position, travelDeg);
            }

            at = next;
            edge = onward;
        }

        return (at.Position, travelDeg);
    }

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
    private TugCandidate? Choose(
        ResolvedTugGoal goal,
        List<TugCandidate> candidates,
        bool offStand,
        out string refusal,
        out List<TugCandidate> survivors
    )
    {
        var tally = new TugChoiceTally();
        foreach (TugCandidate candidate in candidates)
        {
            TugVerdict verdict = Judge(goal, candidate, offStand);
            Tally(goal, candidate, verdict, tally, offStand);
            if (RoomBeforeReversal(candidate, verdict) is { } retry)
            {
                Tally(goal, retry, Judge(goal, retry, offStand), tally, offStand);
            }
        }

        TugCandidate? best = tally.Best;
        survivors = tally.Survivors;
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
    private void Tally(ResolvedTugGoal goal, TugCandidate candidate, TugVerdict verdict, TugChoiceTally tally, bool offStand)
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
        tally.Survivors.Add(candidate);
        tally.Best = IsBetter(goal, offStand, candidate, tally.Best) ? candidate : tally.Best;
    }

    /// <summary>
    /// The one retry a candidate gets when it was dropped only because its final pull onto the stop ran past it by
    /// no more than <see cref="RoomRetryLimitFt"/>: the same moves with a straight of the kind before the
    /// reversal, <see cref="RoomRetryOvershootFactor"/> × the overshoot + <see cref="RoomRetryPadFt"/> long,
    /// inserted just before the reversal into that pull, so the pull has room to capture its line before the stop.
    /// Null when the candidate gets no retry, including when the pull reverses a move of an earlier goal.
    /// </summary>
    private TugCandidate? RoomBeforeReversal(TugCandidate candidate, TugVerdict verdict)
    {
        if ((verdict.Path is not null) || (verdict.FinalPullOvershootFt is not { } overshootFt) || (overshootFt > RoomRetryLimitFt()))
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

    /// <summary>
    /// The final-pull overshoot a candidate is retried for, feet: the along-line travel one line capture can need at
    /// most — a 90° turn-in on the routine radius plus the roll-out arc, (1 + <see cref="TugKinematics.RolloutMarginRadii"/>) × R.
    /// </summary>
    private double RoomRetryLimitFt() => (1.0 + TugKinematics.RolloutMarginRadii) * TugKinematics.TurnRadiusFt(_request.AircraftType, tight: false);

    private bool IsBetter(ResolvedTugGoal goal, bool offStand, TugCandidate candidate, TugCandidate? best)
    {
        if (best is null)
        {
            return true;
        }

        if (candidate.Reversals != best.Reversals)
        {
            return candidate.Reversals < best.Reversals;
        }

        if (IsLaneGoal(goal, offStand))
        {
            double Score(TugCandidate c) => c.PathLengthFt + (LeadInDeparturePenalty * CachedLeadInDepartureFt(goal, c));
            return Score(candidate) < Score(best);
        }

        if (goal.Shape == TugGoalShape.TaxiwayLine)
        {
            double candidateDeg = TotalRotationDeg(candidate);
            double bestDeg = TotalRotationDeg(best);
            return Math.Abs(candidateDeg - bestDeg) > 1.0 ? candidateDeg < bestDeg : candidate.PathLengthFt < best.PathLengthFt;
        }

        return candidate.PathLengthFt < best.PathLengthFt;
    }

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
        TugPathRefusal? path =
            _pathCheck?.Check(candidate.Traces, goal.ExemptNames, goal.OvershootTaxiway, goal.Subject) ?? NeighbourRefusal(goal, candidate);
        return new TugVerdict(shapeReason, path, finalPullOvershootFt);
    }

    /// <summary>
    /// The first parked or held neighbour a candidate's flown path swings into, as a refusal, or null when it clears
    /// them all. Each move is swept from its own start with the moves flown through from it before the first reversal —
    /// the run <c>GroundConflictDetector</c> brakes for as that move begins — against the same floor the detector
    /// anchors to that move's start pose, so the planner and the detector cannot disagree about which swing is flyable.
    ///
    /// <para>Only a <see cref="TugGoalShape.Faced"/> goal is judged this way, because only it has templates to choose
    /// between: dropping the one that swings into a neighbour leaves the others. The single-shape goals — a bare push,
    /// a push straight back to a taxiway, a line capture, a facing — have nothing to fall back on, and refusing them
    /// would take away the tug's own answer to a blocked alley, which is to creep up and stop short of the aircraft in
    /// it (<see cref="GroundConflictDetector"/>'s outline stop). Those keep going to the tug.</para>
    ///
    /// <para>Limiting the check to faced goals is a judgement call: the towing references — AC 00-65A §11.9 and §11.17 —
    /// leave the swing to the wing walkers and the tug crew and give no rule for which move to refuse, and the 7110.65
    /// and the AIM say nothing about towing at all.</para>
    /// </summary>
    /// <param name="goal">The goal being planned, for the refusal's wording.</param>
    /// <param name="candidate">The candidate whose flown path is swept.</param>
    /// <returns>The refusal, or null when every neighbour stays clear.</returns>
    private TugPathRefusal? NeighbourRefusal(ResolvedTugGoal goal, TugCandidate candidate)
    {
        IReadOnlyList<TugMoveTrace> traces = candidate.Traces;
        if ((goal.Shape != TugGoalShape.Faced) || (_request.ParkedNeighbours.Count == 0))
        {
            return null;
        }

        var frame = new GroundOutlineFrame(_request.Start.Position);
        for (int i = 0; i < traces.Count; i++)
        {
            List<(TugPose Pose, double AlongFt)> path = RunPathFrom(traces, i);
            if (path.Count == 0)
            {
                continue;
            }

            var moverSize = GroundOutlineSize.Of(_request.AircraftType, towedNoseFirst: traces[i].Move.Kind == PushbackLegKind.Pull);
            foreach (TugParkedNeighbour neighbour in _request.ParkedNeighbours)
            {
                GroundOutlineSweepResult swept = GroundOutlineSweep.Sweep(
                    path,
                    path[0].Pose,
                    frame,
                    moverSize,
                    neighbour.Position,
                    neighbour.TrueHeadingDeg,
                    GroundOutlineSize.Of(neighbour.AircraftType, towedNoseFirst: false)
                );
                if (swept.Foul is not { } foul)
                {
                    continue;
                }

                Log.LogDebug(
                    "Tug {Subject}: {Template} move {Move} swings within {ClearanceFt:F1} ft of {Neighbour} (started {StartFt:F1} ft "
                        + "off, floor {FloorFt:F1} ft) {AlongFt:F1} ft along",
                    goal.Subject,
                    candidate.Template,
                    i + 1,
                    foul.ClearanceFt,
                    neighbour.Describe(),
                    swept.StartClearanceFt,
                    swept.FloorFt,
                    foul.AlongFt
                );
                return new TugPathRefusal(TugPathSeverity.ParkedNeighbour, $"Unable, {goal.Subject} would swing into {neighbour.Describe()}");
            }
        }

        return null;
    }

    /// <summary>
    /// The poses of move <paramref name="index"/> and of every move flown through from it before the first reversal,
    /// each with how far along that run it sits, feet.
    /// </summary>
    /// <param name="traces">The candidate's moves.</param>
    /// <param name="index">The move the run starts at.</param>
    /// <returns>The run's poses with their along-distances.</returns>
    private static List<(TugPose Pose, double AlongFt)> RunPathFrom(IReadOnlyList<TugMoveTrace> traces, int index)
    {
        var path = new List<(TugPose Pose, double AlongFt)>();
        double alongFt = 0.0;
        LatLon? previous = null;
        for (int i = index; (i < traces.Count) && (traces[i].Move.Kind == traces[index].Move.Kind); i++)
        {
            foreach (TugPose pose in traces[i].Samples)
            {
                alongFt += previous is { } from ? GeoMath.DistanceNm(from, pose.Position) * GeoMath.FeetPerNm : 0.0;
                path.Add((pose, alongFt));
                previous = pose.Position;
            }
        }

        return path;
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
        bool linePull = (last.Move.Kind == PushbackLegKind.Pull) && (last.Move.Shape == TugMoveShape.ViaLine);
        double pastDeg = (linePull && IsLaneGoal(goal, offStand)) ? PastLineDeg(last) : 0.0;
        if (pastDeg > MaxPullPastLineDeg)
        {
            return ($"the pull onto the line swung the nose {pastDeg:F1}° past the line's heading", null);
        }

        if ((EndDropReason(goal, last) ?? OvershootDropReason(goal, traces.Take(traces.Count - 1))) is { } reason)
        {
            return (reason, null);
        }

        return (StopOvershootFt(last, goal) is { } stopFt) && (stopFt > StopOvershootToleranceFt)
            ? (StopOvershootReason(last.Move.Kind, stopFt), linePull ? stopFt : null)
            : (OvershootDropReason(goal, [last]), null);
    }

    /// <summary>
    /// How far a line move's direction of travel swings past its line's direction, degrees: measured on the side
    /// opposite the one the move starts on, so a capture that turns in, passes the line heading and turns back shows
    /// the angle it passed it by; zero for one that turns onto the line heading without crossing it.
    /// </summary>
    private static double PastLineDeg(TugMoveTrace trace)
    {
        var line = new TrueHeading(trace.Move.LineTravelTrueDeg);
        IReadOnlyList<TugPose> samples = trace.Samples;
        if (samples.Count == 0)
        {
            return 0.0;
        }

        double startDeg = line.SignedAngleTo(new TrueHeading(samples[0].TravelTrueDeg(trace.Move.Kind)));
        double side = startDeg >= 0.0 ? 1.0 : -1.0;
        return Math.Max(0.0, samples.Max(s => -side * line.SignedAngleTo(new TrueHeading(s.TravelTrueDeg(trace.Move.Kind)))));
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
