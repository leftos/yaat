using Microsoft.Extensions.Logging;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

/// <summary>
/// What <see cref="SimulationEngine.ApplyPostDispatch"/> needs that must be taken <em>before</em> a dispatch runs: the
/// Class B the pilot was waiting on (a pattern entry or approach clearance replaces the boundary hold it reads), and the
/// read-backs the aircraft already had pending. The post-dispatch step arms the frequency's read-back gate only for a
/// read-back the dispatch itself queued — one pending afterwards that is not in <paramref name="Readbacks"/> — so a
/// read-back a pilot queued on its own neither arms the gate nor satisfies the one armed for an instruction's read-back.
/// The set compares transmissions by reference, since <see cref="PilotTransmission"/> is a value-equal record.
/// </summary>
/// <param name="BravoWait">The Class B the pilot was waiting on before the dispatch.</param>
/// <param name="Readbacks">The read-backs pending for the aircraft when the dispatch began.</param>
public sealed record PreDispatchCapture(BravoClearanceWait? BravoWait, IReadOnlySet<PilotTransmission> Readbacks)
{
    /// <summary>Takes both captures for <paramref name="aircraft"/>: what the dispatch is about to change behind it.</summary>
    public static PreDispatchCapture BeforeDispatch(AircraftState aircraft) =>
        new(
            ImplicitBravoClearance.CaptureWait(aircraft),
            new HashSet<PilotTransmission>(
                aircraft.PendingPilotTransmissions.Where(t => t.Kind == PilotTransmissionKind.Readback),
                ReferenceEqualityComparer.Instance
            )
        );
}

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
    ///
    /// <para>
    /// Deferring replaces the dispatch, so this performs the issue-time supersede
    /// <see cref="CommandDispatcher.DispatchCompound"/> would have done: a fresh immediate compound cancels the
    /// controller-authored WAIT / BEHIND deferrals pending on the aircraft, exactly as the dispatcher clears them.
    /// Sibling reaction deferrals are kept, so several commands issued in a row still fire in issue order. A
    /// conditional incoming (<see cref="CommandDispatcher.IsConditionalIncoming"/>) is purely additive and cancels
    /// nothing — the same rule the dispatcher applies.
    /// </para>
    /// </summary>
    public void DeferForReaction(AircraftState aircraft, CompoundCommand compound, double seconds, DispatchOrigin origin)
    {
        if (!CommandDispatcher.IsConditionalIncoming(compound))
        {
            aircraft.DeferredDispatches.RemoveAll(d => !d.IsReactionDelay);
        }

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
    internal DispatchContext BuildDispatchContext(AircraftState aircraft, bool isScenarioScripted, string? facilityHint)
    {
        AirportGroundLayout? groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        return new DispatchContext
        {
            GroundLayout = groundLayout,
            Rng = World.Rng,
            Weather = World.Weather,
            FindAircraft = FindAircraft,
            ListAircraft = () => World.GetSnapshot(),
            ValidateDctFixes = Scenario?.ValidateDctFixes ?? true,
            AutoCrossRunwayMode = Scenario?.AutoCrossRunwayMode ?? AutoCrossRunwayMode.Off,
            SoloTrainingMode = Scenario?.SoloTrainingMode ?? false,
            SoloRpoCommandsAllowed = Scenario?.SoloRpoCommandsAllowed ?? false,
            RpoShowPilotSpeech = Scenario?.RpoShowPilotSpeech ?? false,
            TerminalEmitter = AddTerminalEntry,
            ArtccConfig = Scenario?.ArtccConfig,
            AsdexSafetyLogicConfigs = Scenario?.AsdexSafetyLogicConfigs ?? AsdexSafetyLogicSeed.Empty,
            ToweredAirports = Scenario?.ToweredAirports ?? ToweredAirports.Empty,
            ActiveRunways = Scenario?.ActiveRunways ?? ActiveRunways.Empty,
            ScenarioElapsedSeconds = Scenario?.ElapsedSeconds ?? 0,
            SessionStartUtc = Scenario?.SessionStartUtc ?? SimScenarioState.ProcessDayUtc,
            PreserveConditionals = false,
            IsScenarioScripted = isScenarioScripted,
            FacilityHint = facilityHint ?? Scenario?.StudentPosition?.FacilityId,
            LayoutForAirport = ResolveAirportLayout,
            SpeechContextFor = ac => PilotSpeechContextFor(ac, ac.HasControllerAcknowledgedInitialContact),
        };
    }

    /// <summary>
    /// <c>TAXIALL</c>: taxis every aircraft at parking to the destination, each through the dispatcher's TAXIAUTO arm
    /// so the pathfinder discovers a route from where that aircraft stands. 7110.65 3-7-2 wants a specific route in a
    /// taxi clearance; the route-less TAXI is honoured only for an aircraft already at the bar (issue #393), which as a
    /// bulk instruction would move nothing. The result counts the aircraft that took the instruction and the ones that
    /// refused it; the command itself succeeds whenever a destination was given.
    /// </summary>
    public CommandResult TaxiAll(TaxiAllCommand taxiAll)
    {
        if ((taxiAll.DestinationRunway is null) && (taxiAll.DestinationParking is null) && (taxiAll.DestinationSpot is null))
        {
            return new CommandResult(false, "TAXIALL requires a destination runway, @parking, or $spot");
        }

        var taxi = new TaxiAutoCommand(taxiAll.DestinationRunway, taxiAll.DestinationParking, taxiAll.DestinationSpot, Expedite: false);
        int taxied = 0;
        int failed = 0;
        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            if (aircraft.Phases?.CurrentPhase is not AtParkingPhase)
            {
                continue;
            }

            CommandResult result = CommandDispatcher.Dispatch(
                taxi,
                aircraft,
                BuildDispatchContext(aircraft, isScenarioScripted: false, facilityHint: null)
            );
            if (result.Success)
            {
                taxied++;
                _logger.LogInformation("[TaxiAll] {Callsign}: {Message}", aircraft.Callsign, result.Message);
            }
            else
            {
                failed++;
                _logger.LogWarning("[TaxiAll] {Callsign}: failed — {Message}", aircraft.Callsign, result.Message);
            }
        }

        string message = failed > 0 ? $"TAXIALL: {taxied} aircraft taxied, {failed} failed" : $"TAXIALL: {taxied} aircraft taxied";
        return new CommandResult(true, message);
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
    ///
    /// </summary>
    /// <param name="capture">
    /// What had to be taken before the dispatch ran: the Class B the pilot was waiting on (a pattern entry or approach
    /// clearance replaces the boundary hold the grant reads) and the read-backs the aircraft already had pending, which
    /// is how the gate below arms for this dispatch's read-back and not a pilot's own.
    /// </param>
    public void ApplyPostDispatch(
        AircraftState aircraft,
        CompoundCommand compound,
        CommandResult result,
        DispatchOrigin origin,
        PreDispatchCapture capture
    )
    {
        bool soloTrainingMode = Scenario?.SoloTrainingMode ?? false;
        // The pilot-voice side (request resolution, frequency gates, read-backs, "unable") runs whenever someone answers
        // pilots — the solo student or an AI position (identical to solo mode when the AI is off). Student-only
        // bookkeeping — two-way-comms registration and evaluator scoring — never runs for an AI-controller dispatch.
        bool answering = Scenario?.PilotContacts.AnyAnswering ?? false;
        bool human = origin == DispatchOrigin.Human;
        double elapsedSeconds = Scenario?.ElapsedSeconds ?? 0;
        BravoClearanceWording? bravoGrant = null;
        // Whether the controller had acknowledged the pilot's contact before this command, so the read-back to the command
        // that acknowledges it keeps the full callsign: read before the registration below, and false for a compound
        // carrying a STBY/ROGER, whose dispatch has already set the flag.
        bool acknowledgedBefore =
            aircraft.HasControllerAcknowledgedInitialContact
            && !compound.Blocks.SelectMany(b => b.Commands).Any(c => c is AcknowledgePilotContactCommand);

        if (result.Success)
        {
            bravoGrant = Scenario is { } scenario
                ? ImplicitBravoClearance.TryGrant(aircraft, compound, capture.BravoWait, scenario, AirspaceDatabase.Default, LookupAirportPosition)
                : null;

            if (human)
            {
                RecordStudentCommand(aircraft, compound, elapsedSeconds, soloTrainingMode);
            }

            if (answering)
            {
                PilotRequestTracker.ApplyControllerResponse(aircraft, compound, elapsedSeconds);
                // The controller has just spoken to this aircraft, so the
                // awaiting-controller-response gate (if it was set after this pilot's
                // last proactive call) clears. Commands that produce a readback also
                // arm the readback gate just below; both gates are independent.
                World.AcknowledgeControllerResponse(aircraft.Callsign);
                // One controller transmission per successful dispatch, however many commands it chained, recorded
                // before the read-back below reads the load band, so the read-back's band counts the controller's
                // transmission.
                World.RecordControllerTransmission(elapsedSeconds, ControllerAirtimeSeconds(compound, aircraft));
                QueueAppliedReadback(aircraft, compound, result, acknowledgedBefore, bravoGrant);
            }
        }
        else if (answering)
        {
            QueueSoloUnableIfNeeded(aircraft, result);
        }

        // Every read-back this dispatch queued arms the frequency's read-back gate, whichever site queued it: the
        // applied read-back above — queued when the instruction is issued even if the command itself is only deferred by
        // a reaction delay — the pilot's "unable" after a refused command, a contact/handoff read-back or a pattern-size
        // reply. The gate holds other pilots until that read-back is on air, so none of them can cut in between the
        // instruction and its read-back. A read-back the pilot had already queued on its own — its own "unable" about an
        // assigned altitude, a re-asked request — is not a reply to this instruction and arms nothing.
        PilotTransmission[] queuedReadbacks =
        [
            .. aircraft.PendingPilotTransmissions.Where(t => (t.Kind == PilotTransmissionKind.Readback) && !capture.Readbacks.Contains(t)),
        ];
        if (answering && (queuedReadbacks.Length > 0))
        {
            World.ExpectPilotReadback(queuedReadbacks, elapsedSeconds);
        }
    }

    /// <summary>
    /// The student-only bookkeeping of a successful human dispatch: two-way-comms registration and, in solo training, the
    /// evaluator's record of the command.
    /// </summary>
    private void RecordStudentCommand(AircraftState aircraft, CompoundCommand compound, double elapsedSeconds, bool soloTrainingMode)
    {
        Pilot.PilotInitialContactEligibility.RegisterControllerContact(aircraft, Scenario, compound);
        if (soloTrainingMode)
        {
            SoloTrainingEvaluator.RecordControllerCommand(aircraft, compound, elapsedSeconds, World.GetSnapshot());
        }
    }

    /// <summary>
    /// Queues the pilot's read-back of a successful dispatch, worded for the frequency's load now, with the airtime of the
    /// same read-back said canonically for the meter. A granted Class B clearance joins both forms. A compound with no
    /// read-back queues nothing.
    /// </summary>
    /// <param name="acknowledgedBefore">Whether the controller had acknowledged the pilot's contact before this command.</param>
    /// <param name="bravoGrant">The implicit Class B clearance this dispatch granted, if any.</param>
    private void QueueAppliedReadback(
        AircraftState aircraft,
        CompoundCommand compound,
        CommandResult result,
        bool acknowledgedBefore,
        BravoClearanceWording? bravoGrant
    )
    {
        PilotSpeechContext speechContext = PilotSpeechContextFor(aircraft, acknowledgedBefore);
        ActiveRunways activeRunways = Scenario?.ActiveRunways ?? ActiveRunways.Empty;
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(compound, result, aircraft, speechContext, activeRunways);
        if (readback is null)
        {
            return;
        }

        // The same read-back without load variation, whose length the frequency meter records.
        PilotSpeechText canonical =
            PilotResponder.BuildReadbackAsApplied(compound, result, aircraft, PilotSpeechContext.Canonical, activeRunways)
            ?? throw new InvalidOperationException(
                $"{aircraft.Callsign}: the read-back of '{compound.SourceText}' verbalized with a varied context but not canonically"
            );
        if (bravoGrant is { } wording)
        {
            readback = PilotResponder.WithBravoClearance(aircraft, readback, wording, speechContext);
            canonical = PilotResponder.WithBravoClearance(aircraft, canonical, wording, PilotSpeechContext.Canonical);
        }

        PilotResponder.QueueSoloPilotTransmission(aircraft, readback, canonical, PilotTransmissionKind.Readback, initialContact: false);
    }

    /// <summary>
    /// The spoken-wording context of a pilot's read-back now: <see cref="PilotSpeechContext.Canonical"/> unless a pilot is
    /// answering, else the varied personality at the band the pilot's trait shifts the frequency's activity to (the raw
    /// band stamped on the transmission for the speech rate never reads the trait), the scenario seed, the whole second,
    /// whether the pilot abbreviates its N-number (<see cref="SpokenFormDraws.CanShortenCallsign"/> against the other
    /// aircraft on the frequency, then <see cref="SpokenFormDraws.DrawShortCallsign"/>), whether a contact read-back
    /// groups its frequency (<see cref="SpokenFormDraws.DrawGroupedContactFrequency"/>) and the aircraft's indicated airspeed
    /// now, against which a speed read-back says it is slowing, speeding up or maintaining.
    /// </summary>
    /// <param name="aircraft">The aircraft reading back.</param>
    /// <param name="acknowledgedBefore">Whether the controller had acknowledged the pilot's contact before this command.</param>
    private PilotSpeechContext PilotSpeechContextFor(AircraftState aircraft, bool acknowledgedBefore)
    {
        if ((Scenario is not { } scenario) || !scenario.PilotContacts.AnyAnswering)
        {
            return PilotSpeechContext.Canonical;
        }

        double elapsedSeconds = scenario.ElapsedSeconds;
        int second = (int)Math.Floor(elapsedSeconds);
        FrequencyLoadBand band = PilotTraits.EffectiveBand(
            World.ActiveFrequency.GetLoadBand(elapsedSeconds),
            PilotTraits.For(aircraft.Callsign, scenario.RngSeed)
        );
        bool shortenCallsign =
            SpokenFormDraws.CanShortenCallsign(
                aircraft.Callsign,
                acknowledgedBefore,
                aircraft.HasLeftStudentFrequency,
                OtherCallsignsOnFrequency(aircraft)
            ) && SpokenFormDraws.DrawShortCallsign(aircraft.Callsign, scenario.RngSeed, second);
        return new PilotSpeechContext(PilotPersonality.Varied, band, scenario.RngSeed, second)
        {
            ShortenCallsign = shortenCallsign,
            GroupContactFrequency = SpokenFormDraws.DrawGroupedContactFrequency(aircraft.Callsign, band, scenario.RngSeed, second),
            CurrentIndicatedAirspeed = aircraft.IndicatedAirspeed,
        };
    }

    /// <summary>
    /// The callsigns of the other aircraft still on the student's frequency: live in the session, not shadow traffic, and
    /// not yet sent to another controller.
    /// </summary>
    private IEnumerable<string> OtherCallsignsOnFrequency(AircraftState aircraft) =>
        World
            .GetSnapshot()
            .Where(other =>
                !ReferenceEquals(other, aircraft)
                && !other.IsShadow
                && (other.CompletionReason == CompletionReason.Active)
                && !other.HasLeftStudentFrequency
            )
            .Select(other => other.Callsign);

    /// <summary>
    /// How long the controller spoke to issue <paramref name="compound"/>: the airtime of its canonical spoken read-back,
    /// the instruction as issued in the pilot's words, or the one-second minimum when it verbalizes to nothing.
    /// </summary>
    private double ControllerAirtimeSeconds(CompoundCommand compound, AircraftState aircraft) =>
        FrequencyState.EstimateAirtimeSeconds(
            PilotResponder.BuildReadback(compound, aircraft, Scenario?.ActiveRunways ?? ActiveRunways.Empty)?.Tts ?? string.Empty
        );

    private static void QueueSoloUnableIfNeeded(AircraftState aircraft, CommandResult result)
    {
        if (result.IsOperatorGuidance)
        {
            return;
        }

        if (result.RejectedCommandType is not { } rejectedType)
        {
            return;
        }

        CommandDefinition? definition = CommandRegistry.Get(rejectedType);
        if (definition?.ProducesPilotUnable != true)
        {
            return;
        }

        PilotSpeechText transmission = result.PilotUnable ?? PilotResponder.BuildUnable(aircraft, result.Message);
        PilotResponder.QueueSoloPilotTransmission(aircraft, transmission, PilotTransmissionKind.Readback, initialContact: false);
    }

    /// <summary>
    /// How <see cref="AmendFlightPlan"/>'s refusal of an invalid callsign begins; yaat-server matches it to answer an ERAM
    /// amendment with FLID FORMAT, so the wording and the match cannot drift apart.
    /// </summary>
    public const string InvalidCallsignRefusalPrefix = "Invalid callsign '";

    /// <summary>
    /// Applies a flight-plan amendment and reports the outcome: an aircraft whose callsign fails
    /// <see cref="Callsign.IsValid"/>, or one that is not in the world, is left alone and the result says so.
    /// </summary>
    public CommandResult AmendFlightPlan(string callsign, FlightPlanAmendment amendment)
    {
        if (!Callsign.IsValid(callsign))
        {
            _logger.LogWarning("AmendFlightPlan rejected invalid callsign '{Callsign}'", callsign);
            return new CommandResult(false, $"{InvalidCallsignRefusalPrefix}{callsign}'");
        }

        AircraftState? ac = FindAircraft(callsign);
        if (ac is null)
        {
            return new CommandResult(false, $"Aircraft '{callsign}' not found");
        }

        bool wasFiled = ac.FlightPlan.HasFlightPlan;

        ApplyAircraftAndAirportFields(ac, amendment);
        ApplyClearanceFields(ac, amendment);
        ApplyStarsAndBeaconFields(ac, amendment);

        // Resolve ground layout if departure/destination changed
        if (amendment.Departure is not null || amendment.Destination is not null)
        {
            ac.Ground.Layout = ResolveGroundLayout(ac);
        }

        // Editing a flight plan via the Flight Plan Editor on a radar-only (no-plan) target
        // files the plan: establish it and issue a discrete beacon code (VFR draws from the
        // VFR bank, IFR from the IFR bank). Don't flip Transponder.Code — the pilot keeps
        // squawking their current code until the controller issues SQ. This is the single
        // owner of "filing establishes the plan + assigns a beacon" and of the /A default for
        // a new plan filed with a type but no equipment suffix; the typed DA/VP/NEW create
        // path reaches it through its own AmendFlightPlan call. On a plan already filed, a
        // null suffix leaves the filed one alone. An amendment that deletes the beacon code
        // (ERAM AM BCN deletion) leaves the plan with no code, so nothing is drawn for it.
        if (!wasFiled)
        {
            ac.FlightPlan.HasFlightPlan = true;
            // A new plan starts proposed: a DM entered while the flight had none belongs to no plan.
            ac.FlightPlan.DepartureMessage = null;
            if (!string.IsNullOrEmpty(ac.FlightPlan.AircraftType) && string.IsNullOrEmpty(ac.FlightPlan.EquipmentSuffix))
            {
                ac.FlightPlan.EquipmentSuffix = "A";
            }
            if ((ac.Transponder.AssignedCode == 0) && !amendment.ClearBeaconCode)
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

        MarkTdlsItemsChanged(callsign);
        return new CommandResult(true, $"Amended {callsign}");
    }

    /// <summary>
    /// Stores an amended filed type as the bare type. Filed FP type only — never the actual physical type: Tower Cab
    /// (out-the-window) keeps reading <see cref="AircraftState.AircraftType"/>, which is fixed at spawn. A type that
    /// arrives with element a or an equipment suffix (the desktop editor's box text, a CRC echo, a STARS implied type)
    /// is split by <see cref="FlightPlanNormalization.SplitTypeAndSuffix"/>: the parsed suffix and count land in their
    /// own fields, indicator <c>H</c> sets the special aircraft indicator, and a part the string does not give leaves
    /// the stored value alone. Null means the type was not edited.
    /// </summary>
    private static void ApplyAmendedAircraftType(AircraftFlightPlan plan, string? amendedType)
    {
        if (amendedType is null)
        {
            return;
        }

        FiledAircraftType filed = FlightPlanNormalization.SplitTypeAndSuffix(amendedType);
        plan.AircraftType = filed.Type;
        plan.ApplyFiledAircraftData(filed);
    }

    /// <summary>
    /// Copies the aircraft and airport identity fields the amendment carries; a null field means "not edited",
    /// not "cleared". First of the three field groups, which run in the order the fields are filed.
    /// </summary>
    private static void ApplyAircraftAndAirportFields(AircraftState ac, FlightPlanAmendment amendment)
    {
        // Applied before the explicit suffix / count / indicator fields below, so an explicit field wins over the part
        // parsed out of the type string.
        ApplyAmendedAircraftType(ac.FlightPlan, amendment.AircraftType);
        if (amendment.EquipmentSuffix is not null)
        {
            ac.FlightPlan.EquipmentSuffix = amendment.EquipmentSuffix;
        }
        if (amendment.IcaoEquipmentCodes is not null)
        {
            ac.FlightPlan.IcaoEquipmentCodes = amendment.IcaoEquipmentCodes;
        }
        if (amendment.SpecialAircraftIndicator is not null)
        {
            ac.FlightPlan.HasSpecialAircraftIndicator = amendment.SpecialAircraftIndicator.Value;
        }
        if (amendment.NumberOfAircraft is not null)
        {
            ac.FlightPlan.NumberOfAircraft = amendment.NumberOfAircraft.Value == 0 ? null : amendment.NumberOfAircraft.Value;
        }
        if (amendment.Departure is not null)
        {
            ac.FlightPlan.Departure = amendment.Departure;
        }
        if (amendment.Destination is not null)
        {
            ac.FlightPlan.Destination = amendment.Destination;
        }
    }

    /// <summary>
    /// Copies the cleared profile — speed, altitude, flight rules, route, remarks — and runs the two side effects
    /// that reach past the plan: a route change refreshes the stored departure clearance and the pending
    /// initial-climb phases, and remarks re-derive the CRC voice type. Route is applied after flight rules because
    /// the departure-route resolution reads the amended rules.
    /// </summary>
    private static void ApplyClearanceFields(AircraftState ac, FlightPlanAmendment amendment)
    {
        ApplySpeedFields(ac.FlightPlan, amendment);
        if (amendment.Altitude is not null)
        {
            ac.FlightPlan.Altitude = amendment.Altitude;
        }
        if (amendment.RequestedAltitude is not null)
        {
            ac.FlightPlan.RequestedAltitude = amendment.RequestedAltitude;
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
        ApplyRemarksFields(ac, amendment);
    }

    /// <summary>
    /// The filed speed: a true airspeed (whose 0 keeps a filed Mach or classified speed), a Mach number or a classified
    /// speed, each clearing the others (<see cref="AircraftFlightPlan.SetTrueAirspeed"/>).
    /// </summary>
    private static void ApplySpeedFields(AircraftFlightPlan plan, FlightPlanAmendment amendment)
    {
        if (amendment.CruiseSpeed is { } knots)
        {
            plan.SetTrueAirspeed(knots);
        }
        if (amendment.CruiseMach is { } mach)
        {
            plan.SetMach(mach);
        }
        if (amendment.ClassifiedSpeed)
        {
            plan.SetClassifiedSpeed();
        }
    }

    /// <summary>
    /// The remarks: the whole composed string (<see cref="AircraftFlightPlan.ReplaceRemarks"/>), then either part on its own.
    /// Remarks are canonical for voice type: a /v//r//t/ marker (or its absence = full voice) in the interfacility remarks,
    /// where every voice writer puts it, drives the CRC voice-type field; a marker typed into the intrafacility remarks is
    /// free text. A VATSIM operational convention, not an FAA flight-plan field.
    /// </summary>
    private static void ApplyRemarksFields(AircraftState ac, FlightPlanAmendment amendment)
    {
        if (amendment.Remarks is not null)
        {
            ac.FlightPlan.ReplaceRemarks(amendment.Remarks);
        }
        if (amendment.InterfacilityRemarks is not null)
        {
            ac.FlightPlan.InterfacilityRemarks = amendment.InterfacilityRemarks;
        }
        if (amendment.IntrafacilityRemarks is not null)
        {
            ac.FlightPlan.IntrafacilityRemarks = amendment.IntrafacilityRemarks;
        }
        if ((amendment.Remarks ?? amendment.InterfacilityRemarks ?? amendment.IntrafacilityRemarks) is not null)
        {
            ac.Voice.Type = FlightPlanVoice.ParseVoiceType(ac.FlightPlan.InterfacilityRemarks);
        }
    }

    /// <summary>
    /// Copies the STARS scratchpads and the assigned beacon code — the display-side fields, which are amended
    /// after the filed plan itself.
    /// </summary>
    private static void ApplyStarsAndBeaconFields(AircraftState ac, FlightPlanAmendment amendment)
    {
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
            // squawk the new one (matching the auto-assign-on-filing branch in AmendFlightPlan). The
            // resulting beacon mismatch is shown on the data block until the pilot complies.
            ac.Transponder.AssignCode(amendment.BeaconCode.Value, amendment.BeaconAssignedByFacilityId, amendment.BeaconAssignedBySectorId);
        }
        if (amendment.ClearBeaconCode)
        {
            // Deleting the assigned code (ERAM AM BCN deletion) clears who assigned it too; the transmitted code is the
            // pilot's, as above.
            ac.Transponder.AssignCode(0, null, null);
        }
    }

    /// <summary>
    /// Marks the call sign's TDLS items changed, the counterpart of the departure strip's reprint. Every writer of a
    /// filed flight-plan field owes this: <see cref="AmendFlightPlan"/> here, and the <c>APT</c> and <c>CRUISE</c>
    /// arms, which write the plan without passing through it.
    /// </summary>
    internal void MarkTdlsItemsChanged(string callsign)
    {
        lock (Tdls.Gate)
        {
            TdlsMutations.MarkAircraftChanged(Tdls, callsign);
        }
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
        AircraftState? ac = FindAircraft(callsign);
        if (ac is null)
        {
            return 0;
        }

        BeaconCodePool.Release(ac.Transponder.AssignedCode);
        uint newCode = BeaconCodePool.AssignNextCode(ac.FlightPlan.IsVfr);
        ac.Transponder.AssignCode(newCode, assignedByFacilityId, assignedBySectorId);
        return newCode;
    }
}
