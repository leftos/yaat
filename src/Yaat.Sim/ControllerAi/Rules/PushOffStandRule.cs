using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Ground rule 0: a jet or turboprop on a push-back stand asks for a push (<see cref="PilotPendingRequestKind.Pushback"/>),
/// and Ground answers it with the recorded <c>PUSH</c> to the stand's first planned target the live check grades clear
/// (<see cref="PushTargetLiveCheck"/>). The request re-armed the after-push call, so the crew then reports "pushed back
/// from gate" and <see cref="AnswerTaxiOutRule"/> taxis it.
///
/// <para>One push at a time per ramp area: while another aircraft at the same airport pushes in the same area (in its
/// <see cref="PushbackPhase"/> at any speed, a tow from a non-stand origin included, or issued a push it has not started;
/// <see cref="HoldingAfterPushbackPhase"/> does not count), and while every target of the stand is blocked, Ground
/// answers "standby" — once, and again whenever the crew asks again — and pushes as soon as the way is free. The area is
/// the lane family of the stand's lead-out lane; a stand with no lead-out lane, or a pusher with no lane, is compared by
/// distance instead (<see cref="RampAreaRadiusFt"/>). Among the clear targets, one whose lane no other tug is pushing
/// onto is preferred. It never pushes to a blocked target (<c>PUSHF</c>) and never taxis the aircraft off a push-back
/// stand. An aircraft an RPO already pushed is left alone.</para>
///
/// <para>A wait that cannot end on its own opens a <see cref="AiAnomalyKind.PushbackHeld"/> anomaly with the standby that
/// finds it — the stand has no push plan, or every target is unflyable or blocked by aircraft parked and waiting on this
/// one — and any wait does at the <see cref="StandbysBeforeAnomaly"/>th standby for the same request.</para>
/// </summary>
public sealed class PushOffStandRule : IDecisionRule
{
    /// <summary>The ramp area around a stand with no lead-out lane, feet from the stand node to the other pusher.</summary>
    public const double RampAreaRadiusFt = 600.0;

    /// <summary>The standby for one request at which a push still held opens a <see cref="AiAnomalyKind.PushbackHeld"/> anomaly.</summary>
    public const int StandbysBeforeAnomaly = 3;

    private static readonly ILogger Log = SimLog.CreateLogger("PushOffStandRule");

    /// <summary>Each aircraft waiting on its pushback request, by callsign: what its last passes found.</summary>
    private readonly Dictionary<string, PushbackWait> _waits = new(StringComparer.Ordinal);

    public string Name => "push-off-stand";

