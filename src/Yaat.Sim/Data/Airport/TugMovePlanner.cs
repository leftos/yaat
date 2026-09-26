using System.Diagnostics;
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

    /// <summary>
    /// End on a marked point — a free position on the ramp, not a graph node — on a given facing or on whatever facing
    /// the move leaves.
    /// </summary>
    FreePose,
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

    /// <summary>How a refusal names the goal: <c>spot 6B</c>, <c>D16</c>, <c>taxiway Y</c>, <c>the pushback</c>, <c>the marked point</c>.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The tug motion every move of this goal's leg must use, bar the stand push-off (a <c>/PUSH</c> or <c>/PULL</c>
    /// suffix), or null to let the planner choose. Set with <c>with</c> on a spot, stand, node or marked-point goal.
    /// </summary>
    public PushbackLegKind? ForcedKind { get; init; }

    /// <summary>
    /// A marked point's facing as the controller gave it, in words (<c>north</c>), for its refusal; null for every other
    /// goal and a marked point given none.
    /// </summary>
    public string? FacingWord { get; init; }

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

    /// <summary>
    /// End on a marked point (<c>~lat/lon[/facing]</c>): reached like a node goal — along the approach line through it
    /// when it has a facing, straight for it when not — but with no graph edges, so no pavement is exempt for it.
    /// </summary>
    /// <param name="point">The point, minted with <see cref="VirtualNode.Create"/> so the same position is the same node.</param>
    /// <param name="facingWord">The facing the controller gave, in words (<c>north</c>), or null.</param>
    /// <param name="facingTrueDeg">The point's own facing, degrees true, or null.</param>
    /// <param name="label">How readbacks and refusals name it: <c>the marked point</c>, or <c>marked point 2</c> among several.</param>
    /// <returns>The goal.</returns>
    public static TugGoal FreePose(GroundNode point, string? facingWord, double? facingTrueDeg, string label) =>
        new()
        {
            Kind = TugGoalKind.FreePose,
            Node = point,
            Point = point.Position,
            FacingTrueDeg = facingTrueDeg is { } facing ? new TrueHeading(facing).Degrees : null,
            FacingWord = facingWord,
            Label = label,
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
        goal switch
        {
            // The amendment re-plans from the goal's kind and node alone, which would drop a forced leg kind.
            { ForcedKind: not null } => null,
            _ => ForGoalKind(goal, standStart),
        };

    private static TugAmendment? ForGoalKind(TugGoal goal, TugPose standStart) =>
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

    /// <summary>
    /// The targets, in order; at least one. The last is the only arrival. Every one before it is a pass-through hint: the
    /// reference point passes within half the wingspan of it, in order, and keeps moving (<see cref="TugPlanBuilder"/>).
    /// </summary>
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

    /// <summary>
    /// A forced tow (<c>PUSHF</c> / <c>PUSHMF</c>): the flown-path check keeps only its runway and holding-position rules,
    /// the parked-neighbour sweep, the alley clearance, the swing band and the overswing filter are skipped, and the plan
    /// names each rule its chosen candidate overrode (<see cref="TugPlan.ForcedOverrides"/>). The outright refusals and
    /// the pass-through hints still hold.
    /// </summary>
    public required bool Forced { get; init; }
}

/// <summary>
/// A rule a forced tow's plan broke that a plain one would have been refused or steered away for, as data; the RPO note
/// is formatted from it.
/// </summary>
public abstract record TugForcedOverride;

/// <summary>The tow crosses movement-area pavement of a taxiway it was not sent to.</summary>
/// <param name="Taxiway">The taxiway.</param>
public sealed record TugForcedEntersTaxiway(string Taxiway) : TugForcedOverride;

/// <summary>A push onto a taxiway takes the aircraft's centre further past its centreline than the overshoot bound.</summary>
/// <param name="Taxiway">The taxiway pushed onto.</param>
public sealed record TugForcedOvershootsTaxiway(string Taxiway) : TugForcedOverride;

/// <summary>The outline reaches into a taxiway's object-free area the alley clearance would have kept it out of.</summary>
/// <param name="Taxiway">The taxiway.</param>
/// <param name="Part">The part of the aircraft that reaches in deepest.</param>
public sealed record TugForcedFoulsTaxiway(string Taxiway, TugFootprintPart Part) : TugForcedOverride;

/// <summary>The tow passes a parked neighbour inside the clearance floor the neighbour sweep holds a plain tow to.</summary>
/// <param name="Callsign">The neighbour's callsign.</param>
/// <param name="ClosestFt">The closest the two outlines come, feet.</param>
public sealed record TugForcedPassesNeighbour(string Callsign, double ClosestFt) : TugForcedOverride;

/// <summary>A lane push swings the nose past the lane's own turn, which the overswing filter would have dropped.</summary>
/// <param name="SwingDeg">The nose's largest running swing, degrees.</param>
/// <param name="LaneTurnDeg">The lane's own turn from the push-off's heading, degrees, unsigned.</param>
public sealed record TugForcedOverswings(double SwingDeg, double LaneTurnDeg) : TugForcedOverride;

/// <summary>The tow had parked aircraft near it and will not stop for them.</summary>
public sealed record TugForcedIgnoresParked : TugForcedOverride;

/// <summary>
/// How a forced tow's candidate passes the parked neighbours, the key a forced tow is ranked by first
/// (<see cref="Shortlist{T}"/>).
/// </summary>
/// <param name="KeepsFloor">Every run of the candidate keeps every parked neighbour at the sweep floor a plain tow is held to.</param>
/// <param name="ClosestFt">
/// The closest the candidate's outline comes to any parked neighbour's over its whole path, feet; 0 when they overlap.
/// </param>
internal readonly record struct TugNeighbourClearance(bool KeepsFloor, double ClosestFt)
{
    /// <summary>
    /// How much more room a forced candidate that breaks the floor must keep to the neighbours than another to rank
    /// ahead of it, feet; judgement call: within it, the usual keys decide.
    /// </summary>
    internal const double DecidingMarginFt = 5.0;

    /// <summary>
    /// The forced tow's neighbour ranking step: the candidates the usual keys then choose among. Those that keep every
    /// parked neighbour at the floor, when any does; else, when any passes with room, those within
    /// <see cref="DecidingMarginFt"/> of the most room any keeps (a positive pass beats an overlap); else — every one
    /// overlaps — all of them. Order-independent: the margin is measured from the best, never candidate to candidate.
    /// </summary>
    /// <typeparam name="T">The candidate type.</typeparam>
    /// <param name="candidates">The candidates to rank.</param>
    /// <param name="clearanceOf">How each candidate passes the neighbours.</param>
    /// <returns>The shortlist, in the candidates' order; empty only when <paramref name="candidates"/> is.</returns>
    internal static List<T> Shortlist<T>(IReadOnlyList<T> candidates, Func<T, TugNeighbourClearance> clearanceOf)
    {
        List<T> keepers = [.. candidates.Where(c => clearanceOf(c).KeepsFloor)];
        if (keepers.Count > 0)
        {
            return keepers;
        }

        double mostRoomFt = candidates.Count == 0 ? 0.0 : candidates.Max(c => clearanceOf(c).ClosestFt);
        return mostRoomFt <= 0.0
            ? [.. candidates]
            : [.. candidates.Where(c => (clearanceOf(c).ClosestFt > 0.0) && ((mostRoomFt - clearanceOf(c).ClosestFt) <= DecidingMarginFt))];
    }
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

    /// <summary>
    /// For a bare <c>PUSH &lt;taxiway&gt;</c> (a <see cref="TugGoalKind.StraightBackTo"/> goal), which way the plan reaches
    /// the taxiway: straight back across it, or onto a stretch running alongside the push. Null for every other push.
    /// </summary>
    public TugTaxiwayApproach? TaxiwayApproach { get; init; }

    /// <summary>
    /// For a forced tow (<see cref="TugRequest.Forced"/>), each rule the plan overrode, in the order found; empty for every
    /// other plan and for a forced one that overrode nothing.
    /// </summary>
    public required IReadOnlyList<TugForcedOverride> ForcedOverrides { get; init; }
}

/// <summary>How a bare <c>PUSH &lt;taxiway&gt;</c> reaches the taxiway (<see cref="TugPlanBuilder.AcrossAngleDeg"/> splits the two).</summary>
public enum TugTaxiwayApproach
{
    /// <summary>The push ray crosses the taxiway steeply: straight back until the aircraft reaches it.</summary>
    Across,

    /// <summary>The taxiway runs alongside the push behind the aircraft: onto its centreline with the nose along it.</summary>
    Alongside,
}

