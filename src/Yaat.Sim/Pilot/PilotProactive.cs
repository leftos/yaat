using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Proactive pilot transmissions that fire on simulator state alone (no controller command
/// to react to). Houses airborne-spawn check-ins, arrival approach requests, controlled-airspace
/// boundary holds, and pending-request follow-up reminders.
///
/// Each entry point is idempotent: it consults the aircraft's <see cref="AircraftState.HasMadeInitialContact"/>
/// (or feature-specific flag) so it fires once per logical event.
/// </summary>
public static class PilotProactive
{
    private static readonly ILogger Log = SimLog.CreateLogger("PilotProactive");

    /// <summary>
    /// The destinations already warned about for a missing field elevation (<see cref="LogMissingElevation"/>). Logging
    /// state only: no simulation decision reads it.
    /// </summary>
    private static readonly HashSet<string> WarnedMissingElevation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-aircraft, per-tick airborne-spawn check-in. Fires once when an aircraft is first
    /// observed airborne in solo-training mode and has not yet spoken to ATC. No-op when any
    /// gate fails: solo mode off, on the ground, already made initial contact, student
    /// position is GND or unknown, primary airport unknown, or the airport lookup returns null.
    /// On success, queues the check-in line into <see cref="AircraftState.PendingPilotTransmissions"/>
    /// and sets <see cref="AircraftState.HasMadeInitialContact"/> so subsequent ticks no-op, and puts the pilot on the
    /// student's frequency (<see cref="AircraftState.TunedPosition"/> null). A pilot tuned to a position that is neither the
    /// student's, an AI-staffed one, nor its own track owner, with no handoff to the student pending, holds the call instead:
    /// the instructor is warned once (<see cref="AircraftState.AirborneCheckInHeldWarned"/>) and the check-in stays owed.
    /// </summary>
    public static void TickAirborneCheckIn(AircraftState aircraft, SimScenarioState scenario, Func<string, LatLon?> airportLookup)
    {
        if (!IsAirborneCheckInDue(aircraft, scenario))
        {
            return;
        }

        bool held = IsHeldOffTheStudentsFrequency(aircraft, scenario);
        if (held && aircraft.AirborneCheckInHeldWarned)
        {
            return;
        }

        if (PrimaryAirportPosition(scenario, airportLookup) is not { } airportPos)
        {
            return;
        }

        PilotSpeechText? line = PilotResponder.BuildAirborneCheckIn(aircraft, scenario, airportPos);
        if (line is null)
        {
            return;
        }

        if (held && (aircraft.TunedPosition is { } tuned))
        {
            Log.LogDebug("{Callsign}: airborne check-in held, pilot tuned to {Tuned}", aircraft.Callsign, tuned);
            aircraft.PendingWarnings.Add(HeldCheckInWarning(tuned, line));
            aircraft.AirborneCheckInHeldWarned = true;
            return;
        }

        // The check-in is the pilot's initial call to the student: it is now on the student's frequency.
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, initialContact: true);
        aircraft.HasMadeInitialContact = true;
        aircraft.TunedPosition = null;
    }

    /// <summary>
    /// The airborne check-in's gates before the line is built: solo mode, airborne, no initial contact yet, outside the tower's
    /// arrival side, a non-GND student position, and the facility SOP allowing the call (<see cref="PilotInitialContactEligibility"/>).
    /// </summary>
    private static bool IsAirborneCheckInDue(AircraftState aircraft, SimScenarioState scenario)
    {
        if (!scenario.SoloTrainingMode)
        {
            return false;
        }

        if (aircraft.HasMadeInitialContact)
        {
            return false;
        }

        if (aircraft.IsOnGround)
        {
            return false;
        }

        // Inside the tower's arrival-side jurisdiction (final, landing, pattern, go-around) the pilot is the tower's:
        // an aircraft on a three-mile final never makes an initial call to approach, even when a tower staffed by the
        // AI answered its on-final call and the human student is the radar position (AIM 5-4-3.a).
        if (Phases.TowerCabPhases.IsArrivalSide(aircraft.Phases?.CurrentPhase))
        {
            return false;
        }

        string? positionType = scenario.StudentPositionType;
        if (string.IsNullOrEmpty(positionType) || (positionType == "GND"))
        {
            return false;
        }

        return PilotInitialContactEligibility.CanInitiateWithStudent(aircraft, scenario);
    }

    /// <summary>
    /// The instructor's warning for a held check-in: where the pilot is tuned (the position's callsign, or "another frequency"
    /// for <see cref="TrackOwner.Elsewhere"/>), then the check-in line it would have said, so it does not read as a call made.
    /// </summary>
    private static string HeldCheckInWarning(TrackOwner tuned, PilotSpeechText line)
    {
        string where = tuned.IsSamePositionByCallsign(TrackOwner.Elsewhere) ? "another frequency" : tuned.Callsign;
        return $"check-in held, tuned to {where}: {line.Terminal}";
    }

    /// <summary>The scenario's primary airport position, or null when the scenario names none or the lookup does not know it.</summary>
    private static LatLon? PrimaryAirportPosition(SimScenarioState scenario, Func<string, LatLon?> airportLookup)
    {
        string? primaryAirport = scenario.PrimaryAirportId;
        return string.IsNullOrEmpty(primaryAirport) ? null : airportLookup(primaryAirport);
    }

    /// <summary>
    /// Whether the airborne check-in is held because the pilot is on another frequency: tuned away from the student, not to an
    /// AI-staffed position, not to its own track owner (whose transfer to the student the SOP gate already allowed), and with no
    /// handoff to the student pending (a pending handoff makes the call, which retunes the pilot).
    /// </summary>
    private static bool IsHeldOffTheStudentsFrequency(AircraftState aircraft, SimScenarioState scenario)
    {
        if (!aircraft.IsTunedAwayFrom(scenario.StudentPosition) || PilotResponder.IsTunedToAiStaffedPosition(aircraft, scenario.PilotContacts))
        {
            return false;
        }

        if ((aircraft.Track.Owner is { } owner) && (aircraft.TunedPosition?.IsSamePositionByCallsign(owner) == true))
        {
            return false;
        }

        return !((scenario.StudentPosition is { } student) && (aircraft.Track.HandoffPeer?.MatchesPosition(student) == true));
    }

    public static void TickArrivalApproachRequest(AircraftState aircraft, SimScenarioState scenario, Func<string, LatLon?> airportLookup)
    {
        if (!scenario.SoloTrainingMode)
        {
            return;
        }

        if (aircraft.IsOnGround)
        {
            return;
        }

        if (aircraft.FlightPlan.IsVfr)
        {
            return;
        }

        if (!aircraft.HasMadeInitialContact)
        {
            return;
        }

        // A capped request (a request for lower that used up its re-asks) does not hold the approach request back, which
        // replaces it; a capped approach request still holds back asking it again.
        if ((aircraft.PendingPilotRequest is { IsOpen: true } open) && (open.BlocksOtherCalls || (open.Kind == PilotPendingRequestKind.Approach)))
        {
            return;
        }

        if (aircraft.PendingPilotTransmissions.Count > 0)
        {
            return;
        }

        if (aircraft.Phases?.ActiveApproach is not null)
        {
            return;
        }

        if (aircraft.Phases?.LandingClearance is not null)
        {
            return;
        }

        string? destination = !string.IsNullOrWhiteSpace(aircraft.FlightPlan.Destination)
            ? aircraft.FlightPlan.Destination
            : scenario.PrimaryAirportId;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        LatLon? destinationPosition = airportLookup(destination);
        if (destinationPosition is null)
        {
            return;
        }

        double distanceNm = GeoMath.DistanceNm(destinationPosition.Value, aircraft.Position);
        if (distanceNm > 10.0)
        {
            return;
        }

        string? positionType = scenario.StudentPositionType;
        if (string.IsNullOrWhiteSpace(positionType))
        {
            return;
        }

        if (positionType is "GND" or "TWR")
        {
            return;
        }

        string facilityCallName = PilotResponder.ResolveStudentFacilityCallName(
            scenario,
            positionType,
            positionType == "CTR" ? "center" : "approach"
        );
        // The pilot does not speak a runway (ATC assigns it), but the pending-request context still
        // tracks the planned landing runway for follow-up matching.
        string? runwayId = aircraft.Procedure.DestinationRunway ?? aircraft.Phases?.AssignedRunway?.Designator;
        PilotSpeechText line = PilotResponder.BuildArrivalApproachRequest(aircraft);
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, initialContact: false);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Approach,
            scenario.ElapsedSeconds,
            line,
            PilotRequestContext.Runway(runwayId, facilityCallName)
        );
    }

    /// <summary>The descent profile's gradient: 300 ft per NM, the top of the range controllers plan on (7110.65 4-8-1.h.2(a) NOTE).</summary>
    private const double ProfileFtPerNm = 300.0;

    /// <summary>How far above the profile a pilot no controller has given a descent asks for lower.</summary>
    private const double FirstRequestAboveProfileFt = 1000.0;

    /// <summary>
    /// How far above the profile a pilot asks for lower after a descent clearance (the 2,000 ft band), and when it asks
    /// again after an unanswered request asked in the 1,000 ft band used up its re-asks.
    /// </summary>
    private const double StepDownRequestAboveProfileFt = 2000.0;

    /// <summary>The least time after a descent clearance (<see cref="AircraftState.DescentClearedAtSeconds"/>) before the pilot asks again.</summary>
    private const double StepDownWaitSeconds = 180.0;

    /// <summary>
    /// The least time after the last repeat of a capped request for lower before it re-opens, once in the 2,000 ft band.
    /// </summary>
    private const double CappedRequestReopenWaitSeconds = 120.0;

    /// <summary>
    /// The straight-line distance from the destination field within which the pilot never asks for lower: the approach
    /// request takes over. A judgement call: FAA publications are silent on it; 10 NM matches the approach-request radius
    /// (<see cref="TickArrivalApproachRequest"/>).
    /// </summary>
    private const double NoDescentRequestWithinNm = 10.0;

    /// <summary>
    /// The height above the destination field elevation at or below which the pilot never asks for lower. A judgement
    /// call: FAA publications are silent on it; it pairs with <see cref="NoDescentRequestWithinNm"/>.
    /// </summary>
    private const double NoDescentRequestBelowFieldPlusFt = 3000.0;

    /// <summary>The altitude below which the 250 KIAS limit applies (14 CFR 91.117(a)).</summary>
    private const double SpeedLimitAltitudeFt = 10000.0;

    /// <summary>The speed limit below <see cref="SpeedLimitAltitudeFt"/>.</summary>
    private const double SpeedLimitKts = 250.0;

    /// <summary>The track a descent adds to slow to <see cref="SpeedLimitKts"/> when it crosses <see cref="SpeedLimitAltitudeFt"/> faster.</summary>
    private const double SpeedLimitSlowdownNm = 10.0;

    /// <summary>
    /// An IFR arrival with no descent clearance in force asks the radar student "request lower" once it is
    /// <see cref="FirstRequestAboveProfileFt"/> above its descent profile (<see cref="DescentProfileFt"/>), at once when
    /// it is already past that (a late handoff). After any descent clearance
    /// (<see cref="AircraftState.DescentClearedAtSeconds"/>) it asks again only <see cref="StepDownWaitSeconds"/> after
    /// it and <see cref="StepDownRequestAboveProfileFt"/> above the profile. The pilot never leaves its altitude on its
    /// own: this only records the request, which <see cref="PilotRequestTracker"/> re-asks and any descent clearance
    /// answers. An unanswered request asked in the 1,000 ft band that used up its re-asks stays open and silent until the
    /// aircraft is <see cref="StepDownRequestAboveProfileFt"/> above the profile, when it is asked once afresh
    /// (<see cref="PilotPendingRequest.ReopenedAfterCap"/>); one asked from 2,000 ft or more above the profile is never
    /// re-opened. The aircraft remembers the cap (<see cref="AircraftState.DescentRequestCap"/>), so a request that replaces
    /// a capped one does not reset it; only a descent clearance does. Never
    /// within <see cref="NoDescentRequestWithinNm"/> of the field or at or below
    /// <see cref="NoDescentRequestBelowFieldPlusFt"/> above it (<see cref="IsInsideDescentRequestFloor"/>). Solo only,
    /// like <see cref="TickArrivalApproachRequest"/>.
    /// </summary>
    public static void TickDescentRequest(AircraftState aircraft, SimScenarioState scenario, Func<string, LatLon?> airportLookup)
    {
        // The aircraft's memory of a capped request for lower, kept when another request replaces it: the one re-open, in
        // the 2,000 ft band, or nothing once that is spent. An open uncapped request holds the pilot back (IsFreeToCall).
        DescentRequestCap cap = aircraft.DescentRequestCap;
        bool reopen = (cap == DescentRequestCap.Reopenable);
        if (
            (cap == DescentRequestCap.Spent)
            || (reopen && !IsCappedRequestReopenWaitOver(aircraft, scenario.ElapsedSeconds))
            || !IsAskForLowerEligible(aircraft, scenario, airportLookup)
            || (ResolveDescentDestination(aircraft, airportLookup) is not { } destination)
        )
        {
            return;
        }

        double aboveFt = aircraft.Altitude - DescentProfileFt(aircraft, destination.Position, destination.ElevationFt);
        double thresholdFt = RequestThresholdAboveProfileFt(aircraft, scenario.ElapsedSeconds, reopen);
        if (aboveFt < thresholdFt)
        {
            return;
        }

        string facilityCallName = StudentRadarFacilityCallName(scenario);
        PilotSpeechText line = PilotResponder.BuildDescentRequest(aircraft, facilityCallName);
        Log.LogDebug(
            "{Callsign}: request lower, {Above:F0} ft above the profile (threshold {Threshold:F0} ft), reopened {Reopen}",
            aircraft.Callsign,
            aboveFt,
            thresholdFt,
            reopen
        );
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, initialContact: false);
        PilotPendingRequest request = PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.Descent,
            scenario.ElapsedSeconds,
            line,
            PilotRequestContext.Facility(facilityCallName)
        );
        request.ReopenedAfterCap = reopen;
        // The band is the height above the profile the pilot asks from, not the threshold it crossed.
        request.AskedInStepDownBand = (aboveFt >= StepDownRequestAboveProfileFt);
        if (reopen)
        {
            aircraft.DescentRequestCap = DescentRequestCap.Spent;
        }
    }

    /// <summary>
    /// Whether <see cref="CappedRequestReopenWaitSeconds"/> have passed since the last repeat of the aircraft's capped
    /// request for lower (<see cref="AircraftState.DescentRequestCappedAtSeconds"/>).
    /// </summary>
    private static bool IsCappedRequestReopenWaitOver(AircraftState aircraft, double nowSeconds) =>
        (aircraft.DescentRequestCappedAtSeconds is { } cappedAt) && ((nowSeconds - cappedAt) >= CappedRequestReopenWaitSeconds);

    /// <summary>
    /// Whether the pilot may ask for lower now: the gates of <see cref="IsDescentRequestEligible"/> (a capped request does
    /// not hold it back) and no reason not to ask (<see cref="ShouldNotAskForLower"/>).
    /// </summary>
    private static bool IsAskForLowerEligible(AircraftState aircraft, SimScenarioState scenario, Func<string, LatLon?> airportLookup) =>
        IsDescentRequestEligible(aircraft, scenario) && !ShouldNotAskForLower(aircraft, scenario.StudentPosition, airportLookup);

    /// <summary>
    /// How far above the profile the pilot must be to ask now: <see cref="FirstRequestAboveProfileFt"/>, or
    /// <see cref="StepDownRequestAboveProfileFt"/> after a descent clearance or for a re-opened request
    /// (<paramref name="reopen"/>), and never (infinity) inside the wait after a descent clearance.
    /// </summary>
    private static double RequestThresholdAboveProfileFt(AircraftState aircraft, double nowSeconds, bool reopen)
    {
        if (aircraft.DescentClearedAtSeconds is { } clearedAt)
        {
            return ((nowSeconds - clearedAt) < StepDownWaitSeconds) ? double.PositiveInfinity : StepDownRequestAboveProfileFt;
        }

        return reopen ? StepDownRequestAboveProfileFt : FirstRequestAboveProfileFt;
    }

    /// <summary>
    /// Whether the aircraft is inside the floor below which the pilot never asks for lower: within
    /// <see cref="NoDescentRequestWithinNm"/> straight-line of its filed destination, or at or below the field elevation
    /// plus <see cref="NoDescentRequestBelowFieldPlusFt"/>. The field's position comes from
    /// <paramref name="airportLookup"/> (the engine's, as for the profile) and its elevation from the navdata. False with
    /// no destination or no position for it; the altitude half needs the field elevation.
    /// </summary>
    internal static bool IsInsideDescentRequestFloor(AircraftState aircraft, Func<string, LatLon?> airportLookup)
    {
        string destination = aircraft.FlightPlan.Destination;
        if (string.IsNullOrWhiteSpace(destination) || (airportLookup(destination) is not { } field))
        {
            return false;
        }

        if (GeoMath.DistanceNm(field, aircraft.Position) <= NoDescentRequestWithinNm)
        {
            return true;
        }

        // A guard: reached only when a STAR altitude ahead pulls the profile below field + 3,000 ft beyond 10 NM; no
        // procedure in today's navdata does.
        return (NavigationDatabase.Instance.GetAirportElevation(destination) is { } elevationFt)
            && (aircraft.Altitude <= (elevationFt + NoDescentRequestBelowFieldPlusFt));
    }

    /// <summary>
    /// An IFR aircraft with no clearance beyond its destination asks the radar student for further clearance about
    /// <see cref="FlightPhysics.FurtherClearanceRequestMinutes"/> before the airport, and on reaching it reports holding
    /// there and records the re-ask <see cref="PilotRequestTracker"/> repeats until a lateral clearance moves it on.
    /// Solo only, like <see cref="TickDescentRequest"/>. A pilot tuned away from the student's frequency says neither the
    /// request nor the holding report, and asks as soon as it is retuned.
    /// </summary>
    public static void TickFurtherClearanceRequest(AircraftState aircraft, SimScenarioState scenario)
    {
        if (aircraft.Phases?.CurrentPhase is HoldingPatternPhase { IsClearanceLimitHold: true } hold)
        {
            ReportHoldingAtClearanceLimit(aircraft, scenario, hold);
            return;
        }

        if (
            !FlightPhysics.IsLastTargetTheClearanceLimit(aircraft)
            || !IsDescentRequestEligible(aircraft, scenario)
            || aircraft.IsTunedAwayFrom(scenario.StudentPosition)
        )
        {
            return;
        }

        NavigationTarget direct = aircraft.Targets.NavigationRoute[0];
        if (MinutesToCallAt(aircraft, direct) is not { } minutes)
        {
            return;
        }

        string facilityCallName = StudentRadarFacilityCallName(scenario);
        PilotSpeechText line = PilotResponder.BuildFurtherClearanceRequest(aircraft, facilityCallName, direct.Name, minutes);
        Log.LogDebug("{Callsign}: request further clearance, {Airport} {Minutes} min ahead", aircraft.Callsign, direct.Name, minutes);
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, initialContact: false);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.FurtherClearance,
            scenario.ElapsedSeconds,
            line,
            PilotRequestContext.Facility(facilityCallName)
        );
    }

    /// <summary>
    /// The whole minutes the pilot names when it asks for further clearance now: null beyond
    /// <see cref="FlightPhysics.FurtherClearanceRequestMinutes"/> of the airport, and under a minute out, where the holding
    /// report on arrival carries the request.
    /// </summary>
    private static int? MinutesToCallAt(AircraftState aircraft, NavigationTarget direct)
    {
        if (aircraft.GroundSpeed < 1)
        {
            return null;
        }

        double minutes = FlightPhysics.MinutesTo(aircraft, direct.Position);
        if ((minutes > FlightPhysics.FurtherClearanceRequestMinutes) || (minutes < 1.0))
        {
            return null;
        }

        return (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Once per clearance-limit hold: the holding report, and the request for further clearance recorded for re-asking.
    /// An open request does not hold the report back. A pilot tuned away from the student's frequency holds the report
    /// until it is retuned.
    /// </summary>
    private static void ReportHoldingAtClearanceLimit(AircraftState aircraft, SimScenarioState scenario, HoldingPatternPhase hold)
    {
        bool mayReport =
            !hold.ClearanceLimitReported
            && scenario.SoloTrainingMode
            && IsRadarStudent(scenario.StudentPositionType)
            && IsAirborneIfrInContact(aircraft)
            && (aircraft.PendingPilotTransmissions.Count == 0)
            && !aircraft.IsTunedAwayFrom(scenario.StudentPosition);
        if (!mayReport)
        {
            return;
        }

        string facilityCallName = StudentRadarFacilityCallName(scenario);
        PilotSpeechText report = PilotResponder.BuildHoldingAtClearanceLimitReport(aircraft, facilityCallName, hold.FixName);
        PilotSpeechText reask = PilotResponder.BuildHoldingFurtherClearanceRequest(aircraft, facilityCallName, hold.FixName);
        Log.LogDebug("{Callsign}: reports holding over {Airport}", aircraft.Callsign, hold.FixName);
        PilotResponder.QueueSoloPilotTransmission(aircraft, report, PilotTransmissionKind.Proactive, initialContact: false);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.FurtherClearance,
            scenario.ElapsedSeconds,
            reask,
            PilotRequestContext.Facility(facilityCallName)
        );
        hold.ClearanceLimitReported = true;
    }

    /// <summary>How the pilot addresses the radar student: its radio name, else "center" or "approach" by position type.</summary>
    private static string StudentRadarFacilityCallName(SimScenarioState scenario)
    {
        string positionType = scenario.StudentPositionType!;
        return PilotResponder.ResolveStudentFacilityCallName(scenario, positionType, positionType == "CTR" ? "center" : "approach");
    }

    /// <summary>
    /// Whether the aircraft still waits on a clearance beyond its destination: flying to its clearance limit (the direct,
    /// or a route end at the destination) or holding there. <see cref="PilotRequestTracker"/> reads it to close an open
    /// request for further clearance as moot.
    /// </summary>
    internal static bool IsAwaitingFurtherClearance(AircraftState aircraft) =>
        FlightPhysics.IsLastTargetTheClearanceLimit(aircraft)
        || (aircraft.Phases?.CurrentPhase is HoldingPatternPhase { IsClearanceLimitHold: true });

    /// <summary>
    /// Whether the pilot may make a new call now: solo training with a radar student, an airborne IFR aircraft whose
    /// check-in the controller has acknowledged, nothing queued to say and no request open that holds its calls back
    /// (<see cref="IsFreeToCall"/>).
    /// </summary>
    private static bool IsDescentRequestEligible(AircraftState aircraft, SimScenarioState scenario) =>
        IsInContactWithRadarStudent(aircraft, scenario) && IsFreeToCall(aircraft);

    /// <summary>Solo training with a radar student, and an airborne IFR aircraft whose check-in the controller has acknowledged.</summary>
    private static bool IsInContactWithRadarStudent(AircraftState aircraft, SimScenarioState scenario) =>
        scenario.SoloTrainingMode && IsRadarStudent(scenario.StudentPositionType) && IsAirborneIfrInContact(aircraft);

    /// <summary>Whether the student works a radar position: approach, departure or center, not ground or tower.</summary>
    private static bool IsRadarStudent(string? positionType) => !string.IsNullOrWhiteSpace(positionType) && (positionType is not ("GND" or "TWR"));

    /// <summary>Whether the aircraft is an airborne IFR flight whose check-in the controller has acknowledged.</summary>
    private static bool IsAirborneIfrInContact(AircraftState aircraft) =>
        !aircraft.IsOnGround && !aircraft.FlightPlan.IsVfr && aircraft.HasMadeInitialContact && aircraft.HasControllerAcknowledgedInitialContact;

    /// <summary>
    /// Whether the pilot has nothing queued to say and no request open that holds its calls back: a capped request
    /// (<see cref="PilotPendingRequest.BlocksOtherCalls"/>) does not.
    /// </summary>
    private static bool IsFreeToCall(AircraftState aircraft) =>
        (aircraft.PendingPilotRequest is not { BlocksOtherCalls: true }) && (aircraft.PendingPilotTransmissions.Count == 0);

    /// <summary>
    /// The filed destination's position and field elevation, or null with no destination filed, no position for it, or
    /// (logged as a warning) no elevation for it.
    /// </summary>
    private static (LatLon Position, double ElevationFt)? ResolveDescentDestination(AircraftState aircraft, Func<string, LatLon?> airportLookup)
    {
        string destination = aircraft.FlightPlan.Destination;
        if (string.IsNullOrWhiteSpace(destination) || (airportLookup(destination) is not { } position))
        {
            return null;
        }

        if (NavigationDatabase.Instance.GetAirportElevation(destination) is not { } elevationFt)
        {
            LogMissingElevation(aircraft.Callsign, destination);
            return null;
        }

        return (position, elevationFt);
    }

    /// <summary>
    /// Logs a destination with no field elevation as a warning the first time it is seen, at Debug after. The set is
    /// logging state only: it touches no simulation state, so it cannot change a replay.
    /// </summary>
    private static void LogMissingElevation(string callsign, string destination)
    {
        bool first;
        lock (WarnedMissingElevation)
        {
            first = WarnedMissingElevation.Add(destination);
        }

        if (first)
        {
            Log.LogWarning("{Callsign}: no field elevation for destination {Destination}, no request for lower", callsign, destination);
        }
        else
        {
            Log.LogDebug("{Callsign}: no field elevation for destination {Destination}, no request for lower", callsign, destination);
        }
    }

    /// <summary>
    /// Whether the pilot has no reason to ask for lower, or to repeat an open request for it: a descent is authorised or
    /// queued (<see cref="DescentAuthorized"/>), a climb is in progress or cleared (<see cref="IsClimbingOrDeparting"/>),
    /// the aircraft is off the student's work (<see cref="IsOffTheStudentsWork"/>), or it is inside the floor near the
    /// field (<see cref="IsInsideDescentRequestFloor"/>), where the approach request takes over.
    /// <see cref="PilotRequestTracker"/> reads it to close an open request as moot. <paramref name="studentPosition"/> is
    /// the student's position, against which the pilot's tuned position is read; <paramref name="airportLookup"/> places
    /// the destination for the floor.
    /// </summary>
    internal static bool ShouldNotAskForLower(AircraftState aircraft, TrackOwner? studentPosition, Func<string, LatLon?> airportLookup) =>
        DescentAuthorized(aircraft)
        || IsClimbingOrDeparting(aircraft)
        || IsOffTheStudentsWork(aircraft, studentPosition)
        || IsInsideDescentRequestFloor(aircraft, airportLookup);

    /// <summary>An assigned altitude above it, a climb via, or an initial-climb or departure-procedure phase.</summary>
    private static bool IsClimbingOrDeparting(AircraftState aircraft) =>
        ((aircraft.Targets.AssignedAltitude is { } assigned) && (assigned > aircraft.Altitude + FlightPhysics.AltitudeSnapFt))
        || (aircraft.Procedure.ClimbViaTargetFt is not null)
        || (aircraft.Phases?.CurrentPhase is InitialClimbPhase or DepartureProcedurePhase);

    /// <summary>Off the student's frequency, holding, or cleared to land.</summary>
    private static bool IsOffTheStudentsWork(AircraftState aircraft, TrackOwner? studentPosition) =>
        aircraft.IsTunedAwayFrom(studentPosition)
        || (aircraft.Phases?.CurrentPhase is HoldingPatternPhase)
        || (aircraft.Phases?.LandingClearance is not null);

    /// <summary>
    /// Whether a descent is already authorised or waiting to be applied, so the pilot has no reason to ask: an assigned
    /// altitude below it, a descend via, a controller's crossing on the route, an active or deferred approach clearance,
    /// or a vertical command still queued behind a condition or a pilot-reaction delay.
    /// </summary>
    private static bool DescentAuthorized(AircraftState aircraft) =>
        ((aircraft.Targets.AssignedAltitude is { } assigned) && (assigned < aircraft.Altitude - FlightPhysics.AltitudeSnapFt))
        || aircraft.Procedure.StarViaMode
        || aircraft.Targets.NavigationRoute.Exists(t => t.AltitudeRestriction is { Source: CifpAltitudeRestrictionSource.Atc })
        || (aircraft.Phases?.ActiveApproach is not null)
        || (aircraft.Approach.PendingClearance is not null)
        || HasQueuedVerticalCommand(aircraft);

    /// <summary>
    /// Whether a vertical command waits to be applied: in a queued block not yet applied (an <c>AT</c> condition), or in a
    /// deferred dispatch (a pilot-reaction delay or a <c>WAIT</c>).
    /// </summary>
    private static bool HasQueuedVerticalCommand(AircraftState aircraft)
    {
        foreach (CommandBlock block in aircraft.Queue.Blocks)
        {
            if (!block.IsApplied && !block.TriggerMissed && (block.ParsedCommands is { } commands) && commands.Exists(IsVertical))
            {
                return true;
            }
        }

        foreach (DeferredDispatch deferred in aircraft.DeferredDispatches)
        {
            if (deferred.Payload.Blocks.Any(b => b.Commands.Any(IsVertical)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVertical(ParsedCommand command) =>
        (CommandDescriber.GetQueuedCommandDimension(command) & CommandDimension.Vertical) != CommandDimension.None;

    /// <summary>
    /// The highest altitude a constant descent to the field allows here: the field elevation plus
    /// <see cref="ProfileFtPerNm"/> of the along-route distance to the destination (the route, then direct to the field).
    /// On a STAR, the first route fix whose published altitude (<see cref="CifpAltitudeRestriction.Altitude1Ft"/>, the
    /// upper bound of a window) is below the aircraft gives a second line, that altitude plus <see cref="ProfileFtPerNm"/>
    /// back from the fix, and the lower of the two lines applies. Planning to an altitude never authorises flying it.
    /// </summary>
    private static double DescentProfileFt(AircraftState aircraft, LatLon destinationPosition, double fieldElevationFt)
    {
        double ceilingFt = aircraft.Altitude - FlightPhysics.AltitudeSnapFt;
        double alongNm = 0.0;
        LatLon previous = aircraft.Position;
        bool onStar = !string.IsNullOrEmpty(aircraft.Procedure.ActiveStarId);
        double? starLineFt = null;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            alongNm += GeoMath.DistanceNm(previous, fix.Position);
            previous = fix.Position;
            if ((starLineFt is null) && onStar && (fix.AltitudeRestriction is { } restriction) && (restriction.Altitude1Ft < ceilingFt))
            {
                starLineFt = ProfileLineFt(aircraft, restriction.Altitude1Ft, alongNm);
            }
        }

        alongNm += GeoMath.DistanceNm(previous, destinationPosition);
        double fieldLineFt = ProfileLineFt(aircraft, fieldElevationFt, alongNm);
        return starLineFt is { } star ? Math.Min(fieldLineFt, star) : fieldLineFt;
    }

    /// <summary>
    /// The profile <paramref name="distanceNm"/> back from where it reaches <paramref name="targetAltitudeFt"/>: that
    /// altitude plus <see cref="ProfileFtPerNm"/> per NM, with <see cref="SpeedLimitSlowdownNm"/> less to descend in when
    /// the descent crosses <see cref="SpeedLimitAltitudeFt"/> faster than <see cref="SpeedLimitKts"/> (the track it takes to
    /// slow).
    /// </summary>
    private static double ProfileLineFt(AircraftState aircraft, double targetAltitudeFt, double distanceNm)
    {
        bool slows =
            (aircraft.Altitude > SpeedLimitAltitudeFt) && (targetAltitudeFt < SpeedLimitAltitudeFt) && (aircraft.IndicatedAirspeed > SpeedLimitKts);
        double descentNm = distanceNm - (slows ? SpeedLimitSlowdownNm : 0.0);
        return targetAltitudeFt + (ProfileFtPerNm * Math.Max(0.0, descentNm));
    }

    private const double ReportAtFixArrivalNm = 0.5;

    /// <summary>
    /// Per-tick poll for the one-shot deferred reports armed by <c>REPORT &lt;n&gt; FINAL</c> and
    /// <c>REPORT &lt;fix&gt;</c>. Pattern-leg reports are voiced from the pattern phases, not here.
    /// Each fires once — clearing the armed field — when the aircraft reaches the armed distance to
    /// the assigned-runway threshold (n-mile final, gated to inbound aircraft so a same-runway
    /// departure never reports final) or the resolved fix. Unlike the other proactive ticks this
    /// runs in both solo and RPO mode; <see cref="PilotResponder.RouteByTunedPosition"/> routes
    /// to whoever the pilot is tuned to.
    /// </summary>
    public static void TickReportTriggers(AircraftState aircraft, SimScenarioState scenario)
    {
        if (aircraft.IsOnGround)
        {
            return;
        }

        AircraftApproachState approach = aircraft.Approach;

        if (
            approach.ReportFinalMileTarget is { } miles
            && aircraft.Phases?.AssignedRunway is { } runway
            && ApproachCommandHandler.IsInboundToLand(aircraft)
        )
        {
            double distNm = GeoMath.DistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
            if (distNm <= miles)
            {
                approach.ReportFinalMileTarget = null;
                string runwayId = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
                PilotResponder.RouteByTunedPosition(
                    aircraft,
                    PilotReportRouting.From(scenario),
                    PilotResponder.BuildMileFinalReport(aircraft, miles, runwayId)
                );
            }
        }

        if (approach.ReportAtFixName is { } fixName && approach.ReportAtFixLat is { } fixLat && approach.ReportAtFixLon is { } fixLon)
        {
            double distNm = GeoMath.DistanceNm(aircraft.Position, new LatLon(fixLat, fixLon));
            if (distNm <= ReportAtFixArrivalNm)
            {
                approach.ReportAtFixName = null;
                approach.ReportAtFixLat = null;
                approach.ReportAtFixLon = null;
                PilotResponder.RouteByTunedPosition(aircraft, PilotReportRouting.From(scenario), PilotResponder.BuildAtFixReport(aircraft, fixName));
            }
        }
    }

    /// <summary>
    /// Re-voices or closes the aircraft's open request (<see cref="PilotRequestTracker.TryQueueFollowUp"/>);
    /// <paramref name="airportLookup"/> (the engine's) places the destination for the request-for-lower floor.
    /// </summary>
    public static void TickPendingRequests(AircraftState aircraft, SimScenarioState scenario, Func<string, LatLon?> airportLookup)
    {
        // Follow-ups re-voice an unanswered request; they belong wherever someone answers pilots (the solo student
        // or an AI position), not only in solo training.
        if (!scenario.PilotContacts.AnyAnswering)
        {
            return;
        }

        PilotRequestTracker.TryQueueFollowUp(aircraft, scenario.ElapsedSeconds, scenario.StudentPosition, airportLookup);
    }

    /// <summary>
    /// Watches airborne VFR aircraft in solo training and inserts a self-clearing boundary
    /// hold when the projected track would enter Class B/C before the required gate is met.
    /// AIM §3-2-1.4 places responsibility on the pilot to meet Class B/C/D entry
    /// requirements before entry; this models that responsibility for the student.
    /// </summary>
    public static void TickAirspaceBoundaryRespect(
        AircraftState aircraft,
        SimScenarioState scenario,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup
    )
    {
        if (!scenario.SoloTrainingMode)
        {
            return;
        }

        if (aircraft.IsOnGround || !aircraft.FlightPlan.IsVfr)
        {
            return;
        }

        if (aircraft.Phases is { IsComplete: false })
        {
            return;
        }

        // The request looks further ahead than the hold, so any Bravo entry the hold is about to act on was already
        // seen here: the request goes out first, or with the hold on the same tick.
        TryRequestBravoClearance(aircraft, scenario, airspace, airportLookup);

        AirspaceBoundaryCrossing? crossing = airspace.FindFirstProjectedEntry(aircraft, lookaheadSeconds: 60);
        if (crossing is null || EntryGateSatisfied(aircraft, crossing.Volume.Class))
        {
            return;
        }

        AirspaceVolume volume = crossing.Volume;
        LatLon reference = ResolveReference(crossing, airportLookup);

        // Already laterally inside the footprint means the entry can only be vertical, and no turn avoids
        // a shelf that is directly overhead — level off beneath it and stay on course instead.
        int? levelOffCeiling = volume.ContainsLateral(aircraft.Position)
            ? ResolveLevelOffCeiling(aircraft, volume, scenario.MagneticModelDateUtc)
            : null;
        AirspaceHoldMode mode = levelOffCeiling is null ? AirspaceHoldMode.Orbit : AirspaceHoldMode.LevelOff;

        AnnounceUnableAssignedAltitude(aircraft, volume, levelOffCeiling);

        var phase = new AirspaceBoundaryHoldPhase
        {
            AirspaceClass = volume.Class,
            Ident = string.IsNullOrWhiteSpace(volume.Ident) ? volume.IcaoId : volume.Ident,
            NameText = volume.Name,
            ReferencePosition = reference,
            OrbitDirection = AirspaceAvoidance.AwayFrom(aircraft.TrueTrack, aircraft.Position, crossing.Intersection),
            VolumeLowerFtMsl = volume.LowerFtMsl,
            VolumeUpperFtMsl = volume.UpperFtMsl,
            Mode = mode,
            VolumeId = volume.Id,
            LevelOffCeilingFtMsl = levelOffCeiling,
        };

        var phases = new PhaseList();
        phases.Add(phase);
        aircraft.Phases = phases;
    }

    /// <summary>
    /// The pilot is free to call the student for a Class B clearance: not cleared yet, in contact with the student and
    /// not sent to another frequency, nothing queued to say, and no request open that holds its calls back.
    /// </summary>
    private static bool CanRequestBravoClearance(AircraftState aircraft, TrackOwner? studentPosition) =>
        !aircraft.IsClearedIntoBravo
        && aircraft.HasMadeInitialContact
        && !aircraft.IsTunedAwayFrom(studentPosition)
        && (aircraft.PendingPilotTransmissions.Count == 0)
        && (aircraft.PendingPilotRequest is not { BlocksOtherCalls: true });

    private static LatLon ResolveReference(AirspaceBoundaryCrossing crossing, Func<string, LatLon?> airportLookup) =>
        airportLookup(crossing.Volume.Ident) ?? airportLookup(crossing.Volume.IcaoId) ?? crossing.Intersection;

    /// <summary>
    /// The pilot calls for a Class B clearance ahead of the boundary (7110.65 §7-9-2.a; AIM 3-2-3) when the track
    /// enters Class B through its side within <see cref="ImplicitBravoClearance.EntryLookaheadSeconds"/>, and records it
    /// as an open airspace-entry request, which the tracker repeats until the controller answers. Like the arrival
    /// approach request, it waits until the pilot is in contact with the student, the frequency queue is clear, and no
    /// other request is open. A pilot the student has sent to another frequency (<c>CT</c>/<c>FCA</c>) no longer calls
    /// the student. A climb into a shelf from beneath it draws no call: the pilot levels off under the shelf by choice
    /// and says nothing (issue #154).
    /// </summary>
    private static void TryRequestBravoClearance(
        AircraftState aircraft,
        SimScenarioState scenario,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup
    )
    {
        if (!CanRequestBravoClearance(aircraft, scenario.StudentPosition))
        {
            return;
        }

        AirspaceBoundaryCrossing? crossing = airspace.FindFirstProjectedEntry(
            aircraft,
            ImplicitBravoClearance.EntryLookaheadSeconds,
            AirspaceClass.Bravo
        );
        if ((crossing is null) || crossing.Volume.ContainsLateral(aircraft.Position))
        {
            return;
        }

        AirspaceVolume volume = crossing.Volume;
        string ident = string.IsNullOrWhiteSpace(volume.Ident) ? volume.IcaoId : volume.Ident;
        PilotSpeechText line = PilotResponder.BuildBravoClearanceRequest(
            aircraft,
            ImplicitBravoClearance.WordingFor(aircraft, volume.Ident, airspace, airportLookup)
        );
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Proactive, initialContact: false);
        PilotRequestTracker.RecordRequest(
            aircraft,
            PilotPendingRequestKind.AirspaceEntry,
            scenario.ElapsedSeconds,
            line,
            PilotRequestContext.Airspace(AirspaceClass.Bravo, ident, ResolveReference(crossing, airportLookup))
        );
    }

    /// <summary>
    /// A VFR pilot choosing their own altitude says nothing — real pilots don't narrate self-avoidance
    /// (issue #154). But an altitude the controller assigned is a clearance the pilot cannot legally fly,
    /// and AIM 5-5-6.a.3 requires them to say so and offer what they can do instead. Fires once per
    /// assignment: the hold installed straight after this suppresses the boundary tick until the
    /// controller changes something.
    /// </summary>
    private static void AnnounceUnableAssignedAltitude(AircraftState aircraft, AirspaceVolume volume, int? levelOffCeiling)
    {
        if (levelOffCeiling is not { } ceiling || aircraft.Targets.AssignedAltitude is not { } assigned || assigned < volume.LowerFtMsl)
        {
            return;
        }

        string airspaceName = volume.Class == AirspaceClass.Bravo ? "bravo" : "charlie";
        PilotSpeechText line = PilotResponder.BuildUnableAirspaceAltitude(aircraft, (int)assigned, ceiling, airspaceName);
        PilotResponder.QueueSoloPilotTransmission(aircraft, line, PilotTransmissionKind.Readback, initialContact: false);
    }

    /// <summary>
    /// The altitude the pilot levels at beneath <paramref name="volume"/>, or null when there is no flyable
    /// airspace under it (a surface area) and the aircraft must turn away instead. The surface reference is
    /// the volume's primary airport; without it the AGL check falls back to MSL, which is conservative
    /// everywhere the fixture covers.
    /// </summary>
    private static int? ResolveLevelOffCeiling(AircraftState aircraft, AirspaceVolume volume, DateTime magneticModelDateUtc)
    {
        NavigationDatabase? navDb = Data.NavigationDatabase.Instance;
        double surfaceElevation = navDb?.GetAirportElevation(volume.Ident) ?? navDb?.GetAirportElevation(volume.IcaoId) ?? 0;
        double magneticCourse = aircraft.TrueTrack.ToMagnetic(MagneticDeclination.GetDeclination(aircraft.Position, magneticModelDateUtc)).Degrees;
        return AirspaceAvoidance.LevelOffCeilingFt(volume.LowerFtMsl, magneticCourse, surfaceElevation);
    }

    private static bool EntryGateSatisfied(AircraftState aircraft, AirspaceClass airspaceClass) =>
        airspaceClass switch
        {
            AirspaceClass.Bravo => aircraft.IsClearedIntoBravo,
            AirspaceClass.Charlie => aircraft.HasMadeInitialContact && aircraft.HasControllerAcknowledgedInitialContact,
            _ => true,
        };
}