    public void Evaluate(AiRuleScope scope)
    {
        var asking = new HashSet<string>(StringComparer.Ordinal);
        foreach (AircraftState aircraft in scope.Jurisdiction)
        {
            AiAircraftMemo memo = scope.MemoFor(aircraft);
            if ((scope.Tick.LayoutFor(aircraft) is not { } layout) || !Applies(aircraft, layout) || (aircraft.PendingPilotRequest is not { } request))
            {
                memo.ForgetObservation(Name);
                continue;
            }

            asking.Add(aircraft.Callsign);
            PushbackWait wait = WaitFor(aircraft.Callsign, request);
            if (memo.CanAct(scope.Now))
            {
                Answer(
                    new Ask
                    {
                        Scope = scope,
                        Aircraft = aircraft,
                        Memo = memo,
                        Layout = layout,
                        Wait = wait,
                    }
                );
            }
        }

        foreach (string gone in _waits.Keys.Where(callsign => !asking.Contains(callsign)).ToList())
        {
            _waits.Remove(gone);
        }

        scope.CloseVanished(AiAnomalyKind.PushbackHeld, _waits.Where(kv => kv.Value.Flagged).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Forgets every wait: a snapshot restore resets the brain. The stand plans are the world's
    /// (<see cref="SimulationWorld.PushPlans"/>).
    /// </summary>
    public void Reset() => _waits.Clear();

    /// <summary>
    /// The aircraft the rule acts on: on the ground, still parked on a push-back stand, its pushback request open, and no
    /// push already under way.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="layout">The ground layout it is on, or null when its airport has none.</param>
    /// <returns>Whether the rule answers its pushback request.</returns>
    public static bool Applies(AircraftState aircraft, AirportGroundLayout? layout) =>
        aircraft.IsOnGround
        && (aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Pushback })
        && (aircraft.Phases?.CurrentPhase is AtParkingPhase)
        && (ParkedStandDeparture.Of(aircraft, layout) == StandDeparture.PushBack)
        && !PushUnderWay(aircraft);

    /// <summary>A push an RPO already started: the aircraft's phases hold a pushback or after-push phase.</summary>
    private static bool PushUnderWay(AircraftState aircraft) =>
        aircraft.Phases?.Phases.Any(phase => phase is PushbackPhase or HoldingAfterPushbackPhase) == true;

    /// <summary>The wait for <paramref name="request"/>: the one already kept for it, else a fresh one (a new request starts over).</summary>
    private PushbackWait WaitFor(string callsign, PilotPendingRequest request)
    {
        if (!_waits.TryGetValue(callsign, out PushbackWait? wait) || (wait.RequestedAtSeconds != request.FirstRequestedAtSeconds))
        {
            wait = new PushbackWait(request.FirstRequestedAtSeconds);
            _waits[callsign] = wait;
        }

        return wait;
    }

    /// <summary>Pushes the aircraft when its ramp area is free and a target is clear; answers standby otherwise.</summary>
    private void Answer(Ask ask)
    {
        if ((ask.Aircraft.Ground.ParkingSpot is not { } spot) || (ask.Layout.FindParkingByName(spot) is not { Name: { } stand } standNode))
        {
            return;
        }

        List<AircraftState> pushers = [.. ask.Scope.Tick.Snapshot.Where(other => IsPusherHere(ask, other))];
        List<string> sameArea = [.. pushers.Where(pusher => InRampArea(ask.Layout, standNode, pusher)).Select(pusher => pusher.Callsign)];
        if (sameArea.Count > 0)
        {
            Standby(ask, stand, $"{string.Join(", ", sameArea)} pushing in the same ramp area", hopeless: false);
            return;
        }

        TargetPass pass = ClearTargetFor(ask, stand, pushers);
        if (pass.Target is not { } target)
        {
            Standby(ask, stand, $"no push target clear ({pass.Outcomes})", pass.Hopeless);
            return;
        }

        var intent = new AiIntent(Name, $"pushback requested; {target.Command}");
        if (ask.Scope.TryIssue(ask.Aircraft, ask.Memo, target.Command, intent))
        {
            ask.Memo.Intent = GroundIntent.PushIssued;
        }
    }

    /// <summary>
    /// Whether <paramref name="other"/> is another aircraft pushing at this aircraft's airport: the same ground layout, and in
    /// its <see cref="PushbackPhase"/> or issued a push by this brain it has not started yet (it is still parked; the
    /// pilot's reaction delay has not run out). The issued push stops counting once its effect window
    /// (the scenario's <c>CommandRunDelayMaxSeconds</c> plus <see cref="AiRuleScope.EffectGraceSeconds"/>) has passed.
    /// </summary>
    private static bool IsPusherHere(Ask ask, AircraftState other) =>
        !ReferenceEquals(other, ask.Aircraft) && IsPushing(ask.Scope, other) && ReferenceEquals(ask.Scope.Tick.LayoutFor(other), ask.Layout);

    private static bool IsPushing(AiRuleScope scope, AircraftState other) =>
        other.Phases?.CurrentPhase switch
        {
            PushbackPhase => true,
            AtParkingPhase => (other.Ground.PushedBackFrom is null)
                && scope.Memos.TryGetValue(other.Callsign, out AiAircraftMemo? memo)
                && (memo.Intent == GroundIntent.PushIssued)
                && (scope.Now <= memo.IssuedAtSeconds + scope.Tick.Scenario.CommandRunDelayMaxSeconds + AiRuleScope.EffectGraceSeconds),
            _ => false,
        };

    /// <summary>
    /// "Standby" (<c>STBY</c>) while the push must wait, once per asking: the request goes to standby and the crew asks
    /// again later (<see cref="PilotRequestTracker.StandbyFollowUpDelaySeconds"/>), when it is answered again. The standby
    /// that finds the wait <paramref name="hopeless"/>, and the <see cref="StandbysBeforeAnomaly"/>th for the request,
    /// opens a <see cref="AiAnomalyKind.PushbackHeld"/> anomaly naming the stand and why it waits.
    /// </summary>
    private void Standby(Ask ask, string stand, string reason, bool hopeless)
    {
        AircraftState aircraft = ask.Aircraft;
        if (aircraft.PendingPilotRequest is not { ResponseState: PilotPendingRequestResponseState.None })
        {
            return;
        }

        Log.LogDebug("{Callsign}: pushback held, {Reason}", aircraft.Callsign, reason);
        if (!ask.Scope.TryIssue(aircraft, ask.Memo, "STBY", new AiIntent(Name, $"pushback requested; standby: {reason}")))
        {
            return;
        }

        PushbackWait wait = ask.Wait;
        wait.Standbys++;
        if (!hopeless && (wait.Standbys < StandbysBeforeAnomaly))
        {
            return;
        }

        wait.Flagged = true;
        string why = hopeless ? "nothing it waits for can clear" : $"still held at standby {wait.Standbys}";
        ask.Scope.Tick.Anomalies.Open(
            AiAnomalyKind.PushbackHeld,
            ask.Scope.Position.PositionId,
            aircraft.Callsign,
            ask.Scope.Now,
            $"pushback from stand {stand} held, {why}: {reason}"
        );
    }

    /// <summary>
    /// Whether <paramref name="pusher"/> is pushing in the ramp area of <paramref name="standNode"/>: its lane (the lead-out
    /// lane of the stand it pushed from, else the taxi edge it is on) is in the lane family of the stand's lead-out lane,
    /// or — when either has no lane — it is within <see cref="RampAreaRadiusFt"/> of the stand node.
    /// </summary>
    private static bool InRampArea(AirportGroundLayout layout, GroundNode standNode, AircraftState pusher)
    {
        string? standLane = RampLaneReposition.LeadOutLane(standNode);
        string? pusherLane = PusherLane(layout, pusher);
        if ((standLane is null) || (pusherLane is null))
        {
            return GeoMath.DistanceNm(standNode.Position, pusher.Position) * GeoMath.FeetPerNm <= RampAreaRadiusFt;
        }

        return string.Equals(standLane, pusherLane, StringComparison.OrdinalIgnoreCase)
            || (
                RampLaneReposition.AreSiblingLanes(standLane, pusherLane)
                && RampLaneReposition.IsRampTaxilane(layout, standLane)
                && RampLaneReposition.IsRampTaxilane(layout, pusherLane)
            );
    }

    /// <summary>
    /// The lane a pusher is in: the lead-out lane of the stand it pushed from (or is still parked on, its push issued but
    /// not started), else the nearest taxi edge's name, else null.
    /// </summary>
    private static string? PusherLane(AirportGroundLayout layout, AircraftState pusher)
    {
        string? origin = pusher.Ground.PushedBackFrom ?? ((pusher.Phases?.CurrentPhase is AtParkingPhase) ? pusher.Ground.ParkingSpot : null);
        if ((origin is { Length: > 0 }) && (layout.FindParkingByName(origin) is { } originNode))
        {
            return RampLaneReposition.LeadOutLane(originNode);
        }

        return layout.FindNearestTaxiEdge(pusher.Position)?.Edge.TaxiwayName;
    }

    /// <summary>
    /// The stand's planned target the live check grades clear against every aircraft about, preferring one whose lane no
    /// other tug is pushing onto, with each target's outcome listed (<c>PUSH TC unflyable, PUSH TE blocked by SWA2436</c>);
    /// a null target when the stand has no plan or no target of it is clear, hopeless when no target is merely blocked by
    /// something that can move.
    /// </summary>
    private TargetPass ClearTargetFor(Ask ask, string stand, List<AircraftState> pushers)
    {
        if (PlanFor(ask, stand) is not { } entry)
        {
            return new TargetPass(null, "the stand has no push plan", Hopeless: true);
        }

        var request = new PushTargetLiveCheckRequest
        {
            Subject = NeighbourCandidate.From(ask.Aircraft),
            Actual = AircraftFootprint.FromType(ask.Aircraft.AircraftType),
            Others = NeighbourCandidate.OnGround(ask.Scope.Tick.Snapshot),
        };
        List<TargetVerdict> verdicts = [.. entry.Targets.Select(target => Grade(target, request, ask.Wait))];
        string outcomes = string.Join(", ", verdicts.Select(v => $"{v.Target.Command} {v.Text}"));
        ask.Wait.Blockers = [.. verdicts.Select(v => v.Blocker).OfType<string>()];
        List<PrecomputedPushTarget> clear = [.. verdicts.Where(v => v.Outcome == PushTargetLiveOutcome.Clear).Select(v => v.Target)];
        ask.Wait.AnyClear = clear.Count > 0;
        if (clear.Count == 0)
        {
            Log.LogDebug(
                "{Callsign}: no push target of stand {Stand} at {Airport} is clear: {Outcomes}",
                ask.Aircraft.Callsign,
                stand,
                ask.Layout.AirportId,
                outcomes
            );
            bool hopeless = !verdicts.Any(v => (v.Blocker is { } blocker) && !WaitsOn(blocker, ask.Aircraft.Callsign));
            return new TargetPass(null, outcomes, hopeless);
        }

        HashSet<string> busyLanes = BusyLanes(pushers);
        return new TargetPass(clear.FirstOrDefault(target => !busyLanes.Contains(target.Name)) ?? clear[0], outcomes, Hopeless: false);
    }

    /// <summary>
    /// One target's outcome for the asking aircraft. An unflyable outcome is kept for the rest of the wait — the parked pose
    /// and the type it is flown with do not change — and not checked again.
    /// </summary>
    private static TargetVerdict Grade(PrecomputedPushTarget target, PushTargetLiveCheckRequest request, PushbackWait wait)
    {
        if (wait.Unflyable.Contains(target.Command))
        {
            return new TargetVerdict(target, PushTargetLiveOutcome.Unflyable, Blocker: null, "unflyable");
        }

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, request);
        switch (verdict.Outcome)
        {
            case PushTargetLiveOutcome.Unflyable:
                wait.Unflyable.Add(target.Command);
                return new TargetVerdict(target, PushTargetLiveOutcome.Unflyable, Blocker: null, "unflyable");
            case PushTargetLiveOutcome.Blocked:
                return new TargetVerdict(target, PushTargetLiveOutcome.Blocked, verdict.BlockerCallsign, $"blocked by {verdict.BlockerCallsign}");
            case PushTargetLiveOutcome.Clear:
                return new TargetVerdict(target, PushTargetLiveOutcome.Clear, Blocker: null, "clear");
            default:
                throw new ArgumentOutOfRangeException(nameof(request), verdict.Outcome, "Unknown push target outcome");
        }
    }

