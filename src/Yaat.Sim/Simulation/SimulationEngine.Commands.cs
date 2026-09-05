using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Command entry points and the aircraft mutations they drive. Routing is the ActionRouter's (Simulation/Actions/);
// this file holds the entry points that call it and the bodies its aviation arm shares with every run kind.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// A controller command from the standalone engine's one controller (a test, the solo client), routed through
    /// <see cref="Actions"/> under <see cref="LocalConnectionId"/> and recorded when the run records.
    /// </summary>
    public CommandResult SendCommand(string callsign, string command) =>
        Actions.Issue(new ActionInput(callsign, command, LocalConnectionId, Initials: "", Baked: null)).Result;

    /// <summary>
    /// Dispatches one command from an AI position exactly the way the live server routes a command from that
    /// position's synthetic connection: through <see cref="Actions"/> under <see cref="AiConnectionId"/>, so track verbs
    /// run under the position's identity and aviation verbs under <see cref="DispatchOrigin.ControllerAi"/>. The command
    /// is recorded with the AI connection id, accepted or not, so a recording of an AI-driven session replays it identically.
    /// </summary>
    public CommandResult DispatchAiCommand(AiPositionConfig from, string callsign, string command)
    {
        if (Scenario is null)
        {
            return ActionRefusals.NoScenario();
        }

        return Actions.Issue(new ActionInput(callsign, command, AiConnectionId.Format(from.PositionId), Initials: "AI", Baked: null)).Result;
    }

    /// <summary>
    /// Enqueues <paramref name="compound"/> as a pilot-reaction deferral firing in <paramref name="seconds"/>; the
    /// aircraft begins complying when it fires through <c>ProcessDeferredDispatches</c>. The seconds come from
    /// <see cref="ReactionDelayPolicy.Decide"/> — sampled live, baked on replay. The deferral carries the origin as its
    /// <c>IsScenarioScripted</c> flag, so an AI command that fires after the delay still never marks student contact.
    /// </summary>
    public void DeferForReaction(AircraftState aircraft, CompoundCommand compound, double seconds, DispatchOrigin origin)
    {
        aircraft.DeferredDispatches.Add(
            new DeferredDispatch(seconds, compound)
            {
                SourceText = compound.SourceText,
                IsReactionDelay = true,
                IsScenarioScripted = origin == DispatchOrigin.ControllerAi,
            }
        );
    }

    /// <summary>The dispatch context for a controller-issued command against <paramref name="aircraft"/> on this engine.</summary>
    internal DispatchContext BuildDispatchContext(AircraftState aircraft, bool isScenarioScripted)
    {
        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        return new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            () => World.GetSnapshot(),
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false,
            Scenario?.SoloTrainingMode ?? false,
            Scenario?.RpoShowPilotSpeech ?? false,
            AddTerminalEntry,
            Scenario?.ArtccConfig,
            Scenario?.ElapsedSeconds ?? 0,
            PreserveConditionals: false,
            IsScenarioScripted: isScenarioScripted
        );
    }

    /// <summary>
    /// Post-dispatch bookkeeping for a controller-issued command: two-way-comms registration and, in
    /// solo-training mode, evaluator scoring, pending-request resolution, frequency-gate release, the
    /// "unable" response on rejection, and the pilot read-back.
    ///
    /// The router's aviation arm calls this after every dispatch — fresh or recorded — so the post-dispatch state is
    /// sim state on every run kind. Keeping it in one place is what stops the entry points from drifting: the
    /// pending-request resolution and the frequency-gate release once lived only in <see cref="SendCommand"/> and were
    /// therefore dark on the live server, so pilots re-announced "ready for departure" every 120 s forever (issue #307),
    /// and a replay that skipped the read-back gates left its frequency in a state the live session never had.
    ///
    /// Deferred and preset dispatches do not come through here, so they never re-fire read-backs.
    ///
    /// <paramref name="origin"/> decides which of these are the student's: only a
    /// <see cref="DispatchOrigin.Human"/> dispatch registers controller contact and is evaluator-scored;
    /// an AI-controller dispatch still resolves the pilot's pending request, releases the frequency gate,
    /// and gets the read-back / "unable" whenever anyone answers pilots (<c>PilotContacts.AnyAnswering</c>).
    /// </summary>
    public void ApplyPostDispatch(AircraftState aircraft, CompoundCommand compound, CommandResult result, DispatchOrigin origin)
    {
        bool soloTrainingMode = Scenario?.SoloTrainingMode ?? false;
        // The pilot-voice side (request resolution, frequency gates, read-backs, "unable") runs whenever someone answers
        // pilots — the solo student or an AI position (identical to solo mode when the AI is off). Student-only
        // bookkeeping — two-way-comms registration and evaluator scoring — never runs for an AI-controller dispatch.
        bool answering = Scenario?.PilotContacts.AnyAnswering ?? false;
        bool human = origin == DispatchOrigin.Human;
        double elapsedSeconds = Scenario?.ElapsedSeconds ?? 0;

        if (result.Success)
        {
            if (human)
            {
                Pilot.PilotInitialContactEligibility.RegisterControllerContact(aircraft, Scenario, compound);
            }

            if (human && soloTrainingMode)
            {
                SoloTrainingEvaluator.RecordControllerCommand(aircraft, compound, elapsedSeconds, World.GetSnapshot());
            }

            if (answering)
            {
                PilotRequestTracker.ApplyControllerResponse(aircraft, compound, elapsedSeconds);
                // The controller has just spoken to this aircraft, so the
                // awaiting-controller-response gate (if it was set after this pilot's
                // last proactive call) clears. Commands that produce a readback also
                // arm the readback gate just below; both gates are independent.
                World.AcknowledgeControllerResponse(aircraft.Callsign);
            }
        }
        else if (answering)
        {
            QueueSoloUnableIfNeeded(aircraft, result);
        }

        if (result.Success && answering)
        {
            var activityLevel = World.ActiveFrequency.GetActivityLevel(elapsedSeconds);
            var readback = Yaat.Sim.Pilot.PilotResponder.BuildReadbackAsApplied(
                compound,
                result.EffectiveCommand,
                aircraft,
                PilotPersonality.Varied,
                activityLevel
            );
            if (readback is not null)
            {
                World.ExpectPilotReadback(aircraft.Callsign, elapsedSeconds);
                Yaat.Sim.Pilot.PilotResponder.QueueSoloPilotTransmission(
                    aircraft,
                    readback,
                    Yaat.Sim.Pilot.PilotTransmissionKind.Readback,
                    Yaat.Sim.Pilot.PilotResponder.SourceResponse
                );
            }
        }
    }

    private static void QueueSoloUnableIfNeeded(AircraftState aircraft, CommandResult result)
    {
        if (result.RejectedCommandType is not { } rejectedType)
        {
            return;
        }

        var definition = CommandRegistry.Get(rejectedType);
        if (definition?.ProducesPilotUnable != true)
        {
            return;
        }

        var transmission = PilotResponder.BuildUnable(aircraft, result.Message);
        PilotResponder.QueueSoloPilotTransmission(aircraft, transmission, PilotTransmissionKind.Readback, PilotResponder.SourceResponse);
    }

    public void WarpAircraft(string callsign, double latitude, double longitude, TrueHeading trueHeading)
    {
        var aircraft = FindAircraft(callsign);
        if (aircraft is null)
        {
            return;
        }

        // Clear stale state
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }
        aircraft.Ground.AssignedTaxiRoute = null;
        aircraft.Ground.Hold = null;
        aircraft.Queue.Blocks.Clear();

        // Place on ground
        aircraft.Position = new LatLon(latitude, longitude);
        aircraft.TrueHeading = trueHeading;
        aircraft.TrueTrack = trueHeading;
        aircraft.IndicatedAirspeed = 0;
        aircraft.IsOnGround = true;
        aircraft.Targets.TargetSpeed = 0;

        // Install ground-idle phase so subsequent commands have phase context
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));

        aircraft.Ground.Layout = ResolveGroundLayout(aircraft);
    }

    public void AmendFlightPlan(string callsign, FlightPlanAmendment amendment)
    {
        if (!Callsign.IsValid(callsign))
        {
            _logger.LogWarning("AmendFlightPlan rejected invalid callsign '{Callsign}'", callsign);
            return;
        }

        var ac = FindAircraft(callsign);
        if (ac is null)
        {
            return;
        }

        bool wasFiled = ac.FlightPlan.HasFlightPlan;

        if (amendment.AircraftType is not null)
        {
            // Filed FP type only — never the actual physical type. Tower Cab (out-the-window)
            // keeps reading AircraftState.AircraftType, which is fixed at spawn.
            ac.FlightPlan.AircraftType = amendment.AircraftType;
        }
        if (amendment.EquipmentSuffix is not null)
        {
            ac.FlightPlan.EquipmentSuffix = amendment.EquipmentSuffix;
        }
        if (amendment.IcaoEquipmentCodes is not null)
        {
            ac.FlightPlan.IcaoEquipmentCodes = amendment.IcaoEquipmentCodes;
        }
        if (amendment.Departure is not null)
        {
            ac.FlightPlan.Departure = amendment.Departure;
        }
        if (amendment.Destination is not null)
        {
            ac.FlightPlan.Destination = amendment.Destination;
        }
        if (amendment.CruiseSpeed is not null)
        {
            ac.FlightPlan.CruiseSpeed = amendment.CruiseSpeed.Value;
        }
        if (amendment.Altitude is not null)
        {
            ac.FlightPlan.Altitude = amendment.Altitude;
        }
        if (amendment.FlightRules is not null)
        {
            ac.FlightPlan.FlightRules = amendment.FlightRules;
        }
        if (amendment.Route is not null)
        {
            ac.FlightPlan.Route = amendment.Route;
            DepartureClearanceHandler.RefreshStoredDepartureClearance(ac);
            DepartureClearanceHandler.RefreshPendingInitialClimbPhases(ac);
        }
        if (amendment.Remarks is not null)
        {
            ac.FlightPlan.Remarks = amendment.Remarks;
            // Remarks are canonical for voice type: a /v//r//t/ marker (or its absence = full voice) drives
            // the CRC voice-type field. A VATSIM operational convention, not an FAA flight-plan field.
            ac.Voice.Type = FlightPlanVoice.ParseVoiceType(ac.FlightPlan.Remarks);
        }
        if (amendment.Scratchpad1 is not null)
        {
            ac.Stars.Scratchpad1 = amendment.Scratchpad1;
            ac.Stars.WasScratchpad1Cleared = string.IsNullOrEmpty(amendment.Scratchpad1);
        }
        if (amendment.Scratchpad2 is not null)
        {
            ac.Stars.Scratchpad2 = amendment.Scratchpad2;
        }
        if (amendment.BeaconCode is not null)
        {
            // Amend only the *assigned* beacon code, never the code the transponder transmits — a
            // controller assigns a beacon; the pilot keeps squawking the current code until told to
            // squawk the new one (matching the auto-assign-on-filing branch below). The resulting
            // beacon mismatch is shown on the data block until the pilot complies.
            ac.Transponder.AssignCode(amendment.BeaconCode.Value, amendment.BeaconAssignedByFacilityId, amendment.BeaconAssignedBySectorId);
        }

        // Resolve ground layout if departure/destination changed
        if (amendment.Departure is not null || amendment.Destination is not null)
        {
            ac.Ground.Layout = ResolveGroundLayout(ac);
        }

        // Editing a flight plan via the Flight Plan Editor on a radar-only (no-plan) target
        // files the plan: establish it and issue a discrete beacon code (VFR draws from the
        // VFR bank, IFR from the IFR bank). Don't flip Transponder.Code — the pilot keeps
        // squawking their current code until the controller issues SQ. This is the single
        // owner of "filing establishes the plan + assigns a beacon"; the typed DA/VP/NEW
        // create path reaches it through its own AmendFlightPlan call.
        if (!wasFiled)
        {
            ac.FlightPlan.HasFlightPlan = true;
            if (ac.Transponder.AssignedCode == 0)
            {
                // Attribute the filing draw to whatever the amendment carries: the ERAM VP path stamps
                // its sector (7110.65 §5-2-7.a); instructor/STARS filing paths carry null.
                ac.Transponder.AssignCode(
                    BeaconCodePool.AssignNextCode(ac.FlightPlan.IsVfr),
                    amendment.BeaconAssignedByFacilityId,
                    amendment.BeaconAssignedBySectorId
                );
            }
        }

        // Bump the revision counter so the strip can render the new value.
        // CRC displays revision regardless of which fields changed — the counter
        // is a "has been edited" signal, not a per-field diff.
        ac.FlightPlan.RevisionNumber++;
    }

    /// <summary>
    /// Releases the aircraft's current assigned beacon code back to the pool and draws a fresh
    /// discrete code (VFR bank for VFR, IFR bank for IFR). Does not flip <c>Transponder.Code</c> —
    /// the pilot keeps squawking their current code until the controller issues <c>SQ</c>. The
    /// assigner is the acting ERAM sector when the request came from an ERAM position (bare
    /// <c>QB</c>), else null. Returns the new assigned code, or 0 if the aircraft is unknown.
    /// </summary>
    public uint RequestNewBeaconCode(string callsign, string? assignedByFacilityId, string? assignedBySectorId)
    {
        var ac = FindAircraft(callsign);
        if (ac is null)
        {
            return 0;
        }

        BeaconCodePool.Release(ac.Transponder.AssignedCode);
        var newCode = BeaconCodePool.AssignNextCode(ac.FlightPlan.IsVfr);
        ac.Transponder.AssignCode(newCode, assignedByFacilityId, assignedBySectorId);
        return newCode;
    }
}
