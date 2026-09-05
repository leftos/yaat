using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Command entry points and the aircraft mutations they drive.
public sealed partial class SimulationEngine
{
    public CommandResult SendCommand(string callsign, string command)
    {
        var aircraft = FindAircraft(callsign);
        if (aircraft is null)
        {
            return new CommandResult(false, $"Aircraft '{callsign}' not found");
        }

        var parseResult = CommandParser.ParseCompound(command, aircraft.FlightPlan.Route);
        if (!parseResult.IsSuccess)
        {
            return new CommandResult(false, $"Failed to parse command: {command} — {parseResult.Reason}");
        }

        return DispatchLiveCommand(aircraft, parseResult.Value!, DispatchOrigin.Human).Result;
    }

    /// <summary>
    /// Dispatches one command from an AI position exactly the way the live server routes a command from that
    /// position's synthetic connection: track verbs (and any <c>AS</c>-prefixed command) through the track engine under
    /// the prefix's identity, coordination verbs refused (they mutate server-only state), everything else through the
    /// aviation dispatcher under <see cref="DispatchOrigin.ControllerAi"/>. A successful command is appended to the
    /// action log with the AI connection id, so a recording of an AI-driven session replays it identically.
    /// </summary>
    public CommandResult DispatchAiCommand(AiPositionConfig from, string callsign, string command)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return new CommandResult(false, "No scenario loaded");
        }

        var aircraft = FindAircraft(callsign);
        if (aircraft is null)
        {
            return new CommandResult(false, $"Aircraft '{callsign}' not found");
        }

        var connectionId = AiConnectionId.Format(from.PositionId);
        CommandResult result;
        double? reactionDelaySeconds = null;
        var asPrefixCheck = TrackResolver.ExtractAsPrefix(command);
        var firstParse = CommandParser.Parse(asPrefixCheck.Remainder);
        bool isTrack =
            firstParse.IsSuccess
            && firstParse.Value is not null
            && (TrackEngine.IsTrackCommand(firstParse.Value) || asPrefixCheck.AsOverrideTcp is not null);
        if (isTrack)
        {
            result =
                _replayTrackApplier.Apply(command, aircraft, connectionId, scenario) ?? new CommandResult(false, $"'{command}' dispatched nothing");
        }
        else
        {
            var (kind, _, _) = RecordedCommandClassifier.Classify(command);
            if (kind != RecordedCommandKind.Compound && kind != RecordedCommandKind.SayOrShow)
            {
                return new CommandResult(false, $"'{command}' ({kind}) has no engine-side handler; only the live server dispatches it");
            }

            var parseResult = CommandParser.ParseCompound(command, aircraft.FlightPlan.Route);
            if (!parseResult.IsSuccess)
            {
                return new CommandResult(false, $"Failed to parse command: {command} — {parseResult.Reason}");
            }

            (result, reactionDelaySeconds) = DispatchLiveCommand(aircraft, parseResult.Value!, DispatchOrigin.ControllerAi);
        }

        if (result.Success)
        {
            RecordAction(
                new RecordedCommand(scenario.ElapsedSeconds, callsign, command, "AI", connectionId) { ReactionDelaySeconds = reactionDelaySeconds }
            );
        }

        return result;
    }

    /// <summary>
    /// The live dispatch pipeline shared by every controller-issued command: the pilot-reaction deferral, else the
    /// dispatcher, then <see cref="ApplyPostDispatch"/>. Returns the sampled reaction delay (null when none) so the
    /// caller can bake it into the recorded command.
    /// </summary>
    private (CommandResult Result, double? ReactionDelaySeconds) DispatchLiveCommand(
        AircraftState aircraft,
        CompoundCommand compound,
        DispatchOrigin origin
    )
    {
        bool soloTrainingMode = Scenario?.SoloTrainingMode ?? false;

        // Pilot-reaction delay (command-run delay): when active, defer the whole dispatch by a sampled
        // number of seconds and acknowledge immediately so the controller knows the command landed and
        // the sim isn't frozen. The aircraft begins complying when the deferral fires.
        CommandResult result;
        var reactionDelay = TryDeferCommandForReaction(aircraft, compound, origin);
        if (reactionDelay is double reactionSeconds)
        {
            // In solo training mode the student is the pilot's only audience: showing the exact sampled
            // delay would reveal precisely how long the aircraft will take to comply. Suppress the
            // acknowledgement entirely — the pilot's read-back (queued below) is the acknowledgement.
            result = soloTrainingMode
                ? new CommandResult(true, null)
                : new CommandResult(true, $"Pilot complying in {(int)Math.Round(reactionSeconds)}s");
        }
        else
        {
            var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
            var dispatchCtx = new DispatchContext(
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
                IsScenarioScripted: origin == DispatchOrigin.ControllerAi
            );
            result = CommandDispatcher.DispatchCompound(compound, aircraft, dispatchCtx);
        }

        ApplyPostDispatch(aircraft, compound, result, origin);

        return (result, reactionDelay);
    }

    /// <summary>
    /// Post-dispatch bookkeeping for a controller-issued command: two-way-comms registration and, in
    /// solo-training mode, evaluator scoring, pending-request resolution, frequency-gate release, the
    /// "unable" response on rejection, and the pilot read-back.
    ///
    /// Both hosts must call this after dispatching a user-issued command — <see cref="SendCommand"/>
    /// for the standalone engine and <c>RoomEngine.HandleStandardCmd</c> for the live server. Keeping
    /// it in one place is what stops the two from drifting: the pending-request resolution and the
    /// frequency-gate release lived only in <see cref="SendCommand"/> and were therefore dark on the
    /// live server, so pilots re-announced "ready for departure" every 120 s forever (issue #307).
    ///
    /// The read-back hook lives here, on the user-issued path only, so deferred / preset / replay
    /// dispatches don't re-fire read-backs.
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

    /// <summary>
    /// If a command-run delay is active, enqueue <paramref name="compound"/> as a pilot-reaction
    /// deferred dispatch and return the delay in seconds; otherwise return null and the caller
    /// dispatches immediately. The delay simulates the time a pilot needs to set up the FMC / autopilot
    /// after the controller issues an instruction.
    ///
    /// Commands carrying explicit leading timing — a WAIT/WAITD or a BEHIND give-way condition — are NOT
    /// reaction-delayed: the controller's explicit timing already models the wait, and those produce
    /// their own deferred dispatch inside <see cref="CommandDispatcher.DispatchCompound"/>.
    ///
    /// Sampling draws from <see cref="SimulationWorld.ReactionDelayRng"/> (never the shared RNG) so it
    /// can't perturb replay-critical emergent events. The returned value is the actual delay applied
    /// (after the order-preserving clamp); the server bakes it into the recorded command so replays
    /// reproduce it exactly rather than re-sampling. The deferral carries <paramref name="origin"/> as
    /// its <c>IsScenarioScripted</c> flag, so an AI command that fires after the delay still never marks
    /// student contact.
    /// </summary>
    public double? TryDeferCommandForReaction(AircraftState aircraft, CompoundCommand compound, DispatchOrigin origin)
    {
        var scenario = Scenario;
        if (scenario is null || scenario.CommandRunDelayMaxSeconds <= 0)
        {
            return null;
        }

        if (HasExplicitLeadingTiming(compound))
        {
            return null;
        }

        // Pure frequency-change / radio-contact commands are not reaction-delayed: the AIM (4-2-3)
        // expects a pilot to switch frequency "as soon as possible", and holding the aircraft on the
        // current frequency for several seconds would teach a backwards habit. A mixed compound
        // (e.g. "FH 270; CON TWR") is still delayed as a whole — only a purely-comm compound is exempt.
        if (IsPureCommCompound(compound))
        {
            return null;
        }

        int max = scenario.CommandRunDelayMaxSeconds;
        int min = Math.Clamp(scenario.CommandRunDelayMinSeconds, 0, max);
        int sampled = min >= max ? max : World.ReactionDelayRng.Next(min, max + 1);

        // Preserve issue order: a command issued later must never start complying before one issued
        // earlier. Clamp this command's delay so it fires no sooner than any already-pending reaction
        // deferral on the same aircraft (ProcessDeferredDispatches applies same-tick expiries FIFO).
        double clampFloor = 0;
        foreach (var pending in aircraft.DeferredDispatches)
        {
            if (pending.IsReactionDelay && pending.RemainingSeconds > clampFloor)
            {
                clampFloor = pending.RemainingSeconds;
            }
        }

        double seconds = Math.Max(sampled, clampFloor);
        // An AI-controller command that fires after its reaction delay is still not the student establishing contact.
        aircraft.DeferredDispatches.Add(
            new DeferredDispatch(seconds, compound)
            {
                SourceText = compound.SourceText,
                IsReactionDelay = true,
                IsScenarioScripted = origin == DispatchOrigin.ControllerAi,
            }
        );
        return seconds;
    }

    private static bool HasExplicitLeadingTiming(CompoundCommand compound)
    {
        if (compound.Blocks.Count == 0)
        {
            return false;
        }

        var first = compound.Blocks[0];
        if (first.Condition is GiveWayCondition)
        {
            return true;
        }

        foreach (var cmd in first.Commands)
        {
            if (cmd is WaitCommand or WaitDistanceCommand)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPureCommCompound(CompoundCommand compound)
    {
        bool hasAny = false;
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                hasAny = true;
                if (cmd is not (ContactCommand or FrequencyChangeApprovedCommand or AcknowledgePilotContactCommand))
                {
                    return false;
                }
            }
        }

        return hasAny;
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