    /// <summary>
    /// Whether <paramref name="blocker"/> is itself parked waiting on <paramref name="callsign"/>: it is asking for a push
    /// this rule answers, no target of its stand was clear on its last pass, and <paramref name="callsign"/> blocked one.
    /// </summary>
    private bool WaitsOn(string blocker, string callsign) =>
        _waits.TryGetValue(blocker, out PushbackWait? wait) && !wait.AnyClear && wait.Blockers.Contains(callsign);

    /// <summary>The taxiways other tugs' moves end on (<see cref="PushbackPhase.EndTaxiway"/>).</summary>
    private static HashSet<string> BusyLanes(List<AircraftState> pushers)
    {
        var lanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AircraftState pusher in pushers)
        {
            if (pusher.Phases is not { } phases)
            {
                continue;
            }

            foreach (PushbackPhase move in phases.Phases.OfType<PushbackPhase>())
            {
                if (move.EndTaxiway is { Length: > 0 } lane)
                {
                    lanes.Add(lane);
                }
            }
        }

        return lanes;
    }

    /// <summary>
    /// The stand's push plan for the aircraft's design group, read from the world's store (<see cref="PushPlanStore"/>),
    /// where the scenario load or the aircraft's spawn put it. A plan missing there is a bug: it is logged and the stand
    /// is planned now, on the tick, and stored. <paramref name="stand"/> is the layout node's own name, as the seed and
    /// the planner match it exactly.
    /// </summary>
    private static PushTargetEntry? PlanFor(Ask ask, string stand)
    {
        PushPlanStore store = ask.Scope.Tick.World.PushPlans;
        AirplaneDesignGroup group = AirplaneDesignGroups.GroupForType(ask.Aircraft.AircraftType);
        if (store.TryGet(ask.Layout, stand, group, out PushTargetEntry? stored))
        {
            return stored;
        }

        Log.LogWarning(
            "{Callsign}: the push plan of stand {Stand} at {Airport} was not made before the tick; planning it on the tick",
            ask.Aircraft.Callsign,
            stand,
            ask.Layout.AirportId
        );
        return store.PlanStand(ask.Layout, stand, group);
    }

    /// <summary>One answer's inputs: the evaluation, the asking aircraft, its memo, its layout and its wait.</summary>
    private readonly record struct Ask
    {
        public required AiRuleScope Scope { get; init; }

        public required AircraftState Aircraft { get; init; }

        public required AiAircraftMemo Memo { get; init; }

        public required AirportGroundLayout Layout { get; init; }

        public required PushbackWait Wait { get; init; }
    }

    /// <summary>A target pass: the target to push to (null when none is clear), every outcome as listed, and whether the wait is hopeless.</summary>
    private sealed record TargetPass(PrecomputedPushTarget? Target, string Outcomes, bool Hopeless);

    /// <summary>One target's outcome: the blocker's callsign when blocked or held, and the outcome as the rationale lists it.</summary>
    private sealed record TargetVerdict(PrecomputedPushTarget Target, PushTargetLiveOutcome Outcome, string? Blocker, string Text);

    /// <summary>
    /// What the rule keeps about one aircraft's wait for a push, for the request first made at
    /// <see cref="RequestedAtSeconds"/>: the standbys answered, the targets found unflyable, what blocked it last pass,
    /// and whether it was flagged.
    /// </summary>
    private sealed class PushbackWait(double requestedAtSeconds)
    {
        public double RequestedAtSeconds { get; } = requestedAtSeconds;

        public int Standbys { get; set; }

        public HashSet<string> Unflyable { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Blockers { get; set; } = new(StringComparer.Ordinal);

        public bool AnyClear { get; set; }

        public bool Flagged { get; set; }
    }
}
