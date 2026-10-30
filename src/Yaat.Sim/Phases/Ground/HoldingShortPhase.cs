using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft holds short of a runway or explicit hold-short point.
/// Speed=0. Generates a notification on start.
/// Clearance-gated: completes when RunwayCrossing clearance is satisfied
/// (via CROSS, LUAW, or CTO command).
/// </summary>
public sealed class HoldingShortPhase(HoldShortPoint holdShort) : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldingShortPhase");

    private HoldShortPoint _holdShort = holdShort;
    private bool _hasAnnouncedReady;

    // Set once the pilot has said it is holding short, cleared across, for traffic beyond the far side. Snapshotted.
    private bool _saidTrafficAhead;

    // Set while the phase holds at its bar, any runway bar the crossing clearance releases, for the lead of the follow armed
    // behind it. Snapshotted.
    private bool _waitingForLead;

    // Set once the pilot has made the call for the current wait for the lead; cleared as a wait starts or ends. Snapshotted.
    private bool _saidWaitForLead;

    // The end the phase's name and terminal text give its runway, chosen as the hold starts from the room's active runways
    // (RunwayCrossingEnd.ActiveEndOf); null — the combined id — when no end of it is active. Kept until the hold ends. Snapshotted.
    private string? _displayEnd;

    public HoldShortPoint HoldShort => _holdShort;

    /// <summary>
    /// Whether the phase holds at its bar, any runway bar the crossing clearance releases, for the lead of the follow armed behind
    /// it to be ahead on their shared route (<see cref="FollowingPhase.WaitsAtBarForLead"/>).
    /// </summary>
    public bool IsWaitingForLead => _waitingForLead;

    /// <summary>
    /// Re-points this phase at the taxi route's own <see cref="HoldShortPoint"/> after a snapshot restore.
    ///
    /// Live, the phase is constructed with the route's instance, so the two share one object — clearing the hold-short
    /// through the phase updates the route, and the phase can see route-only fields such as
    /// <see cref="HoldShortPoint.TailOverRunwayNodeId"/>. The phase snapshot carries only the node id, runway and
    /// reason, so restoring rebuilds a detached copy and both of those properties are lost. Called by
    /// <see cref="AircraftState.FromSnapshot"/> once the route is available.
    /// </summary>
    internal void RebindHoldShort(HoldShortPoint holdShort) => _holdShort = holdShort;

    public override string Name =>
        _holdShort.TargetName is { } target ? $"Holding Short {_displayEnd ?? HoldShortTarget.Describe(target)}" : "Holding Short";

    public override bool IsIdleAwaitingCommands => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        // Clear any stale target heading left over from the previous
        // taxi segment — otherwise FlightPhysics would keep rotating the
        // stationary aircraft toward it. Consistent with sister hold
        // phases (AtParkingPhase, HoldingAfterExitPhase, etc.).
        ctx.Targets.TargetTrueHeading = null;

        Log.LogDebug(
            "[HoldShort] {Callsign}: holding short of {Target}, nodeId={NodeId}, reason={Reason}",
            ctx.Aircraft.Callsign,
            _holdShort.TargetName ?? "unknown",
            _holdShort.NodeId,
            _holdShort.Reason
        );

        _displayEnd = RunwayCrossingEnd.ActiveEndOf(ctx.Aircraft, _holdShort.TargetName, ctx.GroundLayout, ctx.ActiveRunways);

        // Generate notification
        string target = _displayEnd ?? HoldShortTarget.Describe(_holdShort.TargetName ?? "unknown");
        string taxiway = ctx.Aircraft.Ground.CurrentTaxiway ?? "taxiway";
        // A route-incomplete hold is short of the taxiway the route still needs, so it reads like a controller's HS.
        bool heldAtNamedTarget = _holdShort.Reason is HoldShortReason.ExplicitHoldShort or HoldShortReason.RouteIncomplete;
        string label = heldAtNamedTarget ? $"holding short of {target}" : $"holding short runway {target}";
        // At the end of a route that could not reach its destination the pilot asks for the rest of the way.
        bool requestFurtherTaxi = _holdShort.Reason == HoldShortReason.RouteIncomplete;
        string warningText = $"{ctx.Aircraft.Callsign} {label} at {taxiway}";
        PilotSpeechText speechText = heldAtNamedTarget
            ? PilotResponder.BuildHoldingShortTaxi(ctx.Aircraft, label, taxiway, requestFurtherTaxi)
            : PilotResponder.BuildHoldingShortCrossing(ctx.Aircraft, ResolveSpokenCrossingRunway(ctx, target));
        // At the stop a spawn's preset taxi ends at, the delayed "holding short of C at T41W, … ready to taxi" call replaces
        // this report when that call can come (someone answers it and the pacing rate is above zero), so the pilot does not
        // say it twice and never sits at the bar with neither.
        bool replacedByInitialCall = (PresetTaxiStopLocation(ctx.Aircraft) is not null) && InitialCallupCall.CanCall(ctx);
        if (!replacedByInitialCall)
        {
            PilotResponder.RouteRpoTransmission(ctx.Aircraft, ctx.SoloTrainingMode, ctx.RpoShowPilotSpeech, speechText, warningText);
        }

        // Tail-over-runway (issue #172 W3): the aircraft holds at the taxiway line with its tail still
        // over the runway behind it. Protecting the runway is the controller's job (7110.65 3-7-4), not
        // something the pilot reports — so surface it on the controller/terminal warning lane only,
        // never as a pilot transmission, and never with the combined "01L/19R" in a pilot's mouth.
        if (
            _holdShort.TailOverRunwayNodeId is { } tailNode
            && ctx.GroundLayout is { } groundLayout
            && groundLayout.Nodes.TryGetValue(tailNode, out GroundNode? tailRwyNode)
            && tailRwyNode.RunwayId is { } tailRwy
        )
        {
            string tailName =
                RunwayCrossingEnd.ActiveEndOf(ctx.Aircraft, tailRwy.ToString(), groundLayout, ctx.ActiveRunways) ?? tailRwy.ToDisplayString();
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} not clear of RWY {tailName} — tail over the hold-short bars");
        }

        // "Ready for departure" is only correct at the aircraft's assigned departure runway
        // (DestinationRunway). At an intermediate runway the route merely crosses (RunwayCrossing),
        // the aircraft holds short and awaits a controller-issued crossing clearance — runway
        // crossings are controller-initiated (AIM 4-3-18.a.5, 7110.65 3-7-2), so the pilot makes no
        // "ready" call there. The crossing hold is surfaced on the controller-facing warning lane
        // above instead (issue #194).
        if (
            !_hasAnnouncedReady
            && _holdShort.Reason == HoldShortReason.DestinationRunway
            && _holdShort.TargetName is { Length: > 0 } runwayId
            && ctx.PilotContacts.ResolveFor(ctx.Aircraft, "TWR", PilotContactRoster.SurfaceAirportOf(ctx.Aircraft), ctx.ToEligibilityContext(), false)
                is { } answering
        )
        {
            string facilityCallName = PilotResponder.ResolveAnsweringCallName(answering, "TWR", "tower");
            PilotSpeechText line = PilotResponder.BuildHoldingShortReady(ctx.Aircraft, runwayId, facilityCallName);
            PilotResponder.QueueSoloPilotTransmission(ctx.Aircraft, line, PilotTransmissionKind.Proactive, initialContact: false);
            PilotRequestTracker.RecordRequest(
                ctx.Aircraft,
                PilotPendingRequestKind.Takeoff,
                ctx.ScenarioElapsedSeconds,
                line,
                PilotRequestContext.Runway(runwayId, facilityCallName)
            );
            _hasAnnouncedReady = true;
            answering.MarkInitialContact(ctx.Aircraft);
        }
    }

    /// <summary>
    /// The runway this bar holds short of as a controller or pilot names it, when the target is a combined centerline id
    /// (<c>01R/19L</c>) — which a crossing bar, a departure bar and a runway <c>HS</c> all can carry: the aircraft's own
    /// runway (<see cref="PhaseList.AssignedRunway"/>, when it is this one) by its assigned end, as the departure "ready" call
    /// names it; any other runway by the end a pilot names it by (<see cref="RunwayCrossingEnd.Spoken"/>: the active end, else
    /// the nearest). The display designator of the target otherwise (<c>1R</c>).
    /// </summary>
    public string RunwayEndFacing(AircraftState aircraft, ActiveRunways activeRunways)
    {
        if ((_holdShort.TargetName is not { } combined) || !combined.Contains('/'))
        {
            return HoldShortTarget.Describe(_holdShort.TargetName ?? "the runway");
        }

        return RunwayCrossingEnd.EndFacing(aircraft.Position, combined, aircraft.Ground.Layout, activeRunways, aircraft.Phases?.AssignedRunway);
    }

    /// <summary>
    /// For a runway-crossing hold-short the spoken report names only one end of the runway (e.g. "runway one five" rather than
    /// the combined "one five / three three"): the end a pilot names it by (<see cref="RunwayCrossingEnd.Spoken"/>: the active
    /// end, else the nearest; the first end when the thresholds cannot be resolved). Falls back to
    /// <paramref name="displayDesignator"/> (the end stored as the hold started, else the target as given) when the hold-short
    /// is not a runway crossing, such as the departure bar.
    /// </summary>
    private string ResolveSpokenCrossingRunway(PhaseContext ctx, string displayDesignator)
    {
        if (_holdShort.Reason != HoldShortReason.RunwayCrossing || _holdShort.TargetName is not { Length: > 0 } combined)
        {
            return displayDesignator;
        }

        return RunwayCrossingEnd.Spoken(ctx.Aircraft, combined, ctx.GroundLayout, ctx.ActiveRunways);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;

        // Check if clearance has been satisfied
        foreach (ClearanceRequirement req in Requirements)
        {
            if (req.IsSatisfied)
            {
                _holdShort.IsCleared = true;
                // Cleared across, but traffic beyond the far marking leaves no room to clear the runway (AIM 4-3-18.b): hold at
                // the line until it opens, then cross by itself.
                if (RunwayCrossingFarSideGate.HoldsAtBar(ctx, _holdShort))
                {
                    SayHeldForTraffic(ctx);
                    return false;
                }

                if (HoldsForFollowLead(ctx))
                {
                    return false;
                }

                Log.LogDebug("[HoldShort] {Callsign}: cleared at {Target}", ctx.Aircraft.Callsign, _holdShort.TargetName ?? "unknown");
                return true;
            }
        }

        if (
            (PresetTaxiStopLocation(ctx.Aircraft) is { } location)
            && (ElapsedSeconds >= InitialCallupCall.AfterTaxiArrivalDelaySeconds(ctx.Aircraft.Callsign))
        )
        {
            InitialCallupCall.TryMake(ctx, location);
        }

        return false;
    }

    /// <summary>
    /// Whether the aircraft, released at its bar (any runway bar the crossing clearance covers or the hold inherits it at), keeps
    /// holding for the lead of the follow armed behind the crossing that releases this
    /// bar, until that lead is ahead of it on their shared route (<see cref="FollowingPhase.WaitsAtBarForLead"/>): a follow hold,
    /// not an unable, so the clearance stays armed for when it goes. Logs each start and end of a wait. A wait is re-judged once per
    /// second of the phase (<see cref="IsFirstTickOfASecond"/>) rather than every tick, since each judgement plans the follow afresh;
    /// a hold not yet waiting is judged at once, so it never starts across on a stale verdict.
    /// </summary>
    private bool HoldsForFollowLead(PhaseContext ctx)
    {
        if (_waitingForLead && !IsFirstTickOfASecond(ctx))
        {
            return true;
        }

        bool waiting =
            (ArmedFollow(ctx.Aircraft.Phases) is { } follow)
            && (ctx.GroundLayout is { } layout)
            && layout.Nodes.TryGetValue(_holdShort.NodeId, out GroundNode? bar)
            && (ctx.AircraftLookup?.Invoke(follow.TargetCallsign) is { IsOnGround: true } lead)
            && FollowingPhase.WaitsAtBarForLead(ctx, layout, lead, new FollowingPhase.BarCrossing(bar, follow.CrossingClearedRunways));
        if (waiting != _waitingForLead)
        {
            _waitingForLead = waiting;
            _saidWaitForLead = false;
            Log.LogDebug(
                "[HoldShort] {Callsign}: {State} at {Target}",
                ctx.Aircraft.Callsign,
                waiting ? "released at the bar, holding for the follow's lead to be ahead on the shared route" : "the follow's lead is ahead; going",
                _holdShort.TargetName ?? "unknown"
            );
        }

        if (waiting)
        {
            SayWaitIfForTrafficToPass(ctx);
        }

        return waiting;
    }

    /// <summary>
    /// Makes the wait's call (<see cref="AnnounceWaitForFollowLead"/>) the first time a judgement of the wait finds it a real wait
    /// for the traffic to pass (<see cref="IsWaitForTrafficToPass"/>): at its start, or on a later re-check once a lead that was
    /// moving away past the merge has stopped there. Once per wait (<see cref="_saidWaitForLead"/>, snapshotted).
    /// </summary>
    private void SayWaitIfForTrafficToPass(PhaseContext ctx)
    {
        if (_saidWaitForLead || (ArmedFollow(ctx.Aircraft.Phases) is not { } armed) || !IsWaitForTrafficToPass(ctx, armed.TargetCallsign))
        {
            return;
        }

        _saidWaitForLead = true;
        AnnounceWaitForFollowLead(ctx, armed.TargetCallsign);
    }

    /// <summary>
    /// Whether this tick carries the phase's elapsed time (<see cref="Phase.ElapsedSeconds"/>, snapshotted) past a whole second,
    /// so a replay or a restored snapshot re-judges on the same ticks as the original run.
    /// </summary>
    private bool IsFirstTickOfASecond(PhaseContext ctx) => Math.Floor(ElapsedSeconds) != Math.Floor(ElapsedSeconds - ctx.DeltaSeconds);

    /// <summary>
    /// Whether the wait for the follow's lead, as judged this time, is one the pilot reports
    /// (<see cref="FollowingPhase.IsWaitForTrafficToPass"/>): not the short wait for a lead moving away past the merge to get its
    /// tail clear. True when the lead or the layout is gone, so a wait never goes unreported for want of them.
    /// </summary>
    private static bool IsWaitForTrafficToPass(PhaseContext ctx, string leadCallsign) =>
        (ctx.GroundLayout is not { } layout)
        || (ctx.AircraftLookup?.Invoke(leadCallsign) is not { } lead)
        || FollowingPhase.IsWaitForTrafficToPass(ctx, layout, lead);

    /// <summary>
    /// Says, for a wait for the follow's lead, that the aircraft holds short for the traffic it follows
    /// (<see cref="PilotResponder.BuildHoldingShortForFollowTraffic"/>), routed to a student on ground or as an RPO's pilot speech or
    /// warning, and warns the controller that the aircraft, cleared across, is holding for that traffic. Once per wait
    /// (<see cref="SayWaitIfForTrafficToPass"/>).
    /// </summary>
    private void AnnounceWaitForFollowLead(PhaseContext ctx, string leadCallsign)
    {
        string runway = RunwayEndFacing(ctx.Aircraft, ctx.ActiveRunways);
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            PilotResponder.BuildHoldingShortForFollowTraffic(ctx.Aircraft, runway, leadCallsign),
            PilotResponder.SoloPositionsGround
        );
        ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} cleared across RWY {runway}, holding for the follow traffic");
    }

    /// <summary>
    /// The follow armed behind this hold: the first phase queued after it, past the crossing phases a crossing clearance put in
    /// front of the follow; null when that phase is no follow.
    /// </summary>
    private static FollowingPhase? ArmedFollow(PhaseList? phases) =>
        phases?.Phases.Skip(phases.CurrentIndex + 1).SkipWhile(static p => p is CrossingRunwayPhase).FirstOrDefault() as FollowingPhase;

    /// <summary>
    /// Says once that the aircraft, cleared across, is holding short for traffic beyond the far side
    /// (<see cref="RunwayCrossingFarSideGate.SayHoldingShortForTraffic"/>).
    /// </summary>
    private void SayHeldForTraffic(PhaseContext ctx)
    {
        if (_saidTrafficAhead)
        {
            return;
        }

        _saidTrafficAhead = true;
        RunwayCrossingFarSideGate.SayHoldingShortForTraffic(ctx, _holdShort.TargetName);
    }

    /// <summary>
    /// Where the after-taxi-arrival call is made from when this bar is the stop the spawn's preset taxi ends at
    /// (<see cref="AircraftGroundOps.PresetTaxiStop"/>) and the call is still owed: "holding short of C at T41W" at its
    /// taxiway hold short, "at spot 5" at its spot hold short. Null at any other bar, and always at a runway bar.
    /// </summary>
    private ReadyToTaxiLocation? PresetTaxiStopLocation(AircraftState aircraft)
    {
        AircraftGroundOps ground = aircraft.Ground;
        if ((ground.InitialCallup != InitialCallupPlan.AfterTaxiArrival) || ground.InitialCallupDecisionProcessed || ProtectsARunway)
        {
            return null;
        }

        return (ground.PresetTaxiStop is { } stop) && (_holdShort.TargetName is { Length: > 0 } target)
            ? MatchPresetTaxiStop(ground, stop, target)
            : null;
    }

    private static ReadyToTaxiLocation? MatchPresetTaxiStop(AircraftGroundOps ground, PresetTaxiStop stop, string target)
    {
        if (stop.Kind == PresetTaxiStopKind.Spot)
        {
            bool atTheSpot = HoldShortTarget.IsSpotTargetName(target) && SameName(HoldShortTarget.SpotNameOf(target), stop.Name);
            return atTheSpot ? ReadyToTaxiLocation.Spot(stop.Name) : null;
        }

        bool atTheBar =
            (stop.Kind == PresetTaxiStopKind.TaxiwayHoldShort)
            && SameName(target, stop.Name)
            && ((stop.OnTaxiway is null) || SameName(ground.CurrentTaxiway, stop.OnTaxiway));
        return atTheBar ? ReadyToTaxiLocation.HoldingShort(target, ground.CurrentTaxiway ?? "taxiway") : null;
    }

    private static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this bar protects a runway. A <see cref="HoldShortReason.RunwayCrossing"/> or
    /// <see cref="HoldShortReason.DestinationRunway"/> hold always does. An explicit <c>HS</c> bar usually
    /// names a taxiway (<c>F1</c>) or a spot (<c>$17</c>), but a controller can also spell a runway
    /// (<c>HS 1R</c>), so that case is decided on the target name. A route-incomplete hold is short of a taxiway,
    /// never a runway.
    /// </summary>
    public bool ProtectsARunway => ProtectsRunway(_holdShort);

    /// <summary>Whether <paramref name="holdShort"/> protects a runway, by the classification <see cref="ProtectsARunway"/> gives its hold.</summary>
    public static bool ProtectsRunway(HoldShortPoint holdShort) =>
        holdShort.Reason switch
        {
            HoldShortReason.RouteIncomplete => false,
            HoldShortReason.ExplicitHoldShort => IsRunwayTargetName(holdShort.TargetName),
            _ => true,
        };

    /// <summary>
    /// Whether a <see cref="HoldShortPoint.TargetName"/> reads as a runway. The route carries either a single
    /// designator (<c>1R</c>) or the <see cref="RunwayIdentifier"/> pair form (<c>01R/19L</c>), so every
    /// slash-separated end has to be a runway number — 01 to 36, with an optional L/C/R.
    ///
    /// <para>The shape is checked rather than round-tripped through <see cref="RunwayIdentifier.Parse"/>,
    /// which is permissive: it infers an opposite end for any string, so it answers "what runway would this
    /// be" and never "is this a runway at all". Taxiway names start with a letter, which is what keeps
    /// taxiways <c>C</c>, <c>L</c> and <c>R</c> out — note that <see cref="Commands.CommandParser"/>'s
    /// same-named helper would call all three runways, because it disambiguates runways from altitudes,
    /// where a bare trailing L/R/C can only be a runway.</para>
    ///
    /// <para>Two things keep this from over-matching, and both are load-bearing. The <c>$</c> spot guard comes
    /// first because spot names are numeric — <c>HS 17</c> is spot 17, which reads as runway 17 without it.
    /// And a parking stand such as OAK's <c>1C</c> would read as a runway too; it is unreachable only because
    /// <see cref="HoldShortTarget.TryParse"/> refuses parking tokens as hold-short targets. The layout's
    /// <see cref="GroundNodeType.RunwayHoldShort"/> node is the real ground truth, but
    /// <see cref="CanAcceptCommand"/> is handed no context to reach the layout through.</para>
    /// </summary>
    private static bool IsRunwayTargetName(string? targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || HoldShortTarget.IsSpotTargetName(targetName))
        {
            return false;
        }

        string[] ends = targetName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (ends.Length > 0) && ends.All(IsRunwayEnd);
    }

    /// <summary>
    /// Whether one designator is a runway end: 1 or 2 leading digits numbering 01-36, then at most a single
    /// L, C or R. Rejects a bare letter (taxiway <c>C</c>), a letter-then-digit (taxiway <c>F1</c>), a number
    /// outside the compass range, and anything with trailing characters.
    /// </summary>
    private static bool IsRunwayEnd(string end)
    {
        int digits = 0;
        while ((digits < end.Length) && char.IsAsciiDigit(end[digits]))
        {
            digits++;
        }

        if ((digits == 0) || (digits > 2) || (end.Length > digits + 1))
        {
            return false;
        }

        if (int.Parse(end[..digits]) is < 1 or > 36)
        {
            return false;
        }

        return (end.Length == digits) || (char.ToUpperInvariant(end[digits]) is 'L' or 'C' or 'R');
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        string destination = RunwayIdentifier.ToDisplayDesignator(_holdShort.TargetName ?? "runway");
        return cmd switch
        {
            CanonicalCommandType.CrossRunway => EndsItsRoute ? CommandAcceptance.Rejected(RejectionMessage()) : CommandAcceptance.ClearsPhase,
            CanonicalCommandType.LineUpAndWait => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ClearedForTakeoff => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ClearedTakeoffPresent => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldShort => CommandAcceptance.Allowed,
            // CLRWY is dispatched ahead of this gate (CommandDispatcher.TryApplyTowerCommand), which
            // replaces the phase with a ClearRunwayPhase; Allowed here keeps the phase intact for that
            // handler on any path that does reach the gate.
            CanonicalCommandType.ClearRunway => _holdShort.TailOverRunwayNodeId is not null
                ? CommandAcceptance.Allowed
                : CommandAcceptance.Rejected("CLRWY only applies when holding short of a taxiway with the tail over a runway"),
            CanonicalCommandType.Resume when _holdShort.EndsNoDestinationRoute => CommandAcceptance.Rejected(RejectionMessage()),
            CanonicalCommandType.Resume => _holdShort.Reason switch
            {
                HoldShortReason.DestinationRunway => CommandAcceptance.Rejected(
                    $"holding short of destination runway {destination} — RES does not apply (issue CTO or LUAW)"
                ),
                HoldShortReason.RouteIncomplete => CommandAcceptance.Rejected(RejectionMessage()),
                _ => CommandAcceptance.ClearsPhase,
            },
            // FOLLOWG from a bar that protects no runway — the "holding short at a taxiway or spot bar" case
            // COMMANDS.md lists. GroundCommandHandler.TryFollow does the replacing: it clears Ground.Hold and
            // swaps the phase list for a FollowingPhase. At a bar that DOES protect a runway the follow is armed
            // instead: TryFollow keeps this phase and queues the FollowingPhase behind it, because following a
            // leader is not a crossing clearance — 7110.65 §3-7-2d gives the crossing its own "cross (runway)" —
            // so the aircraft stays held until CROSS (or LUAW/CTO at the departure bar).
            CanonicalCommandType.FollowGround => ProtectsARunway ? CommandAcceptance.Allowed : CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected(RejectionMessage()),
        };
    }

    /// <summary>
    /// What is still available from this bar. The two bars offer different sets, so naming the wrong one sends
    /// the controller looking for a command that cannot apply: a runway bar has no RES (it takes a crossing or a
    /// takeoff clearance to leave, and a FOLLOWG there only arms the follow for after the crossing), while a
    /// taxiway or spot bar has both. The end of a route that could not reach its destination takes only a new
    /// TAXI that names the taxiway the route needs.
    /// </summary>
    private string RejectionMessage()
    {
        if (EndsItsRoute)
        {
            return RouteIncompleteMessage(_holdShort.TargetName);
        }

        string target = _displayEnd ?? HoldShortTarget.Describe(_holdShort.TargetName ?? "the runway");
        return ProtectsARunway
            ? $"aircraft is holding short of {target}; only CROSS/LUAW/CTO/HSC, FOLLOWG, a new TAXI, or DEL apply"
            : $"aircraft is holding short of {target}; only RES/FOLLOWG/CROSS/HSC, a new TAXI, or DEL apply";
    }

    /// <summary>
    /// True at the hold ending a route that goes nowhere past it: a route that could not reach its destination
    /// (<see cref="HoldShortReason.RouteIncomplete"/>), or one cut short of a taxiway it never drives
    /// (<see cref="HoldShortPoint.EndsNoDestinationRoute"/>). Neither <c>RES</c> nor <c>CROSS</c> releases it.
    /// </summary>
    private bool EndsItsRoute => (_holdShort.Reason == HoldShortReason.RouteIncomplete) || _holdShort.EndsNoDestinationRoute;

    /// <summary>
    /// The refusal for a command the bar ending an incomplete route does not take (<see cref="HoldShortReason.RouteIncomplete"/>):
    /// only a new TAXI that names the taxiway the route needs moves the aircraft on.
    /// </summary>
    public static string RouteIncompleteMessage(string? targetName) =>
        $"holding short of {targetName} where the route issued ends — issue a TAXI that includes {targetName}";

    protected override List<ClearanceRequirement> CreateRequirements() => [new ClearanceRequirement { Type = ClearanceType.RunwayCrossing }];

    public override PhaseDto ToSnapshot() =>
        new HoldingShortPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            HoldShortNodeId = _holdShort.NodeId,
            RunwayId = _holdShort.TargetName ?? string.Empty,
            Reason = _holdShort.Reason,
            HasAnnouncedReady = _hasAnnouncedReady,
            SaidTrafficAhead = _saidTrafficAhead ? true : null,
            WaitingForLead = _waitingForLead ? true : null,
            SaidWaitForLead = _saidWaitForLead ? true : null,
            DisplayEnd = _displayEnd,
        };

    public static HoldingShortPhase FromSnapshot(HoldingShortPhaseDto dto)
    {
        var holdShort = new HoldShortPoint
        {
            NodeId = dto.HoldShortNodeId,
            Reason = dto.Reason ?? HoldShortReason.RunwayCrossing,
            TargetName = string.IsNullOrEmpty(dto.RunwayId) ? null : dto.RunwayId,
        };

        var phase = new HoldingShortPhase(holdShort)
        {
            _hasAnnouncedReady = dto.HasAnnouncedReady,
            _saidTrafficAhead = dto.SaidTrafficAhead ?? false,
            _waitingForLead = dto.WaitingForLead ?? false,
            _saidWaitForLead = dto.SaidWaitForLead ?? false,
            _displayEnd = dto.DisplayEnd,
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
        };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