/// <summary>
/// Plans a tug move — every <c>PUSH</c> form and <c>PUSHM</c> — as a chain of <see cref="TugMove"/>s flown by
/// <see cref="TugKinematics"/>. Only the last goal is an arrival: its candidate move lists are simulated from where the
/// aircraft is, the unflyable or unsafe ones are dropped, and the best survivor is kept. Every goal before it is a
/// pass-through hint: a candidate is kept only when its reference point passes within half the wingspan of each hint,
/// in order, before the arrival's last reversal. When none does, the tow passes through the first hint on a move onto
/// it (along its line when it has a facing, straight for it when not) and the arrival is planned on from there. Pure
/// geometry: it moves nothing and reads no aircraft state.
///
/// <para><b>Stand start.</b> Off a stand the plan opens with a straight push of half the fuselage length
/// (<see cref="TugGoalKind.Clear"/> and <see cref="TugGoalKind.StraightBackTo"/> goals are straight pushes
/// already and carry no separate push-off), and the next move must be a push too.</para>
///
/// <para><b>A goal with a facing</b> (a spot, a stand, a node or the last goal with an explicit facing) is
/// reached along its approach line — through the stop point along the facing, from either side: pulled onto it or
/// pushed onto it. The side test (a pull when the stop is within 90° of the facing as seen from the aircraft, else a
/// push; the push first within 5° of that boundary) only orders the two sides. Three templates are tried per side: the
/// side move straight onto the line (a spot push stops at the staging point and creeps forward onto the mark); the
/// other kind onto the line first; and, when the nose has to rotate more than 30°, a three-point turn first — the other kind turns the nose to 90°, 60° or 30° either side of
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
        if (!builder.TryPlan(out refusal))
        {
            Log.LogDebug("Tug move refused: {Refusal}", refusal);
            return null;
        }

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
        double halfLenNm = (AircraftLength.ResolveFt(aircraftType) / 2.0) / GeoMath.FeetPerNm;
        double pullFwdNm = SpotPullForwardFt(aircraftType) / GeoMath.FeetPerNm;

        TrueHeading intoRamp = new TrueHeading(facingTrueDeg).ToReciprocal();
        return (GeoMath.ProjectPoint(spot.Position, intoRamp, halfLenNm), GeoMath.ProjectPoint(spot.Position, intoRamp, halfLenNm + pullFwdNm));
    }

    /// <summary>How far behind a spot's stop point its staging point lies, feet: clamp(0.75 × fuselage length, 40, 100).</summary>
    internal static double SpotPullForwardFt(string aircraftType) =>
        Math.Clamp(SpotPullForwardFactor * AircraftLength.ResolveFt(aircraftType), SpotPullForwardMinFt, SpotPullForwardMaxFt);

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

    /// <summary>How a refusal names the goal on its own, arrival or pass-through hint: <c>spot 6A</c>, <c>the marked point</c>.</summary>
    public required string Name { get; init; }

    /// <summary>How a refusal names the move to the goal: <c>the move to spot 6A</c>; <c>the pushback</c> for an unplaced goal.</summary>
    public required string Subject { get; init; }

    /// <summary>
    /// The goal is a pass-through hint (every <c>PUSHM</c> target but the last), which the tow passes rather than ends on.
    /// </summary>
    public bool IsHint { get; init; }

    /// <summary>The nose heading to end on, degrees true (faced, facing and taxiway-line goals).</summary>
    public double FacingTrueDeg { get; init; }

    /// <summary>Where the reference point stops; on the approach line for a faced goal. The goal's node otherwise.</summary>
    public LatLon Stop { get; init; }

    /// <summary>A faced spot's staging point, where a push onto its line stops before the creep forward.</summary>
    public LatLon? Staging { get; init; }

    /// <summary>
    /// The goal is a pass-through hint the tow is taken onto because no arrival passes it: reached as the goal would be,
    /// but with no staging point and no creep onto its stop, since the tow carries on from it to the next target.
    /// </summary>
    public bool PassThrough { get; init; }

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
        ResolvedTugGoal basis = Basis(goal, isHint: !isLast);
        return goal.Kind switch
        {
            TugGoalKind.Spot or TugGoalKind.Stand or TugGoalKind.Node or TugGoalKind.FreePose => ResolveNodeGoal(request.AircraftType, basis, facing),
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

    /// <summary>
    /// A goal's names, the same for the arrival and a pass-through hint: its label (<c>spot 5B</c>), and the move to it
    /// (<c>the move to spot 5B</c>) as the subject of a flown-path refusal.
    /// </summary>
    private static ResolvedTugGoal Basis(TugGoal goal, bool isHint)
    {
        bool unplaced = goal.Kind is TugGoalKind.Clear or TugGoalKind.Facing;
        return new ResolvedTugGoal
        {
            Goal = goal,
            Shape = TugGoalShape.Clear,
            Name = goal.Label,
            Subject = unplaced ? goal.Label : $"the move to {goal.Label}",
            IsHint = isHint,
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

    /// <summary>How a bare <c>PUSH &lt;taxiway&gt;</c> candidate reaches its taxiway; null for every other candidate.</summary>
    internal TugTaxiwayApproach? TaxiwayApproach { get; set; }

    /// <summary>
    /// The candidate's push run may pivot onto its lane further than <see cref="TugRun.MaxWanderDeg"/>: an extended
    /// straight, whose one capture turns the nose through the lane's whole rotation and is bounded by the lane goal's
    /// overswing bound instead (<c>TugPlanBuilder.TurnsTheLanesWay</c>: the lane turn + 10°).
    /// </summary>
    internal bool WanderBoundedByOverswing { get; set; }

    /// <summary>
    /// A forced tow's geometric fallback (<c>TugPlanBuilder.ForcedFallbackCandidates</c>): judged without the run-wander
    /// and pull-past-the-line shape rules, since it is what the tug flies when every template broke them; it must still
    /// end on the stop with the facing.
    /// </summary>
    internal bool IsForcedFallback { get; init; }

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
/// How a candidate was judged: the shape rule it breaks, the flown-path rule it breaks, — when the only shape rule it
/// breaks is its final line pull running past the stop — how far past, feet, and the pass-through hint it misses.
/// </summary>
internal readonly record struct TugVerdict(string? ShapeDrop, TugPathRefusal? Path, double? FinalPullOvershootFt, string? HintMiss)
{
    internal bool Dropped => (ShapeDrop is not null) || (Path is not null) || (HintMiss is not null);
}

/// <summary>A candidate that fouls a protected taxiway's object-free area: how deep, and how far its nose swings each way.</summary>
internal readonly record struct TugFoulingCandidate(TugCandidate Candidate, TugTaxiwayFouling Fouling, TugNoseSwing Swing);

/// <summary>
/// How far a nose turned each way from a reference heading, accumulated sample to sample so a turn past 180° reads as
/// the turn it is rather than folding back: the most the running turn reached to the right (clockwise) and to the left
/// of the reference, both zero or more, degrees.
/// </summary>
internal readonly record struct TugNoseSwing(double RightDeg, double LeftDeg)
{
    /// <summary>The larger of the two: the most the nose was ever turned from the reference, degrees.</summary>
    internal double MaxDeg => Math.Max(RightDeg, LeftDeg);

    /// <summary>How far the nose went against a turn of <paramref name="turnDeg"/> (right positive), degrees.</summary>
    internal double AgainstDeg(double turnDeg) => turnDeg >= 0.0 ? LeftDeg : RightDeg;

    /// <summary>The swing of the noses in <paramref name="noseDegs"/>, in order, from <paramref name="referenceDeg"/>.</summary>
    internal static TugNoseSwing From(double referenceDeg, IEnumerable<double> noseDegs)
    {
        var previous = new TrueHeading(referenceDeg);
        double runningDeg = 0.0;
        double rightDeg = 0.0;
        double leftDeg = 0.0;
        foreach (double noseDeg in noseDegs)
        {
            var nose = new TrueHeading(noseDeg);
            runningDeg += previous.SignedAngleTo(nose);
            previous = nose;
            rightDeg = Math.Max(rightDeg, runningDeg);
            leftDeg = Math.Max(leftDeg, -runningDeg);
        }

        return new TugNoseSwing(rightDeg, leftDeg);
    }
}

/// <summary>What a goal's candidates have yielded so far: the best survivor and the most severe path-only refusal.</summary>
internal sealed class TugChoiceTally
{
    internal TugCandidate? Best { get; set; }

    internal TugPathRefusal? PathRefusal { get; set; }

    /// <summary>Every candidate that survived judging, in the order judged.</summary>
    internal List<TugCandidate> Survivors { get; } = [];

    /// <summary>A candidate whose shape was sound was dropped for swinging into a parked or held neighbour.</summary>
    internal bool NeighbourDropped { get; set; }

    /// <summary>
    /// A candidate whose shape was sound was dropped only for missing a pass-through hint: its flown path is never checked
    /// (<c>TugPlanBuilder.Judge</c>), since a candidate that misses a hint was never a way to fly the move.
    /// </summary>
    internal bool HintDropped { get; private set; }

    /// <summary>The choice among the goal's own templates is over: <see cref="PathRefusal"/> no longer changes.</summary>
    internal bool ChoiceClosed { get; private set; }

    /// <summary>
    /// Counts a dropped candidate's flown-path reason: until the choice is closed, the most severe one is the refusal when
    /// nothing survives; a parked neighbour's drop counts toward <see cref="NeighbourDropped"/> whenever it comes.
    /// </summary>
    internal void CountDrop(TugVerdict verdict)
    {
        if (verdict.ShapeDrop is not null)
        {
            return;
        }

        if (verdict.HintMiss is not null)
        {
            HintDropped = true;
            return;
        }

        if (!ChoiceClosed)
        {
            PathRefusal = ((verdict.Path is { } found) && ((PathRefusal is not { } held) || (found.Severity < held.Severity))) ? found : PathRefusal;
        }

        NeighbourDropped |= verdict.Path is { Severity: TugPathSeverity.ParkedNeighbour };
    }

    /// <summary>
    /// Closes the choice among the goal's own templates: the refusal is theirs, so the speculative pools the keep builds
    /// after it can never change its wording, though their neighbour drops still open the search past the neighbour.
    /// </summary>
    internal void CloseChoice() => ChoiceClosed = true;
}

/// <summary>
/// Candidate pools built one at a time and only when asked for: each pool is built at most once, however many passes
/// walk them, so a pass that stops at the first pool builds nothing after it. The source's enumerator is never
/// disposed: the pools come from iterator methods that hold no resources, so a walk left unfinished leaks nothing.
/// </summary>
internal sealed class TugLazyPools(IEnumerable<List<TugCandidate>> source)
{
    private readonly IEnumerator<List<TugCandidate>> _source = source.GetEnumerator();
    private readonly List<List<TugCandidate>> _built = [];

    /// <summary>The pools in order, building each the first time a pass reaches it.</summary>
    internal IEnumerable<List<TugCandidate>> All()
    {
        for (int i = 0; ; i++)
        {
            if ((i == _built.Count) && !TryBuildNext())
            {
                yield break;
            }

            yield return _built[i];
        }
    }

    /// <summary>Every candidate of every pool, building the pools not built yet, each candidate once.</summary>
    internal List<TugCandidate> Everything() => [.. All().SelectMany(pool => pool).Distinct()];

    /// <summary>The pools built so far, in order, without building any more.</summary>
    internal IReadOnlyList<List<TugCandidate>> Built() => _built;

    private bool TryBuildNext()
    {
        if (!_source.MoveNext())
        {
            return false;
        }

        _built.Add(_source.Current);
        return true;
    }
}

/// <summary>
/// The footprints of the parking stands near a tug move that no parked or held aircraft stands on: each is a parked
/// aircraft of the moving aircraft's own type on the stand's heading — the stand nodes carry no type — drawn as the same
/// <see cref="GroundOutline"/> the neighbour sweep measures. The planner prefers a candidate whose outline stays out of
/// them; entering one never refuses a push.
/// </summary>
internal sealed class TugEmptyStands
{
    /// <summary>
    /// How close a candidate's planned outline may come to an empty stand's footprint and still stay out of it, feet:
    /// the alley clearance's planning margin (<see cref="TugTaxiwayClearance.ClearanceMarginFt"/>), the most a flown
    /// closest approach fell short of the planned one over the push review cases.
    /// </summary>
    internal const double StandMarginFt = TugTaxiwayClearance.ClearanceMarginFt;

    /// <summary>
    /// A parked or held aircraft whose reference point lies this close to a stand node occupies that stand even when it
    /// is not recorded on it, feet; a judgement call — well inside half the shortest fuselage that parks on a stand.
    /// </summary>
    private const double OccupiedWithinFt = 20.0;

    /// <summary>
    /// The most any point of the moving outline may travel between two of the poses a candidate is checked at, feet: the
    /// planned samples lie about 5 ft apart, so each gap is split until no outline point moves further than this.
    /// </summary>
    private const double CheckSpacingFt = 2.0;

    private readonly GroundOutlineFrame _frame;
    private readonly GroundOutlineSize _pushing;
    private readonly GroundOutlineSize _pulling;
    private readonly double _reachFt;
    private readonly List<(string Name, OutlinePoint Centre, GroundOutline Outline)> _stands;

    internal TugEmptyStands(AirportGroundLayout layout, TugRequest request)
    {
        _frame = new GroundOutlineFrame(request.Start.Position);
        _pushing = GroundOutlineSize.Of(request.AircraftType, towedNoseFirst: false);
        _pulling = GroundOutlineSize.Of(request.AircraftType, towedNoseFirst: true);
        _reachFt = _pulling.ReachFt + _pushing.ReachFt + StandMarginFt;
        double rangeFt = TugMovePlanner.MaxGoalDistanceFt + _reachFt;
        _stands =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null) && (n.TrueHeading is not null))
                .Where(n => (GeoMath.DistanceNm(request.Start.Position, n.Position) * GeoMath.FeetPerNm) <= rangeFt)
                .Where(n => !IsOccupied(n, request.ParkedNeighbours))
                .OrderBy(n => n.Id)
                .Select(n =>
                    (n.Name!, _frame.ToLocal(n.Position), GroundOutline.At(_frame.ToLocal(n.Position), n.TrueHeading!.Value.Degrees, _pushing))
                ),
        ];
    }

    /// <summary>The stands whose footprint the outline at <paramref name="pose"/> comes within <see cref="StandMarginFt"/> of.</summary>
    internal IReadOnlySet<string> EnteredAt(TugPose pose, PushbackLegKind kind)
    {
        var entered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddEntered(entered, pose, kind, excluded: entered);
        return entered;
    }

    /// <summary>
    /// The stands but <paramref name="excluded"/> whose footprint the outline comes within <see cref="StandMarginFt"/> of
    /// anywhere along <paramref name="traces"/> — a pulled move carrying the tug's lead ahead of the nose — checked at the
    /// planned samples and between them every <see cref="CheckSpacingFt"/> of outline travel.
    /// </summary>
    internal IReadOnlySet<string> Entered(IEnumerable<TugMoveTrace> traces, IReadOnlySet<string> excluded)
    {
        var entered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TugMoveTrace trace in traces)
        {
            IReadOnlyList<TugPose> samples = trace.Samples;
            for (int i = 0; i < samples.Count; i++)
            {
                foreach (TugPose pose in PosesUpTo(samples, i))
                {
                    AddEntered(entered, pose, trace.Move.Kind, excluded);
                }
            }
        }

        return entered;
    }

    /// <summary>Sample <paramref name="i"/>, preceded by the poses splitting the gap from the sample before it.</summary>
    private IEnumerable<TugPose> PosesUpTo(IReadOnlyList<TugPose> samples, int i)
    {
        TugPose to = samples[i];
        if (i > 0)
        {
            TugPose from = samples[i - 1];
            double turnDeg = new TrueHeading(from.NoseTrueDeg).SignedAngleTo(new TrueHeading(to.NoseTrueDeg));
            double travelFt =
                (GeoMath.DistanceNm(from.Position, to.Position) * GeoMath.FeetPerNm) + (_pulling.ReachFt * Math.Abs(turnDeg) * Math.PI / 180.0);
            int steps = (int)Math.Ceiling(travelFt / CheckSpacingFt);
            for (int step = 1; step < steps; step++)
            {
                double fraction = (double)step / steps;
                var position = new LatLon(
                    from.Position.Lat + (fraction * (to.Position.Lat - from.Position.Lat)),
                    from.Position.Lon + (fraction * (to.Position.Lon - from.Position.Lon))
                );
                yield return new TugPose(position, from.NoseTrueDeg + (fraction * turnDeg));
            }
        }

        yield return to;
    }

    private void AddEntered(HashSet<string> entered, TugPose pose, PushbackLegKind kind, IReadOnlySet<string> excluded)
    {
        OutlinePoint reference = _frame.ToLocal(pose.Position);
        GroundOutline? mover = null;
        foreach ((string name, OutlinePoint centre, GroundOutline outline) in _stands)
        {
            if ((OutlinePoint.Distance(reference, centre) > _reachFt) || entered.Contains(name) || excluded.Contains(name))
            {
                continue;
            }

            mover ??= GroundOutline.At(reference, pose.NoseTrueDeg, kind == PushbackLegKind.Pull ? _pulling : _pushing);
            if (GroundOutline.Clearance(mover.Value, outline) < StandMarginFt)
            {
                entered.Add(name);
            }
        }
    }

    private static bool IsOccupied(GroundNode stand, IReadOnlyList<TugParkedNeighbour> neighbours) =>
        neighbours.Any(n =>
            string.Equals(n.StandName, stand.Name, StringComparison.OrdinalIgnoreCase)
            || ((GeoMath.DistanceNm(n.Position, stand.Position) * GeoMath.FeetPerNm) <= OccupiedWithinFt)
        );
}

