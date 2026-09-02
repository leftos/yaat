using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft holds short of a runway or explicit hold-short point.
/// Speed=0. Generates a notification on start.
/// Clearance-gated: completes when RunwayCrossing clearance is satisfied
/// (via CROSS, LUAW, or CTO command).
/// </summary>
public sealed class HoldingShortPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldingShortPhase");

    private HoldShortPoint _holdShort;
    private bool _hasAnnouncedReady;

    public HoldingShortPhase(HoldShortPoint holdShort)
    {
        _holdShort = holdShort;
    }

    public HoldShortPoint HoldShort => _holdShort;

    /// <summary>
    /// Re-points this phase at the taxi route's own <see cref="HoldShortPoint"/> after a snapshot restore.
    ///
    /// Live, the phase is constructed with the route's instance, so the two share one object — clearing the hold-short
    /// through the phase updates the route, and the phase can see route-only fields such as
    /// <see cref="HoldShortPoint.TailOverRunwayNodeId"/>. The phase snapshot carries only the node id, runway and
    /// reason, so restoring rebuilds a detached copy and both of those properties are lost. Called by
    /// <see cref="AircraftState.FromSnapshot"/> once the route is available.
    /// </summary>
    internal void RebindHoldShort(HoldShortPoint holdShort)
    {
        _holdShort = holdShort;
    }

    public override string Name => _holdShort.TargetName is { } target ? $"Holding Short {HoldShortTarget.Describe(target)}" : "Holding Short";

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

        // Generate notification
        string target = HoldShortTarget.Describe(_holdShort.TargetName ?? "unknown");
        string taxiway = ctx.Aircraft.Ground.CurrentTaxiway ?? "taxiway";
        string label = _holdShort.Reason == HoldShortReason.ExplicitHoldShort ? $"holding short of {target}" : $"holding short runway {target}";
        string warningText = $"{ctx.Aircraft.Callsign} {label} at {taxiway}";
        var speechText =
            _holdShort.Reason == HoldShortReason.ExplicitHoldShort
                ? PilotResponder.BuildHoldingShortTaxi(ctx.Aircraft, label, taxiway)
                : PilotResponder.BuildHoldingShortCrossing(ctx.Aircraft, ResolveSpokenCrossingRunway(ctx, target));
        PilotResponder.RouteRpoTransmission(ctx.Aircraft, ctx.SoloTrainingMode, ctx.RpoShowPilotSpeech, speechText.Tts, warningText);

        // Tail-over-runway (issue #172 W3): the aircraft holds at the taxiway line with its tail still
        // over the runway behind it. Protecting the runway is the controller's job (7110.65 3-7-4), not
        // something the pilot reports — so surface it on the controller/terminal warning lane only,
        // never as a pilot transmission, and never with the combined "01L/19R" in a pilot's mouth.
        if (
            _holdShort.TailOverRunwayNodeId is { } tailNode
            && ctx.GroundLayout is { } groundLayout
            && groundLayout.Nodes.TryGetValue(tailNode, out var tailRwyNode)
            && tailRwyNode.RunwayId is { } tailRwy
        )
        {
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} not clear of RWY {tailRwy.ToDisplayString()} — tail over the hold-short bars");
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
            var facilityCallName = PilotResponder.ResolveAnsweringCallName(answering, "TWR", "tower");
            var line = PilotResponder.BuildHoldingShortReady(ctx.Aircraft, runwayId, facilityCallName);
            PilotResponder.QueueSoloPilotTransmission(ctx.Aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
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
    /// For a runway-crossing hold-short the spoken report names only the single runway end whose
    /// threshold the aircraft is nearest to (e.g. "runway one five" rather than the combined
    /// "one five / three three") — the pilot refers to the side it is about to cross
    /// (<see cref="RunwayCrossingEnd.Nearest"/>, which names the first end when the thresholds cannot be
    /// resolved). Falls back to <paramref name="displayDesignator"/> (the combined form used on the
    /// controller-facing warning) when the hold-short is not a runway crossing.
    /// </summary>
    private string ResolveSpokenCrossingRunway(PhaseContext ctx, string displayDesignator)
    {
        if (_holdShort.Reason != HoldShortReason.RunwayCrossing || _holdShort.TargetName is not { Length: > 0 } combined)
        {
            return displayDesignator;
        }

        return RunwayCrossingEnd.Nearest(ctx.Aircraft, combined, ctx.GroundLayout);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;

        // Check if clearance has been satisfied
        foreach (var req in Requirements)
        {
            if (req.IsSatisfied)
            {
                _holdShort.IsCleared = true;
                Log.LogDebug("[HoldShort] {Callsign}: cleared at {Target}", ctx.Aircraft.Callsign, _holdShort.TargetName ?? "unknown");
                return true;
            }
        }

        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.CrossRunway => CommandAcceptance.ClearsPhase,
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
            CanonicalCommandType.Resume => _holdShort.Reason == HoldShortReason.DestinationRunway
                ? CommandAcceptance.Rejected(
                    $"holding short of destination runway {RunwayIdentifier.ToDisplayDesignator(_holdShort.TargetName ?? "runway")} — RES does not apply (issue CTO or LUAW)"
                )
                : CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected(
                $"aircraft is holding short of {HoldShortTarget.Describe(_holdShort.TargetName ?? "the runway")}; only CROSS/LUAW/CTO/HSC, a new TAXI, or DEL apply"
            ),
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [new ClearanceRequirement { Type = ClearanceType.RunwayCrossing }];
    }

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
        };

    public static HoldingShortPhase FromSnapshot(HoldingShortPhaseDto dto)
    {
        var holdShort = new HoldShortPoint
        {
            NodeId = dto.HoldShortNodeId,
            Reason = dto.Reason ?? HoldShortReason.RunwayCrossing,
            TargetName = string.IsNullOrEmpty(dto.RunwayId) ? null : dto.RunwayId,
        };

        var phase = new HoldingShortPhase(holdShort) { _hasAnnouncedReady = dto.HasAnnouncedReady };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
