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

    /// <summary>Push straight back to abeam a taxiway exit node, keeping the nose where it is.</summary>
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

    /// <summary>Push straight back to abeam a taxiway exit node (a bare <c>PUSH &lt;taxiway&gt;</c>).</summary>
    /// <param name="exitNode">The node on the taxiway to push back to.</param>
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

/// <summary>What to plan: where the aircraft is, what it is, and where the tug is to take it.</summary>
/// <param name="Start">The aircraft's pose now.</param>
/// <param name="StartsAtStand">The aircraft is parked on a stand, so the plan starts with a straight push-off.</param>
/// <param name="AircraftType">ICAO type designator; sets the turn radius and the footprint.</param>
/// <param name="Goals">The goals, in order; at least one.</param>
/// <param name="FinalFacingTrueDeg">A facing that overrides the last goal's, degrees true, or null.</param>
public sealed record TugRequest(TugPose Start, bool StartsAtStand, string AircraftType, IReadOnlyList<TugGoal> Goals, double? FinalFacingTrueDeg);

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
/// reached along its approach line — through the stop point along the facing. The side is a pull when the stop
/// is within 90° of the facing as seen from the aircraft, else a push; within 5° of that boundary both sides are
/// tried. Three templates are tried per side: the side move straight onto the line (a spot push stops at the
/// staging point and creeps forward onto the mark); the other kind onto the line first; and, when the nose has to
/// rotate more than 90°, a three-point turn first. The final pull onto a spot is always a creep. The fewest
/// reversals win, then the shortest path, then the template order, then the push side.</para>
///
/// <para><b>Refusals.</b> A goal further than <see cref="MaxGoalDistanceFt"/> away, a goal on a runway holding
/// position, and a plan whose flown path puts the aircraft's footprint near a runway, across an edge touching a
/// runway holding position, its fuselage across movement-area pavement it was not sent to, or its reference point
/// more than <see cref="TugPathCheck.MaxOffGraphFt"/> from every ground-graph edge.</para>
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

    /// <summary>The simulation step every candidate is flown with, feet.</summary>
    internal const double StepFt = 1.0;

    // PUSH $spot pull-forward: clamp(factor × fuselage length, min, max) ft behind the stop point. Copied from
    // GroundCommandHandler, whose own copy goes when the handler switches to this planner. Aviation-reviewed.
    private const double SpotPullForwardFactor = 0.75;
    private const double SpotPullForwardMinFt = 40.0;
    private const double SpotPullForwardMaxFt = 100.0;

    /// <summary>
    /// Plans a tug move.
    /// </summary>
    /// <param name="layout">The airport ground layout the move happens on.</param>
    /// <param name="request">Where the aircraft is, what it is, and its goals.</param>
    /// <param name="refusal">Why the move cannot be planned, or empty on success.</param>
    /// <returns>The plan, or null when <paramref name="refusal"/> says why not.</returns>
    /// <exception cref="ArgumentException">
    /// The request has no goals, or carries a final facing while its last goal is a <see cref="TugGoalKind.Clear"/> or
    /// <see cref="TugGoalKind.StraightBackTo"/> goal, which has no facing to override.
    /// </exception>
    public static TugPlan? Plan(AirportGroundLayout layout, TugRequest request, out string refusal)
    {
        ValidateRequest(request);
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
        double lengthFt = FuselageLengthFt(aircraftType);
        double halfLenNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;
        double pullFwdNm = Math.Clamp(SpotPullForwardFactor * lengthFt, SpotPullForwardMinFt, SpotPullForwardMaxFt) / GeoMath.FeetPerNm;

        var intoRamp = new TrueHeading(facingTrueDeg).ToReciprocal();
        return (GeoMath.ProjectPoint(spot.Position, intoRamp, halfLenNm), GeoMath.ProjectPoint(spot.Position, intoRamp, halfLenNm + pullFwdNm));
    }

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

        var last = request.Goals[^1].Kind;
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

    /// <summary>A straight push to abeam the node.</summary>
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

    internal static ResolvedTugGoal Resolve(AirportGroundLayout layout, TugRequest request, int index)
    {
        var goal = request.Goals[index];
        bool isLast = index == (request.Goals.Count - 1);
        double? facing = FacingOf(layout, goal, isLast ? request.FinalFacingTrueDeg : null);
        var basis = Basis(goal, index, request.Goals.Count);
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
    private static double? FacingOf(AirportGroundLayout layout, TugGoal goal, double? overrideDeg) =>
        overrideDeg
        ?? goal.Kind switch
        {
            TugGoalKind.Spot => layout.TryGetSpotOutboundHeading(goal.Node!, out double outbound) ? outbound : null,
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
        var node = basis.Goal.Node!;
        if (facing is not { } facingDeg)
        {
            return ToNode(basis, node);
        }

        var faced = basis with { Shape = TugGoalShape.Faced, FacingTrueDeg = facingDeg, Stop = node.Position };
        switch (basis.Goal.Kind)
        {
            case TugGoalKind.Spot:
                var (stop, staging) = TugMovePlanner.SpotStopGeometry(node, facingDeg, aircraftType);
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
        var goal = basis.Goal;
        var onTaxiway = basis with { Stop = goal.Node!.Position, ExemptNames = Names(goal.TaxiwayName!) };
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
        var run = open;
        foreach (var trace in traces)
        {
            var kind = trace.Move.Kind;
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
        var kind = Kind;
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

        var flagged = move with { DwellBefore = (LastKind is { } last) && (last != move.Kind) };
        var trace = TugKinematics.Simulate(End, [flagged], _aircraftType, TugMovePlanner.StepFt).Moves[0];
        _traces.Add(trace);
        End = trace.End;
        LastKind = flagged.Kind;
        Flyable = trace.Completed;
    }
}

/// <summary>Plans a request goal by goal, holding the plan so far.</summary>
internal sealed class TugPlanBuilder
{
    private static readonly ILogger Log = SimLog.CreateLogger("TugMovePlanner");

    /// <summary>A point this far off the reference direction or less is ahead of it; beyond, behind.</summary>
    private const double AheadDeg = 90.0;

    /// <summary>
    /// Within this many degrees of <see cref="AheadDeg"/>, a faced goal's candidates are built for both sides; a
    /// judgement call. Spots that sit abeam each other put the side test on its boundary, where a foot of end error
    /// flips it.
    /// </summary>
    private const double SideTieBandDeg = 5.0;

    /// <summary>A turn or line capture that rotates the nose more than this is flown on the tight radius.</summary>
    private const double TightRotationDeg = 135.0;

    /// <summary>How far off its facing and its approach line a faced goal's last move may end.</summary>
    private const double EndFacingToleranceDeg = 2.0;

    private const double EndCrossTrackToleranceFt = 3.0;

    /// <summary>How far a pull onto a stop point may run past it.</summary>
    private const double PullOvershootToleranceFt = 3.0;

    /// <summary>How far a push onto a spot's staging point may run past it; a judgement call.</summary>
    private const double StagingOvershootToleranceFt = 100.0;

    private readonly AirportGroundLayout _layout;
    private readonly TugRequest _request;
    private readonly TugPathCheck _pathCheck;
    private readonly List<TugMoveTrace> _moves = [];
    private TugPose _end;
    private PushbackLegKind? _lastKind;
    private TugRun? _run;

    internal TugPlanBuilder(AirportGroundLayout layout, TugRequest request)
    {
        _layout = layout;
        _request = request;
        _end = request.Start;
        _pathCheck = new TugPathCheck(layout, request.AircraftType, request.Start.Position);
    }

    internal TugPlan ToPlan() => new(_moves.ToArray(), _end);

    internal bool TryPlanGoal(int index, out string refusal)
    {
        var goal = TugGoalResolver.Resolve(_layout, _request, index);
        if (IsRefusedOutright(goal, out refusal))
        {
            return false;
        }

        bool offStand = (index == 0) && _request.StartsAtStand;
        if (!TryBuildCandidates(goal, offStand, out var candidates, out refusal))
        {
            return false;
        }

        var best = Choose(goal, candidates, offStand, out refusal);
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
        bool straightBack = goal.Shape is TugGoalShape.Clear or TugGoalShape.StraightBack;
        var pushOff =
            (offStand && !straightBack) ? TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.FuselageLengthFt(_request.AircraftType) / 2.0) : null;
        refusal = string.Empty;
        switch (goal.Shape)
        {
            case TugGoalShape.Faced:
                candidates = FacedCandidates(goal, pushOff);
                return true;
            case TugGoalShape.ToNode:
                return TryToNodeCandidate(goal, pushOff, out candidates, out refusal);
            case TugGoalShape.StraightBack:
                return TryStraightBackCandidate(goal, out candidates, out refusal);
            default:
                candidates = [SingleCandidate(goal, pushOff)];
                return true;
        }
    }

    /// <summary>
    /// The faced-goal candidates, in rank order: T1 (the side move onto the line), T2 (the other kind first), and —
    /// when the nose has to rotate more than 90° — T3 (a three-point turn first). The side comes from where the stop
    /// lies against the facing; within <see cref="SideTieBandDeg"/> of the 90° boundary each template is built for
    /// both sides, the push side first.
    /// </summary>
    private List<TugCandidate> FacedCandidates(ResolvedTugGoal goal, TugMove? pushOff)
    {
        var from = NewCandidate("probe", pushOff).End;
        double facing = goal.FacingTrueDeg;
        double stopOffFacingDeg = TugMovePlanner.AbsDiffDeg(GeoMath.BearingTo(from.Position, goal.Stop), facing);
        var sides = SidesFor(stopOffFacingDeg);
        Log.LogDebug(
            "Tug {Subject}: stop {StopOffFacingDeg:F2}° off the facing; building candidates for the {Sides} side",
            goal.Subject,
            stopOffFacingDeg,
            string.Join(" and ", sides)
        );

        var candidates = sides.Select(side => Direct(goal, pushOff, side)).ToList();
        candidates.AddRange(sides.Select(side => OtherKindFirst(goal, pushOff, side)));
        double rotationDeg = TugMovePlanner.AbsDiffDeg(from.NoseTrueDeg, facing);
        if (rotationDeg > AheadDeg)
        {
            candidates.AddRange(sides.Select(side => ThreePointTurn(goal, pushOff, side, from.NoseTrueDeg, rotationDeg)));
        }

        return candidates;
    }

    /// <summary>
    /// The side a faced goal is approached from: pull when the stop is at or within 90° of the facing, else push;
    /// both, push first, within <see cref="SideTieBandDeg"/> of 90°.
    /// </summary>
    private static PushbackLegKind[] SidesFor(double stopOffFacingDeg)
    {
        if (Math.Abs(stopOffFacingDeg - AheadDeg) <= SideTieBandDeg)
        {
            return [PushbackLegKind.Push, PushbackLegKind.Pull];
        }

        return stopOffFacingDeg <= AheadDeg ? [PushbackLegKind.Pull] : [PushbackLegKind.Push];
    }

    /// <summary>T1: the side move straight onto the approach line.</summary>
    private TugCandidate Direct(ResolvedTugGoal goal, TugMove? pushOff, PushbackLegKind side)
    {
        var candidate = NewCandidate($"T1 direct, {side} side", pushOff);
        AddApproach(candidate, goal, side);
        return candidate;
    }

    /// <summary>T2: the other kind onto the approach line first, then the side move.</summary>
    private TugCandidate OtherKindFirst(ResolvedTugGoal goal, TugMove? pushOff, PushbackLegKind side)
    {
        var candidate = NewCandidate($"T2 other kind first, {side} side", pushOff);
        candidate.Add(LineMove(candidate, goal, Opposite(side), stopAt: null));
        AddApproach(candidate, goal, side);
        return candidate;
    }

    /// <summary>T3: the other kind turns the nose to within 90° of the facing first, then the side move.</summary>
    private TugCandidate ThreePointTurn(ResolvedTugGoal goal, TugMove? pushOff, PushbackLegKind side, double startNoseDeg, double rotationDeg)
    {
        double facing = goal.FacingTrueDeg;
        double towardNose = Math.Sign(new TrueHeading(facing).SignedAngleTo(new TrueHeading(startNoseDeg)));
        var candidate = NewCandidate($"T3 three-point turn, {side} side", pushOff);
        candidate.Add(TurnMove(candidate, Opposite(side), facing + (towardNose * (rotationDeg - AheadDeg))));
        AddApproach(candidate, goal, side);
        return candidate;
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
        var approach = LineMove(candidate, goal, side, staged ? goal.Staging : goal.Stop);
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
        var candidate = NewCandidate("to node", pushOff);
        double offNoseDeg = TugMovePlanner.AbsDiffDeg(GeoMath.BearingTo(candidate.End.Position, goal.Stop), candidate.End.NoseTrueDeg);
        var kind = offNoseDeg > AheadDeg ? PushbackLegKind.Push : PushbackLegKind.Pull;
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

    private bool TryStraightBackCandidate(ResolvedTugGoal goal, out List<TugCandidate> candidates, out string refusal)
    {
        double behindFt = TugMovePlanner.AlongFt(goal.Stop, _end.Position, _end.TravelTrueDeg(PushbackLegKind.Push));
        if (behindFt <= 0.0)
        {
            candidates = [];
            refusal = $"Unable, {goal.Goal.Label} is not behind the aircraft";
            return false;
        }

        var candidate = NewCandidate("straight back", pushOff: null);
        candidate.Add(TugMove.Straight(PushbackLegKind.Push, behindFt));
        candidates = [candidate];
        refusal = string.Empty;
        return true;
    }

    /// <summary>The one move list a facing, clear or taxiway-line goal has.</summary>
    private TugCandidate SingleCandidate(ResolvedTugGoal goal, TugMove? pushOff)
    {
        var candidate = NewCandidate(goal.Shape.ToString(), pushOff);
        switch (goal.Shape)
        {
            case TugGoalShape.Facing:
                candidate.Add(TurnMove(candidate, PushbackLegKind.Push, goal.FacingTrueDeg));
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
                candidate.Add(LineMove(candidate, goal, PushbackLegKind.Push, stopAt: null));
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

    /// <summary>A capture of the goal's approach line that leaves the nose on its facing.</summary>
    private static TugMove LineMove(TugCandidate candidate, ResolvedTugGoal goal, PushbackLegKind kind, LatLon? stopAt)
    {
        double facing = goal.FacingTrueDeg;
        bool tight = TugMovePlanner.AbsDiffDeg(candidate.End.NoseTrueDeg, facing) > TightRotationDeg;
        return TugMove.ViaLine(kind, goal.Stop, TugKinematics.FlipForKind(facing, kind), stopAt) with { Tight = tight };
    }

    private static TugMove TurnMove(TugCandidate candidate, PushbackLegKind kind, double facingTrueDeg)
    {
        bool tight = TugMovePlanner.AbsDiffDeg(candidate.End.NoseTrueDeg, facingTrueDeg) > TightRotationDeg;
        return TugMove.TurnTo(kind, facingTrueDeg) with { Tight = tight };
    }

    /// <summary>
    /// The surviving candidate with the fewest reversals, then the shortest path, then the earliest in rank order
    /// (candidates arrive in rank order, so a later one wins only when strictly better). With none left, the refusal
    /// is the most severe flown-path reason any candidate hit, else the generic one.
    /// </summary>
    private TugCandidate? Choose(ResolvedTugGoal goal, List<TugCandidate> candidates, bool offStand, out string refusal)
    {
        TugCandidate? best = null;
        TugPathRefusal? pathRefusal = null;
        foreach (var candidate in candidates)
        {
            var (drop, path) = Judge(goal, candidate, offStand);
            if (drop is not null)
            {
                Log.LogDebug("Tug {Subject}: dropped {Template} ({Moves}): {Reason}", goal.Subject, candidate.Template, candidate.Describe(), drop);
                pathRefusal = MoreSevere(pathRefusal, path);
                continue;
            }

            Log.LogDebug(
                "Tug {Subject}: kept {Template} ({Moves}), {Reversals} reversal(s), {PathFt:F0} ft",
                goal.Subject,
                candidate.Template,
                candidate.Describe(),
                candidate.Reversals,
                candidate.PathLengthFt
            );
            best = IsBetter(candidate, best) ? candidate : best;
        }

        refusal = best is null ? (pathRefusal?.Message ?? $"Unable, cannot line up on {goal.Name} from here") : string.Empty;
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

    private static bool IsBetter(TugCandidate candidate, TugCandidate? best) =>
        (best is null)
        || (candidate.Reversals < best.Reversals)
        || ((candidate.Reversals == best.Reversals) && (candidate.PathLengthFt < best.PathLengthFt));

    private static TugPathRefusal? MoreSevere(TugPathRefusal? current, TugPathRefusal? found) =>
        (found is { } next) && ((current is not { } held) || (next.Severity < held.Severity)) ? found : current;

    /// <summary>
    /// Why a candidate is dropped, or null when it survives. Every flyable candidate's path is checked, even one
    /// already dropped for its shape, so that the refusal can name the pavement any candidate would have reached.
    /// </summary>
    private (string? Drop, TugPathRefusal? Path) Judge(ResolvedTugGoal goal, TugCandidate candidate, bool offStand)
    {
        if (!candidate.Flyable)
        {
            return ("a move ran past its travel budget", null);
        }

        string? shapeReason = goal.Shape == TugGoalShape.Faced ? FacedDropReason(goal, candidate, offStand) : null;
        var path = _pathCheck.Check(candidate.Traces, candidate.End, goal.ExemptNames, goal.Subject);
        return (shapeReason ?? path?.Message, path);
    }

    private string? FacedDropReason(ResolvedTugGoal goal, TugCandidate candidate, bool offStand)
    {
        var traces = candidate.Traces;
        if (offStand && (traces.Count > 1) && (traces[1].Move.Kind == PushbackLegKind.Pull))
        {
            return "the move after the stand push-off is a pull";
        }

        if (TugRun.Follow(_run, traces).Wandered)
        {
            return $"a same-kind run without a turn wandered more than {TugRun.MaxWanderDeg:0}°";
        }

        return EndDropReason(goal, traces[^1]) ?? OvershootDropReason(goal, traces);
    }

    private static string? EndDropReason(ResolvedTugGoal goal, TugMoveTrace last)
    {
        double noseErrorDeg = TugMovePlanner.AbsDiffDeg(last.End.NoseTrueDeg, goal.FacingTrueDeg);
        double crossFt = GeoMath.SignedCrossTrackDistanceNm(last.End.Position, goal.Stop, new TrueHeading(goal.FacingTrueDeg)) * GeoMath.FeetPerNm;
        bool offLine = (noseErrorDeg > EndFacingToleranceDeg) || (Math.Abs(crossFt) > EndCrossTrackToleranceFt);
        return offLine ? $"ended {crossFt:F1} ft off the approach line with the nose {noseErrorDeg:F1}° off the facing" : null;
    }

    private static string? OvershootDropReason(ResolvedTugGoal goal, IReadOnlyList<TugMoveTrace> traces)
    {
        foreach (var trace in traces)
        {
            if ((PullOvershootFt(trace, goal) is { } pullFt) && (pullFt > PullOvershootToleranceFt))
            {
                return $"the pull onto the stop point ran {pullFt:F1} ft past it";
            }

            if ((StagingOvershootFt(trace, goal) is { } pushFt) && (pushFt > StagingOvershootToleranceFt))
            {
                return $"the push onto the staging point ran {pushFt:F1} ft past it";
            }
        }

        return null;
    }

    private static double? PullOvershootFt(TugMoveTrace trace, ResolvedTugGoal goal)
    {
        var move = trace.Move;
        if (move.Kind != PushbackLegKind.Pull)
        {
            return null;
        }

        if ((move.Shape == TugMoveShape.ViaLine) && (move.StopAt == goal.Stop))
        {
            return trace.EndOvershootFt;
        }

        return move.Creep ? TugMovePlanner.AlongFt(trace.End.Position, goal.Stop, goal.FacingTrueDeg) : null;
    }

    private static double? StagingOvershootFt(TugMoveTrace trace, ResolvedTugGoal goal) =>
        ((trace.Move.Kind == PushbackLegKind.Push) && (goal.Staging is { } staging) && (trace.Move.StopAt == staging)) ? trace.EndOvershootFt : null;
}
