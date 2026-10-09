using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// The aircraft a push menu opens for and every aircraft about it: every stored target of its stand is checked against
/// them.
/// </summary>
public sealed record PushTargetLiveCheckRequest
{
    /// <summary>
    /// The aircraft where it stands: the stored moves are flown again from its pose, and its callsign tells it apart from
    /// <see cref="Others"/>.
    /// </summary>
    public required TugNeighbourCandidate Subject { get; init; }

    /// <summary>
    /// The aircraft's own dimensions: they set the outline the start overlap is read with, the turn radius the stored
    /// moves are flown again with, and the outline swept along the path, so one outline decides the whole verdict.
    /// </summary>
    public required AircraftFootprint Actual { get; init; }

    /// <summary>
    /// Every aircraft the caller can see; may include the subject. Only the parked or held ones count
    /// (<see cref="TugParkedNeighbours"/>).
    /// </summary>
    public required IReadOnlyList<TugNeighbourCandidate> Others { get; init; }
}

/// <summary>What the live check found for one stored target.</summary>
public enum PushTargetLiveOutcome
{
    /// <summary>The aircraft flies the stored plan and passes every parked neighbour.</summary>
    Clear,

    /// <summary>A parked or held neighbour blocks it: the aircraft already touches it, or the swept path falls through its floor.</summary>
    Blocked,

    /// <summary>
    /// The aircraft cannot fly the stored plan: it does not complete it, or, for a plan whose last move is not a
    /// floating-stop line, ends more than half its length from the stored end
    /// (<see cref="PushTargetLiveCheck.EndDriftLimitFt"/>). For a plan whose last move is a floating-stop line, completion
    /// is the whole test: completing a final floating line already puts the aircraft on the line, and only the slide
    /// along it remains, which is not part of the plan.
    /// </summary>
    Unflyable,
}

/// <summary>
/// The live check's verdict on one stored target, built only through <see cref="Clear"/>, <see cref="Blocked"/> and
/// <see cref="Unflyable"/>, so a blocker callsign comes with a blocked outcome and with nothing else.
/// </summary>
public sealed record PushTargetLiveVerdict
{
    private PushTargetLiveVerdict() { }

    /// <summary>The target is clear.</summary>
    public static PushTargetLiveVerdict Clear { get; } = new() { Outcome = PushTargetLiveOutcome.Clear };

    /// <summary>The aircraft cannot fly the target's stored plan (<see cref="PushTargetLiveOutcome.Unflyable"/>).</summary>
    public static PushTargetLiveVerdict Unflyable { get; } = new() { Outcome = PushTargetLiveOutcome.Unflyable };

    /// <summary>Clear, blocked or unflyable.</summary>
    public PushTargetLiveOutcome Outcome { get; private init; }

    /// <summary>The neighbour that blocks the target; null unless <see cref="Outcome"/> is blocked.</summary>
    public string? BlockerCallsign { get; private init; }

    /// <summary>The target is blocked by <paramref name="callsign"/>.</summary>
    /// <param name="callsign">The blocking neighbour's callsign.</param>
    /// <returns>The verdict.</returns>
    public static PushTargetLiveVerdict Blocked(string callsign)
    {
        ArgumentException.ThrowIfNullOrEmpty(callsign);
        return new PushTargetLiveVerdict { Outcome = PushTargetLiveOutcome.Blocked, BlockerCallsign = callsign };
    }
}

/// <summary>
/// Whether a stored push target can be flown right now: the target's stored moves — the design group's plan — are flown
/// again from the aircraft's pose with the aircraft's own turn radius, and swept with its own outline against every
/// parked neighbour by the same rule the tug planner holds a candidate to and <see cref="GroundConflictDetector"/> holds a
/// tow under way to — every target, whatever its goal. The move list stays the stored one: the check never re-plans.
/// </summary>
public static class PushTargetLiveCheck
{
    private static readonly ILogger Log = SimLog.CreateLogger("PushTargetLiveCheck");