/// <summary>
/// Plans a request — its last goal as the arrival, every goal before it as a pass-through hint (<see cref="TryPlan"/>) —
/// holding the plan so far.
/// </summary>
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
    /// The straight pushes a stepped fouling-fallback candidate tries between the push-off and its push turn, feet;
    /// judgement calls (user, 2026-09-24).
    /// </summary>
    private static readonly double[] SteppedStraightsFt = [0.0, 20.0, 40.0, 60.0, 80.0, 100.0, 120.0];

    /// <summary>
    /// The fractions of the pivot onto the lane's facing a stepped fouling-fallback candidate's push turn takes the nose
    /// through before the lane capture; judgement calls (user, 2026-09-24).
    /// </summary>
    private static readonly double[] SteppedTurnFractions = [1.0 / 3.0, 0.5, 2.0 / 3.0];

    /// <summary>
    /// How far past the lane's own rotation from the push-off's heading a fouling candidate's nose may swing before it is
    /// dropped as an overswing, degrees; a judgement call (user, 2026-09-24).
    /// </summary>
    internal const double OverswingMarginDeg = 10.0;

    /// <summary>
    /// How far a lane goal's fouling candidate may turn the nose against the lane's own turn before it is dropped,
    /// degrees; a judgement call (user, 2026-09-24).
    /// </summary>
    internal const double WrongWayToleranceDeg = 5.0;

    /// <summary>
    /// How far apart two fouling candidates' nose swings may be and still count as tied, degrees, so exposure decides
    /// between them rather than a fraction of a degree; a judgement call (user, 2026-09-24).
    /// </summary>
    private const double SwingTieDeg = 5.0;

    /// <summary>
    /// How far a lane push's nose may run past its target turn, either way, before the candidate leaves the swing band
    /// (<see cref="InSwingBand(TugNoseSwing, double, bool)"/>), degrees; a judgement call (user, 2026-09-25).
    /// </summary>
    private const double SwingBandMarginDeg = 100.0;

    /// <summary>
    /// The target turn onto an explicit <c>FACE</c>/<c>TAIL</c> facing past which the other way round counts too for the
    /// swing band, degrees: a facing that far round can be reached turning either way; a judgement call (user, 2026-09-25).
    /// </summary>
    private const double SwingBandEitherWayDeg = 150.0;

    /// <summary>
    /// How far a push-off turning the tail away from a parked neighbour turns the nose before the move onto the goal,
    /// degrees; judgement calls (user, 2026-09-24: search an angled push-off before refusing a neighbour). SFO F5 with a
    /// B738 on F6: a CRJ7's straight push-off comes within 22.9 ft of it, a push turning 30° or more keeps 26.6 ft.
    /// </summary>
    private static readonly double[] AngledPushOffsDeg = [15.0, 30.0, 45.0];

    /// <summary>
    /// The step an extended straight (<see cref="ExtendedStraightCandidates"/>) lengthens the straight-then-line straight
    /// by, feet, and the most it lengthens it; judgement calls (user, 2026-09-25: push further straight back before the
    /// turn onto the lane). SFO D2 → 5A with an E75L on D1: the turn onto the lane keeps the 24.5 ft floor from about
    /// 45 ft of extra straight, and from 120 ft the push crosses the lane and bends back.
    /// </summary>
    private const double ExtendedStraightStepFt = 20.0;

    private const double MaxExtendedStraightFt = 200.0;

    /// <summary>
    /// How far back from the stop, in line-capture radii (<see cref="TugKinematics.RolloutMarginRadii"/> × the routine
    /// radius), a forced tow's geometric fallback (<see cref="ForcedFallbackCandidates"/>) puts the point it tows to before
    /// the move onto the line; judgement calls: two radii is the least one capture from square can need, the rest give it
    /// room.
    /// </summary>
    private static readonly double[] ForcedFallbackRunInRadii = [2.0, 3.0, 4.0, 6.0];

    /// <summary>
    /// How far past the lane's centreline a multi-point path (<see cref="MultiPointCandidates"/>) pushes its reference
    /// point before its push turn, feet; judgement calls (user, 2026-09-25).
    /// </summary>
    private static readonly double[] MultiPointPastLaneFt = [10.0, 30.0, 50.0];

    /// <summary>How far a multi-point path's push turn takes the nose the lane's way, degrees; judgement calls.</summary>
    private static readonly double[] MultiPointPushTurnsDeg = [15.0, 30.0, 45.0, 60.0, 90.0];

    /// <summary>How far a multi-point path's pull runs straight, feet; judgement calls.</summary>
    private static readonly double[] MultiPointPullStraightsFt = [20.0, 40.0, 60.0];

    /// <summary>How much further a multi-point path's turning pull takes the nose the lane's way, degrees; judgement calls.</summary>
    private static readonly double[] MultiPointPullTurnsDeg = [30.0, 60.0];

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

    /// <summary>The half-fuselage straight push every plan off a stand starts with (rule 1).</summary>
    private readonly TugMove _standPushOff;

    /// <summary>The flown-path check, or null when there is no layout to check against.</summary>
    private readonly TugPathCheck? _pathCheck;

    /// <summary>The names of the taxiway straight behind the stand the plan starts on, exempt for every goal; empty otherwise.</summary>
    private readonly IReadOnlySet<string> _standBehindNames;

    /// <summary>
    /// The pass-through hints (every goal but the last) the tow has still to pass, in order; a pass-through tow
    /// (<see cref="TryPassFirstHint"/>) removes the one it passes.
    /// </summary>
    private readonly List<ResolvedTugGoal> _pendingHints = [];

    private readonly List<TugMoveTrace> _moves = [];
    private TugPose _end;
    private PushbackLegKind? _lastKind;
    private TugRun? _run;
    private readonly List<TugPlanWarning> _warnings = [];

    /// <summary>What a forced plan's kept candidates overrode, each once (<see cref="NoteForcedOverrides"/>).</summary>
    private readonly List<TugForcedOverride> _overrides = [];
    private GroundNode? _facingJunction;
    private string? _facingTaxiwayName;
    private TugTaxiwayApproach? _taxiwayApproach;
    private TugTaxiwayClearance? _clearance;
    private TugEmptyStands? _emptyStands;

    /// <summary>The goal being kept's unprotected taxiways (<see cref="PrepareKeep"/>).</summary>
    private HashSet<string> _excludedTaxiways = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The goal being kept's empty stands every candidate enters, which no candidate is ranked down for
    /// (<see cref="PrepareKeep"/>).
    /// </summary>
    private IReadOnlySet<string> _excludedStands = new HashSet<string>();

    /// <summary>Of <see cref="_excludedStands"/>, those the stand push-off enters.</summary>
    private IReadOnlySet<string> _pushOffStands = new HashSet<string>();

    // What a candidate's keep has measured of it; a candidate belongs to one goal, and its moves never change once judged.
    private readonly Dictionary<TugCandidate, bool> _clearByCandidate = [];
    private readonly Dictionary<TugCandidate, TugTaxiwayFouling> _foulingByCandidate = [];
    private readonly Dictionary<TugCandidate, IReadOnlySet<string>> _standsByCandidate = [];
    private readonly Dictionary<TugCandidate, bool> _inSwingBandByCandidate = [];
    private readonly Dictionary<TugCandidate, TugNeighbourClearance> _neighbourClearanceByCandidate = [];

    /// <summary>
    /// Each ranked candidate's <see cref="LeadInDepartureFt"/>; a candidate belongs to one goal, and its moves never change
    /// once judged.
    /// </summary>
    private readonly Dictionary<TugCandidate, double> _leadInDepartureByCandidate = [];

    internal TugPlanBuilder(AirportGroundLayout? layout, TugRequest request)
    {
        _layout = layout;
        _request = request;
        _standPushOff = TugMove.Straight(PushbackLegKind.Push, AircraftLength.ResolveFt(request.AircraftType) / 2.0);
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
        )
        {
            TaxiwayApproach = _taxiwayApproach,
            ForcedOverrides = ForcedOverrides(),
        };

    /// <summary>
    /// The rules a forced plan overrode (<see cref="NoteForcedOverrides"/>), then — when the request had any parked
    /// neighbour — that the tow will not stop for parked aircraft. Empty for a plan that is not forced.
    /// </summary>
    private List<TugForcedOverride> ForcedOverrides() =>
        !_request.Forced ? []
        : _request.ParkedNeighbours.Count > 0 ? [.. _overrides, new TugForcedIgnoresParked()]
        : [.. _overrides];

    /// <summary>
    /// Plans the request: every goal but the last is a pass-through hint, refused when it carries a facing of its own (only
    /// the last point takes one) or outright as a goal would be, and the last is the arrival (<see cref="TryArrive"/>),
    /// planned from the start with the context a first goal has.
    /// </summary>
    /// <param name="refusal">Why the move cannot be planned, or empty on success.</param>
    /// <returns>True when the plan is complete.</returns>
    internal bool TryPlan(out string refusal)
    {
        int arrival = _request.Goals.Count - 1;
        LatLon from = _end.Position;
        for (int i = 0; i < arrival; i++)
        {
            ResolvedTugGoal hint = TugGoalResolver.Resolve(_layout, _request, i);
            if (hint.Goal.FacingTrueDeg is not null)
            {
                refusal = $"Unable, {hint.Name} is passed through; only the last point takes a facing";
                return false;
            }

            if (IsRefusedOutright(hint, from, out refusal))
            {
                return false;
            }

            _pendingHints.Add(hint);
            from = hint.Stop;
        }

        return TryArrive(arrival, _request.StartsAtStand, out refusal);
    }

    /// <summary>
    /// Plans the arrival, whose candidates must pass every pending hint (<see cref="HintMissReason"/>). An arrival refused
    /// outright, or one no candidate of which was dropped only for missing a hint, is refused as it stands. Otherwise the tow
    /// passes through the first pending hint (<see cref="TryPassFirstHint"/>) and the arrival is planned on from there, hint
    /// by hint, while its candidates still miss one; when it still fails, the first arrival's refusal is the move's.
    /// </summary>
    private bool TryArrive(int arrival, bool offStand, out string refusal)
    {
        ResolvedTugGoal goal = WithStandBehindExempt(TugGoalResolver.Resolve(_layout, _request, arrival));
        LatLon from = (_pendingHints.Count > 0) ? _pendingHints[^1].Stop : _end.Position;
        if (IsRefusedOutright(goal, from, out refusal))
        {
            return false;
        }

        if (TryPlanResolved(goal, offStand, out refusal, out bool hintMissed))
        {
            return true;
        }

        string arrivalRefusal = refusal;
        while (hintMissed && (_pendingHints.Count > 0))
        {
            Log.LogDebug("Tug {Subject}: no arrival passes {Hint}; falling back to a pass-through tow", goal.Subject, _pendingHints[0].Goal.Label);
            if (!TryPassFirstHint(offStand, out refusal))
            {
                return false;
            }

            offStand = false;
            if (TryPlanResolved(goal, offStand, out _, out hintMissed))
            {
                refusal = string.Empty;
                return true;
            }
        }

        refusal = arrivalRefusal;
        return false;
    }

    /// <summary>
    /// Builds, judges and keeps a resolved goal's candidates from where the plan has got to, and commits the one kept;
    /// false, with the refusal, when none is, and whether any candidate was dropped only for missing a pass-through hint.
    /// </summary>
    private bool TryPlanResolved(ResolvedTugGoal goal, bool offStand, out string refusal, out bool hintMissed)
    {
        hintMissed = false;
        if (!TryBuildCandidates(goal, offStand, out List<TugCandidate>? candidates, out refusal))
        {
            return false;
        }

        TugChoiceTally tally = Choose(goal, candidates, offStand);
        TugCandidate? best =
            (IsKeptOffTheMovementArea(goal) ? KeepOffTheMovementArea(goal, tally, offStand) : tally.Best) ?? ForcedFallback(goal, tally, offStand);
        if (best is null)
        {
            hintMissed = tally.HintDropped;
            refusal = Refusal(goal, tally);
            Log.LogDebug("Tug {Subject}: refused: {Refusal}", goal.Subject, refusal);
            return false;
        }

        refusal = string.Empty;
        if (goal.Shape == TugGoalShape.TaxiwayLine)
        {
            NoteLongPush(goal, best);
        }

        if (_request.Forced)
        {
            NoteForcedOverrides(goal, best, offStand);
        }

        Commit(best, goal);
        return true;
    }

    /// <summary>
    /// A forced tow's last resort when a faced goal kept no candidate, no candidate was refused by the flown-path check and
    /// none was dropped for missing a hint: the best geometric fallback (<see cref="ForcedFallbackCandidates"/>), prepared
    /// to be kept off the movement area when the goal is. Null for any other tow or goal, or when no fallback fits.
    /// </summary>
    private TugCandidate? ForcedFallback(ResolvedTugGoal goal, TugChoiceTally tally, bool offStand)
    {
        if (!_request.Forced || (goal.Shape != TugGoalShape.Faced) || (tally.PathRefusal is not null) || tally.HintDropped)
        {
            return null;
        }

        TugCandidate? best = Choose(goal, ForcedFallbackCandidates(goal, offStand), offStand).Best;
        if ((best is not null) && IsKeptOffTheMovementArea(goal))
        {
            PrepareKeep(goal, offStand);
        }

        return best;
    }

    /// <summary>
    /// Judges a forced plan's kept candidate against the rules forcing skipped, and records each it breaks
    /// (<see cref="AddOverride(List{TugForcedOverride}, TugForcedOverride)"/>): the flown-path check's taxiway rules
    /// (<see cref="NoteForcedPathOverrides"/>), the alley clearance and the overswing filter
    /// (<see cref="NoteForcedFouling"/>), and the parked-neighbour sweep (<see cref="NoteForcedNeighbours"/>).
    /// </summary>
    private void NoteForcedOverrides(ResolvedTugGoal goal, TugCandidate kept, bool offStand)
    {
        NoteForcedPathOverrides(goal, kept);
        NoteForcedFouling(goal, kept, offStand);
        NoteForcedNeighbours(kept);
    }

    /// <summary>
    /// Every taxiway rule of the flown-path check the kept candidate breaks (<see cref="TugPathCheck.SkippedTaxiwayHits"/>):
    /// each taxiway it enters, or the taxiway it is pushed onto and runs past.
    /// </summary>
    private void NoteForcedPathOverrides(ResolvedTugGoal goal, TugCandidate kept)
    {
        if (_pathCheck is null)
        {
            return;
        }

        string? markedPoint = goal.Goal.Kind == TugGoalKind.FreePose ? goal.Name : null;
        foreach (
            TugPathRefusal hit in _pathCheck.SkippedTaxiwayHits(kept.Traces, goal.ExemptNames, goal.OvershootTaxiway, (goal.Subject, markedPoint))
        )
        {
            TugForcedOverride? forced = hit switch
            {
                { Severity: TugPathSeverity.MovementArea, Taxiway: { } entered } => new TugForcedEntersTaxiway(entered),
                { Severity: TugPathSeverity.TaxiwayOvershoot, Taxiway: { } overshot } => new TugForcedOvershootsTaxiway(overshot),
                _ => null,
            };
            if (forced is not null)
            {
                AddOverride(_overrides, forced);
            }
        }
    }

    /// <summary>
    /// The alley clearance, for a goal kept off the movement area: the taxiway the kept candidate's outline fouls, with
    /// the part that reaches in deepest, and — for a lane goal — the overswing filter (<see cref="NoteForcedOverswing"/>).
    /// </summary>
    private void NoteForcedFouling(ResolvedTugGoal goal, TugCandidate kept, bool offStand)
    {
        if (!IsKeptOffTheMovementArea(goal) || (_clearance is null))
        {
            return;
        }

        TugTaxiwayFouling fouling = Fouling(kept);
        if (fouling.Fouls && (fouling.Taxiway is { } fouled))
        {
            AddOverride(_overrides, new TugForcedFoulsTaxiway(fouled, fouling.Part));
            NoteForcedOverswing(goal, new TugFoulingCandidate(kept, fouling, NoseSwing(kept, offStand)), offStand);
        }
    }

    /// <summary>Every parked neighbour the kept candidate passes inside the sweep floor, with the closest the outlines come.</summary>
    private void NoteForcedNeighbours(TugCandidate kept)
    {
        foreach (TugParkedNeighbour neighbour in _request.ParkedNeighbours)
        {
            if (NeighbourPass(kept.Traces, neighbour) is { KeepsFloor: false, ClosestFt: double closestFt })
            {
                AddOverride(_overrides, new TugForcedPassesNeighbour(neighbour.Callsign, closestFt));
            }
        }
    }

    /// <summary>
    /// A forced tow's last resort for a faced goal none of whose templates fits: the simplest geometric tow the turn
    /// radius allows — after the stand push-off, a tow straight for a point on the goal's approach line
    /// <see cref="ForcedFallbackRunInRadii"/> line-capture radii back from the stop, on either side, then the side's move
    /// onto the line (<see cref="AddApproach"/>). The tow to the point is a push off a stand (rule 1), else a push when the
    /// point is behind the aircraft and a pull when it is ahead. Judged like any candidate, so it must still end on the
    /// stop with the facing.
    /// </summary>
    private List<TugCandidate> ForcedFallbackCandidates(ResolvedTugGoal goal, bool offStand)
    {
        TugMove? pushOff = offStand ? _standPushOff : null;
        double captureRadiusFt = TugKinematics.RolloutMarginRadii * TugKinematics.TurnRadiusFt(_request.AircraftType, tight: false);
        var candidates = new List<TugCandidate>();
        foreach (PushbackLegKind side in new[] { PushbackLegKind.Pull, PushbackLegKind.Push })
        {
            // The pull onto the stop comes from behind it along the facing, the push from ahead of it.
            double runInDeg = side == PushbackLegKind.Pull ? goal.FacingTrueDeg + 180.0 : goal.FacingTrueDeg;
            foreach (double radii in ForcedFallbackRunInRadii)
            {
                LatLon runIn = GeoMath.ProjectPoint(goal.Stop, new TrueHeading(runInDeg), radii * captureRadiusFt / GeoMath.FeetPerNm);
                TugCandidate candidate = NewForcedFallback($"forced fallback, {side} side, run-in {radii:0} radii", pushOff);
                bool behind = TugMovePlanner.AbsDiffDeg(GeoMath.BearingTo(candidate.End.Position, runIn), candidate.End.NoseTrueDeg) > AheadDeg;
                candidate.Add(TugMove.ToPoint(offStand || behind ? PushbackLegKind.Push : PushbackLegKind.Pull, runIn));
                AddApproach(candidate, goal, side);
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private TugCandidate NewForcedFallback(string template, TugMove? pushOff)
    {
        var candidate = new TugCandidate(template, _request.AircraftType, _end, _lastKind) { IsForcedFallback = true };
        if (pushOff is not null)
        {
            candidate.Add(pushOff);
        }

        return candidate;
    }

    /// <summary>A fouling lane candidate the overswing filter would have dropped: its swing and the lane's own turn.</summary>
    private void NoteForcedOverswing(ResolvedTugGoal goal, TugFoulingCandidate fouling, bool offStand)
    {
        if (IsLaneGoal(goal, offStand) && !TurnsTheLanesWay(goal, fouling))
        {
            double laneTurnDeg = new TrueHeading(fouling.Candidate.Traces[0].End.NoseTrueDeg).AbsAngleTo(new TrueHeading(goal.FacingTrueDeg));
            AddOverride(_overrides, new TugForcedOverswings(fouling.Swing.MaxDeg, laneTurnDeg));
        }
    }

    /// <summary>
    /// Records a rule a forced plan overrode, once: an override equal to one already held is dropped, and a parked
    /// neighbour is noted once however many stretches of the tow pass it, at the closest any comes — so an overlap
    /// (<c>0 ft</c>) outranks every pass. The plan is only a candidate here; the command logs the overrides of the plan
    /// it installs.
    /// </summary>
    /// <param name="overrides">The overrides so far, in the order found; updated in place.</param>
    /// <param name="forced">The override to record.</param>
    internal static void AddOverride(List<TugForcedOverride> overrides, TugForcedOverride forced)
    {
        if (forced is TugForcedPassesNeighbour passes)
        {
            int held = overrides.FindIndex(o => o is TugForcedPassesNeighbour other && (other.Callsign == passes.Callsign));
            if (held < 0)
            {
                overrides.Add(passes);
            }
            else if (passes.ClosestFt < ((TugForcedPassesNeighbour)overrides[held]).ClosestFt)
            {
                overrides[held] = passes;
            }

            return;
        }

        if (!overrides.Contains(forced))
        {
            overrides.Add(forced);
        }
    }

    /// <summary>
    /// Whether a goal's choice is ranked past its parked neighbours first (<see cref="BestPastNeighbours"/>): a forced tow
    /// with any parked neighbour. Every other tow is ranked by the usual keys alone (<see cref="IsBetter"/>).
    /// </summary>
    private bool RanksPastNeighbours => _request.Forced && (_request.ParkedNeighbours.Count > 0);

    /// <summary>
    /// A forced tow's choice among <paramref name="candidates"/>: the neighbour ranking step
    /// (<see cref="TugNeighbourClearance.Shortlist{T}"/>) first, then the usual keys (<see cref="IsBetter"/>) among the
    /// shortlist. Null when there are no candidates.
    /// </summary>
    private TugCandidate? BestPastNeighbours(ResolvedTugGoal goal, bool offStand, IReadOnlyList<TugCandidate> candidates)
    {
        TugCandidate? best = null;
        foreach (TugCandidate candidate in TugNeighbourClearance.Shortlist(candidates, NeighbourClearance))
        {
            best = IsBetter(goal, offStand, candidate, best) ? candidate : best;
        }

        return best;
    }

    /// <summary>
    /// Whether a candidate keeps every parked neighbour at the sweep floor, and the closest its outline comes to any of
    /// them over its whole path, feet (<see cref="NeighbourPass"/>); measured once.
    /// </summary>
    private TugNeighbourClearance NeighbourClearance(TugCandidate candidate)
    {
        if (!_neighbourClearanceByCandidate.TryGetValue(candidate, out TugNeighbourClearance clearance))
        {
            List<TugNeighbourClearance> passes = [.. _request.ParkedNeighbours.Select(n => NeighbourPass(candidate.Traces, n))];
            double closestFt = passes.Count == 0 ? double.MaxValue : passes.Min(p => p.ClosestFt);
            clearance = new TugNeighbourClearance(passes.All(p => p.KeepsFloor), closestFt);
            _neighbourClearanceByCandidate[candidate] = clearance;
            Log.LogDebug(
                "Tug forced candidate {Template}: {Keeps} the neighbour floor, closest {ClosestFt:F1} ft",
                candidate.Template,
                clearance.KeepsFloor ? "keeps" : "breaks",
                clearance.ClosestFt
            );
        }

        return clearance;
    }

    /// <summary>
    /// How a candidate passes one parked neighbour: whether every run of it (<see cref="RunPathFrom"/>) keeps the sweep
    /// floor a plain tow is held to (<see cref="GroundOutlineSweep"/>), and the closest its outline comes to the
    /// neighbour's over its whole path, feet.
    /// </summary>
    private TugNeighbourClearance NeighbourPass(IReadOnlyList<TugMoveTrace> traces, TugParkedNeighbour neighbour)
    {
        var frame = new GroundOutlineFrame(_request.Start.Position);
        var neighbourSize = GroundOutlineSize.Of(neighbour.AircraftType, towedNoseFirst: false);
        var outline = GroundOutline.At(frame.ToLocal(neighbour.Position), neighbour.TrueHeadingDeg, neighbourSize);
        bool fouls = false;
        double closestFt = double.MaxValue;
        for (int i = 0; i < traces.Count; i++)
        {
            var moverSize = GroundOutlineSize.Of(_request.AircraftType, towedNoseFirst: traces[i].Move.Kind == PushbackLegKind.Pull);
            foreach (TugPose pose in traces[i].Samples)
            {
                closestFt = Math.Min(
                    closestFt,
                    GroundOutline.Clearance(GroundOutline.At(frame.ToLocal(pose.Position), pose.NoseTrueDeg, moverSize), outline)
                );
            }

            List<(TugPose Pose, double AlongFt)> path = RunPathFrom(traces, i);
            if (path.Count > 0)
            {
                GroundOutlineSweepResult swept = GroundOutlineSweep.Sweep(
                    path,
                    path[0].Pose,
                    frame,
                    moverSize,
                    neighbour.Position,
                    neighbour.TrueHeadingDeg,
                    neighbourSize
                );
                fouls |= swept.Foul is not null;
            }
        }

        return new TugNeighbourClearance(!fouls, closestFt);
    }

    /// <summary>Appends a kept candidate's moves to the plan, which then continues from where it ends.</summary>
    private void Commit(TugCandidate best, ResolvedTugGoal goal)
    {
        _moves.AddRange(best.Traces);
        if (best.FacingJunction is { } facingJunction)
        {
            _facingJunction = facingJunction;
            _facingTaxiwayName = goal.Goal.FacingTaxiwayName;
        }
        if (best.TaxiwayApproach is { } approach)
        {
            _taxiwayApproach = approach;
        }
        _end = best.End;
        _lastKind = best.LastKind;
        _run = TugRun.Follow(_run, best.Traces).Open;
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
    /// The candidate a non-movement-area goal keeps. For a lane goal, only the candidates within the swing band
    /// (<see cref="InSwingBand(ResolvedTugGoal, TugCandidate)"/>) are considered while any of them survives; the band never
    /// refuses a push on its own. Among those, by three keys in order of precedence:
    /// <list type="number">
    /// <item>taxiway clearance: a candidate whose flown outline stays outside the object-free area of every protected
    /// taxiway — the movement-area taxiways but those the goal names, the taxiway straight behind the stand, and any the
    /// outline already reaches into where the goal starts — beats every one that fouls; with none clear, the fouling
    /// ranking (<see cref="LeastFouling"/>) picks, and a <see cref="TugFoulsTaxiwayWarning"/> goes on the plan;</item>
    /// <item>the pools in order (<see cref="KeepPools"/>);</item>
    /// <item>within a pool, <see cref="Choose"/>'s ranking with staying out of the empty neighbouring stands' footprints
    /// (<see cref="StaysOutOfEmptyStands"/>) between the reversal count and the path (<see cref="IsBetterClear"/>).</item>
    /// </list>
    /// Null when no pool holds a candidate — the goal is then refused (<see cref="Refusal"/>). A goal every template of
    /// which was dropped is refused as it stands unless a parked neighbour dropped one or one missed a pass-through hint,
    /// when the pools after the first are searched too.
    /// </summary>
    private TugCandidate? KeepOffTheMovementArea(ResolvedTugGoal goal, TugChoiceTally tally, bool offStand)
    {
        if ((tally.Best is null) && !tally.NeighbourDropped && !tally.HintDropped)
        {
            return null;
        }

        PrepareKeep(goal, offStand);
        if (ForcedPastANeighbour(goal, tally, offStand) is { } forced)
        {
            return forced;
        }

        var pools = new TugLazyPools(KeepPools(goal, tally, offStand));

        // A forced tow takes no swing band: every candidate is admitted, and IsClear passes them all.
        bool lane = IsLaneGoal(goal, offStand) && !_request.Forced;
        TugCandidate? kept = Keep(goal, offStand, pools, c => !lane || InSwingBand(goal, c));
        if ((kept is null) && lane)
        {
            Log.LogDebug("Tug {Subject}: no candidate keeps to the swing band; keeping the best of the rest", goal.Subject);
            kept = Keep(goal, offStand, pools, _ => true);
        }

        if (kept is not null)
        {
            LogStandEntries(goal, kept, pools);
        }

        return kept;
    }

    /// <summary>
    /// A forced tow's keep when the ranking's own choice breaks a parked neighbour's floor: every pool a plain tow searches
    /// past a parked neighbour (<see cref="KeepPools"/>, as though the neighbour had dropped a candidate, which it would
    /// have) is searched, and the candidate kept is the best of all of them by the forced ranking
    /// (<see cref="BestPastNeighbours"/>). Null — the keep then goes on as for any forced tow — when the tow is not
    /// forced, has no parked neighbour, has no choice, or its choice keeps the floor.
    /// </summary>
    private TugCandidate? ForcedPastANeighbour(ResolvedTugGoal goal, TugChoiceTally tally, bool offStand)
    {
        if (!RanksPastNeighbours || (tally.Best is not { } chosen) || NeighbourClearance(chosen).KeepsFloor)
        {
            return null;
        }

        tally.NeighbourDropped = true;
        var pools = new TugLazyPools(KeepPools(goal, tally, offStand));
        TugCandidate kept = BestPastNeighbours(goal, offStand, pools.Everything()) ?? chosen;

        Log.LogDebug(
            "Tug {Subject}: forced past a parked neighbour, kept {Template} ({Moves}), closest {ClosestFt:F1} ft",
            goal.Subject,
            kept.Template,
            kept.Describe(),
            NeighbourClearance(kept).ClosestFt
        );
        LogStandEntries(goal, kept, pools);
        return kept;
    }

    /// <summary>
    /// The candidate kept among those <paramref name="admitted"/>: the best clear one (<see cref="BestClearOf"/>), else the
    /// least fouling one (<see cref="LeastFouling"/>); null when none is admitted.
    /// </summary>
    private TugCandidate? Keep(ResolvedTugGoal goal, bool offStand, TugLazyPools pools, Func<TugCandidate, bool> admitted)
    {
        if (BestClearOf(goal, offStand, pools, admitted) is { } clear)
        {
            return clear;
        }

        List<TugCandidate> candidates = [.. pools.Everything().Where(admitted)];
        return candidates.Count == 0 ? null : LeastFouling(goal, offStand, candidates);
    }

    /// <summary>
    /// The pools <see cref="KeepOffTheMovementArea"/> searches, in order: the ranking's own choice; every survivor and,
    /// for a lane goal, the straight-then-line candidates with the straight cut short
    /// (<see cref="ShorterStraightThenLineCandidates"/>); for a lane goal, the stepped candidates
    /// (<see cref="SteppedCandidates"/>); when a parked neighbour dropped a candidate, the other push-offs
    /// (<see cref="OtherPushOffCandidates"/>) with, for a lane goal, the extended straights
    /// (<see cref="ExtendedStraightCandidates"/>); and last, for such a lane goal only when no candidate before it keeps
    /// the neighbour floor without overswinging, the multi-point paths (<see cref="MultiPointCandidates"/>). Every pool but
    /// the first holds only candidates that survive judging.
    /// </summary>
    private IEnumerable<List<TugCandidate>> KeepPools(ResolvedTugGoal goal, TugChoiceTally tally, bool offStand)
    {
        var built = new List<TugCandidate>();
        if (tally.Best is { } chosen)
        {
            built.Add(chosen);
            yield return [chosen];
        }

        bool lane = IsLaneGoal(goal, offStand);
        List<TugCandidate> survivors =
        [
            .. tally.Survivors,
            .. lane ? SurvivorsOf(goal, offStand, ShorterStraightThenLineCandidates(goal, _standPushOff), tally) : [],
        ];
        built.AddRange(survivors);
        yield return survivors;
        if (lane)
        {
            List<TugCandidate> stepped = SurvivorsOf(goal, offStand, SteppedCandidates(goal, _standPushOff), tally);
            built.AddRange(stepped);
            yield return stepped;
        }

        if (!SearchesOtherPushOffs(goal, tally, offStand))
        {
            yield break;
        }

        foreach (List<TugCandidate> pool in NeighbourPools(goal, tally, built))
        {
            yield return pool;
        }
    }

    /// <summary>
    /// The pools searched past a parked neighbour, after <paramref name="built"/>: the other push-offs with, for a lane
    /// goal, the extended straights; then, for a lane goal none of whose candidates so far keeps the neighbour floor
    /// without overswinging, the multi-point paths.
    /// </summary>
    private IEnumerable<List<TugCandidate>> NeighbourPools(ResolvedTugGoal goal, TugChoiceTally tally, List<TugCandidate> built)
    {
        bool lane = IsLaneGoal(goal, offStand: true) && StandPushOffClearsTheNeighbours(goal);
        List<TugCandidate> pushOffs =
        [
            .. SurvivorsOf(goal, true, OtherPushOffCandidates(goal, true), tally),
            .. lane ? ExtendedStraightCandidates(goal, tally) : [],
        ];
        built.AddRange(pushOffs);
        yield return pushOffs;
        if (lane && !built.Any(c => TurnsTheLanesWay(goal, c, offStand: true)))
        {
            Log.LogDebug("Tug {Subject}: nothing keeps the neighbour floor without overswinging; trying the multi-point paths", goal.Subject);
            yield return SurvivorsOf(goal, true, MultiPointCandidates(goal), tally);
        }
    }

    /// <summary>
    /// The best <paramref name="admitted"/> candidate, pool by pool, that is clear of every protected taxiway's object-free
    /// area, ranked within a pool by <see cref="IsBetterClear"/>. Null when no pool has one.
    /// </summary>
    private TugCandidate? BestClearOf(ResolvedTugGoal goal, bool offStand, TugLazyPools pools, Func<TugCandidate, bool> admitted)
    {
        foreach (List<TugCandidate> pool in pools.All())
        {
            IEnumerable<TugCandidate> clear = pool.Where(c => admitted(c) && IsClear(c));
            if (BestClear(goal, offStand, clear) is { } best)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>
    /// Sets up the goal's keep: the protected taxiways (<see cref="_excludedTaxiways"/> are the others) and the empty stands
    /// every candidate enters anyway, which no candidate is ranked down for (<see cref="_excludedStands"/>): those the
    /// outline already reaches into where the goal starts, the stand the goal itself ends on, and — off a stand — those
    /// the stand push-off every template starts with enters.
    /// </summary>
    private void PrepareKeep(ResolvedTugGoal goal, bool offStand)
    {
        _clearance ??= new TugTaxiwayClearance(_layout!, _request.AircraftType);
        _excludedTaxiways = new HashSet<string>(goal.ExemptNames, StringComparer.OrdinalIgnoreCase);
        _excludedTaxiways.UnionWith(_clearance.TaxiwaysFouledAt(_end));
        _emptyStands ??= new TugEmptyStands(_layout!, _request);
        var stands = new HashSet<string>(_emptyStands.EnteredAt(_end, _lastKind ?? PushbackLegKind.Push), StringComparer.OrdinalIgnoreCase);
        if (goal.Goal.Node is { Type: GroundNodeType.Parking, Name: { } goalStand })
        {
            stands.Add(goalStand);
        }

        _pushOffStands = offStand ? _emptyStands.Entered(NewCandidate("probe", _standPushOff).Traces, stands) : new HashSet<string>();
        stands.UnionWith(_pushOffStands);
        _excludedStands = stands;
        Log.LogDebug(
            "Tug {Subject}: kept clear of every movement-area taxiway's object-free area but {Excluded}; "
                + "empty stands every candidate enters: {Stands}",
            goal.Subject,
            _excludedTaxiways.Count == 0 ? "none" : string.Join("/", _excludedTaxiways.Order(StringComparer.OrdinalIgnoreCase)),
            stands.Count == 0 ? "none" : string.Join("/", stands.Order(StringComparer.OrdinalIgnoreCase))
        );
    }

    /// <summary>
    /// The candidate's flown outline stays outside every protected taxiway's object-free area; measured once. Every
    /// candidate of a forced tow counts as clear, so the pools' own ranking picks it (<see cref="TugRequest.Forced"/>).
    /// </summary>
    private bool IsClear(TugCandidate candidate)
    {
        if (_request.Forced)
        {
            return true;
        }

        if (_foulingByCandidate.TryGetValue(candidate, out TugTaxiwayFouling fouling))
        {
            return !fouling.Fouls;
        }

        if (!_clearByCandidate.TryGetValue(candidate, out bool clear))
        {
            clear = !_clearance!.Fouls(candidate.Traces, _excludedTaxiways);
            _clearByCandidate[candidate] = clear;
        }

        return clear;
    }

    /// <summary>How far the candidate's flown outline reaches into a protected taxiway's object-free area; measured once.</summary>
    private TugTaxiwayFouling Fouling(TugCandidate candidate)
    {
        if (!_foulingByCandidate.TryGetValue(candidate, out TugTaxiwayFouling fouling))
        {
            fouling = _clearance!.Measure(candidate.Traces, _excludedTaxiways);
            _foulingByCandidate[candidate] = fouling;
        }

        return fouling;
    }

    /// <summary>The empty stands, but those every candidate enters, whose footprint the candidate's outline enters; measured once.</summary>
    private IReadOnlySet<string> EmptyStandsEntered(TugCandidate candidate)
    {
        if (!_standsByCandidate.TryGetValue(candidate, out IReadOnlySet<string>? stands))
        {
            stands = _emptyStands!.Entered(candidate.Traces, _excludedStands);
            _standsByCandidate[candidate] = stands;
        }

        return stands;
    }

    private bool StaysOutOfEmptyStands(TugCandidate candidate) => EmptyStandsEntered(candidate).Count == 0;

    /// <summary>
    /// Logs, for the candidate kept, each empty stand whose footprint its outline enters and why no candidate staying out
    /// of it won: the stand push-off every template starts with enters it, or — among the candidates built — how many
    /// stay out of it.
    /// </summary>
    private void LogStandEntries(ResolvedTugGoal goal, TugCandidate kept, TugLazyPools pools)
    {
        if (!Log.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        IReadOnlySet<string> none = new HashSet<string>();
        foreach (string stand in _emptyStands!.Entered(kept.Traces, none).Order(StringComparer.OrdinalIgnoreCase))
        {
            string reason = _pushOffStands.Contains(stand) ? "the stand push-off every candidate starts with enters it" : StandLossReason(pools);
            Log.LogDebug(
                "Tug {Subject}: {Template} enters the footprint of empty stand {Stand}: {Reason}",
                goal.Subject,
                kept.Template,
                stand,
                reason
            );
        }
    }

    /// <summary>
    /// Why no candidate staying out of the empty stands was kept, counted over the candidates the keep built; the pools it
    /// never reached are not built for the count.
    /// </summary>
    private string StandLossReason(TugLazyPools pools)
    {
        List<TugCandidate> built = [.. pools.Built().SelectMany(pool => pool).Distinct()];
        int staysOut = built.Count(StaysOutOfEmptyStands);
        return staysOut == 0
            ? $"none of the {built.Count} candidates built stays out of it"
            : $"{staysOut} of the {built.Count} candidates built stay out of it and rank below it";
    }

    /// <summary>The best of the candidates that stay clear, ranked by <see cref="IsBetterClear"/>; null when there are none.</summary>
    private TugCandidate? BestClear(ResolvedTugGoal goal, bool offStand, IEnumerable<TugCandidate> clear)
    {
        TugCandidate? bestClear = null;
        foreach (TugCandidate candidate in clear)
        {
            bestClear = IsBetterClear(goal, offStand, candidate, bestClear) ? candidate : bestClear;
        }

        if (bestClear is not null)
        {
            Log.LogDebug("Tug {Subject}: {Template} ({Moves}) stays clear instead", goal.Subject, bestClear.Template, bestClear.Describe());
        }

        return bestClear;
    }

    /// <summary>
    /// <see cref="Choose"/>'s ranking (<see cref="IsBetter"/>) with one key between the reversal count and the rest: a
    /// candidate staying out of the empty stands' footprints (<see cref="StaysOutOfEmptyStands"/>) beats one entering them.
    /// </summary>
    private bool IsBetterClear(ResolvedTugGoal goal, bool offStand, TugCandidate candidate, TugCandidate? best)
    {
        if ((best is null) || (candidate.Reversals != best.Reversals))
        {
            return IsBetter(goal, offStand, candidate, best);
        }

        bool staysOut = StaysOutOfEmptyStands(candidate);
        return staysOut != StaysOutOfEmptyStands(best) ? staysOut : IsBetter(goal, offStand, candidate, best);
    }

    /// <summary>
    /// The fouling candidate a goal keeps when none stays clear: on a lane goal, only the candidates that turn the nose
    /// the lane's way without overswinging (<see cref="WithoutOverswing"/>); of those, the ones reaching in no more than
    /// <see cref="TugTaxiwayClearance.ClearanceMarginFt"/> deeper than the least, the ones staying out of the empty
    /// stands when there are any, and of those <see cref="LeastFoulingOf"/> — with a <see cref="TugFoulsTaxiwayWarning"/>
    /// on the plan.
    /// </summary>
    private TugCandidate LeastFouling(ResolvedTugGoal goal, bool offStand, List<TugCandidate> candidates)
    {
        List<TugFoulingCandidate> pool = WithoutOverswing(
            goal,
            offStand,
            [.. candidates.Select(c => new TugFoulingCandidate(c, Fouling(c), NoseSwing(c, offStand)))]
        );
        double bandFt = pool.Min(p => p.Fouling.PeakFt) + TugTaxiwayClearance.ClearanceMarginFt;
        List<TugFoulingCandidate> inBand = [.. pool.Where(p => p.Fouling.PeakFt <= bandFt)];
        List<TugFoulingCandidate> staysOut = [.. inBand.Where(p => StaysOutOfEmptyStands(p.Candidate))];
        TugFoulingCandidate least = LeastFoulingOf(staysOut.Count > 0 ? staysOut : inBand);
        TugTaxiwayFouling leastFouling = least.Fouling;
        Log.LogDebug(
            "Tug {Subject}: none stays clear; {Template} ({Moves}) reaches {PeakFt:F1} ft into {Taxiway}'s with the {Part}, swings {SwingDeg:F1}°",
            goal.Subject,
            least.Candidate.Template,
            least.Candidate.Describe(),
            leastFouling.PeakFt,
            leastFouling.Taxiway,
            leastFouling.Part,
            least.Swing.MaxDeg
        );
        _warnings.Add(new TugFoulsTaxiwayWarning(leastFouling.Taxiway!, leastFouling.Part, leastFouling.PeakFt));
        return least.Candidate;
    }

    /// <summary>
    /// The pool's candidate to keep when all of them foul: of those reaching in no more than
    /// <see cref="TugTaxiwayClearance.ClearanceMarginFt"/> deeper than the least (planned and flown clearance differ by up
    /// to that much, so a shallower plan inside the band is no safer), those swinging the nose no more than
    /// <see cref="SwingTieDeg"/> past the least swing among them; of those, the one with the least exposure, then with the
    /// shortest path.
    /// </summary>
    internal static TugFoulingCandidate LeastFoulingOf(IReadOnlyList<TugFoulingCandidate> pool)
    {
        double bandFt = pool.Min(p => p.Fouling.PeakFt) + TugTaxiwayClearance.ClearanceMarginFt;
        List<TugFoulingCandidate> inBand = [.. pool.Where(p => p.Fouling.PeakFt <= bandFt)];
        double swingTieDeg = inBand.Min(p => p.Swing.MaxDeg) + SwingTieDeg;
        return inBand.Where(p => p.Swing.MaxDeg <= swingTieDeg).OrderBy(p => p.Fouling.ExposureFtFt).ThenBy(p => p.Candidate.PathLengthFt).First();
    }

    /// <summary>
    /// A lane goal's fouling candidates without those that overswing — whose nose turns further from the push-off's
    /// heading than the lane's own rotation from it plus <see cref="OverswingMarginDeg"/> — or that turn the nose more
    /// than <see cref="WrongWayToleranceDeg"/> against the lane's turn (a wrong-way first turn, an S-bend). The whole pool
    /// when none remains, and for any other goal.
    /// </summary>
    private static List<TugFoulingCandidate> WithoutOverswing(ResolvedTugGoal goal, bool offStand, List<TugFoulingCandidate> pool)
    {
        if (!IsLaneGoal(goal, offStand))
        {
            return pool;
        }

        List<TugFoulingCandidate> kept = [.. pool.Where(p => TurnsTheLanesWay(goal, p))];
        Log.LogDebug(
            "Tug {Subject}: {Dropped} of {Count} fouling candidate(s) overswing the lane or turn against it",
            goal.Subject,
            pool.Count - kept.Count,
            pool.Count
        );
        return kept.Count > 0 ? kept : pool;
    }

    /// <summary>Whether a lane goal's candidate turns the nose onto the lane without overswinging it or turning against it.</summary>
    private static bool TurnsTheLanesWay(ResolvedTugGoal goal, TugFoulingCandidate fouling)
    {
        double laneTurnDeg = new TrueHeading(fouling.Candidate.Traces[0].End.NoseTrueDeg).SignedAngleTo(new TrueHeading(goal.FacingTrueDeg));
        return (fouling.Swing.MaxDeg <= Math.Abs(laneTurnDeg) + OverswingMarginDeg)
            && (fouling.Swing.AgainstDeg(laneTurnDeg) <= WrongWayToleranceDeg);
    }

    /// <summary>
    /// <see cref="TurnsTheLanesWay(ResolvedTugGoal, TugFoulingCandidate)"/> for a candidate not yet measured for fouling:
    /// the bound on the extended straights and the multi-point paths.
    /// </summary>
    private bool TurnsTheLanesWay(ResolvedTugGoal goal, TugCandidate candidate, bool offStand) =>
        TurnsTheLanesWay(goal, new TugFoulingCandidate(candidate, default, NoseSwing(candidate, offStand)));

    /// <summary>
    /// Whether a lane goal's candidate keeps the nose within the swing band
    /// (<see cref="InSwingBand(TugNoseSwing, double, bool)"/>) of its target turn — the signed shortest rotation from the
    /// goal's starting nose heading to its final facing (the requested <c>FACE</c>/<c>TAIL</c> heading, else the lane's
    /// nose-out heading) — measured over every move from the goal's start; measured once.
    /// </summary>
    private bool InSwingBand(ResolvedTugGoal goal, TugCandidate candidate)
    {
        if (_inSwingBandByCandidate.TryGetValue(candidate, out bool inBand))
        {
            return inBand;
        }

        double targetDeg = new TrueHeading(_end.NoseTrueDeg).SignedAngleTo(new TrueHeading(goal.FacingTrueDeg));
        var swing = TugNoseSwing.From(_end.NoseTrueDeg, candidate.Traces.SelectMany(t => t.Samples).Select(s => s.NoseTrueDeg));
        bool explicitFacing = (_request.FinalFacingTrueDeg is not null) && ReferenceEquals(goal.Goal, _request.Goals[^1]);
        inBand = InSwingBand(swing, targetDeg, explicitFacing);
        _inSwingBandByCandidate[candidate] = inBand;
        if (!inBand)
        {
            Log.LogDebug(
                "Tug {Subject}: {Template} ({Moves}) swings the nose right {RightDeg:F1}° left {LeftDeg:F1}° for a target turn of {TargetDeg:F1}°; "
                    + "outside the swing band",
                goal.Subject,
                candidate.Template,
                candidate.Describe(),
                swing.RightDeg,
                swing.LeftDeg,
                targetDeg
            );
        }

        return inBand;
    }

    /// <summary>
    /// Whether a nose swing stays within the band of a target turn of <paramref name="targetDeg"/> (right positive): the
    /// running rotation within [min(0, target) − <see cref="SwingBandMarginDeg"/>, max(0, target) +
    /// <see cref="SwingBandMarginDeg"/>]. For a target onto an <paramref name="explicitFacing"/> (a requested
    /// <c>FACE</c>/<c>TAIL</c>) past <see cref="SwingBandEitherWayDeg"/>, the target the other way round (target ∓ 360°)
    /// counts too; a lane's own nose-out heading is always reached turning the lane's way.
    /// </summary>
    internal static bool InSwingBand(TugNoseSwing swing, double targetDeg, bool explicitFacing)
    {
        return Within(targetDeg)
            || (explicitFacing && (Math.Abs(targetDeg) > SwingBandEitherWayDeg) && Within(targetDeg - (Math.Sign(targetDeg) * 360.0)));

        bool Within(double target) =>
            (swing.RightDeg <= Math.Max(0.0, target) + SwingBandMarginDeg) && (swing.LeftDeg <= SwingBandMarginDeg - Math.Min(0.0, target));
    }

    /// <summary>
    /// How far a candidate's nose turns each way from its heading at the end of the stand push-off (off a stand) or at
    /// the start of the goal, over every move after that (<see cref="TugNoseSwing"/>).
    /// </summary>
    private TugNoseSwing NoseSwing(TugCandidate candidate, bool offStand)
    {
        double reference = offStand ? candidate.Traces[0].End.NoseTrueDeg : _end.NoseTrueDeg;
        return TugNoseSwing.From(reference, candidate.Traces.Skip(offStand ? 1 : 0).SelectMany(t => t.Samples).Select(s => s.NoseTrueDeg));
    }

    /// <summary>The refusal for a goal no candidate is kept for: the most severe flown-path reason.</summary>
    private static string Refusal(ResolvedTugGoal goal, TugChoiceTally tally) => tally.PathRefusal?.Message ?? NoPlanRefusal(goal);

    /// <summary>
    /// Whether a goal searches push-offs other than the straight half-fuselage one: a faced goal off a stand, when a parked
    /// neighbour dropped one of its candidates, planned fresh rather than as a re-plan of a tow under way — a re-plan (a
    /// mid-push <c>FACE</c> amendment) keeps the push-off that is already running, so it may not swap it.
    /// </summary>
    private bool SearchesOtherPushOffs(ResolvedTugGoal goal, TugChoiceTally tally, bool offStand) =>
        offStand && tally.NeighbourDropped && (goal.Shape == TugGoalShape.Faced) && (_request.PreviousKind is null);

    /// <summary>
    /// The candidates off each of <see cref="OtherPushOffs"/>: the faced-goal templates (<see cref="FacedCandidates"/>)
    /// and, for a lane goal, the straight-then-line ones from where that push-off ends.
    /// </summary>
    private IEnumerable<TugCandidate> OtherPushOffCandidates(ResolvedTugGoal goal, bool offStand)
    {
        foreach (TugMove pushOff in OtherPushOffs())
        {
            foreach (TugCandidate candidate in FacedCandidates(goal, pushOff))
            {
                yield return candidate;
            }

            if (IsLaneGoal(goal, offStand))
            {
                foreach (TugCandidate candidate in StraightThenLineCandidates(goal, pushOff))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>
    /// The push-offs tried past a parked neighbour the straight one swings too close to: a longer straight push, for each
    /// neighbour, until the nose has passed the neighbour's outline by <see cref="GroundOutlineSweep.WingtipBufferFt"/>
    /// (<see cref="PushOffsPastNeighboursFt"/>); and a push turning the tail away from each side a neighbour stands on,
    /// through each of <see cref="AngledPushOffsDeg"/>.
    /// </summary>
    private IEnumerable<TugMove> OtherPushOffs()
    {
        double standardFt = AircraftLength.ResolveFt(_request.AircraftType) / 2.0;
        foreach (double straightFt in PushOffsPastNeighboursFt().Where(ft => ft > standardFt + TugMovePlanner.StepFt).Distinct().Order())
        {
            yield return TugMove.Straight(PushbackLegKind.Push, straightFt);
        }

        foreach (double sign in AwayFromNeighbourSides())
        {
            foreach (double angleDeg in AngledPushOffsDeg)
            {
                yield return TugMove.TurnTo(PushbackLegKind.Push, new TrueHeading(_end.NoseTrueDeg + (sign * angleDeg)).Degrees);
            }
        }
    }

    /// <summary>
    /// For each parked neighbour, how far a straight push must run for the nose tip to pass the far end of the
    /// neighbour's outline along the push by <see cref="GroundOutlineSweep.WingtipBufferFt"/>, feet, rounded up; those
    /// within <see cref="TugMovePlanner.MaxGoalDistanceFt"/>.
    /// </summary>
    private IEnumerable<double> PushOffsPastNeighboursFt()
    {
        var frame = new GroundOutlineFrame(_end.Position);
        double travelRad = _end.TravelTrueDeg(PushbackLegKind.Push) * Math.PI / 180.0;
        double AlongFt(OutlinePoint p) => (p.EastFt * Math.Sin(travelRad)) + (p.NorthFt * Math.Cos(travelRad));
        double halfLengthFt = AircraftLength.ResolveFt(_request.AircraftType) / 2.0;
        foreach (TugParkedNeighbour neighbour in _request.ParkedNeighbours)
        {
            var outline = GroundOutline.At(
                frame.ToLocal(neighbour.Position),
                neighbour.TrueHeadingDeg,
                GroundOutlineSize.Of(neighbour.AircraftType, towedNoseFirst: false)
            );
            double farFt = new[] { outline.Fuselage, outline.Wing, outline.Tailplane }.SelectMany(s => new[] { s.A, s.B }).Max(AlongFt);
            double straightFt = Math.Ceiling(farFt + halfLengthFt + GroundOutlineSweep.WingtipBufferFt);
            if (straightFt <= TugMovePlanner.MaxGoalDistanceFt)
            {
                yield return straightFt;
            }
        }
    }

    /// <summary>
    /// Which way the push travel turns to swing the tail away from each parked neighbour, as the nose's turn sign (right
    /// positive): right for a neighbour on the left of the push travel, left for one on its right; each side once.
    /// </summary>
    private IEnumerable<double> AwayFromNeighbourSides()
    {
        var travel = new TrueHeading(_end.TravelTrueDeg(PushbackLegKind.Push));
        return _request
            .ParkedNeighbours.Select(n => travel.SignedAngleTo(new TrueHeading(GeoMath.BearingTo(_end.Position, n.Position))))
            .Where(relativeDeg => Math.Abs(relativeDeg) is > 0.0 and < 180.0)
            .Select(relativeDeg => relativeDeg < 0.0 ? 1.0 : -1.0)
            .Distinct()
            .Order();
    }

    /// <summary>
    /// Each candidate that survives judging, or its room-before-the-reversal retry when that survives instead; the drops are
    /// counted on <paramref name="tally"/>, whose choice <see cref="Choose"/> has closed, so they open the search past a
    /// parked neighbour but never change the refusal.
    /// </summary>
    private List<TugCandidate> SurvivorsOf(ResolvedTugGoal goal, bool offStand, IEnumerable<TugCandidate> candidates, TugChoiceTally tally) =>
        [.. candidates.Select(c => Survivor(goal, c, offStand, tally)).OfType<TugCandidate>()];

    /// <summary>The candidate when it survives judging, else its room-before-the-reversal retry when that survives, else null.</summary>
    private TugCandidate? Survivor(ResolvedTugGoal goal, TugCandidate candidate, bool offStand, TugChoiceTally tally) =>
        Survivor(goal, candidate, Judge(goal, candidate, offStand), offStand, tally);

    /// <summary>
    /// The candidate judged <paramref name="verdict"/> when it survives, else its room-before-the-reversal retry when that
    /// survives, else null.
    /// </summary>
    private TugCandidate? Survivor(ResolvedTugGoal goal, TugCandidate candidate, TugVerdict verdict, bool offStand, TugChoiceTally tally)
    {
        if (!verdict.Dropped)
        {
            return candidate;
        }

        tally.CountDrop(verdict);
        if (RoomBeforeReversal(candidate, verdict) is not { } retry)
        {
            return null;
        }

        TugVerdict retried = Judge(goal, retry, offStand);
        tally.CountDrop(retried);
        return retried.Dropped ? null : retry;
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

    /// <summary>
    /// A goal refused before any candidate is built: further than <see cref="TugMovePlanner.MaxGoalDistanceFt"/> from
    /// <paramref name="from"/> (where the aircraft is, or the pass-through hint before it), on a runway holding position,
    /// or a marked point on pavement a tow may not end on.
    /// </summary>
    private bool IsRefusedOutright(ResolvedTugGoal goal, LatLon from, out string refusal)
    {
        double distanceFt = goal.Goal.Point is { } point ? GeoMath.DistanceNm(from, point) * GeoMath.FeetPerNm : 0.0;
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
            refusal = goal.IsHint
                ? $"Unable, {goal.Name} is on a runway holding position"
                : $"Unable, {goal.Subject} reaches a runway holding position";
            return true;
        }

        // A marked point has no graph node to classify, so where it lies is checked directly: never on a runway, a
        // runway holding position or a movement-area taxiway.
        if (
            (goal.Goal.Kind == TugGoalKind.FreePose)
            && (goal.Goal.Point is { } marked)
            && (_pathCheck?.MarkedPointRefusal(marked, goal.Name) is { } onPavement)
        )
        {
            refusal = onPavement;
            return true;
        }

        refusal = string.Empty;
        return false;
    }

    /// <summary>A forced leg kind no plan can fly: <c>Unable, spot 7A cannot be reached by a pull</c>.</summary>
    private static string CannotBeReachedBy(ResolvedTugGoal goal, PushbackLegKind kind) =>
        $"Unable, {goal.Name} cannot be reached by a {(kind == PushbackLegKind.Push ? "push" : "pull")}";

    /// <summary>
    /// Why a goal with no surviving candidate and no flown-path reason is refused: a forced leg kind no plan of that kind
    /// can fly; a marked point's facing no plan can end on; else the goal's line cannot be captured from here.
    /// </summary>
    private static string NoPlanRefusal(ResolvedTugGoal goal) =>
        goal.Goal switch
        {
            { ForcedKind: { } forced } => CannotBeReachedBy(goal, forced),
            { Kind: TugGoalKind.FreePose, FacingWord: { } facing } => $"Unable, {goal.Name} cannot be reached facing {facing}",
            _ => $"Unable, cannot line up on {goal.Name} from here",
        };

    private bool TryBuildCandidates(ResolvedTugGoal goal, bool offStand, out List<TugCandidate> candidates, out string refusal)
    {
        TugMove? standPushOff = offStand ? TugMove.Straight(PushbackLegKind.Push, AircraftLength.ResolveFt(_request.AircraftType) / 2.0) : null;
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
        PushbackLegKind[] sides = goal.Goal.ForcedKind is { } forced ? [forced] : SidesFor(stopOffFacingDeg);
        Log.LogDebug(
            "Tug {Subject}: stop {StopOffFacingDeg:F2}° off the facing; building candidates for the {Sides} side",
            goal.Subject,
            stopOffFacingDeg,
            string.Join(" and ", sides)
        );

        var candidates = sides.Select(side => Direct(goal, pushOff, side)).ToList();
        if (!OtherKindFirstCanKeepToForcedKind(goal, pushOff))
        {
            return candidates;
        }

        candidates.AddRange(sides.Select(side => OtherKindFirst(goal, pushOff, side)));
        if (TugMovePlanner.AbsDiffDeg(from.NoseTrueDeg, facing) > ThreePointTurnMinRotationDeg)
        {
            candidates.AddRange(sides.SelectMany(side => ThreePointTurns(goal, pushOff, side, from.NoseTrueDeg)));
        }

        return candidates;
    }

    /// <summary>
    /// Whether a T2 or T3 candidate — which opens with the kind opposite its side — could keep to the goal's forced leg
    /// kind: always when none is forced; never on <c>/PUSH</c>, whose side is the push, so it opens with a pull; on
    /// <c>/PULL</c> only off a stand, where the push it opens with joins the stand push-off run.
    /// </summary>
    private static bool OtherKindFirstCanKeepToForcedKind(ResolvedTugGoal goal, TugMove? pushOff) =>
        goal.Goal.ForcedKind switch
        {
            null => true,
            PushbackLegKind.Pull => pushOff is not null,
            _ => false,
        };

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
    /// step — so the push pivots onto the lane earlier and further from the taxiway behind it. Built whenever the keep
    /// reaches its pool (<see cref="KeepPools"/>).
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
        foreach (PushbackLegKind side in LaneSides(goal))
        {
            TugCandidate candidate = NewCandidate($"T0 straight {straightFt:F0} ft then line, {side} side", pushOff);
            candidate.Add(TugMove.Straight(PushbackLegKind.Push, straightFt));
            AddLaneCapture(candidate, goal, side);
            yield return candidate;
        }
    }

    /// <summary>
    /// The sides a lane goal's straight-then-line and stepped candidates approach from: the forced kind's alone, else push
    /// then pull.
    /// </summary>
    private static PushbackLegKind[] LaneSides(ResolvedTugGoal goal) =>
        goal.Goal.ForcedKind is { } forced ? [forced] : [PushbackLegKind.Push, PushbackLegKind.Pull];

    /// <summary>
    /// The stepped fouling fallback for a spot goal off a stand: after the half-fuselage push-off, a straight push of each
    /// of <see cref="SteppedStraightsFt"/> (none for 0 ft), a push turn to the push-off's nose plus each of
    /// <see cref="SteppedTurnFractions"/> of the pivot onto the lane's facing, then the lane capture and the approach — the
    /// push side on to the staging point, the pull side capturing the lane floating and pulling along it onto the stop.
    /// A turn of <see cref="MinThreePointTurnDeg"/> or less is no turn, and a straight longer than the straight-then-line
    /// straight (<see cref="StraightThenLineStraightFt"/>; none but 0 ft when there is none) would carry the push past
    /// where it pivots onto the lane, so neither step is built. Built whenever the keep reaches its pool
    /// (<see cref="KeepPools"/>).
    /// </summary>
    private IEnumerable<TugCandidate> SteppedCandidates(ResolvedTugGoal goal, TugMove pushOff)
    {
        double startNoseDeg = NewCandidate("probe", pushOff).End.NoseTrueDeg;
        double pivotDeg = new TrueHeading(startNoseDeg).SignedAngleTo(new TrueHeading(goal.FacingTrueDeg));
        double maxStraightFt = StraightThenLineStraightFt(goal, pushOff) ?? 0.0;
        List<(double StraightFt, double TurnToDeg)> steps =
        [
            .. from straightFt in SteppedStraightsFt
            where straightFt <= maxStraightFt
            from fraction in SteppedTurnFractions
            where Math.Abs(fraction * pivotDeg) > MinThreePointTurnDeg
            select (straightFt, new TrueHeading(startNoseDeg + (fraction * pivotDeg)).Degrees),
        ];
        return steps.SelectMany(step => LaneSides(goal).Select(side => SteppedCandidate(goal, pushOff, step, side)));
    }

    /// <summary>
    /// One stepped candidate: the push-off, the step's straight push and push turn, then the lane capture and approach on
    /// <paramref name="side"/>.
    /// </summary>
    private TugCandidate SteppedCandidate(ResolvedTugGoal goal, TugMove pushOff, (double StraightFt, double TurnToDeg) step, PushbackLegKind side)
    {
        TugCandidate candidate = NewCandidate($"T4 stepped {step.StraightFt:F0} ft, turn to {step.TurnToDeg:F0}°, then line, {side} side", pushOff);
        if (step.StraightFt > 0.0)
        {
            candidate.Add(TugMove.Straight(PushbackLegKind.Push, step.StraightFt));
        }

        candidate.Add(TugMove.TurnTo(PushbackLegKind.Push, step.TurnToDeg));
        AddLaneCapture(candidate, goal, side);
        return candidate;
    }

    /// <summary>
    /// The lane capture and approach every straight-then-line shape ends with: the push side pushes onto the lane to the
    /// staging point and creeps forward onto the stop; the pull side captures the lane floating and pulls along it onto
    /// the stop.
    /// </summary>
    private static void AddLaneCapture(TugCandidate candidate, ResolvedTugGoal goal, PushbackLegKind side)
    {
        if (side == PushbackLegKind.Pull)
        {
            candidate.Add(LineMove(goal, PushbackLegKind.Push, stopAt: null));
        }

        AddApproach(candidate, goal, side);
    }

    /// <summary>
    /// The extended straights for a lane goal off a stand a parked neighbour dropped a candidate of: the straight-then-line
    /// shape (<see cref="StraightThenLine"/>) with its straight — none when the lane is too close or turns too far for
    /// one — lengthened by every <see cref="ExtendedStraightStepFt"/> up to <see cref="MaxExtendedStraightFt"/>, so the
    /// turn onto the lane starts further from the neighbour; the turn is always the lane's own. Each side stops lengthening
    /// at the first straight that overswings or fails the flown-path check, which a longer one only makes worse; the
    /// survivors are returned.
    /// </summary>
    private List<TugCandidate> ExtendedStraightCandidates(ResolvedTugGoal goal, TugChoiceTally tally)
    {
        double baseFt = StraightThenLineStraightFt(goal, _standPushOff) ?? 0.0;
        return [.. LaneSides(goal).SelectMany(side => ExtendedStraightsOnSide(goal, _standPushOff, baseFt, side, tally))];
    }

    /// <summary>One side's extended straights, longest last, up to the first that overswings or fails the flown-path check.</summary>
    private IEnumerable<TugCandidate> ExtendedStraightsOnSide(
        ResolvedTugGoal goal,
        TugMove pushOff,
        double baseFt,
        PushbackLegKind side,
        TugChoiceTally tally
    )
    {
        for (double extraFt = ExtendedStraightStepFt; extraFt <= MaxExtendedStraightFt; extraFt += ExtendedStraightStepFt)
        {
            TugCandidate candidate = NewCandidate($"T0 straight {baseFt + extraFt:F0} ft (extended {extraFt:F0} ft) then line, {side} side", pushOff);
            candidate.WanderBoundedByOverswing = true;
            candidate.Add(TugMove.Straight(PushbackLegKind.Push, baseFt + extraFt));
            AddLaneCapture(candidate, goal, side);
            TugVerdict verdict = Judge(goal, candidate, offStand: true);
            if (EndsTheExtension(goal, candidate, verdict))
            {
                Log.LogDebug("Tug {Subject}: {Template} ends the extension: {Reason}", goal.Subject, candidate.Template, ExtensionEnd(verdict));
                yield break;
            }

            if (Survivor(goal, candidate, verdict, offStand: true, tally) is { } survivor)
            {
                yield return survivor;
            }
        }
    }

    /// <summary>
    /// Whether the half-fuselage stand push-off, flown on its own, keeps every parked neighbour's floor. The extended
    /// straights and the multi-point paths all start with it, so when it does not, none of them is built.
    /// </summary>
    private bool StandPushOffClearsTheNeighbours(ResolvedTugGoal goal)
    {
        if (NeighbourRefusal(goal, NewCandidate("the stand push-off alone", _standPushOff)) is not { } refusal)
        {
            return true;
        }

        Log.LogDebug(
            "Tug {Subject}: the stand push-off alone fails: {Refusal}; no extended straight or multi-point path",
            goal.Subject,
            refusal.Message
        );
        return false;
    }

    /// <summary>An extended straight that fails the flown-path check for anything but a parked neighbour, or that overswings.</summary>
    private bool EndsTheExtension(ResolvedTugGoal goal, TugCandidate candidate, TugVerdict verdict) =>
        (verdict.Path is { Severity: not TugPathSeverity.ParkedNeighbour })
        || (candidate.Flyable && !TurnsTheLanesWay(goal, candidate, offStand: true));

    private static string ExtensionEnd(TugVerdict verdict) =>
        verdict.Path is { Severity: not TugPathSeverity.ParkedNeighbour } hit ? hit.Message : "it swings the nose past the lane's turn";

    /// <summary>
    /// The multi-point paths for a lane goal off a stand, when nothing else keeps a parked neighbour's floor without
    /// overswinging: after the half-fuselage push-off, a straight push until the reference point is each of
    /// <see cref="MultiPointPastLaneFt"/> past the lane's centreline; a push turn the lane's way through each of
    /// <see cref="MultiPointPushTurnsDeg"/> short of the lane's own turn; a pull forward, straight
    /// (<see cref="MultiPointPullStraightsFt"/>) or turning on the lane's way (<see cref="MultiPointPullTurnsDeg"/>, no
    /// further than the lane's turn + <see cref="OverswingMarginDeg"/>); a push adjusting onto the lane; and the creep pull
    /// onto the mark, on either side as the straight-then-line shape ends. Three reversals, each with its dwell. None for a
    /// forced leg, which a path of both kinds cannot keep to, or when the lane is not behind the push-off.
    /// </summary>
    private List<TugCandidate> MultiPointCandidates(ResolvedTugGoal goal)
    {
        TugMove pushOff = _standPushOff;
        TugPose from = NewCandidate("probe", pushOff).End;
        if ((goal.Goal.ForcedKind is not null) || (LaneCrossingFt(goal, from) is not { } crossFt))
        {
            return [];
        }

        double noseDeg = from.NoseTrueDeg;
        double laneTurnDeg = new TrueHeading(noseDeg).SignedAngleTo(new TrueHeading(goal.FacingTrueDeg));
        double sign = laneTurnDeg >= 0.0 ? 1.0 : -1.0;
        double maxTurnDeg = Math.Abs(laneTurnDeg) + OverswingMarginDeg;
        List<(double StraightFt, double PushTurnDeg, TugMove Pull)> steps =
        [
            .. from pastFt in MultiPointPastLaneFt
            from pushTurnDeg in MultiPointPushTurnsDeg
            where pushTurnDeg < Math.Abs(laneTurnDeg)
            from pull in MultiPointPulls(noseDeg, sign, pushTurnDeg, maxTurnDeg)
            select (crossFt + pastFt, pushTurnDeg, pull),
        ];
        return
        [
            .. steps.SelectMany(step =>
                LaneSides(goal).Select(side => MultiPointCandidate(goal, pushOff, noseDeg + (sign * step.PushTurnDeg), step, side))
            ),
        ];
    }

    /// <summary>
    /// The multi-point paths for the request's first goal that survive judging and keep to the lane's turn — the ones the
    /// keep may choose from when it reaches them — in the order built, whatever else the goal's templates yield. Nothing
    /// when the goal is not a lane goal off a stand. The builder-level hook the multi-point shape is tested through.
    /// </summary>
    internal List<TugCandidate> AcceptableMultiPointPaths()
    {
        ResolvedTugGoal goal = WithStandBehindExempt(TugGoalResolver.Resolve(_layout, _request, 0));
        bool offStand = _request.StartsAtStand;
        if (!IsLaneGoal(goal, offStand))
        {
            return [];
        }

        var tally = new TugChoiceTally();
        tally.CloseChoice();
        return [.. SurvivorsOf(goal, offStand, MultiPointCandidates(goal), tally).Where(c => TurnsTheLanesWay(goal, c, offStand))];
    }

    /// <summary>
    /// A multi-point path's pulls after a push turn of <paramref name="pushTurnDeg"/> the lane's way (<paramref name="sign"/>,
    /// right positive): each straight, and each further turn that keeps the nose within <paramref name="maxTurnDeg"/> of
    /// <paramref name="startNoseDeg"/>.
    /// </summary>
    private static IEnumerable<TugMove> MultiPointPulls(double startNoseDeg, double sign, double pushTurnDeg, double maxTurnDeg) =>
        MultiPointPullStraightsFt
            .Select(ft => TugMove.Straight(PushbackLegKind.Pull, ft))
            .Concat(
                MultiPointPullTurnsDeg
                    .Where(turnDeg => pushTurnDeg + turnDeg <= maxTurnDeg)
                    .Select(turnDeg => TugMove.TurnTo(PushbackLegKind.Pull, new TrueHeading(startNoseDeg + (sign * (pushTurnDeg + turnDeg))).Degrees))
            );

    /// <summary>
    /// One multi-point path: the push-off, the straight past the lane, the push turn, the pull, then the lane capture on
    /// <paramref name="side"/>.
    /// </summary>
    private TugCandidate MultiPointCandidate(
        ResolvedTugGoal goal,
        TugMove pushOff,
        double pushTurnToDeg,
        (double StraightFt, double PushTurnDeg, TugMove Pull) step,
        PushbackLegKind side
    )
    {
        var pushTurnTo = new TrueHeading(pushTurnToDeg);
        string pull =
            step.Pull.Shape == TugMoveShape.TurnTo ? $"pull turn to {step.Pull.FacingTrueDeg:F0}°" : $"pull {step.Pull.StraightDistanceFt:F0} ft";
        TugCandidate candidate = NewCandidate(
            $"T5 multi-point: straight {step.StraightFt:F0} ft, push turn to {pushTurnTo.Degrees:F0}°, {pull}, then line, {side} side",
            pushOff
        );
        candidate.Add(TugMove.Straight(PushbackLegKind.Push, step.StraightFt));
        candidate.Add(TugMove.TurnTo(PushbackLegKind.Push, pushTurnTo.Degrees));
        candidate.Add(step.Pull);
        AddLaneCapture(candidate, goal, side);
        return candidate;
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
        double halfNm = AircraftLength.ResolveFt(_request.AircraftType) / 2.0 / GeoMath.FeetPerNm;
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

        double backFt = TugKinematics.TurnRadiusFt(_request.AircraftType, tight: false) + (AircraftLength.ResolveFt(_request.AircraftType) / 2.0);
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

    /// <summary>
    /// The node joining <paramref name="taxiway"/> and <paramref name="facingName"/> nearest the exit, or null when they
    /// never meet.
    /// </summary>
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

    /// <summary>
    /// A side the push can end on when the junction is near: the exit's own, or one whose stop is within
    /// <see cref="TugMovePlanner.MaxGoalDistanceFt"/>.
    /// </summary>
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
    /// stop, except on a leg forced to <c>/PUSH</c>, whose push ends on the stop itself. The final pull onto a spot is
    /// always a creep, except onto a pass-through hint (<see cref="ResolvedTugGoal.PassThrough"/>), which has no staging
    /// point either.
    /// </summary>
    private static void AddApproach(TugCandidate candidate, ResolvedTugGoal goal, PushbackLegKind side)
    {
        bool staged = (side == PushbackLegKind.Push) && (goal.Staging is not null) && (goal.Goal.ForcedKind != PushbackLegKind.Push);
        bool creepOntoSpot = (side == PushbackLegKind.Pull) && (goal.Goal.Kind == TugGoalKind.Spot) && !goal.PassThrough;
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
        if ((goal.Goal.ForcedKind is { } forced) && (forced != kind))
        {
            // A forced kind is a hard constraint: never substitute the kind the node's bearing calls for.
            candidates = [];
            refusal = CannotBeReachedBy(goal, forced);
            return false;
        }

        if ((pushOff is not null) && (kind == PushbackLegKind.Pull))
        {
            candidates = [];
            refusal = goal.Goal switch
            {
                { ForcedKind: { } forcedPull } => CannotBeReachedBy(goal, forcedPull),
                { Kind: TugGoalKind.FreePose } => CannotBeReachedBy(goal, PushbackLegKind.Push),
                _ => $"Unable, {goal.Name} is ahead of the nose — the aircraft has to be pushed back off the stand first",
            };
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
            straight.TaxiwayApproach = TugTaxiwayApproach.Across;
            candidates = [straight];
            return true;
        }

        if (AlongsideCandidate(goal, pushTravelDeg, standPushOff, IsCentreline) is { } alongside)
        {
            alongside.TaxiwayApproach = TugTaxiwayApproach.Alongside;
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

    /// <summary>
    /// A candidate starting with <paramref name="pushOff"/>, or with nothing when it is null. A push-off other than the
    /// standard half-fuselage straight (<see cref="OtherPushOffs"/>) is named in the template.
    /// </summary>
    private TugCandidate NewCandidate(string template, TugMove? pushOff)
    {
        string named =
            ((pushOff is { } other) && (other != _standPushOff)) ? $"{template}, off a {other.Shape} push-off {DescribePushOff(other)}" : template;
        var candidate = new TugCandidate(named, _request.AircraftType, _end, _lastKind);
        if (pushOff is not null)
        {
            candidate.Add(pushOff);
        }

        return candidate;
    }

    private static string DescribePushOff(TugMove pushOff) =>
        pushOff.Shape == TugMoveShape.TurnTo ? $"to {pushOff.FacingTrueDeg:F0}°" : $"{pushOff.StraightDistanceFt:F0} ft";

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
    /// reversal retry ranks straight after it), on the tally with every survivor and with the drops of the candidates
    /// whose shape was sound: the most severe flown-path reason among them, and whether a parked neighbour dropped one.
    /// The choice is closed on return (<see cref="TugChoiceTally.CloseChoice"/>), so the refusal (<see cref="Refusal"/>)
    /// is these templates' own; a candidate dropped for its shape was never a way to fly the move, so the pavement it
    /// would have crossed is not counted.
    /// </summary>
    private TugChoiceTally Choose(ResolvedTugGoal goal, List<TugCandidate> candidates, bool offStand)
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

        if (RanksPastNeighbours)
        {
            tally.Best = BestPastNeighbours(goal, offStand, tally.Survivors);
        }

        TugCandidate? best = tally.Best;
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

        tally.CloseChoice();
        return tally;
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
                DropReason(verdict)
            );
            tally.CountDrop(verdict);
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
        var retry = new TugCandidate($"{candidate.Template}, {roomFt:F1} ft room before the reversal", _request.AircraftType, _end, _lastKind)
        {
            WanderBoundedByOverswing = candidate.WanderBoundedByOverswing,
        };
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

        if (candidate.IsForcedFallback && best.IsForcedFallback)
        {
            return IsShorterTow(candidate, best);
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

    /// <summary>
    /// How forced fallback candidates rank against each other: the shorter tow, then the fewer reversals. The fallback
    /// serves pushes real ramps never fly, so a lane's lead-in and the rotation keys that pick among real push shapes do
    /// not apply; lengths within a foot count as equal.
    /// </summary>
    private static bool IsShorterTow(TugCandidate candidate, TugCandidate best) =>
        Math.Abs(candidate.PathLengthFt - best.PathLengthFt) > 1.0
            ? candidate.PathLengthFt < best.PathLengthFt
            : candidate.Reversals < best.Reversals;

    /// <summary>
    /// How a dropped candidate's reason is logged: the shape rule it broke, with any flown-path hit after it, then the
    /// pass-through hint it missed.
    /// </summary>
    private static string DropReason(TugVerdict verdict)
    {
        string? shapeAndPath = (verdict.ShapeDrop, verdict.Path) switch
        {
            (null, { } hit) => hit.Message,
            ({ } shape, { } hit) => $"{shape} (its flown path: {hit.Message})",
            _ => verdict.ShapeDrop,
        };
        return (shapeAndPath, verdict.HintMiss) switch
        {
            ({ } reason, { } hint) => $"{reason}; {hint}",
            (null, { } hint) => hint,
            ({ } reason, null) => reason,
            _ => throw new UnreachableException("a dropped candidate carries no reason"),
        };
    }

    /// <summary>
    /// Why a candidate is dropped: the shape rule it breaks (the travel budget, the stand push-off rule, the run
    /// wander, the end tolerance or the overshoot), the pass-through hint it misses (<see cref="HintMissReason"/>, a step
    /// apart from the refusals), and the flown-path rule it breaks. All null when it survives. The hint step comes before
    /// the flown path: a candidate that misses a hint was never a way to fly the move, so its path is not checked. Every
    /// other flyable candidate's path is, even one already dropped for its shape, so the log shows what it would have
    /// crossed; only a shape-sound candidate's path hit can become the refusal.
    /// </summary>
    private TugVerdict Judge(ResolvedTugGoal goal, TugCandidate candidate, bool offStand)
    {
        if (!candidate.Flyable)
        {
            return new TugVerdict("a move ran past its travel budget", null, null, null);
        }

        (string? shapeReason, double? finalPullOvershootFt) =
            goal.Shape == TugGoalShape.Faced ? FacedDropReason(goal, candidate, offStand) : (null, null);
        if (HintMissReason(candidate.Traces) is { } hintMiss)
        {
            return new TugVerdict(shapeReason, null, finalPullOvershootFt, hintMiss);
        }

        string? markedPoint = goal.Goal.Kind == TugGoalKind.FreePose ? goal.Name : null;
        TugPathRefusal? path =
            _pathCheck?.Check(candidate.Traces, goal.ExemptNames, goal.OvershootTaxiway, (goal.Subject, markedPoint), _request.Forced)
            ?? (_request.Forced ? null : NeighbourRefusal(goal, candidate));
        return new TugVerdict(shapeReason, path, finalPullOvershootFt, null);
    }

    /// <summary>
    /// The hint step: why a candidate misses the pass-through hints, or null when it passes them all. A hint is passed at
    /// the first sample, at or after the one that passed the hint before it, where the reference point lies within half
    /// the wingspan of the hint's point (a spot's rest point, else the node or marked point) — during a move of the hint's
    /// forced kind when it carries one (<c>$7A/PULL</c> is passed while pulling). Only the moves before the candidate's
    /// last reversal count (<see cref="LastReversalIndex"/>) — the moves from it on are the final arrival — or every move
    /// when it has none. The samples are walked once, in order, and never collected.
    /// </summary>
    /// <param name="traces">The candidate's moves.</param>
    /// <returns>The miss, naming the hint and how close the reference point came, or null.</returns>
    private string? HintMissReason(IReadOnlyList<TugMoveTrace> traces) =>
        (_pendingHints.Count == 0) ? null : HintMiss(_pendingHints, traces, TugMovePlanner.WingspanFt(_request.AircraftType) / 2.0);

    /// <summary>The hint step (<see cref="HintMissReason"/>) for the given hints, in order, and half-span.</summary>
    /// <param name="hints">The pass-through hints, in order.</param>
    /// <param name="traces">The candidate's moves.</param>
    /// <param name="halfSpanFt">How close the reference point must come to each hint's point, feet.</param>
    /// <returns>The miss, naming the hint and how close the reference point came, or null.</returns>
    internal static string? HintMiss(IReadOnlyList<ResolvedTugGoal> hints, IReadOnlyList<TugMoveTrace> traces, double halfSpanFt)
    {
        int next = 0;
        double closestFt = double.PositiveInfinity;
        foreach ((LatLon position, PushbackLegKind kind) in traces.Take(LastReversalIndex(traces)).SelectMany(PassingSamples))
        {
            while ((next < hints.Count) && (EligibleFeet(hints[next], position, kind) is { } feet))
            {
                closestFt = Math.Min(closestFt, feet);
                if (feet > halfSpanFt)
                {
                    break;
                }

                next++;
                closestFt = double.PositiveInfinity;
            }

            if (next == hints.Count)
            {
                return null;
            }
        }

        return (next == hints.Count) ? null : MissMessage(hints[next], closestFt, halfSpanFt);
    }

    /// <summary>A move's samples as the hint step walks them: where the reference point is, and the move's kind.</summary>
    private static IEnumerable<(LatLon Position, PushbackLegKind Kind)> PassingSamples(TugMoveTrace trace) =>
        trace.Samples.Select(s => (s.Position, trace.Move.Kind));

    /// <summary>
    /// The index of a candidate's last reversal, where its final arrival begins, or the move count when it has none. The
    /// first move is never counted: its reversal is of a move before the candidate (the running move a mid-push
    /// <c>PUSHM</c> redirects, or the last move of a pass-through tow).
    /// </summary>
    private static int LastReversalIndex(IReadOnlyList<TugMoveTrace> traces)
    {
        for (int i = traces.Count - 1; i >= 1; i--)
        {
            if (traces[i].Move.DwellBefore)
            {
                return i;
            }
        }

        return traces.Count;
    }

    /// <summary>
    /// How far a sample lies from a hint's point, feet, or null when the sample's move is not of the kind the hint is
    /// forced to.
    /// </summary>
    private static double? EligibleFeet(ResolvedTugGoal hint, LatLon position, PushbackLegKind kind) =>
        (hint.Goal.ForcedKind is { } forced) && (forced != kind) ? null : FeetBetween(position, hint.Stop);

    /// <summary>
    /// A hint miss as the drop log reads it: <c>it passes no closer than 145 ft to spot 5A before its final arrival (half
    /// the span: 59 ft)</c>, with <c>while pulling</c> / <c>while pushing</c> after the hint when it is forced to a kind.
    /// </summary>
    private static string MissMessage(ResolvedTugGoal hint, double closestFt, double halfSpanFt)
    {
        string during = hint.Goal.ForcedKind switch
        {
            PushbackLegKind.Push => " while pushing",
            PushbackLegKind.Pull => " while pulling",
            _ => string.Empty,
        };
        string span = $"half the span: {halfSpanFt:F0} ft";
        return $"it passes no closer than {closestFt:F0} ft to {hint.Goal.Label}{during} before its final arrival ({span})";
    }

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// The fallback when no arrival passes the first pending hint: a pass-through tow onto it, planned as that hint's own
    /// goal (<see cref="ResolvedTugGoal.PassThrough"/>) with the context the tow has here — every template, off a stand
    /// the stand push-off and the lane candidates, the alley clearance — except that it ends on the hint's point with no
    /// staging point and no creep, and the arrival plans on from there, carrying straight on when it keeps the same kind.
    /// The later hints are not asked of it. False, with the refusal, when the hint cannot be reached from here.
    /// </summary>
    private bool TryPassFirstHint(bool offStand, out string refusal)
    {
        ResolvedTugGoal hint = _pendingHints[0];
        List<ResolvedTugGoal> later = [.. _pendingHints.Skip(1)];
        _pendingHints.Clear();
        ResolvedTugGoal pass = WithStandBehindExempt(hint) with { Staging = null, PassThrough = true };
        bool passed = TryPlanResolved(pass, offStand, out refusal, out _);
        _pendingHints.AddRange(passed ? later : [hint, .. later]);
        return passed;
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
                return new TugPathRefusal(TugPathSeverity.ParkedNeighbour, $"Unable, {goal.Subject} would swing into {neighbour.Describe()}", null);
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

        if (ForcedKindDropReason(goal, traces, offStand) is { } forcedReason)
        {
            return (forcedReason, null);
        }

        if (!candidate.WanderBoundedByOverswing && !candidate.IsForcedFallback && TugRun.Follow(_run, traces).Wandered)
        {
            return ($"a same-kind run without a turn wandered more than {TugRun.MaxWanderDeg:0}°", null);
        }

        TugMoveTrace last = traces[^1];
        bool linePull = (last.Move.Kind == PushbackLegKind.Pull) && (last.Move.Shape == TugMoveShape.ViaLine);
        double pastDeg = (linePull && IsLaneGoal(goal, offStand) && !candidate.IsForcedFallback) ? PastLineDeg(last) : 0.0;
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
    /// The rule a forced leg kind adds: every move is of that kind, but for the push-off run a plan off a stand opens with
    /// (its pushes up to the first move of another kind), which the stand requires whatever the leg is forced to. Null
    /// when the goal forces no kind or the candidate keeps to it.
    /// </summary>
    private static string? ForcedKindDropReason(ResolvedTugGoal goal, IReadOnlyList<TugMoveTrace> traces, bool offStand)
    {
        if (goal.Goal.ForcedKind is not { } forced)
        {
            return null;
        }

        int afterPushOff = 0;
        while (offStand && (afterPushOff < traces.Count) && (traces[afterPushOff].Move.Kind == PushbackLegKind.Push))
        {
            afterPushOff++;
        }

        return traces.Skip(afterPushOff).Any(t => t.Move.Kind != forced) ? $"a {Opposite(forced)} move on a leg forced to {forced}" : null;
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