    /// <summary>
    /// The verdict on <paramref name="target"/> now. Blocked by a neighbour the aircraft's outline
    /// (<see cref="PushTargetLiveCheckRequest.Actual"/>) already touches where it stands, read with a tug and towbar ahead
    /// of the nose when the target opens with a pull
    /// (<see cref="TugParkedNeighbours.FindStartOverlap(TugNeighbourCandidate, AircraftFootprint, bool, IEnumerable{TugNeighbourCandidate})"/>);
    /// else unflyable when the aircraft does not fly the stored moves to completion, or, for a plan whose last move is not a
    /// floating-stop line (<see cref="EndsOnAFloatingLine"/>), ends more than <see cref="EndDriftLimitFt"/> from the stored
    /// end (<see cref="EndDriftFt"/>); else blocked by the parked neighbour within reach of the re-flown path whose sweep
    /// floor the path falls through first along it; else clear.
    /// </summary>
    /// <param name="target">The stored target.</param>
    /// <param name="request">The aircraft and every aircraft about it.</param>
    /// <returns>The verdict.</returns>
    public static PushTargetLiveVerdict Check(PrecomputedPushTarget target, PushTargetLiveCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        bool towedNoseFirst = (target.Moves.Count > 0) && (target.Moves[0].Kind == PushbackLegKind.Pull);
        if (TugParkedNeighbours.FindStartOverlap(request.Subject, request.Actual, towedNoseFirst, request.Others) is { } overlap)
        {
            Log.LogDebug("{Stand} {Command}: {Neighbour} already touches the aircraft", StandOf(request), target.Command, overlap.NeighbourCallsign);
            return PushTargetLiveVerdict.Blocked(overlap.NeighbourCallsign);
        }

        if (target.Moves.Count == 0)
        {
            return PushTargetLiveVerdict.Clear;
        }

        var start = new TugPose(request.Subject.Position, request.Subject.TrueHeadingDeg);
        List<TugMove> moves = [.. target.Moves.Select(m => m.ToMove())];
        TugSimulation flown = TugKinematics.Simulate(start, moves, request.Actual, TugMovePlanner.StepFt);
        if (!flown.Completed)
        {
            Log.LogDebug(
                "{Stand} {Command}: {Type} does not fly the stored moves to completion; unflyable",
                StandOf(request),
                target.Command,
                request.Actual.TypeCode
            );
            return PushTargetLiveVerdict.Unflyable;
        }

        if (!EndsOnAFloatingLine(target))
        {
            double driftFt = EndDriftFt(target, flown);
            if (driftFt > EndDriftLimitFt(request.Actual))
            {
                Log.LogDebug(
                    "{Stand} {Command}: {Type} ends {DriftFt:F1} ft from the stored end, over half its length; unflyable",
                    StandOf(request),
                    target.Command,
                    request.Actual.TypeCode,
                    driftFt
                );
                return PushTargetLiveVerdict.Unflyable;
            }
        }

        return FirstBlocker(target, request, start, flown) is { } blocker ? PushTargetLiveVerdict.Blocked(blocker) : PushTargetLiveVerdict.Clear;
    }

    /// <summary>
    /// The farthest a re-flown plan may drift from the stored one and still be this aircraft's push, feet: half its length.
    /// </summary>
    /// <param name="actual">The aircraft's dimensions.</param>
    /// <returns>The limit, feet.</returns>
    public static double EndDriftLimitFt(AircraftFootprint actual) => actual.LengthFt / 2.0;

    /// <summary>
    /// The target's last move captures a line with no stop on it (<c>PUSH TE</c>): where along the line it ends is not
    /// part of the plan.
    /// </summary>
    /// <param name="target">The stored target.</param>
    /// <returns>Whether it ends on a floating line.</returns>
    public static bool EndsOnAFloatingLine(PrecomputedPushTarget target) =>
        (target.Moves.Count > 0) && (target.Moves[^1].Shape == TugMoveShape.ViaLine) && (target.Moves[^1].StopAtLatitude is null);

    /// <summary>
    /// How far the re-flown plan drifted from the stored one, feet: how far the flown end lies from the stored planned
    /// end. <see cref="Check"/> holds it to <see cref="EndDriftLimitFt"/> only for a plan whose last move is not a
    /// floating-stop line (<see cref="EndsOnAFloatingLine"/>): completing a final floating line already puts the aircraft
    /// on the line, and only the slide along it remains, which is not part of the plan.
    /// </summary>
    /// <param name="target">The stored target; at least one move.</param>
    /// <param name="flown">Its moves as the aircraft flew them.</param>
    /// <returns>The drift, feet.</returns>
    public static double EndDriftFt(PrecomputedPushTarget target, TugSimulation flown)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(flown);
        PushMoveEntry last = target.Moves[^1];
        return GeoMath.DistanceNm(new LatLon(last.PlannedEndLatitude, last.PlannedEndLongitude), flown.End.Position) * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// Of the parked neighbours within reach of the re-flown path (<see cref="TugParkedNeighbours.WithinReachOfPath"/>),
    /// the one whose sweep floor the path falls through first along it, or null when it keeps every floor.
    /// </summary>
    private static string? FirstBlocker(PrecomputedPushTarget target, PushTargetLiveCheckRequest request, TugPose start, TugSimulation flown)
    {
        var anchor = new TugRowAnchor(start, target.Moves[0].Kind);
        var rowClearances = new TugRowClearances();
        TugParkedNeighbour? first = null;
        double firstAlongFt = double.MaxValue;
        foreach (TugParkedNeighbour neighbour in TugParkedNeighbours.WithinReachOfPath(request.Subject, request.Actual, flown.Moves, request.Others))
        {
            double? rowClearanceFt = TugNeighbourSweep.RowClearanceFt(rowClearances, anchor, request.Actual, neighbour);
            if (
                (TugNeighbourSweep.FirstFoulAlongFt(flown.Moves, neighbour, start.Position, request.Actual, rowClearanceFt) is { } alongFt)
                && (alongFt < firstAlongFt)
            )
            {
                first = neighbour;
                firstAlongFt = alongFt;
            }
        }

        if ((first is not null) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "{Stand} {Command}: blocked by {Neighbour} {AlongFt:F1} ft along the path; closest {ClosestFt:F1} ft",
                StandOf(request),
                target.Command,
                first.Describe(),
                firstAlongFt,
                TugNeighbourSweep.ClosestFt(flown.Moves, first, start.Position, request.Actual)
            );
        }

        return first?.Callsign;
    }

    private static string StandOf(PushTargetLiveCheckRequest request) => request.Subject.StandName ?? "(no stand)";
}
